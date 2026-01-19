using System.Text.RegularExpressions;
using DerivCTrader.Application.Interfaces;
using DerivCTrader.Domain.Entities;
using DerivCTrader.Domain.Enums;
using Microsoft.Extensions.Logging;

namespace DerivMT5.Application.Parsers;

/// <summary>
/// Parser for Volatility Signals Channel (-1001768939027)
/// Format 1: VOLATILITY 50 1S INDEX
///           BUY NOW
///           APPLY PROPER RISK MANAGEMENT
/// 
/// Format 2: VOLATILITY 25 (1s) SELL NOW 748900
///           TP 748000
///           TP 746500
///           TP 744500
/// </summary>
public class VolatilitySignalsParser : ISignalParser
{
    private readonly ILogger<VolatilitySignalsParser> _logger;
    private const string ChannelId = "-1001768939027";

    public VolatilitySignalsParser(ILogger<VolatilitySignalsParser> logger)
    {
        _logger = logger;
    }

    public bool CanParse(string providerChannelId)
    {
        return providerChannelId == ChannelId;
    }

    public Task<ParsedSignal?> ParseAsync(string message, string providerChannelId, byte[]? imageData = null)
    {
        try
        {
            _logger.LogInformation("[VolatilitySignalsParser] Parsing message: {Message}", message);

            // Pattern: VOLATILITY 50 1S INDEX or VOLATILITY 25 (1s)
            var assetPattern = @"VOLATILITY\s+(\d+)\s*(?:\(1s\)|1S)?";
            var assetMatch = Regex.Match(message, assetPattern, RegexOptions.IgnoreCase);

            if (!assetMatch.Success)
            {
                _logger.LogWarning("[VolatilitySignalsParser] Asset not found");
                return Task.FromResult<ParsedSignal?>(null);
            }

            var volatilityNumber = assetMatch.Groups[1].Value;
            var asset = $"Volatility {volatilityNumber}";

            // Pattern: BUY NOW or SELL NOW
            var directionPattern = @"(BUY|SELL)\s+NOW(?:\s+([\d.]+))?";
            var directionMatch = Regex.Match(message, directionPattern, RegexOptions.IgnoreCase);

            if (!directionMatch.Success)
            {
                _logger.LogWarning("[VolatilitySignalsParser] Direction not found");
                return Task.FromResult<ParsedSignal?>(null);
            }

            var direction = directionMatch.Groups[1].Value.Equals("BUY", StringComparison.OrdinalIgnoreCase)
                ? TradeDirection.Buy
                : TradeDirection.Sell;

            // Check if execution price is provided
            decimal? entryPrice = null;
            if (directionMatch.Groups[2].Success)
            {
                entryPrice = decimal.Parse(directionMatch.Groups[2].Value);
            }

            // Extract TPs
            var tps = ExtractTakeProfits(message);

            // Determine signal type based on whether entry price is provided
            var signalType = entryPrice.HasValue 
                ? SignalType.Text // Pending order (execute if current price is favorable)
                : SignalType.MarketExecution; // Immediate market execution

            var signal = new ParsedSignal
            {
                ProviderChannelId = providerChannelId,
                ProviderName = "DerivGold",
                Asset = asset,
                Direction = direction,
                EntryPrice = entryPrice,
                TakeProfit = tps.tp1,
                TakeProfit2 = tps.tp2,
                TakeProfit3 = tps.tp3,
                StopLoss = null, // No SL in this format
                SignalType = signalType,
                ReceivedAt = DateTime.UtcNow
            };

            _logger.LogInformation(
                "[VolatilitySignalsParser] Parsed: {Asset} {Direction} @ {Entry}, Type: {Type}",
                signal.Asset, signal.Direction, signal.EntryPrice, signal.SignalType);

            return Task.FromResult<ParsedSignal?>(signal);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "[VolatilitySignalsParser] Error parsing signal");
            return Task.FromResult<ParsedSignal?>(null);
        }
    }

    private static (decimal? tp1, decimal? tp2, decimal? tp3, decimal? tp4) ExtractTakeProfits(string message)
    {
        decimal? tp1 = null, tp2 = null, tp3 = null, tp4 = null;

        // Pattern: TP 748000 (no number, just TP followed by price)
        var tpPattern = @"TP\s+([\d.]+)";
        var matches = Regex.Matches(message, tpPattern, RegexOptions.IgnoreCase);

        int tpIndex = 0;
        foreach (Match match in matches)
        {
            if (!match.Success) continue;

            var value = decimal.Parse(match.Groups[1].Value);
            
            switch (tpIndex)
            {
                case 0: tp1 = value; break;
                case 1: tp2 = value; break;
                case 2: tp3 = value; break;
                case 3: tp4 = value; break;
            }
            tpIndex++;
        }

        return (tp1, tp2, tp3, tp4);
    }
}
