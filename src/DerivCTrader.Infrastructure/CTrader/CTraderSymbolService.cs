using DerivCTrader.Infrastructure.CTrader.Interfaces;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Configuration;
using OpenAPI.Net.Helpers;
using DerivCTrader.Infrastructure.CTrader.Models;

namespace DerivCTrader.Infrastructure.CTrader
{
    /// <summary>
    /// Manages symbol ID mappings for cTrader
    /// Fetches symbol list on startup and caches them
    /// </summary>
    public class CTraderSymbolService : ICTraderSymbolService
    {
    public bool TryGetSymbolTickValue(long symbolId, out double tickValue)
    {
        return _symbolIdToTickValue.TryGetValue(symbolId, out tickValue);
    }

    public bool TryGetSymbolContractSize(long symbolId, out double contractSize)
    {
        return _symbolIdToContractSize.TryGetValue(symbolId, out contractSize);
    }

    public bool TryGetSymbolMarginInitial(long symbolId, out double marginInitial)
    {
        return _symbolIdToMarginInitial.TryGetValue(symbolId, out marginInitial);
    }

    private static long ReadInt64Prop(object obj, string propName)
    {
        try
        {
            var prop = obj.GetType().GetProperty(propName);
            if (prop == null) return 0;
            var value = prop.GetValue(obj);
            return value == null ? 0 : Convert.ToInt64(value);
        }
        catch
        {
            return 0;
        }
    }

    private static double ReadDoubleProp(object obj, string propName)
    {
        try
        {
            var prop = obj.GetType().GetProperty(propName);
            if (prop == null) return 0.0;
            var value = prop.GetValue(obj);
            return value == null ? 0.0 : Convert.ToDouble(value);
        }
        catch
        {
            return 0.0;
        }
    }
    private readonly ICTraderClient _client;
    private readonly ILogger<CTraderSymbolService> _logger;
    private readonly IConfiguration _configuration;
    private readonly Dictionary<string, long> _symbolMap = new();
    private readonly Dictionary<string, long> _symbolMapIgnoreCase = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, long> _symbolMapNormalized = new(StringComparer.Ordinal);
    private readonly Dictionary<long, string> _symbolIdToName = new();
    private readonly Dictionary<long, int> _symbolIdToDigits = new();
    private readonly Dictionary<long, long> _symbolIdToMinVolume = new();
    private readonly Dictionary<long, long> _symbolIdToMaxVolume = new();
    private readonly Dictionary<long, long> _symbolIdToStepVolume = new();
    private readonly Dictionary<long, double> _symbolIdToTickValue = new();
    private readonly Dictionary<long, double> _symbolIdToContractSize = new();
    private readonly Dictionary<long, double> _symbolIdToMarginInitial = new();
    private readonly SemaphoreSlim _volumeFetchLock = new(1, 1);
    private bool _initialized = false;
    private bool _fetchedFromServer = false;

    public bool IsInitialized => _initialized && _fetchedFromServer;

    public bool TryGetSymbolName(long symbolId, out string symbolName)
    {
        return _symbolIdToName.TryGetValue(symbolId, out symbolName!);
    }

    public bool TryGetSymbolDigits(long symbolId, out int digits)
    {
        return _symbolIdToDigits.TryGetValue(symbolId, out digits);
    }

    public bool TryGetSymbolVolumeConstraints(long symbolId, out long minVolume, out long maxVolume, out long stepVolume)
    {
        var okMin = _symbolIdToMinVolume.TryGetValue(symbolId, out minVolume);
        var okMax = _symbolIdToMaxVolume.TryGetValue(symbolId, out maxVolume);
        var okStep = _symbolIdToStepVolume.TryGetValue(symbolId, out stepVolume);
        return okMin || okMax || okStep;
    }

