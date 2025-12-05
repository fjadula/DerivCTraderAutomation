using System.Text.RegularExpressions;
using DerivCTrader.Application.Interfaces;
using Microsoft.Extensions.Logging;

namespace DerivCTrader.Infrastructure.ExpiryCalculation;

public class BinaryExpiryCalculator : IBinaryExpiryCalculator
{
    private readonly ILogger<BinaryExpiryCalculator> _logger;
    private static readonly HashSet<string> VolatilityIndices = new(StringComparer.OrdinalIgnoreCase)
    {
        "VIX10", "VIX25", "VIX50", "VIX75", "VIX100",
        "Volatility 10", "Volatility 25", "Volatility 50", "Volatility 75", "Volatility 100",
        "1HZ10V", "1HZ25V", "1HZ50V", "1HZ75V", "1HZ100V"
    };

    public BinaryExpiryCalculator(ILogger<BinaryExpiryCalculator> logger)
    {
        _logger = logger;
    }

    /// <summary>
    /// Interface implementation: Calculate expiry in minutes for a given signal type and asset
    /// </summary>
    public int CalculateExpiry(string signalType, string asset)
    {
        return CalculateExpiryMinutes(asset);
    }

    /// <summary>
    /// Calculate expiry duration intelligently based on:
    /// 1. Asset type (volatility indices are always fast)
    /// 2. Signal timeframe (H4 needs longer than 1M)
    /// 3. Pattern type (wedges/triangles need confirmation time)
    /// 
    /// Examples:
    /// - USDJPY H4 Rising Wedge → 480 min (8 hours)
    /// - EURUSD 15M Support → 30 min
    /// - VIX25 any timeframe → 15 min
    /// - GBP/CAD (no timeframe) → 21 min (minimum)
    /// </summary>
    public int CalculateExpiryMinutes(string asset, string? timeframe = null, string? pattern = null)
    {
        // 1. Volatility indices always use 15 minutes (fast-moving)
        if (IsVolatilityIndex(asset))
        {
            _logger.LogInformation("Asset {Asset} is volatility index - using 15 min expiry", asset);
            return 15;
        }

        // 2. Parse timeframe to get base minutes
        int baseMinutes = ParseTimeframeToMinutes(timeframe);

        // 3. Adjust for pattern type (patterns need confirmation candles)
        if (!string.IsNullOrEmpty(pattern))
        {
            if (pattern.Contains("wedge", StringComparison.OrdinalIgnoreCase) ||
                pattern.Contains("triangle", StringComparison.OrdinalIgnoreCase) ||
                pattern.Contains("pennant", StringComparison.OrdinalIgnoreCase))
            {
                // Patterns need multiple candles to confirm - multiply by 2-3x
                baseMinutes = (int)(baseMinutes * 2.5);
                _logger.LogInformation("Pattern '{Pattern}' detected - extending expiry by 2.5x", pattern);
            }
        }

        // 4. Apply absolute bounds
        const int MinimumExpiry = 21;   // Never less than 21 minutes
        const int MaximumExpiry = 1440; // Never more than 24 hours (1 day)

        baseMinutes = Math.Max(baseMinutes, MinimumExpiry);
        baseMinutes = Math.Min(baseMinutes, MaximumExpiry);

        _logger.LogInformation(
            "Expiry calculated for {Asset}: {Minutes} min (Timeframe: {Timeframe}, Pattern: {Pattern})", 
            asset, baseMinutes, timeframe ?? "None", pattern ?? "None");

        return baseMinutes;
    }

    /// <summary>
    /// Parse timeframe string (e.g., "H4", "15M", "D1") into base minutes
    /// Logic: Use 2 candles as baseline (to allow pattern development)
    /// </summary>
    private int ParseTimeframeToMinutes(string? timeframe)
    {
        if (string.IsNullOrEmpty(timeframe))
        {
            _logger.LogWarning("No timeframe provided, using default 30 minutes");
            return 30;  // Default for signals without timeframe
        }

        try
        {
            // Match formats: "1M", "5M", "15M", "30M", "1H", "H1", "4H", "H4", "D1"
            var match = Regex.Match(timeframe, @"(\d+)([MHD])", RegexOptions.IgnoreCase);
            
            if (!match.Success)
            {
                // Try reversed format: H4, M15, D1
                match = Regex.Match(timeframe, @"([MHD])(\d+)", RegexOptions.IgnoreCase);
            }

            if (!match.Success)
            {
                _logger.LogWarning("Could not parse timeframe '{Timeframe}', using default 30 min", timeframe);
                return 30;
            }

            // Extract number and unit
            int number = int.Parse(match.Groups[1].Value.All(char.IsDigit) 
                ? match.Groups[1].Value 
                : match.Groups[2].Value);
            
            string unit = (match.Groups[1].Value.All(char.IsLetter) 
                ? match.Groups[1].Value 
                : match.Groups[2].Value).ToUpper();

            // Calculate base minutes (2 candles minimum for pattern confirmation)
            int multiplier = 2; // Wait for 2 candles
            
            int baseMinutes = unit switch
            {
                "M" => number * multiplier,              // 15M → 30 min (2 candles)
                "H" => number * 60 * multiplier,         // H4 → 480 min (8 hours = 2 candles)
                "D" => number * 1440 / 2,                // D1 → 720 min (12 hours = half day)
                _ => 30
            };

            _logger.LogDebug("Parsed timeframe '{Timeframe}': {Number}{Unit} → {Minutes} min base", 
                timeframe, number, unit, baseMinutes);

            return baseMinutes;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error parsing timeframe '{Timeframe}'", timeframe);
            return 30;
        }
    }

    /// <summary>
    /// Calculate expected expiry timestamp for logging purposes
    /// </summary>
    public DateTime CalculateExpiryTimestamp(string asset, string? timeframe = null, string? pattern = null)
    {
        var expiryMinutes = CalculateExpiryMinutes(asset, timeframe, pattern);
        return DateTime.UtcNow.AddMinutes(expiryMinutes);
    }

    /// <summary>
    /// Get expiry display string (e.g., "15M", "30M", "480M", "8H")
    /// </summary>
    public string GetExpiryDisplay(string asset, string? timeframe = null, string? pattern = null)
    {
        var minutes = CalculateExpiryMinutes(asset, timeframe, pattern);
        
        // Display in hours if >= 60 minutes
        if (minutes >= 60)
        {
            var hours = minutes / 60.0;
            return hours % 1 == 0 
                ? $"{(int)hours}H" 
                : $"{hours:F1}H";
        }
        
        return $"{minutes}M";
    }

    private bool IsVolatilityIndex(string asset)
    {
        if (string.IsNullOrWhiteSpace(asset))
            return false;

        // Direct match
        if (VolatilityIndices.Contains(asset))
            return true;

        // Partial match for variations
        return VolatilityIndices.Any(vi => asset.Contains(vi, StringComparison.OrdinalIgnoreCase));
    }
}
