namespace DerivCTrader.Domain.Entities;

/// <summary>
/// Matching queue between cTrader executions and Deriv binary detections
/// 
/// PURPOSE: Acts as a buffer to match cTrader order executions with Deriv binary option placements
/// 
/// FLOW:
/// 1. cTrader: Order executes (price crosses entry) → Write to this queue
/// 2. Deriv: Place binary option with queue data
/// 3. KhulaFxTradeMonitor: Detects binary execution on Deriv
/// 4. Match: Find corresponding row using (Asset, Direction) FIFO
/// 5. Cleanup: Delete matched queue row
/// 
/// NOTE: This queue contains ONLY matching metadata, not full trade details.
/// Full Deriv trade details (stake, expiry, outcome, profit) belong in BinaryOptionTrades table.
/// </summary>
public class TradeExecutionQueue
{
    /// <summary>
    /// Queue row ID (auto-increment)
    /// </summary>
    public int QueueId { get; set; }

    /// <summary>
    /// cTrader order ID from execution event
    /// </summary>
    public string? CTraderOrderId { get; set; }

    /// <summary>
    /// Asset symbol (e.g., "EURUSD")
    /// Used for matching with KhulaFxTradeMonitor detections
    /// </summary>
    public string Asset { get; set; } = string.Empty;

    /// <summary>
    /// Trade direction (e.g., "Buy", "Sell")
    /// Used for matching with KhulaFxTradeMonitor detections
    /// </summary>
    public string Direction { get; set; } = string.Empty;

    /// <summary>
    /// Strategy name to populate in BinaryOptionTrades
    /// Format: "ProviderName_Asset_Timestamp"
    /// </summary>
    public string? StrategyName { get; set; }

    /// <summary>
    /// Provider channel ID (for audit trail)
    /// </summary>
    public string? ProviderChannelId { get; set; }

    /// <summary>
    /// Whether this is an opposite direction trade
    /// </summary>
    public bool IsOpposite { get; set; }

    /// <summary>
    /// When the cTrader execution was detected
    /// </summary>
    public DateTime CreatedAt { get; set; }
}
