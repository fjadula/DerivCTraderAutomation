using DerivCTrader.Application.Interfaces;
using DerivCTrader.Domain.Entities;
using DerivCTrader.Infrastructure.MarketData;
using Microsoft.Extensions.Logging;

namespace DerivCTrader.TradeExecutor.Services;

/// <summary>
/// Fetches historical market data from Massive API for backtesting.
/// Optimized to only fetch the 14:30-15:00 UTC window needed for DowOpenGS strategy.
/// </summary>
public class MassiveDataFetcher
{
    private readonly ILogger<MassiveDataFetcher> _logger;
    private readonly IMassiveApiService _massiveApi;
    private readonly IBacktestRepository _backtestRepo;

    // Default ticker mapping (can be overridden)
    // GS = Goldman Sachs stock (direct stock ticker)
    // DIA = SPDR Dow Jones Industrial Average ETF (tracks Dow Jones, available on stocks tier)
    // Note: I:DJI requires indices subscription
    private const string DEFAULT_GS_TICKER = "GS";
    private const string DEFAULT_DOW_TICKER = "DIA";  // ETF that tracks DJIA

    // Time window for DowOpenGS strategy (UTC)
    // Trading window: 14:29-15:30 UTC for signal evaluation and up to 60-min expiry
    private const int TradingStartHour = 14;
    private const int TradingStartMinute = 29;  // Start at 14:29 to ensure we have 14:29:50
    private const int TradingEndHour = 15;
    private const int TradingEndMinute = 31;    // End at 15:31 to ensure we have 60-min expiry data

    // Market close window: 20:59-21:01 UTC for official "previous close"
    private const int MarketCloseStartHour = 20;
    private const int MarketCloseStartMinute = 59;
    private const int MarketCloseEndHour = 21;
    private const int MarketCloseEndMinute = 1;

    public MassiveDataFetcher(
        ILogger<MassiveDataFetcher> logger,
        IMassiveApiService massiveApi,
        IBacktestRepository backtestRepo)
    {
        _logger = logger;
        _massiveApi = massiveApi;
        _backtestRepo = backtestRepo;
    }

    /// <summary>
    /// Search for available tickers to verify correct symbols.
    /// </summary>
    public async Task SearchTickersAsync(string market, string search)
    {
        Console.WriteLine($"\nSearching tickers: market={market}, search={search}");
        var tickers = await _massiveApi.SearchTickersAsync(market, search);

        Console.WriteLine($"\nFound {tickers.Count} tickers:");
        foreach (var t in tickers)
        {
            Console.WriteLine($"  {t.Ticker,-10} {t.Name,-40} [{t.Market}] Active={t.Active}");
        }
    }

    /// <summary>
    /// Comprehensive ticker search for DowOpenGS strategy symbols.
    /// </summary>
    public async Task SearchAllTickersAsync()
    {
        Console.WriteLine(new string('=', 60));
        Console.WriteLine("  COMPREHENSIVE TICKER SEARCH");
        Console.WriteLine(new string('=', 60));

        // Search for GS stock with various terms
        Console.WriteLine("\n--- GOLDMAN SACHS STOCK ---");
        await SearchTickersAsync("stocks", "GS");
        await SearchTickersAsync("stocks", "goldman sachs");

        // Search for Dow Jones related indices
        Console.WriteLine("\n--- DOW JONES INDICES ---");
        await SearchTickersAsync("indices", "DJI");
        await SearchTickersAsync("indices", "DJIA");
        await SearchTickersAsync("indices", "dow jones");
        await SearchTickersAsync("indices", "US30");

        // Search for Dow futures
        Console.WriteLine("\n--- DOW JONES FUTURES ---");
        await SearchTickersAsync("indices", "YM");
        await SearchTickersAsync("fx", "YM");  // Some APIs list futures under fx

        Console.WriteLine(new string('=', 60));
        Console.WriteLine("  Use --gs-ticker and --dow-ticker to specify correct symbols");
        Console.WriteLine(new string('=', 60));
    }

