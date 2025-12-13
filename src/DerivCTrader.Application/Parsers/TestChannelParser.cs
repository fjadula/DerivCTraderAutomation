using System.Text.RegularExpressions;
using DerivCTrader.Application.Interfaces;
using DerivCTrader.Domain.Entities;
using DerivCTrader.Domain.Enums;
using Microsoft.Extensions.Logging;

namespace DerivCTrader.Application.Parsers;

/// <summary>
/// Parser for test channel that tries all known signal formats
/// </summary>
public class TestChannelParser : ISignalParser
{
    private readonly ILogger<TestChannelParser> _logger;

    public TestChannelParser(ILogger<TestChannelParser> logger)
    {
        _logger = logger;
    }

    public bool CanParse(string providerChannelId)
    {
        var canParse = providerChannelId == "-1001304028537"; // TestChannel only
        _logger.LogInformation("TestChannelParser.CanParse({Channel}): {Result}", providerChannelId, canParse);
        return canParse;
    }

    public async Task<ParsedSignal?> ParseAsync(string message, string providerChannelId, byte[]? imageData = null)
    {
        try
        {
            _logger.LogInformation("TestChannelParser: Trying all formats...");

            // Try Format 1: VIPFX - "We are selling EURUSD now at 1.16359"
            var result = TryVipFxFormat(message, providerChannelId);
            if (result != null)
            {
                _logger.LogInformation("TestChannelParser: ✅ Matched VIPFX format");
                return result;
            }

            // Try Format 2: PerfectFX - "AUDCAD SELL AT 0.92242 TP 0.90084 SL 0.92869"
            result = TryPerfectFxFormat(message, providerChannelId);
            if (result != null)
            {
                _logger.LogInformation("TestChannelParser: ✅ Matched PerfectFX format");
                return result;
            }

            // Try Format 3: TradingHubVIP - "BUY XAUUSD FOR 4238 SL@4233 Tp@4243"
            result = TryTradingHubFormat(message, providerChannelId);
            if (result != null)
            {
                _logger.LogInformation("TestChannelParser: ✅ Matched TradingHubVIP format");
                return result;
            }

            // Try Format 4: Synthetic Indices - "Sell : Volatility 75 Index Zone : 43130.00"
            result = TrySyntheticIndicesFormat(message, providerChannelId);
            if (result != null)
            {
                _logger.LogInformation("TestChannelParser: ✅ Matched Synthetic Indices format");
                return result;
            }

            // Try Format 5: VIP Channel Binary - "OPEN GBP/CAD PUT 15 MIN"
            result = TryVipChannelFormat(message, providerChannelId);
            if (result != null)
            {
                _logger.LogInformation("TestChannelParser: ✅ Matched VIP Channel format");
                return result;
            }

            _logger.LogWarning("TestChannelParser: ❌ No format matched");
            return null;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "TestChannelParser: Error parsing signal");
            return null;
        }
    }

    private ParsedSignal? TryVipFxFormat(string message, string providerChannelId)
    {
        var pattern = @"We\s+are\s+(selling|buying)\s+(\w+)\s+now\s+at\s+([\d.]+)[\s\S]*?(?:Take\s+profit\s+at:\s*([\d.]+))?[\s\S]*?(?:Stop\s+loss\s+at:\s*([\d.]+))?";
        var match = Regex.Match(message, pattern, RegexOptions.IgnoreCase | RegexOptions.Singleline);

        if (!match.Success) return null;

        var asset = match.Groups[2].Value.ToUpper();
        var direction = match.Groups[1].Value.ToLower() == "selling" ? TradeDirection.Sell : TradeDirection.Buy;
        var entry = decimal.Parse(match.Groups[3].Value, System.Globalization.CultureInfo.InvariantCulture);
        decimal? tp = null;
        decimal? sl = null;
        if (decimal.TryParse(match.Groups[4].Value, out var parsedTp))
            tp = parsedTp;
        if (decimal.TryParse(match.Groups[5].Value, out var parsedSl))
            sl = parsedSl;
        decimal pip = asset.EndsWith("JPY") ? 0.01m : 0.0001m;
        decimal pipOffset = pip * 30;
        if (!tp.HasValue)
            tp = direction == TradeDirection.Buy ? entry + pipOffset : entry - pipOffset;
        if (!sl.HasValue)
            sl = direction == TradeDirection.Buy ? entry - pipOffset : entry + pipOffset;
        return new ParsedSignal
        {
            ProviderChannelId = providerChannelId,
            ProviderName = "TestChannel",
            Asset = asset,
            Direction = direction,
            EntryPrice = entry,
            TakeProfit = tp,
            StopLoss = sl,
            SignalType = SignalType.Text,
            ReceivedAt = DateTime.UtcNow
        };
    }

    private ParsedSignal? TryPerfectFxFormat(string message, string providerChannelId)
    {
        var pattern = @"(\w+)\s+(BUY|SELL)\s+AT\s+([\d.]+)(?:\s+TP\s+([\d.]+))?(?:\s+SL\s+([\d.]+))?";
        var match = Regex.Match(message, pattern, RegexOptions.IgnoreCase | RegexOptions.Singleline);

        if (!match.Success) return null;

        var asset = match.Groups[1].Value.ToUpper();
        var direction = match.Groups[2].Value.ToUpper() == "BUY" ? TradeDirection.Buy : TradeDirection.Sell;
        var entry = decimal.Parse(match.Groups[3].Value, System.Globalization.CultureInfo.InvariantCulture);
        decimal? tp = null;
        decimal? sl = null;
        if (decimal.TryParse(match.Groups[4].Value, out var parsedTp))
            tp = parsedTp;
        if (decimal.TryParse(match.Groups[5].Value, out var parsedSl))
            sl = parsedSl;
        decimal pip = asset.EndsWith("JPY") ? 0.01m : 0.0001m;
        decimal pipOffset = pip * 30;
        if (!tp.HasValue)
            tp = direction == TradeDirection.Buy ? entry + pipOffset : entry - pipOffset;
        if (!sl.HasValue)
            sl = direction == TradeDirection.Buy ? entry - pipOffset : entry + pipOffset;
        return new ParsedSignal
        {
            ProviderChannelId = providerChannelId,
            ProviderName = "TestChannel",
            Asset = asset,
            Direction = direction,
            EntryPrice = entry,
            TakeProfit = tp,
            StopLoss = sl,
            SignalType = SignalType.Text,
            ReceivedAt = DateTime.UtcNow
        };
    }

    private ParsedSignal? TryTradingHubFormat(string message, string providerChannelId)
    {
        // Format: "BUY XAUUSD FOR 4238 OTHER LIMIT 4235 SL@4233 Tp@4243 Tp-2@4290"
        var pattern = @"(BUY|SELL)\s+(\w+)\s+FOR\s+([\d.]+).*?(?:SL@([\d.]+))?.*?(?:Tp@([\d.]+))?";
        var match = Regex.Match(message, pattern, RegexOptions.IgnoreCase | RegexOptions.Singleline);

        if (!match.Success) return null;

        var asset = match.Groups[2].Value.ToUpper();
        var direction = match.Groups[1].Value.ToUpper() == "BUY" ? TradeDirection.Buy : TradeDirection.Sell;
        var entry = decimal.Parse(match.Groups[3].Value, System.Globalization.CultureInfo.InvariantCulture);
        decimal? tp = null;
        decimal? sl = null;
        if (decimal.TryParse(match.Groups[5].Value, out var parsedTp))
            tp = parsedTp;
        if (decimal.TryParse(match.Groups[4].Value, out var parsedSl))
            sl = parsedSl;
        decimal pip = asset.EndsWith("JPY") ? 0.01m : 0.0001m;
        decimal pipOffset = pip * 30;
        if (!tp.HasValue)
            tp = direction == TradeDirection.Buy ? entry + pipOffset : entry - pipOffset;
        if (!sl.HasValue)
            sl = direction == TradeDirection.Buy ? entry - pipOffset : entry + pipOffset;
        return new ParsedSignal
        {
            ProviderChannelId = providerChannelId,
            ProviderName = "TestChannel",
            Asset = asset,
            Direction = direction,
            EntryPrice = entry,
            TakeProfit = tp,
            StopLoss = sl,
            SignalType = SignalType.Text,
            ReceivedAt = DateTime.UtcNow
        };
    }

    private ParsedSignal? TrySyntheticIndicesFormat(string message, string providerChannelId)
    {
        // Supports 2 families:
        // 1) Zone format:
        //    buy : Volatility 50 Index
        //    Zone : 137.50 - 136.00
        //    TP1 : 138.50
        //    TP2 : 139.70
        //    TP3 : 141.60
        //    SL : 135.00
        // 2) Market format (no zone):
        //    BUY VOLATILITY 50(1s) INDEX
        //    TARGET:199656.00
        //    SL:192768.00

        var zonePattern = @"(Buy|Sell)\s*:\s*(.+?)\s*(?:Zone|Entry)\s*:\s*([\d.]+)\s*-\s*([\d.]+)";
        var zoneMatch = Regex.Match(message, zonePattern, RegexOptions.IgnoreCase | RegexOptions.Singleline);
        if (zoneMatch.Success)
        {
            var entryMinZ = decimal.Parse(zoneMatch.Groups[3].Value, System.Globalization.CultureInfo.InvariantCulture);
            var entryMaxZ = decimal.Parse(zoneMatch.Groups[4].Value, System.Globalization.CultureInfo.InvariantCulture);
            var entryZ = (entryMinZ + entryMaxZ) / 2m; // midpoint of zone

            var (tp1Z, tp2Z, tp3Z, tp4Z) = ExtractTakeProfits(message);
            var slZ = ExtractStopLoss(message);
            decimal pointOffsetZ = 200m;
            decimal? fallbackTpZ = tp1Z;
            decimal? fallbackSlZ = slZ;
            var directionZ = zoneMatch.Groups[1].Value.Equals("BUY", StringComparison.OrdinalIgnoreCase)
                ? TradeDirection.Buy
                : TradeDirection.Sell;
            if (!tp1Z.HasValue)
                fallbackTpZ = directionZ == TradeDirection.Buy ? entryZ + pointOffsetZ : entryZ - pointOffsetZ;
            if (!slZ.HasValue)
                fallbackSlZ = directionZ == TradeDirection.Buy ? entryZ - pointOffsetZ : entryZ + pointOffsetZ;
            return new ParsedSignal
            {
                ProviderChannelId = providerChannelId,
                ProviderName = "TestChannel",
                Asset = CleanupAsset(zoneMatch.Groups[2].Value),
                Direction = directionZ,
                EntryPrice = entryZ,
                TakeProfit = fallbackTpZ,
                TakeProfit2 = tp2Z,
                TakeProfit3 = tp3Z,
                TakeProfit4 = tp4Z,
                StopLoss = fallbackSlZ,
                SignalType = SignalType.Text,
                ReceivedAt = DateTime.UtcNow
            };
        }

        var marketPattern = @"\b(BUY|SELL)\b(?:\s+POSITION)?\s+((?:VOLATILITY|BOOM|CRASH)\b[\s\S]*?\bINDEX\b)";
        var marketMatch = Regex.Match(message, marketPattern, RegexOptions.IgnoreCase | RegexOptions.Singleline);
        if (!marketMatch.Success)
            return null;

        var (mtp1M, mtp2M, mtp3M, mtp4M) = ExtractTakeProfits(message);
        var mslM = ExtractStopLoss(message);
        var targetM = ExtractTarget(message);
        mtp1M ??= targetM;
        decimal? entryM = null;
        var entryMatchM = Regex.Match(message, "(\\d{4,6}\\.\\d+)");
        if (entryMatchM.Success)
            entryM = decimal.Parse(entryMatchM.Groups[1].Value, System.Globalization.CultureInfo.InvariantCulture);
        decimal pointOffsetM = 200m;
        decimal? fallbackTpM = mtp1M;
        decimal? fallbackSlM = mslM;
        var directionM = marketMatch.Groups[1].Value.Equals("BUY", StringComparison.OrdinalIgnoreCase)
            ? TradeDirection.Buy
            : TradeDirection.Sell;
        if (!mtp1M.HasValue && entryM.HasValue)
            fallbackTpM = directionM == TradeDirection.Buy ? entryM + pointOffsetM : entryM - pointOffsetM;
        if (!mslM.HasValue && entryM.HasValue)
            fallbackSlM = directionM == TradeDirection.Buy ? entryM - pointOffsetM : entryM + pointOffsetM;
        return new ParsedSignal
        {
            ProviderChannelId = providerChannelId,
            ProviderName = "TestChannel",
            Asset = CleanupAsset(marketMatch.Groups[2].Value),
            Direction = directionM,
            EntryPrice = null,
            TakeProfit = fallbackTpM,
            TakeProfit2 = mtp2M,
            TakeProfit3 = mtp3M,
            TakeProfit4 = mtp4M,
            StopLoss = fallbackSlM,
            SignalType = SignalType.Text,
            ReceivedAt = DateTime.UtcNow
        };
    }

    private static string CleanupAsset(string raw)
    {
        var cleaned = Regex.Replace(raw, @"[^A-Za-z0-9()\s]", " ");
        cleaned = Regex.Replace(cleaned, @"\s+", " ").Trim();

        if (cleaned.StartsWith("POSITION ", StringComparison.OrdinalIgnoreCase))
            cleaned = cleaned.Substring("POSITION ".Length).Trim();

        // Keep DB-friendly size (ParsedSignalsQueue.Asset is NVARCHAR(20)) while remaining resolvable by cTraderSymbolService
        cleaned = Regex.Replace(cleaned, @"\bINDEX\b", "", RegexOptions.IgnoreCase).Trim();
        cleaned = Regex.Replace(cleaned, @"\(\s*1s\s*\)", " 1s", RegexOptions.IgnoreCase).Trim();
        cleaned = Regex.Replace(cleaned, @"\s+", " ").Trim();

        if (cleaned.Length <= 20)
            return cleaned;

        cleaned = Regex.Replace(cleaned, @"\bVOLATILITY\b", "Vol", RegexOptions.IgnoreCase).Trim();
        cleaned = Regex.Replace(cleaned, @"\s+", " ").Trim();

        if (cleaned.Length <= 20)
            return cleaned;

        var noSpaces = cleaned.Replace(" ", "");
        if (noSpaces.Length <= 20)
            return noSpaces;

        return noSpaces.Substring(0, 20);
    }

    private static (decimal? tp1, decimal? tp2, decimal? tp3, decimal? tp4) ExtractTakeProfits(string message)
    {
        decimal? tp1 = null;
        decimal? tp2 = null;
        decimal? tp3 = null;
        decimal? tp4 = null;

        // Match TP1, TP2, TP3, TP4
        var matches = Regex.Matches(
            message,
            @"\bTP\s*([1-4])\b\s*[:.\-@]?\s*([0-9]+(?:\.[0-9]+)?)",
            RegexOptions.IgnoreCase | RegexOptions.Singleline);

        foreach (Match m in matches)
        {
            if (!m.Success)
                continue;

            var idx = m.Groups[1].Value;
            var value = decimal.Parse(m.Groups[2].Value, System.Globalization.CultureInfo.InvariantCulture);

            switch (idx)
            {
                case "1": tp1 = value; break;
                case "2": tp2 = value; break;
                case "3": tp3 = value; break;
                case "4": tp4 = value; break;
            }
        }

        // Also match generic TP (no number) as TP1 if not already set
        if (tp1 == null)
        {
            var m = Regex.Match(
                message,
                @"\bTP\b\s*[:.\-@]?\s*([0-9]+(?:\.[0-9]+)?)",
                RegexOptions.IgnoreCase | RegexOptions.Singleline);
            if (m.Success)
            {
                tp1 = decimal.Parse(m.Groups[1].Value, System.Globalization.CultureInfo.InvariantCulture);
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

    private ParsedSignal? TryVipChannelFormat(string message, string providerChannelId)
    {
        // Format: "OPEN GBP/CAD PUT 15 MIN"
        var pattern = @"OPEN\s+([\w/]+)\s+(CALL|PUT)\s+(\d+)\s+MIN";
        var match = Regex.Match(message, pattern, RegexOptions.IgnoreCase);

        if (!match.Success) return null;

        return new ParsedSignal
        {
            ProviderChannelId = providerChannelId,
            ProviderName = "TestChannel",
            Asset = match.Groups[1].Value.Replace("/", "").ToUpper(),
            Direction = match.Groups[2].Value.ToUpper() == "CALL" ? TradeDirection.Call : TradeDirection.Put,
            SignalType = SignalType.PureBinary,
            ReceivedAt = DateTime.UtcNow,
            EntryPrice = null,
            TakeProfit = null,
            StopLoss = null
        };
    }
}