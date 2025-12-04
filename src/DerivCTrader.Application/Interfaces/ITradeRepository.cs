using DerivCTrader.Domain.Entities;

namespace DerivCTrader.Application.Interfaces;

public interface ITradeRepository
{
    // ForexTrades
    Task<int> CreateForexTradeAsync(ForexTrade trade);
    Task UpdateForexTradeAsync(ForexTrade trade);
    Task<ForexTrade?> GetForexTradeByIdAsync(int tradeId);
    
    // BinaryOptionTrades
    Task<int> CreateBinaryTradeAsync(BinaryOptionTrade trade);
    Task UpdateBinaryTradeAsync(BinaryOptionTrade trade);
    Task<BinaryOptionTrade?> GetBinaryTradeByIdAsync(int tradeId);
    
    // TradeIndicators
    Task CreateTradeIndicatorAsync(TradeIndicator indicator);
    
    // TradeExecutionQueue
    Task<int> EnqueueTradeAsync(TradeExecutionQueue queueItem);
    Task<TradeExecutionQueue?> DequeueMatchingTradeAsync(string asset, string direction);
    Task DeleteQueueItemAsync(int queueId);
    
    // ProviderChannelConfig
    Task<ProviderChannelConfig?> GetProviderConfigAsync(string providerChannelId);
    Task<List<ProviderChannelConfig>> GetAllProviderConfigsAsync();
}
