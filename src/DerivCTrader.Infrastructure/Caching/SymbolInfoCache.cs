using DerivCTrader.Application.Interfaces;
using DerivCTrader.Domain.Entities;
using Microsoft.Extensions.Logging;

namespace DerivCTrader.Infrastructure.Caching;

/// <summary>
/// In-memory cache for SymbolInfo loaded from database at startup.
/// Provides O(1) lookups by cTrader symbol ID and fast name-based lookups.
/// </summary>
public class SymbolInfoCache : ISymbolInfoCache
{
    private readonly ITradeRepository _repository;
    private readonly ILogger<SymbolInfoCache> _logger;

    private readonly Dictionary<long, SymbolInfo> _byCTraderId = new();
    private readonly Dictionary<string, SymbolInfo> _byNameExact = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, SymbolInfo> _byNameNormalized = new(StringComparer.Ordinal);
    private List<SymbolInfo> _allSymbols = new();
    private bool _initialized;

    public bool IsInitialized => _initialized;
    public int Count => _allSymbols.Count;

    public SymbolInfoCache(ITradeRepository repository, ILogger<SymbolInfoCache> logger)
    {
        _repository = repository;
        _logger = logger;
    }

    public async Task InitializeAsync()
    {
        if (_initialized)
            return;

        try
        {
            _logger.LogInformation("Loading SymbolInfo cache from database...");

            var symbols = await _repository.GetAllSymbolInfoAsync();

            _byCTraderId.Clear();
            _byNameExact.Clear();
            _byNameNormalized.Clear();
            _allSymbols = symbols;

            foreach (var symbol in symbols)
            {
                // Index by cTrader ID
                if (symbol.CTraderSymbolId > 0)
                {
                    _byCTraderId[symbol.CTraderSymbolId] = symbol;
                }

                // Index by exact name (case-insensitive)
                if (!string.IsNullOrWhiteSpace(symbol.SymbolName))
                {
                    _byNameExact[symbol.SymbolName] = symbol;

                    // Also index normalized version for fuzzy matching
                    var normalized = NormalizeSymbolName(symbol.SymbolName);
                    if (!string.IsNullOrWhiteSpace(normalized))
                    {
                        _byNameNormalized.TryAdd(normalized, symbol);

                        // Add common variations for synthetics
                        AddNormalizedVariations(normalized, symbol);
                    }

                    // Add BaseAsset as lookup key (e.g., "R_25" -> Volatility 25)
                    if (!string.IsNullOrWhiteSpace(symbol.BaseAsset))
                    {
                        _byNameExact.TryAdd(symbol.BaseAsset, symbol);
                        _byNameNormalized.TryAdd(NormalizeSymbolName(symbol.BaseAsset), symbol);
                    }
                }
            }

            _initialized = true;
            _logger.LogInformation("✅ SymbolInfo cache loaded: {Count} symbols", symbols.Count);

            // Log some examples for verification
            LogCacheStats();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to initialize SymbolInfo cache");
            throw;
        }
    }

    public SymbolInfo? GetByName(string symbolName)
    {
        if (string.IsNullOrWhiteSpace(symbolName))
            return null;

        // Try exact match first
        if (_byNameExact.TryGetValue(symbolName, out var symbol))
            return symbol;

        // Try normalized match
        var normalized = NormalizeSymbolName(symbolName);
        if (!string.IsNullOrWhiteSpace(normalized) && _byNameNormalized.TryGetValue(normalized, out symbol))
            return symbol;

        // Try common variations
        var variations = new[]
        {
            symbolName.Replace("/", ""),
            symbolName.Replace(" ", ""),
            symbolName.Replace("(", "").Replace(")", ""),
            symbolName + " Index",
            symbolName.Replace(" Index", ""),
        };

        foreach (var variant in variations)
        {
            if (_byNameExact.TryGetValue(variant, out symbol))
                return symbol;

            var normalizedVariant = NormalizeSymbolName(variant);
            if (!string.IsNullOrWhiteSpace(normalizedVariant) && _byNameNormalized.TryGetValue(normalizedVariant, out symbol))
                return symbol;
        }

        return null;
    }

    public SymbolInfo? GetByCTraderId(long cTraderSymbolId)
    {
        _byCTraderId.TryGetValue(cTraderSymbolId, out var symbol);
        return symbol;
    }

    public bool TryGetVolumeConstraints(long cTraderSymbolId, out long minVolume, out long maxVolume, out long stepVolume)
    {
        minVolume = 0;
        maxVolume = 0;
        stepVolume = 0;

        var symbol = GetByCTraderId(cTraderSymbolId);
        if (symbol == null)
            return false;

        // Convert from lots to cTrader wire format (scaled by 100)
        // DB stores: MinTradeQuantity = 0.50 (lots)
        // cTrader wire expects: 50 (0.50 * 100)
        minVolume = (long)(symbol.MinTradeQuantity * 100);
        maxVolume = (long)(symbol.MaxTradeQuantity * 100);
        stepVolume = (long)(symbol.StepVolume * 100);

        return true;
    }

    public bool TryGetVolumeConstraintsByName(string symbolName, out long minVolume, out long maxVolume, out long stepVolume)
    {
        minVolume = 0;
        maxVolume = 0;
        stepVolume = 0;

        var symbol = GetByName(symbolName);
        if (symbol == null)
            return false;

        minVolume = (long)(symbol.MinTradeQuantity * 100);
        maxVolume = (long)(symbol.MaxTradeQuantity * 100);
        stepVolume = (long)(symbol.StepVolume * 100);

        return true;
    }

    public IReadOnlyList<SymbolInfo> GetAll() => _allSymbols.AsReadOnly();

    private static string NormalizeSymbolName(string name)
    {
        if (string.IsNullOrWhiteSpace(name))
            return string.Empty;

        return name.Trim()
            .ToUpperInvariant()
            .Replace("/", "")
            .Replace(" ", "")
            .Replace("(", "")
            .Replace(")", "")
            .Replace("-", "")
            .Replace("_", "")
            .Replace(".", "")
            .Replace(":", "");
    }

    private void AddNormalizedVariations(string normalized, SymbolInfo symbol)
    {
        // Handle synthetic index naming variations
        // "VOLATILITY25INDEX" -> also add "VOLATILITY25", "V25", etc.

        var withoutIndex = normalized.Replace("INDEX", "");
        if (withoutIndex != normalized)
            _byNameNormalized.TryAdd(withoutIndex, symbol);

        var without1s = normalized.Replace("1S", "");
        if (without1s != normalized)
            _byNameNormalized.TryAdd(without1s, symbol);

        var withoutBoth = withoutIndex.Replace("1S", "");
        if (withoutBoth != normalized && withoutBoth != withoutIndex)
            _byNameNormalized.TryAdd(withoutBoth, symbol);
    }

    private void LogCacheStats()
    {
        var categories = _allSymbols
            .GroupBy(s => s.Category ?? "Unknown")
            .Select(g => $"{g.Key}: {g.Count()}")
            .ToList();

        _logger.LogInformation("SymbolInfo cache categories: {Categories}", string.Join(", ", categories));

        // Log first few symbols for sanity check
        foreach (var symbol in _allSymbols.Take(3))
        {
            _logger.LogDebug("  - {Name} (ID={Id}, Category={Category}, MinLot={Min}, MaxLot={Max})",
                symbol.SymbolName, symbol.CTraderSymbolId, symbol.Category,
                symbol.MinTradeQuantity, symbol.MaxTradeQuantity);
        }
    }
}
