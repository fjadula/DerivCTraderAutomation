using DerivCTrader.Application.Parsers;
using DerivCTrader.Domain.Enums;
using Microsoft.Extensions.Logging;
using Moq;

namespace DerivCTrader.Infrastructure.Tests;

public class CmflixParserTests
{
    private readonly CmflixParser _parser;
    private readonly Mock<ILogger<CmflixParser>> _loggerMock;

    public CmflixParserTests()
    {
        _loggerMock = new Mock<ILogger<CmflixParser>>();
        _parser = new CmflixParser(_loggerMock.Object);
    }

    [Fact]
    public void ParseBatch_WithTodaysSignals_ReturnsAllSignals()
    {
        // Arrange - Today's CMFLIX signal (09/01)
        var message = @"CMFLIX GOLD SIGNALS
09/01
5 MINUTOS

* 09:55 - EUR/USD- CALL
* 10:05 - GBP/USD - CALL
* 10:15 - EUR/GBP - CALL
* 10:35 - USDCAD - CALL
* 10:50 - EUR/USD - CALL
* 10:55 - EUR/USD - CALL
* 11:15 - EUR/USD - CALL
* 11:25 - AUD/JPY- CALL
* 11:45 - AUD/CAD- PUT
* 12:00 - USD/CAD - CALL
* 12:15 - EUR/USD - PUT
* 12:25 - EUR/USD - CALL
* 12:45 - EUR/USD - PUT
* 12:55 - EUR/USD - PUT
* 13:25 - AUD/JPY - CALL
* 13:30 - GBP/USD - CALL
* 13:40 - USD/CAD - PUT
* 13:55 - EUR/USD - PUT
* 14:05 - EUR/USD - CALL
* 14:25 - EUR/USD - PUT
* 14:35 - USD/CAD - PUT
* 14:50 - EUR/USD - PUT
* 15:05 - EUR/USD - CALL
* 15:15 - EUR/USD - CALL
* 15:30 - USD/CAD - PUT
* 15:45 - EUR/USD - PUT
* 16:05 - EUR/USD - PUT
* 16:20 - GBP/USD - CALL";

        // Use override date to ensure consistent test
        var testDate = new DateTime(2026, 1, 9, 0, 0, 0, DateTimeKind.Utc);

        // Act
        var signals = _parser.ParseBatch(message, telegramMessageId: 12345, overrideDate: testDate);

        // Assert
        Assert.Equal(28, signals.Count);

        // Display all parsed signals
        Console.WriteLine("\n========== CMFLIX PARSER TEST RESULTS ==========\n");
        Console.WriteLine($"Total signals parsed: {signals.Count}");
        Console.WriteLine($"Signal date: {testDate:yyyy-MM-dd}");
        Console.WriteLine("\n{0,-5} {1,-10} {2,-8} {3,-12} {4,-12}", "#", "Asset", "Dir", "BRT Time", "UTC Time");
        Console.WriteLine(new string('-', 55));

        for (int i = 0; i < signals.Count; i++)
        {
            var s = signals[i];
            // Calculate back to BRT for display
            var brtTime = s.ScheduledAtUtc!.Value.AddHours(-3);
            Console.WriteLine("{0,-5} {1,-10} {2,-8} {3,-12} {4,-12}",
                i + 1,
                s.Asset,
                s.Direction,
                brtTime.ToString("HH:mm"),
                s.ScheduledAtUtc!.Value.ToString("HH:mm"));
        }

        Console.WriteLine("\n========== VALIDATION CHECKS ==========\n");

        // Verify first signal
        var first = signals[0];
        Assert.Equal("EURUSD", first.Asset);
        Assert.Equal(TradeDirection.Call, first.Direction);
        Assert.Equal(new DateTime(2026, 1, 9, 12, 55, 0, DateTimeKind.Utc), first.ScheduledAtUtc); // 09:55 BRT = 12:55 UTC
        Assert.Equal("CMFLIX", first.ProviderName);
        Assert.Equal(SignalType.PureBinary, first.SignalType);
        Assert.Equal("15", first.Timeframe);
        Console.WriteLine($"[PASS] First signal: {first.Asset} {first.Direction} at {first.ScheduledAtUtc:HH:mm} UTC");

        // Verify last signal
        var last = signals[27];
        Assert.Equal("GBPUSD", last.Asset);
        Assert.Equal(TradeDirection.Call, last.Direction);
        Assert.Equal(new DateTime(2026, 1, 9, 19, 20, 0, DateTimeKind.Utc), last.ScheduledAtUtc); // 16:20 BRT = 19:20 UTC
        Console.WriteLine($"[PASS] Last signal: {last.Asset} {last.Direction} at {last.ScheduledAtUtc:HH:mm} UTC");

        // Count directions
        var callCount = signals.Count(s => s.Direction == TradeDirection.Call);
        var putCount = signals.Count(s => s.Direction == TradeDirection.Put);
        Console.WriteLine($"\n[INFO] CALL signals: {callCount}");
        Console.WriteLine($"[INFO] PUT signals: {putCount}");

        // Count unique assets
        var assets = signals.Select(s => s.Asset).Distinct().ToList();
        Console.WriteLine($"\n[INFO] Unique assets: {string.Join(", ", assets)}");

        Console.WriteLine("\n========== TEST PASSED ==========\n");
    }

    [Fact]
    public void CanParse_WithCmflixChannel_ReturnsTrue()
    {
        var message = "CMFLIX GOLD SIGNALS\n09/01\n5 MINUTOS\n* 09:55 - EUR/USD - CALL";

        Assert.True(_parser.CanParse("-1001473818334", message));
        Assert.True(_parser.CanParse("-1473818334", message));
    }

    [Fact]
    public void CanParse_WithWrongChannel_ReturnsFalse()
    {
        var message = "CMFLIX GOLD SIGNALS\n09/01\n5 MINUTOS\n* 09:55 - EUR/USD - CALL";

        Assert.False(_parser.CanParse("-1001234567890", message));
    }

    [Fact]
    public void ConvertBrazilianTimeToUtc_ConvertsCorrectly()
    {
        var date = new DateTime(2026, 1, 9, 0, 0, 0, DateTimeKind.Utc);

        var utc = CmflixParser.ConvertBrazilianTimeToUtc(date, "09:55");

        // 09:55 BRT (UTC-3) = 12:55 UTC
        Assert.Equal(12, utc.Hour);
        Assert.Equal(55, utc.Minute);
    }
}
