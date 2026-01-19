using DerivCTrader.Application.Parsers;
using DerivCTrader.Domain.Enums;
using Microsoft.Extensions.Logging;
using Moq;

namespace DerivCTrader.Infrastructure.Tests;

/// <summary>
/// Backtest tests for CMFLIX signals.
/// Tests the backtest logic and outputs expected results format.
/// </summary>
public class CmflixBacktestTests
{
    private readonly CmflixParser _parser;
    private readonly Mock<ILogger<CmflixParser>> _loggerMock;

    // Backtest parameters
    private const decimal STAKE_USD = 20m;
    private const decimal PAYOUT_PERCENT = 0.85m; // 85% payout on win
    private const int EXPIRY_MINUTES = 15;

    public CmflixBacktestTests()
    {
        _loggerMock = new Mock<ILogger<CmflixParser>>();
        _parser = new CmflixParser(_loggerMock.Object);
    }

    /// <summary>
    /// Backtest today's CMFLIX signals (Jan 9, 2026) with simulated price data.
    /// This demonstrates the backtest methodology and output format.
    /// </summary>
    [Fact]
    public void Backtest_TodaysSignals_WithSimulatedPrices()
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

        var testDate = new DateTime(2026, 1, 9, 0, 0, 0, DateTimeKind.Utc);
        var signals = _parser.ParseBatch(message, telegramMessageId: 12345, overrideDate: testDate);

        // Simulate price data (random but realistic movements)
        var random = new Random(42); // Fixed seed for reproducible results
        var results = new List<BacktestResult>();

        foreach (var signal in signals)
        {
            // Generate simulated entry price based on asset
            var entryPrice = GetBasePrice(signal.Asset);

            // Simulate price movement: -0.1% to +0.1% over 15 minutes
            var movement = (decimal)(random.NextDouble() * 0.002 - 0.001);
            var exitPrice = entryPrice * (1 + movement);

            // Determine win/loss
            var isCall = signal.Direction == TradeDirection.Call;
            var priceWentUp = exitPrice > entryPrice;
            var won = (isCall && priceWentUp) || (!isCall && !priceWentUp);

            var pnl = won ? STAKE_USD * PAYOUT_PERCENT : -STAKE_USD;

            results.Add(new BacktestResult
            {
                Asset = signal.Asset,
                Direction = signal.Direction.ToString(),
                ScheduledUtc = signal.ScheduledAtUtc!.Value,
                EntryPrice = entryPrice,
                ExitPrice = exitPrice,
                PriceChange = exitPrice - entryPrice,
                PriceChangePercent = movement * 100,
                Won = won,
                PnL = pnl
            });
        }

        // Output results
        Console.WriteLine("\n" + new string('=', 100));
        Console.WriteLine("CMFLIX BACKTEST RESULTS - January 9, 2026 (SIMULATED DATA)");
        Console.WriteLine(new string('=', 100));
        Console.WriteLine($"\nBacktest Parameters:");
        Console.WriteLine($"  Stake: ${STAKE_USD}");
        Console.WriteLine($"  Payout: {PAYOUT_PERCENT * 100}%");
        Console.WriteLine($"  Expiry: {EXPIRY_MINUTES} minutes");
        Console.WriteLine($"  Total Signals: {signals.Count}");

        Console.WriteLine("\n" + new string('-', 100));
        Console.WriteLine("{0,-4} {1,-8} {2,-6} {3,-10} {4,-12} {5,-12} {6,-10} {7,-8} {8,-10}",
            "#", "Asset", "Dir", "UTC Time", "Entry", "Exit", "Change", "Result", "P&L");
        Console.WriteLine(new string('-', 100));

        int wins = 0, losses = 0;
        decimal totalPnL = 0;
        int maxConsecutiveLosses = 0, currentLossStreak = 0;

