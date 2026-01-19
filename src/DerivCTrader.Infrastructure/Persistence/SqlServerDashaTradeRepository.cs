using System.Data;
using Dapper;
using DerivCTrader.Application.Interfaces;
using DerivCTrader.Domain.Entities;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

namespace DerivCTrader.Infrastructure.Persistence;

/// <summary>
/// SQL Server implementation of IDashaTradeRepository.
/// Handles all database operations for Dasha Trade selective martingale system.
/// </summary>
public class SqlServerDashaTradeRepository : IDashaTradeRepository
{
    private readonly string _connectionString;
    private readonly ILogger<SqlServerDashaTradeRepository> _logger;

    public SqlServerDashaTradeRepository(
        IConfiguration configuration,
        ILogger<SqlServerDashaTradeRepository> logger)
    {
        _connectionString = configuration.GetConnectionString("ConnectionString")
            ?? throw new InvalidOperationException("Connection string 'ConnectionString' not found");
        _logger = logger;
    }

    private IDbConnection CreateConnection() => new SqlConnection(_connectionString);

    // =============================================
    // Provider Configuration
    // =============================================

    public async Task<DashaProviderConfig?> GetProviderConfigAsync(string providerChannelId)
    {
        const string sql = @"
            SELECT ConfigId, ProviderChannelId, ProviderName, InitialStake, LadderSteps,
                   ResetAfterStep, DefaultExpiryMinutes, IsActive, ExecuteOnProviderLoss,
                   CreatedAt, UpdatedAt
            FROM DashaProviderConfig
            WHERE ProviderChannelId = @ProviderChannelId AND IsActive = 1";

        using var connection = CreateConnection();
        return await connection.QueryFirstOrDefaultAsync<DashaProviderConfig>(sql, new { ProviderChannelId = providerChannelId });
    }

    public async Task<List<DashaProviderConfig>> GetActiveProviderConfigsAsync()
    {
        const string sql = @"
            SELECT ConfigId, ProviderChannelId, ProviderName, InitialStake, LadderSteps,
                   ResetAfterStep, DefaultExpiryMinutes, IsActive, ExecuteOnProviderLoss,
                   CreatedAt, UpdatedAt
            FROM DashaProviderConfig
            WHERE IsActive = 1";

        using var connection = CreateConnection();
        var configs = await connection.QueryAsync<DashaProviderConfig>(sql);
        return configs.ToList();
    }

    // =============================================
    // Pending Signals
    // =============================================

    public async Task<int> SavePendingSignalAsync(DashaPendingSignal signal)
    {
        const string sql = @"
            INSERT INTO DashaPendingSignals
                (ProviderChannelId, ProviderName, Asset, Direction, Timeframe, ExpiryMinutes,
                 EntryPrice, SignalReceivedAt, ExpiryAt, Status, TelegramMessageId, RawMessage, CreatedAt, UpdatedAt)
            VALUES
                (@ProviderChannelId, @ProviderName, @Asset, @Direction, @Timeframe, @ExpiryMinutes,
                 @EntryPrice, @SignalReceivedAt, @ExpiryAt, @Status, @TelegramMessageId, @RawMessage, @CreatedAt, @UpdatedAt);
            SELECT CAST(SCOPE_IDENTITY() as int);";

        using var connection = CreateConnection();
        var signalId = await connection.ExecuteScalarAsync<int>(sql, signal);

        _logger.LogInformation(
            "Saved Dasha pending signal: Id={SignalId}, Asset={Asset}, Direction={Direction}, ExpiryAt={ExpiryAt}",
            signalId, signal.Asset, signal.Direction, signal.ExpiryAt);

        return signalId;
    }

    public async Task<List<DashaPendingSignal>> GetSignalsAwaitingEvaluationAsync()
    {
        const string sql = @"
            SELECT PendingSignalId, ProviderChannelId, ProviderName, Asset, Direction, Timeframe, ExpiryMinutes,
                   EntryPrice, ExitPrice, SignalReceivedAt, ExpiryAt, EvaluatedAt, Status, ProviderResult,
                   TelegramMessageId, RawMessage, CreatedAt, UpdatedAt
            FROM DashaPendingSignals
            WHERE Status = @Status AND ExpiryAt <= @Now AND EntryPrice > 0
            ORDER BY ExpiryAt ASC";

        using var connection = CreateConnection();
        var signals = await connection.QueryAsync<DashaPendingSignal>(sql, new
        {
            Status = DashaPendingSignalStatus.AwaitingExpiry,
            Now = DateTime.UtcNow
        });
        return signals.ToList();
    }

