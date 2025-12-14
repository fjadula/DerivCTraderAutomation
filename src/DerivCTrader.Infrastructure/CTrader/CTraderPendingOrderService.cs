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
            _logger.LogInformation("[TRACE] Entered HandlePendingExecutionAsync: OrderId={OrderId}, PositionId={PositionId}, Asset={Asset}, Direction={Direction}, IsOpposite={IsOpposite}",
                orderId, positionId, watch.Signal.Asset, watch.EffectiveDirection, watch.IsOpposite);

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
                watch.OrderId, positionId, watch.Signal.Asset, watch.EffectiveDirection, watch.IsOpposite);
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
