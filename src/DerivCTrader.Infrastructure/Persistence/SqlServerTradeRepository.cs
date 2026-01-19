using System.Data;
using System.Text.RegularExpressions;
using Dapper;
using DerivCTrader.Application.Interfaces;
using DerivCTrader.Domain.Entities;
using DerivCTrader.Domain.Enums;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

namespace DerivCTrader.Infrastructure.Persistence;

public class SqlServerTradeRepository : ITradeRepository
{
    public async Task<bool> IsSignalProcessedAsync(int signalId)
    {
        const string sql = @"SELECT Processed FROM ParsedSignalsQueue WHERE SignalId = @SignalId";
        using var connection = CreateConnection();
        var processed = await connection.QueryFirstOrDefaultAsync<bool?>(sql, new { SignalId = signalId });
        return processed.HasValue && processed.Value;
    }

    private readonly string _connectionString;
    private readonly ILogger<SqlServerTradeRepository> _logger;

    private readonly object _forexTradeIdInitLock = new();
    private bool? _forexTradeIdIsIdentity;

    private readonly object _parsedSignalsTimeframeInitLock = new();
    private bool? _parsedSignalsTimeframeIsInt;

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
        using var connection = CreateConnection();
        connection.Open();

        // Some DBs have TradeId as IDENTITY, others have it as NOT NULL without identity.
        // Detect once and adapt.
        if (!_forexTradeIdIsIdentity.HasValue)
        {
            var isIdentity = await connection.ExecuteScalarAsync<int>(
                "SELECT CAST(COLUMNPROPERTY(OBJECT_ID('dbo.ForexTrades'), 'TradeId', 'IsIdentity') AS int);");

            lock (_forexTradeIdInitLock)
            {
                _forexTradeIdIsIdentity ??= isIdentity == 1;
            }
        }

