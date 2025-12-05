using DerivCTrader.Domain.Enums;

namespace DerivCTrader.Domain.Entities;

/// <summary>
/// Queue for parsed signals waiting to be processed by TradeExecutor
/// </summary>
public class SignalQueue
{
    public int SignalId { get; set; }
    public string ProviderChannelId { get; set; } = string.Empty;
    public string ProviderName { get; set; } = string.Empty;
    public string Asset { get; set; } = string.Empty;
    public string Direction { get; set; } = string.Empty;  // "Buy", "Sell", "Call", "Put"
    public decimal? EntryPrice { get; set; }
    public decimal? StopLoss { get; set; }
    public decimal? TakeProfit { get; set; }
    public string SignalType { get; set; } = string.Empty;  // "Text", "Image", "PureBinary"
    public string Status { get; set; } = "Pending";  // "Pending", "Processing", "Executed", "Failed"
    public DateTime ReceivedAt { get; set; }
    public DateTime CreatedAt { get; set; }
    public DateTime? ProcessedAt { get; set; }
    public string? Timeframe { get; set; }  // e.g., "H4", "15M"
    public string? Pattern { get; set; }  // e.g., "Rising wedge"
    public string? RawMessage { get; set; }
}