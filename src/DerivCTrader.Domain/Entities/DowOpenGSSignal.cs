namespace DerivCTrader.Domain.Entities;

/// <summary>
/// Signal log for DowOpenGS strategy.
/// Records every signal evaluation for audit, debugging, and backtesting.
/// </summary>
public class DowOpenGSSignal
{
    /// <summary>Database primary key</summary>
    public int SignalId { get; set; }

    /// <summary>Trade date (UTC)</summary>
    public DateTime TradeDate { get; set; }

    // ===== GOLDMAN SACHS DATA =====

    /// <summary>GS previous close price (21:00 UTC)</summary>
    public decimal GS_PreviousClose { get; set; }

    /// <summary>GS latest price at snapshot time (14:29:50 UTC)</summary>
    public decimal GS_LatestPrice { get; set; }

    /// <summary>GS direction: "BUY", "SELL", or "NEUTRAL"</summary>
    public string GS_Direction { get; set; } = string.Empty;

    /// <summary>GS price change (Latest - PreviousClose)</summary>
    public decimal GS_Change { get; set; }

    // ===== DOW FUTURES DATA =====

    /// <summary>YM=F previous close price (21:00 UTC)</summary>
    public decimal YM_PreviousClose { get; set; }

    /// <summary>YM=F latest price at snapshot time (14:29:50 UTC)</summary>
    public decimal YM_LatestPrice { get; set; }

    /// <summary>YM direction: "BUY", "SELL", or "NEUTRAL"</summary>
    public string YM_Direction { get; set; } = string.Empty;

    /// <summary>YM price change (Latest - PreviousClose)</summary>
    public decimal YM_Change { get; set; }

    // ===== SIGNAL RESULT =====

    /// <summary>Final signal: "BUY", "SELL", or "NO_TRADE"</summary>
    public string FinalSignal { get; set; } = string.Empty;

    /// <summary>Binary expiry used (15 or 30 minutes)</summary>
    public int BinaryExpiry { get; set; }

    /// <summary>Reason for NO_TRADE if applicable</summary>
    public string? NoTradeReason { get; set; }

    // ===== EXECUTION STATUS =====

    /// <summary>Was this a dry run?</summary>
    public bool WasDryRun { get; set; }

    /// <summary>Was CFD trade executed?</summary>
    public bool CFDExecuted { get; set; }

    /// <summary>Was Binary trade executed?</summary>
    public bool BinaryExecuted { get; set; }

    /// <summary>CFD order ID (Deriv or MT5)</summary>
    public string? CFDOrderId { get; set; }

    /// <summary>Binary contract ID (Deriv)</summary>
    public string? BinaryContractId { get; set; }

    /// <summary>CFD entry price (if executed)</summary>
    public decimal? CFDEntryPrice { get; set; }

    /// <summary>CFD calculated stop loss price</summary>
    public decimal? CFDStopLoss { get; set; }

    /// <summary>CFD calculated take profit price</summary>
    public decimal? CFDTakeProfit { get; set; }

    // ===== TIMESTAMPS =====

    /// <summary>When snapshot was taken</summary>
    public DateTime SnapshotAt { get; set; }

    /// <summary>When trades were executed</summary>
    public DateTime? ExecutedAt { get; set; }

    /// <summary>Record creation timestamp</summary>
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

    // ===== ERROR TRACKING =====

    /// <summary>Any error message during execution</summary>
    public string? ErrorMessage { get; set; }
}
