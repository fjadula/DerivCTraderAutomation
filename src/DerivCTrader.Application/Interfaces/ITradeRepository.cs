using DerivCTrader.Domain.Entities;

namespace DerivCTrader.Application.Interfaces;

public interface ITradeRepository
{
    // ===== PARSED SIGNALS =====
    Task<int> SaveParsedSignalAsync(ParsedSignal signal);
    Task<List<ParsedSignal>> GetUnprocessedSignalsAsync();
    Task MarkSignalAsProcessedAsync(int signalId);

    // ===== TRADE EXECUTION QUEUE (UNIFIED) =====
    Task<int> EnqueueTradeAsync(TradeExecutionQueue queueItem);
    Task<TradeExecutionQueue?> DequeueMatchingTradeAsync(string asset, string direction);
    Task DeleteQueueItemAsync(int queueId);
    
    // Deriv-specific queue operations
    Task<List<TradeExecutionQueue>> GetPendingDerivTradesAsync();
    Task UpdateDerivTradeOutcomeAsync(int queueId, string outcome, decimal profit);

    // ===== PROVIDER CONFIG =====
    Task<ProviderChannelConfig?> GetProviderConfigAsync(string channelId);
    Task<List<ProviderChannelConfig>> GetAllProviderConfigsAsync();
    Task<List<ProviderChannelConfig>> GetAllActiveProvidersAsync();

    // ===== EXISTING METHODS (keep these) =====
    Task<int> CreateForexTradeAsync(ForexTrade trade);
    Task UpdateForexTradeAsync(ForexTrade trade);
    Task<ForexTrade?> GetForexTradeByIdAsync(int tradeId);
    Task<int> CreateBinaryTradeAsync(BinaryOptionTrade trade);
    Task UpdateBinaryTradeAsync(BinaryOptionTrade trade);
    Task<BinaryOptionTrade?> GetBinaryTradeByIdAsync(int tradeId);
    Task CreateTradeIndicatorAsync(TradeIndicator indicator);
}