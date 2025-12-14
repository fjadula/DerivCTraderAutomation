using DerivCTrader.Application.Interfaces;
using DerivCTrader.Domain.Entities;
using DerivCTrader.Domain.Enums;
using DerivCTrader.Infrastructure.CTrader.Interfaces;
using DerivCTrader.Infrastructure.CTrader.Models;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using System.Collections.Concurrent;

namespace DerivCTrader.TradeExecutor.Services;

/// <summary>
/// Background service that processes forex signals from ParsedSignalsQueue
/// and creates pending orders on cTrader.
/// 
/// Flow:
/// 1. Poll ParsedSignalsQueue for unprocessed signals
/// 2. Filter for forex signals (exclude pure binary)
/// 3. Load provider config (TakeOriginal/TakeOpposite)
/// 4. Create pending order(s) on cTrader
/// 5. Mark signal as processed
/// 6. CTraderPendingOrderService monitors execution
/// 7. On execution -> writes to TradeExecutionQueue
/// </summary>
public class CTraderForexProcessorService : BackgroundService
{
    private readonly ILogger<CTraderForexProcessorService> _logger;
    private readonly ITradeRepository _repository;
    private readonly ICTraderPendingOrderService _pendingOrderService;
    private readonly ICTraderSymbolService _symbolService;
    private readonly ICTraderOrderManager _orderManager;
    private readonly IConfiguration _configuration;
    private readonly int _pollIntervalSeconds;

    private readonly ConcurrentDictionary<int, byte> _inFlightSignals = new();

    public CTraderForexProcessorService(
        ILogger<CTraderForexProcessorService> logger,
        ITradeRepository repository,
        ICTraderPendingOrderService pendingOrderService,
        ICTraderSymbolService symbolService,
        ICTraderOrderManager orderManager,
        IConfiguration configuration)
    {
        _logger = logger;
        _repository = repository;
        _pendingOrderService = pendingOrderService;
        _symbolService = symbolService;
        _orderManager = orderManager;
        _configuration = configuration;
        _pollIntervalSeconds = configuration.GetValue("CTrader:SignalPollIntervalSeconds", 5);
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("=== CTRADER FOREX PROCESSOR SERVICE STARTED ===");
        Console.WriteLine("========================================");
        Console.WriteLine("  cTrader Forex Signal Processor");
        Console.WriteLine("========================================");
        Console.WriteLine($"📊 Poll Interval: {_pollIntervalSeconds}s");
        Console.WriteLine("🔍 Monitoring ParsedSignalsQueue for forex signals...");
        Console.WriteLine();

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await ProcessForexSignalsAsync(stoppingToken);
                await Task.Delay(TimeSpan.FromSeconds(_pollIntervalSeconds), stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error in cTrader forex processing loop");
                Console.WriteLine($"❌ ERROR: {ex.Message}");
                try
                {
                    await Task.Delay(TimeSpan.FromSeconds(10), stoppingToken);
                }
                catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
                {
                    break;
                }
            }
        }

