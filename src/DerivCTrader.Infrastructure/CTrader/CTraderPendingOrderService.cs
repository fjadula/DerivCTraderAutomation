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
    private readonly ILogger<CTraderPendingOrderService> _logger;
    private readonly ICTraderClient _client;
    private readonly ICTraderOrderManager _orderManager;
    private readonly ICTraderSymbolService _symbolService;
    private readonly ITradeRepository _repository;
    private readonly ITelegramNotifier _telegram;

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
        ITelegramNotifier telegram)
    {
        _logger = logger;
        _client = client;
        _orderManager = orderManager;
        _symbolService = symbolService;
        _repository = repository;
        _telegram = telegram;

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

            // Check if we have this symbol
            if (!_symbolService.HasSymbol(signal.Asset))
            {
                _logger.LogWarning("Unknown symbol: {Asset}", signal.Asset);
                _logger.LogDebug("[DEBUG] Unknown symbol for SignalId={SignalId}: {Asset}", signal.SignalId, signal.Asset);
                return new CTraderOrderResult
                {
                    Success = false,
                    ErrorMessage = $"Unknown symbol: {signal.Asset}"
                };
            }

            // If EntryPrice exists, this is a real pending order (Limit/Stop) and should be placed on cTrader.
            // We enqueue to TradeExecutionQueue only after we see the actual execution event.
            if (signal.EntryPrice.HasValue)
            {
                // --- Marketability check: prevent instantly filled pending orders ---
                var resolvedOrderType = await InferPendingOrderTypeAsync(signal, effectiveDirection, isOpposite);
                var bidAsk = await _orderManager.GetCurrentBidAskAsync(signal.Asset);
                double? marketPrice = null;

                // Retry once if we couldn't get bid/ask - spot subscription might need time
                if (!bidAsk.Bid.HasValue && !bidAsk.Ask.HasValue)
                {
                    _logger.LogDebug("[DEBUG] First bid/ask fetch failed for {Asset}, retrying after 500ms...", signal.Asset);
                    await Task.Delay(TimeSpan.FromMilliseconds(500));
                    bidAsk = await _orderManager.GetCurrentBidAskAsync(signal.Asset);
                }

                if (bidAsk.Bid.HasValue && bidAsk.Ask.HasValue)
                    marketPrice = (bidAsk.Bid.Value + bidAsk.Ask.Value) / 2.0;
                else if (bidAsk.Ask.HasValue)
                    marketPrice = bidAsk.Ask.Value;
                else if (bidAsk.Bid.HasValue)
                    marketPrice = bidAsk.Bid.Value;

                bool isMarketable = false;
                double entryPrice = (double)signal.EntryPrice.Value;

                if (marketPrice.HasValue)
                {
                    // Calculate the minimum distance from market price (use a small buffer to avoid edge cases)
                    // Use a relative buffer of 0.0001 (1 pip for most forex) or absolute buffer if price is very small
                    double minBuffer = Math.Max(marketPrice.Value * 0.0001, 0.0001);

                    if (effectiveDirection == TradeDirection.Buy)
                    {
                        // For Buy Limit, EntryPrice < MarketPrice is valid; EntryPrice >= MarketPrice is marketable
                        if (resolvedOrderType == CTraderOrderType.Limit && entryPrice >= (marketPrice.Value - minBuffer))
                            isMarketable = true;
                        // For Buy Stop, EntryPrice > MarketPrice is valid; EntryPrice <= MarketPrice is marketable
                        if (resolvedOrderType == CTraderOrderType.Stop && entryPrice <= (marketPrice.Value + minBuffer))
                            isMarketable = true;
                    }
                    else if (effectiveDirection == TradeDirection.Sell)
                    {
                        // For Sell Limit, EntryPrice > MarketPrice is valid; EntryPrice <= MarketPrice is marketable
                        if (resolvedOrderType == CTraderOrderType.Limit && entryPrice <= (marketPrice.Value + minBuffer))
                            isMarketable = true;
                        // For Sell Stop, EntryPrice < MarketPrice is valid; EntryPrice >= MarketPrice is marketable
                        if (resolvedOrderType == CTraderOrderType.Stop && entryPrice >= (marketPrice.Value - minBuffer))
                            isMarketable = true;
                    }

                    _logger.LogInformation(
                        "[MARKETABILITY] Asset={Asset}, Direction={Direction}, OrderType={OrderType}, EntryPrice={EntryPrice}, MarketPrice={MarketPrice}, Bid={Bid}, Ask={Ask}, IsMarketable={IsMarketable}",
                        signal.Asset, effectiveDirection, resolvedOrderType, entryPrice, marketPrice.Value, bidAsk.Bid, bidAsk.Ask, isMarketable);
                }
                else
                {
                    // CRITICAL: If we can't get market price, we cannot verify the order won't fill immediately
                    // Skip the order to prevent accidental market fills
                    _logger.LogWarning(
                        "[CTraderPendingOrderService] Cannot verify order marketability - bid/ask unavailable. Skipping order: Asset={Asset}, Direction={Direction}, EntryPrice={EntryPrice}",
                        signal.Asset, effectiveDirection, signal.EntryPrice);
                    return new CTraderOrderResult { Success = false, ErrorMessage = "Cannot place pending order: market price unavailable for marketability check" };
                }

                if (isMarketable)
                {
                    _logger.LogWarning("[CTraderPendingOrderService] Skipping marketable pending order: Asset={Asset}, Direction={Direction}, EntryPrice={EntryPrice}, MarketPrice={MarketPrice}, OrderType={OrderType}", signal.Asset, effectiveDirection, signal.EntryPrice, marketPrice, resolvedOrderType);
                    return new CTraderOrderResult { Success = false, ErrorMessage = "Marketable pending order would be filled instantly." };
                }
                // --- End marketability check ---

                _logger.LogInformation("[DEBUG] About to create order: Asset={Asset} EntryPrice={EntryPrice} OrderType={OrderType} Bid={Bid} Ask={Ask}", signal.Asset, signal.EntryPrice, resolvedOrderType, bidAsk.Bid, bidAsk.Ask);
                var pendingResult = await _orderManager.CreateOrderAsync(signal, resolvedOrderType, isOpposite);

                _logger.LogDebug("[DEBUG] CreateOrderAsync returned: Success={Success}, OrderId={OrderId}, PositionId={PositionId}, SltpApplied={SltpApplied}",
                    pendingResult.Success, pendingResult.OrderId, pendingResult.PositionId, pendingResult.SltpApplied);

                if (!pendingResult.Success || !pendingResult.OrderId.HasValue)
                {
                    _logger.LogError("Failed to place pending order: {Error}", pendingResult.ErrorMessage);
                    _logger.LogDebug("[DEBUG] Failed to place pending order for SignalId={SignalId}: {Error}", signal.SignalId, pendingResult.ErrorMessage);
                    return pendingResult;
                }

                if (signal.SignalId > 0)
                {
                    lock (_placedOrdersLock)
                    {
                        _placedOrdersBySignalLeg[(signal.SignalId, isOpposite)] = pendingResult.OrderId.Value;
                        _logger.LogDebug("[DEBUG] Added to _placedOrdersBySignalLeg: SignalId={SignalId}, IsOpposite={IsOpposite}, OrderId={OrderId}", signal.SignalId, isOpposite, pendingResult.OrderId.Value);
                    }
                }

                // If cTrader filled the order immediately (can happen when entry is already crossable),
                // don't add a watch (the execution event may already have been consumed by the request wait).
                if (pendingResult.PositionId.HasValue && pendingResult.PositionId.Value > 0)
                {
                    _logger.LogInformation(
                        "🎯 Pending order filled immediately: OrderId={OrderId}, PositionId={PositionId}. Enqueuing TradeExecutionQueue now...",
                        pendingResult.OrderId.Value,
                        pendingResult.PositionId.Value);

                    _logger.LogDebug("[DEBUG] Immediate fill: SignalId={SignalId}, OrderId={OrderId}, PositionId={PositionId}", signal.SignalId, pendingResult.OrderId.Value, pendingResult.PositionId.Value);

                    await PersistForexTradeFillAsync(
                        signal,
                        cTraderOrderId: pendingResult.OrderId.Value,
                        positionId: pendingResult.PositionId.Value,
                        effectiveDirection,
                        isOpposite,
                        executedPrice: pendingResult.ExecutedPrice,
                        sltpApplied: pendingResult.SltpApplied);

                    await NotifyFillAsync(
                        signal,
                        cTraderOrderId: pendingResult.OrderId.Value,
                        positionId: pendingResult.PositionId.Value,
                        effectiveDirection,
                        isOpposite,
                        executedPrice: pendingResult.ExecutedPrice,
                        sltpApplied: pendingResult.SltpApplied);

                    await EnqueueExecutedTradeAsync(signal, pendingResult.OrderId.Value, isOpposite, effectiveDirection);

                    // NOTE: Do NOT mark as processed here!
                    // The calling CTraderForexProcessorService will handle marking as processed
                    // AFTER both original and opposite orders have been attempted.
                    // This ensures opposite orders can still be created even if the original fills immediately.
                    _logger.LogDebug("[DEBUG] Immediate fill complete for SignalId={SignalId}, IsOpposite={IsOpposite}. Processed flag will be set by caller.", signal.SignalId, isOpposite);

                    return pendingResult;
                }

                lock (_pendingLock)
                {
                    _pendingExecutions[pendingResult.OrderId.Value] = new PendingExecutionWatch
                    {
                        OrderId = pendingResult.OrderId.Value,
                        Signal = signal,
                        EffectiveDirection = effectiveDirection,
                        IsOpposite = isOpposite,
                        CreatedAt = DateTime.UtcNow
                    };
                    _logger.LogDebug("[DEBUG] Added to _pendingExecutions: OrderId={OrderId}, SignalId={SignalId}, IsOpposite={IsOpposite}", pendingResult.OrderId.Value, signal.SignalId, isOpposite);
                }

                _logger.LogInformation(
                    "✅ Pending order placed on cTrader: OrderId={OrderId}, Asset={Asset}, Direction={Direction}, Type={OrderType}, Entry={Entry}",
                    pendingResult.OrderId.Value,
                    signal.Asset,
                    effectiveDirection,
                    resolvedOrderType,
                    signal.EntryPrice);

                _logger.LogDebug("[DEBUG] Pending order placed: SignalId={SignalId}, OrderId={OrderId}, Type={OrderType}", signal.SignalId, pendingResult.OrderId.Value, resolvedOrderType);

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

    /// <summary>
    /// NOTE: Legacy price-cross -> market execution flow has been removed.
    /// Pending order fills are detected via execution events (ProtoOAExecutionEvent).
    /// </summary>
    // (kept intentionally empty)

    private void OnClientMessageReceived(object? sender, CTraderMessage message)
    {
        try
        {
            if (message.PayloadType != (int)ProtoOAPayloadType.ProtoOaExecutionEvent)
                return;

            var execEvent = ProtoOAExecutionEvent.Parser.ParseFrom(message.Payload);

            var execTypeName = execEvent.ExecutionType.ToString();

            _logger.LogDebug("[DEBUG] OnClientMessageReceived: OrderId={OrderId}, ExecutionType={ExecutionType}", execEvent.Order?.OrderId, execTypeName);

            // Only treat as a fill when cTrader reports the order was filled.
            if (execEvent.ExecutionType != ProtoOAExecutionType.OrderFilled &&
                execEvent.ExecutionType != ProtoOAExecutionType.OrderPartialFill)
            {
                // Still handle position closed events for DB + Telegram.
                if (IsPositionClosedExecution(execTypeName))
                {
                    _ = Task.Run(async () => await HandlePositionClosedAsync(execEvent, execTypeName));
                }

                return;
            }

            var orderId = execEvent.Order?.OrderId;
            if (!orderId.HasValue || orderId.Value <= 0)
                return;

            PendingExecutionWatch? watch;
            lock (_pendingLock)
            {
                if (!_pendingExecutions.TryGetValue(orderId.Value, out watch))
                {
                    _logger.LogWarning("[DEBUG] Fill event received for unknown OrderId={OrderId}. Possible race condition or missed tracking.", orderId.Value);
                    return;
                }

                // Remove first to prevent duplicates on multiple events.
                _pendingExecutions.Remove(orderId.Value);
                _logger.LogDebug("[DEBUG] Removed from _pendingExecutions: OrderId={OrderId}, SignalId={SignalId}, IsOpposite={IsOpposite}", orderId.Value, watch?.Signal?.SignalId, watch?.IsOpposite);
            }

            _ = Task.Run(async () => await HandlePendingExecutionAsync(execEvent, watch));
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
            var orderId = execEvent.Order?.OrderId ?? watch.OrderId;
            var positionId = TryExtractPositionId(execEvent);
            var executedPrice = TryExtractExecutedPrice(execEvent);

            _logger.LogInformation(
                "🎯 Pending order FILLED: OrderId={OrderId}, PositionId={PositionId}, Asset={Asset}, Direction={Direction}",
                orderId,
                positionId,
                watch.Signal.Asset,
                watch.EffectiveDirection);

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

            // Enqueue execution for Deriv.
            await EnqueueExecutedTradeAsync(watch.Signal, orderId, watch.IsOpposite, watch.EffectiveDirection);
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
                Symbol = signal.Asset ?? string.Empty,
                Direction = effectiveDirection.ToString(),
                // Persist the requested entry from the signal as the trade's EntryPrice.
                // The fill/execution price can legitimately differ (e.g., marketable LIMIT fills at better price).
                EntryPrice = signal.EntryPrice ?? (executedPrice.HasValue ? (decimal?)Convert.ToDecimal(executedPrice.Value) : null),
                EntryTime = DateTime.UtcNow,
                Status = "OPEN",
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
        // 2025-12-12 17:47 UTC
        // GBPUSD Sell  1.34750
        // TP: 1.30636
        // SL: 1.36750
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
            $"{utcNow:yyyy-MM-dd HH:mm} UTC\n" +
            $"{signal.Asset} {effectiveDirection}  {F(entry)}\n" +
            $"TP: {F(tpDecimal)}\n" +
            $"SL: {F(sl)}";

        await _telegram.SendTradeMessageAsync(msg);
    }

    private async Task HandlePositionClosedAsync(ProtoOAExecutionEvent execEvent, string execTypeName)
    {
        try
        {
            var positionId = TryExtractPositionId(execEvent);
            if (!positionId.HasValue || positionId.Value <= 0)
                return;

            int? tradeId = null;
            lock (_positionLock)
            {
                if (_positionToForexTradeId.TryGetValue(positionId.Value, out var id))
                    tradeId = id;
            }

            ForexTrade? trade = null;
            if (tradeId.HasValue)
            {
                trade = await _repository.GetForexTradeByIdAsync(tradeId.Value);
            }
            else
            {
                trade = await _repository.FindLatestForexTradeByCTraderPositionIdAsync(positionId.Value);
            }

            if (trade == null)
                return;

            var exitPrice = TryExtractExitPrice(execEvent);
            var pnl = TryExtractProfit(execEvent);

            trade.ExitTime = DateTime.UtcNow;
            trade.ExitPrice = exitPrice.HasValue ? (decimal?)Convert.ToDecimal(exitPrice.Value) : trade.ExitPrice;
            trade.PnL = pnl.HasValue ? (decimal?)pnl.Value : trade.PnL;
            trade.Status = "CLOSED";
            trade.Notes = AppendCloseInfo(trade.Notes, execTypeName);

            await _repository.UpdateForexTradeAsync(trade);

            var reason = InferCloseReason(execTypeName, trade.Notes);
            var pnlText = trade.PnL.HasValue ? trade.PnL.Value.ToString("+0.##;-0.##;0") : "?";
            var exitText = trade.ExitPrice.HasValue ? trade.ExitPrice.Value.ToString() : "?";

            var msg =
                $"🏁 CLOSED {trade.Symbol} {trade.Direction} @ {exitText}\n" +
                $"PositionId={positionId.Value} PnL={pnlText} Reason={reason}";

            await _telegram.SendTradeMessageAsync(msg);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to handle position closed event");
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

    private static string InferCloseReason(string execTypeName, string? notes)
    {
        if (execTypeName.Contains("TakeProfit", StringComparison.OrdinalIgnoreCase) ||
            (notes?.Contains("TakeProfit", StringComparison.OrdinalIgnoreCase) ?? false))
            return "TP";

        if (execTypeName.Contains("StopLoss", StringComparison.OrdinalIgnoreCase) ||
            (notes?.Contains("StopLoss", StringComparison.OrdinalIgnoreCase) ?? false))
            return "SL";

        // Best-effort: anything else is treated as an early/manual close.
        return "EARLY";
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
            var position = execEvent.GetType().GetProperty("Position")?.GetValue(execEvent);
            var p = TryExtractDouble(position, new[] { "ClosePrice", "ExecutionPrice", "Price" });
            if (p.HasValue)
                return p;

            var deal = execEvent.GetType().GetProperty("Deal")?.GetValue(execEvent);
            return TryExtractDouble(deal, new[] { "ExecutionPrice", "Price" });
        }
        catch
        {
            return null;
        }
    }

    private static decimal? TryExtractProfit(ProtoOAExecutionEvent execEvent)
    {
        try
        {
            var deal = execEvent.GetType().GetProperty("Deal")?.GetValue(execEvent);
            var d = TryExtractDecimal(deal, new[] { "Profit", "GrossProfit", "Pnl", "PnL" });
            if (d.HasValue)
                return d;

            var position = execEvent.GetType().GetProperty("Position")?.GetValue(execEvent);
            return TryExtractDecimal(position, new[] { "Profit", "GrossProfit", "Pnl", "PnL" });
        }
        catch
        {
            return null;
        }
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
        // Format: ProviderName_Asset_Timestamp
        return $"{signal.ProviderName}_{signal.Asset}_{DateTime.UtcNow:yyyyMMddHHmmss}";
    }

    private async Task EnqueueExecutedTradeAsync(ParsedSignal executedSignal, long cTraderOrderId, bool isOpposite)
    {
        await EnqueueExecutedTradeAsync(executedSignal, cTraderOrderId, isOpposite, GetEffectiveDirection(executedSignal.Direction, isOpposite));
    }

    private async Task EnqueueExecutedTradeAsync(ParsedSignal executedSignal, long cTraderOrderId, bool isOpposite, TradeDirection effectiveDirection)
    {
        var queueItem = new TradeExecutionQueue
        {
            CTraderOrderId = cTraderOrderId.ToString(),
            Asset = executedSignal.Asset,
            Direction = effectiveDirection.ToString(),
            StrategyName = BuildStrategyName(executedSignal),
            ProviderChannelId = executedSignal.ProviderChannelId,
            IsOpposite = isOpposite,
            CreatedAt = DateTime.UtcNow
        };

        var queueId = await _repository.EnqueueTradeAsync(queueItem);
        _logger.LogInformation(
            "💾 Written to TradeExecutionQueue: QueueId={QueueId}, OrderId={OrderId}, Asset={Asset}, Direction={Direction}, IsOpposite={IsOpposite}",
            queueId,
            cTraderOrderId,
            executedSignal.Asset,
            effectiveDirection,
            isOpposite);
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

        // 2) Otherwise infer from current price vs entry.
        // This prevents placing a marketable LIMIT (which fills immediately and looks like a market execution).
        if (!signal.EntryPrice.HasValue || string.IsNullOrWhiteSpace(signal.Asset))
            return CTraderOrderType.Limit;

        var (bid, ask) = await _orderManager.GetCurrentBidAskAsync(signal.Asset);
        if (isOpposite && !bid.HasValue && !ask.HasValue)
        {
            // Best-effort retry: ticks/subscription can lag right after startup.
            await Task.Delay(TimeSpan.FromMilliseconds(500));
            (bid, ask) = await _orderManager.GetCurrentBidAskAsync(signal.Asset);
        }

        // Use the relevant side of the market for marketability checks.
        // BUY uses ASK (fills if ask <= limit), SELL uses BID (fills if bid >= limit).
        var current = (effectiveDirection == TradeDirection.Buy || effectiveDirection == TradeDirection.Call)
            ? ask
            : bid;

        // Fallback to mid if we couldn't get bid/ask.
        current ??= await _orderManager.GetCurrentPriceAsync(signal.Asset);

        if (!current.HasValue || current.Value <= 0)
        {
            // Fallback: treat the requested EntryPrice as the current spot price so the order
            // type is determined consistently with user's intent (i.e., BUY with Entry => LIMIT).
            current = (double)signal.EntryPrice.Value;
            _logger.LogInformation(
                "InferPendingOrderType: spot price unavailable; using EntryPrice as spot fallback. Asset={Asset} Dir={Dir} Entry={Entry} Bid={Bid} Ask={Ask} IsOpposite={IsOpposite}",
                signal.Asset,
                effectiveDirection,
                signal.EntryPrice,
                bid,
                ask,
                isOpposite);
        }

        var entry = (double)signal.EntryPrice.Value;

        if (isOpposite)
        {
            _logger.LogInformation(
                "InferPendingOrderType: Asset={Asset} Dir={Dir} Entry={Entry} Bid={Bid} Ask={Ask} Current={Current} (IsOpposite={IsOpposite})",
                signal.Asset,
                effectiveDirection,
                entry,
                bid,
                ask,
                current.Value,
                isOpposite);
        }

        // BUY: entry below/at market => LIMIT; above market => STOP
        if (effectiveDirection == TradeDirection.Buy || effectiveDirection == TradeDirection.Call)
            return entry <= current.Value ? CTraderOrderType.Limit : CTraderOrderType.Stop;

        // SELL: entry above/at market => LIMIT; below market => STOP
        if (effectiveDirection == TradeDirection.Sell || effectiveDirection == TradeDirection.Put)
            return entry >= current.Value ? CTraderOrderType.Limit : CTraderOrderType.Stop;

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
