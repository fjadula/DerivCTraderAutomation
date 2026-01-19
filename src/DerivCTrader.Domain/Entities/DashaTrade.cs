namespace DerivCTrader.Domain.Entities;

/// <summary>
/// Executed trade record with full audit trail.
/// Created only when provider's signal lost and we execute on Deriv.
/// </summary>
public class DashaTrade
{
    public int TradeId { get; set; }
    public int PendingSignalId { get; set; }
    public string ProviderChannelId { get; set; } = string.Empty;
    public string ProviderName { get; set; } = string.Empty;

    // Trade details
    public string Asset { get; set; } = string.Empty;
    public string Direction { get; set; } = string.Empty;   // "CALL" or "PUT"
    public int ExpiryMinutes { get; set; }

    // Deriv execution
    public string? DerivContractId { get; set; }            // NULL until executed
    public decimal Stake { get; set; }
    public int StakeStep { get; set; }                      // 0, 1, or 2 (position on ladder)
    public decimal? PurchasePrice { get; set; }
    public decimal? Payout { get; set; }

    // Provider context
    public decimal ProviderEntryPrice { get; set; }         // Provider's entry snapshot
    public decimal ProviderExitPrice { get; set; }          // Provider's exit price at expiry
    public string ProviderResult { get; set; } = "Lost";    // Always "Lost" since we only trade on loss

    // Our execution result
    public string? ExecutionResult { get; set; }            // "Won" or "Lost"
    public decimal? Profit { get; set; }

    // Timing
    public DateTime ProviderSignalAt { get; set; }
    public DateTime ProviderExpiryAt { get; set; }
    public DateTime? ExecutedAt { get; set; }
    public DateTime? SettledAt { get; set; }

    // Telegram notification
    public int? TelegramMessageId { get; set; }

    // Metadata
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

    /// <summary>
    /// Returns true if this trade has been settled (outcome known).
    /// </summary>
    public bool IsSettled => SettledAt.HasValue && ExecutionResult != null;

    /// <summary>
    /// Returns true if we won this trade.
    /// </summary>
    public bool Won => ExecutionResult == "Won";
}