        if (_forexTradeIdIsIdentity == true)
        {
            const string identitySql = @"
                INSERT INTO ForexTrades (PositionId, Symbol, Direction, EntryPrice, ExitPrice, SL, TP, EntryTime, ExitTime,
                                        PnL, PnLPercent, Status, Outcome, RR, StrategyName, Notes, CreatedAt, IndicatorsLinked, TelegramMessageId)
                OUTPUT INSERTED.TradeId
                VALUES (@PositionId, @Symbol, @Direction, @EntryPrice, @ExitPrice, @SL, @TP, @EntryTime, @ExitTime,
                       @PnL, @PnLPercent, @Status, @Outcome, @RR, @Strategy, @Notes, @CreatedAt, @IndicatorsLinked, @TelegramMessageId);";

            var tradeId = await connection.ExecuteScalarAsync<int>(identitySql, trade);
            _logger.LogInformation("Created Forex trade {TradeId} for {Symbol} PositionId={PositionId}", tradeId, trade.Symbol, trade.PositionId);
            return tradeId;
        }
        else
        {
            using var tx = connection.BeginTransaction();

            const string nextIdSql = @"
                SELECT ISNULL(MAX(TradeId), 0) + 1
                FROM ForexTrades WITH (UPDLOCK, HOLDLOCK);";

            var nextId = await connection.ExecuteScalarAsync<int>(nextIdSql, transaction: tx);
            trade.TradeId = nextId;

            const string insertSql = @"
                INSERT INTO ForexTrades (TradeId, PositionId, Symbol, Direction, EntryPrice, ExitPrice, SL, TP, EntryTime, ExitTime,
                                        PnL, PnLPercent, Status, Outcome, RR, StrategyName, Notes, CreatedAt, IndicatorsLinked, TelegramMessageId)
                VALUES (@TradeId, @PositionId, @Symbol, @Direction, @EntryPrice, @ExitPrice, @SL, @TP, @EntryTime, @ExitTime,
                       @PnL, @PnLPercent, @Status, @Outcome, @RR, @Strategy, @Notes, @CreatedAt, @IndicatorsLinked, @TelegramMessageId);";

            await connection.ExecuteAsync(insertSql, trade, transaction: tx);
            tx.Commit();

            _logger.LogInformation("Created Forex trade {TradeId} for {Symbol} PositionId={PositionId}", nextId, trade.Symbol, trade.PositionId);
            return nextId;
        }
    }

    public async Task UpdateForexTradeAsync(ForexTrade trade)
    {
        const string sql = @"
            UPDATE ForexTrades
            SET Symbol = @Symbol, Direction = @Direction, EntryPrice = @EntryPrice,
                ExitPrice = @ExitPrice, SL = @SL, TP = @TP, EntryTime = @EntryTime, ExitTime = @ExitTime,
                PnL = @PnL, PnLPercent = @PnLPercent, Status = @Status, Outcome = @Outcome, RR = @RR,
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

    public async Task UpdateForexTradeTelegramMessageIdAsync(int tradeId, int telegramMessageId)
    {
        const string sql = @"
            UPDATE ForexTrades
            SET TelegramMessageId = @TelegramMessageId
            WHERE TradeId = @TradeId";

        using var connection = CreateConnection();
        await connection.ExecuteAsync(sql, new { TradeId = tradeId, TelegramMessageId = telegramMessageId });
        _logger.LogInformation("Updated TelegramMessageId for TradeId={TradeId} to {MessageId}", tradeId, telegramMessageId);
    }

    public async Task<ForexTrade?> FindLatestForexTradeByCTraderPositionIdAsync(long positionId)
    {
        // Query using the dedicated PositionId column (with fallback to Notes search for legacy data)
        const string sql = @"
            SELECT TOP 1 *
            FROM ForexTrades
            WHERE PositionId = @PositionId
               OR Notes LIKE '%CTraderPositionId=' + CAST(@PositionId AS NVARCHAR(50)) + '%'
            ORDER BY TradeId DESC";

        using var connection = CreateConnection();
        return await connection.QueryFirstOrDefaultAsync<ForexTrade>(sql, new { PositionId = positionId });
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

    // ===== SYMBOL INFO OPERATIONS =====

    public async Task<SymbolInfo?> GetSymbolInfoByNameAsync(string symbolName)
    {
        const string sql = @"
            SELECT * FROM SymbolInfo
            WHERE SymbolName = @SymbolName OR SymbolName LIKE '%' + @SymbolName + '%'";

        using var connection = CreateConnection();
        return await connection.QueryFirstOrDefaultAsync<SymbolInfo>(sql, new { SymbolName = symbolName });
    }

    public async Task<SymbolInfo?> GetSymbolInfoByCTraderIdAsync(long cTraderSymbolId)
    {
        const string sql = "SELECT * FROM SymbolInfo WHERE CTraderSymbolId = @CTraderSymbolId";
        using var connection = CreateConnection();
        return await connection.QueryFirstOrDefaultAsync<SymbolInfo>(sql, new { CTraderSymbolId = cTraderSymbolId });
    }

    public async Task<List<SymbolInfo>> GetAllSymbolInfoAsync()
    {
        const string sql = "SELECT * FROM SymbolInfo WHERE IsEnabled = 1 ORDER BY Category, SymbolName";
        using var connection = CreateConnection();
        var results = await connection.QueryAsync<SymbolInfo>(sql);
        return results.ToList();
    }

    public async Task UpsertSymbolInfoAsync(SymbolInfo symbolInfo)
    {
        const string sql = @"
            MERGE SymbolInfo AS target
            USING (SELECT @CTraderSymbolId AS CTraderSymbolId) AS source
            ON target.CTraderSymbolId = source.CTraderSymbolId
            WHEN MATCHED THEN
                UPDATE SET
                    SymbolName = @SymbolName,
                    BaseAsset = @BaseAsset,
                    QuoteAsset = @QuoteAsset,
                    PipPosition = @PipPosition,
                    MinChange = @MinChange,
                    LotSize = @LotSize,
                    MinTradeQuantity = @MinTradeQuantity,
                    MaxTradeQuantity = @MaxTradeQuantity,
                    StepVolume = @StepVolume,
                    MinSlDistancePips = @MinSlDistancePips,
                    MinTpDistancePips = @MinTpDistancePips,
                    Commission = @Commission,
                    SwapLong = @SwapLong,
                    SwapShort = @SwapShort,
                    IsEnabled = @IsEnabled,
                    Category = @Category,
                    LastUpdatedUtc = GETUTCDATE()
            WHEN NOT MATCHED THEN
                INSERT (CTraderSymbolId, SymbolName, BaseAsset, QuoteAsset, PipPosition, MinChange,
                        LotSize, MinTradeQuantity, MaxTradeQuantity, StepVolume,
                        MinSlDistancePips, MinTpDistancePips, Commission, SwapLong, SwapShort,
                        IsEnabled, Category, LastUpdatedUtc, CreatedAtUtc)
                VALUES (@CTraderSymbolId, @SymbolName, @BaseAsset, @QuoteAsset, @PipPosition, @MinChange,
                        @LotSize, @MinTradeQuantity, @MaxTradeQuantity, @StepVolume,
                        @MinSlDistancePips, @MinTpDistancePips, @Commission, @SwapLong, @SwapShort,
                        @IsEnabled, @Category, GETUTCDATE(), GETUTCDATE());";

        using var connection = CreateConnection();
        await connection.ExecuteAsync(sql, symbolInfo);
        _logger.LogInformation("Upserted SymbolInfo for {SymbolName} (CTraderSymbolId={CTraderSymbolId})",
            symbolInfo.SymbolName, symbolInfo.CTraderSymbolId);
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

    public async Task UpdateTradeExecutionQueueDerivContractAsync(int queueId, string derivContractId)
    {
        const string sql = @"
            UPDATE TradeExecutionQueue
            SET DerivContractId = @DerivContractId
            WHERE QueueId = @QueueId";

        using var connection = CreateConnection();
        await connection.ExecuteAsync(sql, new { QueueId = queueId, DerivContractId = derivContractId });
        _logger.LogInformation("Updated queue item {QueueId} with DerivContractId={DerivContractId}", queueId, derivContractId);
    }

    // ===== PARSED SIGNALS OPERATIONS =====

    public async Task<int> SaveParsedSignalAsync(ParsedSignal signal)
    {
        const string sql = @"
            INSERT INTO ParsedSignalsQueue (Asset, Direction, EntryPrice, StopLoss, TakeProfit,
                                             ProviderChannelId, ProviderName, SignalType, ReceivedAt,
                                             Processed, Timeframe, Pattern, RawMessage, TelegramMessageId, NotificationMessageId, ScheduledAtUtc)
            VALUES (@Asset, @Direction, @EntryPrice, @StopLoss, @TakeProfit,
                    @ProviderChannelId, @ProviderName, @SignalType, @ReceivedAt,
                    @Processed, @Timeframe, @Pattern, @RawMessage, @TelegramMessageId, @NotificationMessageId, @ScheduledAtUtc);
            SELECT CAST(SCOPE_IDENTITY() as int);";

        const string findExistingSql = @"
            SELECT TOP 1 SignalId
            FROM ParsedSignalsQueue
            WHERE Asset = @Asset
              AND Direction = @Direction
              AND ProviderChannelId = @ProviderChannelId
              AND ((EntryPrice = @EntryPrice) OR (EntryPrice IS NULL AND @EntryPrice IS NULL))
              AND ((StopLoss = @StopLoss) OR (StopLoss IS NULL AND @StopLoss IS NULL))
              AND ((TakeProfit = @TakeProfit) OR (TakeProfit IS NULL AND @TakeProfit IS NULL))
              AND ((ScheduledAtUtc = @ScheduledAtUtc) OR (ScheduledAtUtc IS NULL AND @ScheduledAtUtc IS NULL))
            ORDER BY SignalId DESC;";

        using var connection = CreateConnection();

        var timeframeIsInt = await IsParsedSignalsQueueTimeframeIntAsync(connection);
        object? storedTimeframe = signal.Timeframe;
        if (timeframeIsInt && !string.IsNullOrWhiteSpace(signal.Timeframe))
        {
            if (TryParseMinutesTimeframe(signal.Timeframe!, out var minutes))
            {
                storedTimeframe = minutes;
            }
            else
            {
                _logger.LogWarning(
                    "ParsedSignalsQueue.Timeframe is INT but value '{Timeframe}' is not minute-based. Storing NULL.",
                    signal.Timeframe);
                storedTimeframe = null;
            }
        }

        var storageAsset = NormalizeAssetForStorage(signal.Asset);

        var parameters = new
        {
            Asset = storageAsset,
            Direction = signal.Direction.ToString(),
            signal.EntryPrice,
            signal.StopLoss,
            signal.TakeProfit,
            signal.ProviderChannelId,
            signal.ProviderName,
            SignalType = signal.SignalType.ToString(),
            signal.ReceivedAt,
            Processed = false,
            Timeframe = storedTimeframe,
            signal.Pattern,
            signal.RawMessage,
            signal.TelegramMessageId,
            signal.NotificationMessageId,
            signal.ScheduledAtUtc
        };

        int signalId;
        try
        {
            signalId = await connection.ExecuteScalarAsync<int>(sql, parameters);
        }
        catch (SqlException ex) when (ex.Number is 2601 or 2627)
        {
            var existingSignalId = await connection.QueryFirstOrDefaultAsync<int?>(findExistingSql, new
            {
                Asset = storageAsset,
                Direction = signal.Direction.ToString(),
                signal.EntryPrice,
                signal.StopLoss,
                signal.TakeProfit,
                signal.ProviderChannelId,
                signal.ScheduledAtUtc
            });

            if (existingSignalId.HasValue)
            {
                _logger.LogInformation(
                    "Duplicate parsed signal ignored (existing SignalId={SignalId}, Asset={Asset}, Direction={Direction}, ProviderChannelId={ProviderChannelId})",
                    existingSignalId.Value, storageAsset, signal.Direction, signal.ProviderChannelId);
                return existingSignalId.Value;
            }

            _logger.LogWarning(
                ex,
                "Duplicate parsed signal insert detected but existing row not found (Asset={Asset}, Direction={Direction}, ProviderChannelId={ProviderChannelId})",
                storageAsset, signal.Direction, signal.ProviderChannelId);
            throw;
        }
        
        _logger.LogInformation("Saved parsed signal: SignalId={SignalId}, Asset={Asset}, TelegramMessageId={TelegramMessageId}", signalId, storageAsset, signal.TelegramMessageId);
        return signalId;
    }

    private async Task<bool> IsParsedSignalsQueueTimeframeIntAsync(IDbConnection connection)
    {
        if (_parsedSignalsTimeframeIsInt.HasValue)
            return _parsedSignalsTimeframeIsInt.Value;

        const string sql = @"
            SELECT TOP 1 t.name
            FROM sys.columns c
            INNER JOIN sys.types t ON c.user_type_id = t.user_type_id
            WHERE c.object_id = OBJECT_ID('dbo.ParsedSignalsQueue')
              AND c.name = 'Timeframe';";

        var typeName = await connection.ExecuteScalarAsync<string?>(sql);
        var isInt = string.Equals(typeName, "int", StringComparison.OrdinalIgnoreCase);

        lock (_parsedSignalsTimeframeInitLock)
        {
            _parsedSignalsTimeframeIsInt ??= isInt;
        }

        return isInt;
    }

    private static bool TryParseMinutesTimeframe(string timeframe, out int minutes)
    {
        minutes = 0;
        var trimmed = timeframe.Trim();

        // Accept: 5M, 5m, 5 (already minutes)
        var m = Regex.Match(trimmed, @"^(\d+)\s*M$", RegexOptions.IgnoreCase);
        if (m.Success && int.TryParse(m.Groups[1].Value, out minutes))
            return true;

        return int.TryParse(trimmed, out minutes);
    }

    private string NormalizeAssetForStorage(string? asset)
    {
        // ParsedSignalsQueue.Asset is NVARCHAR(20). Keep entries insertable while staying compatible with symbol matching.
        var cleaned = (asset ?? string.Empty).Trim();
        if (cleaned.Length <= 20)
            return cleaned;

        // Prefer removing common suffixes rather than blunt truncation
        cleaned = Regex.Replace(cleaned, @"\bINDEX\b", "", RegexOptions.IgnoreCase).Trim();
        cleaned = Regex.Replace(cleaned, @"\(\s*1s\s*\)", " 1s", RegexOptions.IgnoreCase).Trim();
        cleaned = Regex.Replace(cleaned, @"\s+", " ").Trim();
        if (cleaned.Length <= 20)
            return cleaned;

        cleaned = Regex.Replace(cleaned, @"\bVOLATILITY\b", "Vol", RegexOptions.IgnoreCase).Trim();
        cleaned = Regex.Replace(cleaned, @"\s+", " ").Trim();
        if (cleaned.Length <= 20)
            return cleaned;

        var noSpaces = cleaned.Replace(" ", "");
        if (noSpaces.Length <= 20)
            return noSpaces;

        _logger.LogWarning("Asset too long for ParsedSignalsQueue (len={Len}). Truncating: {Asset}", noSpaces.Length, noSpaces);
        return noSpaces.Substring(0, 20);
    }

    public async Task<List<ParsedSignal>> GetUnprocessedSignalsAsync()
    {
        // NOTE: Some deployments have ParsedSignalsQueue.Timeframe as INT, while the domain model uses string.
        // Cast to NVARCHAR so Dapper reliably populates ParsedSignal.Timeframe and expiry parsing works.
        const string sql = @"
            SELECT
                SignalId,
                Asset,
                Direction,
                EntryPrice,
                StopLoss,
                TakeProfit,
                ProviderChannelId,
                ProviderName,
                SignalType,
                ReceivedAt,
                Processed,
                CAST(Timeframe AS NVARCHAR(20)) AS Timeframe,
                Pattern,
                RawMessage,
                TelegramMessageId,
                NotificationMessageId
            FROM ParsedSignalsQueue
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

    public async Task MarkSignalAsUnprocessedAsync(int signalId)
    {
        const string sql = @"
            UPDATE ParsedSignalsQueue
            SET Processed = 0, ProcessedAt = NULL
            WHERE SignalId = @SignalId";

        using var connection = CreateConnection();
        await connection.ExecuteAsync(sql, new { SignalId = signalId });
        _logger.LogInformation("Marked signal as UNprocessed: SignalId={SignalId}", signalId);
    }

    public async Task UpdateParsedSignalSlTpAsync(int signalId, decimal? stopLoss, decimal? takeProfit)
    {
        const string sql = @"
            UPDATE ParsedSignalsQueue
            SET StopLoss = COALESCE(@StopLoss, StopLoss),
                TakeProfit = COALESCE(@TakeProfit, TakeProfit)
            WHERE SignalId = @SignalId";

        using var connection = CreateConnection();
        await connection.ExecuteAsync(sql, new { SignalId = signalId, StopLoss = stopLoss, TakeProfit = takeProfit });
        _logger.LogInformation("Updated ParsedSignal SL/TP: SignalId={SignalId}, SL={SL}, TP={TP}", signalId, stopLoss, takeProfit);
    }
    
    public async Task UpdateParsedSignalNotificationMessageIdAsync(int signalId, int notificationMessageId)
    {
        const string sql = @"
            UPDATE ParsedSignalsQueue
            SET NotificationMessageId = @NotificationMessageId
            WHERE SignalId = @SignalId";

        using var connection = CreateConnection();
        await connection.ExecuteAsync(sql, new { SignalId = signalId, NotificationMessageId = notificationMessageId });
        _logger.LogInformation("Updated ParsedSignal NotificationMessageId: SignalId={SignalId}, NotificationMessageId={NotificationMessageId}",
            signalId, notificationMessageId);
    }

    // ===== SCHEDULED SIGNALS (CMFLIX, etc.) =====

    public async Task<ParsedSignal?> GetNextScheduledSignalAsync(DateTime afterUtc)
    {
        const string sql = @"
            SELECT TOP 1
                SignalId,
                Asset,
                Direction,
                EntryPrice,
                StopLoss,
                TakeProfit,
                ProviderChannelId,
                ProviderName,
                SignalType,
                ReceivedAt,
                Processed,
                CAST(Timeframe AS NVARCHAR(20)) AS Timeframe,
                Pattern,
                RawMessage,
                TelegramMessageId,
                NotificationMessageId,
                ScheduledAtUtc
            FROM ParsedSignalsQueue
            WHERE ScheduledAtUtc IS NOT NULL
              AND ScheduledAtUtc > @AfterUtc
              AND Processed = 0
            ORDER BY ScheduledAtUtc ASC";

        using var connection = CreateConnection();
        return await connection.QueryFirstOrDefaultAsync<ParsedSignal>(sql, new { AfterUtc = afterUtc });
    }

    public async Task<List<ParsedSignal>> GetScheduledSignalsDueAsync(DateTime nowUtc)
    {
        const string sql = @"
            SELECT
                SignalId,
                Asset,
                Direction,
                EntryPrice,
                StopLoss,
                TakeProfit,
                ProviderChannelId,
                ProviderName,
                SignalType,
                ReceivedAt,
                Processed,
                CAST(Timeframe AS NVARCHAR(20)) AS Timeframe,
                Pattern,
                RawMessage,
                TelegramMessageId,
                NotificationMessageId,
                ScheduledAtUtc
            FROM ParsedSignalsQueue
            WHERE ScheduledAtUtc IS NOT NULL
              AND ScheduledAtUtc <= @NowUtc
              AND Processed = 0
            ORDER BY ScheduledAtUtc ASC";

        using var connection = CreateConnection();
        var signals = await connection.QueryAsync<ParsedSignal>(sql, new { NowUtc = nowUtc });
        return signals.ToList();
    }

    // ===== DERIV TRADE QUEUE OPERATIONS =====

    public async Task<List<TradeExecutionQueue>> GetPendingDerivTradesAsync()
    {
        const string sql = @"
            SELECT * FROM TradeExecutionQueue 
            WHERE (DerivContractId IS NULL OR LTRIM(RTRIM(DerivContractId)) = '')
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

    // ===== CHARTSENSE SETUPS =====

    public async Task<int> CreateChartSenseSetupAsync(ChartSenseSetup setup)
    {
        const string sql = @"
            INSERT INTO ChartSenseSetups (
                Asset, Direction, Timeframe, PatternType, PatternClassification,
                EntryPrice, EntryZoneMin, EntryZoneMax,
                CTraderOrderId, CTraderPositionId, Status, TimeoutAt,
                SignalId, TelegramMessageId, DerivContractId, DerivExpiryMinutes,
                CreatedAt, UpdatedAt, ImageHash, CalibrationData, DetectedLines, ProviderChannelId
            ) VALUES (
                @Asset, @Direction, @Timeframe, @PatternType, @PatternClassification,
                @EntryPrice, @EntryZoneMin, @EntryZoneMax,
                @CTraderOrderId, @CTraderPositionId, @Status, @TimeoutAt,
                @SignalId, @TelegramMessageId, @DerivContractId, @DerivExpiryMinutes,
                @CreatedAt, @UpdatedAt, @ImageHash, @CalibrationData, @DetectedLines, @ProviderChannelId
            );
            SELECT CAST(SCOPE_IDENTITY() as int);";

        using var connection = CreateConnection();
        var setupId = await connection.ExecuteScalarAsync<int>(sql, new
        {
            setup.Asset,
            Direction = setup.Direction.ToString(),
            setup.Timeframe,
            setup.PatternType,
            PatternClassification = setup.PatternClassification.ToString(),
            setup.EntryPrice,
            setup.EntryZoneMin,
            setup.EntryZoneMax,
            setup.CTraderOrderId,
            setup.CTraderPositionId,
            Status = setup.Status.ToString(),
            setup.TimeoutAt,
            setup.SignalId,
            setup.TelegramMessageId,
            setup.DerivContractId,
            setup.DerivExpiryMinutes,
            setup.CreatedAt,
            setup.UpdatedAt,
            setup.ImageHash,
            setup.CalibrationData,
            setup.DetectedLines,
            setup.ProviderChannelId
        });

        _logger.LogInformation("Created ChartSenseSetup: SetupId={SetupId}, Asset={Asset}, Direction={Direction}",
            setupId, setup.Asset, setup.Direction);
        return setupId;
    }

    public async Task UpdateChartSenseSetupAsync(ChartSenseSetup setup)
    {
        const string sql = @"
            UPDATE ChartSenseSetups SET
                Direction = @Direction,
                Timeframe = @Timeframe,
                PatternType = @PatternType,
                PatternClassification = @PatternClassification,
                EntryPrice = @EntryPrice,
                EntryZoneMin = @EntryZoneMin,
                EntryZoneMax = @EntryZoneMax,
                CTraderOrderId = @CTraderOrderId,
                CTraderPositionId = @CTraderPositionId,
                Status = @Status,
                TimeoutAt = @TimeoutAt,
                SignalId = @SignalId,
                TelegramMessageId = @TelegramMessageId,
                DerivContractId = @DerivContractId,
                DerivExpiryMinutes = @DerivExpiryMinutes,
                UpdatedAt = GETUTCDATE(),
                ClosedAt = @ClosedAt,
                ImageHash = @ImageHash,
                CalibrationData = @CalibrationData,
                DetectedLines = @DetectedLines
            WHERE SetupId = @SetupId";

        using var connection = CreateConnection();
        await connection.ExecuteAsync(sql, new
        {
            setup.SetupId,
            Direction = setup.Direction.ToString(),
            setup.Timeframe,
            setup.PatternType,
            PatternClassification = setup.PatternClassification.ToString(),
            setup.EntryPrice,
            setup.EntryZoneMin,
            setup.EntryZoneMax,
            setup.CTraderOrderId,
            setup.CTraderPositionId,
            Status = setup.Status.ToString(),
            setup.TimeoutAt,
            setup.SignalId,
            setup.TelegramMessageId,
            setup.DerivContractId,
            setup.DerivExpiryMinutes,
            setup.ClosedAt,
            setup.ImageHash,
            setup.CalibrationData,
            setup.DetectedLines
        });

        _logger.LogInformation("Updated ChartSenseSetup: SetupId={SetupId}, Status={Status}",
            setup.SetupId, setup.Status);
    }

    public async Task<ChartSenseSetup?> GetActiveChartSenseSetupByAssetAsync(string asset)
    {
        const string sql = @"
            SELECT * FROM ChartSenseSetups
            WHERE Asset = @Asset AND Status IN ('Watching', 'PendingPlaced', 'Filled')";

        using var connection = CreateConnection();
        var setup = await connection.QueryFirstOrDefaultAsync<ChartSenseSetupDto>(sql, new { Asset = asset });
        return setup != null ? MapToChartSenseSetup(setup) : null;
    }

    public async Task<ChartSenseSetup?> GetChartSenseSetupByIdAsync(int setupId)
    {
        const string sql = "SELECT * FROM ChartSenseSetups WHERE SetupId = @SetupId";

        using var connection = CreateConnection();
        var setup = await connection.QueryFirstOrDefaultAsync<ChartSenseSetupDto>(sql, new { SetupId = setupId });
        return setup != null ? MapToChartSenseSetup(setup) : null;
    }

    public async Task<List<ChartSenseSetup>> GetChartSenseSetupsWithExpiredTimeoutAsync()
    {
        const string sql = @"
            SELECT * FROM ChartSenseSetups
            WHERE Status = 'PendingPlaced' AND TimeoutAt IS NOT NULL AND TimeoutAt < GETUTCDATE()";

        using var connection = CreateConnection();
        var setups = await connection.QueryAsync<ChartSenseSetupDto>(sql);
        return setups.Select(MapToChartSenseSetup).ToList();
    }

    public async Task<List<ChartSenseSetup>> GetFilledChartSenseSetupsAsync()
    {
        const string sql = "SELECT * FROM ChartSenseSetups WHERE Status = 'Filled'";

        using var connection = CreateConnection();
        var setups = await connection.QueryAsync<ChartSenseSetupDto>(sql);
        return setups.Select(MapToChartSenseSetup).ToList();
    }

    public async Task<ChartSenseSetup?> GetChartSenseSetupByCTraderOrderIdAsync(long orderId)
    {
        const string sql = "SELECT * FROM ChartSenseSetups WHERE CTraderOrderId = @OrderId";

        using var connection = CreateConnection();
        var setup = await connection.QueryFirstOrDefaultAsync<ChartSenseSetupDto>(sql, new { OrderId = orderId });
        return setup != null ? MapToChartSenseSetup(setup) : null;
    }

    public async Task<ChartSenseSetup?> GetChartSenseSetupByCTraderPositionIdAsync(long positionId)
    {
        const string sql = "SELECT * FROM ChartSenseSetups WHERE CTraderPositionId = @PositionId";

        using var connection = CreateConnection();
        var setup = await connection.QueryFirstOrDefaultAsync<ChartSenseSetupDto>(sql, new { PositionId = positionId });
        return setup != null ? MapToChartSenseSetup(setup) : null;
    }

    // DTO for mapping database rows to ChartSenseSetup (handles string enums)
    private class ChartSenseSetupDto
    {
        public int SetupId { get; set; }
        public string Asset { get; set; } = string.Empty;
        public string Direction { get; set; } = string.Empty;
        public string Timeframe { get; set; } = string.Empty;
        public string PatternType { get; set; } = string.Empty;
        public string PatternClassification { get; set; } = string.Empty;
        public decimal? EntryPrice { get; set; }
        public decimal? EntryZoneMin { get; set; }
        public decimal? EntryZoneMax { get; set; }
        public long? CTraderOrderId { get; set; }
        public long? CTraderPositionId { get; set; }
        public string Status { get; set; } = string.Empty;
        public DateTime? TimeoutAt { get; set; }
        public int? SignalId { get; set; }
        public int? TelegramMessageId { get; set; }
        public string? DerivContractId { get; set; }
        public int? DerivExpiryMinutes { get; set; }
        public DateTime CreatedAt { get; set; }
        public DateTime UpdatedAt { get; set; }
        public DateTime? ClosedAt { get; set; }
        public string? ImageHash { get; set; }
        public string? CalibrationData { get; set; }
        public string? DetectedLines { get; set; }
        public string ProviderChannelId { get; set; } = string.Empty;
    }

    private static ChartSenseSetup MapToChartSenseSetup(ChartSenseSetupDto dto)
    {
        return new ChartSenseSetup
        {
            SetupId = dto.SetupId,
            Asset = dto.Asset,
            Direction = Enum.Parse<TradeDirection>(dto.Direction, ignoreCase: true),
            Timeframe = dto.Timeframe,
            PatternType = dto.PatternType,
            PatternClassification = Enum.Parse<ChartSensePatternClassification>(dto.PatternClassification, ignoreCase: true),
            EntryPrice = dto.EntryPrice,
            EntryZoneMin = dto.EntryZoneMin,
            EntryZoneMax = dto.EntryZoneMax,
            CTraderOrderId = dto.CTraderOrderId,
            CTraderPositionId = dto.CTraderPositionId,
            Status = Enum.Parse<ChartSenseStatus>(dto.Status, ignoreCase: true),
            TimeoutAt = dto.TimeoutAt,
            SignalId = dto.SignalId,
            TelegramMessageId = dto.TelegramMessageId,
            DerivContractId = dto.DerivContractId,
            DerivExpiryMinutes = dto.DerivExpiryMinutes,
            CreatedAt = dto.CreatedAt,
            UpdatedAt = dto.UpdatedAt,
            ClosedAt = dto.ClosedAt,
            ImageHash = dto.ImageHash,
            CalibrationData = dto.CalibrationData,
            DetectedLines = dto.DetectedLines,
            ProviderChannelId = dto.ProviderChannelId
        };
    }
}