    /// <summary>
    /// Fetch data for all required symbols for a date range.
    /// Only fetches the 14:29-15:01 UTC window needed for backtesting.
    /// </summary>
    public async Task<int> FetchDataForBacktestAsync(
        DateTime startDate,
        DateTime endDate,
        string? gsTicker = null,
        string? dowTicker = null)
    {
        gsTicker ??= DEFAULT_GS_TICKER;
        dowTicker ??= DEFAULT_DOW_TICKER;

        Console.WriteLine($"\n{new string('=', 60)}");
        Console.WriteLine("  MASSIVE API DATA FETCHER");
        Console.WriteLine(new string('=', 60));
        Console.WriteLine($"  Period: {startDate:yyyy-MM-dd} to {endDate:yyyy-MM-dd}");
        Console.WriteLine($"  GS ticker: {gsTicker}");
        Console.WriteLine($"  DOW ticker: {dowTicker} -> WS30");
        Console.WriteLine();
        Console.WriteLine("  Time windows fetched (UTC):");
        Console.WriteLine($"    Trading:     {TradingStartHour:00}:{TradingStartMinute:00} - {TradingEndHour:00}:{TradingEndMinute:00}");
        Console.WriteLine($"    Market Close: {MarketCloseStartHour:00}:{MarketCloseStartMinute:00} - {MarketCloseEndHour:00}:{MarketCloseEndMinute:00}");
        Console.WriteLine(new string('=', 60));

        // Get trading days (excludes weekends and US holidays)
        var tradingDays = await _backtestRepo.GetTradingDaysAsync(startDate, endDate);
        Console.WriteLine($"\n  Trading days to fetch: {tradingDays.Count}");
        Console.WriteLine();

        var totalCandles = 0;

        // Fetch GS data
        Console.WriteLine($"  Fetching {gsTicker} (Goldman Sachs)...");
        var gsCandles = await FetchSymbolDataAsync(gsTicker, "GS", tradingDays);
        totalCandles += gsCandles;
        Console.WriteLine($"    Imported: {gsCandles} candles");

        // Fetch WS30/Dow data
        Console.WriteLine($"  Fetching {dowTicker} (Wall Street 30)...");
        var ws30Candles = await FetchSymbolDataAsync(dowTicker, "WS30", tradingDays);
        totalCandles += ws30Candles;
        Console.WriteLine($"    Imported: {ws30Candles} candles");

        // Print summary
        Console.WriteLine($"\n{new string('=', 50)}");
        Console.WriteLine($"  TOTAL CANDLES IMPORTED: {totalCandles}");
        Console.WriteLine(new string('=', 50));

        // Show coverage
        await PrintDataCoverageAsync();

        return totalCandles;
    }

    /// <summary>
    /// Fetch data for a single symbol across multiple trading days.
    /// Fetches both trading window (14:29-15:31 UTC) and market close window (20:59-21:01 UTC).
    /// </summary>
    private async Task<int> FetchSymbolDataAsync(string apiTicker, string internalSymbol, List<DateTime> tradingDays)
    {
        var allCandles = new List<MarketPriceCandle>();
        var processed = 0;
        var failedDays = 0;

        foreach (var day in tradingDays)
        {
            processed++;

            // WINDOW 1: Trading window 14:29 to 15:31 UTC (for signal + 60-min expiry)
            var tradingStartTime = day.Date.AddHours(TradingStartHour).AddMinutes(TradingStartMinute);
            var tradingEndTime = day.Date.AddHours(TradingEndHour).AddMinutes(TradingEndMinute);

            var tradingCandles = await _massiveApi.GetMinuteCandlesAsync(apiTicker, tradingStartTime, tradingEndTime);

            if (tradingCandles.Count == 0)
            {
                failedDays++;
                _logger.LogWarning("No trading window data for {Ticker} on {Date}", apiTicker, day);
            }
            else
            {
                foreach (var candle in tradingCandles)
                {
                    allCandles.Add(new MarketPriceCandle
                    {
                        Symbol = internalSymbol,
                        TimeUtc = candle.TimeUtc,
                        Open = candle.Open,
                        High = candle.High,
                        Low = candle.Low,
                        Close = candle.Close,
                        Volume = candle.Volume,
                        DataSource = "MassiveAPI"
                    });
                }
            }

            // WINDOW 2: Market close window 20:59 to 21:01 UTC (for previous close)
            var closeStartTime = day.Date.AddHours(MarketCloseStartHour).AddMinutes(MarketCloseStartMinute);
            var closeEndTime = day.Date.AddHours(MarketCloseEndHour).AddMinutes(MarketCloseEndMinute);

            var closeCandles = await _massiveApi.GetMinuteCandlesAsync(apiTicker, closeStartTime, closeEndTime);

            if (closeCandles.Count == 0)
            {
                _logger.LogWarning("No market close data for {Ticker} on {Date}", apiTicker, day);
            }
            else
            {
                foreach (var candle in closeCandles)
                {
                    allCandles.Add(new MarketPriceCandle
                    {
                        Symbol = internalSymbol,
                        TimeUtc = candle.TimeUtc,
                        Open = candle.Open,
                        High = candle.High,
                        Low = candle.Low,
                        Close = candle.Close,
                        Volume = candle.Volume,
                        DataSource = "MassiveAPI"
                    });
                }
            }

            // Progress indicator
            if (processed % 5 == 0)
            {
                Console.WriteLine($"    Progress: {processed}/{tradingDays.Count} days...");
            }
        }

        if (failedDays > 0)
        {
            Console.WriteLine($"    Warning: {failedDays} days had no trading window data");
        }

        // Bulk insert
        if (allCandles.Count > 0)
        {
            try
            {
                await _backtestRepo.BulkInsertCandlesAsync(allCandles);
                _logger.LogInformation("Inserted {Count} candles for {Symbol}", allCandles.Count, internalSymbol);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Bulk insert failed for {Symbol}, trying individual inserts", internalSymbol);
                // Try individual inserts if bulk fails (handles duplicates)
                var inserted = 0;
                foreach (var candle in allCandles)
                {
                    try
                    {
                        await _backtestRepo.BulkInsertCandlesAsync(new[] { candle });
                        inserted++;
                    }
                    catch
                    {
                        // Skip duplicates
                    }
                }
                return inserted;
            }
        }

        return allCandles.Count;
    }