        for (int i = 0; i < results.Count; i++)
        {
            var r = results[i];
            var resultStr = r.Won ? "WIN" : "LOSS";
            var pnlStr = r.PnL >= 0 ? $"+${r.PnL:F2}" : $"-${Math.Abs(r.PnL):F2}";

            Console.WriteLine("{0,-4} {1,-8} {2,-6} {3,-10} {4,-12:F5} {5,-12:F5} {6,-10:F5} {7,-8} {8,-10}",
                i + 1,
                r.Asset,
                r.Direction == "Call" ? "CALL" : "PUT",
                r.ScheduledUtc.ToString("HH:mm"),
                r.EntryPrice,
                r.ExitPrice,
                r.PriceChange,
                resultStr,
                pnlStr);

            totalPnL += r.PnL;
            if (r.Won)
            {
                wins++;
                currentLossStreak = 0;
            }
            else
            {
                losses++;
                currentLossStreak++;
                maxConsecutiveLosses = Math.Max(maxConsecutiveLosses, currentLossStreak);
            }
        }

        // Summary
        var winRate = (decimal)wins / results.Count * 100;
        var callCount = results.Count(r => r.Direction == "Call");
        var putCount = results.Count(r => r.Direction == "Put");
        var callWins = results.Count(r => r.Direction == "Call" && r.Won);
        var putWins = results.Count(r => r.Direction == "Put" && r.Won);

        Console.WriteLine("\n" + new string('=', 100));
        Console.WriteLine("SUMMARY");
        Console.WriteLine(new string('=', 100));
        Console.WriteLine($"\n  Total Trades:           {results.Count}");
        Console.WriteLine($"  Wins:                   {wins}");
        Console.WriteLine($"  Losses:                 {losses}");
        Console.WriteLine($"  Win Rate:               {winRate:F1}%");
        Console.WriteLine($"  Max Consecutive Losses: {maxConsecutiveLosses}");
        Console.WriteLine($"\n  CALL Signals:           {callCount} ({(decimal)callWins/callCount*100:F1}% win rate)");
        Console.WriteLine($"  PUT Signals:            {putCount} ({(decimal)putWins/putCount*100:F1}% win rate)");
        Console.WriteLine($"\n  Total P&L:              {(totalPnL >= 0 ? "+" : "")}${totalPnL:F2}");
        Console.WriteLine($"  ROI:                    {totalPnL / (STAKE_USD * results.Count) * 100:F1}%");

        // Breakdown by asset
        Console.WriteLine("\n" + new string('-', 50));
        Console.WriteLine("BREAKDOWN BY ASSET");
        Console.WriteLine(new string('-', 50));
        var byAsset = results.GroupBy(r => r.Asset).OrderByDescending(g => g.Count());
        foreach (var group in byAsset)
        {
            var assetWins = group.Count(r => r.Won);
            var assetTotal = group.Count();
            var assetWinRate = (decimal)assetWins / assetTotal * 100;
            var assetPnL = group.Sum(r => r.PnL);
            Console.WriteLine($"  {group.Key,-8}: {assetTotal,2} trades, {assetWins,2} wins ({assetWinRate:F0}%), P&L: {(assetPnL >= 0 ? "+" : "")}${assetPnL:F2}");
        }

        Console.WriteLine("\n" + new string('=', 100));
        Console.WriteLine("NOTE: This backtest uses SIMULATED price data for demonstration.");
        Console.WriteLine("For actual results, run against real historical data from MarketPriceHistory table.");
        Console.WriteLine(new string('=', 100) + "\n");

        // Assertions
        Assert.Equal(28, results.Count);
        Assert.True(winRate >= 0 && winRate <= 100);
    }

    /// <summary>
    /// Get realistic base price for each asset
    /// </summary>
    private static decimal GetBasePrice(string asset)
    {
        return asset switch
        {
            "EURUSD" => 1.0350m,
            "GBPUSD" => 1.2520m,
            "EURGBP" => 0.8265m,
            "USDCAD" => 1.4380m,
            "AUDJPY" => 97.50m,
            "AUDCAD" => 0.8920m,
            _ => 1.0000m
        };
    }

    private class BacktestResult
    {
        public string Asset { get; set; } = "";
        public string Direction { get; set; } = "";
        public DateTime ScheduledUtc { get; set; }
        public decimal EntryPrice { get; set; }
        public decimal ExitPrice { get; set; }
        public decimal PriceChange { get; set; }
        public decimal PriceChangePercent { get; set; }
        public bool Won { get; set; }
        public decimal PnL { get; set; }
    }
}
