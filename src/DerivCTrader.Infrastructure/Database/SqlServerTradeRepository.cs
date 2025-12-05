using System.Data.SqlClient;
using DerivCTrader.Application.Interfaces;
using DerivCTrader.Domain.Entities;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Microsoft.Data.SqlClient;

namespace DerivCTrader.Infrastructure.Database;

public class SqlServerTradeRepository : ITradeRepository
{
    private readonly string _connectionString;
    private readonly ILogger<SqlServerTradeRepository> _logger;

    public SqlServerTradeRepository(IConfiguration configuration, ILogger<SqlServerTradeRepository> logger)
    {
        _connectionString = configuration.GetConnectionString("DefaultConnection")
            ?? throw new ArgumentNullException("Connection string not found");
        _logger = logger;
    }

    // ===== SIGNAL QUEUE OPERATIONS =====

    public async Task<int> SaveToQueueAsync(SignalQueue signal)
    {
        try
        {
            const string sql = @"
                INSERT INTO SignalQueue (
                    ProviderChannelId, Asset, Direction, EntryPrice, 
                    TakeProfit, StopLoss, SignalType, Status, 
                    ReceivedAt, CreatedAt
                ) VALUES (
                    @ProviderChannelId, @Asset, @Direction, @EntryPrice,
                    @TakeProfit, @StopLoss, @SignalType, @Status,
                    @ReceivedAt, @CreatedAt
                );
                SELECT CAST(SCOPE_IDENTITY() as int);";

            using var connection = new SqlConnection(_connectionString);
            await connection.OpenAsync();

            using var command = new SqlCommand(sql, connection);
            command.Parameters.AddWithValue("@ProviderChannelId", signal.ProviderChannelId);
            command.Parameters.AddWithValue("@Asset", signal.Asset);
            command.Parameters.AddWithValue("@Direction", signal.Direction);
            command.Parameters.AddWithValue("@EntryPrice", signal.EntryPrice ?? (object)DBNull.Value);
            command.Parameters.AddWithValue("@TakeProfit", signal.TakeProfit ?? (object)DBNull.Value);
            command.Parameters.AddWithValue("@StopLoss", signal.StopLoss ?? (object)DBNull.Value);
            command.Parameters.AddWithValue("@SignalType", signal.SignalType);
            command.Parameters.AddWithValue("@Status", signal.Status);
            command.Parameters.AddWithValue("@ReceivedAt", signal.ReceivedAt);
            command.Parameters.AddWithValue("@CreatedAt", signal.CreatedAt);

            var signalId = (int)await command.ExecuteScalarAsync();

            _logger.LogInformation("✅ Signal saved to queue: ID={SignalId}, Asset={Asset}",
                signalId, signal.Asset);

            return signalId;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to save signal to queue");
            throw;
        }
    }

    public async Task<List<SignalQueue>> GetPendingSignalsAsync()
    {
        try
        {
            const string sql = @"
                SELECT 
                    SignalId, ProviderChannelId, Asset, Direction, 
                    EntryPrice, TakeProfit, StopLoss, SignalType, 
                    Status, ReceivedAt, CreatedAt, ProcessedAt
                FROM SignalQueue
                WHERE Status = 'Pending'
                ORDER BY ReceivedAt ASC";

            using var connection = new SqlConnection(_connectionString);
            await connection.OpenAsync();

            using var command = new SqlCommand(sql, connection);
            using var reader = await command.ExecuteReaderAsync();

            var signals = new List<SignalQueue>();
            while (await reader.ReadAsync())
            {
                signals.Add(new SignalQueue
                {
                    SignalId = reader.GetInt32(0),
                    ProviderChannelId = reader.GetString(1),
                    Asset = reader.GetString(2),
                    Direction = reader.GetString(3),
                    EntryPrice = reader.IsDBNull(4) ? null : reader.GetDecimal(4),
                    TakeProfit = reader.IsDBNull(5) ? null : reader.GetDecimal(5),
                    StopLoss = reader.IsDBNull(6) ? null : reader.GetDecimal(6),
                    SignalType = reader.GetString(7),
                    Status = reader.GetString(8),
                    ReceivedAt = reader.GetDateTime(9),
                    CreatedAt = reader.GetDateTime(10),
                    ProcessedAt = reader.IsDBNull(11) ? null : reader.GetDateTime(11)
                });
            }

            return signals;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to get pending signals");
            throw;
        }
    }

