using DerivCTrader.Application.Interfaces;
using DerivCTrader.Application.Strategies;
using DerivCTrader.Domain.Entities;
using Microsoft.Extensions.Logging;

namespace DerivCTrader.TradeExecutor.Services;

/// <summary>
/// Backtest runner for DowOpenGS strategy.
/// Replays historical data through the same signal evaluation logic used in live trading.
/// </summary>
public class DowOpenGSBacktestRunner
{
    private const string StrategyName = "DowOpenGS";
    private const int MarketOpenHour = 14;
    private const int MarketOpenMinute = 30;

    private readonly ILogger<DowOpenGSBacktestRunner> _logger;
    private readonly IBacktestRepository _backtestRepo;
    private readonly IStrategyRepository _strategyRepo;

    // Symbols
    private const string GS_SYMBOL = "GS";
    // Using WS30 (from DIA ETF) instead of YM=F for signal evaluation
    // DIA tracks the Dow Jones Industrial Average, same as futures
    private const string DOW_SYMBOL = "WS30";
    private const string WS30_SYMBOL = "WS30";

    // Multi-expiry analysis (minutes from 14:30 UTC entry)
    // EOD = 390 minutes (14:30 to 21:00 UTC = 6.5 hours)
    private static readonly int[] BinaryExpiries = { 15, 30, 45, 60, 90, 120, 180, 240, 390 };
    private const int EOD_EXPIRY_MINUTES = 390; // 14:30 + 6.5 hours = 21:00 UTC
    private const int EOD_HOUR = 21; // US market close

    // Multi-duration CFD hold times for comparison (minutes)
    // Tests different max hold times with SL/TP
    private static readonly int[] CFDHoldDurations = { 30, 60, 90, 120, 180, 240, 390 };

    public DowOpenGSBacktestRunner(
        ILogger<DowOpenGSBacktestRunner> logger,
        IBacktestRepository backtestRepo,
        IStrategyRepository strategyRepo)
    {
        _logger = logger;
        _backtestRepo = backtestRepo;
        _strategyRepo = strategyRepo;
    }

