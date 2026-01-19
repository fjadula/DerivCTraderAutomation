using System.Text.RegularExpressions;
using DerivCTrader.Application.Interfaces;
using DerivCTrader.Domain.Entities;
using DerivCTrader.Domain.Enums;
using Microsoft.Extensions.Logging;

namespace DerivCTrader.Application.Parsers;

/// <summary>
/// Parser for FX Trading Professor channel
/// Format: "GOLD BUY 4322
///          TP1 4326
///          TP2 4330
///          TP3 4336
///          TP4 4340
///          STOPLOSS 4310"
/// Market: Gold (XAUUSD)
/// Expiry: 45 minutes (fixed)
/// </summary>
public class FxTradingProfessorParser : ISignalParser
{
    private readonly ILogger<FxTradingProfessorParser> _logger;

    public FxTradingProfessorParser(ILogger<FxTradingProfessorParser> logger)
    {
        _logger = logger;
    }

    public bool CanParse(string providerChannelId)
    {
        var canParse = providerChannelId == "-1002242743399"; // FX Trading Professor
        _logger.LogInformation("FxTradingProfessorParser.CanParse({Channel}): {Result}", providerChannelId, canParse);
        return canParse;
    }

    public async Task<ParsedSignal?> ParseAsync(string message, string providerChannelId, byte[]? imageData = null)
    {
        try
        {
            _logger.LogInformation("FxTradingProfessorParser: Starting parse attempt");
            _logger.LogInformation("FxTradingProfessorParser: Message: {Message}", message);

            // Pattern: "GOLD BUY 4322" or "GOLD SELL 4300"
            var pattern = @"\b(GOLD|XAUUSD)\s+(BUY|SELL)\s+([\d.]+)";
            var match = Regex.Match(message, pattern, RegexOptions.IgnoreCase | RegexOptions.Singleline);

            if (!match.Success)
            {
                _logger.LogWarning("FxTradingProfessorParser: Main pattern did not match");
                return null;
            }

            var asset = "XAUUSD"; // Always map GOLD to XAUUSD
            var direction = match.Groups[2].Value.Equals("BUY", StringComparison.OrdinalIgnoreCase)
                ? TradeDirection.Buy
                : TradeDirection.Sell;
            var entry = decimal.Parse(match.Groups[3].Value, System.Globalization.CultureInfo.InvariantCulture);

            // Extract TPs
            var (tp1, tp2, tp3, tp4) = ExtractTakeProfits(message);

            // Extract StopLoss
            var slMatch = Regex.Match(message, @"\bSTOPLOSS\s+([\d.]+)", RegexOptions.IgnoreCase);
            decimal? sl = slMatch.Success
                ? decimal.Parse(slMatch.Groups[1].Value, System.Globalization.CultureInfo.InvariantCulture)
                : null;

            // Fallback if TP/SL missing (XAUUSD uses larger pip size)
            decimal pipOffset = 5m; // 5 points for gold
            if (!tp1.HasValue)
                tp1 = direction == TradeDirection.Buy ? entry + pipOffset : entry - pipOffset;
            if (!sl.HasValue)
                sl = direction == TradeDirection.Buy ? entry - pipOffset : entry + pipOffset;

            _logger.LogInformation("FxTradingProfessorParser: Parsed - {Asset} {Direction} @ {Entry}, TP1: {TP1}, TP2: {TP2}, TP3: {TP3}, TP4: {TP4}, SL: {SL}",
                asset, direction, entry, tp1, tp2, tp3, tp4, sl);

            return new ParsedSignal
            {
                ProviderChannelId = providerChannelId,
                ProviderName = "FXTradingProfessor",
                Asset = asset,
                Direction = direction,
                EntryPrice = entry,
                TakeProfit = tp1,
                TakeProfit2 = tp2,
                TakeProfit3 = tp3,
                TakeProfit4 = tp4,
                StopLoss = sl,
                SignalType = SignalType.Text,
                ReceivedAt = DateTime.UtcNow
            };
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error parsing FX Trading Professor signal");
            return null;
        }
    }

    private static (decimal? tp1, decimal? tp2, decimal? tp3, decimal? tp4) ExtractTakeProfits(string message)
    {
        decimal? tp1 = null;
        decimal? tp2 = null;
        decimal? tp3 = null;
        decimal? tp4 = null;

        var matches = Regex.Matches(
            message,
            @"\bTP\s*([1-4])\s+([\d.]+)",
            RegexOptions.IgnoreCase | RegexOptions.Singleline);

        foreach (Match m in matches)
        {
            if (!m.Success)
                continue;

            var index = m.Groups[1].Value;
            var value = decimal.Parse(m.Groups[2].Value, System.Globalization.CultureInfo.InvariantCulture);
            switch (index)
            {
                case "1": tp1 = value; break;
                case "2": tp2 = value; break;
                case "3": tp3 = value; break;
                case "4": tp4 = value; break;
            }
        }

        return (tp1, tp2, tp3, tp4);
    }
}
