using System.Text.RegularExpressions;
using DerivCTrader.Application.Interfaces;
using DerivCTrader.Domain.Entities;
using DerivCTrader.Domain.Enums;
using Microsoft.Extensions.Logging;

namespace DerivCTrader.Application.Parsers;

/// <summary>
/// Parser for Dasha Trade Telegram channel signals.
///
/// Signal format: "USDJPY m15 down" or "EURUSD M5 up"
/// - Asset: 6-7 character forex pair (e.g., USDJPY, EURUSD, GBPUSD)
/// - Timeframe: m5, m15, m30, h1, etc. (case insensitive)
/// - Direction: up or down (case insensitive)
///
/// This parser is specifically for the selective martingale strategy.
/// Signals are NOT executed immediately - they are stored for expiry evaluation.
/// </summary>
public class DashaTradeParser : ISignalParser
{
    private const string DASHA_TRADE_CHANNEL_ID = "-1001570351142";
    private readonly ILogger<DashaTradeParser> _logger;

    // Pattern: ASSET TIMEFRAME DIRECTION
    // Examples: "USDJPY m15 down", "EURUSD M5 up", "GBPJPY h1 DOWN"
    private static readonly Regex SignalPattern = new(
        @"^([A-Z]{6,7})\s+([mMhHdD]\d+)\s+(up|down)$",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    // Alternative pattern with different word order or extra text
    private static readonly Regex AltSignalPattern = new(
        @"([A-Z]{6,7})\s+([mMhHdD]\d+)\s+(up|down)",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    public DashaTradeParser(ILogger<DashaTradeParser> logger)
    {
        _logger = logger;
    }

    public bool CanParse(string providerChannelId)
    {
        var canParse = providerChannelId == DASHA_TRADE_CHANNEL_ID;
        _logger.LogDebug("DashaTradeParser.CanParse({Channel}): {Result}", providerChannelId, canParse);
        return canParse;
    }

    public Task<ParsedSignal?> ParseAsync(string message, string providerChannelId, byte[]? imageData = null)
    {
        try
        {
            _logger.LogInformation("DashaTradeParser: Parsing message from {Channel}", providerChannelId);
            _logger.LogDebug("DashaTradeParser: Message content: {Message}", message);

            if (string.IsNullOrWhiteSpace(message))
            {
                _logger.LogDebug("DashaTradeParser: Empty message");
                return Task.FromResult<ParsedSignal?>(null);
            }

            // Normalize: trim and collapse whitespace
            var normalizedMessage = Regex.Replace(message.Trim(), @"\s+", " ");

            // Try exact pattern first
            var match = SignalPattern.Match(normalizedMessage);

            // If no exact match, try alternative pattern (allows extra text)
            if (!match.Success)
            {
                match = AltSignalPattern.Match(normalizedMessage);
            }

            if (!match.Success)
            {
                _logger.LogDebug("DashaTradeParser: No pattern match for message: {Message}", message);
                return Task.FromResult<ParsedSignal?>(null);
            }

            var asset = match.Groups[1].Value.ToUpperInvariant();
            var timeframe = match.Groups[2].Value.ToUpperInvariant();
            var directionStr = match.Groups[3].Value.ToUpperInvariant();

            // Parse direction: UP -> Buy/Call, DOWN -> Sell/Put
            var direction = directionStr == "UP" ? TradeDirection.Buy : TradeDirection.Sell;

            // Parse timeframe to minutes
            var expiryMinutes = ParseTimeframeToMinutes(timeframe);

            _logger.LogInformation(
                "DashaTradeParser: Parsed signal - Asset={Asset}, Direction={Direction}, Timeframe={Timeframe}, ExpiryMinutes={Expiry}",
                asset, directionStr, timeframe, expiryMinutes);

            // Create ParsedSignal with Dasha-specific settings
            // Note: SignalType.Text is used but the TelegramSignalScraperService will detect
            // this is from DashaTrade channel and route it to DashaPendingSignals instead
            var signal = new ParsedSignal
            {
                Asset = asset,
                Direction = direction,
                Timeframe = timeframe,
                ProviderChannelId = providerChannelId,
                ProviderName = "DashaTrade",
                SignalType = SignalType.PureBinary, // Will be executed as Rise/Fall on Deriv
                ReceivedAt = DateTime.UtcNow,
                RawMessage = message
            };

            return Task.FromResult<ParsedSignal?>(signal);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "DashaTradeParser: Error parsing message");
            return Task.FromResult<ParsedSignal?>(null);
        }
    }

    /// <summary>
    /// Parses timeframe string to minutes.
    /// M5 = 5, M15 = 15, M30 = 30, H1 = 60, H4 = 240, D1 = 1440
    /// </summary>
    private static int ParseTimeframeToMinutes(string timeframe)
    {
        var normalized = timeframe.ToUpperInvariant();

        // Extract the unit (M, H, D) and the number
        var unit = normalized[0];
        var numberStr = normalized.Substring(1);

        if (!int.TryParse(numberStr, out var number))
        {
            return 15; // Default to 15 minutes
        }

        return unit switch
        {
            'M' => number,           // Minutes
            'H' => number * 60,      // Hours
            'D' => number * 1440,    // Days
            _ => 15                  // Default
        };
    }

    /// <summary>
    /// Gets the raw direction string (UP/DOWN) from a ParsedSignal.
    /// Used for Dasha Trade logic since we need UP/DOWN not BUY/SELL.
    /// </summary>
    public static string GetDashaDirection(TradeDirection direction)
    {
        return direction == TradeDirection.Buy ? "UP" : "DOWN";
    }
}
