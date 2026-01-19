using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Extensions.Logging;

namespace DerivCTrader.Infrastructure.MarketData;

/// <summary>
/// Yahoo Finance market data service using raw HTTP calls.
/// Fetches quotes from Yahoo's v7 quote endpoint.
/// </summary>
public class YahooFinanceService : IYahooFinanceService
{
    private readonly HttpClient _httpClient;
    private readonly ILogger<YahooFinanceService> _logger;

    private const string BaseUrl = "https://query1.finance.yahoo.com/v7/finance/quote";

    public YahooFinanceService(HttpClient httpClient, ILogger<YahooFinanceService> logger)
    {
        _httpClient = httpClient;
        _logger = logger;

        // Set user agent to avoid blocks
        _httpClient.DefaultRequestHeaders.UserAgent.ParseAdd(
            "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36");
    }

    public async Task<YahooQuote> GetQuoteAsync(string symbol)
    {
        var quotes = await GetQuotesAsync(symbol);
        return quotes.TryGetValue(symbol, out var quote)
            ? quote
            : new YahooQuote
            {
                Symbol = symbol,
                Success = false,
                ErrorMessage = $"No quote returned for {symbol}"
            };
    }

    public async Task<Dictionary<string, YahooQuote>> GetQuotesAsync(params string[] symbols)
    {
        var result = new Dictionary<string, YahooQuote>(StringComparer.OrdinalIgnoreCase);

        if (symbols.Length == 0)
            return result;

        try
        {
            var symbolsParam = string.Join(",", symbols);
            var url = $"{BaseUrl}?symbols={Uri.EscapeDataString(symbolsParam)}";

            _logger.LogDebug("Fetching Yahoo Finance quotes for: {Symbols}", symbolsParam);

            var response = await _httpClient.GetAsync(url);

            if (!response.IsSuccessStatusCode)
            {
                var error = $"Yahoo Finance returned {response.StatusCode}";
                _logger.LogWarning(error);

                foreach (var symbol in symbols)
                {
                    result[symbol] = new YahooQuote
                    {
                        Symbol = symbol,
                        Success = false,
                        ErrorMessage = error
                    };
                }
                return result;
            }

            var content = await response.Content.ReadAsStringAsync();
            var json = JsonDocument.Parse(content);

            // Navigate to quoteResponse.result array
            if (!json.RootElement.TryGetProperty("quoteResponse", out var quoteResponse) ||
                !quoteResponse.TryGetProperty("result", out var resultArray))
            {
                _logger.LogWarning("Unexpected Yahoo Finance response structure");

                foreach (var symbol in symbols)
                {
                    result[symbol] = new YahooQuote
                    {
                        Symbol = symbol,
                        Success = false,
                        ErrorMessage = "Unexpected response structure"
                    };
                }
                return result;
            }

            // Parse each quote
            foreach (var quoteElement in resultArray.EnumerateArray())
            {
                var quote = ParseQuote(quoteElement);
                if (!string.IsNullOrEmpty(quote.Symbol))
                {
                    result[quote.Symbol] = quote;
                }
            }

            // Mark any missing symbols as failed
            foreach (var symbol in symbols)
            {
                if (!result.ContainsKey(symbol))
                {
                    result[symbol] = new YahooQuote
                    {
                        Symbol = symbol,
                        Success = false,
                        ErrorMessage = "Symbol not found in response"
                    };
                }
            }

            _logger.LogInformation("Fetched {Count} quotes from Yahoo Finance", result.Count);
            return result;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error fetching Yahoo Finance quotes");

            foreach (var symbol in symbols)
            {
                result[symbol] = new YahooQuote
                {
                    Symbol = symbol,
                    Success = false,
                    ErrorMessage = ex.Message
                };
            }

            return result;
        }
    }

    private YahooQuote ParseQuote(JsonElement element)
    {
        var quote = new YahooQuote();

        try
        {
            // Symbol
            if (element.TryGetProperty("symbol", out var symbolProp))
                quote.Symbol = symbolProp.GetString() ?? string.Empty;

            // Previous close
            if (element.TryGetProperty("regularMarketPreviousClose", out var prevCloseProp))
                quote.PreviousClose = prevCloseProp.GetDecimal();

            // Regular market price
            if (element.TryGetProperty("regularMarketPrice", out var regPriceProp))
                quote.RegularMarketPrice = regPriceProp.GetDecimal();

            // Pre-market price (may not exist)
            if (element.TryGetProperty("preMarketPrice", out var preMarketProp) &&
                preMarketProp.ValueKind == JsonValueKind.Number)
            {
                quote.PreMarketPrice = preMarketProp.GetDecimal();
            }

            // Determine latest price: prefer pre-market if available and non-zero
            quote.LatestPrice = quote.PreMarketPrice > 0
                ? quote.PreMarketPrice.Value
                : quote.RegularMarketPrice;

            // Timestamp
            if (element.TryGetProperty("regularMarketTime", out var timeProp))
            {
                var unixTime = timeProp.GetInt64();
                quote.QuoteTime = DateTimeOffset.FromUnixTimeSeconds(unixTime).UtcDateTime;
            }
            else
            {
                quote.QuoteTime = DateTime.UtcNow;
            }

            quote.Success = quote.PreviousClose > 0 && quote.LatestPrice > 0;

            _logger.LogDebug(
                "Parsed {Symbol}: PrevClose={PrevClose}, Latest={Latest}, PreMarket={PreMarket}",
                quote.Symbol, quote.PreviousClose, quote.LatestPrice, quote.PreMarketPrice);
        }
        catch (Exception ex)
        {
            quote.Success = false;
            quote.ErrorMessage = ex.Message;
            _logger.LogWarning(ex, "Error parsing quote for {Symbol}", quote.Symbol);
        }

        return quote;
    }
}
