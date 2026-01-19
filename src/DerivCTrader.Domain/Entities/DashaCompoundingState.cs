namespace DerivCTrader.Domain.Entities;

/// <summary>
/// Persistent compounding ladder state per provider.
/// Survives service restarts.
///
/// Ladder: $50 -> $100 -> $200 -> RESET
/// Rules:
/// - Win at step 0: advance to step 1 ($100)
/// - Win at step 1: advance to step 2 ($200)
/// - Win at step 2: RESET to step 0 ($50)
/// - Loss at ANY step: RESET to step 0 ($50)
/// </summary>
public class DashaCompoundingState
{
    public int StateId { get; set; }
    public string ProviderChannelId { get; set; } = string.Empty;

    // Current position on ladder (0-indexed)
    public int CurrentStep { get; set; }            // 0 = $50, 1 = $100, 2 = $200
    public decimal CurrentStake { get; set; }       // Current stake amount

    // Statistics
    public int ConsecutiveWins { get; set; }
    public int TotalWins { get; set; }
    public int TotalLosses { get; set; }
    public decimal TotalProfit { get; set; }

    // Last update
    public DateTime? LastTradeAt { get; set; }
    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;
}
