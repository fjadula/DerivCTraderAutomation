using DerivCTrader.Application.Interfaces;
using DerivCTrader.Domain.Entities;
using DerivCTrader.Infrastructure.MarketData;
using Microsoft.Extensions.Logging;

namespace DerivCTrader.TradeExecutor.Services;

/// <summary>
/// Fetches historical market data from FinanceFlowAPI for backtesting.
/// Optimized to only fetch the 14:30-15:00 UTC window needed for DowOpenGS strategy.
/// </summary>
public class FinanceFlowDataFetcher
{
    private readonly ILogger<FinanceFlowDataFetcher> _logger;
    private readonly IFinanceFlowService _financeFlow;
    private readonly IBacktestRepository _backtestRepo;

    // Symbol mapping: FinanceFlowAPI symbol -> Our internal symbol
    private static readonly Dictionary<string, string> SymbolMap = new()
    {
        { "GS", "GS" },           // Goldman Sachs - direct
        { "DJIA", "WS30" },       // Dow Jones Industrial Average -> Wall Street 30
        { "US30", "WS30" },       // Alternative Dow symbol
        { "^DJI", "WS30" },       // Yahoo-style symbol
    };

    // Time window for DowOpenGS strategy (UTC)
    private const int StrategyStartHour = 14;
    private const int StrategyStartMinute = 29;  // Start slightly before to ensure we have 14:29:50
    private const int StrategyEndHour = 15;
    private const int StrategyEndMinute = 1;     // End slightly after 15:00

    public FinanceFlowDataFetcher(
        ILogger<FinanceFlowDataFetcher> logger,
        IFinanceFlowService financeFlow,
        IBacktestRepository backtestRepo)
    {
        _logger = logger;
        _financeFlow = financeFlow;
        _backtestRepo = backtestRepo;
    }

    /// <summary>
    /// Fetch data for all required symbols for a date range.
    /// Only fetches the 14:29-15:01 UTC window needed for backtesting.
    /// </summary>
    public async Task<int> FetchDataForBacktestAsync(DateTime startDate, DateTime endDate, string? wsSymbol = null)
    {
        Console.WriteLine($"\n{new string('=', 50)}");
        Console.WriteLine("  FINANCEFLOW DATA FETCHER");
        Console.WriteLine(new string('=', 50));
        Console.WriteLine($"  Period: {startDate:yyyy-MM-dd} to {endDate:yyyy-MM-dd}");
        Console.WriteLine(new string('=', 50));

        // Get trading days (excludes weekends and US holidays)
        var tradingDays = await _backtestRepo.GetTradingDaysAsync(startDate, endDate);
        Console.WriteLine($"\n  Trading days to fetch: {tradingDays.Count}");

        // Determine which WS30 symbol to use
        var ws30Symbol = wsSymbol ?? "DJIA";
        Console.WriteLine($"  WS30 symbol: {ws30Symbol}");
        Console.WriteLine();

        var totalCandles = 0;

        // Fetch GS data
        Console.WriteLine("  Fetching GS (Goldman Sachs)...");
        var gsCandles = await FetchSymbolDataAsync("GS", "GS", tradingDays);
        totalCandles += gsCandles;
        Console.WriteLine($"    Imported: {gsCandles} candles");

        // Fetch WS30/Dow data
        Console.WriteLine($"  Fetching {ws30Symbol} (Wall Street 30)...");
        var ws30Candles = await FetchSymbolDataAsync(ws30Symbol, "WS30", tradingDays);
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
    /// </summary>
    private async Task<int> FetchSymbolDataAsync(string apiSymbol, string internalSymbol, List<DateTime> tradingDays)
    {
        var allCandles = new List<MarketPriceCandle>();
        var processed = 0;

        foreach (var day in tradingDays)
        {
            processed++;

            // Define the time window: 14:29 to 15:01 UTC
            var startTime = day.Date.AddHours(StrategyStartHour).AddMinutes(StrategyStartMinute);
            var endTime = day.Date.AddHours(StrategyEndHour).AddMinutes(StrategyEndMinute);

            // Fetch candles from API
            var candles = await _financeFlow.GetCandlesAsync(apiSymbol, startTime, endTime);

            if (candles.Count == 0)
            {
                _logger.LogWarning("No data returned for {Symbol} on {Date}", apiSymbol, day);
                continue;
            }

            // Convert to our entity
            foreach (var candle in candles)
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
                    DataSource = "FinanceFlowAPI"
                });
            }

            // Progress indicator
            if (processed % 10 == 0)
            {
                Console.WriteLine($"    Progress: {processed}/{tradingDays.Count} days...");
            }
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
                _logger.LogError(ex, "Failed to insert candles for {Symbol}", internalSymbol);
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
    /// Fetch only the entry/exit candles needed for a specific day (minimal API usage).
    /// This is the most efficient mode - only 2 requests per day.
    /// </summary>
    public async Task<int> FetchMinimalDataAsync(DateTime startDate, DateTime endDate, string? wsSymbol = null)
    {
        Console.WriteLine($"\n{new string('=', 50)}");
        Console.WriteLine("  MINIMAL DATA FETCH MODE");
        Console.WriteLine(new string('=', 50));
        Console.WriteLine("  Only fetching entry (14:30) and exit (15:00) candles");
        Console.WriteLine($"  Period: {startDate:yyyy-MM-dd} to {endDate:yyyy-MM-dd}");

        var tradingDays = await _backtestRepo.GetTradingDaysAsync(startDate, endDate);
        Console.WriteLine($"  Trading days: {tradingDays.Count}");

        var ws30Symbol = wsSymbol ?? "DJIA";
        var totalCandles = 0;

        // For minimal mode, we fetch just the key timestamps:
        // 14:29, 14:30, 14:45, 15:00
        var keyMinutes = new[] { 29, 30, 45, 60 }; // 60 = 15:00

        foreach (var day in tradingDays)
        {
            // Fetch full window once per day (more efficient than multiple small requests)
            var startTime = day.Date.AddHours(14).AddMinutes(29);
            var endTime = day.Date.AddHours(15).AddMinutes(1);

            // GS
            var gsCandles = await _financeFlow.GetCandlesAsync("GS", startTime, endTime);
            foreach (var c in gsCandles)
            {
                await SaveCandleAsync("GS", c);
                totalCandles++;
            }

            // WS30
            var wsCandles = await _financeFlow.GetCandlesAsync(ws30Symbol, startTime, endTime);
            foreach (var c in wsCandles)
            {
                await SaveCandleAsync("WS30", c);
                totalCandles++;
            }

            Console.WriteLine($"  {day:yyyy-MM-dd}: GS={gsCandles.Count}, WS30={wsCandles.Count} candles");
        }

        Console.WriteLine($"\n  Total candles: {totalCandles}");
        return totalCandles;
    }

    private async Task SaveCandleAsync(string symbol, FinanceFlowCandle candle)
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
                    DataSource = "FinanceFlowAPI"
                }
            });
        }
        catch
        {
            // Ignore duplicates
        }
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
