namespace DerivCTrader.Infrastructure.Deriv;

/// <summary>
/// Result of placing a binary option trade
/// </summary>
public class DerivTradeResult
{
    public bool Success { get; set; }
    public string? ContractId { get; set; }
    public decimal BuyPrice { get; set; }
    public decimal PurchasePrice { get; set; }
    public decimal? Payout { get; set; }
    public DateTime? PurchaseTime { get; set; }
    public string? ErrorMessage { get; set; }
    public string? ErrorCode { get; set; }
}

/// <summary>
/// Outcome of a settled contract
/// </summary>
public class DerivContractOutcome
{
    public string? ContractId { get; set; }
    public bool IsWin { get; set; }
    public string? Status { get; set; }
    public decimal Profit { get; set; } // Positive for win, negative for loss
    public decimal? Payout { get; set; }
    public decimal? ExitSpot { get; set; }
    public DateTime? SettledAt { get; set; }
}

/// <summary>
/// Deriv API configuration
/// </summary>
public class DerivConfig
{
    public string WebSocketUrl { get; set; } = string.Empty;
    public string ApiToken { get; set; } = string.Empty;
    public string Token { get; set; } = string.Empty;
    public string AppId { get; set; } = string.Empty;
}

/// <summary>
/// Maps trading assets to Deriv symbols
/// </summary>
public static class DerivAssetMapper
{
    private static readonly Dictionary<string, string> AssetMap = new(StringComparer.OrdinalIgnoreCase)
    {
        // ========================================
        // FOREX PAIRS - ONLY these 25 pairs are available on Deriv for binary options
        // Verified: January 2026
        // Format: frx prefix (e.g., frxEURUSD)
        // DO NOT add pairs not in this list - they will fail on Deriv API
        // ========================================

        // Major Pairs (14 pairs)
        ["AUDJPY"] = "frxAUDJPY",       // AUD/JPY
        ["AUD/JPY"] = "frxAUDJPY",
        ["AUDUSD"] = "frxAUDUSD",       // AUD/USD
        ["AUD/USD"] = "frxAUDUSD",
        ["EURAUD"] = "frxEURAUD",       // EUR/AUD
        ["EUR/AUD"] = "frxEURAUD",
        ["EURCAD"] = "frxEURCAD",       // EUR/CAD
        ["EUR/CAD"] = "frxEURCAD",
        ["EURCHF"] = "frxEURCHF",       // EUR/CHF
        ["EUR/CHF"] = "frxEURCHF",
        ["EURGBP"] = "frxEURGBP",       // EUR/GBP
        ["EUR/GBP"] = "frxEURGBP",
        ["EURJPY"] = "frxEURJPY",       // EUR/JPY
        ["EUR/JPY"] = "frxEURJPY",
        ["EURUSD"] = "frxEURUSD",       // EUR/USD
        ["EUR/USD"] = "frxEURUSD",
        ["GBPAUD"] = "frxGBPAUD",       // GBP/AUD
        ["GBP/AUD"] = "frxGBPAUD",
        ["GBPJPY"] = "frxGBPJPY",       // GBP/JPY
        ["GBP/JPY"] = "frxGBPJPY",
        ["GBPUSD"] = "frxGBPUSD",       // GBP/USD
        ["GBP/USD"] = "frxGBPUSD",
        ["USDCAD"] = "frxUSDCAD",       // USD/CAD
        ["USD/CAD"] = "frxUSDCAD",
        ["USDCHF"] = "frxUSDCHF",       // USD/CHF
        ["USD/CHF"] = "frxUSDCHF",
        ["USDJPY"] = "frxUSDJPY",       // USD/JPY
        ["USD/JPY"] = "frxUSDJPY",

        // Minor Pairs (11 pairs)
        ["AUDCAD"] = "frxAUDCAD",       // AUD/CAD
        ["AUD/CAD"] = "frxAUDCAD",
        ["AUDCHF"] = "frxAUDCHF",       // AUD/CHF
        ["AUD/CHF"] = "frxAUDCHF",
        ["AUDNZD"] = "frxAUDNZD",       // AUD/NZD
        ["AUD/NZD"] = "frxAUDNZD",
        ["EURNZD"] = "frxEURNZD",       // EUR/NZD
        ["EUR/NZD"] = "frxEURNZD",
        ["GBPCAD"] = "frxGBPCAD",       // GBP/CAD
        ["GBP/CAD"] = "frxGBPCAD",
        ["GBPCHF"] = "frxGBPCHF",       // GBP/CHF
        ["GBP/CHF"] = "frxGBPCHF",
        ["GBPNZD"] = "frxGBPNZD",       // GBP/NZD
        ["GBP/NZD"] = "frxGBPNZD",
        ["NZDJPY"] = "frxNZDJPY",       // NZD/JPY
        ["NZD/JPY"] = "frxNZDJPY",
        ["NZDUSD"] = "frxNZDUSD",       // NZD/USD
        ["NZD/USD"] = "frxNZDUSD",
        ["USDMXN"] = "frxUSDMXN",       // USD/MXN
        ["USD/MXN"] = "frxUSDMXN",
        ["USDPLN"] = "frxUSDPLN",       // USD/PLN
        ["USD/PLN"] = "frxUSDPLN",

        // Volatility Indices (various naming conventions)
        ["Volatility 10 Index"] = "R_10",
        ["Volatility 25 Index"] = "R_25",
        ["Volatility 50 Index"] = "R_50",
        ["Volatility 75 Index"] = "R_75",
        ["Volatility 100 Index"] = "R_100",
        ["Volatility 10"] = "R_10",
        ["Volatility 25"] = "R_25",
        ["Volatility 50"] = "R_50",
        ["Volatility 75"] = "R_75",
        ["Volatility 100"] = "R_100",
        ["VOLATILITY10"] = "R_10",
        ["VOLATILITY25"] = "R_25",
        ["VOLATILITY50"] = "R_50",
        ["VOLATILITY75"] = "R_75",
        ["VOLATILITY100"] = "R_100",
        ["VOLATILITY 10"] = "R_10",
        ["VOLATILITY 25"] = "R_25",
        ["VOLATILITY 50"] = "R_50",
        ["VOLATILITY 75"] = "R_75",
        ["VOLATILITY 100"] = "R_100",
        ["V10"] = "R_10",
        ["V25"] = "R_25",
        ["V50"] = "R_50",
        ["V75"] = "R_75",
        ["V100"] = "R_100",
        ["Vol 10"] = "R_10",
        ["Vol 25"] = "R_25",
        ["Vol 50"] = "R_50",
        ["Vol 75"] = "R_75",
        ["Vol 100"] = "R_100",
        ["R_10"] = "R_10",
        ["R_25"] = "R_25",
        ["R_50"] = "R_50",
        ["R_75"] = "R_75",
        ["R_100"] = "R_100",

        // Volatility Indices (1-second tick)
        ["Volatility 10 (1s) Index"] = "1HZ10V",
        ["Volatility 25 (1s) Index"] = "1HZ25V",
        ["Volatility 50 (1s) Index"] = "1HZ50V",
        ["Volatility 75 (1s) Index"] = "1HZ75V",
        ["Volatility 100 (1s) Index"] = "1HZ100V",
        ["Volatility 150 (1s) Index"] = "1HZ150V",
        ["Volatility 200 (1s) Index"] = "1HZ200V",
        ["Volatility 250 (1s) Index"] = "1HZ250V",
        ["Volatility 300 (1s) Index"] = "1HZ300V",
        ["Volatility 10 1s"] = "1HZ10V",
        ["Volatility 25 1s"] = "1HZ25V",
        ["Volatility 50 1s"] = "1HZ50V",
        ["Volatility 75 1s"] = "1HZ75V",
        ["Volatility 100 1s"] = "1HZ100V",
        ["Volatility 150 1s"] = "1HZ150V",
        ["Volatility 200 1s"] = "1HZ200V",
        ["Volatility 250 1s"] = "1HZ250V",
        ["Volatility 300 1s"] = "1HZ300V",
        ["1HZ10V"] = "1HZ10V",
        ["1HZ25V"] = "1HZ25V",
        ["1HZ50V"] = "1HZ50V",
        ["1HZ75V"] = "1HZ75V",
        ["1HZ100V"] = "1HZ100V",
        ["1HZ150V"] = "1HZ150V",
        ["1HZ200V"] = "1HZ200V",
        ["1HZ250V"] = "1HZ250V",
        ["1HZ300V"] = "1HZ300V",

        // Crash/Boom Indices
        ["Crash 300 Index"] = "CRASH300N",
        ["Crash 500 Index"] = "CRASH500",
        ["Crash 1000 Index"] = "CRASH1000",
        ["Boom 300 Index"] = "BOOM300N",
        ["Boom 500 Index"] = "BOOM500",
        ["Boom 1000 Index"] = "BOOM1000",
        ["Crash 300"] = "CRASH300N",
        ["Crash 500"] = "CRASH500",
        ["Crash 1000"] = "CRASH1000",
        ["Boom 300"] = "BOOM300N",
        ["Boom 500"] = "BOOM500",
        ["Boom 1000"] = "BOOM1000",
        ["CRASH300"] = "CRASH300N",
        ["CRASH500"] = "CRASH500",
        ["CRASH1000"] = "CRASH1000",
        ["BOOM300"] = "BOOM300N",
        ["BOOM500"] = "BOOM500",
        ["BOOM1000"] = "BOOM1000",

        // Step Index
        ["Step Index"] = "stpRNG",
        ["STEP INDEX"] = "stpRNG",
        ["stpRNG"] = "stpRNG",

        // Jump Indices
        ["Jump 10 Index"] = "JD10",
        ["Jump 25 Index"] = "JD25",
        ["Jump 50 Index"] = "JD50",
        ["Jump 75 Index"] = "JD75",
        ["Jump 100 Index"] = "JD100",
        ["Jump 10"] = "JD10",
        ["Jump 25"] = "JD25",
        ["Jump 50"] = "JD50",
        ["Jump 75"] = "JD75",
        ["Jump 100"] = "JD100",
        ["JD10"] = "JD10",
        ["JD25"] = "JD25",
        ["JD50"] = "JD50",
        ["JD75"] = "JD75",
        ["JD100"] = "JD100",

        // Bear/Bull Market Indices
        ["Bear Market Index"] = "RDBEAR",
        ["Bull Market Index"] = "RDBULL",
        ["BEAR MARKET"] = "RDBEAR",
        ["BULL MARKET"] = "RDBULL",
        ["RDBEAR"] = "RDBEAR",
        ["RDBULL"] = "RDBULL",

        // Range Break Indices
        ["Range Break 100 Index"] = "RDBULL",
        ["Range Break 200 Index"] = "RDBEAR"
    };

