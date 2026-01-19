using System.Text.RegularExpressions;
using DerivCTrader.Application.Interfaces;
using DerivCTrader.Domain.Entities;
using DerivCTrader.Domain.Enums;
using Microsoft.Extensions.Logging;

namespace DerivCTrader.Application.Parsers;

/// <summary>
/// Parser for DerivPlus Telegram channel signals
///
/// Signal format:
/// CALL Volatility 75 Index
/// Expiry: 5 Minutes
///
/// or:
///
/// PUT Volatility 300 Index
/// Expiry :10 Minutes
///
/// This is a pure binary signal (no cTrader, straight to Deriv binary).
/// Uses MT5 symbol names which map to Deriv symbols.
/// </summary>
public class DerivPlusParser : ISignalParser
{
    private const string DERIV_PLUS_CHANNEL_ID = "-1628868943";
    private const string DERIV_PLUS_CHANNEL_ID_SUPERGROUP = "-1001628868943";
    private readonly ILogger<DerivPlusParser> _logger;

    // MT5 symbol name to Deriv symbol mapping
    private static readonly Dictionary<string, string> SymbolMapping = new(StringComparer.OrdinalIgnoreCase)
    {
        { "Volatility 10 Index", "R_10" },
        { "Volatility 25 Index", "R_25" },
        { "Volatility 50 Index", "R_50" },
        { "Volatility 75 Index", "R_75" },
        { "Volatility 100 Index", "R_100" },
        { "Volatility 10 (1s) Index", "1HZ10V" },
        { "Volatility 25 (1s) Index", "1HZ25V" },
        { "Volatility 50 (1s) Index", "1HZ50V" },
        { "Volatility 75 (1s) Index", "1HZ75V" },
        { "Volatility 100 (1s) Index", "1HZ100V" },
        { "Volatility 150 (1s) Index", "1HZ150V" },
        { "Volatility 200 (1s) Index", "1HZ200V" },
        { "Volatility 250 (1s) Index", "1HZ250V" },
        { "Volatility 300 (1s) Index", "1HZ300V" },
        { "Volatility 150 Index", "R_150" },
        { "Volatility 200 Index", "R_200" },
        { "Volatility 250 Index", "R_250" },
        { "Volatility 300 Index", "R_300" },
        { "Boom 300 Index", "BOOM300" },
        { "Boom 500 Index", "BOOM500" },
        { "Boom 1000 Index", "BOOM1000" },
        { "Crash 300 Index", "CRASH300" },
        { "Crash 500 Index", "CRASH500" },
        { "Crash 1000 Index", "CRASH1000" },
        { "Jump 10 Index", "JD10" },
        { "Jump 25 Index", "JD25" },
        { "Jump 50 Index", "JD50" },
        { "Jump 75 Index", "JD75" },
        { "Jump 100 Index", "JD100" },
        { "Step Index", "stpRNG" },
        { "Range Break 100 Index", "RDBEAR" },
        { "Range Break 200 Index", "RDBULL" }
    };

    public DerivPlusParser(ILogger<DerivPlusParser> logger)
    {
        _logger = logger;
    }

    public bool CanParse(string providerChannelId)
    {
        var canParse = providerChannelId == DERIV_PLUS_CHANNEL_ID || providerChannelId == DERIV_PLUS_CHANNEL_ID_SUPERGROUP;
        _logger.LogDebug("DerivPlusParser.CanParse({Channel}): {Result}", providerChannelId, canParse);
        return canParse;
    }

