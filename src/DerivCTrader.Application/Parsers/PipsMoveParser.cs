using System.Text.RegularExpressions;
using DerivCTrader.Application.Interfaces;
using DerivCTrader.Domain.Entities;
using DerivCTrader.Domain.Enums;
using Microsoft.Extensions.Logging;

namespace DerivCTrader.Application.Parsers;

/// <summary>
/// Parser for PIPSMOVE channel
/// Format: "Buy limit - AUDJPY
///          Entry: 104.502
///          Stop-loss: 104.437
///          Take profit: 104.700"
/// </summary>
public class PipsMoveParser : ISignalParser
{
    private readonly ILogger<PipsMoveParser> _logger;

    public PipsMoveParser(ILogger<PipsMoveParser> logger)
    {
        _logger = logger;
    }

    public bool CanParse(string providerChannelId)
    {
        var canParse = providerChannelId == "-1001592593543"; // PIPSMOVE
        _logger.LogInformation("PipsMoveParser.CanParse({Channel}): {Result}", providerChannelId, canParse);
        return canParse;
    }

    public async Task<ParsedSignal?> ParseAsync(string message, string providerChannelId, byte[]? imageData = null)
    {
        try
        {
            _logger.LogInformation("PipsMoveParser: Starting parse attempt");
            _logger.LogInformation("PipsMoveParser: Message: {Message}", message);

            // Pattern: "Buy limit - AUDJPY" or "Sell limit - EURUSD" or "Buy - GBPUSD"
            var directionPattern = @"\b(Buy|Sell)\s*(?:limit|stop)?\s*[-–]\s*([A-Z]{6})\b";
            var dirMatch = Regex.Match(message, directionPattern, RegexOptions.IgnoreCase | RegexOptions.Singleline);

            if (!dirMatch.Success)
            {
                _logger.LogWarning("PipsMoveParser: Direction/Asset pattern did not match");
                return null;
            }

            var direction = dirMatch.Groups[1].Value.Equals("BUY", StringComparison.OrdinalIgnoreCase)
                ? TradeDirection.Buy
                : TradeDirection.Sell;
            var asset = dirMatch.Groups[2].Value.ToUpper();

            // Extract Entry price
            var entryMatch = Regex.Match(message, @"Entry\s*:\s*([\d.]+)", RegexOptions.IgnoreCase);
            if (!entryMatch.Success)
            {
                _logger.LogWarning("PipsMoveParser: Entry price not found");
                return null;
            }
            var entry = decimal.Parse(entryMatch.Groups[1].Value, System.Globalization.CultureInfo.InvariantCulture);

            // Extract Stop-loss
            var slMatch = Regex.Match(message, @"Stop[-\s]?loss\s*:\s*([\d.]+)", RegexOptions.IgnoreCase);
            decimal? sl = slMatch.Success
                ? decimal.Parse(slMatch.Groups[1].Value, System.Globalization.CultureInfo.InvariantCulture)
                : null;

            // Extract Take profit
            var tpMatch = Regex.Match(message, @"Take\s*profit\s*:\s*([\d.]+)", RegexOptions.IgnoreCase);
            decimal? tp = tpMatch.Success
                ? decimal.Parse(tpMatch.Groups[1].Value, System.Globalization.CultureInfo.InvariantCulture)
                : null;

            // Fallback if TP/SL missing
            decimal pipSize = asset.EndsWith("JPY") ? 0.01m : 0.0001m;
            decimal pipOffset = pipSize * 30;
            if (!tp.HasValue)
                tp = direction == TradeDirection.Buy ? entry + pipOffset : entry - pipOffset;
            if (!sl.HasValue)
                sl = direction == TradeDirection.Buy ? entry - pipOffset : entry + pipOffset;

            _logger.LogInformation("PipsMoveParser: Parsed - {Asset} {Direction} @ {Entry}, TP: {TP}, SL: {SL}",
                asset, direction, entry, tp, sl);

            return new ParsedSignal
            {
                ProviderChannelId = providerChannelId,
                ProviderName = "PIPSMOVE",
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
            _logger.LogError(ex, "Error parsing PIPSMOVE signal");
            return null;
        }
    }
}