    public async Task<List<DashaPendingSignal>> GetSignalsNeedingEntryPriceAsync()
    {
        const string sql = @"
            SELECT PendingSignalId, ProviderChannelId, ProviderName, Asset, Direction, Timeframe, ExpiryMinutes,
                   EntryPrice, ExitPrice, SignalReceivedAt, ExpiryAt, EvaluatedAt, Status, ProviderResult,
                   TelegramMessageId, RawMessage, CreatedAt, UpdatedAt
            FROM DashaPendingSignals
            WHERE Status = @Status AND EntryPrice = 0
            ORDER BY SignalReceivedAt ASC";

        using var connection = CreateConnection();
        var signals = await connection.QueryAsync<DashaPendingSignal>(sql, new
        {
            Status = DashaPendingSignalStatus.AwaitingExpiry
        });
        return signals.ToList();
    }

    public async Task<DashaPendingSignal?> GetPendingSignalByIdAsync(int pendingSignalId)
    {
        const string sql = @"
            SELECT PendingSignalId, ProviderChannelId, ProviderName, Asset, Direction, Timeframe, ExpiryMinutes,
                   EntryPrice, ExitPrice, SignalReceivedAt, ExpiryAt, EvaluatedAt, Status, ProviderResult,
                   TelegramMessageId, RawMessage, CreatedAt, UpdatedAt
            FROM DashaPendingSignals
            WHERE PendingSignalId = @PendingSignalId";

        using var connection = CreateConnection();
        return await connection.QueryFirstOrDefaultAsync<DashaPendingSignal>(sql, new { PendingSignalId = pendingSignalId });
    }

    public async Task UpdatePendingSignalAsync(DashaPendingSignal signal)
    {
        const string sql = @"
            UPDATE DashaPendingSignals
            SET EntryPrice = @EntryPrice,
                ExitPrice = @ExitPrice,
                EvaluatedAt = @EvaluatedAt,
                Status = @Status,
                ProviderResult = @ProviderResult,
                UpdatedAt = @UpdatedAt
            WHERE PendingSignalId = @PendingSignalId";

        signal.UpdatedAt = DateTime.UtcNow;

        using var connection = CreateConnection();
        await connection.ExecuteAsync(sql, signal);

        _logger.LogInformation(
            "Updated Dasha pending signal: Id={SignalId}, Status={Status}, EntryPrice={EntryPrice}",
            signal.PendingSignalId, signal.Status, signal.EntryPrice);
    }

    public async Task<bool> SignalExistsAsync(string asset, string direction, DateTime signalTime, string providerChannelId)
    {
        // Deduplicate signals within a 2-minute window
        const string sql = @"
            SELECT COUNT(1)
            FROM DashaPendingSignals
            WHERE Asset = @Asset
              AND Direction = @Direction
              AND ProviderChannelId = @ProviderChannelId
              AND ABS(DATEDIFF(SECOND, SignalReceivedAt, @SignalTime)) < 120";

        using var connection = CreateConnection();
        var count = await connection.ExecuteScalarAsync<int>(sql, new
        {
            Asset = asset,
            Direction = direction,
            SignalTime = signalTime,
            ProviderChannelId = providerChannelId
        });
        return count > 0;
    }

    // =============================================
    // Compounding State
    // =============================================

    public async Task<DashaCompoundingState?> GetCompoundingStateAsync(string providerChannelId)
    {
        const string sql = @"
            SELECT StateId, ProviderChannelId, CurrentStep, CurrentStake, ConsecutiveWins,
                   TotalWins, TotalLosses, TotalProfit, LastTradeAt, UpdatedAt
            FROM DashaCompoundingState
            WHERE ProviderChannelId = @ProviderChannelId";

        using var connection = CreateConnection();
        return await connection.QueryFirstOrDefaultAsync<DashaCompoundingState>(sql, new { ProviderChannelId = providerChannelId });
    }

    public async Task<int> CreateCompoundingStateAsync(DashaCompoundingState state)
    {
        const string sql = @"
            INSERT INTO DashaCompoundingState
                (ProviderChannelId, CurrentStep, CurrentStake, ConsecutiveWins, TotalWins, TotalLosses, TotalProfit, UpdatedAt)
            VALUES
                (@ProviderChannelId, @CurrentStep, @CurrentStake, @ConsecutiveWins, @TotalWins, @TotalLosses, @TotalProfit, @UpdatedAt);
            SELECT CAST(SCOPE_IDENTITY() as int);";

        using var connection = CreateConnection();
        var stateId = await connection.ExecuteScalarAsync<int>(sql, state);

        _logger.LogInformation(
            "Created Dasha compounding state: Provider={Provider}, InitialStake={Stake}",
            state.ProviderChannelId, state.CurrentStake);

        return stateId;
    }

