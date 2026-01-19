using DerivCTrader.Domain.Entities;

namespace DerivCTrader.Application.Interfaces;

/// <summary>
/// Repository for backtest data operations.
/// </summary>
public interface IBacktestRepository
{
    // ===== MARKET PRICE HISTORY =====

    /// <summary>
    /// Get a single candle for a symbol at a specific time.
    /// </summary>
    Task<MarketPriceCandle?> GetCandleAsync(string symbol, DateTime timeUtc);

    /// <summary>
    /// Get candles for a symbol within a time range.
    /// </summary>
    Task<List<MarketPriceCandle>> GetCandlesAsync(string symbol, DateTime startUtc, DateTime endUtc);

    /// <summary>
    /// Get the close price at a specific time (or nearest available).
    /// </summary>
    Task<decimal?> GetPriceAtTimeAsync(string symbol, DateTime timeUtc);

    /// <summary>
    /// Get the previous close for a symbol on a given date (21:00 UTC of prior day).
    /// </summary>
    Task<decimal?> GetPreviousCloseForDateAsync(string symbol, DateTime tradeDate);

    /// <summary>
    /// Bulk insert candles from CSV import.
    /// </summary>
    Task BulkInsertCandlesAsync(IEnumerable<MarketPriceCandle> candles);

    /// <summary>
    /// Check if data exists for a symbol in a date range.
    /// </summary>
    Task<(DateTime? Earliest, DateTime? Latest, int Count)> GetDataCoverageAsync(string symbol);

    // ===== BACKTEST RUNS =====

    /// <summary>
    /// Create a new backtest run.
    /// </summary>
    Task<int> CreateBacktestRunAsync(BacktestRun run);

    /// <summary>
    /// Update backtest run with results.
    /// </summary>
    Task UpdateBacktestRunAsync(BacktestRun run);

    /// <summary>
    /// Get a backtest run by ID.
    /// </summary>
    Task<BacktestRun?> GetBacktestRunAsync(int runId);

    /// <summary>
    /// Get all backtest runs for a strategy.
    /// </summary>
    Task<List<BacktestRun>> GetBacktestRunsAsync(string strategyName);

    // ===== BACKTEST TRADES =====

    /// <summary>
    /// Save a backtest trade result.
    /// </summary>
    Task SaveBacktestTradeAsync(BacktestTrade trade);

    /// <summary>
    /// Bulk save backtest trades.
    /// </summary>
    Task BulkSaveBacktestTradesAsync(IEnumerable<BacktestTrade> trades);

    /// <summary>
    /// Get all trades for a backtest run.
    /// </summary>
    Task<List<BacktestTrade>> GetBacktestTradesAsync(int runId);

    // ===== UTILITIES =====

    /// <summary>
    /// Get all trading days in a date range (excludes weekends and holidays).
    /// </summary>
    Task<List<DateTime>> GetTradingDaysAsync(DateTime startDate, DateTime endDate);
}
