namespace DerivCTrader.Domain.Entities;

/// <summary>
/// Represents a signal awaiting expiry evaluation.
/// Stored when signal is received, evaluated when expiry time elapses.
/// </summary>
public class DashaPendingSignal
{
    public int PendingSignalId { get; set; }
    public string ProviderChannelId { get; set; } = string.Empty;
    public string ProviderName { get; set; } = string.Empty;

    // Signal details
    public string Asset { get; set; } = string.Empty;       // e.g., "USDJPY"
    public string Direction { get; set; } = string.Empty;   // "UP" or "DOWN"
    public string Timeframe { get; set; } = string.Empty;   // e.g., "M15", "M5"
    public int ExpiryMinutes { get; set; }                  // Derived from timeframe

    // Price snapshots
    public decimal EntryPrice { get; set; }                 // Spot at signal receipt
    public decimal? ExitPrice { get; set; }                 // Spot at expiry (filled after wait)

    // Timing
    public DateTime SignalReceivedAt { get; set; }          // When signal arrived
    public DateTime ExpiryAt { get; set; }                  // SignalReceivedAt + ExpiryMinutes
    public DateTime? EvaluatedAt { get; set; }              // When we fetched exit price

    // Evaluation result
    public string Status { get; set; } = DashaPendingSignalStatus.AwaitingExpiry;
    public string? ProviderResult { get; set; }             // "Won" or "Lost"

    // Raw data
    public int? TelegramMessageId { get; set; }
    public string? RawMessage { get; set; }

    // Metadata
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;

    /// <summary>
    /// Determines if the provider's signal lost based on entry/exit prices.
    /// DOWN signal: Lost if ExitPrice > EntryPrice
    /// UP signal: Lost if ExitPrice < EntryPrice
    /// </summary>
    public bool DidProviderLose()
    {
        if (ExitPrice == null) return false;

        return Direction.ToUpperInvariant() switch
        {
            "DOWN" => ExitPrice.Value > EntryPrice,
            "UP" => ExitPrice.Value < EntryPrice,
            _ => false
        };
    }
}

/// <summary>
/// Status values for DashaPendingSignal
/// </summary>
public static class DashaPendingSignalStatus
{
    public const string AwaitingExpiry = "AwaitingExpiry";
    public const string ProviderWon = "ProviderWon";
    public const string ProviderLost = "ProviderLost";
    public const string Executed = "Executed";
    public const string Error = "Error";
}
