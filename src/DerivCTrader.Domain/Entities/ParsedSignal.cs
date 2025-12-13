using DerivCTrader.Domain.Enums;

namespace DerivCTrader.Domain.Entities;

public class ParsedSignal
{
    public int SignalId { get; set; }  // Add this if missing
    public string Asset { get; set; } = string.Empty;
    public TradeDirection Direction { get; set; }
    public decimal? EntryPrice { get; set; }
    public decimal? StopLoss { get; set; }
    public decimal? TakeProfit { get; set; }
    public decimal? TakeProfit2 { get; set; }
    public decimal? TakeProfit3 { get; set; }
    public decimal? TakeProfit4 { get; set; }
    public decimal? RiskRewardRatio { get; set; }
    public decimal? LotSize { get; set; }
    public double RiskUsd { get; set; }  // Risk amount in USD for synthetic indices
    public double StopLossTicks { get; set; }  // Stop loss in ticks for synthetic indices
    public string ProviderChannelId { get; set; } = string.Empty;
    public string ProviderName { get; set; } = string.Empty;
    public SignalType SignalType { get; set; }
    public DateTime ReceivedAt { get; set; }
    public string? RawMessage { get; set; }
    public string? Pattern { get; set; }  // For ChartSense: "Rising wedge", "Wedge", etc.
    public string? Timeframe { get; set; }  // e.g., "H4", "1H", "15M"
    public bool Processed { get; set; } = false;  // Add this
    public DateTime? ProcessedAt { get; set; }  // Add this
}
