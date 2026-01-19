namespace DerivCTrader.Domain.Entities;

/// <summary>
/// Per-provider configuration for selective martingale execution.
/// Supports extensibility for adding new providers with different settings.
/// </summary>
public class DashaProviderConfig
{
    public int ConfigId { get; set; }
    public string ProviderChannelId { get; set; } = string.Empty;
    public string ProviderName { get; set; } = string.Empty;

    // Martingale configuration
    public decimal InitialStake { get; set; } = 50.00m;
    public string LadderSteps { get; set; } = "50,100,200";  // Comma-separated stake ladder
    public int ResetAfterStep { get; set; } = 3;

    // Signal configuration
    public int DefaultExpiryMinutes { get; set; } = 15;

    // Flags
    public bool IsActive { get; set; } = true;
    public bool ExecuteOnProviderLoss { get; set; } = true;

    // Metadata
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;

    /// <summary>
    /// Parses the LadderSteps string into an array of decimal stakes.
    /// </summary>
    public decimal[] GetLadderArray()
    {
        return LadderSteps
            .Split(',', StringSplitOptions.RemoveEmptyEntries)
            .Select(s => decimal.Parse(s.Trim()))
            .ToArray();
    }
}
