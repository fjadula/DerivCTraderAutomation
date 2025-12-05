using DerivCTrader.Domain.Entities;

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
    /// <returns>True if order was placed successfully</returns>
    Task<bool> ProcessSignalAsync(ParsedSignal signal, bool isOpposite = false);

    /// <summary>
    /// Get count of currently monitored pending orders
    /// </summary>
    int GetMonitoredOrderCount();
}