    /// <summary>
    /// Convert asset name to Deriv symbol
    /// </summary>
    public static string ToDerivSymbol(string asset)
    {
        if (string.IsNullOrWhiteSpace(asset))
            throw new ArgumentException("Asset cannot be empty", nameof(asset));

        // Try exact match first
        if (AssetMap.TryGetValue(asset, out var symbol))
            return symbol;

        // Try partial match (e.g., "EURUSD" in "EURUSD M5")
        var match = AssetMap.FirstOrDefault(kvp =>
            asset.Contains(kvp.Key, StringComparison.OrdinalIgnoreCase));

        if (match.Key != null)
            return match.Value;

        // Fallback: Remove slashes/spaces and check if it's a forex pair
        string cleanedAsset = asset.Replace("/", "").Replace(" ", "").Trim().ToUpper();

        // If cleaned asset looks like a forex pair (6 uppercase letters)
        // format it with frx prefix: EURUSD -> frxEURUSD
        if (cleanedAsset.Length == 6 && cleanedAsset.All(char.IsLetter))
        {
            return $"frx{cleanedAsset}";
        }

        // If no match, return as-is (might be a volatility index or other asset)
        return asset.Trim();
    }

    /// <summary>
    /// Check if asset is a forex pair
    /// </summary>
    public static bool IsForexPair(string asset)
    {
        var derivSymbol = ToDerivSymbol(asset);
        // Forex pairs use frx prefix: frxEURUSD, frxGBPUSD, etc.
        return derivSymbol.StartsWith("frx", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Check if asset is a volatility index
    /// </summary>
    public static bool IsVolatilityIndex(string asset)
    {
        var derivSymbol = ToDerivSymbol(asset);
        return derivSymbol.StartsWith("R_", StringComparison.OrdinalIgnoreCase);
    }
}