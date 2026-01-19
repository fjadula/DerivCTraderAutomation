using System.Text.RegularExpressions;
using DerivCTrader.Application.Interfaces;
using DerivCTrader.Domain.Entities;
using DerivCTrader.Domain.Enums;
using Microsoft.Extensions.Logging;

namespace DerivCTrader.Application.Parsers;

/// <summary>
/// Parser for Synthetic Indices Trader channel
/// Format: "Sell : Volatility 75 Index Zone : 43130.00 - 43750.00 TP1 : 42570.00 SL : 44240.00"
/// </summary>
public class SyntheticIndicesParser : ISignalParser
{
    private readonly ILogger<SyntheticIndicesParser> _logger;

    // Known channel IDs for Synthetic Indices providers (keep minimal + explicit)
    private static readonly HashSet<string> SupportedChannelIds = new(StringComparer.Ordinal)
    {
        "-1003204276456", // SyntheticIndicesTrader (legacy)
        "-1003375573206", // SyntheticIndicesTrader (new)
        "-1003046812685"  // VIP KNIGHTS
    };

    public SyntheticIndicesParser(ILogger<SyntheticIndicesParser> logger)
    {
        _logger = logger;
    }

    public bool CanParse(string providerChannelId)
    {
        var canParse = SupportedChannelIds.Contains(providerChannelId);

        _logger.LogInformation("SyntheticIndicesParser.CanParse({Channel}): {Result}", providerChannelId, canParse);
        return canParse;
    }

    public async Task<ParsedSignal?> ParseAsync(string message, string providerChannelId, byte[]? imageData = null)
    {
        try
        {
            _logger.LogInformation("SyntheticIndicesParser: Starting parse attempt");
            _logger.LogInformation("SyntheticIndicesParser: Message: {Message}", message);

            var parsed = TryParseZoneSignal(message, providerChannelId)
                      ?? TryParseVipKnightsSignal(message, providerChannelId)
                      ?? TryParseMarketSignal(message, providerChannelId);
            if (parsed == null)
            {
                _logger.LogWarning("SyntheticIndicesParser: No supported pattern matched");
                return null;
            }

            _logger.LogInformation(
                "SyntheticIndicesParser: Parsed - {Asset} {Direction} Entry: {Entry} TP1: {TP1} TP2: {TP2} TP3: {TP3} SL: {SL}",
                parsed.Asset,
                parsed.Direction,
                parsed.EntryPrice,
                parsed.TakeProfit,
                parsed.TakeProfit2,
                parsed.TakeProfit3,
                parsed.StopLoss);

            return parsed;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error parsing Synthetic Indices signal");
            return null;
        }
    }

    private ParsedSignal? TryParseZoneSignal(string message, string providerChannelId)
    {
        // Example:
        // buy : Volatility 50 Index
        // Zone : 137.50 - 136.00
        // TP1 : 138.50
        // TP2 : 139.70
        // TP3 : 141.60
        // SL : 135.00

        var pattern = @"(Buy|Sell)\s*:\s*(.+?)\s*(?:Zone|Entry)\s*:\s*([\d.]+)\s*-\s*([\d.]+)";
        var match = Regex.Match(message, pattern, RegexOptions.IgnoreCase | RegexOptions.Singleline);
        if (!match.Success)
            return null;

        var direction = match.Groups[1].Value.Equals("BUY", StringComparison.OrdinalIgnoreCase)
            ? TradeDirection.Buy
            : TradeDirection.Sell;

        var rawAsset = match.Groups[2].Value.Trim();
        var asset = CleanupAsset(rawAsset);

        var entryMin = decimal.Parse(match.Groups[3].Value, System.Globalization.CultureInfo.InvariantCulture);
        var entryMax = decimal.Parse(match.Groups[4].Value, System.Globalization.CultureInfo.InvariantCulture);
        // Always use the midpoint of the zone as the EntryPrice for cTrader
        var entry = (entryMin + entryMax) / 2m;

        var (tp1, tp2, tp3, tp4) = ExtractTakeProfits(message);
        var sl = ExtractStopLoss(message);

        // Fallback: if TP1 or SL is missing, set 200 points away from entry
        decimal pointOffset = 200m;
        decimal? fallbackTp = tp1;
        decimal? fallbackSl = sl;
        if (!tp1.HasValue)
            fallbackTp = direction == TradeDirection.Buy ? entry + pointOffset : entry - pointOffset;
        if (!sl.HasValue)
            fallbackSl = direction == TradeDirection.Buy ? entry - pointOffset : entry + pointOffset;

        return new ParsedSignal
        {
            ProviderChannelId = providerChannelId,
            ProviderName = "SyntheticIndicesTrader",
            Asset = asset,
            Direction = direction,
            EntryPrice = entry, // Always set to zone midpoint
            TakeProfit = fallbackTp,
            TakeProfit2 = tp2,
            TakeProfit3 = tp3,
            TakeProfit4 = tp4,
            StopLoss = fallbackSl,
            SignalType = SignalType.Text,
            ReceivedAt = DateTime.UtcNow
        };
    }

