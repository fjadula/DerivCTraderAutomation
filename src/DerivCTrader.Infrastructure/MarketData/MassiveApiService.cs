using System.Net.Http.Headers;
using System.Text.Json;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

namespace DerivCTrader.Infrastructure.MarketData;

/// <summary>
/// Massive API client for fetching historical market data.
/// API: https://api.massive.com/v3
/// </summary>
public class MassiveApiService : IMassiveApiService
{
    private readonly HttpClient _httpClient;
    private readonly ILogger<MassiveApiService> _logger;
    private readonly string _baseUrl;

    // Rate limiting: stay safe with API limits
    // Polygon.io free tier: 5 requests per minute = 12 seconds per request
    private static readonly SemaphoreSlim _rateLimiter = new(1);
    private static DateTime _lastRequest = DateTime.MinValue;
    private const int MinRequestIntervalMs = 13000; // 5 requests per minute (13 seconds to be safe)

    public MassiveApiService(
        HttpClient httpClient,
        IConfiguration configuration,
        ILogger<MassiveApiService> logger)
    {
        _httpClient = httpClient;
        _logger = logger;

        var apiKey = configuration["MassiveAPI:ApiKey"]
            ?? throw new InvalidOperationException("MassiveAPI:ApiKey not configured");
        _baseUrl = configuration["MassiveAPI:BaseUrl"]
            ?? "https://api.massive.com/v3";

        // Set up authorization header
        _httpClient.DefaultRequestHeaders.Authorization =
            new AuthenticationHeaderValue("Bearer", apiKey);
    }

    public async Task<List<MassiveCandle>> GetMinuteCandlesAsync(string ticker, DateTime fromUtc, DateTime toUtc)
    {
        await RateLimitAsync();

        // Polygon.io format: /v2/aggs/ticker/{TICKER}/range/1/minute/{FROM}/{TO}
        // FROM/TO as Unix milliseconds timestamps
        var fromMs = new DateTimeOffset(fromUtc, TimeSpan.Zero).ToUnixTimeMilliseconds();
        var toMs = new DateTimeOffset(toUtc, TimeSpan.Zero).ToUnixTimeMilliseconds();

        var url = $"{_baseUrl}/v2/aggs/ticker/{ticker}/range/1/minute/{fromMs}/{toMs}?adjusted=true&sort=asc";

        _logger.LogDebug("Fetching candles: {Ticker} from {From} to {To}", ticker, fromUtc, toUtc);

        try
        {
            var response = await _httpClient.GetAsync(url);

            if (!response.IsSuccessStatusCode)
            {
                var errorContent = await response.Content.ReadAsStringAsync();
                _logger.LogWarning("Massive API error {StatusCode}: {Error}", response.StatusCode, errorContent);
                return new List<MassiveCandle>();
            }

            var json = await response.Content.ReadAsStringAsync();
            var doc = JsonDocument.Parse(json);

            var candles = new List<MassiveCandle>();

            if (doc.RootElement.TryGetProperty("results", out var results))
            {
                foreach (var item in results.EnumerateArray())
                {
                    try
                    {
                        var candle = new MassiveCandle
                        {
                            TimeUtc = DateTimeOffset
                                .FromUnixTimeMilliseconds(item.GetProperty("t").GetInt64())
                                .UtcDateTime,
                            Open = item.GetProperty("o").GetDecimal(),
                            High = item.GetProperty("h").GetDecimal(),
                            Low = item.GetProperty("l").GetDecimal(),
                            Close = item.GetProperty("c").GetDecimal()
                        };

                        // Volume is optional
                        if (item.TryGetProperty("v", out var volumeElement))
                        {
                            candle.Volume = volumeElement.GetInt64();
                        }

                        candles.Add(candle);
                    }
                    catch (Exception ex)
                    {
                        _logger.LogWarning(ex, "Failed to parse candle data");
                    }
                }
            }

            _logger.LogInformation("Fetched {Count} candles for {Ticker}", candles.Count, ticker);
            return candles;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to fetch candles for {Ticker}", ticker);
            return new List<MassiveCandle>();
        }
    }

    public async Task<List<MassiveTicker>> SearchTickersAsync(string market, string search)
    {
        await RateLimitAsync();

        // Polygon.io format: /v3/reference/tickers
        var url = $"{_baseUrl}/v3/reference/tickers?market={market}&search={search}&active=true&limit=20";

        _logger.LogDebug("Searching tickers: market={Market}, search={Search}", market, search);

        try
        {
            var response = await _httpClient.GetAsync(url);

            if (!response.IsSuccessStatusCode)
            {
                var errorContent = await response.Content.ReadAsStringAsync();
                _logger.LogWarning("Massive API error {StatusCode}: {Error}", response.StatusCode, errorContent);
                return new List<MassiveTicker>();
            }

            var json = await response.Content.ReadAsStringAsync();
            var doc = JsonDocument.Parse(json);

            var tickers = new List<MassiveTicker>();

            if (doc.RootElement.TryGetProperty("results", out var results))
            {
                foreach (var item in results.EnumerateArray())
                {
                    try
                    {
                        tickers.Add(new MassiveTicker
                        {
                            Ticker = item.GetProperty("ticker").GetString() ?? "",
                            Name = item.TryGetProperty("name", out var name) ? name.GetString() ?? "" : "",
                            Market = item.TryGetProperty("market", out var mkt) ? mkt.GetString() ?? "" : "",
                            Active = item.TryGetProperty("active", out var active) && active.GetBoolean()
                        });
                    }
                    catch (Exception ex)
                    {
                        _logger.LogWarning(ex, "Failed to parse ticker data");
                    }
                }
            }

            _logger.LogInformation("Found {Count} tickers matching '{Search}'", tickers.Count, search);
            return tickers;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to search tickers");
            return new List<MassiveTicker>();
        }
    }

