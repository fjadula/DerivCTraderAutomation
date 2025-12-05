using DerivCTrader.Domain.Entities;

namespace DerivCTrader.Application.Interfaces;

/// <summary>
/// Repository interface for all database operations
/// </summary>
public interface ITradeRepository
{
    // ===== SIGNAL QUEUE OPERATIONS =====

    /// <summary>
    /// Save a parsed signal to the queue for processing
    /// </summary>
    Task<int> SaveToQueueAsync(SignalQueue signal);

    /// <summary>
    /// Get all pending signals waiting to be executed
    /// </summary>
    Task<List<SignalQueue>> GetPendingSignalsAsync();

    /// <summary>
    /// Update signal status (Pending → Processing → Executed)
    /// </summary>
    Task UpdateSignalStatusAsync(int signalId, string status);

    // ===== DERIV TRADES OPERATIONS =====

    /// <summary>
    /// Save Deriv binary option trade
    /// </summary>
    Task<int> SaveDerivTradeAsync(DerivTrade trade);

    /// <summary>
    /// Get all open Deriv trades
    /// </summary>
    Task<List<DerivTrade>> GetOpenDerivTradesAsync();

    /// <summary>
    /// Update trade outcome after expiry
    /// </summary>
    Task UpdateDerivTradeOutcomeAsync(int tradeId, string status, decimal profit);

    /// <summary>
    /// Get trade by contract ID
    /// </summary>
    Task<DerivTrade?> GetDerivTradeByContractIdAsync(string contractId);

    // ===== PROVIDER CHANNEL CONFIG =====

    /// <summary>
    /// Get provider channel configuration
    /// </summary>
    Task<ProviderChannelConfig?> GetProviderConfigAsync(string channelId);

    /// <summary>
    /// Get all active provider configs
    /// </summary>
    Task<List<ProviderChannelConfig>> GetAllActiveProvidersAsync();

    // ===== EXISTING METHODS (keep these) =====
    Task<int> CreateForexTradeAsync(ForexTrade trade);
    Task UpdateForexTradeAsync(ForexTrade trade);
    Task<ForexTrade?> GetForexTradeByIdAsync(int tradeId);
    Task<int> CreateBinaryTradeAsync(BinaryOptionTrade trade);
    Task UpdateBinaryTradeAsync(BinaryOptionTrade trade);
    Task<BinaryOptionTrade?> GetBinaryTradeByIdAsync(int tradeId);
    Task CreateTradeIndicatorAsync(TradeIndicator indicator);
    Task<int> EnqueueTradeAsync(TradeExecutionQueue queueItem);
    Task<TradeExecutionQueue?> DequeueMatchingTradeAsync(string asset, string direction);
    Task DeleteQueueItemAsync(int queueId);
    Task<List<ProviderChannelConfig>> GetAllProviderConfigsAsync();
}