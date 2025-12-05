using DerivCTrader.Infrastructure.CTrader.Interfaces;
using DerivCTrader.Infrastructure.CTrader.Models;
using Microsoft.Extensions.Logging;

namespace DerivCTrader.Infrastructure.CTrader;

/// <summary>
/// Manages symbol ID mappings for cTrader
/// Fetches symbol list on startup and caches them
/// </summary>
public class CTraderSymbolService : ICTraderSymbolService
{
    private readonly ICTraderClient _client;
    private readonly ILogger<CTraderSymbolService> _logger;
    private readonly Dictionary<string, long> _symbolMap = new();
    private bool _initialized = false;

    public CTraderSymbolService(ICTraderClient client, ILogger<CTraderSymbolService> logger)
    {
        _client = client;
        _logger = logger;
    }

    /// <summary>
    /// Fetch all available symbols from cTrader
    /// Call this once after authentication
    /// </summary>
    public async Task InitializeAsync()
    {
        if (_initialized)
            return;

        try
        {
            _logger.LogInformation("Fetching symbol list from cTrader...");

            var request = new ProtoOASymbolsListReq
            {
                CtidTraderAccountId = GetAccountId()
            };

            await _client.SendMessageAsync(request, (int)ProtoOAPayloadType.ProtoOaGetSymbolsReq);

            // Wait for response
            var response = await _client.WaitForResponseAsync<ProtoOASymbolsListRes>(
                (int)ProtoOAPayloadType.ProtoOaGetSymbolsRes,
                TimeSpan.FromSeconds(10));

            if (response == null)
            {
                _logger.LogError("Failed to receive symbol list from cTrader");
                return;
            }

            // Build mapping
            foreach (var symbol in response.Symbols)
            {
                _symbolMap[symbol.SymbolName] = symbol.SymbolId;
                _logger.LogDebug("Mapped symbol: {Name} -> {Id}", symbol.SymbolName, symbol.SymbolId);
            }

            _initialized = true;
            _logger.LogInformation("Symbol mapping initialized: {Count} symbols", _symbolMap.Count);
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

        // Try common variations
        var variations = new[]
        {
            assetName,
            assetName.Replace("/", ""),  // GBP/USD -> GBPUSD
            assetName.Replace(" ", ""),  // EUR USD -> EURUSD
            assetName.ToUpper()
        };

        foreach (var variant in variations)
        {
            if (_symbolMap.TryGetValue(variant, out symbolId))
                return symbolId;
        }

        throw new ArgumentException($"Unknown symbol: {assetName}");
    }

    /// <summary>
    /// Check if a symbol exists
    /// </summary>
    public bool HasSymbol(string assetName)
    {
        if (!_initialized)
            return false;

        return _symbolMap.ContainsKey(assetName) ||
               _symbolMap.ContainsKey(assetName.Replace("/", "")) ||
               _symbolMap.ContainsKey(assetName.ToUpper());
    }

    private long GetAccountId()
    {
        // TODO: Get from configuration
        return 2295141; // Demo account
    }
}
