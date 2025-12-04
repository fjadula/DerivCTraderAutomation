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
            ?? throw new InvalidOperationException("Connection string 'DefaultConnection' not found");
        _logger = logger;
    }

    private IDbConnection CreateConnection() => new SqlConnection(_connectionString);

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
            INSERT INTO TradeExecutionQueue (CTraderOrderId, Asset, Direction, StrategyName, IsOpposite, CreatedAt)
            VALUES (@CTraderOrderId, @Asset, @Direction, @StrategyName, @IsOpposite, @CreatedAt);
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
}