    /// <summary>
    /// Run a backtest for the specified date range.
    /// </summary>
    public async Task<BacktestRun> RunBacktestAsync(DateTime startDate, DateTime endDate, string? notes = null)
    {
        _logger.LogInformation("Starting DowOpenGS backtest from {Start} to {End}", startDate, endDate);
        Console.WriteLine($"\n{new string('=', 50)}");
        Console.WriteLine("  DOWOPENGS BACKTEST");
        Console.WriteLine(new string('=', 50));
        Console.WriteLine($"  Period: {startDate:yyyy-MM-dd} to {endDate:yyyy-MM-dd}");
        Console.WriteLine($"{new string('=', 50)}\n");

        // Load parameters from DB
        var config = await LoadConfigAsync();

        // Create backtest run record
        var run = new BacktestRun
        {
            StrategyName = StrategyName,
            StartDate = startDate,
            EndDate = endDate,
            BinaryStakeUSD = config.BinaryStakeUSD,
            CFDVolume = config.CFDVolume,
            CFDStopLossPercent = config.CFDStopLossPercent,
            CFDTakeProfitPercent = config.CFDTakeProfitPercent,
            Notes = notes
        };

        run.RunId = await _backtestRepo.CreateBacktestRunAsync(run);
        _logger.LogInformation("Created backtest run {RunId}", run.RunId);

        // Get trading days
        var tradingDays = await _backtestRepo.GetTradingDaysAsync(startDate, endDate);
        run.TotalTradingDays = tradingDays.Count;
        _logger.LogInformation("Found {Count} trading days to process", tradingDays.Count);

        // Track results
        var trades = new List<BacktestTrade>();
        var consecutiveLosses = 0;
        var maxConsecutiveLosses = 0;
        decimal runningPnL = 0;
        decimal peakPnL = 0;
        decimal maxDrawdown = 0;

        // Multi-expiry tracking
        var multiExpiryResults = new Dictionary<int, ExpiryStats>();
        foreach (var expiry in BinaryExpiries)
        {
            multiExpiryResults[expiry] = new ExpiryStats();
        }

        // CFD exit reason tracking
        var cfdExitReasons = new Dictionary<string, CFDExitStats>
        {
            { "SL_HIT", new CFDExitStats() },
            { "TP_HIT", new CFDExitStats() },
            { "TIME_EXIT", new CFDExitStats() },
            { "NO_DATA", new CFDExitStats() }
        };

        // Manual EOD close tracking (no SL/TP, just hold until 21:00 UTC)
        var manualEodStats = new CFDExitStats();

        // Multi-duration CFD hold tracking (with SL/TP for each duration)
        var cfdHoldResults = new Dictionary<int, CFDExitStats>();
        foreach (var duration in CFDHoldDurations)
        {
            cfdHoldResults[duration] = new CFDExitStats();
        }

        // Process each trading day
        foreach (var tradeDate in tradingDays)
        {
            var (trade, expiryResults, eodCloseResult, cfdDurationResults) = await ProcessTradingDayWithMultiExpiryAsync(tradeDate, config, run.RunId);

            if (trade == null)
            {
                _logger.LogDebug("Skipping {Date} - insufficient data", tradeDate);
                continue;
            }

            trades.Add(trade);
            run.TotalSignals++;

            // Update signal counts
            switch (trade.FinalSignal)
            {
                case "BUY":
                    run.BuySignals++;
                    break;
                case "SELL":
                    run.SellSignals++;
                    break;
                case "NO_TRADE":
                    run.NoTradeSignals++;
                    continue; // Skip trade simulation for NO_TRADE
            }

            // Aggregate multi-expiry results
            if (expiryResults != null)
            {
                foreach (var (expiry, result, pnl) in expiryResults)
                {
                    if (multiExpiryResults.TryGetValue(expiry, out var stats))
                    {
                        stats.Trades++;
                        if (result == "WIN")
                            stats.Wins++;
                        else
                            stats.Losses++;
                        stats.TotalPnL += pnl;
                    }
                }
            }

            // Simulate binary trade (using the default/primary expiry)
            if (trade.BinaryEntryPrice.HasValue && trade.BinaryExitPrice.HasValue)
            {
                run.BinaryTrades++;
                if (trade.BinaryResult == "WIN")
                {
                    run.BinaryWins++;
                    consecutiveLosses = 0;
                }
                else
                {
                    run.BinaryLosses++;
                    consecutiveLosses++;
                }
                run.BinaryTotalPnL += trade.BinaryPnL ?? 0;
            }

            // Simulate CFD trade with SL/TP
            if (trade.CFDEntryPrice.HasValue && trade.CFDExitPrice.HasValue)
            {
                run.CFDTrades++;
                if (trade.CFDResult == "WIN")
                {
                    run.CFDWins++;
                    consecutiveLosses = 0;
                }
                else
                {
                    run.CFDLosses++;
                    consecutiveLosses++;
                }
                run.CFDTotalPnL += trade.CFDPnL ?? 0;

                // Track CFD exit reason
                var exitReason = trade.CFDExitReason ?? "NO_DATA";
                if (cfdExitReasons.TryGetValue(exitReason, out var reasonStats))
                {
                    reasonStats.Trades++;
                    reasonStats.TotalPnL += trade.CFDPnL ?? 0;
                    if (trade.CFDResult == "WIN")
                        reasonStats.Wins++;
                    else
                        reasonStats.Losses++;
                }
            }

            // Track manual EOD close result (no SL/TP)
            if (eodCloseResult != null)
            {
                manualEodStats.Trades++;
                manualEodStats.TotalPnL += eodCloseResult.Value.pnl;
                if (eodCloseResult.Value.result == "WIN")
                    manualEodStats.Wins++;
                else
                    manualEodStats.Losses++;
            }

            // Track multi-duration CFD results
            if (cfdDurationResults != null)
            {
                foreach (var (duration, (result, pnl)) in cfdDurationResults)
                {
                    if (cfdHoldResults.TryGetValue(duration, out var durationStats))
                    {
                        durationStats.Trades++;
                        durationStats.TotalPnL += pnl;
                        if (result == "WIN")
                            durationStats.Wins++;
                        else
                            durationStats.Losses++;
                    }
                }
            }

            // Track max consecutive losses
            maxConsecutiveLosses = Math.Max(maxConsecutiveLosses, consecutiveLosses);

            // Track drawdown
            runningPnL = run.BinaryTotalPnL + run.CFDTotalPnL;
            peakPnL = Math.Max(peakPnL, runningPnL);
            var currentDrawdown = peakPnL - runningPnL;
            maxDrawdown = Math.Max(maxDrawdown, currentDrawdown);

            // Log progress every 10 days
            if (trades.Count % 10 == 0)
            {
                Console.WriteLine($"  Processed {trades.Count} trading days...");
            }
        }

        // Calculate win rates
        if (run.BinaryTrades > 0)
            run.BinaryWinRate = (decimal)run.BinaryWins / run.BinaryTrades * 100;

        if (run.CFDTrades > 0)
            run.CFDWinRate = (decimal)run.CFDWins / run.CFDTrades * 100;

        run.MaxConsecutiveLosses = maxConsecutiveLosses;
        run.MaxDrawdown = maxDrawdown;
        run.CompletedAt = DateTime.UtcNow;

        // Save trades
        await _backtestRepo.BulkSaveBacktestTradesAsync(trades);
        _logger.LogInformation("Saved {Count} backtest trades", trades.Count);

        // Update run summary
        await _backtestRepo.UpdateBacktestRunAsync(run);

        // Print results
        PrintResults(run);
        PrintMultiExpiryResults(multiExpiryResults, config.BinaryStakeUSD);
        PrintCFDExitDetails(cfdExitReasons, manualEodStats, config.CFDVolume);
        PrintCFDHoldDurationResults(cfdHoldResults, config.CFDVolume);

        return run;
    }

