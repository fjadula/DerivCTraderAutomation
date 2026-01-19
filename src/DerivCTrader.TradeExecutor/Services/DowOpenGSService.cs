using DerivCTrader.Application.Interfaces;
using DerivCTrader.Application.Strategies;
using DerivCTrader.Domain.Entities;
using DerivCTrader.Domain.Enums;
using DerivCTrader.Infrastructure.CTrader.Interfaces;
using DerivCTrader.Infrastructure.CTrader.Models;
using DerivCTrader.Infrastructure.MarketData;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace DerivCTrader.TradeExecutor.Services;

/// <summary>
/// DowOpenGS Strategy Service
///
/// Trades the Dow Jones opening-bell momentum using Goldman Sachs and Dow (DIA ETF) confirmation.
///
/// Timeline (UTC):
/// - 14:29:50: Take snapshot of GS and DIA prices via Polygon.io
/// - 14:30:00: Execute trades if signal confirms
///
/// Signal Logic:
/// - GS direction = LatestPrice > PreviousClose ? BUY : SELL
/// - DIA direction = LatestPrice > PreviousClose ? BUY : SELL
/// - If both agree: execute in that direction
/// - If they disagree: NO_TRADE
/// </summary>
public class DowOpenGSService : BackgroundService
{
    private const string StrategyName = "DowOpenGS";
    private const int MarketOpenHour = 14;
    private const int MarketOpenMinute = 30;

    // Polygon.io symbols (DIA is the Dow Jones ETF, equivalent to YM futures)
    private const string GS_TICKER = "GS";
    private const string DOW_TICKER = "DIA"; // DIA ETF tracks Dow Jones (used instead of YM=F)

    private readonly ILogger<DowOpenGSService> _logger;
    private readonly IStrategyRepository _strategyRepo;
    private readonly IMassiveApiService _polygonApi;
    private readonly IMarketExecutor _executor;
    private readonly ITelegramNotifier _notifier;
    private readonly ICTraderOrderManager _cTraderOrderManager;
    private readonly ITradeRepository _tradeRepo;

