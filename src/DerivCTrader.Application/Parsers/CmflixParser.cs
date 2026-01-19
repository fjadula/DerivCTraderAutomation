using System.Text.RegularExpressions;
using DerivCTrader.Domain.Entities;
using DerivCTrader.Domain.Enums;
using Microsoft.Extensions.Logging;

namespace DerivCTrader.Application.Parsers;

/// <summary>
/// Parser for CMFLIX Gold Signals Telegram channel
///
/// Signal format (batch of scheduled signals):
/// CMFLIX GOLD SIGNALS
/// 08/01
/// 5 MINUTOS
///
/// * 09:10 - EUR/GBP - CALL
/// * 09:45 - EUR/USD - CALL
/// * 10:10 - AUD/JPY - PUT
/// ...
///
/// Times are in Brazilian time (UTC-3) and need to be converted to UTC.
/// Despite title saying "5 MINUTOS", actual expiry is 15 minutes.
/// </summary>
public class CmflixParser
{
    private const string CHANNEL_ID = "-1001473818334";
    private const string CHANNEL_ID_SHORT = "-1473818334";
    private const int BRAZILIAN_UTC_OFFSET_HOURS = -3;
    private const int DEFAULT_EXPIRY_MINUTES = 15;

    private readonly ILogger<CmflixParser> _logger;

    // Regex patterns
    private static readonly Regex DatePattern = new(@"(\d{2})/(\d{2})", RegexOptions.Compiled);
    // Support both EUR/USD and EURUSD formats
    private static readonly Regex SignalLinePattern = new(
        @"(\d{2}:\d{2})\s*-\s*([A-Z]{3}/?[A-Z]{3})\s*-?\s*(CALL|PUT)",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    public CmflixParser(ILogger<CmflixParser> logger)
    {
        _logger = logger;
    }

    /// <summary>
    /// Check if this message is from the CMFLIX channel and contains signal data
    /// </summary>
    public bool CanParse(string providerChannelId, string message)
    {
        var isChannel = providerChannelId == CHANNEL_ID ||
                        providerChannelId == CHANNEL_ID_SHORT ||
                        providerChannelId.EndsWith("1473818334");

        var hasSignalContent = message.Contains("CMFLIX", StringComparison.OrdinalIgnoreCase) ||
                               (DatePattern.IsMatch(message) && SignalLinePattern.IsMatch(message));

        return isChannel && hasSignalContent;
    }

    /// <summary>
    /// Parse a batch of scheduled signals from a CMFLIX message.
    /// Returns multiple ParsedSignal objects, one for each signal line.
    /// </summary>
    public List<ParsedSignal> ParseBatch(string message, int? telegramMessageId = null, DateTime? overrideDate = null)
    {
        var signals = new List<ParsedSignal>();

        try
        {
            _logger.LogInformation("CmflixParser: Parsing batch message");

            // 1. Extract date from header (DD/MM format)
            DateTime signalDate;
            if (overrideDate.HasValue)
            {
                signalDate = overrideDate.Value.Date;
            }
            else
            {
                var dateMatch = DatePattern.Match(message);
                if (!dateMatch.Success)
                {
                    _logger.LogWarning("CmflixParser: No date found in message");
                    return signals;
                }

                var day = int.Parse(dateMatch.Groups[1].Value);
                var month = int.Parse(dateMatch.Groups[2].Value);
                var year = DateTime.UtcNow.Year;

                // Handle year rollover (e.g., signals posted in December for January)
                if (month < DateTime.UtcNow.Month - 6)
                {
                    year++;
                }
                else if (month > DateTime.UtcNow.Month + 6)
                {
                    year--;
                }

                signalDate = new DateTime(year, month, day, 0, 0, 0, DateTimeKind.Utc);
            }

            _logger.LogInformation("CmflixParser: Signal date = {Date:yyyy-MM-dd}", signalDate);

            // 2. Parse each signal line
            var matches = SignalLinePattern.Matches(message);

            foreach (Match match in matches)
            {
                var timeStr = match.Groups[1].Value;      // "09:10"
                var symbolRaw = match.Groups[2].Value;    // "EUR/GBP"
                var directionStr = match.Groups[3].Value; // "CALL"

                // 3. Convert Brazilian time to UTC: add 3 hours
                var timeParts = timeStr.Split(':');
                var hour = int.Parse(timeParts[0]);
                var minute = int.Parse(timeParts[1]);

                var brazilianDateTime = signalDate.AddHours(hour).AddMinutes(minute);
                var utcDateTime = brazilianDateTime.AddHours(-BRAZILIAN_UTC_OFFSET_HOURS); // UTC-3 -> UTC means add 3

                // Normalize symbol: EUR/GBP -> EURGBP
                var asset = symbolRaw.Replace("/", "");

                // Map direction
                var direction = directionStr.Equals("CALL", StringComparison.OrdinalIgnoreCase)
                    ? TradeDirection.Call
                    : TradeDirection.Put;

                var signal = new ParsedSignal
                {
                    Asset = asset,
                    Direction = direction,
                    ScheduledAtUtc = utcDateTime,
                    ProviderChannelId = CHANNEL_ID,
                    ProviderName = "CMFLIX",
                    SignalType = SignalType.PureBinary,
                    Timeframe = DEFAULT_EXPIRY_MINUTES.ToString(),
                    ReceivedAt = DateTime.UtcNow,
                    TelegramMessageId = telegramMessageId,
                    RawMessage = $"{timeStr} - {symbolRaw} - {directionStr}",
                    Processed = false
                };

                signals.Add(signal);

                _logger.LogDebug(
                    "CmflixParser: Parsed signal - {Asset} {Direction} scheduled at {ScheduledUtc:HH:mm} UTC (was {BrazilianTime} BRT)",
                    asset, direction, utcDateTime, timeStr);
            }

            _logger.LogInformation(
                "CmflixParser: Successfully parsed {Count} signals for {Date:yyyy-MM-dd}",
                signals.Count, signalDate);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "CmflixParser: Error parsing batch message");
        }

        return signals;
    }

    /// <summary>
    /// Convert Brazilian time string to UTC DateTime for a given date.
    /// Brazilian time is UTC-3.
    /// </summary>
    public static DateTime ConvertBrazilianTimeToUtc(DateTime date, string timeStr)
    {
        var parts = timeStr.Split(':');
        var hour = int.Parse(parts[0]);
        var minute = int.Parse(parts[1]);

        var brazilianDateTime = date.Date.AddHours(hour).AddMinutes(minute);
        return brazilianDateTime.AddHours(-BRAZILIAN_UTC_OFFSET_HOURS);
    }
}
