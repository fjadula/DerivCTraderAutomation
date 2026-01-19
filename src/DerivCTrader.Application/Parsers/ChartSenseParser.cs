using System.Security.Cryptography;
using System.Text.RegularExpressions;
using DerivCTrader.Application.Interfaces;
using DerivCTrader.Domain.Entities;
using DerivCTrader.Domain.Enums;
using Microsoft.Extensions.Logging;

namespace DerivCTrader.Application.Parsers;

/// <summary>
/// Parser for ChartSense channel - IMAGE-ONLY signals
/// Extracts trading information from chart images using OCR and line detection.
///
/// Channel ID: -1001200022443
/// Signal Format: Images only (no text)
/// Assets: Forex only (EURUSD, GBPUSD, XAUUSD, etc.)
/// </summary>
public class ChartSenseParser : ISignalParser
{
    private readonly ILogger<ChartSenseParser> _logger;
    private static readonly string ChartSenseChannelId = "-1001200022443";

    // Supported forex pairs
    private static readonly HashSet<string> SupportedAssets = new(StringComparer.OrdinalIgnoreCase)
    {
        "EURUSD", "GBPUSD", "USDJPY", "USDCHF", "AUDUSD", "USDCAD", "NZDUSD",
        "EURGBP", "EURJPY", "GBPJPY", "AUDJPY", "CADJPY", "CHFJPY",
        "EURAUD", "EURCHF", "EURCAD", "EURNZD",
        "GBPAUD", "GBPCAD", "GBPCHF", "GBPNZD",
        "AUDCAD", "AUDCHF", "AUDNZD",
        "CADCHF", "NZDCAD", "NZDCHF",
        "XAUUSD", "GOLD"  // Gold
    };

