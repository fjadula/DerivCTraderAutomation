using System.Text.RegularExpressions;
using DerivCTrader.Application.Interfaces;
using DerivCTrader.Domain.Entities;
using DerivCTrader.Domain.Enums;
using Microsoft.Extensions.Logging;

namespace DerivCTrader.Application.Parsers;

/// <summary>
/// Parser for TradingHubVIP channel
/// Format: "BUY XAUUSD FOR 4238 OTHER LIMIT 4235 SL@4233 Tp@4243 Tp-2@4290"
/// </summary>
public class TradingHubVipParser : ISignalParser
{
    private readonly ILogger<TradingHubVipParser> _logger;

    public TradingHubVipParser(ILogger<TradingHubVipParser> logger)
    {
        _logger = logger;
    }

    public bool CanParse(string providerChannelId)
    {
        var canParse = providerChannelId == "-1476865523"; // TradingHubVIP

        _logger.LogInformation("TradingHubVipParser.CanParse({Channel}): {Result}", providerChannelId, canParse);
        return canParse;
    }

    public async Task<ParsedSignal?> ParseAsync(string message, string providerChannelId, byte[]? imageData = null)
    {
        try
        {
            _logger.LogInformation("TradingHubVipParser: Starting parse attempt");
            _logger.LogInformation("TradingHubVipParser: Message: {Message}", message);

            // Format: "BUY XAUUSD FOR 4238 OTHER LIMIT 4235 SL@4233 Tp@4243 Tp-2@4290"
            var pattern = @"(BUY|SELL)\s+(\w+)\s+FOR\s+([\d.]+).*?SL@([\d.]+).*?Tp@([\d.]+)";
            var match = Regex.Match(message, pattern, RegexOptions.IgnoreCase | RegexOptions.Singleline);

            if (!match.Success)
            {
                _logger.LogWarning("TradingHubVipParser: Pattern did not match");
                return null;
            }

            _logger.LogInformation("TradingHubVipParser: Pattern matched!");

            var asset = match.Groups[2].Value.ToUpper();
            var direction = match.Groups[1].Value.ToUpper() == "BUY" ? TradeDirection.Buy : TradeDirection.Sell;
            var entry = decimal.Parse(match.Groups[3].Value);
            var sl = decimal.Parse(match.Groups[4].Value);
            var tp = decimal.Parse(match.Groups[5].Value);

            _logger.LogInformation("TradingHubVipParser: Parsed - {Asset} {Direction} @ {Entry}, TP: {TP}, SL: {SL}",
                asset, direction, entry, tp, sl);

            return new ParsedSignal
            {
                ProviderChannelId = providerChannelId,
                ProviderName = "TradingHubVIP",
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
            _logger.LogError(ex, "Error parsing TradingHubVIP signal");
            return null;
        }
    }
}