    public async Task UpdateCompoundingStateAsync(DashaCompoundingState state)
    {
        const string sql = @"
            UPDATE DashaCompoundingState
            SET CurrentStep = @CurrentStep,
                CurrentStake = @CurrentStake,
                ConsecutiveWins = @ConsecutiveWins,
                TotalWins = @TotalWins,
                TotalLosses = @TotalLosses,
                TotalProfit = @TotalProfit,
                LastTradeAt = @LastTradeAt,
                UpdatedAt = @UpdatedAt
            WHERE ProviderChannelId = @ProviderChannelId";

        state.UpdatedAt = DateTime.UtcNow;

        using var connection = CreateConnection();
        await connection.ExecuteAsync(sql, state);

        _logger.LogInformation(
            "Updated Dasha compounding state: Provider={Provider}, Step={Step}, Stake={Stake}",
            state.ProviderChannelId, state.CurrentStep, state.CurrentStake);
    }

    // =============================================
    // Trades
    // =============================================

    public async Task<int> CreateTradeAsync(DashaTrade trade)
    {
        const string sql = @"
            INSERT INTO DashaTrades
                (PendingSignalId, ProviderChannelId, ProviderName, Asset, Direction, ExpiryMinutes,
                 DerivContractId, Stake, StakeStep, PurchasePrice, Payout,
                 ProviderEntryPrice, ProviderExitPrice, ProviderResult,
                 ExecutionResult, Profit, ProviderSignalAt, ProviderExpiryAt, ExecutedAt, SettledAt,
                 TelegramMessageId, CreatedAt)
            VALUES
                (@PendingSignalId, @ProviderChannelId, @ProviderName, @Asset, @Direction, @ExpiryMinutes,
                 @DerivContractId, @Stake, @StakeStep, @PurchasePrice, @Payout,
                 @ProviderEntryPrice, @ProviderExitPrice, @ProviderResult,
                 @ExecutionResult, @Profit, @ProviderSignalAt, @ProviderExpiryAt, @ExecutedAt, @SettledAt,
                 @TelegramMessageId, @CreatedAt);
            SELECT CAST(SCOPE_IDENTITY() as int);";

        using var connection = CreateConnection();
        var tradeId = await connection.ExecuteScalarAsync<int>(sql, trade);

        _logger.LogInformation(
            "Created Dasha trade: Id={TradeId}, Asset={Asset}, Direction={Direction}, Stake={Stake}",
            tradeId, trade.Asset, trade.Direction, trade.Stake);

        return tradeId;
    }

    public async Task UpdateTradeAsync(DashaTrade trade)
    {
        const string sql = @"
            UPDATE DashaTrades
            SET DerivContractId = @DerivContractId,
                PurchasePrice = @PurchasePrice,
                Payout = @Payout,
                ExecutionResult = @ExecutionResult,
                Profit = @Profit,
                ExecutedAt = @ExecutedAt,
                SettledAt = @SettledAt,
                TelegramMessageId = @TelegramMessageId
            WHERE TradeId = @TradeId";

        using var connection = CreateConnection();
        await connection.ExecuteAsync(sql, trade);

        _logger.LogInformation(
            "Updated Dasha trade: Id={TradeId}, Result={Result}, Profit={Profit}",
            trade.TradeId, trade.ExecutionResult, trade.Profit);
    }

    public async Task<DashaTrade?> GetTradeByIdAsync(int tradeId)
    {
        const string sql = @"
            SELECT TradeId, PendingSignalId, ProviderChannelId, ProviderName, Asset, Direction, ExpiryMinutes,
                   DerivContractId, Stake, StakeStep, PurchasePrice, Payout,
                   ProviderEntryPrice, ProviderExitPrice, ProviderResult,
                   ExecutionResult, Profit, ProviderSignalAt, ProviderExpiryAt, ExecutedAt, SettledAt,
                   TelegramMessageId, CreatedAt
            FROM DashaTrades
            WHERE TradeId = @TradeId";

        using var connection = CreateConnection();
        return await connection.QueryFirstOrDefaultAsync<DashaTrade>(sql, new { TradeId = tradeId });
    }