    /// <summary>
    /// Process a trading day and return:
    /// - trade: The backtest trade record
    /// - expiryResults: Binary results for all expiries (15, 30, 45, 60, EOD)
    /// - eodCloseResult: Manual CFD close at 21:00 UTC (no SL/TP)
    /// - cfdDurationResults: CFD results for each hold duration (30, 60, 90, 120, 180, 240, 390 min)
    /// </summary>
    private async Task<(BacktestTrade? trade, List<(int expiry, string result, decimal pnl)>? expiryResults, (string result, decimal pnl)? eodCloseResult, Dictionary<int, (string result, decimal pnl)>? cfdDurationResults)> ProcessTradingDayWithMultiExpiryAsync(
        DateTime tradeDate, DowOpenGSConfig config, int runId)
    {
        // Define timestamps
        var snapshotTime = tradeDate.Date.AddHours(MarketOpenHour).AddMinutes(MarketOpenMinute - 1);
        var entryTime = tradeDate.Date.AddHours(MarketOpenHour).AddMinutes(MarketOpenMinute);
        var eodTime = tradeDate.Date.AddHours(EOD_HOUR); // 21:00 UTC

        // Get previous closes (using 21:00 UTC from prior day)
        var gsPrevClose = await _backtestRepo.GetPreviousCloseForDateAsync(GS_SYMBOL, tradeDate);
        var dowPrevClose = await _backtestRepo.GetPreviousCloseForDateAsync(DOW_SYMBOL, tradeDate);

        if (!gsPrevClose.HasValue || !dowPrevClose.HasValue)
        {
            _logger.LogDebug("Missing previous close data for {Date}", tradeDate);
            return (null, null, null, null);
        }

        // Get snapshot prices (at 14:29 UTC - using close of 14:29 candle)
        var gsLatest = await _backtestRepo.GetPriceAtTimeAsync(GS_SYMBOL, snapshotTime);
        var dowLatest = await _backtestRepo.GetPriceAtTimeAsync(DOW_SYMBOL, snapshotTime);

        if (!gsLatest.HasValue || !dowLatest.HasValue)
        {
            _logger.LogDebug("Missing snapshot data for {Date}", tradeDate);
            return (null, null, null, null);
        }

        // Evaluate signal using shared logic
        var signal = DowOpenGSSignalEvaluator.Evaluate(
            gsPrevClose.Value, gsLatest.Value,
            dowPrevClose.Value, dowLatest.Value,
            config.DefaultBinaryExpiry,
            config.ExtendedBinaryExpiry,
            config.MinGSMoveForExtendedExpiry);

        // Create trade record
        var trade = new BacktestTrade
        {
            RunId = runId,
            TradeDate = tradeDate,
            GS_PreviousClose = signal.GS_PreviousClose,
            GS_LatestPrice = signal.GS_LatestPrice,
            GS_Direction = signal.GS_Direction,
            GS_Change = signal.GS_Change,
            YM_PreviousClose = signal.YM_PreviousClose,
            YM_LatestPrice = signal.YM_LatestPrice,
            YM_Direction = signal.YM_Direction,
            YM_Change = signal.YM_Change,
            FinalSignal = signal.FinalSignal,
            NoTradeReason = signal.NoTradeReason,
            BinaryExpiry = signal.BinaryExpiry,
            SnapshotTimeUtc = snapshotTime,
            EntryTimeUtc = entryTime
        };

        // If NO_TRADE, return early
        if (signal.FinalSignal == "NO_TRADE")
        {
            return (trade, null, null, null);
        }

        // Get WS30 entry price (at 14:30 UTC)
        var ws30Entry = await _backtestRepo.GetPriceAtTimeAsync(WS30_SYMBOL, entryTime);
        if (!ws30Entry.HasValue)
        {
            _logger.LogDebug("Missing WS30 entry price for {Date}", tradeDate);
            return (trade, null, null, null);
        }

        trade.BinaryEntryPrice = ws30Entry.Value;
        trade.CFDEntryPrice = ws30Entry.Value;

        // Simulate binary trades for ALL expiries (including EOD)
        var expiryResults = new List<(int expiry, string result, decimal pnl)>();

        foreach (var expiry in BinaryExpiries)
        {
            var exitTime = entryTime.AddMinutes(expiry);
            var ws30Exit = await _backtestRepo.GetPriceAtTimeAsync(WS30_SYMBOL, exitTime);

            if (ws30Exit.HasValue)
            {
                var (result, pnl) = DowOpenGSSignalEvaluator.SimulateBinaryResult(
                    signal.FinalSignal,
                    ws30Entry.Value,
                    ws30Exit.Value,
                    config.BinaryStakeUSD);

                expiryResults.Add((expiry, result, pnl));

                // Set primary binary result using signal's recommended expiry
                if (expiry == signal.BinaryExpiry)
                {
                    trade.BinaryExitPrice = ws30Exit.Value;
                    trade.BinaryResult = result;
                    trade.BinaryPnL = pnl;
                }
            }
        }

        // Simulate CFD trade with SL/TP
        var cfdEndTime = entryTime.AddMinutes(config.CFDMaxHoldMinutes);
        var cfdCandles = await _backtestRepo.GetCandlesAsync(WS30_SYMBOL, entryTime, cfdEndTime);

        if (cfdCandles.Count > 0)
        {
            var cfdResult = DowOpenGSSignalEvaluator.SimulateCFDResult(
                signal.FinalSignal,
                ws30Entry.Value,
                config.CFDStopLossPercent,
                config.CFDTakeProfitPercent,
                config.CFDVolume,
                cfdCandles,
                config.CFDMaxHoldMinutes);

            trade.CFDStopLoss = cfdResult.StopLoss;
            trade.CFDTakeProfit = cfdResult.TakeProfit;
            trade.CFDExitPrice = cfdResult.ExitPrice;
            trade.CFDExitReason = cfdResult.ExitReason;
            trade.CFDResult = cfdResult.Result;
            trade.CFDPnL = cfdResult.PnL;
            trade.CFDExitTimeUtc = cfdResult.ExitTime;
        }

        // Simulate MANUAL EOD close (no SL/TP, just hold until 21:00 UTC)
        (string result, decimal pnl)? eodCloseResult = null;
        var ws30Eod = await _backtestRepo.GetPriceAtTimeAsync(WS30_SYMBOL, eodTime);
        if (ws30Eod.HasValue)
        {
            // Calculate P&L for manual close at EOD (no SL/TP)
            var pointsMove = signal.FinalSignal == "BUY"
                ? ws30Eod.Value - ws30Entry.Value
                : ws30Entry.Value - ws30Eod.Value;

            var eodPnl = pointsMove * config.CFDVolume;
            var eodResult = eodPnl > 0 ? "WIN" : "LOSS";
            eodCloseResult = (eodResult, eodPnl);
        }

        // Simulate CFD trades for multiple hold durations (with SL/TP)
        var cfdDurationResults = new Dictionary<int, (string result, decimal pnl)>();
        foreach (var duration in CFDHoldDurations)
        {
            var durationEndTime = entryTime.AddMinutes(duration);
            var durationCandles = await _backtestRepo.GetCandlesAsync(WS30_SYMBOL, entryTime, durationEndTime);

            if (durationCandles.Count > 0)
            {
                var durationResult = DowOpenGSSignalEvaluator.SimulateCFDResult(
                    signal.FinalSignal,
                    ws30Entry.Value,
                    config.CFDStopLossPercent,
                    config.CFDTakeProfitPercent,
                    config.CFDVolume,
                    durationCandles,
                    duration);

                if (durationResult.Result != null && durationResult.PnL.HasValue)
                {
                    cfdDurationResults[duration] = (durationResult.Result, durationResult.PnL.Value);
                }
            }
        }

        return (trade, expiryResults, eodCloseResult, cfdDurationResults.Count > 0 ? cfdDurationResults : null);
    }