    public async Task EnsureSymbolVolumeConstraintsAsync(long symbolId, CancellationToken cancellationToken = default)
    {
        if (_symbolIdToMinVolume.ContainsKey(symbolId) && _symbolIdToMaxVolume.ContainsKey(symbolId) && _symbolIdToStepVolume.ContainsKey(symbolId))
            return;

        if (!_client.IsConnected || !_client.IsAccountAuthenticated)
            return;

        await _volumeFetchLock.WaitAsync(cancellationToken);
        try
        {
            // Double-check after lock
            if (_symbolIdToMinVolume.ContainsKey(symbolId) && _symbolIdToMaxVolume.ContainsKey(symbolId) && _symbolIdToStepVolume.ContainsKey(symbolId))
                return;

            var req = new ProtoOASymbolByIdReq
            {
                CtidTraderAccountId = GetAccountIdFromConfig(),
            };

            // Some SDKs model SymbolId as a repeated field (read-only collection).
            req.SymbolId.Add(symbolId);

            await _client.SendMessageAsync(req, 2112); // PROTO_OA_SYMBOL_BY_ID_REQ
            var res = await _client.WaitForResponseAsync<ProtoOASymbolByIdRes>(
                2113, // PROTO_OA_SYMBOL_BY_ID_RES
                TimeSpan.FromSeconds(10));

            if (res == null)
                return;

            // Different SDK versions expose symbol details slightly differently; use reflection to extract safely.
            object? symbolObj = res.Symbol;
            if (symbolObj == null)
                return;

            // Some SDKs model Symbol as a repeated field/collection.
            if (symbolObj is System.Collections.IEnumerable enumerable && symbolObj is not string)
            {
                object? first = null;
                foreach (var item in enumerable)
                {
                    first = item;
                    break;
                }

                if (first != null)
                    symbolObj = first;
            }

                var minVolume = ReadInt64Prop(symbolObj, "MinVolume");
                var maxVolume = ReadInt64Prop(symbolObj, "MaxVolume");
                var stepVolume = ReadInt64Prop(symbolObj, "StepVolume");

                // New: Read TickValue, ContractSize, MarginInitial (reflection, fallback to 0 if missing)
                var tickValue = ReadDoubleProp(symbolObj, "TickValue");
                var contractSize = ReadDoubleProp(symbolObj, "ContractSize");
                var marginInitial = ReadDoubleProp(symbolObj, "MarginInitial");

                // Cache even if some are zero; it prevents re-fetch loops.
                _symbolIdToMinVolume[symbolId] = minVolume;
                _symbolIdToMaxVolume[symbolId] = maxVolume;
                _symbolIdToStepVolume[symbolId] = stepVolume;
                _symbolIdToTickValue[symbolId] = tickValue;
                _symbolIdToContractSize[symbolId] = contractSize;
                _symbolIdToMarginInitial[symbolId] = marginInitial;

                _logger.LogInformation(
                    "Loaded volume constraints for SymbolId={SymbolId}: min={Min}, max={Max}, step={Step}, tickValue={TickValue}, contractSize={ContractSize}, marginInitial={MarginInitial}",
                    symbolId, minVolume, maxVolume, stepVolume, tickValue, contractSize, marginInitial);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // normal shutdown
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to load volume constraints for SymbolId={SymbolId}", symbolId);
        }
        finally
        {
            _volumeFetchLock.Release();
        }
    }

    public CTraderSymbolService(ICTraderClient client, ILogger<CTraderSymbolService> logger, IConfiguration configuration)
    {
        _client = client;
        _logger = logger;
        _configuration = configuration;
    }

