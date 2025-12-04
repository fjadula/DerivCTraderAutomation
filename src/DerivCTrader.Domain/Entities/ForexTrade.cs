using DerivCTrader.Domain.Enums;

namespace DerivCTrader.Domain.Entities;

public class ForexTrade
{
    public int TradeId { get; set; }
    public string Symbol { get; set; } = string.Empty;
    public string Direction { get; set; } = string.Empty;
    public decimal? EntryPrice { get; set; }
    public decimal? ExitPrice { get; set; }
    public DateTime? EntryTime { get; set; }
    public DateTime? ExitTime { get; set; }
    public decimal? PnL { get; set; }
    public decimal? PnLPercent { get; set; }
    public string? Status { get; set; }
    public string? Notes { get; set; }
    public DateTime CreatedAt { get; set; }
    public bool? IndicatorsLinked { get; set; }
}
