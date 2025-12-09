using System.Text.RegularExpressions;
using DerivCTrader.Application.Interfaces;
using DerivCTrader.Domain.Entities;
using DerivCTrader.Domain.Enums;
using Microsoft.Extensions.Logging;

namespace DerivCTrader.Application.Parsers;

public class VipChannelParser : ISignalParser
{
    private readonly ILogger<VipChannelParser> _logger;

    public VipChannelParser(ILogger<VipChannelParser> logger)
    {
        _logger = logger;
    }

    public bool CanParse(string providerChannelId)
    {
        // REMOVED TestChannel - now handled by TestChannelParser
        var canParse = providerChannelId == "-1392143914";  // VIPChannel only

        _logger.LogInformation("VipChannelParser.CanParse({Channel}): {Result}", providerChannelId, canParse);

        return canParse;
    }

    public async Task<ParsedSignal?> ParseAsync(string message, string providerChannelId, byte[]? imageData = null)
    {
        try
        {
            _logger.LogInformation("VipChannelParser: Starting parse attempt");
            _logger.LogInformation("VipChannelParser: Message: {Message}", message);

            // VIP Channel format: "OPEN GBP/CAD PUT 15 MIN"
            var pattern = @"OPEN\s+([\w/]+)\s+(CALL|PUT)\s+(\d+)\s+MIN";
            var match = Regex.Match(message, pattern, RegexOptions.IgnoreCase);

            if (!match.Success)
            {
                _logger.LogWarning("VipChannelParser: Pattern did not match");
                return null;
            }

            _logger.LogInformation("VipChannelParser: Pattern matched!");

            var asset = match.Groups[1].Value.Replace("/", "").ToUpper();  // GBP/CAD -> GBPCAD
            var direction = match.Groups[2].Value.ToUpper() == "CALL" ? TradeDirection.Call : TradeDirection.Put;
            var expiry = int.Parse(match.Groups[3].Value);

            _logger.LogInformation("VipChannelParser: Parsed - {Asset} {Direction}, Expiry: {Expiry} min",
                asset, direction, expiry);

            return new ParsedSignal
            {
                ProviderChannelId = providerChannelId,
                ProviderName = "VIPChannel",
                Asset = asset,
                Direction = direction,
                SignalType = SignalType.PureBinary,
                ReceivedAt = DateTime.UtcNow,
                // For pure binary signals, entry/TP/SL are not needed
                EntryPrice = null,
                TakeProfit = null,
                StopLoss = null
            };
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error parsing VIP Channel signal");
            return null;
        }
    }
}
