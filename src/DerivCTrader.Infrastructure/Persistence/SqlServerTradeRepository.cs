using System.Data;
using Dapper;
using DerivCTrader.Application.Interfaces;
using DerivCTrader.Domain.Entities;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

namespace DerivCTrader.Infrastructure.Persistence;

public class SqlServerTradeRepository : ITradeRepository
{
    private readonly string _connectionString;
    private readonly ILogger<SqlServerTradeRepository> _logger;

    public SqlServerTradeRepository(IConfiguration configuration, ILogger<SqlServerTradeRepository> logger)
    {
        _connectionString = configuration.GetConnectionString("ConnectionString") 
            ?? throw new InvalidOperationException("Connection string 'ConnectionString' not found");
        _logger = logger;
    }

    private IDbConnection CreateConnection() => new SqlConnection(_connectionString);

    // ===== SIGNAL QUEUE OPERATIONS =====

    public async Task<int> SaveToQueueAsync(SignalQueue signal)
    {
        const string sql = @"
            INSERT INTO SignalQueue (ProviderChannelId, ProviderName, Asset, Direction, EntryPrice, StopLoss, TakeProfit, 
                                     SignalType, Status, ReceivedAt, CreatedAt, Timeframe, Pattern, RawMessage)
            VALUES (@ProviderChannelId, @ProviderName, @Asset, @Direction, @EntryPrice, @StopLoss, @TakeProfit,
                    @SignalType, @Status, @ReceivedAt, @CreatedAt, @Timeframe, @Pattern, @RawMessage);
            SELECT CAST(SCOPE_IDENTITY() as int);";

        using var connection = CreateConnection();
        var signalId = await connection.ExecuteScalarAsync<int>(sql, signal);
        _logger.LogInformation("Saved signal to queue: SignalId={SignalId}, Asset={Asset}, Direction={Direction}", 
            signalId, signal.Asset, signal.Direction);
        return signalId;
    }

    public async Task<List<SignalQueue>> GetPendingSignalsAsync()
    {
        const string sql = "SELECT * FROM SignalQueue WHERE Status = 'Pending' ORDER BY ReceivedAt ASC";
        using var connection = CreateConnection();
        var signals = await connection.QueryAsync<SignalQueue>(sql);
        return signals.ToList();
    }

    public async Task UpdateSignalStatusAsync(int signalId, string status)
    {
        const string sql = @"
            UPDATE SignalQueue 
            SET Status = @Status, ProcessedAt = @ProcessedAt 
            WHERE SignalId = @SignalId";

        using var connection = CreateConnection();
        await connection.ExecuteAsync(sql, new 
        { 
            SignalId = signalId, 
            Status = status, 
            ProcessedAt = DateTime.UtcNow 
        });
        _logger.LogInformation("Updated signal status: SignalId={SignalId}, Status={Status}", signalId, status);
    }

    // ===== DERIV TRADES OPERATIONS =====

    public async Task<int> SaveDerivTradeAsync(DerivTrade trade)
    {
        const string sql = @"
            INSERT INTO DerivTrades (ContractId, Asset, Direction, Stake, ExpiryMinutes, EntryPrice, Status, OpenTime,
                                     StrategyName, Timeframe, Pattern, ProviderChannelId, ProviderName)
            VALUES (@ContractId, @Asset, @Direction, @Stake, @ExpiryMinutes, @EntryPrice, @Status, @OpenTime,
                    @StrategyName, @Timeframe, @Pattern, @ProviderChannelId, @ProviderName);
            SELECT CAST(SCOPE_IDENTITY() as int);";

        using var connection = CreateConnection();
        var tradeId = await connection.ExecuteScalarAsync<int>(sql, trade);
        _logger.LogInformation("Saved Deriv trade: TradeId={TradeId}, ContractId={ContractId}", tradeId, trade.ContractId);
        return tradeId;
    }

    public async Task<List<DerivTrade>> GetOpenDerivTradesAsync()
    {
        const string sql = "SELECT * FROM DerivTrades WHERE Status = 'Open'";
        using var connection = CreateConnection();
        var trades = await connection.QueryAsync<DerivTrade>(sql);
        return trades.ToList();
    }

