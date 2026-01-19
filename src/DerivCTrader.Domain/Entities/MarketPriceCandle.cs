namespace DerivCTrader.Domain.Entities;

/// <summary>
/// 1-minute OHLC candle for backtesting.
/// Stores historical price data from Yahoo Finance or other sources.
/// </summary>
public class MarketPriceCandle
{
    public long Id { get; set; }

    /// <summary>Symbol (GS, YM=F, WS30, etc.)</summary>
    public string Symbol { get; set; } = string.Empty;

    /// <summary>Candle timestamp (UTC)</summary>
    public DateTime TimeUtc { get; set; }

    /// <summary>Open price</summary>
    public decimal Open { get; set; }

    /// <summary>High price</summary>
    public decimal High { get; set; }

    /// <summary>Low price</summary>
    public decimal Low { get; set; }

    /// <summary>Close price</summary>
    public decimal Close { get; set; }

    /// <summary>Volume (optional)</summary>
    public long? Volume { get; set; }

    /// <summary>Data source (YahooFinance, CSV, etc.)</summary>
    public string DataSource { get; set; } = "YahooFinance";
}
