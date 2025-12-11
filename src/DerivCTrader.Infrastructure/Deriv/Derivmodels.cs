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
        // Major Forex Pairs (both with and without slashes)
        ["EURUSD"] = "frxEURUSD",
        ["EUR/USD"] = "frxEURUSD",
        ["GBPUSD"] = "frxGBPUSD",
        ["GBP/USD"] = "frxGBPUSD",
        ["USDJPY"] = "frxUSDJPY",
        ["USD/JPY"] = "frxUSDJPY",
        ["AUDUSD"] = "frxAUDUSD",
        ["AUD/USD"] = "frxAUDUSD",
        ["USDCAD"] = "frxUSDCAD",
        ["USD/CAD"] = "frxUSDCAD",
        ["USDCHF"] = "frxUSDCHF",
        ["USD/CHF"] = "frxUSDCHF",
        ["NZDUSD"] = "frxNZDUSD",
        ["NZD/USD"] = "frxNZDUSD",

        // Cross Pairs (both with and without slashes)
        ["EURJPY"] = "frxEURJPY",
        ["EUR/JPY"] = "frxEURJPY",
        ["EURGBP"] = "frxEURGBP",
        ["EUR/GBP"] = "frxEURGBP",
        ["GBPJPY"] = "frxGBPJPY",
        ["GBP/JPY"] = "frxGBPJPY",
        ["AUDJPY"] = "frxAUDJPY",
        ["AUD/JPY"] = "frxAUDJPY",
        ["EURAUD"] = "frxEURAUD",
        ["EUR/AUD"] = "frxEURAUD",
        ["EURCAD"] = "frxEURCAD",
        ["EUR/CAD"] = "frxEURCAD",

        // Volatility Indices
        ["Volatility 10 Index"] = "R_10",
        ["Volatility 25 Index"] = "R_25",
        ["Volatility 50 Index"] = "R_50",
        ["Volatility 75 Index"] = "R_75",
        ["Volatility 100 Index"] = "R_100",
        ["VOLATILITY10"] = "R_10",
        ["VOLATILITY25"] = "R_25",
        ["VOLATILITY50"] = "R_50",
        ["VOLATILITY75"] = "R_75",
        ["VOLATILITY100"] = "R_100",
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

        // Crash/Boom Indices
        ["Crash 300 Index"] = "CRASH300N",
        ["Crash 500 Index"] = "CRASH500",
        ["Crash 1000 Index"] = "CRASH1000",
        ["Boom 300 Index"] = "BOOM300N",
        ["Boom 500 Index"] = "BOOM500",
        ["Boom 1000 Index"] = "BOOM1000",

        // Step Indices
        ["Step Index"] = "stpRNG",

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
        
        // If cleaned asset looks like a forex pair (6-7 uppercase letters)
        // and doesn't have a prefix, add frx
        if (cleanedAsset.Length >= 6 && cleanedAsset.Length <= 7 && 
            cleanedAsset.All(char.IsLetter) &&
            !cleanedAsset.StartsWith("frx", StringComparison.OrdinalIgnoreCase))
        {
            return $"frx{cleanedAsset}";
        }

        // If no match, return cleaned asset
        return cleanedAsset;
    }

    /// <summary>
    /// Check if asset is a forex pair
    /// </summary>
    public static bool IsForexPair(string asset)
    {
        var derivSymbol = ToDerivSymbol(asset);
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