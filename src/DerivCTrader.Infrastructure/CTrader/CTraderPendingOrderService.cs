using DerivCTrader.Application.Interfaces;
using DerivCTrader.Domain.Entities;
using DerivCTrader.Domain.Enums;
using DerivCTrader.Infrastructure.CTrader.Interfaces;
using DerivCTrader.Infrastructure.CTrader.Models;
using Microsoft.Extensions.Logging;
using OpenAPI.Net;

namespace DerivCTrader.Infrastructure.CTrader;

/// <summary>
/// Orchestrates the complete cTrader pending order flow:
/// 1. Place pending order at entry price
/// 2. Monitor price ticks
/// 3. Detect entry cross in correct direction
/// 4. Write execution to TradeExecutionQueue for Deriv processing
/// </summary>
public class CTraderPendingOrderService : ICTraderPendingOrderService
{
    /// <summary>
    /// Determines if the asset is a Deriv synthetic index (used for special order classification logic).
    /// </summary>
    private static bool IsSyntheticAsset(string asset)
    {
        if (string.IsNullOrWhiteSpace(asset))
            return false;
        // Example: Deriv synthetics often start with 'R_', 'BOOM', 'CRASH', etc.
        return asset.StartsWith("R_", StringComparison.OrdinalIgnoreCase)
            || asset.StartsWith("BOOM", StringComparison.OrdinalIgnoreCase)
            || asset.StartsWith("CRASH", StringComparison.OrdinalIgnoreCase)
            || asset.StartsWith("VOL", StringComparison.OrdinalIgnoreCase)
            || asset.StartsWith("JUMP", StringComparison.OrdinalIgnoreCase)
            || asset.StartsWith("STEP", StringComparison.OrdinalIgnoreCase)
            || asset.StartsWith("BEAR", StringComparison.OrdinalIgnoreCase)
            || asset.StartsWith("BULL", StringComparison.OrdinalIgnoreCase)
            || asset.StartsWith("WS", StringComparison.OrdinalIgnoreCase)
            || asset.Contains("SYNTH", StringComparison.OrdinalIgnoreCase)
            || asset.Contains("Volatility", StringComparison.OrdinalIgnoreCase)
            || asset.Contains("Crash", StringComparison.OrdinalIgnoreCase)
            || asset.Contains("Boom", StringComparison.OrdinalIgnoreCase);
    }
    private readonly ILogger<CTraderPendingOrderService> _logger;
    private readonly ICTraderClient _client;
    private readonly ICTraderOrderManager _orderManager;
    private readonly ICTraderSymbolService _symbolService;
    private readonly ITradeRepository _repository;
    private readonly ITelegramNotifier _telegram;
    private readonly DerivCTrader.Infrastructure.Deriv.IDerivTickProvider _derivTickProvider;

    private readonly Dictionary<long, PendingExecutionWatch> _pendingExecutions = new();
    private readonly object _pendingLock = new();

    private readonly Dictionary<long, int> _positionToForexTradeId = new();
    private readonly object _positionLock = new();

    private readonly Dictionary<(int SignalId, bool IsOpposite), long> _placedOrdersBySignalLeg = new();
    private readonly object _placedOrdersLock = new();

    public CTraderPendingOrderService(
        ILogger<CTraderPendingOrderService> logger,
        ICTraderClient client,
        ICTraderOrderManager orderManager,
        ICTraderSymbolService symbolService,
        ITradeRepository repository,
        ITelegramNotifier telegram,
        DerivCTrader.Infrastructure.Deriv.IDerivTickProvider derivTickProvider)
    {
        _logger = logger;
        _client = client;
        _orderManager = orderManager;
        _symbolService = symbolService;
        _repository = repository;
        _telegram = telegram;
        _derivTickProvider = derivTickProvider;

        // Subscribe to execution events from cTrader.
        // This is the most reliable way to detect pending order fills.
        _client.MessageReceived += OnClientMessageReceived;
    }