    public DowOpenGSService(
        ILogger<DowOpenGSService> logger,
        IStrategyRepository strategyRepo,
        IMassiveApiService polygonApi,
        IMarketExecutor executor,
        ITelegramNotifier notifier,
        ICTraderOrderManager cTraderOrderManager,
        ITradeRepository tradeRepo)
    {
        _logger = logger;
        _strategyRepo = strategyRepo;
        _polygonApi = polygonApi;
        _executor = executor;
        _notifier = notifier;
        _cTraderOrderManager = cTraderOrderManager;
        _tradeRepo = tradeRepo;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("=== DOW OPEN GS SERVICE STARTED ===");
        Console.WriteLine("========================================");
        Console.WriteLine("  DowOpenGS Strategy Service");
        Console.WriteLine("========================================");
        Console.WriteLine("📈 Market: Dow Jones (Wall Street 30)");
        Console.WriteLine("⏰ Execution: 14:30 UTC daily");
        Console.WriteLine();

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                // Calculate time until next execution window
                var now = DateTime.UtcNow;
                var nextExecutionTime = GetNextExecutionTime(now);
                var waitTime = nextExecutionTime - now;

                if (waitTime.TotalSeconds > 0)
                {
                    _logger.LogDebug("Waiting until {NextExecution} UTC ({WaitMinutes:F1} minutes)",
                        nextExecutionTime.ToString("HH:mm:ss"), waitTime.TotalMinutes);

                    // Wait in chunks to allow cancellation
                    while (waitTime.TotalSeconds > 60 && !stoppingToken.IsCancellationRequested)
                    {
                        await Task.Delay(TimeSpan.FromMinutes(1), stoppingToken);
                        now = DateTime.UtcNow;
                        waitTime = nextExecutionTime - now;
                    }

                    if (waitTime.TotalSeconds > 0 && !stoppingToken.IsCancellationRequested)
                    {
                        await Task.Delay(waitTime, stoppingToken);
                    }
                }

                if (stoppingToken.IsCancellationRequested)
                    break;

                // Execute strategy
                await ExecuteStrategyAsync(stoppingToken);

                // Wait until next day (avoid re-triggering today)
                var tomorrow = DateTime.UtcNow.Date.AddDays(1);
                var sleepUntil = tomorrow.AddHours(MarketOpenHour).AddMinutes(MarketOpenMinute - 1);
                var sleepTime = sleepUntil - DateTime.UtcNow;

                if (sleepTime.TotalMinutes > 0)
                {
                    _logger.LogDebug("Strategy executed. Sleeping until {SleepUntil} UTC", sleepUntil);
                    await Task.Delay(sleepTime, stoppingToken);
                }
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error in DowOpenGS strategy loop");
                Console.WriteLine($"❌ DowOpenGS ERROR: {ex.Message}");
                await Task.Delay(TimeSpan.FromMinutes(5), stoppingToken);
            }
        }

        _logger.LogInformation("=== DOW OPEN GS SERVICE STOPPED ===");
    }

    private static DateTime GetNextExecutionTime(DateTime now)
    {
        // Target: 14:29:50 UTC (10 seconds before market open)
        var today = now.Date;
        var snapshotTime = today.AddHours(MarketOpenHour).AddMinutes(MarketOpenMinute - 1).AddSeconds(50);

        // If we've passed today's snapshot time, target tomorrow
        if (now > snapshotTime)
        {
            snapshotTime = snapshotTime.AddDays(1);
        }

        return snapshotTime;
    }

    private async Task ExecuteStrategyAsync(CancellationToken cancellationToken)
    {
        var tradeDate = DateTime.UtcNow.Date;
        _logger.LogInformation("🚀 Executing DowOpenGS strategy for {TradeDate}", tradeDate);
        Console.WriteLine($"\n🚀 DowOpenGS: Executing for {tradeDate:yyyy-MM-dd}");

        // 1. Check if strategy is enabled
        var control = await _strategyRepo.GetStrategyControlAsync(StrategyName);
        if (control == null || !control.IsEnabled)
        {
            _logger.LogWarning("DowOpenGS strategy is disabled");
            Console.WriteLine("⚠️ Strategy disabled - skipping");
            return;
        }

        // 2. Check if today is a US market holiday
        if (await _strategyRepo.IsUSMarketHolidayAsync(tradeDate))
        {
            _logger.LogInformation("Today is a US market holiday - skipping");
            Console.WriteLine("🏖️ US market holiday - skipping");
            return;
        }

        // 3. Check if we already have a signal for today
        var existingSignal = await _strategyRepo.GetDowOpenGSSignalByDateAsync(tradeDate);
        if (existingSignal != null)
        {
            _logger.LogWarning("Signal already exists for {TradeDate} - skipping", tradeDate);
            Console.WriteLine("⚠️ Signal already recorded today - skipping");
            return;
        }

        // 4. Load parameters
        var config = await LoadConfigAsync();

        // 5. Wait until exactly snapshot time (14:29:50)
        var snapshotTarget = tradeDate.AddHours(MarketOpenHour).AddMinutes(MarketOpenMinute - 1).AddSeconds(50);
        var waitForSnapshot = snapshotTarget - DateTime.UtcNow;
        if (waitForSnapshot.TotalSeconds > 0)
        {
            _logger.LogDebug("Waiting {Seconds:F1}s for snapshot time", waitForSnapshot.TotalSeconds);
            await Task.Delay(waitForSnapshot, cancellationToken);
        }

        // 6. Take snapshot - fetch GS and DIA prices from Polygon.io
        var snapshotTime = DateTime.UtcNow;
        _logger.LogInformation("📸 Taking price snapshot at {SnapshotTime} via Polygon.io", snapshotTime.ToString("HH:mm:ss"));
        Console.WriteLine($"📸 Taking snapshot at {snapshotTime:HH:mm:ss} UTC (Polygon.io)");

        // Fetch latest quotes from Polygon.io
        var quotes = await _polygonApi.GetLatestQuotesAsync(GS_TICKER, DOW_TICKER);
        var gsQuote = quotes.GetValueOrDefault(GS_TICKER);
        var diaQuote = quotes.GetValueOrDefault(DOW_TICKER);

        if (gsQuote == null || !gsQuote.Success || diaQuote == null || !diaQuote.Success)
        {
            var gsError = gsQuote?.ErrorMessage ?? "Not found";
            var diaError = diaQuote?.ErrorMessage ?? "Not found";
            var error = $"Failed to fetch quotes: GS={gsError}, DIA={diaError}";
            _logger.LogError(error);
            Console.WriteLine($"❌ {error}");
            return;
        }

        // 7. Get previous closes from Polygon.io
        var gsPrevClose = await _polygonApi.GetPreviousCloseAsync(GS_TICKER);
        var diaPrevClose = await _polygonApi.GetPreviousCloseAsync(DOW_TICKER);

        if (gsPrevClose == null || diaPrevClose == null)
        {
            _logger.LogError("Failed to fetch previous close from Polygon.io");
            Console.WriteLine("❌ Previous close not available from Polygon.io");
            return;
        }

        // 8. Evaluate signal
        var signal = EvaluateSignal(
            gsPrevClose.Close, gsQuote.LatestPrice,
            diaPrevClose.Close, diaQuote.LatestPrice,
            config);

        signal.TradeDate = tradeDate;
        signal.SnapshotAt = snapshotTime;
        signal.WasDryRun = control.DryRun;

        // 9. Log the signal
        LogSignal(signal);

        // 10. Save signal to database
        var signalId = await _strategyRepo.SaveDowOpenGSSignalAsync(signal);
        signal.SignalId = signalId;

        // 11. If NO_TRADE, we're done
        if (signal.FinalSignal == "NO_TRADE")
        {
            _logger.LogInformation("Signal: NO_TRADE - {Reason}", signal.NoTradeReason);
            Console.WriteLine($"⏸️ NO_TRADE: {signal.NoTradeReason}");
            await SendNotificationAsync(signal);
            return;
        }

        // 12. Wait until exactly 14:30:00 for execution
        var executeTarget = tradeDate.AddHours(MarketOpenHour).AddMinutes(MarketOpenMinute);
        var waitForExecute = executeTarget - DateTime.UtcNow;
        if (waitForExecute.TotalSeconds > 0)
        {
            _logger.LogDebug("Waiting {Seconds:F1}s for execution time", waitForExecute.TotalSeconds);
            await Task.Delay(waitForExecute, cancellationToken);
        }

        // 13. Execute trades (unless dry run)
        if (control.DryRun)
        {
            _logger.LogInformation("🔸 DRY RUN - would execute {Signal}", signal.FinalSignal);
            Console.WriteLine($"🔸 DRY RUN: Would execute {signal.FinalSignal}");
            await SendNotificationAsync(signal);
            return;
        }

        await ExecuteTradesAsync(signal, control, config, cancellationToken);

        // 14. Update signal with execution results
        await _strategyRepo.UpdateDowOpenGSSignalExecutionAsync(
            signal.SignalId,
            signal.CFDOrderId,
            signal.BinaryContractId,
            signal.CFDEntryPrice,
            signal.CFDStopLoss,
            signal.CFDTakeProfit,
            signal.ErrorMessage);

        // 15. Send notification
        await SendNotificationAsync(signal);
    }

    private static DowOpenGSSignal EvaluateSignal(
        decimal gsPrevClose, decimal gsLatest,
        decimal ymPrevClose, decimal ymLatest,
        DowOpenGSConfig config)
    {
        // Use shared evaluator for consistent logic between live and backtest
        return DowOpenGSSignalEvaluator.Evaluate(
            gsPrevClose, gsLatest,
            ymPrevClose, ymLatest,
            config.DefaultBinaryExpiry,
            config.ExtendedBinaryExpiry,
            config.MinGSMoveForExtendedExpiry);
    }

    private async Task ExecuteTradesAsync(
        DowOpenGSSignal signal,
        StrategyControl control,
        DowOpenGSConfig config,
        CancellationToken cancellationToken)
    {
        _logger.LogInformation("🔥 Executing trades: {Signal}", signal.FinalSignal);
        Console.WriteLine($"🔥 Executing {signal.FinalSignal}...");

        var direction = signal.FinalSignal;
        var errors = new List<string>();

        // Execute CFD on cTrader if enabled
        if (control.ExecuteCFD)
        {
            try
            {
                // Get current price from cTrader to calculate SL/TP
                var currentPrice = await _cTraderOrderManager.GetCurrentPriceAsync(config.DerivCFDSymbol);
                if (!currentPrice.HasValue)
                {
                    errors.Add("CFD: Could not fetch current price from cTrader");
                    _logger.LogWarning("❌ CFD failed: Could not fetch current price for {Symbol}", config.DerivCFDSymbol);
                    Console.WriteLine($"   ❌ CFD failed: Could not fetch current price");
                }
                else
                {
                    // Calculate SL/TP based on current price
                    var isBuy = direction == "BUY";
                    var slPrice = isBuy
                        ? currentPrice.Value * (1 - (double)config.CFDStopLossPercent / 100)
                        : currentPrice.Value * (1 + (double)config.CFDStopLossPercent / 100);
                    var tpPrice = isBuy
                        ? currentPrice.Value * (1 + (double)config.CFDTakeProfitPercent / 100)
                        : currentPrice.Value * (1 - (double)config.CFDTakeProfitPercent / 100);

                    // Create ParsedSignal for cTrader order manager
                    var parsedSignal = new ParsedSignal
                    {
                        Asset = config.DerivCFDSymbol,
                        Direction = isBuy ? TradeDirection.Buy : TradeDirection.Sell,
                        StopLoss = (decimal)slPrice,
                        TakeProfit = (decimal)tpPrice,
                        ProviderName = StrategyName,
                        ProviderChannelId = StrategyName,
                        SignalType = SignalType.MarketExecution,
                        ReceivedAt = DateTime.UtcNow
                    };

                    _logger.LogInformation("📤 Placing cTrader MARKET order: {Symbol} {Direction} SL={SL:F2} TP={TP:F2}",
                        config.DerivCFDSymbol, direction, slPrice, tpPrice);

                    // Execute market order on cTrader
                    var cfdResult = await _cTraderOrderManager.CreateOrderAsync(parsedSignal, CTraderOrderType.Market);

                    if (cfdResult.Success)
                    {
                        signal.CFDExecuted = true;
                        signal.CFDOrderId = cfdResult.PositionId?.ToString() ?? cfdResult.OrderId?.ToString();
                        signal.CFDEntryPrice = cfdResult.ExecutedPrice.HasValue ? (decimal)cfdResult.ExecutedPrice.Value : null;
                        signal.CFDStopLoss = (decimal)slPrice;
                        signal.CFDTakeProfit = (decimal)tpPrice;

                        _logger.LogInformation("✅ cTrader CFD executed: PositionId={PositionId} @ {Price}, SL/TP={SltpStatus}",
                            cfdResult.PositionId, cfdResult.ExecutedPrice,
                            cfdResult.SltpApplied == true ? "Applied" : "Pending");
                        Console.WriteLine($"   ✅ CFD: PositionId={cfdResult.PositionId} @ {cfdResult.ExecutedPrice:F2}");

                        // Save trade to ForexTrades table
                        var forexTrade = new ForexTrade
                        {
                            PositionId = cfdResult.PositionId,
                            Symbol = config.DerivCFDSymbol,
                            Direction = direction,
                            EntryPrice = signal.CFDEntryPrice,
                            SL = signal.CFDStopLoss,
                            TP = signal.CFDTakeProfit,
                            EntryTime = DateTime.UtcNow,
                            Status = "OPEN",
                            Strategy = StrategyName,
                            Notes = $"DowOpenGS signal: GS={signal.GS_Direction}, YM={signal.YM_Direction}",
                            CreatedAt = DateTime.UtcNow
                        };

                        var tradeId = await _tradeRepo.CreateForexTradeAsync(forexTrade);
                        _logger.LogInformation("📝 Saved ForexTrade: TradeId={TradeId}, PositionId={PositionId}",
                            tradeId, cfdResult.PositionId);
                    }
                    else
                    {
                        errors.Add($"CFD: {cfdResult.ErrorMessage}");
                        _logger.LogWarning("❌ cTrader CFD failed: {Error}", cfdResult.ErrorMessage);
                        Console.WriteLine($"   ❌ CFD failed: {cfdResult.ErrorMessage}");
                    }
                }
            }
            catch (Exception ex)
            {
                errors.Add($"CFD: {ex.Message}");
                _logger.LogError(ex, "CFD execution error");
            }
        }

        // Execute Binary if enabled
        if (control.ExecuteBinary)
        {
            try
            {
                var contractType = direction == "BUY" ? "CALL" : "PUT";

                var binaryRequest = new BinaryTradeRequest
                {
                    Symbol = config.DerivBinarySymbol,
                    ContractType = contractType,
                    StakeUSD = config.BinaryStakeUSD,
                    ExpiryMinutes = signal.BinaryExpiry
                };

                var binaryResult = await _executor.ExecuteDerivBinaryAsync(binaryRequest);

                if (binaryResult.Success)
                {
                    signal.BinaryExecuted = true;
                    signal.BinaryContractId = binaryResult.OrderId;

                    _logger.LogInformation("✅ Binary executed: {ContractId} ({Expiry}m)",
                        binaryResult.OrderId, signal.BinaryExpiry);
                    Console.WriteLine($"   ✅ Binary: {binaryResult.OrderId} ({signal.BinaryExpiry}m)");
                }
                else
                {
                    errors.Add($"Binary: {binaryResult.ErrorMessage}");
                    _logger.LogWarning("❌ Binary failed: {Error}", binaryResult.ErrorMessage);
                    Console.WriteLine($"   ❌ Binary failed: {binaryResult.ErrorMessage}");
                }
            }
            catch (Exception ex)
            {
                errors.Add($"Binary: {ex.Message}");
                _logger.LogError(ex, "Binary execution error");
            }
        }

        // MT5 placeholder check
        if (control.ExecuteMT5)
        {
            _logger.LogWarning("MT5 execution requested but not implemented");
            errors.Add("MT5: Not implemented");
        }

        if (errors.Count > 0)
        {
            signal.ErrorMessage = string.Join("; ", errors);
        }

        signal.ExecutedAt = DateTime.UtcNow;
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
                case "DerivCFDSymbol":
                    config.DerivCFDSymbol = param.ParameterValue;
                    break;
                case "DerivBinarySymbol":
                    config.DerivBinarySymbol = param.ParameterValue;
                    break;
            }
        }

        return config;
    }

    private void LogSignal(DowOpenGSSignal signal)
    {
        _logger.LogInformation(
            "Signal Evaluation: GS {GS_Dir} ({GS_Change:+0.00;-0.00}), YM {YM_Dir} ({YM_Change:+0.00;-0.00}) => {Final}",
            signal.GS_Direction, signal.GS_Change,
            signal.YM_Direction, signal.YM_Change,
            signal.FinalSignal);

        Console.WriteLine($"📊 GS: {signal.GS_Direction} ({signal.GS_Change:+0.00;-0.00})");
        Console.WriteLine($"📊 YM: {signal.YM_Direction} ({signal.YM_Change:+0.00;-0.00})");
        Console.WriteLine($"📊 Signal: {signal.FinalSignal} (Expiry: {signal.BinaryExpiry}m)");
    }

    private async Task SendNotificationAsync(DowOpenGSSignal signal)
    {
        try
        {
            var emoji = signal.FinalSignal switch
            {
                "BUY" => "🟢",
                "SELL" => "🔴",
                _ => "⚪"
            };

            var message = $"{emoji} DowOpenGS Signal\n" +
                         $"Date: {signal.TradeDate:yyyy-MM-dd}\n" +
                         $"GS: {signal.GS_Direction} ({signal.GS_Change:+0.00;-0.00})\n" +
                         $"YM: {signal.YM_Direction} ({signal.YM_Change:+0.00;-0.00})\n" +
                         $"Signal: {signal.FinalSignal}\n" +
                         (signal.FinalSignal != "NO_TRADE"
                             ? $"Expiry: {signal.BinaryExpiry}m\n"
                             : $"Reason: {signal.NoTradeReason}\n") +
                         (signal.WasDryRun ? "⚠️ DRY RUN" : "") +
                         (signal.CFDExecuted ? $"\nCFD: {signal.CFDOrderId}" : "") +
                         (signal.BinaryExecuted ? $"\nBinary: {signal.BinaryContractId}" : "");

            await _notifier.SendTradeMessageAsync(message);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to send DowOpenGS notification");
        }
    }
}
