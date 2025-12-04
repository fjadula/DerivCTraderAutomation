namespace DerivCTrader.Domain.Entities;

public class TradeIndicator
{
    public int IndicatorId { get; set; }
    public int? TradeId { get; set; }
    public string? TradeType { get; set; }
    public string? StrategyName { get; set; }
    public string? StrategyVersion { get; set; }
    public string? Timeframe { get; set; }
    public string? IndicatorsJSON { get; set; }
    public DateTime RecordedAt { get; set; }
    public bool? UsedForTraining { get; set; }
    public string? Notes { get; set; }
}