        _logger.LogInformation("=== CTRADER FOREX PROCESSOR SERVICE STOPPED ===");
    }

    private async Task ProcessForexSignalsAsync(CancellationToken cancellationToken)
    {
        if (!_symbolService.IsInitialized)
        {
            _logger.LogDebug("cTrader symbols not initialized yet; skipping forex processing this cycle");
            return;
        }

        // Get all unprocessed signals
        var signals = await _repository.GetUnprocessedSignalsAsync();

        _logger.LogDebug("🔍 Polling database - found {Count} total unprocessed signals", signals.Count);

        if (signals.Count == 0)
            return;

        // Filter for forex signals only (exclude pure binary)
        var forexSignals = signals
            .Where(s => s.SignalType != SignalType.PureBinary)
            .ToList();

        if (forexSignals.Count == 0)
            return;

        _logger.LogInformation("📋 Found {Count} unprocessed forex signals", forexSignals.Count);
        Console.WriteLine($"📋 Processing {forexSignals.Count} forex signal(s)...");

        foreach (var signal in forexSignals)
        {
            try
            {
                await ProcessSingleForexSignalAsync(signal, cancellationToken);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to process forex signal {SignalId}", signal.SignalId);
                Console.WriteLine($"❌ Failed signal #{signal.SignalId}: {ex.Message}");
                
                // Mark as processed on failure only if configured (default: true)
                var markOnFailure = _configuration.GetValue("CTrader:MarkSignalsAsProcessedOnFailure", false);
                if (markOnFailure)
                {
                    _logger.LogInformation("[PRE] Calling MarkSignalAsProcessedAsync (failure) for SignalId={SignalId}", signal.SignalId);
                    await _repository.MarkSignalAsProcessedAsync(signal.SignalId);
                    _logger.LogInformation("[POST] MarkSignalAsProcessedAsync (failure) completed for SignalId={SignalId}", signal.SignalId);
                }
            }
        }
    }

    private async Task ProcessSingleForexSignalAsync(ParsedSignal signal, CancellationToken cancellationToken)
    {
        if (signal.SignalId > 0 && !_inFlightSignals.TryAdd(signal.SignalId, 0))
        {
            _logger.LogDebug("Skipping SignalId={SignalId} because it is already being processed in-flight", signal.SignalId);
            return;
        }

        try
        {
        _logger.LogInformation("🔨 Processing forex signal #{SignalId}: {Asset} {Direction}", 
            signal.SignalId, signal.Asset, signal.Direction);

        // --- ENTRY PRICE LOGIC ---
        // If the signal has a zone (Pattern contains 'Zone' and RawMessage has a zone), use the midpoint as EntryPrice.
        // Otherwise, use the given EntryPrice.
        decimal? entryPriceToUse = signal.EntryPrice;
        if (!string.IsNullOrEmpty(signal.Pattern) && signal.Pattern.Contains("Zone", StringComparison.OrdinalIgnoreCase) && signal.RawMessage != null)
        {
            // Try to extract zone prices from RawMessage (e.g., "Zone : 2422.50 - 2414.50")
            var raw = signal.RawMessage;
            var zoneMatch = System.Text.RegularExpressions.Regex.Match(raw, @"Zone\s*:\s*(\d+\.?\d*)\s*-\s*(\d+\.?\d*)");
            if (zoneMatch.Success)
            {
                if (decimal.TryParse(zoneMatch.Groups[1].Value, out var z1) && decimal.TryParse(zoneMatch.Groups[2].Value, out var z2))
                {
                    var midpoint = (z1 + z2) / 2m;
                    // Add a 1-point buffer to avoid immediate market fill
                    if (signal.Direction == TradeDirection.Buy)
                        entryPriceToUse = midpoint - 1m;
                    else if (signal.Direction == TradeDirection.Sell)
                        entryPriceToUse = midpoint + 1m;
                    else
                        entryPriceToUse = midpoint;
                    _logger.LogInformation("[ENTRY] Using zone midpoint as EntryPrice (with 1-point buffer): {EntryPrice} (from {Z1} - {Z2})", entryPriceToUse, z1, z2);
                }
            }
        }

        // Overwrite signal.EntryPrice if needed (ParsedSignal is a class, not a record)
        var signalForOrder = new ParsedSignal
        {
            SignalId = signal.SignalId,
            Asset = signal.Asset,
            Direction = signal.Direction,
            EntryPrice = entryPriceToUse,
            StopLoss = signal.StopLoss,
            TakeProfit = signal.TakeProfit,
            TakeProfit2 = signal.TakeProfit2,
            TakeProfit3 = signal.TakeProfit3,
            TakeProfit4 = signal.TakeProfit4,
            RiskRewardRatio = signal.RiskRewardRatio,
            LotSize = signal.LotSize,
            ProviderChannelId = signal.ProviderChannelId,
            ProviderName = signal.ProviderName,
            SignalType = signal.SignalType,
            ReceivedAt = signal.ReceivedAt,
            RawMessage = signal.RawMessage,
            Pattern = signal.Pattern,
            Timeframe = signal.Timeframe,
            Processed = signal.Processed,
            ProcessedAt = signal.ProcessedAt
        };

        Console.WriteLine($"🔨 Signal #{signal.SignalId}: {signal.Asset} {signal.Direction} @ {signalForOrder.EntryPrice}");

        // Load provider configuration
        var providerConfig = await _repository.GetProviderConfigAsync(signal.ProviderChannelId);
        if (providerConfig == null)
        {
            _logger.LogWarning("No provider config found for channel {ChannelId}, using defaults", 
                signal.ProviderChannelId);
            // Default: TakeOriginal only
            providerConfig = new ProviderChannelConfig
            {
                ProviderChannelId = signal.ProviderChannelId,
                TakeOriginal = true,
                TakeOpposite = false
            };
        }


        var ordersCreated = 0;
        CTraderOrderResult? originalOrderResult = null;
        CTraderOrderResult? oppositeOrderResult = null;
        var expectedOrders = (providerConfig.TakeOriginal ? 1 : 0) + (providerConfig.TakeOpposite ? 1 : 0);

        // Log current bid/ask for debugging
        if (_orderManager != null && signalForOrder.Asset != null)
        {
            var bidAsk = await _orderManager.GetCurrentBidAskAsync(signalForOrder.Asset);
            _logger.LogInformation("[DEBUG] Pre-order: Asset={Asset} EntryPrice={EntryPrice} Bid={Bid} Ask={Ask}", signalForOrder.Asset, signalForOrder.EntryPrice, bidAsk.Item1, bidAsk.Item2);
        }

        // Create original direction order
        if (providerConfig.TakeOriginal)
        {
            _logger.LogInformation("📝 Creating ORIGINAL direction order: {Direction}", signalForOrder.Direction);
            Console.WriteLine($"   📝 Creating ORIGINAL order ({signalForOrder.Direction})...");
            originalOrderResult = await _pendingOrderService.ProcessSignalAsync(signalForOrder, isOpposite: false);
            if (originalOrderResult.Success)
            {
                ordersCreated++;
                _logger.LogInformation("✅ Original order created successfully");
                Console.WriteLine("   ✅ Original order placed");
            }
            else
            {
                _logger.LogWarning("⚠️ Failed to create original direction order");
                Console.WriteLine($"   ⚠️ Original order failed: {originalOrderResult.ErrorMessage}");
            }
        }

        // Create opposite direction order
        if (providerConfig.TakeOpposite)
        {
            var oppositeDirection = signalForOrder.Direction == TradeDirection.Buy
                ? TradeDirection.Sell
                : TradeDirection.Buy;
            _logger.LogInformation("📝 Creating OPPOSITE direction order: {Direction}", oppositeDirection);
            Console.WriteLine($"   📝 Creating OPPOSITE order ({oppositeDirection})...");
            var oppositeSignal = TryRecalculateStopsForOpposite(signalForOrder, oppositeDirection);
            // NOTE: Do NOT set oppositeSignal.Direction here!
            // The isOpposite=true flag tells ProcessSignalAsync to flip the direction via GetEffectiveDirection.
            // Setting it here would cause a double-flip (Buy -> Sell -> Buy).
            oppositeOrderResult = await _pendingOrderService.ProcessSignalAsync(oppositeSignal, isOpposite: true);
            if (oppositeOrderResult.Success)
            {
                ordersCreated++;
                _logger.LogInformation("✅ Opposite order created successfully");
                Console.WriteLine("   ✅ Opposite order placed");
            }
            else
            {
                _logger.LogWarning("⚠️ Failed to create opposite direction order");
                Console.WriteLine($"   ⚠️ Opposite order failed: {oppositeOrderResult.ErrorMessage}");
            }
        }

        // --- PATCH: Check if already processed (immediate fill) ---
        bool alreadyProcessed = false;
        if (signal.SignalId > 0)
        {
            alreadyProcessed = await _repository.IsSignalProcessedAsync(signal.SignalId);
        }

        // Determine if we should mark as processed
        // Mark as processed if ANY order was successfully created (either original or opposite)
        var originalSuccess = originalOrderResult is { Success: true };
        var oppositeSuccess = oppositeOrderResult is { Success: true };
        var anyOrderSucceeded = originalSuccess || oppositeSuccess;

        _logger.LogInformation(
            "[PROCESSED-CHECK] SignalId={SignalId}, AlreadyProcessed={AlreadyProcessed}, OriginalSuccess={OriginalSuccess}, OppositeSuccess={OppositeSuccess}, AnyOrderSucceeded={AnyOrderSucceeded}, OrdersCreated={OrdersCreated}, ExpectedOrders={ExpectedOrders}",
            signal.SignalId, alreadyProcessed, originalSuccess, oppositeSuccess, anyOrderSucceeded, ordersCreated, expectedOrders);

        if (!alreadyProcessed && signal.SignalId > 0)
        {
            if (anyOrderSucceeded)
            {
                // At least one order was successfully created - mark as processed to prevent duplicates
                _logger.LogInformation("[PRE] Marking as processed (at least one order succeeded) for SignalId={SignalId}", signal.SignalId);
                try
                {
                    await _repository.MarkSignalAsProcessedAsync(signal.SignalId);
                    _logger.LogInformation("[POST] MarkSignalAsProcessedAsync completed for SignalId={SignalId}", signal.SignalId);
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "[ERROR] Failed to mark signal as processed: SignalId={SignalId}", signal.SignalId);
                    // Don't rethrow - we still want to log success below
                }
            }
            else
            {
                // No orders were created - leave unprocessed for retry
                _logger.LogWarning(
                    "Leaving signal unprocessed because no orders were created (SignalId={SignalId}, OrdersCreated={OrdersCreated}, ExpectedOrders={ExpectedOrders}, OriginalError={OriginalError}, OppositeError={OppositeError})",
                    signal.SignalId,
                    ordersCreated,
                    expectedOrders,
                    originalOrderResult?.ErrorMessage ?? "N/A",
                    oppositeOrderResult?.ErrorMessage ?? "N/A");
            }
        }
        else if (alreadyProcessed)
        {
            _logger.LogInformation("[SKIP] SignalId={SignalId} already marked as processed", signal.SignalId);
        }

        _logger.LogInformation("✅ Forex signal #{SignalId} processed - {Count} order(s) created", 
            signal.SignalId, ordersCreated);
        Console.WriteLine($"✅ Signal #{signal.SignalId} complete - {ordersCreated} pending order(s) on cTrader");
        Console.WriteLine();
        }
        finally
        {
            if (signal.SignalId > 0)
                _inFlightSignals.TryRemove(signal.SignalId, out _);
        }
    }

    private ParsedSignal TryRecalculateStopsForOpposite(ParsedSignal original, TradeDirection oppositeDirection)
    {
        // Only supported when we have an explicit entry price.
        if (!original.EntryPrice.HasValue)
            return original;

        var entry = original.EntryPrice.Value;

        decimal? AdjustStopLoss(decimal? stopLoss)
        {
            if (!stopLoss.HasValue)
                return null;

            var dist = Math.Abs(entry - stopLoss.Value);
            return oppositeDirection switch
            {
                TradeDirection.Buy => entry - dist,
                TradeDirection.Sell => entry + dist,
                _ => stopLoss
            };
        }

        decimal? AdjustTakeProfit(decimal? takeProfit)
        {
            if (!takeProfit.HasValue)
                return null;

            var dist = Math.Abs(takeProfit.Value - entry);
            return oppositeDirection switch
            {
                TradeDirection.Buy => entry + dist,
                TradeDirection.Sell => entry - dist,
                _ => takeProfit
            };
        }

        var recalculated = new ParsedSignal
        {
            SignalId = original.SignalId,
            Asset = original.Asset,
            Direction = original.Direction,
            EntryPrice = original.EntryPrice,
            StopLoss = AdjustStopLoss(original.StopLoss),
            TakeProfit = AdjustTakeProfit(original.TakeProfit),
            TakeProfit2 = AdjustTakeProfit(original.TakeProfit2),
            TakeProfit3 = AdjustTakeProfit(original.TakeProfit3),
            TakeProfit4 = AdjustTakeProfit(original.TakeProfit4),
            RiskRewardRatio = original.RiskRewardRatio,
            LotSize = original.LotSize,
            ProviderChannelId = original.ProviderChannelId,
            ProviderName = original.ProviderName,
            SignalType = original.SignalType,
            ReceivedAt = original.ReceivedAt,
            RawMessage = original.RawMessage,
            Pattern = original.Pattern,
            Timeframe = original.Timeframe,
            Processed = original.Processed,
            ProcessedAt = original.ProcessedAt
        };

        // Helpful trace so you can confirm the opposite leg got flipped stops.
        _logger.LogInformation(
            "📐 Opposite SL/TP recalculated from entry {Entry} for {Asset}: SL {OldSL}->{NewSL}, TP1 {OldTP}->{NewTP}",
            entry,
            original.Asset,
            original.StopLoss,
            recalculated.StopLoss,
            original.TakeProfit,
            recalculated.TakeProfit);

        return recalculated;
    }
}
