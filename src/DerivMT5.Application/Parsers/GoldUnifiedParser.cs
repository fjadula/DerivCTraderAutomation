using System.Text.RegularExpressions;
using DerivCTrader.Application.Interfaces;
using DerivCTrader.Domain.Entities;
using DerivCTrader.Domain.Enums;
using Microsoft.Extensions.Logging;

namespace DerivMT5.Application.Parsers;

/// <summary>
/// Parser for remaining Gold signal channels
/// Channels: -1002782055957, -1001685029638, -1001631556618, -1002242743399, -1003046812685
/// All use similar format with slight variations
/// </summary>
public class GoldUnifiedParser : ISignalParser
{
    private readonly ILogger<GoldUnifiedParser> _logger;

    private static readonly HashSet<string> SupportedChannelIds = new(StringComparer.Ordinal)
    {
        "-1002782055957", // BUY GOLD@ 4320
        "-1001685029638", // GOLD SELL 4331 MORE SELL 4335
        "-1001631556618", // XAUUSD SELL 4190 OR 4194 + US30
        "-1002242743399", // GOLD SELL 4320
        "-1003046812685"  // BUY VIX15(1S)
    };

    public GoldUnifiedParser(ILogger<GoldUnifiedParser> logger)
    {
        _logger = logger;
    }

    public bool CanParse(string providerChannelId)
    {
        return SupportedChannelIds.Contains(providerChannelId);
    }

