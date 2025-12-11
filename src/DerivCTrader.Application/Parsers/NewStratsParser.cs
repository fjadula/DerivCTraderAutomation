using System.Text.RegularExpressions;
using DerivCTrader.Application.Interfaces;
using DerivCTrader.Domain.Entities;
using DerivCTrader.Domain.Enums;
using Microsoft.Extensions.Logging;

namespace DerivCTrader.Application.Parsers;

/// <summary>
/// Parser for NewStrats Telegram channel (-1628868943 or -1001628868943)
/// Handles multiple strategies (1minbreakout, etc.) from the same channel
/// Strategy name is extracted from "Strat:" field and used as provider name
/// 
/// Format: OPEN EUR/CAD CALL Strat:1minbreakout
///         OPEN EUR/USD PUT Strat:1minbreakout
/// 
/// These signals have NO entry price, so they are treated as PureBinary signals
/// that skip cTrader and go directly to Deriv execution.
/// 
/// Note: Telegram channel IDs can appear with or without the -100 prefix
/// depending on which account is viewing the channel
/// </summary>
public class NewStratsParser : ISignalParser
{
    private readonly ILogger<NewStratsParser> _logger;
    
    // Accept both ID formats (with and without -100 prefix)
    private static readonly string[] ValidChannelIds = { "-1628868943", "-1001628868943" };

    public NewStratsParser(ILogger<NewStratsParser> logger)
    {
        _logger = logger;
    }

    public bool CanParse(string providerChannelId)
    {
        return ValidChannelIds.Contains(providerChannelId);
    }

    public Task<ParsedSignal?> ParseAsync(string message, string providerChannelId, byte[]? imageData = null)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(message))
            {
                _logger.LogWarning("Empty message received from NewStrats channel");
                return Task.FromResult<ParsedSignal?>(null);
            }

            _logger.LogInformation("Parsing NewStrats signal: {Message}", message);

            // Pattern: OPEN EUR/CAD CALL Strat:1minbreakout OR OPEN Volatility75 CALL  Strat:1minbreakout
            // Capture: action, asset, direction, strategy
            // Asset can be: EUR/CAD (forex) or Volatility75 (synthetic)
            // \s+ handles variable whitespace (including double spaces)
            var pattern = @"^(OPEN|CLOSE)\s+([A-Z]{3}/[A-Z]{3}|[A-Z][a-z]+\d*)\s+(CALL|PUT)\s+Strat:(\w+)";
            var match = Regex.Match(message, pattern, RegexOptions.IgnoreCase | RegexOptions.Multiline);

            if (!match.Success)
            {
                _logger.LogWarning("Message does not match NewStrats format: {Message}", message);
                return Task.FromResult<ParsedSignal?>(null);
            }

            var action = match.Groups[1].Value.ToUpper();
            var asset = match.Groups[2].Value.ToUpper();
            var direction = match.Groups[3].Value.ToUpper();
            var strategyName = match.Groups[4].Value.ToLower();

            // Validate action
            if (action != "OPEN" && action != "CLOSE")
            {
                _logger.LogWarning("Invalid action: {Action}", action);
                return Task.FromResult<ParsedSignal?>(null);
            }

            // Parse direction
            TradeDirection tradeDirection;
            if (direction == "CALL")
            {
                tradeDirection = TradeDirection.Buy;
            }
            else if (direction == "PUT")
            {
                tradeDirection = TradeDirection.Sell;
            }
            else
            {
                _logger.LogWarning("Invalid direction: {Direction}", direction);
                return Task.FromResult<ParsedSignal?>(null);
            }

            // Determine signal type - NewStrats signals have no entry price,
            // so they are pure binary signals that skip cTrader
            SignalType signalType = SignalType.PureBinary;

            var signal = new ParsedSignal
            {
                Asset = asset,
                Direction = tradeDirection,
                SignalType = signalType,
                ProviderName = strategyName, // Use strategy name as provider (e.g., "1minbreakout")
                ProviderChannelId = providerChannelId,
                RawMessage = message,
                ReceivedAt = DateTime.UtcNow
            };

            _logger.LogInformation(
                "Successfully parsed NewStrats PURE BINARY signal - Asset: {Asset}, Direction: {Direction}, Action: {Action}, Strategy: {Strategy}",
                signal.Asset, signal.Direction, action, strategyName);

            return Task.FromResult<ParsedSignal?>(signal);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error parsing NewStrats signal: {Message}", message);
            return Task.FromResult<ParsedSignal?>(null);
        }
    }
}
