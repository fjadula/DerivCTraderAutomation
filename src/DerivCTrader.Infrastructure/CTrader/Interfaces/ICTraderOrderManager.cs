using DerivCTrader.Domain.Entities;
using DerivCTrader.Infrastructure.CTrader.Models;

namespace DerivCTrader.Infrastructure.CTrader.Interfaces;

/// <summary>
/// Interface for cTrader order management operations
/// </summary>
public interface ICTraderOrderManager
{
    /// <summary>
    /// Create a new pending order
    /// </summary>
    Task<CTraderOrderResult> CreateOrderAsync(ParsedSignal signal, CTraderOrderType orderType, bool isOpposite = false);

    /// <summary>
    /// Cancel a pending order
    /// </summary>
    Task<bool> CancelOrderAsync(long orderId);

    /// <summary>
    /// Modify stop loss and take profit on existing position
    /// </summary>
    Task<bool> ModifyPositionAsync(long positionId, double? stopLoss, double? takeProfit);

    /// <summary>
    /// Close an existing position
    /// </summary>
    Task<bool> ClosePositionAsync(long positionId, double volume);

    /// <summary>
    /// Get current market price for a symbol
    /// </summary>
    Task<double?> GetCurrentPriceAsync(string symbol);

    /// <summary>
    /// Get current bid/ask for a symbol (when available)
    /// </summary>
    Task<(double? Bid, double? Ask)> GetCurrentBidAskAsync(string symbol);
}