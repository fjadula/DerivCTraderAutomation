namespace DerivCTrader.Infrastructure.MarketData;

/// <summary>
/// Service for fetching market data from FinanceFlowAPI.
/// Used for backtesting historical price data.
/// </summary>
public interface IFinanceFlowService
{
    /// <summary>
    /// Fetch 1-minute candles for a symbol within a time range.
    /// </summary>
    /// <param name="symbol">Symbol (e.g., "DJIA", "US30", "GS")</param>
    /// <param name="startUtc">Start time (UTC)</param>
    /// <param name="endUtc">End time (UTC)</param>
    /// <returns>List of candles</returns>
    Task<List<FinanceFlowCandle>> GetCandlesAsync(string symbol, DateTime startUtc, DateTime endUtc);

    /// <summary>
    /// Fetch current spot price for a symbol.
    /// </summary>
    Task<decimal?> GetSpotPriceAsync(string symbol);

    /// <summary>
    /// Get available symbols from the catalog.
    /// </summary>
    Task<List<string>> GetAvailableSymbolsAsync();
}

/// <summary>
/// Candle data from FinanceFlowAPI.
/// </summary>
public class FinanceFlowCandle
{
    public DateTime TimeUtc { get; set; }
    public decimal Open { get; set; }
    public decimal High { get; set; }
    public decimal Low { get; set; }
    public decimal Close { get; set; }
    public long? Volume { get; set; }
}