    public async Task UpdateSignalStatusAsync(int signalId, string status)
    {
        try
        {
            const string sql = @"
                UPDATE SignalQueue 
                SET Status = @Status,
                    ProcessedAt = CASE WHEN @Status = 'Executed' THEN GETUTCDATE() ELSE ProcessedAt END
                WHERE SignalId = @SignalId";

            using var connection = new SqlConnection(_connectionString);
            await connection.OpenAsync();

            using var command = new SqlCommand(sql, connection);
            command.Parameters.AddWithValue("@SignalId", signalId);
            command.Parameters.AddWithValue("@Status", status);

            await command.ExecuteNonQueryAsync();

            _logger.LogInformation("✅ Signal {SignalId} status updated to {Status}", signalId, status);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to update signal status");
            throw;
        }
    }

    // ===== DERIV TRADES OPERATIONS =====

    public async Task<int> SaveDerivTradeAsync(DerivTrade trade)
    {
        try
        {
            const string sql = @"
                INSERT INTO DerivTrades (
                    SignalId, ContractId, Asset, Direction, Stake,
                    Expiry, PurchasePrice, Payout, Status, StrategyName,
                    PurchasedAt, CreatedAt
                ) VALUES (
                    @SignalId, @ContractId, @Asset, @Direction, @Stake,
                    @Expiry, @PurchasePrice, @Payout, @Status, @StrategyName,
                    @PurchasedAt, @CreatedAt
                );
                SELECT CAST(SCOPE_IDENTITY() as int);";

            using var connection = new SqlConnection(_connectionString);
            await connection.OpenAsync();

            using var command = new SqlCommand(sql, connection);
            command.Parameters.AddWithValue("@SignalId", trade.SignalId ?? (object)DBNull.Value);
            command.Parameters.AddWithValue("@ContractId", trade.ContractId);
            command.Parameters.AddWithValue("@Asset", trade.Asset);
            command.Parameters.AddWithValue("@Direction", trade.Direction);
            command.Parameters.AddWithValue("@Stake", trade.Stake);
            command.Parameters.AddWithValue("@Expiry", trade.Expiry);
            command.Parameters.AddWithValue("@PurchasePrice", trade.PurchasePrice);
            command.Parameters.AddWithValue("@Payout", trade.Payout ?? (object)DBNull.Value);
            command.Parameters.AddWithValue("@Status", trade.Status);
            command.Parameters.AddWithValue("@StrategyName", trade.StrategyName ?? (object)DBNull.Value);
            command.Parameters.AddWithValue("@PurchasedAt", trade.PurchasedAt);
            command.Parameters.AddWithValue("@CreatedAt", trade.CreatedAt);

            var tradeId = (int)await command.ExecuteScalarAsync();

            _logger.LogInformation("✅ Deriv trade saved: ID={TradeId}, Contract={ContractId}",
                tradeId, trade.ContractId);

            return tradeId;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to save Deriv trade");
            throw;
        }
    }

    public async Task<List<DerivTrade>> GetOpenDerivTradesAsync()
    {
        try
        {
            const string sql = @"
                SELECT 
                    TradeId, SignalId, ContractId, Asset, Direction,
                    Stake, Expiry, PurchasePrice, Payout, Status,
                    StrategyName, PurchasedAt, SettledAt, Profit, CreatedAt
                FROM DerivTrades
                WHERE Status = 'Open'
                ORDER BY PurchasedAt ASC";

            using var connection = new SqlConnection(_connectionString);
            await connection.OpenAsync();

            using var command = new SqlCommand(sql, connection);
            using var reader = await command.ExecuteReaderAsync();

            var trades = new List<DerivTrade>();
            while (await reader.ReadAsync())
            {
                trades.Add(new DerivTrade
                {
                    TradeId = reader.GetInt32(0),
                    SignalId = reader.IsDBNull(1) ? null : reader.GetInt32(1),
                    ContractId = reader.GetString(2),
                    Asset = reader.GetString(3),
                    Direction = reader.GetString(4),
                    Stake = reader.GetDecimal(5),
                    Expiry = reader.GetInt32(6),
                    PurchasePrice = reader.GetDecimal(7),
                    Payout = reader.IsDBNull(8) ? null : reader.GetDecimal(8),
                    Status = reader.GetString(9),
                    StrategyName = reader.IsDBNull(10) ? null : reader.GetString(10),
                    PurchasedAt = reader.GetDateTime(11),
                    SettledAt = reader.IsDBNull(12) ? null : reader.GetDateTime(12),
                    Profit = reader.IsDBNull(13) ? null : reader.GetDecimal(13),
                    CreatedAt = reader.GetDateTime(14)
                });
            }

            return trades;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to get open Deriv trades");
            throw;
        }
    }

