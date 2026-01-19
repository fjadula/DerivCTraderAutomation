using System.Text.RegularExpressions;
using DerivCTrader.Application.Interfaces;
using DerivCTrader.Domain.Entities;
using DerivCTrader.Domain.Enums;
using Microsoft.Extensions.Logging;

namespace DerivMT5.Application.Parsers;

/// <summary>
/// Parser for Gold Signal Channel 1 (-1001357835235)
/// Format: XAUUSD SELL 4335/4338
///         TP1. 4325
///         TP2. 4315
///         TP3. 4300/4285
///         SL. 4341
/// </summary>
public class GoldSignalParser1 : ISignalParser
{
    private readonly ILogger<GoldSignalParser1> _logger;
    private const string ChannelId = "-1001357835235";

    public GoldSignalParser1(ILogger<GoldSignalParser1> logger)
    {
        _logger = logger;
    }

    public bool CanParse(string providerChannelId)
    {
        return providerChannelId == ChannelId;
    }

    public Task<ParsedSignal?> ParseAsync(string message, string providerChannelId, byte[]? imageData = null)
    {
        try
        {
            _logger.LogInformation("[GoldSignalParser1] Parsing message: {Message}", message);

            // Pattern: XAUUSD SELL 4335/4338 or XAUUSD SELL 4335
            var pattern = @"(XAUUSD|GOLD)\s+(BUY|SELL)\s+([\d.]+)(?:/([\d.]+))?";
            var match = Regex.Match(message, pattern, RegexOptions.IgnoreCase);

            if (!match.Success)
            {
                _logger.LogWarning("[GoldSignalParser1] Failed to match pattern");
                return Task.FromResult<ParsedSignal?>(null);
            }

            var asset = "XAUUSD";
            var direction = match.Groups[2].Value.Equals("BUY", StringComparison.OrdinalIgnoreCase)
                ? TradeDirection.Buy
                : TradeDirection.Sell;

            var price1 = decimal.Parse(match.Groups[3].Value);
            var price2 = match.Groups[4].Success 
                ? decimal.Parse(match.Groups[4].Value) 
                : price1;

            // Use midpoint if two prices provided
            var entryPrice = (price1 + price2) / 2m;

            // Extract TPs (use first value if multiple)
            var tps = ExtractTakeProfits(message);
            var sl = ExtractStopLoss(message);

            var signal = new ParsedSignal
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
                SignalType = SignalType.Text,
                ReceivedAt = DateTime.UtcNow
            };

            _logger.LogInformation(
                "[GoldSignalParser1] Parsed: {Asset} {Direction} @ {Entry}, TP1: {TP1}, SL: {SL}",
                signal.Asset, signal.Direction, signal.EntryPrice, signal.TakeProfit, signal.StopLoss);

            return Task.FromResult<ParsedSignal?>(signal);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "[GoldSignalParser1] Error parsing signal");
            return Task.FromResult<ParsedSignal?>(null);
        }
    }

    private static (decimal? tp1, decimal? tp2, decimal? tp3, decimal? tp4) ExtractTakeProfits(string message)
    {
        decimal? tp1 = null, tp2 = null, tp3 = null, tp4 = null;

        // Pattern: TP1. 4325 or TP1: 4325 or TP 4325 or TP1 4325
        var tpPattern = @"TP\s*(\d)?[:\.\s]?\s*([\d.]+)(?:/([\d.]+))?";
        var matches = Regex.Matches(message, tpPattern, RegexOptions.IgnoreCase);

        foreach (Match match in matches)
        {
            if (!match.Success) continue;

            var tpNumber = match.Groups[1].Success ? match.Groups[1].Value : "1";
            var value1 = decimal.Parse(match.Groups[2].Value);
            // If two values (e.g., 4300/4285), use the first one
            var value = value1;

            switch (tpNumber)
            {
                case "1": tp1 = value; break;
                case "2": tp2 = value; break;
                case "3": tp3 = value; break;
                case "4": tp4 = value; break;
                default:
                    if (tp1 == null) tp1 = value;
                    else if (tp2 == null) tp2 = value;
                    else if (tp3 == null) tp3 = value;
                    else if (tp4 == null) tp4 = value;
                    break;
            }
        }

        return (tp1, tp2, tp3, tp4);
    }

    private static decimal? ExtractStopLoss(string message)
    {
        // Pattern: SL. 4341 or SL: 4341 or SL 4341
        var slPattern = @"SL[:\.\s]?\s*([\d.]+)";
        var match = Regex.Match(message, slPattern, RegexOptions.IgnoreCase);

        return match.Success ? decimal.Parse(match.Groups[1].Value) : null;
    }
}
