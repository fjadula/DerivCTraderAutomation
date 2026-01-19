namespace DerivCTrader.Domain.Entities;

/// <summary>
/// Individual trade result from a backtest.
/// Contains signal data and simulated trade outcomes.
/// </summary>
public class BacktestTrade
{
    public long TradeId { get; set; }

    /// <summary>FK to BacktestRuns</summary>
    public int RunId { get; set; }

    /// <summary>Trade date</summary>
    public DateTime TradeDate { get; set; }

    // ===== SIGNAL DATA (same as DowOpenGSSignal) =====

    public decimal GS_PreviousClose { get; set; }
    public decimal GS_LatestPrice { get; set; }
    public string GS_Direction { get; set; } = string.Empty;
    public decimal GS_Change { get; set; }

    public decimal YM_PreviousClose { get; set; }
    public decimal YM_LatestPrice { get; set; }
    public string YM_Direction { get; set; } = string.Empty;
    public decimal YM_Change { get; set; }

    /// <summary>Final signal: BUY, SELL, NO_TRADE</summary>
    public string FinalSignal { get; set; } = string.Empty;

    /// <summary>Reason for NO_TRADE if applicable</summary>
    public string? NoTradeReason { get; set; }

    // ===== BINARY TRADE RESULT =====

    /// <summary>Binary expiry used (15 or 30 minutes)</summary>
    public int? BinaryExpiry { get; set; }

    /// <summary>WS30 price at entry (14:30 UTC)</summary>
    public decimal? BinaryEntryPrice { get; set; }

    /// <summary>WS30 price at expiry</summary>
    public decimal? BinaryExitPrice { get; set; }

    /// <summary>WIN, LOSS, or NULL (no trade)</summary>
    public string? BinaryResult { get; set; }

    /// <summary>Binary P&L: +stake*(payout-1) for win, -stake for loss</summary>
    public decimal? BinaryPnL { get; set; }

    // ===== CFD TRADE RESULT =====

    /// <summary>CFD entry price</summary>
    public decimal? CFDEntryPrice { get; set; }

    /// <summary>CFD exit price</summary>
    public decimal? CFDExitPrice { get; set; }

    /// <summary>Calculated stop loss price</summary>
    public decimal? CFDStopLoss { get; set; }

    /// <summary>Calculated take profit price</summary>
    public decimal? CFDTakeProfit { get; set; }

    /// <summary>TP_HIT, SL_HIT, TIME_EXIT</summary>
    public string? CFDExitReason { get; set; }

    /// <summary>WIN, LOSS, or NULL (no trade)</summary>
    public string? CFDResult { get; set; }

    /// <summary>CFD P&L based on lot size and price movement</summary>
    public decimal? CFDPnL { get; set; }

    /// <summary>When CFD was exited</summary>
    public DateTime? CFDExitTimeUtc { get; set; }

    // ===== TIMESTAMPS =====

    /// <summary>Snapshot time (14:29:50 UTC)</summary>
    public DateTime SnapshotTimeUtc { get; set; }

    /// <summary>Entry time (14:30:00 UTC)</summary>
    public DateTime EntryTimeUtc { get; set; }

    // ===== COMPUTED PROPERTIES =====

    /// <summary>Total P&L for this trade</summary>
    public decimal TotalPnL => (BinaryPnL ?? 0) + (CFDPnL ?? 0);

    /// <summary>Was this a tradeable signal?</summary>
    public bool IsTradeable => FinalSignal == "BUY" || FinalSignal == "SELL";
}
