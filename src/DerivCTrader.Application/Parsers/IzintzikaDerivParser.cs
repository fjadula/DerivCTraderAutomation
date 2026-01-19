using System.Text.RegularExpressions;
using DerivCTrader.Domain.Entities;
using DerivCTrader.Domain.Enums;
using Microsoft.Extensions.Logging;

namespace DerivCTrader.Application.Parsers;

/// <summary>
/// Parser for IzintzikaDeriv Telegram channel (-1001666286465)
///
/// Signal format (batch of entry orders):
/// Área Restrita 14/01/2026
/// ...
/// 📊EURUSD PUT 1.16523
/// 📊EURUSD CALL 1.16414
/// 📊EURJPY PUT 185.083
/// 📊GBPJPY CALL 213.450
/// 📊BTCUSD PUT 95240.4
/// ...
///
/// Each line is a pending order with Asset, Direction (PUT/CALL), and Entry Price.
/// No SL/TP provided - defaults are calculated based on pip distance.
/// </summary>
public class IzintzikaDerivParser
{
    private const string CHANNEL_ID = "-1001666286465";
    private const string CHANNEL_ID_SHORT = "-1666286465";
    private const int DEFAULT_PIPS = 30; // Default SL/TP distance in pips

    private readonly ILogger<IzintzikaDerivParser> _logger;

    // Regex pattern: 📊{ASSET} {PUT|CALL} {PRICE}
    // Handles: 📊EURUSD PUT 1.16523, 📊BTCUSD CALL 94508.2
    private static readonly Regex SignalLinePattern = new(
        @"📊\s*([A-Z]{6,})\s+(PUT|CALL)\s+([\d.]+)",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    public IzintzikaDerivParser(ILogger<IzintzikaDerivParser> logger)
    {
        _logger = logger;
    }

    /// <summary>
    /// Check if this message is from the IzintzikaDeriv channel and contains signal data
    /// </summary>
    public bool CanParse(string providerChannelId, string message)
    {
        var isChannel = providerChannelId == CHANNEL_ID ||
                        providerChannelId == CHANNEL_ID_SHORT ||
                        providerChannelId.EndsWith("1666286465");

        var hasSignalContent = message.Contains("📊") && SignalLinePattern.IsMatch(message);

        return isChannel && hasSignalContent;
    }

    /// <summary>
    /// Parse a batch of entry order signals from an IzintzikaDeriv message.
    /// Returns multiple ParsedSignal objects, one for each signal line.
    /// </summary>
    public List<ParsedSignal> ParseBatch(string message, int? telegramMessageId = null)
    {
        var signals = new List<ParsedSignal>();

        try
        {
            _logger.LogInformation("IzintzikaDerivParser: Parsing batch message");

            // Parse each signal line
            var matches = SignalLinePattern.Matches(message);

            foreach (Match match in matches)
            {
                var asset = match.Groups[1].Value.ToUpperInvariant();  // "EURUSD"
                var directionStr = match.Groups[2].Value;              // "PUT" or "CALL"
                var priceStr = match.Groups[3].Value;                  // "1.16523"

                // Parse entry price
                if (!decimal.TryParse(priceStr, System.Globalization.NumberStyles.Any,
                    System.Globalization.CultureInfo.InvariantCulture, out var entryPrice))
                {
                    _logger.LogWarning("IzintzikaDerivParser: Failed to parse price '{Price}' for {Asset}", priceStr, asset);
                    continue;
                }

                // Map PUT/CALL to Sell/Buy for forex (cTrader uses Buy/Sell)
                // PUT = expect price to go DOWN = SELL
                // CALL = expect price to go UP = BUY
                var direction = directionStr.Equals("CALL", StringComparison.OrdinalIgnoreCase)
                    ? TradeDirection.Buy
                    : TradeDirection.Sell;

                // Calculate default SL/TP based on asset type
                var (stopLoss, takeProfit) = CalculateDefaultStops(asset, entryPrice, direction);

                var signal = new ParsedSignal
                {
                    Asset = asset,
                    Direction = direction,
                    EntryPrice = entryPrice,
                    StopLoss = stopLoss,
                    TakeProfit = takeProfit,
                    ProviderChannelId = CHANNEL_ID,
                    ProviderName = "IzintzikaDeriv",
                    SignalType = SignalType.Text, // Forex signals with entry price
                    ReceivedAt = DateTime.UtcNow,
                    TelegramMessageId = telegramMessageId,
                    RawMessage = match.Value,
                    Processed = false
                };

                signals.Add(signal);

                _logger.LogDebug(
                    "IzintzikaDerivParser: Parsed signal - {Asset} {Direction} @ {Entry}, SL: {SL}, TP: {TP}",
                    asset, direction, entryPrice, stopLoss, takeProfit);
            }

            _logger.LogInformation(
                "IzintzikaDerivParser: Successfully parsed {Count} signals",
                signals.Count);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "IzintzikaDerivParser: Error parsing batch message");
        }

        return signals;
    }

    /// <summary>
    /// Calculate default SL and TP based on asset type and direction.
    /// Uses pip-based calculation for forex pairs.
    /// </summary>
    private static (decimal? StopLoss, decimal? TakeProfit) CalculateDefaultStops(
        string asset, decimal entryPrice, TradeDirection direction)
    {
        // Determine pip value based on asset
        decimal pipSize;
        int pipDistance = DEFAULT_PIPS;

        if (asset.Contains("JPY"))
        {
            // JPY pairs: pip = 0.01
            pipSize = 0.01m;
        }
        else if (asset.StartsWith("BTC") || asset.StartsWith("XBT"))
        {
            // Bitcoin: use larger distance (100 points)
            pipSize = 1m;
            pipDistance = 100;
        }
        else if (asset.StartsWith("XAU") || asset.Contains("GOLD"))
        {
            // Gold: pip = 0.1
            pipSize = 0.1m;
            pipDistance = 50;
        }
        else
        {
            // Standard forex pairs: pip = 0.0001
            pipSize = 0.0001m;
        }

        var offset = pipSize * pipDistance;

        decimal? stopLoss;
        decimal? takeProfit;

        if (direction == TradeDirection.Buy)
        {
            takeProfit = entryPrice + offset;
            stopLoss = entryPrice - offset;
        }
        else // Sell
        {
            takeProfit = entryPrice - offset;
            stopLoss = entryPrice + offset;
        }

        return (stopLoss, takeProfit);
    }
}