    // Timeframe patterns
    private static readonly Regex TimeframePattern = new(
        @"\b(M15|M30|H1|H2|H4|D1|1H|2H|4H|15M|30M|Daily)\b",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    // Direction patterns
    private static readonly Regex DirectionPattern = new(
        @"\b(BUY|SELL|LONG|SHORT|BULLISH|BEARISH)\b",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    // Pattern names
    private static readonly Regex PatternNamePattern = new(
        @"\b(TRENDLINE|WEDGE|TRIANGLE|RECTANGLE|CHANNEL|BREAKOUT|SUPPORT|RESISTANCE|FLAG|PENNANT)\b",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    public ChartSenseParser(ILogger<ChartSenseParser> logger)
    {
        _logger = logger;
    }

    public bool CanParse(string providerChannelId)
    {
        var canParse = providerChannelId == ChartSenseChannelId;
        _logger.LogInformation("ChartSenseParser.CanParse({Channel}): {Result}", providerChannelId, canParse);
        return canParse;
    }

    public async Task<ParsedSignal?> ParseAsync(string message, string providerChannelId, byte[]? imageData = null)
    {
        try
        {
            _logger.LogInformation("ChartSenseParser: Starting parse attempt");

            // ChartSense is IMAGE-ONLY - must have image data
            if (imageData == null || imageData.Length == 0)
            {
                _logger.LogWarning("ChartSenseParser: No image data provided - ChartSense requires images");
                return null;
            }

            _logger.LogInformation("ChartSenseParser: Image data received ({Size} bytes)", imageData.Length);

            // The message parameter contains OCR'd text from the image (done by TelegramSignalScraperService)
            if (string.IsNullOrWhiteSpace(message))
            {
                _logger.LogWarning("ChartSenseParser: No OCR text extracted from image");
                return null;
            }

            _logger.LogInformation("ChartSenseParser: OCR text: {Text}", message);

            // 1. Extract asset symbol
            var asset = ExtractAsset(message);
            if (string.IsNullOrEmpty(asset))
            {
                _logger.LogWarning("ChartSenseParser: Could not extract asset from OCR text");
                return null;
            }

            // 2. Extract direction
            var direction = ExtractDirection(message);
            if (!direction.HasValue)
            {
                _logger.LogWarning("ChartSenseParser: Could not extract direction from OCR text");
                return null;
            }

            // 3. Extract timeframe
            var timeframe = ExtractTimeframe(message);
            if (string.IsNullOrEmpty(timeframe))
            {
                _logger.LogInformation("ChartSenseParser: No timeframe found, defaulting to H1");
                timeframe = "H1";
            }

            // 4. Extract pattern name
            var patternName = ExtractPatternName(message);
            if (string.IsNullOrEmpty(patternName))
            {
                patternName = "Unknown";
            }

            // 5. Classify pattern
            var classification = ClassifyPattern(patternName);

            // 6. Calculate image hash for duplicate detection
            var imageHash = ComputeImageHash(imageData);

            _logger.LogInformation(
                "ChartSenseParser: Parsed - Asset={Asset}, Direction={Direction}, Timeframe={Timeframe}, Pattern={Pattern}, Classification={Classification}",
                asset, direction, timeframe, patternName, classification);

            // Note: Entry price derivation from chart lines will be done in Phase 3 (ChartImageAnalyzer)
            // For now, return null entry - ChartSenseSetupService will handle market-based entry

            return new ParsedSignal
            {
                ProviderChannelId = providerChannelId,
                ProviderName = "ChartSense",
                Asset = NormalizeAsset(asset),
                Direction = direction.Value,
                EntryPrice = null,  // Will be derived from chart analysis in Phase 3
                StopLoss = null,    // Not used for ChartSense (per design decision)
                TakeProfit = null,  // Not used for ChartSense (per design decision)
                Timeframe = NormalizeTimeframe(timeframe),
                Pattern = patternName,
                SignalType = SignalType.Image,
                ReceivedAt = DateTime.UtcNow,
                RawMessage = message
            };
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error parsing ChartSense signal");
            return null;
        }
    }

    private string? ExtractAsset(string text)
    {
        // Look for known forex pairs in the OCR text
        foreach (var asset in SupportedAssets)
        {
            // Match with or without slash (EURUSD or EUR/USD)
            var patterns = new[]
            {
                asset,
                asset.Insert(3, "/"),  // EUR/USD
                asset.Insert(3, " ")   // EUR USD
            };

            foreach (var pattern in patterns)
            {
                if (text.Contains(pattern, StringComparison.OrdinalIgnoreCase))
                {
                    return asset;
                }
            }
        }

        // Try regex for any 6-letter uppercase combo that looks like a pair
        var pairMatch = Regex.Match(text, @"\b([A-Z]{3})/?([A-Z]{3})\b", RegexOptions.IgnoreCase);
        if (pairMatch.Success)
        {
            var pair = pairMatch.Groups[1].Value.ToUpper() + pairMatch.Groups[2].Value.ToUpper();
            if (SupportedAssets.Contains(pair))
            {
                return pair;
            }
        }

        return null;
    }

    private TradeDirection? ExtractDirection(string text)
    {
        var match = DirectionPattern.Match(text);
        if (!match.Success)
            return null;

        var dirText = match.Groups[1].Value.ToUpper();
        return dirText switch
        {
            "BUY" or "LONG" or "BULLISH" => TradeDirection.Buy,
            "SELL" or "SHORT" or "BEARISH" => TradeDirection.Sell,
            _ => null
        };
    }

    private string? ExtractTimeframe(string text)
    {
        var match = TimeframePattern.Match(text);
        return match.Success ? match.Groups[1].Value.ToUpper() : null;
    }

    private string? ExtractPatternName(string text)
    {
        var match = PatternNamePattern.Match(text);
        if (!match.Success)
            return null;

        // Capitalize first letter
        var pattern = match.Groups[1].Value;
        return char.ToUpper(pattern[0]) + pattern.Substring(1).ToLower();
    }

    private ChartSensePatternClassification ClassifyPattern(string? patternName)
    {
        if (string.IsNullOrEmpty(patternName))
            return ChartSensePatternClassification.Reaction; // Default

        var upper = patternName.ToUpper();

        // Breakout patterns
        if (upper.Contains("BREAKOUT") ||
            upper.Contains("BREAK") ||
            upper.Contains("FLAG") ||
            upper.Contains("PENNANT"))
        {
            return ChartSensePatternClassification.Breakout;
        }

        // Reaction patterns (default)
        return ChartSensePatternClassification.Reaction;
    }

    private static string NormalizeAsset(string asset)
    {
        // Normalize GOLD to XAUUSD
        if (asset.Equals("GOLD", StringComparison.OrdinalIgnoreCase))
            return "XAUUSD";

        return asset.ToUpper().Replace("/", "").Replace(" ", "");
    }

    private static string NormalizeTimeframe(string timeframe)
    {
        // Normalize various formats to standard
        var upper = timeframe.ToUpper();
        return upper switch
        {
            "1H" => "H1",
            "2H" => "H2",
            "4H" => "H4",
            "15M" => "M15",
            "30M" => "M30",
            "DAILY" => "D1",
            _ => upper
        };
    }

    private static string ComputeImageHash(byte[] imageData)
    {
        using var sha256 = SHA256.Create();
        var hashBytes = sha256.ComputeHash(imageData);
        return Convert.ToHexString(hashBytes);
    }
}