    public async Task<DashaTrade?> GetTradeByContractIdAsync(string derivContractId)
    {
        const string sql = @"
            SELECT TradeId, PendingSignalId, ProviderChannelId, ProviderName, Asset, Direction, ExpiryMinutes,
                   DerivContractId, Stake, StakeStep, PurchasePrice, Payout,
                   ProviderEntryPrice, ProviderExitPrice, ProviderResult,
                   ExecutionResult, Profit, ProviderSignalAt, ProviderExpiryAt, ExecutedAt, SettledAt,
                   TelegramMessageId, CreatedAt
            FROM DashaTrades
            WHERE DerivContractId = @DerivContractId";

        using var connection = CreateConnection();
        return await connection.QueryFirstOrDefaultAsync<DashaTrade>(sql, new { DerivContractId = derivContractId });
    }

    public async Task<List<DashaTrade>> GetUnsettledTradesAsync()
    {
        const string sql = @"
            SELECT TradeId, PendingSignalId, ProviderChannelId, ProviderName, Asset, Direction, ExpiryMinutes,
                   DerivContractId, Stake, StakeStep, PurchasePrice, Payout,
                   ProviderEntryPrice, ProviderExitPrice, ProviderResult,
                   ExecutionResult, Profit, ProviderSignalAt, ProviderExpiryAt, ExecutedAt, SettledAt,
                   TelegramMessageId, CreatedAt
            FROM DashaTrades
            WHERE SettledAt IS NULL AND DerivContractId IS NOT NULL
            ORDER BY ExecutedAt ASC";

        using var connection = CreateConnection();
        var trades = await connection.QueryAsync<DashaTrade>(sql);
        return trades.ToList();
    }

    public async Task<List<DashaTrade>> GetRecentTradesAsync(string providerChannelId, int count = 50)
    {
        const string sql = @"
            SELECT TOP (@Count)
                   TradeId, PendingSignalId, ProviderChannelId, ProviderName, Asset, Direction, ExpiryMinutes,
                   DerivContractId, Stake, StakeStep, PurchasePrice, Payout,
                   ProviderEntryPrice, ProviderExitPrice, ProviderResult,
                   ExecutionResult, Profit, ProviderSignalAt, ProviderExpiryAt, ExecutedAt, SettledAt,
                   TelegramMessageId, CreatedAt
            FROM DashaTrades
            WHERE ProviderChannelId = @ProviderChannelId
            ORDER BY CreatedAt DESC";

        using var connection = CreateConnection();
        var trades = await connection.QueryAsync<DashaTrade>(sql, new { ProviderChannelId = providerChannelId, Count = count });
        return trades.ToList();
    }

    // =============================================
    // Analytics
    // =============================================

    public async Task<DashaProviderStats> GetProviderStatsAsync(string providerChannelId)
    {
        const string sql = @"
            SELECT
                @ProviderChannelId AS ProviderChannelId,
                (SELECT COUNT(*) FROM DashaPendingSignals WHERE ProviderChannelId = @ProviderChannelId) AS TotalSignalsReceived,
                (SELECT COUNT(*) FROM DashaPendingSignals WHERE ProviderChannelId = @ProviderChannelId AND ProviderResult = 'Won') AS ProviderWins,
                (SELECT COUNT(*) FROM DashaPendingSignals WHERE ProviderChannelId = @ProviderChannelId AND ProviderResult = 'Lost') AS ProviderLosses,
                (SELECT COUNT(*) FROM DashaTrades WHERE ProviderChannelId = @ProviderChannelId) AS TradesExecuted,
                (SELECT COUNT(*) FROM DashaTrades WHERE ProviderChannelId = @ProviderChannelId AND ExecutionResult = 'Won') AS OurWins,
                (SELECT COUNT(*) FROM DashaTrades WHERE ProviderChannelId = @ProviderChannelId AND ExecutionResult = 'Lost') AS OurLosses,
                (SELECT ISNULL(SUM(Profit), 0) FROM DashaTrades WHERE ProviderChannelId = @ProviderChannelId) AS TotalProfit,
                (SELECT ISNULL(SUM(Stake), 0) FROM DashaTrades WHERE ProviderChannelId = @ProviderChannelId) AS TotalStaked";

        using var connection = CreateConnection();
        var stats = await connection.QueryFirstOrDefaultAsync<DashaProviderStats>(sql, new { ProviderChannelId = providerChannelId });
        return stats ?? new DashaProviderStats { ProviderChannelId = providerChannelId };
    }
}