    private ParsedSignal? TryParseVipKnightsSignal(string message, string providerChannelId)
    {
        // VIP KNIGHTS format:
        // SELL VIX50(1S)
        // @ 229914.40
        // Sl 231414.20
        // To 226500.00      <- TP1
        // Tp2 223500.00
        // Tp3 220500.00
        // Tp4 217500.00
        // Lotsize 0.025 (0.05)

        // Pattern: Direction + Asset (VIX/VOLATILITY/BOOM/CRASH variants)
        var pattern = @"\b(BUY|SELL)\s+(VIX|VOLATILITY|BOOM|CRASH)\s*(\d+)\s*(?:\((\d+)s?\))?";
        var match = Regex.Match(message, pattern, RegexOptions.IgnoreCase | RegexOptions.Singleline);
        if (!match.Success)
            return null;

        var direction = match.Groups[1].Value.Equals("BUY", StringComparison.OrdinalIgnoreCase)
            ? TradeDirection.Buy
            : TradeDirection.Sell;

        // Build asset name: "Volatility 50 1s" or "Volatility 50"
        var assetFamily = match.Groups[2].Value.ToUpperInvariant();
        var assetNumber = match.Groups[3].Value;
        var assetSuffix = match.Groups[4].Success ? $" {match.Groups[4].Value}s" : "";

        // Normalize VIX -> Volatility
        if (assetFamily == "VIX")
            assetFamily = "Volatility";
        else
            assetFamily = char.ToUpper(assetFamily[0]) + assetFamily.Substring(1).ToLower();

        var asset = $"{assetFamily} {assetNumber}{assetSuffix}".Trim();

        // Extract entry price: "@ 229914.40"
        var entryMatch = Regex.Match(message, @"@\s*([\d.]+)", RegexOptions.IgnoreCase);
        decimal? entry = entryMatch.Success
            ? decimal.Parse(entryMatch.Groups[1].Value, System.Globalization.CultureInfo.InvariantCulture)
            : null;

        // Extract TPs - VIP KNIGHTS uses "To" for TP1
        var (tp1, tp2, tp3, tp4) = ExtractTakeProfitsVipKnights(message);
        var sl = ExtractStopLoss(message);

        // Fallback if TP1/SL missing
        decimal pointOffset = 200m;
        if (!tp1.HasValue && entry.HasValue)
            tp1 = direction == TradeDirection.Buy ? entry + pointOffset : entry - pointOffset;
        if (!sl.HasValue && entry.HasValue)
            sl = direction == TradeDirection.Buy ? entry - pointOffset : entry + pointOffset;

        return new ParsedSignal
        {
            ProviderChannelId = providerChannelId,
            ProviderName = "VIP KNIGHTS",
            Asset = asset,
            Direction = direction,
            EntryPrice = entry,
            TakeProfit = tp1,
            TakeProfit2 = tp2,
            TakeProfit3 = tp3,
            TakeProfit4 = tp4,
            StopLoss = sl,
            SignalType = SignalType.Text,
            ReceivedAt = DateTime.UtcNow
        };
    }

