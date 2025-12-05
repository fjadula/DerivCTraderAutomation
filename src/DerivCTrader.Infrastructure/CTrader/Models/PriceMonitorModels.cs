using DerivCTrader.Domain.Entities;

namespace DerivCTrader.Infrastructure.CTrader.Models;

/// <summary>
/// Represents a pending order being watched for price crosses
/// </summary>
public class PendingOrderWatch
{
    /// <summary>
    /// cTrader order ID
    /// </summary>
    public long OrderId { get; set; }

    /// <summary>
    /// cTrader symbol ID (e.g., 1 for EURUSD)
    /// </summary>
    public long SymbolId { get; set; }

    /// <summary>
    /// The parsed trading signal
    /// </summary>
    public ParsedSignal Signal { get; set; } = null!;

    /// <summary>
    /// When this watch was created
    /// </summary>
    public DateTime CreatedAt { get; set; }

    /// <summary>
    /// Flag to prevent multiple triggers for BUY orders (price crosses above entry)
    /// </summary>
    public bool HasCrossedAbove { get; set; }

    /// <summary>
    /// Flag to prevent multiple triggers for SELL orders (price crosses below entry)
    /// </summary>
    public bool HasCrossedBelow { get; set; }
}

/// <summary>
/// Event args fired when a watched order's entry price is crossed
/// </summary>
public class OrderCrossedEventArgs : EventArgs
{
    /// <summary>
    /// cTrader order ID
    /// </summary>
    public long OrderId { get; set; }

    /// <summary>
    /// The trading signal
    /// </summary>
    public ParsedSignal Signal { get; set; } = null!;

    /// <summary>
    /// The price at which the order should execute
    /// </summary>
    public double ExecutionPrice { get; set; }
}

/// <summary>
/// Price tick data from cTrader ProtoOASpotEvent
/// </summary>
public class SpotEventData
{
    /// <summary>
    /// cTrader symbol ID
    /// </summary>
    public long SymbolId { get; set; }

    /// <summary>
    /// Current bid price (price at which we can sell)
    /// </summary>
    public double Bid { get; set; }

    /// <summary>
    /// Current ask price (price at which we can buy)
    /// </summary>
    public double Ask { get; set; }

    /// <summary>
    /// Server timestamp (milliseconds since epoch)
    /// </summary>
    public long Timestamp { get; set; }
}