    [Obsolete("Use ProcessTradingDayWithMultiExpiryAsync instead")]
    private async Task<BacktestTrade?> ProcessTradingDayAsync(DateTime tradeDate, DowOpenGSConfig config, int runId)
    {
        var (trade, _, _, _) = await ProcessTradingDayWithMultiExpiryAsync(tradeDate, config, runId);
        return trade;
    }

    private async Task<DowOpenGSConfig> LoadConfigAsync()
    {
        var config = new DowOpenGSConfig();
        var parameters = await _strategyRepo.GetStrategyParametersAsync(StrategyName);

        foreach (var param in parameters)
        {
            switch (param.ParameterName)
            {
                case "BinaryStakeUSD":
                    if (decimal.TryParse(param.ParameterValue, out var stake))
                        config.BinaryStakeUSD = stake;
                    break;
                case "CFDVolume":
                    if (decimal.TryParse(param.ParameterValue, out var volume))
                        config.CFDVolume = volume;
                    break;
                case "CFDStopLossPercent":
                    if (decimal.TryParse(param.ParameterValue, out var slPercent))
                        config.CFDStopLossPercent = slPercent;
                    break;
                case "CFDTakeProfitPercent":
                    if (decimal.TryParse(param.ParameterValue, out var tpPercent))
                        config.CFDTakeProfitPercent = tpPercent;
                    break;
                case "CFDMaxHoldMinutes":
                    if (int.TryParse(param.ParameterValue, out var maxHold))
                        config.CFDMaxHoldMinutes = maxHold;
                    break;
                case "DefaultBinaryExpiry":
                    if (int.TryParse(param.ParameterValue, out var defaultExpiry))
                        config.DefaultBinaryExpiry = defaultExpiry;
                    break;
                case "ExtendedBinaryExpiry":
                    if (int.TryParse(param.ParameterValue, out var extExpiry))
                        config.ExtendedBinaryExpiry = extExpiry;
                    break;
                case "MinGSMoveForExtendedExpiry":
                    if (decimal.TryParse(param.ParameterValue, out var minMove))
                        config.MinGSMoveForExtendedExpiry = minMove;
                    break;
            }
        }

        return config;
    }