    public async Task<Dictionary<string, MassiveQuote>> GetLatestQuotesAsync(params string[] tickers)
    {
        var results = new Dictionary<string, MassiveQuote>();

        foreach (var ticker in tickers)
        {
            await RateLimitAsync();

            // Use Polygon.io's previous day endpoint to get latest data
            // For free tier, this gives us the most recent trading day's data
            var url = $"{_baseUrl}/v2/aggs/ticker/{ticker}/prev?adjusted=true";

            _logger.LogDebug("Fetching latest quote for {Ticker}", ticker);

            try
            {
                var response = await _httpClient.GetAsync(url);

                if (!response.IsSuccessStatusCode)
                {
                    var errorContent = await response.Content.ReadAsStringAsync();
                    _logger.LogWarning("Polygon API error {StatusCode} for {Ticker}: {Error}",
                        response.StatusCode, ticker, errorContent);

                    results[ticker] = new MassiveQuote
                    {
                        Ticker = ticker,
                        Success = false,
                        ErrorMessage = $"API error: {response.StatusCode}"
                    };
                    continue;
                }

                var json = await response.Content.ReadAsStringAsync();
                var doc = JsonDocument.Parse(json);

                if (doc.RootElement.TryGetProperty("results", out var resultsArray) &&
                    resultsArray.GetArrayLength() > 0)
                {
                    var item = resultsArray[0];
                    var close = item.GetProperty("c").GetDecimal();
                    var open = item.GetProperty("o").GetDecimal();

                    results[ticker] = new MassiveQuote
                    {
                        Ticker = ticker,
                        LatestPrice = close,
                        PreviousClose = open, // Using open as proxy for previous close
                        Change = close - open,
                        ChangePercent = open != 0 ? (close - open) / open * 100 : 0,
                        Timestamp = DateTimeOffset.FromUnixTimeMilliseconds(
                            item.GetProperty("t").GetInt64()).UtcDateTime,
                        Success = true
                    };

                    _logger.LogInformation("Quote {Ticker}: ${Price:F2}", ticker, close);
                }
                else
                {
                    results[ticker] = new MassiveQuote
                    {
                        Ticker = ticker,
                        Success = false,
                        ErrorMessage = "No data returned"
                    };
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to fetch quote for {Ticker}", ticker);
                results[ticker] = new MassiveQuote
                {
                    Ticker = ticker,
                    Success = false,
                    ErrorMessage = ex.Message
                };
            }
        }

        return results;
    }

    public async Task<MassivePreviousClose?> GetPreviousCloseAsync(string ticker)
    {
        await RateLimitAsync();

        // Get the previous trading day's OHLCV data
        var url = $"{_baseUrl}/v2/aggs/ticker/{ticker}/prev?adjusted=true";

        _logger.LogDebug("Fetching previous close for {Ticker}", ticker);

        try
        {
            var response = await _httpClient.GetAsync(url);

            if (!response.IsSuccessStatusCode)
            {
                var errorContent = await response.Content.ReadAsStringAsync();
                _logger.LogWarning("Polygon API error {StatusCode} for {Ticker}: {Error}",
                    response.StatusCode, ticker, errorContent);
                return null;
            }

            var json = await response.Content.ReadAsStringAsync();
            var doc = JsonDocument.Parse(json);

            if (doc.RootElement.TryGetProperty("results", out var resultsArray) &&
                resultsArray.GetArrayLength() > 0)
            {
                var item = resultsArray[0];

                return new MassivePreviousClose
                {
                    Ticker = ticker,
                    Open = item.GetProperty("o").GetDecimal(),
                    High = item.GetProperty("h").GetDecimal(),
                    Low = item.GetProperty("l").GetDecimal(),
                    Close = item.GetProperty("c").GetDecimal(),
                    Volume = item.TryGetProperty("v", out var vol) ? vol.GetInt64() : 0,
                    Date = DateTimeOffset.FromUnixTimeMilliseconds(
                        item.GetProperty("t").GetInt64()).UtcDateTime.Date
                };
            }

            return null;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to fetch previous close for {Ticker}", ticker);
            return null;
        }
    }

    private async Task RateLimitAsync()
    {
        await _rateLimiter.WaitAsync();
        try
        {
            var elapsed = (DateTime.UtcNow - _lastRequest).TotalMilliseconds;
            if (elapsed < MinRequestIntervalMs)
            {
                var delay = MinRequestIntervalMs - (int)elapsed;
                _logger.LogDebug("Rate limiting: waiting {Delay}ms", delay);
                await Task.Delay(delay);
            }
            _lastRequest = DateTime.UtcNow;
        }
        finally
        {
            _rateLimiter.Release();
        }
    }
}
