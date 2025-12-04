using System.Text.RegularExpressions;
using DerivCTrader.Application.Interfaces;
using DerivCTrader.Domain.Entities;
using DerivCTrader.Domain.Enums;
using Microsoft.Extensions.Logging;

namespace DerivCTrader.Application.Parsers;

public class VipFxParser : ISignalParser
{
    private readonly ILogger<VipFxParser> _logger;

    public VipFxParser(ILogger<VipFxParser> logger)
    {
        _logger = logger;
    }

    public bool CanParse(string providerChannelId)
    {
        var canParse = providerChannelId == "-1001138473049"     // VIPFX
                    || providerChannelId == "-1001304028537";    // TestChannel

        _logger.LogInformation("VipFxParser.CanParse({Channel}): {Result}", providerChannelId, canParse);

        return canParse;
    }

    public async Task<ParsedSignal?> ParseAsync(string message, string providerChannelId, byte[]? imageData = null)
    {
        try
        {
            Console.WriteLine("========================================");
            Console.WriteLine($"VipFxParser: Parsing message of length {message.Length}");
            Console.WriteLine($"VipFxParser: Full message:");
            Console.WriteLine(message);
            Console.WriteLine("========================================");

            _logger.LogInformation("VipFxParser: Starting parse attempt");

            // Try multiple patterns
            var patterns = new[]
            {
            // Original pattern
            @"We are (selling|buying) (\w+) now at ([\d.]+).*?Take profit at:\s*([\d.]+).*?Stop loss at:\s*([\d.]+)",
            
            // More flexible whitespace
            @"We\s+are\s+(selling|buying)\s+(\w+)\s+now\s+at\s+([\d.]+)[\s\S]*?Take\s+profit\s+at:\s*([\d.]+)[\s\S]*?Stop\s+loss\s+at:\s*([\d.]+)",
        };

            Match? match = null;

            for (int i = 0; i < patterns.Length; i++)
            {
                Console.WriteLine($"VipFxParser: Trying pattern {i + 1}...");
                match = Regex.Match(message, patterns[i], RegexOptions.Singleline | RegexOptions.IgnoreCase);
                if (match.Success)
                {
                    Console.WriteLine($"VipFxParser: ✅ Pattern {i + 1} MATCHED!");
                    break;
                }
                else
                {
                    Console.WriteLine($"VipFxParser: ❌ Pattern {i + 1} failed");
                }
            }

            if (match == null || !match.Success)
            {
                Console.WriteLine("VipFxParser: ❌ ALL PATTERNS FAILED!");
                Console.WriteLine("VipFxParser: Showing message bytes:");
                var bytes = System.Text.Encoding.UTF8.GetBytes(message);
                Console.WriteLine($"Message has {bytes.Length} bytes");
                for (int i = 0; i < Math.Min(150, bytes.Length); i++)
                {
                    if (bytes[i] == 10) Console.Write("[LF]");
                    else if (bytes[i] == 13) Console.Write("[CR]");
                    else if (bytes[i] == 32) Console.Write("[SP]");
                    else if (bytes[i] < 32 || bytes[i] > 126) Console.Write($"[{bytes[i]}]");
                    else Console.Write((char)bytes[i]);
                }
                Console.WriteLine();
                _logger.LogWarning("VipFxParser: Pattern did not match");
                return null;
            }

            Console.WriteLine($"VipFxParser: Captured {match.Groups.Count} groups:");
            for (int i = 0; i < match.Groups.Count; i++)
            {
                Console.WriteLine($"  Group[{i}]: '{match.Groups[i].Value}'");
            }

            var direction = match.Groups[1].Value.ToLower() == "selling" ? TradeDirection.Sell : TradeDirection.Buy;
            var asset = match.Groups[2].Value;
            var entry = decimal.Parse(match.Groups[3].Value, System.Globalization.CultureInfo.InvariantCulture);
            var tp = decimal.Parse(match.Groups[4].Value, System.Globalization.CultureInfo.InvariantCulture);
            var sl = decimal.Parse(match.Groups[5].Value, System.Globalization.CultureInfo.InvariantCulture);

            Console.WriteLine("VipFxParser: ✅ SUCCESSFULLY PARSED:");
            Console.WriteLine($"  Asset: {asset}");
            Console.WriteLine($"  Direction: {direction}");
            Console.WriteLine($"  Entry: {entry}");
            Console.WriteLine($"  TP: {tp}");
            Console.WriteLine($"  SL: {sl}");
            Console.WriteLine("========================================");

            _logger.LogInformation("VipFxParser: Successfully parsed - {Asset} {Dir} @ {Entry}, TP: {TP}, SL: {SL}",
                asset, direction, entry, tp, sl);

            return new ParsedSignal
            {
                ProviderChannelId = providerChannelId,
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
            Console.WriteLine($"VipFxParser: ❌ EXCEPTION: {ex.Message}");
            Console.WriteLine(ex.StackTrace);
            _logger.LogError(ex, "Error parsing VIPFX signal");
            return null;
        }
    }
}