    public Task<ParsedSignal?> ParseAsync(string message, string providerChannelId, byte[]? imageData = null)
    {
        try
        {
            _logger.LogInformation("DerivPlusParser: Parsing message from {Channel}", providerChannelId);
            _logger.LogDebug("DerivPlusParser: Message content:\n{Message}", message);

            // Pattern 1: "CALL Volatility 75 Index" or "PUT Volatility 300 Index"
            // The asset can be multi-word like "Volatility 75 Index" or "Volatility 300 (1s) Index"
            var directionPattern = @"^(CALL|PUT)\s+(.+?)(?:\r?\n|$)";
            var directionMatch = Regex.Match(message.Trim(), directionPattern, RegexOptions.IgnoreCase | RegexOptions.Multiline);

            if (!directionMatch.Success)
            {
                _logger.LogDebug("DerivPlusParser: No direction pattern found");
                return Task.FromResult<ParsedSignal?>(null);
            }

            var directionStr = directionMatch.Groups[1].Value.ToUpperInvariant();
            var rawAsset = directionMatch.Groups[2].Value.Trim();
            var direction = directionStr == "CALL" ? TradeDirection.Call : TradeDirection.Put;

            _logger.LogInformation("DerivPlusParser: Direction={Direction}, RawAsset={RawAsset}", directionStr, rawAsset);

            // Map MT5 symbol to Deriv symbol
            var derivSymbol = MapToDerivSymbol(rawAsset);
            if (string.IsNullOrEmpty(derivSymbol))
            {
                _logger.LogWarning("DerivPlusParser: Could not map asset '{Asset}' to Deriv symbol", rawAsset);
                return Task.FromResult<ParsedSignal?>(null);
            }

            _logger.LogInformation("DerivPlusParser: Mapped {RawAsset} -> {DerivSymbol}", rawAsset, derivSymbol);

            // Pattern 2: Parse expiry - "Expiry: 5 Minutes" or "Expiry :10 Minutes" or "Expiry: 5 Min"
            int expiryMinutes = 5; // Default
            var expiryPattern = @"Expiry\s*:\s*(\d+)\s*(?:Minutes?|Min)";
            var expiryMatch = Regex.Match(message, expiryPattern, RegexOptions.IgnoreCase);

            if (expiryMatch.Success && int.TryParse(expiryMatch.Groups[1].Value, out var parsedExpiry))
            {
                expiryMinutes = parsedExpiry;
                _logger.LogInformation("DerivPlusParser: Parsed expiry: {Expiry} minutes", expiryMinutes);
            }
            else
            {
                _logger.LogWarning("DerivPlusParser: Could not parse expiry, using default {Default} minutes", expiryMinutes);
            }

            // Store expiry in Timeframe field as "5M", "10M" format for BinaryExecutionService to use
            var timeframe = $"{expiryMinutes}M";

            var signal = new ParsedSignal
            {
                Asset = derivSymbol,
                Direction = direction,
                ProviderChannelId = providerChannelId,
                ProviderName = "DerivPlus",
                SignalType = SignalType.PureBinary,
                Timeframe = timeframe, // Store expiry here
                ReceivedAt = DateTime.UtcNow,
                RawMessage = message,
                // Pure binary signals don't need entry/SL/TP
                EntryPrice = null,
                StopLoss = null,
                TakeProfit = null
            };

            _logger.LogInformation(
                "DerivPlusParser: Successfully parsed - {Asset} {Direction} Expiry={Expiry}m",
                signal.Asset, signal.Direction, expiryMinutes);

            return Task.FromResult<ParsedSignal?>(signal);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "DerivPlusParser: Error parsing signal");
            return Task.FromResult<ParsedSignal?>(null);
        }
    }

    private string? MapToDerivSymbol(string mt5Symbol)
    {
        // Direct lookup
        if (SymbolMapping.TryGetValue(mt5Symbol, out var derivSymbol))
        {
            return derivSymbol;
        }

        // Try partial match for variations
        foreach (var kvp in SymbolMapping)
        {
            if (mt5Symbol.Contains(kvp.Key, StringComparison.OrdinalIgnoreCase) ||
                kvp.Key.Contains(mt5Symbol, StringComparison.OrdinalIgnoreCase))
            {
                return kvp.Value;
            }
        }

        // Try extracting number for volatility indices
        // e.g., "Volatility 75" -> R_75
        var volMatch = Regex.Match(mt5Symbol, @"Volatility\s*(\d+)", RegexOptions.IgnoreCase);
        if (volMatch.Success)
        {
            var number = volMatch.Groups[1].Value;
            // Check if it's a 1-second index
            if (mt5Symbol.Contains("1s", StringComparison.OrdinalIgnoreCase))
            {
                return $"1HZ{number}V";
            }
            return $"R_{number}";
        }

        // Return original if no mapping found (let Deriv handle it)
        return mt5Symbol;
    }
}
