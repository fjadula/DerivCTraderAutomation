namespace DerivCTrader.Infrastructure.MarketData;

/// <summary>
/// Service for fetching market data from Polygon.io (Massive API wrapper).
/// Used for both live trading and backtesting historical data.
/// </summary>
public interface IMassiveApiService
{
    /// <summary>
    /// Fetch 1-minute candles for a ticker within a time range.
    /// </summary>
    /// <param name="ticker">Ticker symbol (e.g., "DJI", "GS")</param>
    /// <param name="fromUtc">Start time (UTC)</param>
    /// <param name="toUtc">End time (UTC)</param>
    /// <returns>List of candles</returns>
    Task<List<MassiveCandle>> GetMinuteCandlesAsync(string ticker, DateTime fromUtc, DateTime toUtc);

    /// <summary>
    /// Search for available tickers.
    /// </summary>
    /// <param name="market">Market type (e.g., "indices", "stocks")</param>
    /// <param name="search">Search term</param>
    /// <returns>List of matching tickers</returns>
    Task<List<MassiveTicker>> SearchTickersAsync(string market, string search);

    /// <summary>
    /// Get the latest quotes for multiple tickers.
    /// Uses Polygon.io's previous close endpoint for real-time data.
    /// </summary>
    /// <param name="tickers">Ticker symbols (e.g., "GS", "DIA")</param>
    /// <returns>Dictionary of ticker to quote data</returns>
    Task<Dictionary<string, MassiveQuote>> GetLatestQuotesAsync(params string[] tickers);

    /// <summary>
    /// Get the previous day's close for a ticker.
    /// </summary>
    /// <param name="ticker">Ticker symbol</param>
    /// <returns>Previous close data</returns>
    Task<MassivePreviousClose?> GetPreviousCloseAsync(string ticker);
}

/// <summary>
/// Candle data from Massive API.
/// </summary>
public class MassiveCandle
{
    public DateTime TimeUtc { get; set; }
    public decimal Open { get; set; }
    public decimal High { get; set; }
    public decimal Low { get; set; }
    public decimal Close { get; set; }
    public long? Volume { get; set; }
}

/// <summary>
/// Ticker information from Massive API.
/// </summary>
public class MassiveTicker
{
    public string Ticker { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public string Market { get; set; } = string.Empty;
    public bool Active { get; set; }
}

/// <summary>
/// Latest quote data from Polygon.io.
/// </summary>
public class MassiveQuote
{
    public string Ticker { get; set; } = string.Empty;
    public decimal LatestPrice { get; set; }
    public decimal PreviousClose { get; set; }
    public decimal Change { get; set; }
    public decimal ChangePercent { get; set; }
    public DateTime Timestamp { get; set; }
    public bool Success { get; set; }
    public string? ErrorMessage { get; set; }
}

/// <summary>
/// Previous close data from Polygon.io.
/// </summary>
public class MassivePreviousClose
{
    public string Ticker { get; set; } = string.Empty;
    public decimal Close { get; set; }
    public decimal Open { get; set; }
    public decimal High { get; set; }
    public decimal Low { get; set; }
    public long Volume { get; set; }
    public DateTime Date { get; set; }
}
