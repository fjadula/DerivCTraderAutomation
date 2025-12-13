using DerivCTrader.Domain.Entities;
using DerivCTrader.Infrastructure.CTrader.Models;

namespace DerivCTrader.Infrastructure.CTrader.Interfaces;

/// <summary>
/// Interface for monitoring price ticks and detecting entry crosses
/// </summary>
public interface ICTraderPriceMonitor
{
    /// <summary>
    /// Event fired when a watched order's entry price is crossed
    /// </summary>
    event EventHandler<OrderCrossedEventArgs>? OrderCrossed;

    /// <summary>
    /// Start watching a pending order for price cross detection
    /// </summary>
    /// <param name="orderId">cTrader order ID</param>
    /// <param name="symbolId">cTrader symbol ID</param>
    /// <param name="signal">The trading signal with entry price</param>
    void WatchOrder(long orderId, long symbolId, ParsedSignal signal, bool isOpposite = false);

    /// <summary>
    /// Stop watching an order (e.g., if cancelled)
    /// </summary>
    void StopWatching(long orderId);

    /// <summary>
    /// Get all currently watched orders
    /// </summary>
    IReadOnlyDictionary<long, PendingOrderWatch> WatchedOrders { get; }
}