    public async Task UpdateDerivTradeOutcomeAsync(int tradeId, string status, decimal profit)
    {
        const string sql = @"
            UPDATE DerivTrades 
            SET Status = @Status, Profit = @Profit, CloseTime = @CloseTime 
            WHERE TradeId = @TradeId";

        using var connection = CreateConnection();
        await connection.ExecuteAsync(sql, new 
        { 
            TradeId = tradeId, 
            Status = status, 
            Profit = profit, 
            CloseTime = DateTime.UtcNow 
        });
        _logger.LogInformation("Updated Deriv trade outcome: TradeId={TradeId}, Status={Status}, Profit={Profit}", 
            tradeId, status, profit);
    }

    public async Task<DerivTrade?> GetDerivTradeByContractIdAsync(string contractId)
    {
        const string sql = "SELECT * FROM DerivTrades WHERE ContractId = @ContractId";
        using var connection = CreateConnection();
        return await connection.QueryFirstOrDefaultAsync<DerivTrade>(sql, new { ContractId = contractId });
    }

    // ===== PROVIDER CONFIG =====

    public async Task<ProviderChannelConfig?> GetProviderConfigAsync(string providerChannelId)
    {
        const string sql = "SELECT * FROM ProviderChannelConfig WHERE ProviderChannelId = @ProviderChannelId";
        using var connection = CreateConnection();
        return await connection.QueryFirstOrDefaultAsync<ProviderChannelConfig>(sql, new { ProviderChannelId = providerChannelId });
    }

    public async Task<List<ProviderChannelConfig>> GetAllProviderConfigsAsync()
    {
        const string sql = "SELECT * FROM ProviderChannelConfig";
        using var connection = CreateConnection();
        var configs = await connection.QueryAsync<ProviderChannelConfig>(sql);
        return configs.ToList();
    }

    public async Task<List<ProviderChannelConfig>> GetAllActiveProvidersAsync()
    {
        const string sql = "SELECT * FROM ProviderChannelConfig WHERE IsActive = 1";
        using var connection = CreateConnection();
        var configs = await connection.QueryAsync<ProviderChannelConfig>(sql);
        return configs.ToList();
    }

    // ===== EXISTING METHODS (keep unchanged) =====

    public async Task<int> CreateForexTradeAsync(ForexTrade trade)
    {
        const string sql = @"
            INSERT INTO ForexTrades (Symbol, Direction, EntryPrice, ExitPrice, EntryTime, ExitTime, 
                                    PnL, PnLPercent, Status, Notes, CreatedAt, IndicatorsLinked)
            VALUES (@Symbol, @Direction, @EntryPrice, @ExitPrice, @EntryTime, @ExitTime, 
                   @PnL, @PnLPercent, @Status, @Notes, @CreatedAt, @IndicatorsLinked);
            SELECT CAST(SCOPE_IDENTITY() as int);";

        using var connection = CreateConnection();
        var tradeId = await connection.ExecuteScalarAsync<int>(sql, trade);
        _logger.LogInformation("Created Forex trade {TradeId} for {Symbol}", tradeId, trade.Symbol);
        return tradeId;
    }

    public async Task UpdateForexTradeAsync(ForexTrade trade)
    {
        const string sql = @"
            UPDATE ForexTrades 
            SET Symbol = @Symbol, Direction = @Direction, EntryPrice = @EntryPrice, 
                ExitPrice = @ExitPrice, EntryTime = @EntryTime, ExitTime = @ExitTime,
                PnL = @PnL, PnLPercent = @PnLPercent, Status = @Status, 
                Notes = @Notes, IndicatorsLinked = @IndicatorsLinked
            WHERE TradeId = @TradeId";

        using var connection = CreateConnection();
        await connection.ExecuteAsync(sql, trade);
        _logger.LogInformation("Updated Forex trade {TradeId}", trade.TradeId);
    }

    public async Task<ForexTrade?> GetForexTradeByIdAsync(int tradeId)
    {
        const string sql = "SELECT * FROM ForexTrades WHERE TradeId = @TradeId";
        using var connection = CreateConnection();
        return await connection.QueryFirstOrDefaultAsync<ForexTrade>(sql, new { TradeId = tradeId });
    }

