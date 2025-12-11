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
        
        // Initialize with common Deriv/cTrader symbols
        // These are based on cTrader API documentation
        InitializeCommonSymbols();
    }

    /// <summary>
    /// Initialize with common Deriv/cTrader symbol mappings
    /// TODO: Fetch actual symbols from cTrader API once protobuf models are properly generated
    /// </summary>
    private void InitializeCommonSymbols()
    {
        // Forex pairs (no slash in cTrader naming)
        var forexPairs = new Dictionary<string, long>
        {
            { "EURUSD", 1 }, { "GBPUSD", 2 }, { "USDJPY", 3 }, { "USDCHF", 4 },
            { "AUDUSD", 5 }, { "USDCAD", 6 }, { "NZDUSD", 7 }, { "EURGBP", 8 },
            { "EURJPY", 9 }, { "GBPJPY", 10 }, { "EURCHF", 11 }, { "EURAUD", 12 },
            { "EURCAD", 13 }, { "GBPCHF", 14 }, { "GBPAUD", 15 }, { "GBPCAD", 16 },
            { "AUDJPY", 17 }, { "AUDNZD", 18 }, { "AUDCHF", 19 }, { "AUDCAD", 20 },
            { "NZDJPY", 21 }, { "CHFJPY", 22 }, { "CADJPY", 23 }, { "CADCHF", 24 },
            { "GBPNZD", 25 }, { "EURNZD", 26 }, { "NZDCHF", 27 }, { "NZDCAD", 28 }
        };

        // Deriv Synthetic Indices (only those that support binary trading)
        var syntheticIndices = new Dictionary<string, long>
        {
            // Volatility Indices - Regular
            { "Volatility 10 Index", 100 },
            { "Volatility 15 Index", 101 },
            { "Volatility 25 Index", 102 },
            { "Volatility 30 Index", 103 },
            { "Volatility 50 Index", 104 },
            { "Volatility 75 Index", 105 },
            { "Volatility 90 Index", 106 },
            { "Volatility 100 Index", 107 },
            
            // Volatility Indices - 1s (one-second)
            { "Volatility 10 (1s) Index", 110 },
            { "Volatility 15 (1s) Index", 111 },
            { "Volatility 25 (1s) Index", 112 },
            { "Volatility 50 (1s) Index", 113 },
            { "Volatility 75 (1s) Index", 114 },
            { "Volatility 100 (1s) Index", 115 },
            
            // Jump Indices
            { "Jump 10 Index", 200 },
            { "Jump 25 Index", 201 },
            { "Jump 50 Index", 202 },
            { "Jump 75 Index", 203 },
            { "Jump 100 Index", 204 },
            
            // Range Break Indices
            { "Range Break 100 Index", 300 },
            { "Range Break 200 Index", 301 },
            
            // Step Indices
            { "Step Index 100", 400 },
            { "Step Index 200", 401 },
            { "Step Index 300", 402 },
            { "Step Index 400", 403 },
            { "Step Index 500", 404 },
            
            // Daily Reset Indices
            { "Bear Market Index", 500 },
            { "Bull Market Index", 501 },
            
            // Alternate naming formats (uppercase, short forms)
            { "VOLATILITY10", 100 },
            { "VOLATILITY15", 101 },
            { "VOLATILITY25", 102 },
            { "VOLATILITY30", 103 },
            { "VOLATILITY50", 104 },
            { "VOLATILITY75", 105 },
            { "VOLATILITY90", 106 },
            { "VOLATILITY100", 107 },
            { "V10", 100 },
            { "V15", 101 },
            { "V25", 102 },
            { "V30", 103 },
            { "V50", 104 },
            { "V75", 105 },
            { "V90", 106 },
            { "V100", 107 },
            
            // Short forms for 1s indices
            { "V10_1S", 110 },
            { "V15_1S", 111 },
            { "V25_1S", 112 },
            { "V50_1S", 113 },
            { "V75_1S", 114 },
            { "V100_1S", 115 },
            
            // Jump short forms
            { "JUMP10", 200 },
            { "JUMP25", 201 },
            { "JUMP50", 202 },
            { "JUMP75", 203 },
            { "JUMP100", 204 },
            
            // Range Break short forms
            { "RANGEBREAK100", 300 },
            { "RANGEBREAK200", 301 },
            
            // Step short forms
            { "STEP100", 400 },
            { "STEP200", 401 },
            { "STEP300", 402 },
            { "STEP400", 403 },
            { "STEP500", 404 }
            
            // NOTE: Boom and Crash indices are NOT included as they don't support binary trading
        };

        foreach (var pair in forexPairs)
            _symbolMap[pair.Key] = pair.Value;

        foreach (var index in syntheticIndices)
            _symbolMap[index.Key] = index.Value;

        _initialized = true;
        _logger.LogInformation("Symbol service initialized with {Count} symbols (28 forex + {SyntheticCount} synthetic indices)", 
            _symbolMap.Count, syntheticIndices.Count);
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
            Console.WriteLine("\n📋 Available cTrader Symbols:");
            Console.WriteLine("════════════════════════════════════════");
            
            var forexSymbols = new List<string>();
            var volatilitySymbols = new List<string>();
            var otherSymbols = new List<string>();
            
            foreach (var symbol in response.Symbols)
            {
                _symbolMap[symbol.SymbolName] = symbol.SymbolId;
                _logger.LogDebug("Mapped symbol: {Name} -> {Id}", symbol.SymbolName, symbol.SymbolId);
                
                // Categorize symbols
                var name = symbol.SymbolName;
                if (name.Contains("V75") || name.Contains("V100") || name.Contains("Volatility") || 
                    name.Contains("1HZ") || name.Contains("VIX"))
                {
                    volatilitySymbols.Add(name);
                }
                else if (name.Length == 6 && !name.Contains("."))  // Likely forex pairs
                {
                    forexSymbols.Add(name);
                }
                else
                {
                    otherSymbols.Add(name);
                }
            }

            // Print categorized symbols
            if (forexSymbols.Any())
            {
                Console.WriteLine("\n💱 Forex Pairs ({0}):", forexSymbols.Count);
                foreach (var s in forexSymbols.OrderBy(x => x).Take(20))
                    Console.WriteLine($"   • {s}");
                if (forexSymbols.Count > 20)
                    Console.WriteLine($"   ... and {forexSymbols.Count - 20} more");
            }

            if (volatilitySymbols.Any())
            {
                Console.WriteLine("\n📊 Volatility Indices ({0}):", volatilitySymbols.Count);
                foreach (var s in volatilitySymbols.OrderBy(x => x))
                    Console.WriteLine($"   • {s}");
            }

            if (otherSymbols.Any())
            {
                Console.WriteLine("\n🔧 Other Assets ({0}):", otherSymbols.Count);
                foreach (var s in otherSymbols.OrderBy(x => x).Take(10))
                    Console.WriteLine($"   • {s}");
                if (otherSymbols.Count > 10)
                    Console.WriteLine($"   ... and {otherSymbols.Count - 10} more");
            }

            Console.WriteLine("\n════════════════════════════════════════");
            Console.WriteLine($"Total: {_symbolMap.Count} symbols available\n");

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
