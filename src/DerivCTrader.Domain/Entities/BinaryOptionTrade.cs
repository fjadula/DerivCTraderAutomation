namespace DerivCTrader.Domain.Entities;

public class BinaryOptionTrade
{
    public int TradeId { get; set; }
    public string? AssetName { get; set; }
    public string? Direction { get; set; }
    public DateTime? OpenTime { get; set; }
    public DateTime? CloseTime { get; set; }
    public int? ExpiryLength { get; set; }
    public string? Result { get; set; }
    public bool? ClosedBeforeExpiry { get; set; }
    public bool? SentToTelegramPublic { get; set; }
    public bool? SentToTelegramPrivate { get; set; }
    public bool? SentToWhatsApp { get; set; }
    public DateTime CreatedAt { get; set; }
    public string? ExpiryDisplay { get; set; }
    public decimal? TradeStake { get; set; }
    public DateTime? ExpectedExpiryTimestamp { get; set; }
    public decimal? EntryPrice { get; set; }
    public decimal? ExitPrice { get; set; }
    public bool? IsTrendingMarket { get; set; }
    public string? TrendDirection { get; set; }
    public bool? RetracedToEMA14 { get; set; }
    public string? EMACrossSignal { get; set; }
    public string? StrategyName { get; set; }
    public string? StrategyVersion { get; set; }
    public string? FeatureSetVersion { get; set; }
    public string? PredictedOutcome { get; set; }
    public decimal? PredictionConfidence { get; set; }
    public DateTime? SignalGeneratedAt { get; set; }
    public DateTime? SignalValidUntil { get; set; }
    public bool? UsedForTraining { get; set; }
    public bool? IsEMARainbowConforming { get; set; }
    public bool? IsRSIConforming { get; set; }
    public bool? SentOpenToTelegram { get; set; }
    public bool? SentCloseToTelegram { get; set; }
    public bool? IndicatorsLinked { get; set; }
}