    /// <summary>
    /// Process a parsed signal:
    /// 1. Validate symbol exists
    /// 2. Place pending order at entry price
    /// 3. Start monitoring for price cross
    /// </summary>
    public async Task<CTraderOrderResult> ProcessSignalAsync(ParsedSignal signal, bool isOpposite = false)
    {
        try
        {
            var effectiveDirection = GetEffectiveDirection(signal.Direction, isOpposite);

            // Duplicate suppression: in-memory (process lifetime) only for the specific leg (original vs opposite)
            // NOTE: We do NOT check DB-level IsSignalProcessedAsync here because:
            //   1. When original order is immediately filled, it may mark signal as processed
            //   2. But we still need to allow the opposite order to be created
            //   3. The in-memory check per-leg is sufficient to prevent true duplicates
            if (signal.SignalId > 0)
            {
                // In-memory check - keyed by (SignalId, IsOpposite) so original and opposite are tracked separately
                lock (_placedOrdersLock)
                {
                    if (_placedOrdersBySignalLeg.ContainsKey((signal.SignalId, isOpposite)))
                    {
                        _logger.LogWarning(
                            "Duplicate order suppressed for SignalId={SignalId} (IsOpposite={IsOpposite}) [in-memory]. This process will only place one order per signal leg.",
                            signal.SignalId,
                            isOpposite);
                        _logger.LogDebug("[DEBUG] In-memory duplicate suppression hit for SignalId={SignalId}, IsOpposite={IsOpposite}", signal.SignalId, isOpposite);
                        return new CTraderOrderResult
                        {
                            Success = false,
                            ErrorMessage = "Duplicate order suppressed for this signal/leg (in-memory)"
                        };
                    }
                }

                // DB-level check ONLY for the original order (not opposite)
                // This prevents re-processing signals across service restarts
                if (!isOpposite && await _repository.IsSignalProcessedAsync(signal.SignalId))
                {
                    _logger.LogWarning(
                        "Duplicate order suppressed for SignalId={SignalId} (IsOpposite={IsOpposite}) [DB]. Signal already marked as processed in DB.",
                        signal.SignalId,
                        isOpposite);
                    _logger.LogDebug("[DEBUG] DB duplicate suppression hit for SignalId={SignalId}, IsOpposite={IsOpposite}", signal.SignalId, isOpposite);
                    return new CTraderOrderResult
                    {
                        Success = false,
                        ErrorMessage = "Duplicate order suppressed for this signal/leg (DB)"
                    };
                }
            }

            _logger.LogInformation("📝 Processing cTrader signal: {Asset} {Direction} @ {Entry} (IsOpposite={IsOpposite})",
                signal.Asset, effectiveDirection, signal.EntryPrice, isOpposite);

            _logger.LogDebug("[DEBUG] Begin ProcessSignalAsync for SignalId={SignalId}, IsOpposite={IsOpposite}, EntryPrice={EntryPrice}", signal.SignalId, isOpposite, signal.EntryPrice);


            if (signal.EntryPrice.HasValue)
            {
                CTraderOrderType resolvedOrderType;
                if (IsSyntheticAsset(signal.Asset))
                {
                    // Use Deriv price probe for classification
                    _logger.LogInformation("[SYNTHETIC] Using Deriv price probe for order type classification: {Asset}", signal.Asset);
                    // Map signal.Asset to Deriv symbol for tick subscription
                    string derivSymbol = MapToDerivSymbol(signal.Asset);
                    var semaphore = new System.Threading.SemaphoreSlim(3, 3); // Should be injected/shared in real code
                    await using var probe = new DerivCTrader.Infrastructure.Deriv.DerivPriceProbe(_derivTickProvider, derivSymbol, semaphore);
                    var price = await probe.ProbeAsync();
                    if (!price.HasValue)
                    {
                        _logger.LogError("[SYNTHETIC] Deriv price probe failed: Asset={Asset}. Marking as UNCLASSIFIED.", signal.Asset);
                        // Mark as UNCLASSIFIED (could update DB or return special result)
                        return new CTraderOrderResult
                        {
                            Success = false,
                            ErrorMessage = "Deriv price unavailable for synthetic; signal marked UNCLASSIFIED."
                        };
                    }
                    var entry = (decimal)signal.EntryPrice.Value;
                    if (effectiveDirection == TradeDirection.Buy)
                        resolvedOrderType = entry > price.Value ? CTraderOrderType.Stop : CTraderOrderType.Limit;
                    else if (effectiveDirection == TradeDirection.Sell)
                        resolvedOrderType = entry < price.Value ? CTraderOrderType.Stop : CTraderOrderType.Limit;
                    else
                        resolvedOrderType = CTraderOrderType.Limit;
                    if (entry == price.Value)
                        resolvedOrderType = CTraderOrderType.Stop; // Default to STOP if equal
                    _logger.LogInformation("[SYNTHETIC] Classified order type: {OrderType} (Entry={Entry}, Price={Price})", resolvedOrderType, entry, price);
                }
                else
                {
                    resolvedOrderType = await InferPendingOrderTypeAsync(signal, effectiveDirection, isOpposite);
                }

                _logger.LogInformation("[DEBUG] About to create order: Asset={Asset} EntryPrice={EntryPrice} OrderType={OrderType}", signal.Asset, signal.EntryPrice, resolvedOrderType);
                var pendingResult = await _orderManager.CreateOrderAsync(signal, resolvedOrderType, isOpposite);

                if (!pendingResult.Success || !pendingResult.OrderId.HasValue)
                {
                    _logger.LogWarning("[DEBUG] Order creation failed or no OrderId returned: SignalId={SignalId}, Error={Error}", signal.SignalId, pendingResult.ErrorMessage);
                    return pendingResult;
                }

                var orderId = pendingResult.OrderId.Value;
                _logger.LogDebug("[DEBUG] Pending order placed: SignalId={SignalId}, OrderId={OrderId}, Type={OrderType}", signal.SignalId, orderId, resolvedOrderType);

                // Track in-memory that we placed an order for this signal leg
                lock (_placedOrdersLock)
                {
                    _placedOrdersBySignalLeg[(signal.SignalId, isOpposite)] = orderId;
                }

                // Determine if order filled immediately (has PositionId and ExecutedAt)
                var isImmediateFill = pendingResult.PositionId.HasValue && pendingResult.PositionId.Value > 0 && pendingResult.ExecutedAt != default;

                if (isImmediateFill)
                {
                    // Order filled immediately - persist and enqueue now
                    _logger.LogInformation("[IMMEDIATE-FILL] Order {OrderId} filled immediately with PositionId={PositionId}", orderId, pendingResult.PositionId);

                    await PersistForexTradeFillAsync(
                        signal,
                        cTraderOrderId: orderId,
                        positionId: pendingResult.PositionId!.Value,
                        effectiveDirection,
                        isOpposite,
                        pendingResult.ExecutedPrice,
                        pendingResult.SltpApplied);

                    await NotifyFillAsync(
                        signal,
                        cTraderOrderId: orderId,
                        positionId: pendingResult.PositionId!.Value,
                        effectiveDirection,
                        isOpposite,
                        pendingResult.ExecutedPrice,
                        pendingResult.SltpApplied);

                    // Mark signal as processed on immediate fill
                    if (signal.SignalId > 0)
                    {
                        _logger.LogInformation("[PRE] Calling MarkSignalAsProcessedAsync for SignalId={SignalId} (immediate fill)", signal.SignalId);
                        await _repository.MarkSignalAsProcessedAsync(signal.SignalId);
                        _logger.LogInformation("[POST] MarkSignalAsProcessedAsync completed for SignalId={SignalId} (immediate fill)", signal.SignalId);
                    }

                    // Enqueue for Deriv binary execution (use PositionId, not OrderId)
                    await EnqueueExecutedTradeAsync(signal, pendingResult.PositionId!.Value, isOpposite, effectiveDirection);
                }
                else
                {
                    // Order accepted but pending - add to tracking for later fill
                    _logger.LogInformation("[PENDING] Order {OrderId} accepted, waiting for fill. Adding to _pendingExecutions.", orderId);

                    var watch = new PendingExecutionWatch
                    {
                        OrderId = orderId,
                        Signal = signal,
                        EffectiveDirection = effectiveDirection,
                        IsOpposite = isOpposite,
                        CreatedAt = DateTime.UtcNow
                    };

                    lock (_pendingLock)
                    {
                        _pendingExecutions[orderId] = watch;
                    }

                    _logger.LogDebug("[DEBUG] Added to _pendingExecutions: OrderId={OrderId}, SignalId={SignalId}, IsOpposite={IsOpposite}", orderId, signal.SignalId, isOpposite);
                }

                return pendingResult;
            }

            // No EntryPrice => we will NOT place a market order from this service.
            // This prevents unexpected market executions (especially for opposite-leg logic).
            _logger.LogDebug("[DEBUG] No EntryPrice for SignalId={SignalId}, skipping market order.", signal.SignalId);
            return new CTraderOrderResult
            {
                Success = false,
                ErrorMessage = "EntryPrice is required for forex execution (market orders are disabled in this pipeline)"
            };
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error processing cTrader signal");
            _logger.LogDebug(ex, "[DEBUG] Exception in ProcessSignalAsync for SignalId={SignalId}", signal.SignalId);
            return new CTraderOrderResult
            {
                Success = false,
                ErrorMessage = ex.Message
            };
        }
    }