    private void PrintResults(BacktestRun run)
    {
        Console.WriteLine($"\n{new string('=', 50)}");
        Console.WriteLine("  BACKTEST RESULTS");
        Console.WriteLine(new string('=', 50));
        Console.WriteLine($"\n  Run ID: {run.RunId}");
        Console.WriteLine($"  Period: {run.StartDate:yyyy-MM-dd} to {run.EndDate:yyyy-MM-dd}");
        Console.WriteLine($"  Trading Days: {run.TotalTradingDays}");
        Console.WriteLine("\n  --- SIGNALS ---");
        Console.WriteLine($"  Total Signals: {run.TotalSignals}");
        Console.WriteLine($"  BUY: {run.BuySignals}");
        Console.WriteLine($"  SELL: {run.SellSignals}");
        Console.WriteLine($"  NO_TRADE: {run.NoTradeSignals}");

        Console.WriteLine("\n  --- BINARY RESULTS ---");
        Console.WriteLine($"  Trades: {run.BinaryTrades}");
        Console.WriteLine($"  Wins: {run.BinaryWins}");
        Console.WriteLine($"  Losses: {run.BinaryLosses}");
        Console.WriteLine($"  Win Rate: {run.BinaryWinRate:F1}%");
        Console.WriteLine($"  P&L: ${run.BinaryTotalPnL:F2}");

        Console.WriteLine("\n  --- CFD RESULTS ---");
        Console.WriteLine($"  Trades: {run.CFDTrades}");
        Console.WriteLine($"  Wins: {run.CFDWins}");
        Console.WriteLine($"  Losses: {run.CFDLosses}");
        Console.WriteLine($"  Win Rate: {run.CFDWinRate:F1}%");
        Console.WriteLine($"  P&L: ${run.CFDTotalPnL:F2}");

        Console.WriteLine("\n  --- RISK METRICS ---");
        Console.WriteLine($"  Max Consecutive Losses: {run.MaxConsecutiveLosses}");
        Console.WriteLine($"  Max Drawdown: ${run.MaxDrawdown:F2}");

        Console.WriteLine("\n  --- TOTAL ---");
        Console.WriteLine($"  Combined P&L: ${run.TotalPnL:F2}");

        Console.WriteLine($"\n{new string('=', 50)}\n");

        _logger.LogInformation(
            "Backtest complete: RunId={RunId}, Binary WR={BinaryWR:F1}%, CFD WR={CfdWR:F1}%, TotalPnL=${TotalPnL:F2}",
            run.RunId, run.BinaryWinRate, run.CFDWinRate, run.TotalPnL);
    }

