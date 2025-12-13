using System.Text.RegularExpressions;
using DerivCTrader.Application.Interfaces;
using DerivCTrader.Domain.Entities;
using DerivCTrader.Domain.Enums;
using Microsoft.Extensions.Logging;

namespace DerivCTrader.Application.Parsers;

public class VipFxParser : ISignalParser
{
    private readonly ILogger<VipFxParser> _logger;

    public VipFxParser(ILogger<VipFxParser> logger)
    {
        _logger = logger;
    }

    public bool CanParse(string providerChannelId)
    {
        // REMOVED TestChannel - now handled by TestChannelParser
        var canParse = providerChannelId == "-1001138473049";  // VIPFX only

        _logger.LogInformation("VipFxParser.CanParse({Channel}): {Result}", providerChannelId, canParse);

        return canParse;
    }

    public async Task<ParsedSignal?> ParseAsync(string message, string providerChannelId, byte[]? imageData = null)
    {
        try
        {
            _logger.LogInformation("VipFxParser: Starting parse attempt");

            // VIPFX format: "We are selling EURUSD now at 1.16359 Take profit at: 1.14965 Stop loss at: 1.17034"
            var patterns = new[]
            {
                @"We are (selling|buying) (\w+) now at ([\d.]+).*?Take profit at:\s*([\d.]+).*?Stop loss at:\s*([\d.]+)",
                @"We\s+are\s+(selling|buying)\s+(\w+)\s+now\s+at\s+([\d.]+)[\s\S]*?Take\s+profit\s+at:\s*([\d.]+)[\s\S]*?Stop\s+loss\s+at:\s*([\d.]+)",
            };

            Match? match = null;

            for (int i = 0; i < patterns.Length; i++)
            {
                match = Regex.Match(message, patterns[i], RegexOptions.Singleline | RegexOptions.IgnoreCase);
                if (match.Success)
                {
                    _logger.LogInformation("VipFxParser: ✅ Pattern {Pattern} MATCHED!", i + 1);
                    break;
                }
            }

            if (match == null || !match.Success)
            {
                _logger.LogWarning("VipFxParser: Pattern did not match");
                // Try to extract with fallback: direction, asset, entry, then set TP/SL 30 pips away
                var fallbackPattern = @"We are (selling|buying) (\w+) now at ([\d.]+)";
                var fallbackMatch = Regex.Match(message, fallbackPattern, RegexOptions.Singleline | RegexOptions.IgnoreCase);
                if (!fallbackMatch.Success)
                {
                    return null;
                }
                var directionFallback = fallbackMatch.Groups[1].Value.ToLower() == "selling" ? TradeDirection.Sell : TradeDirection.Buy;
                var assetFallback = fallbackMatch.Groups[2].Value.ToUpper();
                var entryFallback = decimal.Parse(fallbackMatch.Groups[3].Value, System.Globalization.CultureInfo.InvariantCulture);
                decimal pip = 0.0001m;
                if (assetFallback.EndsWith("JPY")) pip = 0.01m;
                decimal offset = pip * 30;
                decimal? tpFallback = null;
                decimal? slFallback = null;
                if (directionFallback == TradeDirection.Buy)
                {
                    tpFallback = entryFallback + offset;
                    slFallback = entryFallback - offset;
                }
                else
                {
                    tpFallback = entryFallback - offset;
                    slFallback = entryFallback + offset;
                }
                _logger.LogInformation("VipFxParser: Fallback parse - {Asset} {Dir} @ {Entry}, TP: {TP}, SL: {SL}",
                    assetFallback, directionFallback, entryFallback, tpFallback, slFallback);
                return new ParsedSignal
                {
                    ProviderChannelId = providerChannelId,
                    ProviderName = "VIPFX",
                    Asset = assetFallback,
                    Direction = directionFallback,
                    EntryPrice = entryFallback,
                    TakeProfit = tpFallback,
                    StopLoss = slFallback,
                    SignalType = SignalType.Text,
                    ReceivedAt = DateTime.UtcNow
                };
            }

            var direction = match.Groups[1].Value.ToLower() == "selling" ? TradeDirection.Sell : TradeDirection.Buy;
            var asset = match.Groups[2].Value.ToUpper();
            var entry = decimal.Parse(match.Groups[3].Value, System.Globalization.CultureInfo.InvariantCulture);
            decimal? tp = null;
            decimal? sl = null;
            if (decimal.TryParse(match.Groups[4].Value, System.Globalization.NumberStyles.Any, System.Globalization.CultureInfo.InvariantCulture, out var parsedTp))
                tp = parsedTp;
            if (decimal.TryParse(match.Groups[5].Value, System.Globalization.NumberStyles.Any, System.Globalization.CultureInfo.InvariantCulture, out var parsedSl))
                sl = parsedSl;

            // Fallback if TP/SL missing
            decimal pipSize = asset.EndsWith("JPY") ? 0.01m : 0.0001m;
            decimal pipOffset = pipSize * 30;
            if (!tp.HasValue)
                tp = direction == TradeDirection.Buy ? entry + pipOffset : entry - pipOffset;
            if (!sl.HasValue)
                sl = direction == TradeDirection.Buy ? entry - pipOffset : entry + pipOffset;

            _logger.LogInformation("VipFxParser: Successfully parsed - {Asset} {Dir} @ {Entry}, TP: {TP}, SL: {SL}",
                asset, direction, entry, tp, sl);

            return new ParsedSignal
            {
                ProviderChannelId = providerChannelId,
                ProviderName = "VIPFX",
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
            _logger.LogError(ex, "Error parsing VIPFX signal");
            return null;
        }
    }
}
