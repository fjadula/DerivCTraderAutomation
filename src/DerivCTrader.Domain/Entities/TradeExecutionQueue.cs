namespace DerivCTrader.Domain.Entities;

/// <summary>
/// Unified queue for both cTrader and Deriv trade executions
/// </summary>
public class TradeExecutionQueue
{
    public int QueueId { get; set; }
    
    // Common fields (existing)
    public string Asset { get; set; } = string.Empty;
    public string Direction { get; set; } = string.Empty;
    public string? StrategyName { get; set; }
    public bool IsOpposite { get; set; }
    public DateTime CreatedAt { get; set; }
    
    // Platform identifier
    public string Platform { get; set; } = "cTrader";  // "cTrader" or "Deriv"
    
    // cTrader-specific fields
    public string? CTraderOrderId { get; set; }
    
    // Deriv-specific fields
    public string? DerivContractId { get; set; }
    public decimal? Stake { get; set; }
    public int? ExpiryMinutes { get; set; }
    public DateTime? SettledAt { get; set; }
    public string? Outcome { get; set; }  // "Win", "Loss", null = pending
    public decimal? Profit { get; set; }
    
    // Signal metadata
    public string? Timeframe { get; set; }
    public string? Pattern { get; set; }
    public string? ProviderChannelId { get; set; }
    public string? ProviderName { get; set; }
}
