using DerivCTrader.Application.Interfaces;
using DerivCTrader.Domain.Entities;
using DerivCTrader.Infrastructure.MarketData;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace DerivCTrader.TradeExecutor.Services;

/// <summary>
/// Background service that caches previous close prices at 21:00 UTC daily.
///
/// This ensures that when DowOpenGS runs at 14:30 UTC the next day,
/// it has accurate and consistent previous close data without relying on
/// Yahoo Finance's historical API during execution.
///
/// Symbols cached:
/// - GS (Goldman Sachs)
/// - YM=F (Dow Jones Futures)
/// </summary>
public class PreviousCloseService : BackgroundService
{
    private const int CacheHour = 21;  // 21:00 UTC (after US market close at 20:00 UTC)
    private const int CacheMinute = 0;

    private static readonly string[] SymbolsToCache = { "GS", "YM=F" };

    private readonly ILogger<PreviousCloseService> _logger;
    private readonly IStrategyRepository _strategyRepo;
    private readonly IYahooFinanceService _yahooFinance;

    public PreviousCloseService(
        ILogger<PreviousCloseService> logger,
        IStrategyRepository strategyRepo,
        IYahooFinanceService yahooFinance)
    {
        _logger = logger;
        _strategyRepo = strategyRepo;
        _yahooFinance = yahooFinance;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("=== PREVIOUS CLOSE CACHE SERVICE STARTED ===");
        Console.WriteLine("========================================");
        Console.WriteLine("  Previous Close Cache Service");
        Console.WriteLine("========================================");
        Console.WriteLine($"⏰ Cache Time: {CacheHour:D2}:{CacheMinute:D2} UTC daily");
        Console.WriteLine($"📊 Symbols: {string.Join(", ", SymbolsToCache)}");
        Console.WriteLine();

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                // Calculate time until next cache run
                var now = DateTime.UtcNow;
                var nextCacheTime = GetNextCacheTime(now);
                var waitTime = nextCacheTime - now;

                if (waitTime.TotalSeconds > 0)
                {
                    _logger.LogDebug("Waiting until {NextCache} UTC ({WaitHours:F1} hours)",
                        nextCacheTime.ToString("HH:mm:ss"), waitTime.TotalHours);

                    // Wait in chunks to allow cancellation
                    while (waitTime.TotalMinutes > 10 && !stoppingToken.IsCancellationRequested)
                    {
                        await Task.Delay(TimeSpan.FromMinutes(10), stoppingToken);
                        now = DateTime.UtcNow;
                        waitTime = nextCacheTime - now;
                    }

                    if (waitTime.TotalSeconds > 0 && !stoppingToken.IsCancellationRequested)
                    {
                        await Task.Delay(waitTime, stoppingToken);
                    }
                }

                if (stoppingToken.IsCancellationRequested)
                    break;

                // Cache previous closes
                await CachePreviousClosesAsync(stoppingToken);

                // Wait until next day
                await Task.Delay(TimeSpan.FromHours(23), stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error in PreviousCloseService loop");
                Console.WriteLine($"❌ PreviousCloseService ERROR: {ex.Message}");
                await Task.Delay(TimeSpan.FromMinutes(30), stoppingToken);
            }
        }

        _logger.LogInformation("=== PREVIOUS CLOSE CACHE SERVICE STOPPED ===");
    }

    private static DateTime GetNextCacheTime(DateTime now)
    {
        var today = now.Date;
        var cacheTime = today.AddHours(CacheHour).AddMinutes(CacheMinute);

        // If we've passed today's cache time, target tomorrow
        if (now > cacheTime)
        {
            cacheTime = cacheTime.AddDays(1);
        }

        return cacheTime;
    }

    private async Task CachePreviousClosesAsync(CancellationToken cancellationToken)
    {
        var cacheDate = DateTime.UtcNow.Date;

        _logger.LogInformation("📥 Caching previous closes for {Date}", cacheDate);
        Console.WriteLine($"\n📥 Caching previous closes for {cacheDate:yyyy-MM-dd}");

        // Check if today is a market holiday (don't cache on holidays)
        if (await _strategyRepo.IsUSMarketHolidayAsync(cacheDate))
        {
            _logger.LogInformation("Today is a US market holiday - skipping cache");
            Console.WriteLine("🏖️ US market holiday - skipping");
            return;
        }

        // Also skip weekends (Saturday = 6, Sunday = 0)
        if (cacheDate.DayOfWeek == DayOfWeek.Saturday || cacheDate.DayOfWeek == DayOfWeek.Sunday)
        {
            _logger.LogInformation("Weekend - skipping cache");
            Console.WriteLine("🗓️ Weekend - skipping");
            return;
        }

        // Fetch quotes
        var quotes = await _yahooFinance.GetQuotesAsync(SymbolsToCache);

        foreach (var symbol in SymbolsToCache)
        {
            if (!quotes.TryGetValue(symbol, out var quote) || !quote.Success)
            {
                _logger.LogWarning("Failed to fetch quote for {Symbol}: {Error}",
                    symbol, quote?.ErrorMessage ?? "Not found");
                Console.WriteLine($"   ⚠️ {symbol}: Failed - {quote?.ErrorMessage}");
                continue;
            }

            var previousClose = new MarketPreviousClose
            {
                Symbol = symbol,
                PreviousClose = quote.PreviousClose,
                CloseDate = cacheDate,
                DataSource = "YahooFinance"
            };

            await _strategyRepo.UpsertPreviousCloseAsync(previousClose);

            _logger.LogInformation("Cached {Symbol} previous close: {Price} for {Date}",
                symbol, quote.PreviousClose, cacheDate);
            Console.WriteLine($"   ✅ {symbol}: {quote.PreviousClose}");
        }

        Console.WriteLine("📥 Cache complete");
    }

    /// <summary>
    /// Manually trigger a cache refresh (for testing or recovery)
    /// </summary>
    public async Task ManualCacheAsync()
    {
        await CachePreviousClosesAsync(CancellationToken.None);
    }
}
