using System.Text.RegularExpressions;
using DerivCTrader.Application.Interfaces;
using DerivCTrader.Domain.Entities;
using DerivCTrader.Domain.Enums;
using Microsoft.Extensions.Logging;

namespace DerivCTrader.Application.Parsers;

/// <summary>
/// Parser for Synthetic Indices Trader channel
/// Format: "Sell : Volatility 75 Index Zone : 43130.00 - 43750.00 TP1 : 42570.00 SL : 44240.00"
/// </summary>
public class SyntheticIndicesParser : ISignalParser
{
    private readonly ILogger<SyntheticIndicesParser> _logger;

    public SyntheticIndicesParser(ILogger<SyntheticIndicesParser> logger)
    {
        _logger = logger;
    }

    public bool CanParse(string providerChannelId)
    {
        var canParse = providerChannelId == "-1003204276456"; // SyntheticIndicesTrader

        _logger.LogInformation("SyntheticIndicesParser.CanParse({Channel}): {Result}", providerChannelId, canParse);
        return canParse;
    }

    public async Task<ParsedSignal?> ParseAsync(string message, string providerChannelId, byte[]? imageData = null)
    {
        try
        {
            _logger.LogInformation("SyntheticIndicesParser: Starting parse attempt");
            _logger.LogInformation("SyntheticIndicesParser: Message: {Message}", message);

            // Format: "Sell : Volatility 75 Index Zone : 43130.00 - 43750.00 TP1 : 42570.00 TP2 : 42000.00 TP3 : 40900.00 SL : 44240.00"
            // Also handles: "Buy : Crash 500 Index"
            var pattern = @"(Buy|Sell)\s*:\s*(.+?)(?:Zone|Entry)\s*:\s*([\d.]+)\s*-\s*([\d.]+).*?TP1?\s*:\s*([\d.]+).*?SL\s*:\s*([\d.]+)";
            var match = Regex.Match(message, pattern, RegexOptions.IgnoreCase | RegexOptions.Singleline);

            if (!match.Success)
            {
                _logger.LogWarning("SyntheticIndicesParser: Pattern did not match");
                return null;
            }

            _logger.LogInformation("SyntheticIndicesParser: Pattern matched!");

            var direction = match.Groups[1].Value.ToUpper() == "BUY" ? TradeDirection.Buy : TradeDirection.Sell;
            var rawAsset = match.Groups[2].Value.Trim();

            // Convert "Volatility 75 Index" to "Volatility 75 (1s) Index" (Deriv format)
            // Or keep as-is and let order execution handle the mapping
            var asset = ConvertToDerivAsset(rawAsset);

            var entryMin = decimal.Parse(match.Groups[3].Value, System.Globalization.CultureInfo.InvariantCulture);
            var entryMax = decimal.Parse(match.Groups[4].Value, System.Globalization.CultureInfo.InvariantCulture);
            var entry = (entryMin + entryMax) / 2; // Use middle of zone
            var tp = decimal.Parse(match.Groups[5].Value, System.Globalization.CultureInfo.InvariantCulture);
            var sl = decimal.Parse(match.Groups[6].Value, System.Globalization.CultureInfo.InvariantCulture);

            _logger.LogInformation("SyntheticIndicesParser: Parsed - {Asset} {Direction} @ {Entry}, TP: {TP}, SL: {SL}",
                asset, direction, entry, tp, sl);

            return new ParsedSignal
            {
                ProviderChannelId = providerChannelId,
                ProviderName = "SyntheticIndicesTrader",
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
            _logger.LogError(ex, "Error parsing Synthetic Indices signal");
            return null;
        }
    }

    private string ConvertToDerivAsset(string rawAsset)
    {
        // Map signal names to Deriv asset names
        // You may need to adjust these mappings based on actual Deriv asset names

        var normalized = rawAsset.ToUpper().Replace(" ", "");

        if (normalized.Contains("VOLATILITY75"))
            return "Volatility 75 (1s) Index"; // Deriv uses this format

        if (normalized.Contains("VOLATILITY100"))
            return "Volatility 100 (1s) Index";

        if (normalized.Contains("VOLATILITY10"))
            return "Volatility 10 (1s) Index";

        if (normalized.Contains("CRASH500"))
            return "Crash 500 Index";

        if (normalized.Contains("CRASH1000"))
            return "Crash 1000 Index";

        if (normalized.Contains("BOOM500"))
            return "Boom 500 Index";

        if (normalized.Contains("BOOM1000"))
            return "Boom 1000 Index";

        // Return as-is if no mapping found
        return rawAsset;
    }
}