    public async Task UpdateDerivTradeOutcomeAsync(int tradeId, string status, decimal profit)
    {
        try
        {
            const string sql = @"
                UPDATE DerivTrades 
                SET Status = @Status,
                    Profit = @Profit,
                    SettledAt = GETUTCDATE()
                WHERE TradeId = @TradeId";

            using var connection = new SqlConnection(_connectionString);
            await connection.OpenAsync();

            using var command = new SqlCommand(sql, connection);
            command.Parameters.AddWithValue("@TradeId", tradeId);
            command.Parameters.AddWithValue("@Status", status);
            command.Parameters.AddWithValue("@Profit", profit);

            await command.ExecuteNonQueryAsync();

            _logger.LogInformation("📊 Trade {TradeId} outcome: {Status} Profit={Profit}",
                tradeId, status, profit);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to update Deriv trade outcome");
            throw;
        }
    }

    public async Task<DerivTrade?> GetDerivTradeByContractIdAsync(string contractId)
    {
        try
        {
            const string sql = @"
                SELECT 
                    TradeId, SignalId, ContractId, Asset, Direction,
                    Stake, Expiry, PurchasePrice, Payout, Status,
                    StrategyName, PurchasedAt, SettledAt, Profit, CreatedAt
                FROM DerivTrades
                WHERE ContractId = @ContractId";

            using var connection = new SqlConnection(_connectionString);
            await connection.OpenAsync();

            using var command = new SqlCommand(sql, connection);
            command.Parameters.AddWithValue("@ContractId", contractId);

            using var reader = await command.ExecuteReaderAsync();

            if (await reader.ReadAsync())
            {
                return new DerivTrade
                {
                    TradeId = reader.GetInt32(0),
                    SignalId = reader.IsDBNull(1) ? null : reader.GetInt32(1),
                    ContractId = reader.GetString(2),
                    Asset = reader.GetString(3),
                    Direction = reader.GetString(4),
                    Stake = reader.GetDecimal(5),
                    Expiry = reader.GetInt32(6),
                    PurchasePrice = reader.GetDecimal(7),
                    Payout = reader.IsDBNull(8) ? null : reader.GetDecimal(8),
                    Status = reader.GetString(9),
                    StrategyName = reader.IsDBNull(10) ? null : reader.GetString(10),
                    PurchasedAt = reader.GetDateTime(11),
                    SettledAt = reader.IsDBNull(12) ? null : reader.GetDateTime(12),
                    Profit = reader.IsDBNull(13) ? null : reader.GetDecimal(13),
                    CreatedAt = reader.GetDateTime(14)
                };
            }

            return null;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to get Deriv trade by contract ID");
            throw;
        }
    }

    // ===== PROVIDER CHANNEL CONFIG =====

    public async Task<ProviderChannelConfig?> GetProviderConfigAsync(string channelId)
    {
        try
        {
            const string sql = @"
                SELECT 
                    ProviderChannelId, ProviderName, TakeOriginal,
                    TakeOpposite, IsActive, CreatedAt
                FROM ProviderChannelConfig
                WHERE ProviderChannelId = @ChannelId";

            using var connection = new SqlConnection(_connectionString);
            await connection.OpenAsync();

            using var command = new SqlCommand(sql, connection);
            command.Parameters.AddWithValue("@ChannelId", channelId);

            using var reader = await command.ExecuteReaderAsync();

            if (await reader.ReadAsync())
            {
                return new ProviderChannelConfig
                {
                    ProviderChannelId = reader.GetString(0),
                    ProviderName = reader.GetString(1),
                    TakeOriginal = reader.GetBoolean(2),
                    TakeOpposite = reader.GetBoolean(3),
                    IsActive = reader.GetBoolean(4),
                    CreatedAt = reader.GetDateTime(5)
                };
            }

            return null;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to get provider config");
            throw;
        }
    }

    public async Task<List<ProviderChannelConfig>> GetAllActiveProvidersAsync()
    {
        try
        {
            const string sql = @"
                SELECT 
                    ProviderChannelId, ProviderName, TakeOriginal,
                    TakeOpposite, IsActive, CreatedAt
                FROM ProviderChannelConfig
                WHERE IsActive = 1
                ORDER BY ProviderName";

            using var connection = new SqlConnection(_connectionString);
            await connection.OpenAsync();

            using var command = new SqlCommand(sql, connection);
            using var reader = await command.ExecuteReaderAsync();

            var configs = new List<ProviderChannelConfig>();
            while (await reader.ReadAsync())
            {
                configs.Add(new ProviderChannelConfig
                {
                    ProviderChannelId = reader.GetString(0),
                    ProviderName = reader.GetString(1),
                    TakeOriginal = reader.GetBoolean(2),
                    TakeOpposite = reader.GetBoolean(3),
                    IsActive = reader.GetBoolean(4),
                    CreatedAt = reader.GetDateTime(5)
                });
            }

            return configs;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to get active providers");
            throw;
        }
    }
}