    /// <summary>
    /// Fetch all available symbols from cTrader
    /// Call this once after authentication
    /// </summary>
    public async Task InitializeAsync()
    {
        if (_fetchedFromServer)
            return;

        if (!_client.IsConnected || !_client.IsAccountAuthenticated)
        {
            _logger.LogWarning("Symbol initialization skipped: cTrader client not connected/account-authenticated");
            return;
        }

        try
        {
            _logger.LogInformation("Fetching symbol list from cTrader (dynamic IDs)...");

            var request = new ProtoOASymbolsListReq
            {
                CtidTraderAccountId = GetAccountIdFromConfig()
            };

            await _client.SendMessageAsync(request, 2114); // PROTO_OA_SYMBOLS_LIST_REQ

            var response = await _client.WaitForResponseAsync<ProtoOASymbolsListRes>(
                2115, // PROTO_OA_SYMBOLS_LIST_RES
                TimeSpan.FromSeconds(15));

            if (response == null)
            {
                _logger.LogError("Failed to receive symbol list from cTrader");
                return;
            }

            _symbolMap.Clear();
            _symbolMapIgnoreCase.Clear();
            _symbolMapNormalized.Clear();
            _symbolIdToName.Clear();
            _symbolIdToDigits.Clear();
            _symbolIdToMinVolume.Clear();
            _symbolIdToMaxVolume.Clear();
            _symbolIdToStepVolume.Clear();
            _symbolIdToTickValue.Clear();
            _symbolIdToContractSize.Clear();
            _symbolIdToMarginInitial.Clear();

            var symbolCount = 0;
            foreach (var symbol in response.Symbol)
            {
                if (string.IsNullOrWhiteSpace(symbol.SymbolName))
                    continue;

                _symbolMap[symbol.SymbolName] = symbol.SymbolId;
                _symbolMapIgnoreCase[symbol.SymbolName] = symbol.SymbolId;

                // Explicit mapping for VOLATILITY25 (internal) to Volatility 25 Index (cTrader)
                if (symbol.SymbolName == "Volatility 25 Index")
                {
                    _symbolMap["VOLATILITY25"] = symbol.SymbolId;
                    _symbolMapIgnoreCase["VOLATILITY25"] = symbol.SymbolId;
                }

                var normalizedKey = NormalizeSymbolKey(symbol.SymbolName);
                if (!string.IsNullOrWhiteSpace(normalizedKey))
                {
                    // If collisions happen, keep the first mapping and let the exact/ignore-case paths win.
                    _symbolMapNormalized.TryAdd(normalizedKey, symbol.SymbolId);

                    // Common synthetic index name variants:
                    // - Optional "(1s)" token
                    // - Optional trailing "Index" word
                    var normalizedNo1s = normalizedKey.Replace("1S", "", StringComparison.Ordinal);
                    if (!string.Equals(normalizedNo1s, normalizedKey, StringComparison.Ordinal))
                        _symbolMapNormalized.TryAdd(normalizedNo1s, symbol.SymbolId);

                    var normalizedNoIndex = normalizedKey.Replace("INDEX", "", StringComparison.Ordinal);
                    if (!string.Equals(normalizedNoIndex, normalizedKey, StringComparison.Ordinal))
                        _symbolMapNormalized.TryAdd(normalizedNoIndex, symbol.SymbolId);

                    var normalizedNo1sNoIndex = normalizedNo1s.Replace("INDEX", "", StringComparison.Ordinal);
                    if (!string.Equals(normalizedNo1sNoIndex, normalizedKey, StringComparison.Ordinal))
                        _symbolMapNormalized.TryAdd(normalizedNo1sNoIndex, symbol.SymbolId);
                }
                _symbolIdToName[symbol.SymbolId] = symbol.SymbolName;
                _symbolIdToDigits[symbol.SymbolId] = TryGetDigits(symbol);

                symbolCount++;
            }

            _initialized = true;
            _fetchedFromServer = true;

            _logger.LogInformation("✅ Symbol mapping fetched from cTrader: {Count} symbols", symbolCount);

            // Safety log for common pairs to catch mismaps early.
            LogSanity("USDJPY");
            LogSanity("EURJPY");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to initialize symbol mapping");
            throw;
        }
    }

    /// <summary>
    /// Get cTrader symbol ID for an asset name
    /// </summary>
    public long GetSymbolId(string assetName)
    {
        if (!_initialized)
            throw new InvalidOperationException("Symbol service not initialized. Call InitializeAsync first.");

        // Try exact match first
        if (_symbolMap.TryGetValue(assetName, out var symbolId))
            return symbolId;

        // Try case-insensitive exact
        if (_symbolMapIgnoreCase.TryGetValue(assetName, out symbolId))
            return symbolId;

        // Try common variations
        var variations = new[]
        {
            assetName,
            assetName.Replace("/", ""),  // GBP/USD -> GBPUSD
            assetName.Replace(" ", ""),  // EUR USD -> EURUSD
            assetName.ToUpper(),
            assetName.ToLower(),
            assetName.Replace("(", " ").Replace(")", " "),
            assetName.Replace("Index", "").Trim(),
        };

        foreach (var variant in variations)
        {
            if (_symbolMap.TryGetValue(variant, out symbolId))
                return symbolId;

            if (_symbolMapIgnoreCase.TryGetValue(variant, out symbolId))
                return symbolId;
        }

        // Try normalized lookup (handles synthetic naming differences like spaces/parentheses/suffixes)
        var normalized = NormalizeSymbolKey(assetName);
        if (!string.IsNullOrWhiteSpace(normalized) && _symbolMapNormalized.TryGetValue(normalized, out symbolId))
            return symbolId;

        // Fuzzy/like search: substring and Levenshtein distance (for last resort)
        // Substring match (case-insensitive)
        var assetLower = assetName.ToLowerInvariant();
        var bestMatch = _symbolMapIgnoreCase.Keys
            .Select(k => new { Key = k, Index = k.ToLowerInvariant().IndexOf(assetLower) })
            .Where(x => x.Index >= 0)
            .OrderBy(x => x.Index)
            .FirstOrDefault();
        if (bestMatch != null && _symbolMapIgnoreCase.TryGetValue(bestMatch.Key, out symbolId))
            return symbolId;

        // Levenshtein distance fallback (only if assetName is not too short)
        if (assetName.Length > 4)
        {
            int MinDistance = 3;
            string? closest = null;
            int bestDistance = int.MaxValue;
            foreach (var k in _symbolMapIgnoreCase.Keys)
            {
                int dist = LevenshteinDistance(assetName.ToLowerInvariant(), k.ToLowerInvariant());
                if (dist < bestDistance && dist <= MinDistance)
                {
                    bestDistance = dist;
                    closest = k;
                }
            }
            if (closest != null && _symbolMapIgnoreCase.TryGetValue(closest, out symbolId))
                return symbolId;
        }

        throw new ArgumentException($"Unknown symbol: {assetName}");

        // --- Local function for Levenshtein distance ---
        int LevenshteinDistance(string s, string t)
        {
            if (string.IsNullOrEmpty(s)) return t.Length;
            if (string.IsNullOrEmpty(t)) return s.Length;
            int[,] d = new int[s.Length + 1, t.Length + 1];
            for (int i = 0; i <= s.Length; i++) d[i, 0] = i;
            for (int j = 0; j <= t.Length; j++) d[0, j] = j;
            for (int i = 1; i <= s.Length; i++)
            {
                for (int j = 1; j <= t.Length; j++)
                {
                    int cost = s[i - 1] == t[j - 1] ? 0 : 1;
                    d[i, j] = Math.Min(
                        Math.Min(d[i - 1, j] + 1, d[i, j - 1] + 1),
                        d[i - 1, j - 1] + cost);
                }
            }
            return d[s.Length, t.Length];
        }
    }

