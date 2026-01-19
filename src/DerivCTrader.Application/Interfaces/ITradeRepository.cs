using DerivCTrader.Domain.Entities;

namespace DerivCTrader.Application.Interfaces;

public interface ITradeRepository
{
    Task<bool> IsSignalProcessedAsync(int signalId);
    // ===== PARSED SIGNALS =====
    Task<int> SaveParsedSignalAsync(ParsedSignal signal);
    Task<List<ParsedSignal>> GetUnprocessedSignalsAsync();
    Task MarkSignalAsProcessedAsync(int signalId);
    Task MarkSignalAsUnprocessedAsync(int signalId);
    Task UpdateParsedSignalSlTpAsync(int signalId, decimal? stopLoss, decimal? takeProfit);
    
    /// <summary>
    /// Update the NotificationMessageId for a signal (used for threading order/fill notifications)
    /// </summary>
    Task UpdateParsedSignalNotificationMessageIdAsync(int signalId, int notificationMessageId);

    // ===== SCHEDULED SIGNALS (CMFLIX, etc.) =====
    /// <summary>
    /// Get the next unprocessed scheduled signal (by ScheduledAtUtc) after a given time.
    /// Used by ScheduledBinaryExecutionService to find the next signal to wait for.
    /// </summary>
    Task<ParsedSignal?> GetNextScheduledSignalAsync(DateTime afterUtc);

    /// <summary>
    /// Get all unprocessed scheduled signals that are due (ScheduledAtUtc <= nowUtc).
    /// Used for processing signals that may have been missed.
    /// </summary>
    Task<List<ParsedSignal>> GetScheduledSignalsDueAsync(DateTime nowUtc);

    // ===== TRADE EXECUTION QUEUE (UNIFIED) =====
    Task<int> EnqueueTradeAsync(TradeExecutionQueue queueItem);
    Task<TradeExecutionQueue?> DequeueMatchingTradeAsync(string asset, string direction);
    Task DeleteQueueItemAsync(int queueId);
    Task UpdateTradeExecutionQueueDerivContractAsync(int queueId, string derivContractId);
    
    // Deriv-specific queue operations
    Task<List<TradeExecutionQueue>> GetPendingDerivTradesAsync();
    Task UpdateDerivTradeOutcomeAsync(int queueId, string outcome, decimal profit);

    // ===== PROVIDER CONFIG =====
    Task<ProviderChannelConfig?> GetProviderConfigAsync(string channelId);
    Task<List<ProviderChannelConfig>> GetAllProviderConfigsAsync();
    Task<List<ProviderChannelConfig>> GetAllActiveProvidersAsync();

    // ===== SYMBOL INFO =====
    Task<SymbolInfo?> GetSymbolInfoByNameAsync(string symbolName);
    Task<SymbolInfo?> GetSymbolInfoByCTraderIdAsync(long cTraderSymbolId);
    Task<List<SymbolInfo>> GetAllSymbolInfoAsync();
    Task UpsertSymbolInfoAsync(SymbolInfo symbolInfo);

    // ===== EXISTING METHODS (keep these) =====
    Task<int> CreateForexTradeAsync(ForexTrade trade);
    Task UpdateForexTradeAsync(ForexTrade trade);
    Task<ForexTrade?> GetForexTradeByIdAsync(int tradeId);

    /// <summary>
    /// Update the TelegramMessageId for a forex trade (used for threading close/modify notifications)
    /// </summary>
    Task UpdateForexTradeTelegramMessageIdAsync(int tradeId, int telegramMessageId);
    Task<ForexTrade?> FindLatestForexTradeByCTraderPositionIdAsync(long positionId);
    Task<int> CreateBinaryTradeAsync(BinaryOptionTrade trade);
    Task UpdateBinaryTradeAsync(BinaryOptionTrade trade);
    Task<BinaryOptionTrade?> GetBinaryTradeByIdAsync(int tradeId);
    Task CreateTradeIndicatorAsync(TradeIndicator indicator);

    // ===== CHARTSENSE SETUPS =====
    /// <summary>Create a new ChartSense setup</summary>
    Task<int> CreateChartSenseSetupAsync(ChartSenseSetup setup);

    /// <summary>Update an existing ChartSense setup</summary>
    Task UpdateChartSenseSetupAsync(ChartSenseSetup setup);

    /// <summary>Get active setup for an asset (status: Watching, PendingPlaced, or Filled)</summary>
    Task<ChartSenseSetup?> GetActiveChartSenseSetupByAssetAsync(string asset);

    /// <summary>Get setup by ID</summary>
    Task<ChartSenseSetup?> GetChartSenseSetupByIdAsync(int setupId);

    /// <summary>Get setups with expired timeout (for timeout monitor)</summary>
    Task<List<ChartSenseSetup>> GetChartSenseSetupsWithExpiredTimeoutAsync();

    /// <summary>Get all filled setups (for position tracking)</summary>
    Task<List<ChartSenseSetup>> GetFilledChartSenseSetupsAsync();

    /// <summary>Get setup by cTrader order ID</summary>
    Task<ChartSenseSetup?> GetChartSenseSetupByCTraderOrderIdAsync(long orderId);

    /// <summary>Get setup by cTrader position ID</summary>
    Task<ChartSenseSetup?> GetChartSenseSetupByCTraderPositionIdAsync(long positionId);
}