    private static (decimal? tp1, decimal? tp2, decimal? tp3, decimal? tp4) ExtractTakeProfitsVipKnights(string message)
    {
        decimal? tp1 = null;
        decimal? tp2 = null;
        decimal? tp3 = null;
        decimal? tp4 = null;

        // Match "To" for TP1 (VIP KNIGHTS style)
        var toMatch = Regex.Match(message, @"\bTo\s+([\d.]+)", RegexOptions.IgnoreCase);
        if (toMatch.Success)
            tp1 = decimal.Parse(toMatch.Groups[1].Value, System.Globalization.CultureInfo.InvariantCulture);

        // Also check standard TP1 format as fallback
        if (!tp1.HasValue)
        {
            var tp1Match = Regex.Match(message, @"\bTP\s*1\b\s*[:.\-@]?\s*([\d.]+)", RegexOptions.IgnoreCase);
            if (tp1Match.Success)
                tp1 = decimal.Parse(tp1Match.Groups[1].Value, System.Globalization.CultureInfo.InvariantCulture);
        }

        // Extract TP2, TP3, TP4
        var tp2Match = Regex.Match(message, @"\bTp?\s*2\b\s*[:.\-@]?\s*([\d.]+)", RegexOptions.IgnoreCase);
        if (tp2Match.Success)
            tp2 = decimal.Parse(tp2Match.Groups[1].Value, System.Globalization.CultureInfo.InvariantCulture);

        var tp3Match = Regex.Match(message, @"\bTp?\s*3\b\s*[:.\-@]?\s*([\d.]+)", RegexOptions.IgnoreCase);
        if (tp3Match.Success)
            tp3 = decimal.Parse(tp3Match.Groups[1].Value, System.Globalization.CultureInfo.InvariantCulture);

        var tp4Match = Regex.Match(message, @"\bTp?\s*4\b\s*[:.\-@]?\s*([\d.]+)", RegexOptions.IgnoreCase);
        if (tp4Match.Success)
            tp4 = decimal.Parse(tp4Match.Groups[1].Value, System.Globalization.CultureInfo.InvariantCulture);

        return (tp1, tp2, tp3, tp4);
    }

    private ParsedSignal? TryParseMarketSignal(string message, string providerChannelId)
    {
        // Examples (caption/text):
        // BUY POSITION
        // VOLATILITY 50(1s) INDEX
        // SL.190909.00
        //
        // BUY VOLATILITY 50(1s) INDEX
        // TARGET:199656.00
        // SL:192768.00
        //
        // SELL VOLATILITY 50(1s) INDEX
        // TP1.190130.00
        // TP2.187958.00
        // TP3.189050.00
        // SL.192960.00

        // Be strict about the instrument name to avoid OCR noise capturing price blocks as the "asset"
        var pattern = @"\b(BUY|SELL)\b(?:\s+POSITION)?\s+(?<asset>(?:VOLATILITY|BOOM|CRASH)\b[\s\S]*?\bINDEX\b)";
        var match = Regex.Match(message, pattern, RegexOptions.IgnoreCase | RegexOptions.Singleline);
        if (!match.Success)
            return null;

        var direction = match.Groups[1].Value.Equals("BUY", StringComparison.OrdinalIgnoreCase)
            ? TradeDirection.Buy
            : TradeDirection.Sell;

        var asset = CleanupAsset(match.Groups["asset"].Value);

        // Guard: if we somehow captured a numeric-heavy blob, reject it
        if (!IsPlausibleSyntheticAsset(asset))
            return null;

        var (tp1, tp2, tp3, tp4) = ExtractTakeProfits(message);
        var sl = ExtractStopLoss(message);
        var target = ExtractTarget(message);

        // Prefer explicit TP1; fall back to TARGET
        tp1 ??= target;

        // Fallback: if TP1 or SL is missing, set 200 points away from a pseudo-entry (cannot use null)
        decimal? entry = null;
        // Try to extract a number from the message as a pseudo-entry if possible
        var entryMatch = Regex.Match(message, @"(\d{4,6}\.\d+)");
        if (entryMatch.Success)
            entry = decimal.Parse(entryMatch.Groups[1].Value, System.Globalization.CultureInfo.InvariantCulture);

        decimal pointOffset = 200m;
        decimal? fallbackTp = tp1;
        decimal? fallbackSl = sl;
        if (!tp1.HasValue && entry.HasValue)
            fallbackTp = direction == TradeDirection.Buy ? entry + pointOffset : entry - pointOffset;
        if (!sl.HasValue && entry.HasValue)
            fallbackSl = direction == TradeDirection.Buy ? entry - pointOffset : entry + pointOffset;

        return new ParsedSignal
        {
            ProviderChannelId = providerChannelId,
            ProviderName = "SyntheticIndicesTrader",
            Asset = asset,
            Direction = direction,
            EntryPrice = null, // market execution
            TakeProfit = fallbackTp,
            TakeProfit2 = tp2,
            TakeProfit3 = tp3,
            TakeProfit4 = tp4,
            StopLoss = fallbackSl,
            SignalType = SignalType.Text,
            ReceivedAt = DateTime.UtcNow
        };
    }

