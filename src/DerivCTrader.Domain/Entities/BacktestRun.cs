namespace DerivCTrader.Domain.Entities;

/// <summary>
/// Tracks a backtest execution run with summary statistics.
/// </summary>
public class BacktestRun
{
    public int RunId { get; set; }

    /// <summary>Strategy being tested (e.g., "DowOpenGS")</summary>
    public string StrategyName { get; set; } = string.Empty;

    /// <summary>Backtest start date</summary>
    public DateTime StartDate { get; set; }

    /// <summary>Backtest end date</summary>
    public DateTime EndDate { get; set; }

    // ===== SIGNAL SUMMARY =====

    /// <summary>Total trading days in range</summary>
    public int TotalTradingDays { get; set; }

    /// <summary>Total signals evaluated</summary>
    public int TotalSignals { get; set; }

    /// <summary>Number of BUY signals</summary>
    public int BuySignals { get; set; }

    /// <summary>Number of SELL signals</summary>
    public int SellSignals { get; set; }

    /// <summary>Number of NO_TRADE signals</summary>
    public int NoTradeSignals { get; set; }

    // ===== BINARY RESULTS =====

    /// <summary>Total binary trades executed</summary>
    public int BinaryTrades { get; set; }

    /// <summary>Binary wins</summary>
    public int BinaryWins { get; set; }

    /// <summary>Binary losses</summary>
    public int BinaryLosses { get; set; }

    /// <summary>Binary win rate (0-100%)</summary>
    public decimal? BinaryWinRate { get; set; }

    /// <summary>Total binary P&L</summary>
    public decimal BinaryTotalPnL { get; set; }

    // ===== CFD RESULTS =====

    /// <summary>Total CFD trades executed</summary>
    public int CFDTrades { get; set; }

    /// <summary>CFD wins</summary>
    public int CFDWins { get; set; }

    /// <summary>CFD losses</summary>
    public int CFDLosses { get; set; }

    /// <summary>CFD win rate (0-100%)</summary>
    public decimal? CFDWinRate { get; set; }

    /// <summary>Total CFD P&L</summary>
    public decimal CFDTotalPnL { get; set; }

    // ===== RISK METRICS =====

    /// <summary>Maximum drawdown during backtest</summary>
    public decimal? MaxDrawdown { get; set; }

    /// <summary>Longest losing streak</summary>
    public int? MaxConsecutiveLosses { get; set; }

    /// <summary>Sharpe ratio (if calculated)</summary>
    public decimal? SharpeRatio { get; set; }

    // ===== PARAMETERS USED =====

    /// <summary>Binary stake per trade</summary>
    public decimal BinaryStakeUSD { get; set; }

    /// <summary>CFD lot size</summary>
    public decimal CFDVolume { get; set; }

    /// <summary>CFD stop loss percentage</summary>
    public decimal CFDStopLossPercent { get; set; }

    /// <summary>CFD take profit percentage</summary>
    public decimal CFDTakeProfitPercent { get; set; }

    // ===== TIMESTAMPS =====

    /// <summary>When backtest was started</summary>
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

    /// <summary>When backtest completed</summary>
    public DateTime? CompletedAt { get; set; }

    /// <summary>Optional notes</summary>
    public string? Notes { get; set; }

    // ===== COMPUTED PROPERTIES =====

    /// <summary>Check if backtest is complete</summary>
    public bool IsComplete => CompletedAt.HasValue;

    /// <summary>Total P&L (Binary + CFD)</summary>
    public decimal TotalPnL => BinaryTotalPnL + CFDTotalPnL;
}
