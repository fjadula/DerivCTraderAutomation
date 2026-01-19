using System.Text.Json;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

namespace DerivCTrader.Infrastructure.MarketData;

/// <summary>
/// FinanceFlowAPI client for fetching historical market data.
/// API Documentation: https://financeflowapi.com/docs
/// </summary>
public class FinanceFlowService : IFinanceFlowService
{
    private readonly HttpClient _httpClient;
    private readonly ILogger<FinanceFlowService> _logger;
    private readonly string _apiKey;
    private readonly string _baseUrl;

    // Rate limiting: 20 requests/min
    private static readonly SemaphoreSlim _rateLimiter = new(1);
    private static DateTime _lastRequest = DateTime.MinValue;
    private const int MinRequestIntervalMs = 3100; // ~19 requests/min to stay safe

    public FinanceFlowService(
        HttpClient httpClient,
        IConfiguration configuration,
        ILogger<FinanceFlowService> logger)
    {
        _httpClient = httpClient;
        _logger = logger;

        _apiKey = configuration["FinanceFlowAPI:ApiKey"]
            ?? throw new InvalidOperationException("FinanceFlowAPI:ApiKey not configured");
        _baseUrl = configuration["FinanceFlowAPI:BaseUrl"]
            ?? "https://api.financeflowapi.com/v1";

        _httpClient.DefaultRequestHeaders.Add("X-API-Key", _apiKey);
    }

    public async Task<List<FinanceFlowCandle>> GetCandlesAsync(string symbol, DateTime startUtc, DateTime endUtc)
    {
        await RateLimitAsync();

        // Format: /tickers/{symbol}/history?interval=1m&from={timestamp}&to={timestamp}
        var fromTimestamp = new DateTimeOffset(startUtc).ToUnixTimeSeconds();
        var toTimestamp = new DateTimeOffset(endUtc).ToUnixTimeSeconds();

        var url = $"{_baseUrl}/tickers/{symbol}/history?interval=1m&from={fromTimestamp}&to={toTimestamp}";

        _logger.LogDebug("Fetching candles: {Symbol} from {Start} to {End}", symbol, startUtc, endUtc);

        try
        {
            var response = await _httpClient.GetAsync(url);

            if (!response.IsSuccessStatusCode)
            {
                var errorContent = await response.Content.ReadAsStringAsync();
                _logger.LogWarning("FinanceFlowAPI error {StatusCode}: {Error}", response.StatusCode, errorContent);
                return new List<FinanceFlowCandle>();
            }

            var json = await response.Content.ReadAsStringAsync();
            var data = JsonSerializer.Deserialize<FinanceFlowHistoryResponse>(json, new JsonSerializerOptions
            {
                PropertyNameCaseInsensitive = true
            });

            if (data?.Candles == null)
            {
                _logger.LogWarning("No candles returned for {Symbol}", symbol);
                return new List<FinanceFlowCandle>();
            }

            var candles = data.Candles.Select(c => new FinanceFlowCandle
            {
                TimeUtc = DateTimeOffset.FromUnixTimeSeconds(c.Timestamp).UtcDateTime,
                Open = c.Open,
                High = c.High,
                Low = c.Low,
                Close = c.Close,
                Volume = c.Volume
            }).ToList();

            _logger.LogInformation("Fetched {Count} candles for {Symbol}", candles.Count, symbol);
            return candles;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to fetch candles for {Symbol}", symbol);
            return new List<FinanceFlowCandle>();
        }
    }

    public async Task<decimal?> GetSpotPriceAsync(string symbol)
    {
        await RateLimitAsync();

        var url = $"{_baseUrl}/tickers/{symbol}/spot";

        try
        {
            var response = await _httpClient.GetAsync(url);

            if (!response.IsSuccessStatusCode)
            {
                return null;
            }

            var json = await response.Content.ReadAsStringAsync();
            var data = JsonSerializer.Deserialize<FinanceFlowSpotResponse>(json, new JsonSerializerOptions
            {
                PropertyNameCaseInsensitive = true
            });

            return data?.Price;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to fetch spot price for {Symbol}", symbol);
            return null;
        }
    }

    public async Task<List<string>> GetAvailableSymbolsAsync()
    {
        await RateLimitAsync();

        var url = $"{_baseUrl}/tickers/catalog";

        try
        {
            var response = await _httpClient.GetAsync(url);

            if (!response.IsSuccessStatusCode)
            {
                return new List<string>();
            }

            var json = await response.Content.ReadAsStringAsync();
            var data = JsonSerializer.Deserialize<FinanceFlowCatalogResponse>(json, new JsonSerializerOptions
            {
                PropertyNameCaseInsensitive = true
            });

            return data?.Symbols ?? new List<string>();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to fetch symbol catalog");
            return new List<string>();
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

    // Response DTOs
    private class FinanceFlowHistoryResponse
    {
        public List<CandleData>? Candles { get; set; }
    }

    private class CandleData
    {
        public long Timestamp { get; set; }
        public decimal Open { get; set; }
        public decimal High { get; set; }
        public decimal Low { get; set; }
        public decimal Close { get; set; }
        public long? Volume { get; set; }
    }

    private class FinanceFlowSpotResponse
    {
        public decimal Price { get; set; }
    }

    private class FinanceFlowCatalogResponse
    {
        public List<string>? Symbols { get; set; }
    }
}
