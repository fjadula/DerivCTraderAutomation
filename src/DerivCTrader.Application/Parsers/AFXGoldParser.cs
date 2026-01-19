using System.Globalization;
using System.Text.RegularExpressions;
using DerivCTrader.Application.Interfaces;
using DerivCTrader.Domain.Entities;
using DerivCTrader.Domain.Enums;
using Microsoft.Extensions.Logging;

namespace DerivCTrader.Application.Parsers;

/// <summary>
/// Parser for AFXGold Telegram channel signals
///
/// Signal format:
/// GOLD BUY NOW
/// Gold Buy Zone 4484 - 4480
/// SL : 4477
/// TP1 : 4487
/// TP2 : 4489
/// TP3 : 4491
/// TP4 : Hold  (ignored)
///
/// This is an immediate execution signal (market order).
/// Default binary expiry: 40 minutes
/// </summary>
public class AFXGoldParser : ISignalParser
{
    private const string AFX_GOLD_CHANNEL_ID = "-1003367695960";
    private readonly ILogger<AFXGoldParser> _logger;

    public AFXGoldParser(ILogger<AFXGoldParser> logger)
    {
        _logger = logger;
    }

    public bool CanParse(string providerChannelId)
    {
        var canParse = providerChannelId == AFX_GOLD_CHANNEL_ID;
        _logger.LogDebug("AFXGoldParser.CanParse({Channel}): {Result}", providerChannelId, canParse);
        return canParse;
    }

