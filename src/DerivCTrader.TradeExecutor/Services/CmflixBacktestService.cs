using DerivCTrader.Application.Parsers;
using DerivCTrader.Domain.Enums;
using DerivCTrader.Infrastructure.Deriv;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace DerivCTrader.TradeExecutor.Services;

/// <summary>
/// Backtest service for CMFLIX signals using real Deriv price data.
/// Run this service to evaluate historical signal performance.
/// </summary>
public class CmflixBacktestService : BackgroundService
{
    private readonly IDerivClient _derivClient;
    private readonly CmflixParser _parser;
    private readonly ILogger<CmflixBacktestService> _logger;
    private readonly bool _isEnabled;
    private readonly decimal _stakeUsd;
    private readonly decimal _payoutPercent;

    public CmflixBacktestService(
        IDerivClient derivClient,
        CmflixParser parser,
        IConfiguration configuration,
        ILogger<CmflixBacktestService> logger)
    {
        _derivClient = derivClient;
        _parser = parser;
        _logger = logger;

        _isEnabled = configuration.GetValue<bool>("CmflixBacktest:Enabled", false);
        _stakeUsd = configuration.GetValue<decimal>("Cmflix:StakeUsd", 20);
        _payoutPercent = configuration.GetValue<decimal>("Cmflix:PayoutPercent", 0.85m);
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!_isEnabled)
        {
            _logger.LogInformation("CmflixBacktestService is disabled. Set CmflixBacktest:Enabled=true to run.");
            return;
        }

        _logger.LogInformation("Starting CMFLIX Backtest Service...");

        try
        {
            // Connect to Deriv
            await _derivClient.ConnectAsync(stoppingToken);
            _logger.LogInformation("Connected to Deriv");

            // Today's signals - replace with actual signal data
            var signalMessage = @"CMFLIX GOLD SIGNALS
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
            var signals = _parser.ParseBatch(signalMessage, overrideDate: testDate);

            _logger.LogInformation("Parsed {Count} signals for backtest", signals.Count);

            var results = new List<BacktestResult>();
            var now = DateTime.UtcNow;

            foreach (var signal in signals)
            {
                if (stoppingToken.IsCancellationRequested)
                    break;

                var entryTime = signal.ScheduledAtUtc!.Value;
                var exitTime = entryTime.AddMinutes(15);

                // Skip signals that haven't happened yet
                if (exitTime > now)
                {
                    _logger.LogDebug("Skipping future signal: {Asset} {Direction} at {Time} UTC",
                        signal.Asset, signal.Direction, entryTime);
                    continue;
                }

                // Fetch real prices from Deriv
                var entryPrice = await _derivClient.GetHistoricalPriceAsync(signal.Asset, entryTime, stoppingToken);
                await Task.Delay(200, stoppingToken); // Rate limit
                var exitPrice = await _derivClient.GetHistoricalPriceAsync(signal.Asset, exitTime, stoppingToken);
                await Task.Delay(200, stoppingToken); // Rate limit

                if (entryPrice == null || exitPrice == null)
                {
                    _logger.LogWarning("Could not fetch prices for {Asset} at {EntryTime} or {ExitTime}",
                        signal.Asset, entryTime, exitTime);
                    continue;
                }

                // Determine win/loss
                var isCall = signal.Direction == TradeDirection.Call;
                var priceWentUp = exitPrice > entryPrice;
                var won = (isCall && priceWentUp) || (!isCall && !priceWentUp);
                var pnl = won ? _stakeUsd * _payoutPercent : -_stakeUsd;

                results.Add(new BacktestResult
                {
                    Asset = signal.Asset,
                    Direction = signal.Direction.ToString(),
                    EntryTime = entryTime,
                    ExitTime = exitTime,
                    EntryPrice = entryPrice.Value,
                    ExitPrice = exitPrice.Value,
                    Won = won,
                    PnL = pnl
                });

                _logger.LogInformation("{Asset} {Direction} at {Time}: Entry={Entry:F5}, Exit={Exit:F5} -> {Result} ({PnL:+0.00;-0.00})",
                    signal.Asset, signal.Direction, entryTime.ToString("HH:mm"),
                    entryPrice, exitPrice, won ? "WIN" : "LOSS", pnl);
            }

            // Print summary
            PrintBacktestSummary(results);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error in CMFLIX backtest");
        }
    }

    private void PrintBacktestSummary(List<BacktestResult> results)
    {
        if (results.Count == 0)
        {
            _logger.LogWarning("No results to summarize. All signals may be in the future.");
            return;
        }

        var wins = results.Count(r => r.Won);
        var losses = results.Count - wins;
        var winRate = (decimal)wins / results.Count * 100;
        var totalPnL = results.Sum(r => r.PnL);

        Console.WriteLine();
        Console.WriteLine(new string('=', 80));
        Console.WriteLine("CMFLIX BACKTEST RESULTS (REAL DERIV DATA)");
        Console.WriteLine(new string('=', 80));
        Console.WriteLine();
        Console.WriteLine($"Total Trades:  {results.Count}");
        Console.WriteLine($"Wins:          {wins}");
        Console.WriteLine($"Losses:        {losses}");
        Console.WriteLine($"Win Rate:      {winRate:F1}%");
        Console.WriteLine($"Total P&L:     {(totalPnL >= 0 ? "+" : "")}${totalPnL:F2}");
        Console.WriteLine();

        Console.WriteLine(new string('-', 80));
        Console.WriteLine("{0,-8} {1,-6} {2,-8} {3,-12} {4,-12} {5,-8} {6,-10}",
            "Asset", "Dir", "Time", "Entry", "Exit", "Result", "P&L");
        Console.WriteLine(new string('-', 80));

        foreach (var r in results)
        {
            Console.WriteLine("{0,-8} {1,-6} {2,-8} {3,-12:F5} {4,-12:F5} {5,-8} {6,-10}",
                r.Asset,
                r.Direction == "Call" ? "CALL" : "PUT",
                r.EntryTime.ToString("HH:mm"),
                r.EntryPrice,
                r.ExitPrice,
                r.Won ? "WIN" : "LOSS",
                r.PnL >= 0 ? $"+${r.PnL:F2}" : $"-${Math.Abs(r.PnL):F2}");
        }

        Console.WriteLine(new string('=', 80));
        Console.WriteLine();

        // Asset breakdown
        Console.WriteLine("BREAKDOWN BY ASSET:");
        var byAsset = results.GroupBy(r => r.Asset).OrderByDescending(g => g.Count());
        foreach (var group in byAsset)
        {
            var assetWins = group.Count(r => r.Won);
            var assetWinRate = (decimal)assetWins / group.Count() * 100;
            var assetPnL = group.Sum(r => r.PnL);
            Console.WriteLine($"  {group.Key,-8}: {group.Count()} trades, {assetWins} wins ({assetWinRate:F0}%), P&L: {(assetPnL >= 0 ? "+" : "")}${assetPnL:F2}");
        }
    }

    private class BacktestResult
    {
        public string Asset { get; set; } = "";
        public string Direction { get; set; } = "";
        public DateTime EntryTime { get; set; }
        public DateTime ExitTime { get; set; }
        public decimal EntryPrice { get; set; }
        public decimal ExitPrice { get; set; }
        public bool Won { get; set; }
        public decimal PnL { get; set; }
    }
}