    // Map cTrader/Telegram asset name to Deriv symbol for tick subscription
    private static string MapToDerivSymbol(string asset)
    {
        // Example: "Volatility 25" => "1HZ25V"
        if (asset.Contains("Volatility", StringComparison.OrdinalIgnoreCase))
        {
            var match = System.Text.RegularExpressions.Regex.Match(asset, @"(\d+)");
            if (match.Success)
                return $"1HZ{match.Value}V";
        }
        // Add more mappings as needed for other synthetics
        return asset.Replace(" ", ""); // fallback: remove spaces
    }
    private void OnClientMessageReceived(object? sender, CTraderMessage message)
    {
        try
        {
            if (message.PayloadType != (int)ProtoOAPayloadType.ProtoOaExecutionEvent)
                return;

            var execEvent = ProtoOAExecutionEvent.Parser.ParseFrom(message.Payload);
            var execTypeName = execEvent.ExecutionType.ToString();

            // Log ALL execution events for diagnostics
            var positionId = TryExtractPositionId(execEvent);
            _logger.LogInformation("[DEBUG-ALL-EVENTS] Received execution event: Type={ExecutionType}, PositionId={PositionId}, OrderId={OrderId}",
                execTypeName, positionId?.ToString() ?? "null", execEvent.Order?.OrderId.ToString() ?? "null");

            // Check for position closed events (these can come as various execution types)
            if (IsPositionClosedExecution(execTypeName))
            {
                _logger.LogInformation("[POSITION-CLOSE] Detected position close event: Type={ExecutionType}, PositionId={PositionId}", execTypeName, positionId);
                _ = Task.Run(async () => await HandlePositionClosedAsync(execEvent, execTypeName));
                return;
            }

            // Check for SL/TP modification events (OrderModified, OrderReplaced, AmendOrder, etc.)
            if (IsSlTpModificationEvent(execTypeName))
            {
                _logger.LogInformation("[SLTP-MODIFY] Detected SL/TP modification event: Type={ExecutionType}, PositionId={PositionId}", execTypeName, positionId);
                _ = Task.Run(async () => await HandleSlTpModificationAsync(execEvent, execTypeName));
                return;
            }

            // Only treat as an entry fill when cTrader reports the order was filled.
            if (execEvent.ExecutionType != ProtoOAExecutionType.OrderFilled &&
                execEvent.ExecutionType != ProtoOAExecutionType.OrderPartialFill)
            {
                return;
            }

            // OrderId is a long in the cTrader protobuf
            var orderId = execEvent.Order?.OrderId;
            if (!orderId.HasValue || orderId.Value <= 0)
                return;

            PendingExecutionWatch? watch;
            lock (_pendingLock)
            {
                if (!_pendingExecutions.TryGetValue(orderId.Value, out watch))
                {
                    // This could be a close order or an order we didn't track - check if it's closing an existing position
                    _logger.LogInformation("[DEBUG] Fill event for unknown OrderId={OrderId}. Checking if it's a position close...", orderId.Value);

                    // Try to handle as a position close (the method will check if a ForexTrade exists for this position)
                    if (positionId.HasValue && positionId.Value > 0)
                    {
                        _ = Task.Run(async () => await TryHandlePositionCloseByFillAsync(execEvent, execTypeName, positionId.Value));
                    }
                    return;
                }

                // Remove first to prevent duplicates on multiple events.
                _pendingExecutions.Remove(orderId.Value);
                _logger.LogDebug(
                    "[DEBUG] Removed from _pendingExecutions: OrderId={OrderId}, SignalId={SignalId}, IsOpposite={IsOpposite}",
                    orderId.Value,
                    watch?.Signal?.SignalId,
                    watch?.IsOpposite);
            }

            if (watch != null)
            {
                _ = Task.Run(async () => await HandlePendingExecutionAsync(execEvent, watch));
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to handle execution event");
            _logger.LogDebug(ex, "[DEBUG] Exception in OnClientMessageReceived");
        }
    }

    private async Task HandlePendingExecutionAsync(ProtoOAExecutionEvent execEvent, PendingExecutionWatch watch)
    {
        try
        {
            // Ensure we have a valid signal to work with
            if (watch.Signal == null)
            {
                _logger.LogWarning("[SKIP] HandlePendingExecutionAsync called with null Signal for OrderId={OrderId}", watch.OrderId);
                return;
            }

            // OrderId is a long in cTrader protobuf, fallback to watch.OrderId if not present
            var orderId = execEvent.Order?.OrderId ?? watch.OrderId;
            var positionId = TryExtractPositionId(execEvent);
            var executedPrice = TryExtractExecutedPrice(execEvent);

            _logger.LogInformation("[TRACE] Entered HandlePendingExecutionAsync: OrderId={OrderId}, PositionId={PositionId}, Asset={Asset}, Direction={Direction}, IsOpposite={IsOpposite}",
                orderId, positionId, watch.Signal.Asset, watch.EffectiveDirection, watch.IsOpposite);

            _logger.LogInformation(
                "🎯 Pending order FILLED: OrderId={OrderId}, PositionId={PositionId}, Asset={Asset}, Direction={Direction}",
                orderId, positionId, watch.Signal.Asset, watch.EffectiveDirection);

            // Apply SL/TP post-fill if present.
            var stopLoss = watch.Signal.StopLoss.HasValue ? (double?)watch.Signal.StopLoss.Value : null;
            var takeProfit = SelectTakeProfit(watch.Signal);

            if (positionId.HasValue && (stopLoss.HasValue || takeProfit.HasValue))
            {
                var amended = await _orderManager.ModifyPositionAsync(positionId.Value, stopLoss, takeProfit);

                // Best-effort retry: sometimes fills settle before SL/TP can be amended.
                if (!amended)
                {
                    await Task.Delay(TimeSpan.FromSeconds(1));
                    amended = await _orderManager.ModifyPositionAsync(positionId.Value, stopLoss, takeProfit);
                }

                _logger.LogInformation(
                    amended
                        ? "✅ SL/TP applied after pending fill: PositionId={PositionId}, SL={SL}, TP={TP}"
                        : "⚠️ Failed to apply SL/TP after pending fill: PositionId={PositionId}, SL={SL}, TP={TP}",
                    positionId.Value,
                    stopLoss,
                    takeProfit);
            }

            if (positionId.HasValue && positionId.Value > 0)
            {
                await PersistForexTradeFillAsync(
                    watch.Signal,
                    cTraderOrderId: orderId,
                    positionId: positionId.Value,
                    watch.EffectiveDirection,
                    watch.IsOpposite,
                    executedPrice,
                    sltpApplied: null);

                await NotifyFillAsync(
                    watch.Signal,
                    cTraderOrderId: orderId,
                    positionId: positionId.Value,
                    watch.EffectiveDirection,
                    watch.IsOpposite,
                    executedPrice,
                    sltpApplied: null);

                // For post-fill (pending order later filled), mark as processed here
                // because the calling CTraderForexProcessorService has already returned.
                // This is different from immediate fill where the caller can still mark it.
                if (watch.Signal.SignalId > 0)
                {
                    _logger.LogInformation("[PRE] Calling MarkSignalAsProcessedAsync for SignalId={SignalId} (post-fill)", watch.Signal.SignalId);
                    await _repository.MarkSignalAsProcessedAsync(watch.Signal.SignalId);
                    _logger.LogInformation("[POST] MarkSignalAsProcessedAsync completed for SignalId={SignalId} (post-fill)", watch.Signal.SignalId);
                }
            }

            // Enqueue execution for Deriv (use PositionId, not OrderId)
            if (positionId.HasValue && positionId.Value > 0)
            {
                await EnqueueExecutedTradeAsync(watch.Signal, positionId.Value, watch.IsOpposite, watch.EffectiveDirection);
            }
            else
            {
                _logger.LogWarning("[ENQUEUE-SKIP] No valid PositionId available for OrderId={OrderId}, skipping queue entry", orderId);
            }
            _logger.LogInformation("[TRACE] Exiting HandlePendingExecutionAsync: OrderId={OrderId}, PositionId={PositionId}, Asset={Asset}, Direction={Direction}, IsOpposite={IsOpposite}",
                orderId, positionId, watch.Signal.Asset, watch.EffectiveDirection, watch.IsOpposite);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed handling pending execution for OrderId={OrderId}", watch.OrderId);
        }
    }



    private async Task PersistForexTradeFillAsync(
        ParsedSignal signal,
        long cTraderOrderId,
        long positionId,
        TradeDirection effectiveDirection,
        bool isOpposite,
        double? executedPrice,
        bool? sltpApplied)
    {
        try
        {
            // Avoid duplicate inserts if we get multiple fill events.
            lock (_positionLock)
            {
                if (_positionToForexTradeId.ContainsKey(positionId))
                    return;
            }

            var trade = new ForexTrade
            {
                PositionId = positionId,
                Symbol = signal.Asset ?? string.Empty,
                Direction = effectiveDirection.ToString(),
                // Persist the requested entry from the signal as the trade's EntryPrice.
                // The fill/execution price can legitimately differ (e.g., marketable LIMIT fills at better price).
                EntryPrice = signal.EntryPrice ?? (executedPrice.HasValue ? (decimal?)Convert.ToDecimal(executedPrice.Value) : null),
                SL = signal.StopLoss,
                TP = signal.TakeProfit,
                EntryTime = DateTime.UtcNow,
                Status = "OPEN",
                Strategy = BuildStrategyName(signal),
                CreatedAt = DateTime.UtcNow,
                Notes = BuildForexNotes(signal, cTraderOrderId, positionId, isOpposite, sltpApplied, executedPrice),
                // DB column is NOT NULL; default to false until indicators are linked.
                IndicatorsLinked = false
            };

            var tradeId = await _repository.CreateForexTradeAsync(trade);

            lock (_positionLock)
            {
                _positionToForexTradeId[positionId] = tradeId;
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to persist ForexTrade on fill: PositionId={PositionId}", positionId);
        }
    }

    private async Task NotifyFillAsync(
        ParsedSignal signal,
        long cTraderOrderId,
        long positionId,
        TradeDirection effectiveDirection,
        bool isOpposite,
        double? executedPrice,
        bool? sltpApplied)
    {
        // User-required format:
        // 🎯 GBPUSD Sell @ 1.27500, TP: 1.26500, SL: 1.28000
        var utcNow = DateTime.UtcNow;

        var entry = signal.EntryPrice;
        if (!entry.HasValue && executedPrice.HasValue)
            entry = Convert.ToDecimal(executedPrice.Value);

        var tp = SelectTakeProfit(signal);
        var sl = signal.StopLoss;

        static string F(decimal? v) => v.HasValue
            ? v.Value.ToString("0.00000", System.Globalization.CultureInfo.InvariantCulture)
            : "-";

        var tpDecimal = tp.HasValue ? (decimal?)Convert.ToDecimal(tp.Value) : null;

        var msg =
            $"🎯 {signal.Asset} {effectiveDirection} @ {F(entry)}, TP: {F(tpDecimal)}, SL: {F(sl)}";

        // Send notification and capture message_id for threading
        int? sentMessageId;
        if (signal.NotificationMessageId.HasValue)
        {
            sentMessageId = await _telegram.SendTradeMessageAsync(msg, signal.NotificationMessageId.Value);
        }
        else
        {
            sentMessageId = await _telegram.SendTradeMessageWithIdAsync(msg);
        }

        // Store the message_id in ForexTrade for threading close/modify notifications
        if (sentMessageId.HasValue)
        {
            int? tradeId = null;
            lock (_positionLock)
            {
                _positionToForexTradeId.TryGetValue(positionId, out var id);
                tradeId = id > 0 ? id : null;
            }

            if (tradeId.HasValue)
            {
                try
                {
                    await _repository.UpdateForexTradeTelegramMessageIdAsync(tradeId.Value, sentMessageId.Value);
                    _logger.LogInformation("[TELEGRAM-THREAD] Stored TelegramMessageId={MessageId} for TradeId={TradeId}, PositionId={PositionId}",
                        sentMessageId.Value, tradeId.Value, positionId);
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Failed to store TelegramMessageId for TradeId={TradeId}", tradeId.Value);
                }
            }
        }
    }

    private async Task HandlePositionClosedAsync(ProtoOAExecutionEvent execEvent, string execTypeName)
    {
        try
        {
            _logger.LogInformation("[TRACE] Entered HandlePositionClosedAsync for position close event. Checking positionId...");
            var positionId = TryExtractPositionId(execEvent);
            _logger.LogInformation("[TRACE] HandlePositionClosedAsync: Extracted PositionId={PositionId}", positionId);
            if (!positionId.HasValue || positionId.Value <= 0)
            {
                _logger.LogWarning("[TRACE] HandlePositionClosedAsync: No valid PositionId found. Skipping forex trade update.");
                return;
            }

            int? tradeId = null;
            lock (_positionLock)
            {
                if (_positionToForexTradeId.TryGetValue(positionId.Value, out var id))
                {
                    tradeId = id;
                    _logger.LogInformation("[TRACE] HandlePositionClosedAsync: Found tradeId={TradeId} for PositionId={PositionId}", tradeId, positionId);
                }
                else
                {
                    _logger.LogWarning("[TRACE] HandlePositionClosedAsync: No tradeId found in _positionToForexTradeId for PositionId={PositionId}", positionId);
                }
            }

            ForexTrade? trade = null;
            if (tradeId.HasValue)
            {
                trade = await _repository.GetForexTradeByIdAsync(tradeId.Value);
                _logger.LogInformation("[TRACE] HandlePositionClosedAsync: Loaded ForexTrade by TradeId={TradeId}: {Trade}", tradeId, trade);
            }
            else
            {
                trade = await _repository.FindLatestForexTradeByCTraderPositionIdAsync(positionId.Value);
                _logger.LogInformation("[TRACE] HandlePositionClosedAsync: Loaded ForexTrade by PositionId={PositionId}: {Trade}", positionId, trade);
            }

            if (trade == null)
            {
                _logger.LogWarning("[TRACE] HandlePositionClosedAsync: No ForexTrade found for PositionId={PositionId}. Skipping update.", positionId);
                return;
            }

            var exitPrice = TryExtractExitPrice(execEvent);
            var pnl = TryExtractProfit(execEvent);

            trade.ExitTime = DateTime.UtcNow;
            trade.ExitPrice = exitPrice.HasValue ? (decimal?)Convert.ToDecimal(exitPrice.Value) : trade.ExitPrice;
            trade.PnL = pnl.HasValue ? (decimal?)pnl.Value : trade.PnL;
            trade.Status = "CLOSED";
            trade.Outcome = DetermineOutcome(trade.PnL);
            trade.RR = CalculateRiskReward(trade.EntryPrice, trade.SL, trade.TP);
            trade.Notes = AppendCloseInfo(trade.Notes, execTypeName);

            _logger.LogInformation("[TRACE] HandlePositionClosedAsync: Updating ForexTrade: TradeId={TradeId}, ExitPrice={ExitPrice}, ExitTime={ExitTime}, PnL={PnL}, Outcome={Outcome}, RR={RR}, Status={Status}", trade.TradeId, trade.ExitPrice, trade.ExitTime, trade.PnL, trade.Outcome, trade.RR, trade.Status);
            await _repository.UpdateForexTradeAsync(trade);

            var reason = InferCloseReason(execTypeName, trade.Notes, trade.ExitPrice, trade.SL, trade.TP);
            var msg = FormatCloseMessage(trade.Symbol, trade.Direction, trade.ExitPrice, trade.PnL, reason, trade.RR);

            // Send as reply to the fill notification if available
            if (trade.TelegramMessageId.HasValue)
            {
                await _telegram.SendTradeMessageAsync(msg, trade.TelegramMessageId.Value);
            }
            else
            {
                await _telegram.SendTradeMessageAsync(msg);
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to handle position closed event");
        }
    }

    /// <summary>
    /// Handles potential position close when we receive an OrderFilled event for an unknown order.
    /// This can happen when a position is closed manually or via a close order we didn't track.
    /// </summary>
    private async Task TryHandlePositionCloseByFillAsync(ProtoOAExecutionEvent execEvent, string execTypeName, long positionId)
    {
        try
        {
            _logger.LogInformation("[TRY-CLOSE] Checking if fill event is closing position: PositionId={PositionId}", positionId);

            // Check if we have an OPEN ForexTrade for this position
            var trade = await _repository.FindLatestForexTradeByCTraderPositionIdAsync(positionId);

            if (trade == null)
            {
                _logger.LogDebug("[TRY-CLOSE] No ForexTrade found for PositionId={PositionId}", positionId);
                return;
            }

            if (trade.Status != "OPEN")
            {
                _logger.LogDebug("[TRY-CLOSE] ForexTrade for PositionId={PositionId} is not OPEN (Status={Status})", positionId, trade.Status);
                return;
            }

            // This fill event is likely closing our tracked position
            _logger.LogInformation("[TRY-CLOSE] Found OPEN ForexTrade for PositionId={PositionId}, treating as position close", positionId);

            var exitPrice = TryExtractExitPrice(execEvent) ?? TryExtractExecutedPrice(execEvent);
            var pnl = TryExtractProfit(execEvent);

            trade.ExitTime = DateTime.UtcNow;
            trade.ExitPrice = exitPrice.HasValue ? (decimal?)Convert.ToDecimal(exitPrice.Value) : trade.ExitPrice;
            trade.PnL = pnl.HasValue ? (decimal?)pnl.Value : trade.PnL;
            trade.Status = "CLOSED";
            trade.Outcome = DetermineOutcome(trade.PnL);
            trade.RR = CalculateRiskReward(trade.EntryPrice, trade.SL, trade.TP);
            trade.Notes = AppendCloseInfo(trade.Notes, $"ClosedByFill_{execTypeName}");

            _logger.LogInformation("[TRY-CLOSE] Updating ForexTrade: TradeId={TradeId}, ExitPrice={ExitPrice}, ExitTime={ExitTime}, PnL={PnL}, Outcome={Outcome}, RR={RR}, Status={Status}",
                trade.TradeId, trade.ExitPrice, trade.ExitTime, trade.PnL, trade.Outcome, trade.RR, trade.Status);

            await _repository.UpdateForexTradeAsync(trade);

            var reason = InferCloseReason(execTypeName, trade.Notes, trade.ExitPrice, trade.SL, trade.TP);
            var msg = FormatCloseMessage(trade.Symbol, trade.Direction, trade.ExitPrice, trade.PnL, reason, trade.RR);

            await _telegram.SendTradeMessageAsync(msg);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to handle potential position close by fill for PositionId={PositionId}", positionId);
        }
    }

    private static bool IsPositionClosedExecution(string execTypeName)
    {
        if (string.IsNullOrWhiteSpace(execTypeName))
            return false;

        // We intentionally avoid hard dependencies on specific enum members.
        // Different OpenAPI/protobuf versions name these slightly differently.
        return (execTypeName.Contains("Position", StringComparison.OrdinalIgnoreCase) &&
                execTypeName.Contains("Closed", StringComparison.OrdinalIgnoreCase)) ||
               execTypeName.Contains("ClosePosition", StringComparison.OrdinalIgnoreCase) ||
               execTypeName.Contains("StopLoss", StringComparison.OrdinalIgnoreCase) ||
               execTypeName.Contains("TakeProfit", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Detect if the execution event is a SL/TP modification (order amended/modified)
    /// </summary>
    private static bool IsSlTpModificationEvent(string execTypeName)
    {
        if (string.IsNullOrWhiteSpace(execTypeName))
            return false;

        // cTrader sends these types when SL/TP is modified on an existing position
        return execTypeName.Contains("Modified", StringComparison.OrdinalIgnoreCase) ||
               execTypeName.Contains("Replaced", StringComparison.OrdinalIgnoreCase) ||
               execTypeName.Contains("Amend", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Handle SL/TP modification events - log the change and send notification for filled orders.
    /// Handles both pending orders (ParsedSignalsQueue) and active positions (ForexTrades).
    /// </summary>
    private async Task HandleSlTpModificationAsync(ProtoOAExecutionEvent execEvent, string execTypeName)
    {
        try
        {
            var positionId = TryExtractPositionId(execEvent);
            var orderId = execEvent.Order?.OrderId;

            // Extract the new SL/TP values from the event
            var newSL = TryExtractStopLoss(execEvent);
            var newTP = TryExtractTakeProfit(execEvent);
            var newSLDecimal = newSL.HasValue ? (decimal?)Convert.ToDecimal(newSL.Value) : null;
            var newTPDecimal = newTP.HasValue ? (decimal?)Convert.ToDecimal(newTP.Value) : null;

            // Case 1: Check if this is a pending order we're tracking
            PendingExecutionWatch? watch = null;
            if (orderId.HasValue && orderId.Value > 0)
            {
                lock (_pendingLock)
                {
                    _pendingExecutions.TryGetValue(orderId.Value, out watch);
                }
            }

            if (watch?.Signal != null)
            {
                // This is a pending order modification (not yet filled)
                _logger.LogInformation(
                    "[SLTP-MODIFY] Pending Order {OrderId} ({Asset}) SL/TP modified: NewSL={NewSL}, NewTP={NewTP}",
                    orderId, watch.Signal.Asset, newSL?.ToString() ?? "unchanged", newTP?.ToString() ?? "unchanged");

                // Update ParsedSignalsQueue (database only - no notification for pending orders)
                if (watch.Signal.SignalId > 0)
                {
                    await _repository.UpdateParsedSignalSlTpAsync(watch.Signal.SignalId, newSLDecimal, newTPDecimal);
                }

                // Update the watch object in memory as well
                if (newSLDecimal.HasValue) watch.Signal.StopLoss = newSLDecimal.Value;
                if (newTPDecimal.HasValue) watch.Signal.TakeProfit = newTPDecimal.Value;

                _logger.LogInformation("[SLTP-MODIFY] Database updated for pending order {OrderId} - no notification sent (order not filled yet)", orderId);
                return;
            }

            // Case 2: Check if this is an active position (filled trade)
            if (!positionId.HasValue || positionId.Value <= 0)
            {
                _logger.LogDebug("[SLTP-MODIFY] No valid PositionId in modification event and no pending order found");
                return;
            }

            var trade = await _repository.FindLatestForexTradeByCTraderPositionIdAsync(positionId.Value);
            if (trade == null)
            {
                _logger.LogDebug("[SLTP-MODIFY] No ForexTrade found for PositionId={PositionId}", positionId.Value);
                return;
            }

            _logger.LogInformation(
                "[SLTP-MODIFY] Position {PositionId} ({Symbol}) SL/TP modified: NewSL={NewSL}, NewTP={NewTP}",
                positionId.Value, trade.Symbol, newSL?.ToString() ?? "unchanged", newTP?.ToString() ?? "unchanged");

            // Update the trade SL/TP columns
            if (newSLDecimal.HasValue) trade.SL = newSLDecimal.Value;
            if (newTPDecimal.HasValue) trade.TP = newTPDecimal.Value;

            // Update the trade notes with the modification info
            var modInfo = $"SLTPModified@{DateTime.UtcNow:HH:mm:ss}";
            if (newSL.HasValue) modInfo += $";NewSL={newSL.Value}";
            if (newTP.HasValue) modInfo += $";NewTP={newTP.Value}";
            trade.Notes = AppendCloseInfo(trade.Notes, modInfo);

            await _repository.UpdateForexTradeAsync(trade);

            // ✅ Send notification for active filled positions (reply to fill notification)
            var posMsg = $"📝 SL/TP Modified: {trade.Symbol} {trade.Direction}";
            if (newSL.HasValue && newTP.HasValue)
            {
                posMsg += $"\nSL: {newSL.Value:0.00000}, TP: {newTP.Value:0.00000}";
            }
            else if (newSL.HasValue)
            {
                posMsg += $"\nSL: {newSL.Value:0.00000}";
            }
            else if (newTP.HasValue)
            {
                posMsg += $"\nTP: {newTP.Value:0.00000}";
            }

            if (trade.TelegramMessageId.HasValue)
            {
                await _telegram.SendTradeMessageAsync(posMsg.Trim(), trade.TelegramMessageId.Value);
                _logger.LogInformation("[SLTP-MODIFY] Telegram notification sent as reply to message {MessageId}", trade.TelegramMessageId.Value);
            }
            else
            {
                await _telegram.SendTradeMessageAsync(posMsg.Trim());
                _logger.LogInformation("[SLTP-MODIFY] Telegram notification sent (no thread - TelegramMessageId not available)");
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to handle SL/TP modification event");
        }
    }

    /// <summary>
    /// Extract Stop Loss price from execution event
    /// </summary>
    private static double? TryExtractStopLoss(ProtoOAExecutionEvent execEvent)
    {
        try
        {
            // Try Position.StopLoss
            var position = execEvent.GetType().GetProperty("Position")?.GetValue(execEvent);
            if (position != null)
            {
                var sl = TryExtractScaledPrice(position, new[] { "StopLoss", "StopLossPrice" });
                if (sl.HasValue && sl.Value > 0)
                    return sl;
            }

            // Try Order.StopLoss
            var order = execEvent.GetType().GetProperty("Order")?.GetValue(execEvent);
            if (order != null)
            {
                var sl = TryExtractScaledPrice(order, new[] { "StopLoss", "StopLossPrice" });
                if (sl.HasValue && sl.Value > 0)
                    return sl;
            }

            return null;
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// Extract Take Profit price from execution event
    /// </summary>
    private static double? TryExtractTakeProfit(ProtoOAExecutionEvent execEvent)
    {
        try
        {
            // Try Position.TakeProfit
            var position = execEvent.GetType().GetProperty("Position")?.GetValue(execEvent);
            if (position != null)
            {
                var tp = TryExtractScaledPrice(position, new[] { "TakeProfit", "TakeProfitPrice" });
                if (tp.HasValue && tp.Value > 0)
                    return tp;
            }

            // Try Order.TakeProfit
            var order = execEvent.GetType().GetProperty("Order")?.GetValue(execEvent);
            if (order != null)
            {
                var tp = TryExtractScaledPrice(order, new[] { "TakeProfit", "TakeProfitPrice" });
                if (tp.HasValue && tp.Value > 0)
                    return tp;
            }

            return null;
        }
        catch
        {
            return null;
        }
    }

    private static string BuildForexNotes(
        ParsedSignal signal,
        long cTraderOrderId,
        long positionId,
        bool isOpposite,
        bool? sltpApplied,
        double? executedPrice)
    {
        var parts = new List<string>
        {
            $"SignalId={signal.SignalId}",
            $"Provider={signal.ProviderName}",
            $"ProviderChannelId={signal.ProviderChannelId}",
            $"CTraderOrderId={cTraderOrderId}",
            $"CTraderPositionId={positionId}",
            $"IsOpposite={(isOpposite ? 1 : 0)}",
            $"SLTPApplied={(sltpApplied.HasValue ? (sltpApplied.Value ? 1 : 0) : -1)}"
        };

        if (executedPrice.HasValue && executedPrice.Value > 0)
            parts.Add($"ExecutedPrice={executedPrice.Value.ToString(System.Globalization.CultureInfo.InvariantCulture)}");

        return string.Join(";", parts);
    }

    private static string? AppendCloseInfo(string? notes, string execTypeName)
    {
        var marker = $"CloseEvent={execTypeName}";
        if (string.IsNullOrWhiteSpace(notes))
            return marker;

        if (notes.Contains(marker, StringComparison.OrdinalIgnoreCase))
            return notes;

        return notes + ";" + marker;
    }

    private static string InferCloseReason(string execTypeName, string? notes, decimal? exitPrice = null, decimal? sl = null, decimal? tp = null)
    {
        // First check explicit execution type names
        if (execTypeName.Contains("TakeProfit", StringComparison.OrdinalIgnoreCase) ||
            (notes?.Contains("TakeProfit", StringComparison.OrdinalIgnoreCase) ?? false))
            return "TP Hit";

        if (execTypeName.Contains("StopLoss", StringComparison.OrdinalIgnoreCase) ||
            (notes?.Contains("StopLoss", StringComparison.OrdinalIgnoreCase) ?? false))
            return "SL Hit";

        // If exit price is available, check which target (SL or TP) is CLOSEST
        if (exitPrice.HasValue && exitPrice.Value > 0)
        {
            decimal? slDiff = null;
            decimal? tpDiff = null;

            if (sl.HasValue && sl.Value > 0)
                slDiff = Math.Abs(exitPrice.Value - sl.Value);

            if (tp.HasValue && tp.Value > 0)
                tpDiff = Math.Abs(exitPrice.Value - tp.Value);

            // Determine which is closer
            if (slDiff.HasValue && tpDiff.HasValue)
            {
                // Both SL and TP available - pick the closest one
                // Use a reasonable tolerance (0.5% of the price) to consider it a hit
                var tolerance = exitPrice.Value * 0.005m;

                if (tpDiff.Value < slDiff.Value && tpDiff.Value <= tolerance)
                    return "TP Hit";
                if (slDiff.Value < tpDiff.Value && slDiff.Value <= tolerance)
                    return "SL Hit";
                // If they're equal or both within tolerance, check which is closer
                if (tpDiff.Value <= tolerance || slDiff.Value <= tolerance)
                    return tpDiff.Value <= slDiff.Value ? "TP Hit" : "SL Hit";
            }
            else if (slDiff.HasValue)
            {
                var tolerance = sl!.Value * 0.005m;
                if (slDiff.Value <= tolerance)
                    return "SL Hit";
            }
            else if (tpDiff.HasValue)
            {
                var tolerance = tp!.Value * 0.005m;
                if (tpDiff.Value <= tolerance)
                    return "TP Hit";
            }
        }

        // Best-effort: anything else is treated as a manual close.
        return "Closed Manually";
    }

    /// <summary>
    /// Calculate Risk:Reward ratio from entry, SL, and TP at trade close
    /// Format: "1:X" where X is the reward relative to 1 unit of risk
    /// </summary>
    private static string? CalculateRiskReward(decimal? entryPrice, decimal? sl, decimal? tp)
    {
        if (!entryPrice.HasValue || entryPrice.Value <= 0)
            return null;
        if (!sl.HasValue || sl.Value <= 0)
            return null;
        if (!tp.HasValue || tp.Value <= 0)
            return null;

        var entry = entryPrice.Value;
        var stopLoss = sl.Value;
        var takeProfit = tp.Value;

        // Calculate distances
        var riskDistance = Math.Abs(entry - stopLoss);
        var rewardDistance = Math.Abs(entry - takeProfit);

        if (riskDistance <= 0)
            return null;

        // Calculate ratio: reward per unit of risk
        var ratio = rewardDistance / riskDistance;

        // Format as "1:X" (e.g., "1:2", "1:3", "1:0.5")
        if (ratio >= 1)
        {
            return $"1:{ratio:0.#}";
        }
        else
        {
            // If risk > reward, show as "X:1"
            var inverseRatio = riskDistance / rewardDistance;
            return $"{inverseRatio:0.#}:1";
        }
    }

    /// <summary>
    /// Determine trade outcome based on PnL value
    /// </summary>
    private static string DetermineOutcome(decimal? pnl)
    {
        if (!pnl.HasValue)
            return "Unknown";
        if (pnl.Value > 0)
            return "Profit";
        if (pnl.Value < 0)
            return "Loss";
        return "Breakeven";
    }

    /// <summary>
    /// Format the close notification message with proper emoji and format
    /// </summary>
    private static string FormatCloseMessage(string symbol, string direction, decimal? exitPrice, decimal? pnl, string reason, string? rr = null)
    {
        var exitText = exitPrice.HasValue && exitPrice.Value > 0
            ? exitPrice.Value.ToString("0.000", System.Globalization.CultureInfo.InvariantCulture)
            : "?";

        // Determine profit/loss status
        var isProfit = pnl.HasValue && pnl.Value > 0;
        var isLoss = pnl.HasValue && pnl.Value < 0;
        var pnlEmoji = isProfit ? "✅" : (isLoss ? "❌" : "➖");
        var pnlText = isProfit ? "Profit" : (isLoss ? "Loss" : "BE");

        var msg = $"🏁 CLOSED {symbol} {direction} @ {exitText}\n" +
                  $"{pnlEmoji} PnL={pnlText} Reason= {reason}";

        if (!string.IsNullOrWhiteSpace(rr))
            msg += $"\n⚖️ R:R={rr}";

        return msg;
    }

    private static double? TryExtractExecutedPrice(ProtoOAExecutionEvent execEvent)
    {
        try
        {
            // Common: execEvent.Order.ExecutionPrice
            var order = execEvent.GetType().GetProperty("Order")?.GetValue(execEvent);
            var orderPrice = TryExtractDouble(order, new[] { "ExecutionPrice", "ExecutedPrice", "Price" });
            if (orderPrice.HasValue)
                return orderPrice;

            // Fallback: deal price
            var deal = execEvent.GetType().GetProperty("Deal")?.GetValue(execEvent);
            return TryExtractDouble(deal, new[] { "ExecutionPrice", "Price" });
        }
        catch
        {
            return null;
        }
    }

    private static double? TryExtractExitPrice(ProtoOAExecutionEvent execEvent)
    {
        try
        {
            // Try Deal.ExecutionPrice first (most reliable for close events)
            var deal = execEvent.GetType().GetProperty("Deal")?.GetValue(execEvent);
            if (deal != null)
            {
                var dealPrice = TryExtractScaledPrice(deal, new[] { "ExecutionPrice", "Price" });
                if (dealPrice.HasValue && dealPrice.Value > 0)
                    return dealPrice;
            }

            // Try Position properties
            var position = execEvent.GetType().GetProperty("Position")?.GetValue(execEvent);
            if (position != null)
            {
                var posPrice = TryExtractScaledPrice(position, new[] { "Price", "ExecutionPrice" });
                if (posPrice.HasValue && posPrice.Value > 0)
                    return posPrice;
            }

            // Try Order.ExecutionPrice
            var order = execEvent.GetType().GetProperty("Order")?.GetValue(execEvent);
            if (order != null)
            {
                var orderPrice = TryExtractScaledPrice(order, new[] { "ExecutionPrice", "Price" });
                if (orderPrice.HasValue && orderPrice.Value > 0)
                    return orderPrice;
            }

            return null;
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// Extract and scale price from cTrader protobuf (prices are stored as long with 5 decimal places)
    /// </summary>
    private static double? TryExtractScaledPrice(object? obj, string[] propertyNames)
    {
        if (obj == null)
            return null;

        foreach (var name in propertyNames)
        {
            var prop = obj.GetType().GetProperty(name);
            var value = prop?.GetValue(obj);
            if (value == null)
                continue;

            try
            {
                var rawValue = Convert.ToDouble(value);
                // cTrader stores prices as scaled integers (e.g., 2447500000 = 2447.5)
                // For most instruments, divide by 100000 (5 decimal places)
                // For synthetic indices like Volatility 25, may need different scaling
                if (rawValue > 1000000) // Likely a scaled integer
                {
                    return rawValue / 100000.0; // Standard 5 decimal scaling
                }
                return rawValue;
            }
            catch
            {
                // ignore
            }
        }

        return null;
    }

    private static decimal? TryExtractProfit(ProtoOAExecutionEvent execEvent)
    {
        try
        {
            // Try Deal first (most reliable for close events)
            var deal = execEvent.GetType().GetProperty("Deal")?.GetValue(execEvent);
            if (deal != null)
            {
                // Try ClosePositionDetail - look for actual profit fields, NOT Balance
                // Balance is the account balance after the trade, not the P&L
                var closeDetail = deal.GetType().GetProperty("ClosePositionDetail")?.GetValue(deal);
                if (closeDetail != null)
                {
                    // Priority: GrossProfit > Profit > ClosedVolume-based calculation
                    // Explicitly avoid "Balance" as it's the account balance, not trade P&L
                    var profitPnl = TryExtractScaledDecimal(closeDetail, new[] { "GrossProfit", "Profit", "NetProfit" });
                    if (profitPnl.HasValue)
                        return profitPnl;
                }

                // Try direct deal properties
                var dealPnl = TryExtractScaledDecimal(deal, new[] { "Pnl", "PnL", "GrossProfit", "Profit" });
                if (dealPnl.HasValue)
                    return dealPnl;
            }

            var position = execEvent.GetType().GetProperty("Position")?.GetValue(execEvent);
            if (position != null)
            {
                var posPnl = TryExtractScaledDecimal(position, new[] { "Pnl", "PnL", "GrossProfit", "Profit" });
                if (posPnl.HasValue)
                    return posPnl;
            }

            return null;
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// Extract and scale decimal value (PnL values in cTrader are in cents/100)
    /// </summary>
    private static decimal? TryExtractScaledDecimal(object? obj, string[] propertyNames)
    {
        if (obj == null)
            return null;

        foreach (var name in propertyNames)
        {
            var prop = obj.GetType().GetProperty(name);
            var value = prop?.GetValue(obj);
            if (value == null)
                continue;

            try
            {
                var rawValue = Convert.ToDecimal(value);
                // cTrader stores money values in cents (divide by 100)
                if (Math.Abs(rawValue) > 100 && name != "Swap") // Likely in cents
                {
                    return rawValue / 100m;
                }
                return rawValue;
            }
            catch
            {
                // ignore
            }
        }

        return null;
    }

    private static double? TryExtractDouble(object? obj, string[] propertyNames)
    {
        if (obj == null)
            return null;

        foreach (var name in propertyNames)
        {
            var prop = obj.GetType().GetProperty(name);
            var value = prop?.GetValue(obj);
            if (value == null)
                continue;

            try
            {
                return Convert.ToDouble(value);
            }
            catch
            {
                // ignore
            }
        }

        return null;
    }

    private static decimal? TryExtractDecimal(object? obj, string[] propertyNames)
    {
        if (obj == null)
            return null;

        foreach (var name in propertyNames)
        {
            var prop = obj.GetType().GetProperty(name);
            var value = prop?.GetValue(obj);
            if (value == null)
                continue;

            try
            {
                return Convert.ToDecimal(value);
            }
            catch
            {
                // ignore
            }
        }

        return null;
    }

    /// <summary>
    /// Get the count of currently monitored orders
    /// </summary>
    public int GetMonitoredOrderCount()
    {
        lock (_pendingLock)
        {
            return _pendingExecutions.Count;
        }
    }

    private string BuildStrategyName(ParsedSignal signal)
    {
        // Just return the provider name (e.g., "TestChannel")
        return signal.ProviderName ?? "Unknown";
    }

    private async Task EnqueueExecutedTradeAsync(ParsedSignal executedSignal, long positionId, bool isOpposite)
    {
        await EnqueueExecutedTradeAsync(executedSignal, positionId, isOpposite, GetEffectiveDirection(executedSignal.Direction, isOpposite));
    }

    private async Task EnqueueExecutedTradeAsync(ParsedSignal executedSignal, long positionId, bool isOpposite, TradeDirection effectiveDirection)
    {
        _logger.LogInformation("[TRACE] Entered EnqueueExecutedTradeAsync: PositionId={PositionId}, Asset={Asset}, Direction={Direction}, IsOpposite={IsOpposite}",
            positionId, executedSignal.Asset, effectiveDirection, isOpposite);
        var queueItem = new TradeExecutionQueue
        {
            CTraderOrderId = positionId.ToString(),  // Store PositionId (not OrderId)
            Asset = executedSignal.Asset,
            Direction = effectiveDirection.ToString(),
            StrategyName = BuildStrategyName(executedSignal),
            ProviderChannelId = executedSignal.ProviderChannelId,
            IsOpposite = isOpposite,
            CreatedAt = DateTime.UtcNow
        };

        var queueId = await _repository.EnqueueTradeAsync(queueItem);
        _logger.LogInformation(
            "💾 Written to TradeExecutionQueue: QueueId={QueueId}, PositionId={PositionId}, Asset={Asset}, Direction={Direction}, IsOpposite={IsOpposite}",
            queueId,
            positionId,
            executedSignal.Asset,
            effectiveDirection,
            isOpposite);
        _logger.LogInformation("[TRACE] Exiting EnqueueExecutedTradeAsync: QueueId={QueueId}, PositionId={PositionId}, Asset={Asset}, Direction={Direction}, IsOpposite={IsOpposite}",
            queueId, positionId, executedSignal.Asset, effectiveDirection, isOpposite);
    }

    private static TradeDirection GetEffectiveDirection(TradeDirection direction, bool isOpposite)
    {
        if (!isOpposite)
            return direction;

        return direction switch
        {
            TradeDirection.Buy => TradeDirection.Sell,
            TradeDirection.Sell => TradeDirection.Buy,
            TradeDirection.Call => TradeDirection.Put,
            TradeDirection.Put => TradeDirection.Call,
            _ => direction
        };
    }

    private static ParsedSignal CloneSignalWithDirection(ParsedSignal signal, TradeDirection direction)
    {
        return new ParsedSignal
        {
            SignalId = signal.SignalId,
            Asset = signal.Asset,
            Direction = direction,
            EntryPrice = signal.EntryPrice,
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
    }

    private async Task<CTraderOrderType> InferPendingOrderTypeAsync(ParsedSignal signal, TradeDirection effectiveDirection, bool isOpposite)
    {
        // 1) Respect explicit hints in text first.
        var raw = signal.RawMessage ?? string.Empty;

        var rawHasLimitHint = raw.Contains("LIMIT", StringComparison.OrdinalIgnoreCase);
        var rawHasStopHint = raw.Contains("STOP", StringComparison.OrdinalIgnoreCase);

        // Opposite leg reuses the original RawMessage.
        // If the provider explicitly says LIMIT/STOP, the opposite leg should use the complementary pending type
        // to avoid turning into a marketable LIMIT that fills immediately.
        if (isOpposite && rawHasLimitHint != rawHasStopHint)
        {
            return rawHasLimitHint ? CTraderOrderType.Stop : CTraderOrderType.Limit;
        }

        // IMPORTANT: The opposite leg reuses the original RawMessage.
        // Only honor LIMIT/STOP hints if the text direction matches the effective direction.
        var rawSuggestsBuy = raw.Contains("BUY", StringComparison.OrdinalIgnoreCase) || raw.Contains("CALL", StringComparison.OrdinalIgnoreCase);
        var rawSuggestsSell = raw.Contains("SELL", StringComparison.OrdinalIgnoreCase) || raw.Contains("PUT", StringComparison.OrdinalIgnoreCase);

        var hintDirectionMatches = (effectiveDirection == TradeDirection.Buy || effectiveDirection == TradeDirection.Call)
            ? rawSuggestsBuy
            : (effectiveDirection == TradeDirection.Sell || effectiveDirection == TradeDirection.Put)
                ? rawSuggestsSell
                : false;

        if (hintDirectionMatches)
        {
            if (rawHasLimitHint)
                return CTraderOrderType.Limit;

            if (rawHasStopHint)
                return CTraderOrderType.Stop;
        }

        // 2) Otherwise, for non-explicit cases, the order type must be decided by the caller (e.g., via Deriv price probe for synthetics).
        // This method no longer infers order type from cTrader price for synthetics.
        // For legacy non-synthetic assets, fallback to LIMIT if not specified.
        return CTraderOrderType.Limit;
    }

    private static long? TryExtractPositionId(object executionEvent)
    {
        // Common shapes across cTrader/OpenAPI protobuf versions.
        // 1) executionEvent.PositionId
        // 2) executionEvent.Position.PositionId
        // 3) executionEvent.Order.PositionId
        var candidates = new (string Parent, string Child)[]
        {
            ("", "PositionId"),
            ("Position", "PositionId"),
            ("Order", "PositionId")
        };

        foreach (var (parent, child) in candidates)
        {
            object? target = executionEvent;
            if (!string.IsNullOrWhiteSpace(parent))
            {
                var parentProp = executionEvent.GetType().GetProperty(parent);
                target = parentProp?.GetValue(executionEvent);
                if (target == null)
                    continue;
            }

            var prop = target.GetType().GetProperty(child);
            var value = prop?.GetValue(target);
            if (value == null)
                continue;

            try
            {
                var positionId = Convert.ToInt64(value);
                if (positionId > 0)
                    return positionId;
            }
            catch
            {
                // ignore
            }
        }

        return null;
    }

    private static double? SelectTakeProfit(ParsedSignal signal)
    {
        if (signal.TakeProfit.HasValue)
            return (double)signal.TakeProfit.Value;
        if (signal.TakeProfit2.HasValue)
            return (double)signal.TakeProfit2.Value;
        if (signal.TakeProfit3.HasValue)
            return (double)signal.TakeProfit3.Value;
        if (signal.TakeProfit4.HasValue)
            return (double)signal.TakeProfit4.Value;
        return null;
    }

    private sealed class PendingExecutionWatch
    {
        public long OrderId { get; set; }
        public ParsedSignal Signal { get; set; } = null!;
        public TradeDirection EffectiveDirection { get; set; }
        public bool IsOpposite { get; set; }
        public DateTime CreatedAt { get; set; }
    }
}
