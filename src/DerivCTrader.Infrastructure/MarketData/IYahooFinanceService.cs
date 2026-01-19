namespace DerivCTrader.Infrastructure.MarketData;

/// <summary>
/// Quote data from Yahoo Finance
/// </summary>
public class YahooQuote
{
    /// <summary>Symbol (e.g., "GS", "YM=F")</summary>
    public string Symbol { get; set; } = string.Empty;

    /// <summary>Previous session close price</summary>
    public decimal PreviousClose { get; set; }

    /// <summary>Current/latest price (premarket or regular)</summary>
    public decimal LatestPrice { get; set; }

    /// <summary>Pre-market price (if available)</summary>
    public decimal? PreMarketPrice { get; set; }

    /// <summary>Regular market price</summary>
    public decimal RegularMarketPrice { get; set; }

    /// <summary>Timestamp of the quote</summary>
    public DateTime QuoteTime { get; set; }

    /// <summary>Whether fetch was successful</summary>
    public bool Success { get; set; }

    /// <summary>Error message if fetch failed</summary>
    public string? ErrorMessage { get; set; }
}

/// <summary>
/// Interface for fetching market data from Yahoo Finance
/// </summary>
public interface IYahooFinanceService
{
    /// <summary>
    /// Fetch quote for a single symbol
    /// </summary>
    /// <param name="symbol">Yahoo Finance symbol (e.g., "GS", "YM=F")</param>
    /// <returns>Quote data</returns>
    Task<YahooQuote> GetQuoteAsync(string symbol);

    /// <summary>
    /// Fetch quotes for multiple symbols in a single call
    /// </summary>
    /// <param name="symbols">Yahoo Finance symbols</param>
    /// <returns>Dictionary of symbol to quote</returns>
    Task<Dictionary<string, YahooQuote>> GetQuotesAsync(params string[] symbols);
}
