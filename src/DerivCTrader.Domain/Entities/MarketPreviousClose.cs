namespace DerivCTrader.Domain.Entities;

/// <summary>
/// Cached previous close prices for market instruments.
/// Stored at 21:00 UTC daily for next-day signal evaluation.
/// </summary>
public class MarketPreviousClose
{
    /// <summary>Database primary key</summary>
    public int Id { get; set; }

    /// <summary>Symbol (e.g., "GS", "YM=F")</summary>
    public string Symbol { get; set; } = string.Empty;

    /// <summary>Previous close price</summary>
    public decimal PreviousClose { get; set; }

    /// <summary>Date of the close (UTC)</summary>
    public DateTime CloseDate { get; set; }

    /// <summary>When this record was cached</summary>
    public DateTime CachedAt { get; set; } = DateTime.UtcNow;

    /// <summary>Source of the data (e.g., "YahooFinance")</summary>
    public string DataSource { get; set; } = "YahooFinance";
}
