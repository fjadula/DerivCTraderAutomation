/*namespace DerivCTrader.Infrastructure.Deriv.Models;

/// <summary>
/// Result of placing a binary option trade
/// </summary>
public class DerivTradeResult
{
    public bool Success { get; set; }
    public string? ContractId { get; set; }
    public decimal PurchasePrice { get; set; }
    public decimal? Payout { get; set; }
    public DateTime PurchaseTime { get; set; }
    public string? ErrorMessage { get; set; }
    public string? ErrorCode { get; set; }
}

/// <summary>
/// Contract outcome after expiry
/// </summary>
public class DerivContractOutcome
{
    public string ContractId { get; set; } = string.Empty;
    public string Status { get; set; } = string.Empty; // "Win" or "Loss"
    public decimal Profit { get; set; }
    public decimal? ExitSpot { get; set; }
    public DateTime? SettledAt { get; set; }
}

/// <summary>
/// Deriv configuration
/// </summary>
public class DerivConfig
{
    public string AppId { get; set; } = string.Empty;
    public string Token { get; set; } = string.Empty;
    public string WebSocketUrl { get; set; } = string.Empty;
}

/// <summary>
/// Asset symbol mapping (Telegram → Deriv)
/// </summary>
public static class DerivAssetMapper
{
    private static readonly Dictionary<string, string> AssetMap = new()
    {
        // Major Forex Pairs
        { "EURUSD", "frxEURUSD" },
        { "GBPUSD", "frxGBPUSD" },
        { "USDJPY", "frxUSDJPY" },
        { "AUDUSD", "frxAUDUSD" },
        { "USDCAD", "frxUSDCAD" },
        { "NZDUSD", "frxNZDUSD" },
        { "EURGBP", "frxEURGBP" },
        { "EURJPY", "frxEURJPY" },
        { "GBPJPY", "frxGBPJPY" },
        { "AUDJPY", "frxAUDJPY" },
        
        // Volatility Indices
        { "V10", "R_10" },
        { "V25", "R_25" },
        { "V50", "R_50" },
        { "V75", "R_75" },
        { "V100", "R_100" },
        { "VOL10", "R_10" },
        { "VOL25", "R_25" },
        { "VOL50", "R_50" },
        { "VOL75", "R_75" },
        { "VOL100", "R_100" },
        { "VOLATILITY10", "R_10" },
        { "VOLATILITY25", "R_25" },
        { "VOLATILITY50", "R_50" },
        { "VOLATILITY75", "R_75" },
        { "VOLATILITY100", "R_100" },
        
        // Crash/Boom
        { "CRASH500", "CRASH500" },
        { "CRASH1000", "CRASH1000" },
        { "BOOM500", "BOOM500" },
        { "BOOM1000", "BOOM1000" }
    };

    public static string ToDerivSymbol(string telegramAsset)
    {
        var normalized = telegramAsset.ToUpper().Replace(" ", "").Replace("-", "");

        if (AssetMap.TryGetValue(normalized, out var derivSymbol))
        {
            return derivSymbol;
        }

        // If not found, return as-is and log warning
        Console.WriteLine($"⚠️ Unknown asset: {telegramAsset}, using as-is");
        return telegramAsset;
    }
}*/