    public async Task<int> CreateBinaryTradeAsync(BinaryOptionTrade trade)
    {
        const string sql = @"
            INSERT INTO BinaryOptionTrades (AssetName, Direction, OpenTime, CloseTime, ExpiryLength, 
                                           Result, ClosedBeforeExpiry, SentToTelegramPublic, 
                                           SentToTelegramPrivate, SentToWhatsApp, CreatedAt, 
                                           ExpiryDisplay, TradeStake, ExpectedExpiryTimestamp, 
                                           EntryPrice, ExitPrice, StrategyName, StrategyVersion,
                                           SignalGeneratedAt, IndicatorsLinked)
            VALUES (@AssetName, @Direction, @OpenTime, @CloseTime, @ExpiryLength, @Result, 
                   @ClosedBeforeExpiry, @SentToTelegramPublic, @SentToTelegramPrivate, 
                   @SentToWhatsApp, @CreatedAt, @ExpiryDisplay, @TradeStake, 
                   @ExpectedExpiryTimestamp, @EntryPrice, @ExitPrice, @StrategyName, 
                   @StrategyVersion, @SignalGeneratedAt, @IndicatorsLinked);
            SELECT CAST(SCOPE_IDENTITY() as int);";

        using var connection = CreateConnection();
        var tradeId = await connection.ExecuteScalarAsync<int>(sql, trade);
        _logger.LogInformation("Created Binary trade {TradeId} for {Asset}", tradeId, trade.AssetName);
        return tradeId;
    }

    public async Task UpdateBinaryTradeAsync(BinaryOptionTrade trade)
    {
        const string sql = @"
            UPDATE BinaryOptionTrades 
            SET AssetName = @AssetName, Direction = @Direction, OpenTime = @OpenTime, 
                CloseTime = @CloseTime, ExpiryLength = @ExpiryLength, Result = @Result,
                ClosedBeforeExpiry = @ClosedBeforeExpiry, EntryPrice = @EntryPrice,
                ExitPrice = @ExitPrice, StrategyName = @StrategyName
            WHERE TradeId = @TradeId";

        using var connection = CreateConnection();
        await connection.ExecuteAsync(sql, trade);
        _logger.LogInformation("Updated Binary trade {TradeId}", trade.TradeId);
    }

    public async Task<BinaryOptionTrade?> GetBinaryTradeByIdAsync(int tradeId)
    {
        const string sql = "SELECT * FROM BinaryOptionTrades WHERE TradeId = @TradeId";
        using var connection = CreateConnection();
        return await connection.QueryFirstOrDefaultAsync<BinaryOptionTrade>(sql, new { TradeId = tradeId });
    }

    public async Task CreateTradeIndicatorAsync(TradeIndicator indicator)
    {
        const string sql = @"
            INSERT INTO TradeIndicators (TradeId, TradeType, StrategyName, StrategyVersion, 
                                        Timeframe, IndicatorsJSON, RecordedAt, 
                                        UsedForTraining, Notes)
            VALUES (@TradeId, @TradeType, @StrategyName, @StrategyVersion, @Timeframe, 
                   @IndicatorsJSON, @RecordedAt, @UsedForTraining, @Notes)";

        using var connection = CreateConnection();
        await connection.ExecuteAsync(sql, indicator);
        _logger.LogInformation("Created TradeIndicator for TradeId {TradeId}", indicator.TradeId);
    }

    public async Task<int> EnqueueTradeAsync(TradeExecutionQueue queueItem)
    {
        const string sql = @"
            INSERT INTO TradeExecutionQueue (CTraderOrderId, Asset, Direction, StrategyName, ProviderChannelId, IsOpposite, DerivContractId, CreatedAt)
            VALUES (@CTraderOrderId, @Asset, @Direction, @StrategyName, @ProviderChannelId, @IsOpposite, @DerivContractId, @CreatedAt);
            SELECT CAST(SCOPE_IDENTITY() as int);";

        using var connection = CreateConnection();
        var queueId = await connection.ExecuteScalarAsync<int>(sql, queueItem);
        _logger.LogInformation("Enqueued trade {Asset} {Direction} to queue", queueItem.Asset, queueItem.Direction);
        return queueId;
    }

