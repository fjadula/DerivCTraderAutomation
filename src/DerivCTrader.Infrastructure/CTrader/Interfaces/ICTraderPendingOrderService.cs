using DerivCTrader.Domain.Entities;
using DerivCTrader.Infrastructure.CTrader.Models;

namespace DerivCTrader.Infrastructure.CTrader.Interfaces;

/// <summary>
/// Interface for orchestrating the complete pending order flow
/// </summary>
public interface ICTraderPendingOrderService
{
    /// <summary>
    /// Process a parsed signal by placing a pending order and monitoring for execution
    /// </summary>
    /// <param name="signal">The parsed trading signal</param>
    /// <param name="isOpposite">Whether to trade opposite direction</param>
    /// <returns>Order placement/execution result (includes SL/TP amend outcome when applicable)</returns>
    Task<CTraderOrderResult> ProcessSignalAsync(ParsedSignal signal, bool isOpposite = false);

    /// <summary>
    /// Get count of currently monitored pending orders
    /// </summary>
    int GetMonitoredOrderCount();
}