    private void PrintMultiExpiryResults(Dictionary<int, ExpiryStats> multiExpiryResults, decimal stakeUSD)
    {
        Console.WriteLine("\n  --- MULTI-EXPIRY BINARY COMPARISON ---");
        Console.WriteLine("  ┌─────────┬────────┬──────┬────────┬──────────┬───────────┐");
        Console.WriteLine("  │ Expiry  │ Trades │ Wins │ Win %  │ P&L      │ Avg/Trade │");
        Console.WriteLine("  ├─────────┼────────┼──────┼────────┼──────────┼───────────┤");

        foreach (var expiry in BinaryExpiries)
        {
            var expiryLabel = expiry == EOD_EXPIRY_MINUTES ? "EOD" : $"{expiry} min";
            if (multiExpiryResults.TryGetValue(expiry, out var stats) && stats.Trades > 0)
            {
                var winRate = (decimal)stats.Wins / stats.Trades * 100;
                var avgPnl = stats.TotalPnL / stats.Trades;
                Console.WriteLine($"  │ {expiryLabel,7} │ {stats.Trades,6} │ {stats.Wins,4} │ {winRate,5:F1}% │ ${stats.TotalPnL,7:F2} │ ${avgPnl,8:F2} │");
            }
            else
            {
                Console.WriteLine($"  │ {expiryLabel,7} │ {0,6} │ {0,4} │ {"N/A",6} │ {"N/A",9} │ {"N/A",10} │");
            }
        }

        Console.WriteLine("  └─────────┴────────┴──────┴────────┴──────────┴───────────┘");

        // Find best expiry
        var bestExpiry = multiExpiryResults
            .Where(x => x.Value.Trades > 0)
            .OrderByDescending(x => x.Value.TotalPnL)
            .FirstOrDefault();

        if (bestExpiry.Value != null && bestExpiry.Value.Trades > 0)
        {
            var bestLabel = bestExpiry.Key == EOD_EXPIRY_MINUTES ? "EOD (21:00 UTC)" : $"{bestExpiry.Key} minutes";
            var bestWinRate = (decimal)bestExpiry.Value.Wins / bestExpiry.Value.Trades * 100;
            Console.WriteLine($"\n  Best Expiry: {bestLabel} ({bestWinRate:F1}% win rate, ${bestExpiry.Value.TotalPnL:F2} P&L)");
        }
    }