    public Task<ParsedSignal?> ParseAsync(string message, string providerChannelId, byte[]? imageData = null)
    {
        try
        {
            _logger.LogInformation("[GoldUnifiedParser] Parsing message from {Channel}: {Message}", 
                providerChannelId, message);

            // Try different patterns
            var signal = TryParseGoldSignal(message, providerChannelId)
                ?? TryParseUS30Signal(message, providerChannelId)
                ?? TryParseVIXSignal(message, providerChannelId);

            if (signal != null)
            {
                _logger.LogInformation(
                    "[GoldUnifiedParser] Parsed: {Asset} {Direction} @ {Entry}, TP1: {TP1}, SL: {SL}",
                    signal.Asset, signal.Direction, signal.EntryPrice, signal.TakeProfit, signal.StopLoss);
            }
            else
            {
                _logger.LogWarning("[GoldUnifiedParser] No pattern matched");
            }

            return Task.FromResult(signal);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "[GoldUnifiedParser] Error parsing signal");
            return Task.FromResult<ParsedSignal?>(null);
        }
    }

    private ParsedSignal? TryParseGoldSignal(string message, string providerChannelId)
    {
        // Patterns:
        // BUY GOLD@ 4320
        // GOLD SELL 4331 MORE SELL 4335
        // XAUUSD SELL 4190 OR 4194
        // GOLD SELL 4320

        var patterns = new[]
        {
            @"(BUY|SELL)\s+(GOLD|XAUUSD)\s*@?\s*([\d.]+)(?:\s+(?:MORE|OR)\s+(?:BUY|SELL)\s+([\d.]+))?",
            @"(GOLD|XAUUSD)\s+(BUY|SELL)\s+([\d.]+)(?:\s+(?:MORE|OR)\s+(?:BUY|SELL)\s+([\d.]+))?"
        };

        Match? match = null;
        foreach (var pattern in patterns)
        {
            match = Regex.Match(message, pattern, RegexOptions.IgnoreCase);
            if (match.Success) break;
        }

        if (match == null || !match.Success)
            return null;

        // Determine direction and prices based on match groups
        string directionStr;
        string price1Str, price2Str = "";

        if (match.Groups[1].Value.ToUpper() is "BUY" or "SELL")
        {
            // Pattern 1: BUY GOLD@ 4320
            directionStr = match.Groups[1].Value;
            price1Str = match.Groups[3].Value;
            if (match.Groups[4].Success)
                price2Str = match.Groups[4].Value;
        }
        else
        {
            // Pattern 2: GOLD SELL 4331
            directionStr = match.Groups[2].Value;
            price1Str = match.Groups[3].Value;
            if (match.Groups[4].Success)
                price2Str = match.Groups[4].Value;
        }

        var direction = directionStr.Equals("BUY", StringComparison.OrdinalIgnoreCase)
            ? TradeDirection.Buy
            : TradeDirection.Sell;

        var price1 = decimal.Parse(price1Str);
        var price2 = string.IsNullOrEmpty(price2Str) ? price1 : decimal.Parse(price2Str);
        var entryPrice = (price1 + price2) / 2m;

        // Extract TPs and SL
        var tps = ExtractTakeProfits(message);
        var sl = ExtractStopLoss(message);

        return new ParsedSignal
        {
            ProviderChannelId = providerChannelId,
            ProviderName = "DerivGold",
            Asset = "XAUUSD",
            Direction = direction,
            EntryPrice = entryPrice,
            TakeProfit = tps.tp1,
            TakeProfit2 = tps.tp2,
            TakeProfit3 = tps.tp3,
            TakeProfit4 = tps.tp4,
            StopLoss = sl,
            SignalType = SignalType.Text,
            ReceivedAt = DateTime.UtcNow
        };
    }

    private ParsedSignal? TryParseUS30Signal(string message, string providerChannelId)
    {
        // Pattern: US30 BUY 48150
        var pattern = @"US30\s+(BUY|SELL)\s+([\d.]+)";
        var match = Regex.Match(message, pattern, RegexOptions.IgnoreCase);

        if (!match.Success)
            return null;

        var direction = match.Groups[1].Value.Equals("BUY", StringComparison.OrdinalIgnoreCase)
            ? TradeDirection.Buy
            : TradeDirection.Sell;

        var entryPrice = decimal.Parse(match.Groups[2].Value);

        var tps = ExtractTakeProfits(message);
        var sl = ExtractStopLoss(message);

        return new ParsedSignal
        {
            ProviderChannelId = providerChannelId,
            ProviderName = "DerivGold",
            Asset = "US30",
            Direction = direction,
            EntryPrice = entryPrice,
            TakeProfit = tps.tp1,
            TakeProfit2 = tps.tp2,
            TakeProfit3 = tps.tp3,
            TakeProfit4 = tps.tp4,
            StopLoss = sl,
            SignalType = SignalType.Text,
            ReceivedAt = DateTime.UtcNow
        };
    }

    private ParsedSignal? TryParseVIXSignal(string message, string providerChannelId)
    {
        // Pattern: BUY VIX15(1S) @ 12970.220
        var pattern = @"(BUY|SELL)\s+(VIX\d+)\s*\(1S\)\s*@\s*([\d.]+)";
        var match = Regex.Match(message, pattern, RegexOptions.IgnoreCase);

        if (!match.Success)
            return null;

        var direction = match.Groups[1].Value.Equals("BUY", StringComparison.OrdinalIgnoreCase)
            ? TradeDirection.Buy
            : TradeDirection.Sell;

        var asset = match.Groups[2].Value; // VIX15, VIX25, etc.
        var entryPrice = decimal.Parse(match.Groups[3].Value);

        var tps = ExtractTakeProfits(message);
        var sl = ExtractStopLoss(message);

        // Extract lot size if present
        var lotSizePattern = @"Lotsize\s+([\d.]+)";
        var lotMatch = Regex.Match(message, lotSizePattern, RegexOptions.IgnoreCase);
        decimal? lotSize = lotMatch.Success ? decimal.Parse(lotMatch.Groups[1].Value) : null;

        return new ParsedSignal
        {
            ProviderChannelId = providerChannelId,
            ProviderName = "DerivGold",
            Asset = asset,
            Direction = direction,
            EntryPrice = entryPrice,
            TakeProfit = tps.tp1,
            TakeProfit2 = tps.tp2,
            TakeProfit3 = tps.tp3,
            TakeProfit4 = tps.tp4,
            StopLoss = sl,
            LotSize = lotSize,
            SignalType = SignalType.Text,
            ReceivedAt = DateTime.UtcNow
        };
    }

    private static (decimal? tp1, decimal? tp2, decimal? tp3, decimal? tp4) ExtractTakeProfits(string message)
    {
        decimal? tp1 = null, tp2 = null, tp3 = null, tp4 = null;

        // Handle various TP formats
        var patterns = new[]
        {
            @"TP\s*(\d)?[:\.\s]?\s*([\d.]+)", // TP1: 4323 or TP 4323
            @"TPs?\s+([\d.]+)" // TPs 4315 or TP 4315
        };

        var allMatches = new List<decimal>();

        foreach (var pattern in patterns)
        {
            var matches = Regex.Matches(message, pattern, RegexOptions.IgnoreCase);
            foreach (Match match in matches)
            {
                if (!match.Success) continue;
                
                // Get the price value (could be in group 2 or group 1 depending on pattern)
                var valueStr = match.Groups[2].Success ? match.Groups[2].Value : match.Groups[1].Value;
                
                if (decimal.TryParse(valueStr, out var value))
                {
                    allMatches.Add(value);
                }
            }
        }

        // Assign to tp1-tp4 in order
        if (allMatches.Count > 0) tp1 = allMatches[0];
        if (allMatches.Count > 1) tp2 = allMatches[1];
        if (allMatches.Count > 2) tp3 = allMatches[2];
        if (allMatches.Count > 3) tp4 = allMatches[3];

        return (tp1, tp2, tp3, tp4);
    }

    private static decimal? ExtractStopLoss(string message)
    {
        // Handle various SL formats: SL: 4341, SL_4344, STOPLOSS:4334, SL 4341
        var slPattern = @"(?:SL|STOPLOSS)[_:\.\s]?\s*([\d.]+)";
        var match = Regex.Match(message, slPattern, RegexOptions.IgnoreCase);

        if (match.Success && decimal.TryParse(match.Groups[1].Value, out var sl))
        {
            return sl;
        }

        // Special case: "SL PREMIUM" means no SL
        if (message.Contains("PREMIUM", StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        return null;
    }
}
