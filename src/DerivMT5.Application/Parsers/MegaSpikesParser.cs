using System.Text.RegularExpressions;
using DerivCTrader.Application.Interfaces;
using DerivCTrader.Domain.Entities;
using DerivCTrader.Domain.Enums;
using Microsoft.Extensions.Logging;

namespace DerivMT5.Application.Parsers;

/// <summary>
/// Parser for Mega Spikes Max Channel (-1001060006944)
/// Format: ??MEGA SPIKES MAX
///         ??Boom 600 Index
///         ??M5
///         ??Buy
///         ??TP 1: 6506
///         ??TP 2: 6527
///         ?? SL: 6448
/// NOTE: Instant market execution - NO DERIV BINARY (Boom/Crash don't support binary)
/// </summary>
public class MegaSpikesParser : ISignalParser
{
    private readonly ILogger<MegaSpikesParser> _logger;
    private const string ChannelId = "-1001060006944";

    public MegaSpikesParser(ILogger<MegaSpikesParser> logger)
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
            _logger.LogInformation("[MegaSpikesParser] Parsing message: {Message}", message);

            // Pattern: ??Boom 600 Index or ??Crash 500 Index
            var assetPattern = @"(Boom|Crash)\s+(\d+)\s+Index";
            var assetMatch = Regex.Match(message, assetPattern, RegexOptions.IgnoreCase);

            if (!assetMatch.Success)
            {
                _logger.LogWarning("[MegaSpikesParser] Asset not found");
                return Task.FromResult<ParsedSignal?>(null);
            }

            var assetType = assetMatch.Groups[1].Value; // Boom or Crash
            var assetNumber = assetMatch.Groups[2].Value; // 600, 500, etc.
            var asset = $"{assetType} {assetNumber}";

            // Pattern: ??Buy or ??Sell
            var directionPattern = @"(Buy|Sell)";
            var directionMatch = Regex.Match(message, directionPattern, RegexOptions.IgnoreCase);

            if (!directionMatch.Success)
            {
                _logger.LogWarning("[MegaSpikesParser] Direction not found");
                return Task.FromResult<ParsedSignal?>(null);
            }

            var direction = directionMatch.Groups[1].Value.Equals("BUY", StringComparison.OrdinalIgnoreCase)
                ? TradeDirection.Buy
                : TradeDirection.Sell;

            // Extract TPs and SL
            var tps = ExtractTakeProfits(message);
            var sl = ExtractStopLoss(message);

            var signal = new ParsedSignal
            {
                ProviderChannelId = providerChannelId,
                ProviderName = "DerivGold",
                Asset = asset,
                Direction = direction,
                EntryPrice = null, // Market execution
                TakeProfit = tps.tp1,
                TakeProfit2 = tps.tp2,
                TakeProfit3 = tps.tp3,
                StopLoss = sl,
                SignalType = SignalType.MarketExecution, // Immediate execution
                ReceivedAt = DateTime.UtcNow
            };

            _logger.LogInformation(
                "[MegaSpikesParser] Parsed: {Asset} {Direction} MARKET, TP1: {TP1}, SL: {SL}",
                signal.Asset, signal.Direction, signal.TakeProfit, signal.StopLoss);

            return Task.FromResult<ParsedSignal?>(signal);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "[MegaSpikesParser] Error parsing signal");
            return Task.FromResult<ParsedSignal?>(null);
        }
    }

    private static (decimal? tp1, decimal? tp2, decimal? tp3, decimal? tp4) ExtractTakeProfits(string message)
    {
        decimal? tp1 = null, tp2 = null, tp3 = null, tp4 = null;

        var tpPattern = @"TP\s*(\d)?[:\.\s]?\s*([\d.]+)";
        var matches = Regex.Matches(message, tpPattern, RegexOptions.IgnoreCase);

        int tpIndex = 0;
        foreach (Match match in matches)
        {
            if (!match.Success) continue;

            var value = decimal.Parse(match.Groups[2].Value);
            
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

    private static decimal? ExtractStopLoss(string message)
    {
        var slPattern = @"SL[:\.\s]?\s*([\d.]+)";
        var match = Regex.Match(message, slPattern, RegexOptions.IgnoreCase);

        return match.Success ? decimal.Parse(match.Groups[1].Value) : null;
    }
}
