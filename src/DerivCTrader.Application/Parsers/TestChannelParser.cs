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
        var pattern = @"We\s+are\s+(selling|buying)\s+(\w+)\s+now\s+at\s+([\d.]+)[\s\S]*?Take\s+profit\s+at:\s*([\d.]+)[\s\S]*?Stop\s+loss\s+at:\s*([\d.]+)";
        var match = Regex.Match(message, pattern, RegexOptions.IgnoreCase | RegexOptions.Singleline);

        if (!match.Success) return null;

        return new ParsedSignal
        {
            ProviderChannelId = providerChannelId,
            ProviderName = "TestChannel",
            Asset = match.Groups[2].Value.ToUpper(),
            Direction = match.Groups[1].Value.ToLower() == "selling" ? TradeDirection.Sell : TradeDirection.Buy,
            EntryPrice = decimal.Parse(match.Groups[3].Value, System.Globalization.CultureInfo.InvariantCulture),
            TakeProfit = decimal.Parse(match.Groups[4].Value, System.Globalization.CultureInfo.InvariantCulture),
            StopLoss = decimal.Parse(match.Groups[5].Value, System.Globalization.CultureInfo.InvariantCulture),
            SignalType = SignalType.Text,
            ReceivedAt = DateTime.UtcNow
        };
    }

    private ParsedSignal? TryPerfectFxFormat(string message, string providerChannelId)
    {
        var pattern = @"(\w+)\s+(BUY|SELL)\s+AT\s+([\d.]+)\s+TP\s+([\d.]+)\s+SL\s+([\d.]+)";
        var match = Regex.Match(message, pattern, RegexOptions.IgnoreCase | RegexOptions.Singleline);

        if (!match.Success) return null;

        return new ParsedSignal
        {
            ProviderChannelId = providerChannelId,
            ProviderName = "TestChannel",
            Asset = match.Groups[1].Value.ToUpper(),
            Direction = match.Groups[2].Value.ToUpper() == "BUY" ? TradeDirection.Buy : TradeDirection.Sell,
            EntryPrice = decimal.Parse(match.Groups[3].Value, System.Globalization.CultureInfo.InvariantCulture),
            TakeProfit = decimal.Parse(match.Groups[4].Value, System.Globalization.CultureInfo.InvariantCulture),
            StopLoss = decimal.Parse(match.Groups[5].Value, System.Globalization.CultureInfo.InvariantCulture),
            SignalType = SignalType.Text,
            ReceivedAt = DateTime.UtcNow
        };
    }

    private ParsedSignal? TryTradingHubFormat(string message, string providerChannelId)
    {
        // Format: "BUY XAUUSD FOR 4238 OTHER LIMIT 4235 SL@4233 Tp@4243 Tp-2@4290"
        var pattern = @"(BUY|SELL)\s+(\w+)\s+FOR\s+([\d.]+).*?SL@([\d.]+).*?Tp@([\d.]+)";
        var match = Regex.Match(message, pattern, RegexOptions.IgnoreCase | RegexOptions.Singleline);

        if (!match.Success) return null;

        return new ParsedSignal
        {
            ProviderChannelId = providerChannelId,
            ProviderName = "TestChannel",
            Asset = match.Groups[2].Value.ToUpper(),
            Direction = match.Groups[1].Value.ToUpper() == "BUY" ? TradeDirection.Buy : TradeDirection.Sell,
            EntryPrice = decimal.Parse(match.Groups[3].Value, System.Globalization.CultureInfo.InvariantCulture),
            TakeProfit = decimal.Parse(match.Groups[5].Value, System.Globalization.CultureInfo.InvariantCulture),
            StopLoss = decimal.Parse(match.Groups[4].Value, System.Globalization.CultureInfo.InvariantCulture),
            SignalType = SignalType.Text,
            ReceivedAt = DateTime.UtcNow
        };
    }

    private ParsedSignal? TrySyntheticIndicesFormat(string message, string providerChannelId)
    {
        // Format: "Sell : Volatility 75 Index Zone : 43130.00 - 43750.00 TP1 : 42570.00 SL : 44240.00"
        var pattern = @"(Buy|Sell)\s*:\s*(.+?)(?:Zone|Entry)\s*:\s*([\d.]+)\s*-\s*([\d.]+).*?TP1?\s*:\s*([\d.]+).*?SL\s*:\s*([\d.]+)";
        var match = Regex.Match(message, pattern, RegexOptions.IgnoreCase | RegexOptions.Singleline);

        if (!match.Success) return null;

        var entryMin = decimal.Parse(match.Groups[3].Value, System.Globalization.CultureInfo.InvariantCulture);
        var entryMax = decimal.Parse(match.Groups[4].Value, System.Globalization.CultureInfo.InvariantCulture);
        var entry = (entryMin + entryMax) / 2; // Use middle of zone

        return new ParsedSignal
        {
            ProviderChannelId = providerChannelId,
            ProviderName = "TestChannel",
            Asset = match.Groups[2].Value.Trim().Replace(" ", "").ToUpper(), // "Volatility 75 Index" -> "VOLATILITY75INDEX"
            Direction = match.Groups[1].Value.ToUpper() == "BUY" ? TradeDirection.Buy : TradeDirection.Sell,
            EntryPrice = entry,
            TakeProfit = decimal.Parse(match.Groups[5].Value, System.Globalization.CultureInfo.InvariantCulture),
            StopLoss = decimal.Parse(match.Groups[6].Value, System.Globalization.CultureInfo.InvariantCulture),
            SignalType = SignalType.Text,
            ReceivedAt = DateTime.UtcNow
        };
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