    public async Task<TradeExecutionQueue?> DequeueMatchingTradeAsync(string asset, string direction)
    {
        const string sql = @"
            SELECT TOP 1 * 
            FROM TradeExecutionQueue 
            WHERE Asset = @Asset AND Direction = @Direction
            ORDER BY CreatedAt ASC";

        using var connection = CreateConnection();
        var match = await connection.QueryFirstOrDefaultAsync<TradeExecutionQueue>(sql, new { Asset = asset, Direction = direction });
        
        if (match != null)
        {
            _logger.LogInformation("Dequeued matching trade QueueId {QueueId} for {Asset} {Direction}", 
                match.QueueId, asset, direction);
        }
        
        return match;
    }

    public async Task DeleteQueueItemAsync(int queueId)
    {
        const string sql = "DELETE FROM TradeExecutionQueue WHERE QueueId = @QueueId";
        using var connection = CreateConnection();
        await connection.ExecuteAsync(sql, new { QueueId = queueId });
        _logger.LogInformation("Deleted queue item {QueueId}", queueId);
    }

    // ===== PARSED SIGNALS OPERATIONS =====

    public async Task<int> SaveParsedSignalAsync(ParsedSignal signal)
    {
        const string sql = @"
            INSERT INTO ParsedSignalsQueue (Asset, Direction, EntryPrice, StopLoss, TakeProfit, 
                                             ProviderChannelId, ProviderName, SignalType, ReceivedAt, 
                                             Processed, Timeframe, Pattern, RawMessage)
            VALUES (@Asset, @Direction, @EntryPrice, @StopLoss, @TakeProfit,
                    @ProviderChannelId, @ProviderName, @SignalType, @ReceivedAt,
                    @Processed, @Timeframe, @Pattern, @RawMessage);
            SELECT CAST(SCOPE_IDENTITY() as int);";

        using var connection = CreateConnection();
        var signalId = await connection.ExecuteScalarAsync<int>(sql, new
        {
            signal.Asset,
            Direction = signal.Direction.ToString(),
            signal.EntryPrice,
            signal.StopLoss,
            signal.TakeProfit,
            signal.ProviderChannelId,
            signal.ProviderName,
            SignalType = signal.SignalType.ToString(),
            signal.ReceivedAt,
            Processed = false,
            signal.Timeframe,
            signal.Pattern,
            signal.RawMessage
        });
        
        _logger.LogInformation("Saved parsed signal: SignalId={SignalId}, Asset={Asset}", signalId, signal.Asset);
        return signalId;
    }

    public async Task<List<ParsedSignal>> GetUnprocessedSignalsAsync()
    {
        const string sql = @"
            SELECT * FROM ParsedSignalsQueue 
            WHERE Processed = 0 
            ORDER BY ReceivedAt ASC";
        
        using var connection = CreateConnection();
        var signals = await connection.QueryAsync<ParsedSignal>(sql);
        return signals.ToList();
    }

    public async Task MarkSignalAsProcessedAsync(int signalId)
    {
        const string sql = @"
            UPDATE ParsedSignalsQueue 
            SET Processed = 1, ProcessedAt = @ProcessedAt 
            WHERE SignalId = @SignalId";

        using var connection = CreateConnection();
        await connection.ExecuteAsync(sql, new { SignalId = signalId, ProcessedAt = DateTime.UtcNow });
        _logger.LogInformation("Marked signal as processed: SignalId={SignalId}", signalId);
    }

    // ===== DERIV TRADE QUEUE OPERATIONS =====

    public async Task<List<TradeExecutionQueue>> GetPendingDerivTradesAsync()
    {
        const string sql = @"
            SELECT * FROM TradeExecutionQueue 
            ORDER BY CreatedAt ASC";

        using var connection = CreateConnection();
        var trades = await connection.QueryAsync<TradeExecutionQueue>(sql);
        return trades.ToList();
    }

    public async Task UpdateDerivTradeQueueOutcomeAsync(int queueId, string outcome, decimal profit)
    {
        const string sql = @"
            UPDATE TradeExecutionQueue 
            SET Outcome = @Outcome, 
                Profit = @Profit, 
                SettledAt = @SettledAt 
            WHERE QueueId = @QueueId";

        using var connection = CreateConnection();
        await connection.ExecuteAsync(sql, new 
        { 
            QueueId = queueId, 
            Outcome = outcome, 
            Profit = profit, 
            SettledAt = DateTime.UtcNow 
        });
        
        _logger.LogInformation("Updated Deriv trade outcome: QueueId={QueueId}, Outcome={Outcome}, Profit={Profit}", 
            queueId, outcome, profit);
    }
}
