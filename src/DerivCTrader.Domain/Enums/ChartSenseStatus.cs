namespace DerivCTrader.Domain.Enums;

/// <summary>
/// Status values for ChartSense setups
/// </summary>
public enum ChartSenseStatus
{
    /// <summary>Setup detected from image, monitoring for entry</summary>
    Watching,

    /// <summary>Pending cTrader order has been placed</summary>
    PendingPlaced,

    /// <summary>Order has been filled, position is active</summary>
    Filled,

    /// <summary>Position closed (TP/SL/Manual)</summary>
    Closed,

    /// <summary>Timeout reached without fill - order cancelled</summary>
    Expired,

    /// <summary>Direction flipped or manually cancelled</summary>
    Invalidated
}