    private void PrintCFDExitDetails(Dictionary<string, CFDExitStats> cfdExitReasons, CFDExitStats manualEodStats, decimal volume)
    {
        Console.WriteLine("\n  --- CFD EXIT REASON BREAKDOWN ---");
        Console.WriteLine("  ┌────────────┬────────┬──────┬────────┬────────┬──────────┐");
        Console.WriteLine("  │ Exit Type  │ Trades │ Wins │ Losses │ Win %  │ P&L      │");
        Console.WriteLine("  ├────────────┼────────┼──────┼────────┼────────┼──────────┤");

        foreach (var (reason, stats) in cfdExitReasons.OrderByDescending(x => x.Value.Trades))
        {
            if (stats.Trades > 0)
            {
                var winRate = (decimal)stats.Wins / stats.Trades * 100;
                Console.WriteLine($"  │ {reason,-10} │ {stats.Trades,6} │ {stats.Wins,4} │ {stats.Losses,6} │ {winRate,5:F1}% │ ${stats.TotalPnL,7:F2} │");
            }
        }

        Console.WriteLine("  └────────────┴────────┴──────┴────────┴────────┴──────────┘");

        // Manual EOD close comparison
        Console.WriteLine("\n  --- CFD: MANUAL EOD CLOSE (No SL/TP) ---");
        Console.WriteLine("  What if you held until 21:00 UTC without SL/TP?");
        Console.WriteLine("  ┌────────────┬────────┬──────┬────────┬────────┬──────────┐");
        Console.WriteLine("  │ Strategy   │ Trades │ Wins │ Losses │ Win %  │ P&L      │");
        Console.WriteLine("  ├────────────┼────────┼──────┼────────┼────────┼──────────┤");

        if (manualEodStats.Trades > 0)
        {
            var eodWinRate = (decimal)manualEodStats.Wins / manualEodStats.Trades * 100;
            Console.WriteLine($"  │ {"EOD Close",-10} │ {manualEodStats.Trades,6} │ {manualEodStats.Wins,4} │ {manualEodStats.Losses,6} │ {eodWinRate,5:F1}% │ ${manualEodStats.TotalPnL,7:F2} │");
        }
        else
        {
            Console.WriteLine($"  │ {"EOD Close",-10} │ {"N/A",6} │ {"N/A",4} │ {"N/A",6} │ {"N/A",6} │ {"N/A",9} │");
        }

        Console.WriteLine("  └────────────┴────────┴──────┴────────┴────────┴──────────┘");

        // Summary comparison
        var slTp = cfdExitReasons.Values.Sum(x => x.TotalPnL);
        var eod = manualEodStats.TotalPnL;
        var better = eod > slTp ? "Manual EOD Close" : "SL/TP Strategy";
        var diff = Math.Abs(eod - slTp);

        Console.WriteLine($"\n  Comparison: SL/TP Strategy P&L: ${slTp:F2} vs Manual EOD P&L: ${eod:F2}");
        Console.WriteLine($"  Winner: {better} (${diff:F2} better)");
    }