    /// <summary>
    /// Check if a symbol exists
    /// </summary>
    public bool HasSymbol(string assetName)
    {
        if (!_initialized)
            return false;

        if (_symbolMap.ContainsKey(assetName) || _symbolMapIgnoreCase.ContainsKey(assetName))
            return true;

        var noSlash = assetName.Replace("/", "");
        if (_symbolMap.ContainsKey(noSlash) || _symbolMapIgnoreCase.ContainsKey(noSlash))
            return true;

        var upper = assetName.ToUpperInvariant();
        if (_symbolMap.ContainsKey(upper) || _symbolMapIgnoreCase.ContainsKey(upper))
            return true;

        var normalized = NormalizeSymbolKey(assetName);
        return !string.IsNullOrWhiteSpace(normalized) && _symbolMapNormalized.ContainsKey(normalized);
    }

    private static string NormalizeSymbolKey(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return string.Empty;

        // Normalize common synthetic name differences.
        // Examples:
        // - "Volatility 50(1s) Index" -> "VOLATILITY501SINDEX"
        // - "Volatility 50 (1s) Index" -> "VOLATILITY501SINDEX"
        // - "VOLATILITY 50 INDEX" -> "VOLATILITY50INDEX"
        var normalized = value.Trim().ToUpperInvariant();

        normalized = normalized
            .Replace("/", "")
            .Replace(" ", "")
            .Replace("(", "")
            .Replace(")", "")
            .Replace("-", "")
            .Replace("_", "")
            .Replace(".", "")
            .Replace(":", "");

        return normalized;
    }

    private long GetAccountIdFromConfig()
    {
        var ctraderSection = _configuration.GetSection("CTrader");
        var environment = ctraderSection["Environment"] ?? "Demo";
        var accountIdKey = environment == "Live" ? "LiveAccountId" : "DemoAccountId";
        return long.Parse(ctraderSection[accountIdKey] ?? "0");
    }

    private void LogSanity(string symbolName)
    {
        if (_symbolMap.TryGetValue(symbolName, out var id) && _symbolIdToName.TryGetValue(id, out var backName))
        {
            _logger.LogInformation("Symbol sanity: {Name} => {Id} (Id maps back to {BackName})", symbolName, id, backName);
        }
    }

    private static int TryGetDigits(object symbol)
    {
        try
        {
            // ProtoOALightSymbol schema differs across package versions; use reflection.
            var digitsProp = symbol.GetType().GetProperty("Digits");
            if (digitsProp == null)
                return 0;

            var value = digitsProp.GetValue(symbol);
            return value is null ? 0 : Convert.ToInt32(value);
        }
        catch
        {
            return 0;
        }
    }
    }
}
