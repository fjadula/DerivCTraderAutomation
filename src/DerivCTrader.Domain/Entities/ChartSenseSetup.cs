using DerivCTrader.Domain.Enums;

namespace DerivCTrader.Domain.Entities;

/// <summary>
/// Stateful setup tracking for ChartSense image-based signals.
/// Only ONE active setup per asset is allowed at any time.
/// </summary>
public class ChartSenseSetup
{
    /// <summary>Database primary key</summary>
    public int SetupId { get; set; }

    /// <summary>Asset symbol (e.g., EURUSD, GBPUSD, XAUUSD)</summary>
    public string Asset { get; set; } = string.Empty;

    /// <summary>Trade direction derived from chart image</summary>
    public TradeDirection Direction { get; set; }

    /// <summary>Chart timeframe (H1, H2, H4, D1, M15, M30)</summary>
    public string Timeframe { get; set; } = string.Empty;

    /// <summary>Pattern name extracted from image (Trendline, Wedge, Rectangle, Breakout, etc.)</summary>
    public string PatternType { get; set; } = string.Empty;

    /// <summary>Pattern classification for expiry calculation</summary>
    public ChartSensePatternClassification PatternClassification { get; set; }

    /// <summary>Derived entry price from chart structure lines</summary>
    public decimal? EntryPrice { get; set; }

    /// <summary>Entry zone lower bound (entry - buffer)</summary>
    public decimal? EntryZoneMin { get; set; }

    /// <summary>Entry zone upper bound (entry + buffer)</summary>
    public decimal? EntryZoneMax { get; set; }

    /// <summary>cTrader pending order ID (when PendingPlaced)</summary>
    public long? CTraderOrderId { get; set; }

    /// <summary>cTrader position ID (when Filled)</summary>
    public long? CTraderPositionId { get; set; }

    /// <summary>Current setup status</summary>
    public ChartSenseStatus Status { get; set; } = ChartSenseStatus.Watching;

    /// <summary>Calculated timeout based on timeframe - order cancelled if not filled by this time</summary>
    public DateTime? TimeoutAt { get; set; }

    /// <summary>Reference to ParsedSignalsQueue row</summary>
    public int? SignalId { get; set; }

    /// <summary>Original Telegram message ID for notification threading</summary>
    public int? TelegramMessageId { get; set; }

    /// <summary>Deriv binary contract ID (after forex fill triggers binary)</summary>
    public string? DerivContractId { get; set; }

    /// <summary>Calculated expiry for Deriv binary based on timeframe and pattern</summary>
    public int? DerivExpiryMinutes { get; set; }

    /// <summary>When setup was created</summary>
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

    /// <summary>Last update timestamp</summary>
    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;

    /// <summary>When setup was closed/expired/invalidated</summary>
    public DateTime? ClosedAt { get; set; }

    /// <summary>SHA256 hash of image to detect duplicate signals</summary>
    public string? ImageHash { get; set; }

    /// <summary>JSON: Y-axis calibration parameters for price mapping</summary>
    public string? CalibrationData { get; set; }

    /// <summary>JSON: Detected support/resistance line coordinates</summary>
    public string? DetectedLines { get; set; }

    /// <summary>Provider channel ID</summary>
    public string ProviderChannelId { get; set; } = string.Empty;
}