    private void PrintCFDHoldDurationResults(Dictionary<int, CFDExitStats> cfdHoldResults, decimal volume)
    {
        Console.WriteLine("\n  --- CFD HOLD DURATION COMPARISON (with SL/TP) ---");
        Console.WriteLine("  Testing different max hold times with SL: 0.35%, TP: 0.70%");
        Console.WriteLine("  ┌──────────┬────────┬──────┬────────┬────────┬──────────┬───────────┐");
        Console.WriteLine("  │ Duration │ Trades │ Wins │ Losses │ Win %  │ P&L      │ Avg/Trade │");
        Console.WriteLine("  ├──────────┼────────┼──────┼────────┼────────┼──────────┼───────────┤");

        foreach (var duration in CFDHoldDurations)
        {
            var durationLabel = duration == EOD_EXPIRY_MINUTES ? "EOD" : $"{duration} min";
            if (cfdHoldResults.TryGetValue(duration, out var stats) && stats.Trades > 0)
            {
                var winRate = (decimal)stats.Wins / stats.Trades * 100;
                var avgPnl = stats.TotalPnL / stats.Trades;
                Console.WriteLine($"  │ {durationLabel,8} │ {stats.Trades,6} │ {stats.Wins,4} │ {stats.Losses,6} │ {winRate,5:F1}% │ ${stats.TotalPnL,7:F2} │ ${avgPnl,8:F2} │");
            }
            else
            {
                Console.WriteLine($"  │ {durationLabel,8} │ {0,6} │ {0,4} │ {0,6} │ {"N/A",6} │ {"N/A",9} │ {"N/A",10} │");
            }
        }

        Console.WriteLine("  └──────────┴────────┴──────┴────────┴────────┴──────────┴───────────┘");

        // Find best duration
        var bestDuration = cfdHoldResults
            .Where(x => x.Value.Trades > 0)
            .OrderByDescending(x => x.Value.TotalPnL)
            .FirstOrDefault();

        if (bestDuration.Value != null && bestDuration.Value.Trades > 0)
        {
            var bestLabel = bestDuration.Key == EOD_EXPIRY_MINUTES ? "EOD (390 min)" : $"{bestDuration.Key} minutes";
            var bestWinRate = (decimal)bestDuration.Value.Wins / bestDuration.Value.Trades * 100;
            Console.WriteLine($"\n  Best Hold Duration: {bestLabel} ({bestWinRate:F1}% win rate, ${bestDuration.Value.TotalPnL:F2} P&L)");
        }

        // Compare shortest vs longest hold time
        if (cfdHoldResults.TryGetValue(30, out var short30) && short30.Trades > 0 &&
            cfdHoldResults.TryGetValue(EOD_EXPIRY_MINUTES, out var longEod) && longEod.Trades > 0)
        {
            Console.WriteLine($"\n  30-min vs EOD: ${short30.TotalPnL:F2} vs ${longEod.TotalPnL:F2}");
            var holdBetter = short30.TotalPnL > longEod.TotalPnL ? "30-min hold" : "EOD hold";
            Console.WriteLine($"  Conclusion: {holdBetter} performs better with SL/TP active");
        }
    }
}

/// <summary>
/// Helper class to track stats for each binary expiry duration.
/// </summary>
public class ExpiryStats
{
    public int Trades { get; set; }
    public int Wins { get; set; }
    public int Losses { get; set; }
    public decimal TotalPnL { get; set; }
}

/// <summary>
/// Helper class to track CFD exit stats by exit reason.
/// </summary>
public class CFDExitStats
{
    public int Trades { get; set; }
    public int Wins { get; set; }
    public int Losses { get; set; }
    public decimal TotalPnL { get; set; }
}