    /// <summary>
    /// Fetch data for a single day (useful for testing).
    /// Fetches both trading window and market close window.
    /// </summary>
    public async Task<int> FetchSingleDayAsync(
        DateTime tradeDate,
        string? gsTicker = null,
        string? dowTicker = null)
    {
        gsTicker ??= DEFAULT_GS_TICKER;
        dowTicker ??= DEFAULT_DOW_TICKER;

        Console.WriteLine($"\nFetching data for single day: {tradeDate:yyyy-MM-dd}");

        // Trading window
        var tradingStart = tradeDate.Date.AddHours(TradingStartHour).AddMinutes(TradingStartMinute);
        var tradingEnd = tradeDate.Date.AddHours(TradingEndHour).AddMinutes(TradingEndMinute);

        // Market close window
        var closeStart = tradeDate.Date.AddHours(MarketCloseStartHour).AddMinutes(MarketCloseStartMinute);
        var closeEnd = tradeDate.Date.AddHours(MarketCloseEndHour).AddMinutes(MarketCloseEndMinute);

        var totalCandles = 0;

        // GS - Trading window
        Console.WriteLine($"\n  {gsTicker} Trading Window: {tradingStart:HH:mm} - {tradingEnd:HH:mm} UTC");
        var gsTradingCandles = await _massiveApi.GetMinuteCandlesAsync(gsTicker, tradingStart, tradingEnd);
        Console.WriteLine($"    Got {gsTradingCandles.Count} candles");
        totalCandles += await SaveCandlesAsync(gsTradingCandles, "GS");

        // GS - Market close window
        Console.WriteLine($"  {gsTicker} Market Close: {closeStart:HH:mm} - {closeEnd:HH:mm} UTC");
        var gsCloseCandles = await _massiveApi.GetMinuteCandlesAsync(gsTicker, closeStart, closeEnd);
        Console.WriteLine($"    Got {gsCloseCandles.Count} candles");
        totalCandles += await SaveCandlesAsync(gsCloseCandles, "GS");

        // WS30/Dow - Trading window
        Console.WriteLine($"\n  {dowTicker} Trading Window: {tradingStart:HH:mm} - {tradingEnd:HH:mm} UTC");
        var dowTradingCandles = await _massiveApi.GetMinuteCandlesAsync(dowTicker, tradingStart, tradingEnd);
        Console.WriteLine($"    Got {dowTradingCandles.Count} candles");
        totalCandles += await SaveCandlesAsync(dowTradingCandles, "WS30");

        // WS30/Dow - Market close window
        Console.WriteLine($"  {dowTicker} Market Close: {closeStart:HH:mm} - {closeEnd:HH:mm} UTC");
        var dowCloseCandles = await _massiveApi.GetMinuteCandlesAsync(dowTicker, closeStart, closeEnd);
        Console.WriteLine($"    Got {dowCloseCandles.Count} candles");
        totalCandles += await SaveCandlesAsync(dowCloseCandles, "WS30");

        Console.WriteLine($"\n  Total saved: {totalCandles} candles");
        return totalCandles;
    }

    private async Task<int> SaveCandlesAsync(List<MassiveCandle> candles, string symbol)
    {
        var saved = 0;
        foreach (var candle in candles)
        {
            try
            {
                await _backtestRepo.BulkInsertCandlesAsync(new[]
                {
                    new MarketPriceCandle
                    {
                        Symbol = symbol,
                        TimeUtc = candle.TimeUtc,
                        Open = candle.Open,
                        High = candle.High,
                        Low = candle.Low,
                        Close = candle.Close,
                        Volume = candle.Volume,
                        DataSource = "MassiveAPI"
                    }
                });
                saved++;
            }
            catch { /* Skip duplicates */ }
        }
        return saved;
    }

    private async Task PrintDataCoverageAsync()
    {
        Console.WriteLine("\n  --- DATA COVERAGE ---");

        var symbols = new[] { "GS", "WS30" };
        foreach (var symbol in symbols)
        {
            var coverage = await _backtestRepo.GetDataCoverageAsync(symbol);
            if (coverage.Count > 0)
            {
                Console.WriteLine($"  {symbol}: {coverage.Earliest:yyyy-MM-dd} to {coverage.Latest:yyyy-MM-dd} ({coverage.Count:N0} candles)");
            }
            else
            {
                Console.WriteLine($"  {symbol}: NO DATA");
            }
        }

        Console.WriteLine();
    }
}
