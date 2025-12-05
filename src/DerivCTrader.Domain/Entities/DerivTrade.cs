namespace DerivCTrader.Domain.Entities;

/// <summary>
/// Entity for Deriv binary option trades
/// </summary>
public class DerivTrade
{
    public int TradeId { get; set; }
    public int? SignalId { get; set; }
    public string ContractId { get; set; } = string.Empty;
    public string Asset { get; set; } = string.Empty;
    public string Direction { get; set; } = string.Empty;  // "Call" or "Put"
    public decimal Stake { get; set; }
    public int ExpiryMinutes { get; set; }
    public int? Expiry { get; set; }
    public decimal? EntryPrice { get; set; }
    public decimal? ExitPrice { get; set; }
    public decimal? PurchasePrice { get; set; }
    public decimal? Payout { get; set; }
    public string Status { get; set; } = "Open";  // "Open", "Won", "Lost"
    public decimal? Profit { get; set; }
    public DateTime OpenTime { get; set; }
    public DateTime? PurchasedAt { get; set; }
    public DateTime? SettledAt { get; set; }
    public DateTime? CloseTime { get; set; }
    public DateTime CreatedAt { get; set; }
    public string? StrategyName { get; set; }
    public string? Timeframe { get; set; }
    public string? Pattern { get; set; }
    public string ProviderChannelId { get; set; } = string.Empty;
    public string ProviderName { get; set; } = string.Empty;
}