    private static string CleanupAsset(string raw)
    {
        // Keep letters, digits, spaces, parentheses; remove extra punctuation
        var cleaned = Regex.Replace(raw, @"[^A-Za-z0-9()\s]", " ");
        cleaned = Regex.Replace(cleaned, @"\s+", " ").Trim();

        // Some channels include the literal word POSITION before the instrument name
        if (cleaned.StartsWith("POSITION ", StringComparison.OrdinalIgnoreCase))
            cleaned = cleaned.Substring("POSITION ".Length).Trim();

        // Keep DB-friendly size (ParsedSignalsQueue.Asset is NVARCHAR(20)) while remaining resolvable by cTraderSymbolService
        // Prefer short canonical forms like "Volatility 25" or "Volatility 50 1s".
        cleaned = Regex.Replace(cleaned, @"\bINDEX\b", "", RegexOptions.IgnoreCase).Trim();
        cleaned = Regex.Replace(cleaned, @"\(\s*1s\s*\)", " 1s", RegexOptions.IgnoreCase).Trim();
        cleaned = Regex.Replace(cleaned, @"\s+", " ").Trim();

        if (cleaned.Length <= 20)
            return cleaned;

        // Abbreviate common long prefix
        cleaned = Regex.Replace(cleaned, @"\bVOLATILITY\b", "Vol", RegexOptions.IgnoreCase).Trim();
        cleaned = Regex.Replace(cleaned, @"\s+", " ").Trim();

        if (cleaned.Length <= 20)
            return cleaned;

        // Last resort: remove spaces
        var noSpaces = cleaned.Replace(" ", "");
        if (noSpaces.Length <= 20)
            return noSpaces;

        // Absolute fallback to avoid DB truncation exceptions
        return noSpaces.Substring(0, 20);
    }

    private static bool IsPlausibleSyntheticAsset(string asset)
    {
        if (string.IsNullOrWhiteSpace(asset))
            return false;

        // Synthetic assets should start with a known family name and contain at least one digit.
        var startsOk = asset.StartsWith("Volatility", StringComparison.OrdinalIgnoreCase)
                       || asset.StartsWith("Vol ", StringComparison.OrdinalIgnoreCase)
                       || asset.StartsWith("Vol", StringComparison.OrdinalIgnoreCase)
                       || asset.StartsWith("Boom", StringComparison.OrdinalIgnoreCase)
                       || asset.StartsWith("Crash", StringComparison.OrdinalIgnoreCase);

        if (!startsOk)
            return false;

        return asset.Any(char.IsDigit);
    }

    private static (decimal? tp1, decimal? tp2, decimal? tp3, decimal? tp4) ExtractTakeProfits(string message)
    {
        decimal? tp1 = null;
        decimal? tp2 = null;
        decimal? tp3 = null;
        decimal? tp4 = null;

        var matches = Regex.Matches(
            message,
            @"\bTP\s*([1-4])\b\s*[:.\-@]?\s*([0-9]+(?:\.[0-9]+)?)",
            RegexOptions.IgnoreCase | RegexOptions.Singleline);

        foreach (Match m in matches)
        {
            if (!m.Success)
                continue;

            var index = m.Groups[1].Value;
            var value = decimal.Parse(m.Groups[2].Value, System.Globalization.CultureInfo.InvariantCulture);
            switch (index)
            {
                case "1": tp1 = value; break;
                case "2": tp2 = value; break;
                case "3": tp3 = value; break;
                case "4": tp4 = value; break;
            }
        }

        return (tp1, tp2, tp3, tp4);
    }

    private static decimal? ExtractStopLoss(string message)
    {
        var m = Regex.Match(
            message,
            @"\bSL\b\s*[:.\-@]?\s*([0-9]+(?:\.[0-9]+)?)",
            RegexOptions.IgnoreCase | RegexOptions.Singleline);

        return m.Success
            ? decimal.Parse(m.Groups[1].Value, System.Globalization.CultureInfo.InvariantCulture)
            : null;
    }

    private static decimal? ExtractTarget(string message)
    {
        var m = Regex.Match(
            message,
            @"\bTARGET\b\s*[:.\-@]?\s*([0-9]+(?:\.[0-9]+)?)",
            RegexOptions.IgnoreCase | RegexOptions.Singleline);

        return m.Success
            ? decimal.Parse(m.Groups[1].Value, System.Globalization.CultureInfo.InvariantCulture)
            : null;
    }
}


