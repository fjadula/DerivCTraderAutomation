using DerivCTrader.Domain.Entities;

namespace DerivCTrader.Application.Interfaces;

/// <summary>
/// Application-level interface for cTrader trading operations
/// This is the abstraction that the Application layer depends on
/// Infrastructure layer will implement this
/// </summary>
public interface ICTraderService
{
    /// <summary>
    /// Place a market order based on parsed signal
    /// </summary>
    Task<CTraderTradeResult> PlaceMarketOrderAsync(ParsedSignal signal, bool isOpposite = false);

    /// <summary>
    /// Place a limit order at specific price
    /// </summary>
    Task<CTraderTradeResult> PlaceLimitOrderAsync(ParsedSignal signal, double limitPrice, bool isOpposite = false);

    /// <summary>
    /// Cancel a pending order
    /// </summary>
    Task<bool> CancelOrderAsync(string orderId);

    /// <summary>
    /// Get current market price for a symbol
    /// </summary>
    Task<decimal> GetCurrentPriceAsync(string symbol);

    /// <summary>
    /// Check if cTrader client is connected
    /// </summary>
    bool IsConnected { get; }

    /// <summary>
    /// Event fired when an order is executed
    /// </summary>
    event EventHandler<OrderExecutedEventArgs>? OrderExecuted;
}

/// <summary>
/// Result of a cTrader trade operation
/// </summary>
public class CTraderTradeResult
{
    public bool Success { get; set; }
    public string? OrderId { get; set; }
    public string? ErrorMessage { get; set; }
    public decimal? ExecutedPrice { get; set; }
    public DateTime ExecutedAt { get; set; }
}

/// <summary>
/// Event arguments for order execution
/// </summary>
public class OrderExecutedEventArgs : EventArgs
{
    public string OrderId { get; set; } = string.Empty;
    public string Symbol { get; set; } = string.Empty;
    public string Direction { get; set; } = string.Empty;
    public decimal ExecutionPrice { get; set; }
    public DateTime ExecutionTime { get; set; }
}