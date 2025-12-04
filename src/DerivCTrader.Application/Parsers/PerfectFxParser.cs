using System.Text.RegularExpressions;
using DerivCTrader.Application.Interfaces;
using DerivCTrader.Domain.Entities;
using DerivCTrader.Domain.Enums;
using Microsoft.Extensions.Logging;

namespace DerivCTrader.Application.Parsers;

public class PerfectFxParser : ISignalParser
{
    private readonly ILogger<PerfectFxParser> _logger;

    public PerfectFxParser(ILogger<PerfectFxParser> logger)
    {
        _logger = logger;
    }

    public bool CanParse(string providerChannelId)
    {
        var canParse = providerChannelId == "-1001446944855";     // PERFECTFX

        _logger.LogInformation("PerfectFxParser.CanParse({Channel}): {Result}", providerChannelId, canParse);

        return canParse;
    }

    public async Task<ParsedSignal?> ParseAsync(string message, string providerChannelId, byte[]? imageData = null)
    {
        try
        {
            _logger.LogInformation("PerfectFxParser: Starting parse attempt");
            _logger.LogInformation("PerfectFxParser: Message: {Message}", message);

            // PERFECTFX format: "AUDUSD BUY AT 0.65156\nTP 0.67392\nSL 0.64429"
            var pattern = @"(\w+)\s+(BUY|SELL)\s+AT\s+([\d.]+)\s+TP\s+([\d.]+)\s+SL\s+([\d.]+)";
            var match = Regex.Match(message, pattern, RegexOptions.Singleline | RegexOptions.IgnoreCase);

            if (!match.Success)
            {
                _logger.LogWarning("PerfectFxParser: Pattern did not match");
                return null;
            }

            _logger.LogInformation("PerfectFxParser: Pattern matched!");

            var asset = match.Groups[1].Value;
            var direction = match.Groups[2].Value.ToUpper() == "BUY" ? TradeDirection.Buy : TradeDirection.Sell;
            var entry = decimal.Parse(match.Groups[3].Value);
            var tp = decimal.Parse(match.Groups[4].Value);
            var sl = decimal.Parse(match.Groups[5].Value);

            _logger.LogInformation("PerfectFxParser: Parsed - {Asset} {Direction} @ {Entry}, TP: {TP}, SL: {SL}",
                asset, direction, entry, tp, sl);

            return new ParsedSignal
            {
                ProviderChannelId = providerChannelId,
                Asset = asset,
                Direction = direction,
                EntryPrice = entry,
                TakeProfit = tp,
                StopLoss = sl,
                SignalType = SignalType.Text,
                ReceivedAt = DateTime.UtcNow
            };
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error parsing PerfectFX signal");
            return null;
        }
    }
}