    public Task<ParsedSignal?> ParseAsync(string message, string providerChannelId, byte[]? imageData = null)
    {
        try
        {
            _logger.LogInformation("AFXGoldParser: Parsing message from {Channel}", providerChannelId);
            _logger.LogDebug("AFXGoldParser: Message content:\n{Message}", message);

            // Normalize the message
            var normalizedMessage = message.ToUpperInvariant();

            // Pattern 1: "GOLD BUY NOW" or "GOLD SELL NOW" (immediate execution)
            var directionMatch = Regex.Match(normalizedMessage, @"GOLD\s+(BUY|SELL)\s+NOW", RegexOptions.IgnoreCase);

            // Pattern 2: Check for "Gold Buy Zone" or "Gold Sell Zone" if first pattern didn't match
            if (!directionMatch.Success)
            {
                directionMatch = Regex.Match(normalizedMessage, @"GOLD\s+(BUY|SELL)\s+ZONE", RegexOptions.IgnoreCase);
            }

            if (!directionMatch.Success)
            {
                _logger.LogDebug("AFXGoldParser: No direction pattern found");
                return Task.FromResult<ParsedSignal?>(null);
            }

            var directionStr = directionMatch.Groups[1].Value.ToUpperInvariant();
            var direction = directionStr == "BUY" ? TradeDirection.Buy : TradeDirection.Sell;

            _logger.LogInformation("AFXGoldParser: Direction detected: {Direction}", direction);

            // Parse Zone (entry price range): "Zone 4484 - 4480" or "Zone 4484-4480"
            decimal? entryPrice = null;
            var zoneMatch = Regex.Match(message, @"Zone\s*([\d.]+)\s*[-–]\s*([\d.]+)", RegexOptions.IgnoreCase);
            if (zoneMatch.Success)
            {
                if (decimal.TryParse(zoneMatch.Groups[1].Value, NumberStyles.Any, CultureInfo.InvariantCulture, out var zone1) &&
                    decimal.TryParse(zoneMatch.Groups[2].Value, NumberStyles.Any, CultureInfo.InvariantCulture, out var zone2))
                {
                    // For BUY: use lower zone price as entry, For SELL: use higher zone price
                    entryPrice = direction == TradeDirection.Buy
                        ? Math.Min(zone1, zone2)
                        : Math.Max(zone1, zone2);
                    _logger.LogInformation("AFXGoldParser: Zone parsed: {Zone1}-{Zone2}, Entry: {Entry}", zone1, zone2, entryPrice);
                }
            }

            // Parse SL: "SL : 4477" or "SL: 4477" or "SL 4477"
            decimal? stopLoss = null;
            var slMatch = Regex.Match(message, @"SL\s*[:\s]\s*([\d.]+)", RegexOptions.IgnoreCase);
            if (slMatch.Success && decimal.TryParse(slMatch.Groups[1].Value, NumberStyles.Any, CultureInfo.InvariantCulture, out var sl))
            {
                stopLoss = sl;
                _logger.LogInformation("AFXGoldParser: SL parsed: {SL}", stopLoss);
            }

            // Parse TP1: "TP1 : 4487" or "TP1: 4487" or "TP1 4487"
            decimal? takeProfit1 = null;
            var tp1Match = Regex.Match(message, @"TP1\s*[:\s]\s*([\d.]+)", RegexOptions.IgnoreCase);
            if (tp1Match.Success && decimal.TryParse(tp1Match.Groups[1].Value, NumberStyles.Any, CultureInfo.InvariantCulture, out var tp1))
            {
                takeProfit1 = tp1;
                _logger.LogInformation("AFXGoldParser: TP1 parsed: {TP1}", takeProfit1);
            }

            // Parse TP2: "TP2 : 4489"
            decimal? takeProfit2 = null;
            var tp2Match = Regex.Match(message, @"TP2\s*[:\s]\s*([\d.]+)", RegexOptions.IgnoreCase);
            if (tp2Match.Success && decimal.TryParse(tp2Match.Groups[1].Value, NumberStyles.Any, CultureInfo.InvariantCulture, out var tp2))
            {
                takeProfit2 = tp2;
                _logger.LogInformation("AFXGoldParser: TP2 parsed: {TP2}", takeProfit2);
            }

            // Parse TP3: "TP3 : 4491"
            decimal? takeProfit3 = null;
            var tp3Match = Regex.Match(message, @"TP3\s*[:\s]\s*([\d.]+)", RegexOptions.IgnoreCase);
            if (tp3Match.Success && decimal.TryParse(tp3Match.Groups[1].Value, NumberStyles.Any, CultureInfo.InvariantCulture, out var tp3))
            {
                takeProfit3 = tp3;
                _logger.LogInformation("AFXGoldParser: TP3 parsed: {TP3}", takeProfit3);
            }

            // TP4 is "Hold" - explicitly ignore it (no parsing needed)
            // The signal says "TP4 : Hold" which means hold until manual close

            // Validate we have minimum required data
            if (!stopLoss.HasValue)
            {
                _logger.LogWarning("AFXGoldParser: Missing SL, cannot parse signal");
                return Task.FromResult<ParsedSignal?>(null);
            }

            if (!takeProfit1.HasValue)
            {
                _logger.LogWarning("AFXGoldParser: Missing TP1, cannot parse signal");
                return Task.FromResult<ParsedSignal?>(null);
            }

            // Signal is valid - this is an immediate execution (market order)
            // "BUY NOW" or "SELL NOW" indicates market execution, not pending order
            var signal = new ParsedSignal
            {
                Asset = "XAUUSD", // Gold
                Direction = direction,
                EntryPrice = entryPrice, // Zone price (for reference, but we execute at market)
                StopLoss = stopLoss,
                TakeProfit = takeProfit1,
                TakeProfit2 = takeProfit2,
                TakeProfit3 = takeProfit3,
                // TakeProfit4 is "Hold" - not set
                ProviderChannelId = providerChannelId,
                ProviderName = "AFXGold",
                SignalType = SignalType.MarketExecution, // Immediate execution
                ReceivedAt = DateTime.UtcNow,
                RawMessage = message
            };

            _logger.LogInformation(
                "AFXGoldParser: Successfully parsed - {Asset} {Direction} Entry={Entry} SL={SL} TP1={TP1} TP2={TP2} TP3={TP3}",
                signal.Asset, signal.Direction, signal.EntryPrice, signal.StopLoss,
                signal.TakeProfit, signal.TakeProfit2, signal.TakeProfit3);

            return Task.FromResult<ParsedSignal?>(signal);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "AFXGoldParser: Error parsing signal");
            return Task.FromResult<ParsedSignal?>(null);
        }
    }
}
