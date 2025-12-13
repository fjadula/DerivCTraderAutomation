using System.Globalization;
using System.Net.WebSockets;
using DerivCTrader.Domain.Entities;
using DerivCTrader.Domain.Enums;
using DerivCTrader.Infrastructure.CTrader.Interfaces;
using DerivCTrader.Infrastructure.CTrader.Models;
using Google.Protobuf;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using OpenAPI.Net;
using OpenAPI.Net.Helpers;

using PayloadType = DerivCTrader.Infrastructure.CTrader.Models.ProtoOAPayloadType;

namespace DerivCTrader.Infrastructure.CTrader;

/// <summary>
/// Manages order placement and management for cTrader
/// </summary>
public class CTraderOrderManager : ICTraderOrderManager
{
    private readonly ILogger<CTraderOrderManager> _logger;
    private readonly ICTraderClient _client;
    private readonly ICTraderSymbolService _symbolService;
    private readonly IConfiguration _configuration;
    private readonly double _defaultLotSize;

    private readonly HashSet<long> _spotSubscribedSymbolIds = new();
    private readonly SemaphoreSlim _spotSubscribeLock = new(1, 1);

    public CTraderOrderManager(
        ILogger<CTraderOrderManager> logger,
        ICTraderClient client,
        ICTraderSymbolService symbolService,
        IConfiguration configuration)
    {
        _logger = logger;
        _client = client;
        _symbolService = symbolService;
        _configuration = configuration;
        
        // 🔧 FIX: Use InvariantCulture for decimal parsing
        var lotSizeString = configuration.GetSection("CTrader")["DefaultLotSize"] ?? "0.2";
        _defaultLotSize = double.Parse(lotSizeString, CultureInfo.InvariantCulture);
    }

    public async Task<CTraderOrderResult> CreateOrderAsync(ParsedSignal signal, CTraderOrderType orderType, bool isOpposite = false)
    {
        try
        {
            if (!_client.IsConnected || !_client.IsAccountAuthenticated)
            {
                return new CTraderOrderResult
                {
                    Success = false,
                    ErrorMessage = "Client not connected or authenticated"
                };
            }

            // Convert domain direction to cTrader protobuf enum.
            // Domain uses Buy/Sell for forex and Call/Put for binary; map both correctly.
            var tradeSide = isOpposite
                ? InvertTradeSide(MapTradeSide(signal.Direction))
                : MapTradeSide(signal.Direction);

            // Convert requested order type to cTrader protobuf enum
            var protoOrderType = orderType switch
            {
                CTraderOrderType.Market => ProtoOAOrderType.Market,
                CTraderOrderType.Limit => ProtoOAOrderType.Limit,
                CTraderOrderType.Stop => ProtoOAOrderType.Stop,
                _ => ProtoOAOrderType.Market
            };

            var symbolId = GetSymbolId(signal.Asset);
            await _symbolService.EnsureSymbolVolumeConstraintsAsync(symbolId);
            var finalVolume = CalculateVolume(symbolId, signal);

            // Use official Spotware.OpenAPI.Net protobuf-generated class
            var orderReq = new ProtoOANewOrderReq
            {
                CtidTraderAccountId = GetAccountId(),
                SymbolId = symbolId,
                OrderType = protoOrderType,  // ✅ Proper protobuf enum (official package)
                TradeSide = tradeSide,        // ✅ Proper protobuf enum (official package)
                Volume = finalVolume
            };

            // Set StopLoss and TakeProfit for LIMIT/STOP orders at order creation (cTrader allows these for pending orders)
            bool sltpSetAtCreation = false;
            if (orderType != CTraderOrderType.Market)
            {
                if (signal.StopLoss.HasValue)
                {
                    orderReq.StopLoss = (double)signal.StopLoss.Value;
                    sltpSetAtCreation = true;
                }
                decimal? takeProfitToSet = null;
                if (signal.TakeProfit.HasValue)
                {
                    takeProfitToSet = signal.TakeProfit.Value;
                    orderReq.TakeProfit = (double)takeProfitToSet;
                    sltpSetAtCreation = true;
                }
                else if (signal.TakeProfit2.HasValue)
                {
                    takeProfitToSet = signal.TakeProfit2.Value;
                    orderReq.TakeProfit = (double)takeProfitToSet;
                    sltpSetAtCreation = true;
                }
                else if (signal.TakeProfit3.HasValue)
                {
                    takeProfitToSet = signal.TakeProfit3.Value;
                    orderReq.TakeProfit = (double)takeProfitToSet;
                    sltpSetAtCreation = true;
                }
                else if (signal.TakeProfit4.HasValue)
                {
                    takeProfitToSet = signal.TakeProfit4.Value;
                    orderReq.TakeProfit = (double)takeProfitToSet;
                    sltpSetAtCreation = true;
                }

                if (sltpSetAtCreation)
                {
                    _logger.LogInformation(
                        "📝 Creating pending order with SL/TP: SL={SL}, TP={TP} (raw TP={RawTP})",
                        signal.StopLoss.HasValue ? (double?)signal.StopLoss.Value : null,
                        signal.TakeProfit.HasValue ? (double?)signal.TakeProfit.Value : null,
                        takeProfitToSet.HasValue ? (double?)takeProfitToSet.Value : null);
                }
            }

            _logger.LogInformation(
                "📝 Creating order: AccountId={AccountId}, TradeSide={TradeSide}, OrderType={OrderType}, DefaultLotSize={DefaultLotSize}, Volume={Volume}, FinalVolume={FinalVolume}",
                orderReq.CtidTraderAccountId,
                tradeSide,
                protoOrderType,
                _defaultLotSize,
                orderReq.Volume,
                finalVolume);
            
            // LIMIT/STOP orders require entry price
            if (orderType != CTraderOrderType.Market)
            {
                if (!signal.EntryPrice.HasValue)
                {
                    return new CTraderOrderResult
                    {
                        Success = false,
                        ErrorMessage = "EntryPrice is required for LIMIT/STOP orders"
                    };
                }

                if (orderType == CTraderOrderType.Limit)
                {
                    orderReq.LimitPrice = (double)signal.EntryPrice.Value;
                    _logger.LogInformation(
                        "📝 Setting LIMIT price: Asset={Asset}, LimitPrice={LimitPrice}, SignalEntryPrice={SignalEntryPrice}",
                        signal.Asset, orderReq.LimitPrice, signal.EntryPrice.Value);
                }

                if (orderType == CTraderOrderType.Stop)
                {
                    orderReq.StopPrice = (double)signal.EntryPrice.Value;
                    _logger.LogInformation(
                        "📝 Setting STOP price: Asset={Asset}, StopPrice={StopPrice}, SignalEntryPrice={SignalEntryPrice}",
                        signal.Asset, orderReq.StopPrice, signal.EntryPrice.Value);
                }
            }

            // NOTE: For MARKET orders, cTrader rejects absolute SL/TP values in the initial request.
            // We'll apply SL/TP after execution (position modification) once we have the PositionId/OrderId.

            await _client.SendMessageAsync(orderReq, PayloadType.ProtoOaNewOrderReq);

            // MARKET orders: wait for execution event.
            // LIMIT/STOP: some Open API/protobuf versions do not send a dedicated NewOrderRes;
            // instead, execution events are emitted (e.g., OrderAccepted and later OrderFilled).
            if (orderType != CTraderOrderType.Market)
            {
                var (response, errorPayload) = await WaitForOrderOutcomeAsync(
                    expectedSymbolId: orderReq.SymbolId,
                    expectedTradeSide: tradeSide,
                    expectedOrderType: protoOrderType,
                    timeout: TimeSpan.FromSeconds(10));

                if (errorPayload != null)
                {
                    // If the broker rejects the volume, adapt once based on the reported max allowed volume.
                    if (TryParseErrorPayload(errorPayload, out var errCode, out var errDesc, out _)
                        && string.Equals(errCode, "TRADING_BAD_VOLUME", StringComparison.OrdinalIgnoreCase)
                        && !string.IsNullOrWhiteSpace(errDesc)
                        && TryParseMaxAllowedVolume(errDesc, out var maxAllowed)
                        && maxAllowed > 0)
                    {
                        // Volume is reported with 2 decimals (e.g., 330.00) while wire uses x100.
                        var maxWire = (long)Math.Floor(maxAllowed * 100d);
                        if (maxWire > 0 && maxWire < orderReq.Volume)
                        {
                            _logger.LogWarning(
                                "TRADING_BAD_VOLUME: retrying order with reduced volume. RequestedWire={RequestedWire}, MaxAllowed={MaxAllowed}, MaxWire={MaxWire}",
                                orderReq.Volume, maxAllowed, maxWire);

                            orderReq.Volume = maxWire;
                            await _client.SendMessageAsync(orderReq, PayloadType.ProtoOaNewOrderReq);

                            var (retryResponse, retryErrorPayload) = await WaitForOrderOutcomeAsync(
                                expectedSymbolId: orderReq.SymbolId,
                                expectedTradeSide: tradeSide,
                                expectedOrderType: protoOrderType,
                                timeout: TimeSpan.FromSeconds(10));

                            if (retryErrorPayload != null)
                            {
                                var retryMsg = FormatErrorPayload(retryErrorPayload);
                                _logger.LogError("❌ Order rejected by cTrader (after volume retry): {Message}", retryMsg);
                                return new CTraderOrderResult { Success = false, ErrorMessage = retryMsg };
                            }

                            response = retryResponse;
                        }
                        else
                        {
                            var msg = FormatErrorPayload(errorPayload);
                            _logger.LogError("❌ Order rejected by cTrader: {Message}", msg);
                            return new CTraderOrderResult { Success = false, ErrorMessage = msg };
                        }
                    }
                    else
                    {
                        var msg = FormatErrorPayload(errorPayload);
                        _logger.LogError("❌ Order rejected by cTrader: {Message}", msg);
                        return new CTraderOrderResult { Success = false, ErrorMessage = msg };
                    }
                }

                if (response == null)
                {
                    return new CTraderOrderResult
                    {
                        Success = false,
                        ErrorMessage = "No execution event received for order placement (timeout)"
                    };
                }

                if (response?.Order == null || response.Order.OrderId <= 0)
                {
                    return new CTraderOrderResult
                    {
                        Success = false,
                        ErrorMessage = "No execution event received for order placement (or OrderId missing)"
                    };
                }

                var orderId = response.Order.OrderId;

                // For pending orders, the first event is often OrderAccepted. Only treat as filled when OrderFilled/OrderPartialFill.
                var isFilledEvent = response.ExecutionType == ProtoOAExecutionType.OrderFilled ||
                                   response.ExecutionType == ProtoOAExecutionType.OrderPartialFill;

                // Extract PositionId only if we have a fill event.
                long? positionId = null;
                if (isFilledEvent && TryExtractPositionId(response, out var extractedPositionId))
                {
                    positionId = extractedPositionId;
                }

                // Safety: ensure the execution event's symbol matches the signal asset.
                var expectedAsset = (signal.Asset ?? string.Empty).Replace("/", string.Empty).Replace(" ", string.Empty).ToUpperInvariant();
                if (TryExtractSymbolId(response, out var executedSymbolId))
                {
                    if (_symbolService.TryGetSymbolName(executedSymbolId, out var executedSymbolName))
                    {
                        var executedAsset = executedSymbolName.Replace("/", string.Empty).Replace(" ", string.Empty).ToUpperInvariant();
                        if (!string.Equals(expectedAsset, executedAsset, StringComparison.Ordinal))
                        {
                            var mismatch = $"Executed symbol mismatch: expected {expectedAsset} but got {executedSymbolName} (SymbolId={executedSymbolId}, OrderId={orderId})";
                            _logger.LogCritical("❌ {Message}", mismatch);
                            return new CTraderOrderResult
                            {
                                Success = false,
                                ErrorMessage = mismatch
                            };
                        }
                    }
                    else
                    {
                        _logger.LogWarning("Could not resolve executed SymbolId={SymbolId} to a symbol name for sanity check", executedSymbolId);
                    }
                }
                else
                {
                    _logger.LogWarning("Could not extract SymbolId from execution event for sanity check (OrderId={OrderId})", orderId);
                }

                // If the order filled immediately and SL/TP were NOT set at creation, apply them post-fill.
                // If SL/TP were already set at creation (sltpSetAtCreation=true), skip redundant modification.
                bool? sltpApplied = null;

                // ALWAYS set sltpApplied if SL/TP were set at creation (whether filled or just accepted)
                if (sltpSetAtCreation)
                {
                    // SL/TP were already set at pending order creation, no post-fill modification needed
                    sltpApplied = true;
                    _logger.LogInformation(
                        "✅ SL/TP already set at order creation: OrderId={OrderId}, SL={SL}, TP={TP}",
                        orderId,
                        orderReq.StopLoss,
                        orderReq.TakeProfit);
                }
                else if (!sltpSetAtCreation && isFilledEvent && positionId.HasValue && positionId.Value > 0)
                {
                    // If order filled immediately but SL/TP were NOT set at creation, try to apply them now
                    var stopLoss = signal.StopLoss.HasValue ? (double?)signal.StopLoss.Value : null;
                    var takeProfit = (double?)null;
                    if (signal.TakeProfit.HasValue)
                        takeProfit = (double)signal.TakeProfit.Value;
                    else if (signal.TakeProfit2.HasValue)
                        takeProfit = (double)signal.TakeProfit2.Value;
                    else if (signal.TakeProfit3.HasValue)
                        takeProfit = (double)signal.TakeProfit3.Value;
                    else if (signal.TakeProfit4.HasValue)
                        takeProfit = (double)signal.TakeProfit4.Value;

                    var sltpRequested = stopLoss.HasValue || takeProfit.HasValue;
                    if (sltpRequested)
                    {
                        var amended = await ModifyPositionAsync(positionId.Value, stopLoss, takeProfit);
                        sltpApplied = amended;

                        if (amended)
                        {
                            _logger.LogInformation(
                                "✅ SL/TP applied post-fill for pending order: PositionId={PositionId}, SL={SL}, TP={TP}",
                                positionId.Value,
                                stopLoss,
                                takeProfit);
                        }
                        else
                        {
                            _logger.LogWarning(
                                "⚠️ Failed to apply SL/TP post-fill: PositionId={PositionId}, SL={SL}, TP={TP}",
                                positionId.Value,
                                stopLoss,
                                takeProfit);
                        }
                    }
                }

                _logger.LogInformation(
                    isFilledEvent && positionId.HasValue
                        ? "✅ Pending order filled immediately: OrderId={OrderId}, PositionId={PositionId}, Type={OrderType}, SL/TP={SltpStatus}"
                        : "✅ Pending order accepted on cTrader: OrderId={OrderId}, Type={OrderType}, ExecutionType={ExecutionType}, SL/TP_SetAtCreation={SltpSetAtCreation}",
                    orderId,
                    positionId,
                    orderType,
                    isFilledEvent ? (sltpApplied.HasValue ? (sltpApplied.Value ? "Applied" : "Failed") : "NotRequested") : null,
                    sltpSetAtCreation);

                return new CTraderOrderResult
                {
                    Success = true,
                    OrderId = orderId,
                    PositionId = positionId,
                    SltpApplied = sltpApplied,
                    ExecutedPrice = response.Order.ExecutionPrice,
                    ExecutedVolume = response.Order.ExecutedVolume,
                    ExecutedAt = isFilledEvent ? DateTime.UtcNow : default
                };
            }

            _logger.LogInformation("📤 Market order sent for {Asset}: {TradeSide} at market price",
                signal.Asset, tradeSide);

            // Market orders should produce either an execution event or an error response quickly.
            {
                var (response, errorPayload) = await WaitForOrderOutcomeAsync(
                    expectedSymbolId: orderReq.SymbolId,
                    expectedTradeSide: tradeSide,
                    expectedOrderType: protoOrderType,
                    timeout: TimeSpan.FromSeconds(10));

                if (errorPayload != null)
                {
                    var msg = FormatErrorPayload(errorPayload);
                    _logger.LogError("❌ Order rejected by cTrader: {Message}", msg);
                    return new CTraderOrderResult { Success = false, ErrorMessage = msg };
                }

                if (response == null)
                {
                    _logger.LogWarning("⚠️ Market order sent but no execution response received (timeout)");
                    return new CTraderOrderResult { Success = false, ErrorMessage = "No execution response received" };
                }

                if (response != null && response.Order != null)
                {
                    var orderId = response.Order.OrderId;

                // Extract PositionId (required for post-execution SL/TP modifications).
                long? positionId = null;
                if (TryExtractPositionId(response, out var extractedPositionId))
                {
                    positionId = extractedPositionId;
                }

                // Safety: ensure the execution event's symbol matches the signal asset.
                var expectedAsset = (signal.Asset ?? string.Empty).Replace("/", string.Empty).Replace(" ", string.Empty).ToUpperInvariant();
                if (TryExtractSymbolId(response, out var executedSymbolId))
                {
                    if (_symbolService.TryGetSymbolName(executedSymbolId, out var executedSymbolName))
                    {
                        var executedAsset = executedSymbolName.Replace("/", string.Empty).Replace(" ", string.Empty).ToUpperInvariant();
                        if (!string.Equals(expectedAsset, executedAsset, StringComparison.Ordinal))
                        {
                            var mismatch = $"Executed symbol mismatch: expected {expectedAsset} but got {executedSymbolName} (SymbolId={executedSymbolId}, OrderId={orderId})";
                            _logger.LogCritical("❌ {Message}", mismatch);
                            return new CTraderOrderResult
                            {
                                Success = false,
                                ErrorMessage = mismatch
                            };
                        }
                    }
                    else
                    {
                        _logger.LogWarning("Could not resolve executed SymbolId={SymbolId} to a symbol name for sanity check", executedSymbolId);
                    }
                }
                else
                {
                    _logger.LogWarning("Could not extract SymbolId from execution event for sanity check (OrderId={OrderId})", orderId);
                }

                _logger.LogInformation("✅ Market order executed: OrderId={OrderId}, ExecutionPrice={Price}",
                    orderId, response.Order.ExecutionPrice);

                // Apply SL/TP immediately after execution for MARKET orders (cTrader rejects absolute SL/TP on initial MARKET request).
                var stopLoss = signal.StopLoss.HasValue ? (double?)signal.StopLoss.Value : null;
                var takeProfit = (double?)null;
                if (signal.TakeProfit.HasValue)
                    takeProfit = (double)signal.TakeProfit.Value;
                else if (signal.TakeProfit2.HasValue)
                    takeProfit = (double)signal.TakeProfit2.Value;
                else if (signal.TakeProfit3.HasValue)
                    takeProfit = (double)signal.TakeProfit3.Value;
                else if (signal.TakeProfit4.HasValue)
                    takeProfit = (double)signal.TakeProfit4.Value;

                var sltpRequested = stopLoss.HasValue || takeProfit.HasValue;
                bool? sltpApplied = null;

                if (sltpRequested && positionId.HasValue && positionId.Value > 0)
                {
                    _logger.LogInformation(
                        "📝 Applying SL/TP to market order position: PositionId={PositionId}, SL={SL}, TP={TP}",
                        positionId.Value,
                        stopLoss,
                        takeProfit);

                    var amended = await ModifyPositionAsync(positionId.Value, stopLoss, takeProfit);
                    sltpApplied = amended;

                    if (amended)
                    {
                        _logger.LogInformation(
                            "✅ SL/TP applied to market order: PositionId={PositionId}, SL={SL}, TP={TP}",
                            positionId.Value,
                            stopLoss,
                            takeProfit);
                    }
                    else
                    {
                        _logger.LogWarning(
                            "⚠️ Market order executed but failed to amend SL/TP: PositionId={PositionId}, SL={SL}, TP={TP}",
                            positionId.Value,
                            stopLoss,
                            takeProfit);
                    }
                }
                else if (sltpRequested)
                {
                    sltpApplied = false;
                    _logger.LogWarning(
                        "⚠️ Market order executed but PositionId unavailable; cannot apply SL/TP: OrderId={OrderId}, SL={SL}, TP={TP}",
                        orderId,
                        stopLoss,
                        takeProfit);
                }

                    return new CTraderOrderResult
                    {
                        Success = true,
                        OrderId = orderId,
                        PositionId = positionId,
                        SltpApplied = sltpApplied,
                        ExecutedPrice = response.Order.ExecutionPrice,
                        ExecutedVolume = response.Order.ExecutedVolume,
                        ExecutedAt = DateTime.UtcNow
                    };
                }

                return new CTraderOrderResult
                {
                    Success = false,
                    ErrorMessage = "No execution response received"
                };
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to create order for {Asset}", signal.Asset);
            return new CTraderOrderResult
            {
                Success = false,
                ErrorMessage = ex.Message
            };
        }
    }

    private async Task<(ProtoOAExecutionEvent? ExecutionEvent, byte[]? ErrorPayload)> WaitForOrderOutcomeAsync(
        long expectedSymbolId,
        ProtoOATradeSide expectedTradeSide,
        ProtoOAOrderType expectedOrderType,
        TimeSpan timeout)
    {
        var execTcs = new TaskCompletionSource<ProtoOAExecutionEvent>(TaskCreationOptions.RunContinuationsAsynchronously);
        var errTcs = new TaskCompletionSource<byte[]>(TaskCreationOptions.RunContinuationsAsynchronously);

        void Handler(object? _, DerivCTrader.Infrastructure.CTrader.Models.CTraderMessage message)
        {
            try
            {
                // Prefer matching execution events to avoid mixing ORIGINAL and OPPOSITE orders.
                if (message.PayloadType == (int)PayloadType.ProtoOaExecutionEvent)
                {
                    var ev = ProtoOAExecutionEvent.Parser.ParseFrom(message.Payload);
                    if (ev == null)
                        return;

                    if (!TryExtractSymbolId(ev, out var symbolId) || symbolId != expectedSymbolId)
                        return;

                    if (TryExtractTradeSide(ev, out var tradeSide) && tradeSide != expectedTradeSide)
                        return;

                    if (TryExtractOrderType(ev, out var orderType) && orderType != expectedOrderType)
                        return;

                    execTcs.TrySetResult(ev);
                    return;
                }

                // Errors are not always easily attributable; accept the first immediate error.
                if (message.PayloadType == 2132 || message.PayloadType == (int)PayloadType.ProtoOaErrorRes)
                {
                    errTcs.TrySetResult(message.Payload);
                }
            }
            catch
            {
                // Best-effort matching only
            }
        }

        _client.MessageReceived += Handler;
        try
        {
            using var cts = new CancellationTokenSource(timeout);
            var delayTask = Task.Delay(timeout, cts.Token);
            var completed = await Task.WhenAny(execTcs.Task, errTcs.Task, delayTask);

            if (completed == execTcs.Task)
            {
                cts.Cancel();
                return (await execTcs.Task, null);
            }

            if (completed == errTcs.Task)
            {
                cts.Cancel();
                return (null, await errTcs.Task);
            }

            return (null, null);
        }
        finally
        {
            _client.MessageReceived -= Handler;
        }
    }

    private static bool TryExtractTradeSide(object executionEvent, out ProtoOATradeSide tradeSide)
    {
        tradeSide = default;
        if (TryExtractEnum(executionEvent, "TradeSide", out tradeSide))
            return true;

        if (TryExtractNestedEnum(executionEvent, "Order", "TradeSide", out tradeSide))
            return true;

        if (TryExtractNestedEnum(executionEvent, "TradeData", "TradeSide", out tradeSide))
            return true;

        // Spotware schema: executionEvent.Order.TradeData.TradeSide
        if (TryExtractNestedEnumPath(executionEvent, out tradeSide, "Order", "TradeData", "TradeSide"))
            return true;

        return false;
    }

    private static bool TryExtractOrderType(object executionEvent, out ProtoOAOrderType orderType)
    {
        orderType = default;
        if (TryExtractEnum(executionEvent, "OrderType", out orderType))
            return true;

        if (TryExtractNestedEnum(executionEvent, "Order", "OrderType", out orderType))
            return true;

        if (TryExtractNestedEnum(executionEvent, "TradeData", "OrderType", out orderType))
            return true;

        // Spotware schema: executionEvent.Order.TradeData.OrderType (not always present)
        if (TryExtractNestedEnumPath(executionEvent, out orderType, "Order", "TradeData", "OrderType"))
            return true;

        return false;
    }

    private static bool TryExtractEnum<TEnum>(object target, string propertyName, out TEnum value) where TEnum : struct
    {
        value = default;
        var prop = target.GetType().GetProperty(propertyName);
        var raw = prop?.GetValue(target);
        if (raw == null)
            return false;

        try
        {
            var numeric = Convert.ToInt32(raw);
            value = (TEnum)Enum.ToObject(typeof(TEnum), numeric);
            return true;
        }
        catch
        {
            return false;
        }
    }

    private static bool TryExtractNestedEnum<TEnum>(object target, string parentPropertyName, string propertyName, out TEnum value) where TEnum : struct
    {
        value = default;
        var parentProp = target.GetType().GetProperty(parentPropertyName);
        var parent = parentProp?.GetValue(target);
        if (parent == null)
            return false;

        return TryExtractEnum(parent, propertyName, out value);
    }

    private static bool TryExtractNestedEnumPath<TEnum>(object target, out TEnum value, params string[] path) where TEnum : struct
    {
        value = default;
        if (path.Length == 0)
            return false;

        object? current = target;
        for (var i = 0; i < path.Length - 1; i++)
        {
            if (current == null)
                return false;

            var prop = current.GetType().GetProperty(path[i]);
            current = prop?.GetValue(current);
        }

        if (current == null)
            return false;

        return TryExtractEnum(current, path[^1], out value);
    }

    private static bool TryExtractInt64Path(object target, out long value, params string[] path)
    {
        value = 0;
        if (path.Length == 0)
            return false;

        object? current = target;
        for (var i = 0; i < path.Length - 1; i++)
        {
            if (current == null)
                return false;

            var prop = current.GetType().GetProperty(path[i]);
            current = prop?.GetValue(current);
        }

        if (current == null)
            return false;

        var lastProp = current.GetType().GetProperty(path[^1]);
        var raw = lastProp?.GetValue(current);
        if (raw == null)
            return false;

        try
        {
            value = Convert.ToInt64(raw);
            return value != 0;
        }
        catch
        {
            return false;
        }
    }

    public async Task<bool> CancelOrderAsync(long orderId)
    {
        try
        {
            _logger.LogInformation("Canceling order {OrderId}", orderId);
            // TODO: Implement ProtoOACancelOrderReq
            await Task.CompletedTask;
            return true;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to cancel order {OrderId}", orderId);
            return false;
        }
    }

    public async Task<bool> ModifyPositionAsync(long positionId, double? stopLoss, double? takeProfit)
    {
        try
        {
            _logger.LogInformation("Modifying position {PositionId}", positionId);

            if (!_client.IsConnected || !_client.IsAccountAuthenticated)
            {
                _logger.LogWarning("Cannot modify position: cTrader client not connected/account-authenticated");
                return false;
            }

            if (!stopLoss.HasValue && !takeProfit.HasValue)
            {
                _logger.LogInformation("No SL/TP provided; skipping amend-position for PositionId={PositionId}", positionId);
                return true;
            }

            // Some environments don't send an explicit response for amend-position; treat absence of an error as success.
            var (success, errorCode, errorMessage) = await SendAmendPositionSltpAsync(positionId, stopLoss, takeProfit);
            if (success)
                return true;

            // Common live issue: TP/SL may be invalid if the market has already crossed it.
            // For TRADING_BAD_STOPS, attempt a safe fallback by applying SL-only and/or TP-only.
            if (string.Equals(errorCode, "TRADING_BAD_STOPS", StringComparison.OrdinalIgnoreCase)
                && stopLoss.HasValue
                && takeProfit.HasValue)
            {
                _logger.LogWarning(
                    "⚠️ AmendPositionSLTP rejected (TRADING_BAD_STOPS). Retrying with SL-only for PositionId={PositionId}",
                    positionId);

                var (slOnlySuccess, _, _) = await SendAmendPositionSltpAsync(positionId, stopLoss, null);
                if (slOnlySuccess)
                    return true;

                _logger.LogWarning(
                    "⚠️ SL-only retry failed. Retrying with TP-only for PositionId={PositionId}",
                    positionId);

                var (tpOnlySuccess, _, _) = await SendAmendPositionSltpAsync(positionId, null, takeProfit);
                return tpOnlySuccess;
            }

            _logger.LogError("❌ AmendPositionSLTP rejected by cTrader: {Message}", errorMessage ?? errorCode ?? "Unknown error");
            return false;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to modify position {PositionId}", positionId);
            return false;
        }
    }

    private async Task<(bool Success, string? ErrorCode, string? ErrorMessage)> SendAmendPositionSltpAsync(long positionId, double? stopLoss, double? takeProfit)
    {
        var req = new ProtoOAAmendPositionSLTPReq
        {
            CtidTraderAccountId = GetAccountId(),
            PositionId = positionId
        };

        if (stopLoss.HasValue)
            req.StopLoss = stopLoss.Value;
        if (takeProfit.HasValue)
            req.TakeProfit = takeProfit.Value;

        _logger.LogInformation(
            "📤 Sending AmendPositionSLTPReq: AccountId={AccountId}, PositionId={PositionId}, SL={SL}, TP={TP}",
            req.CtidTraderAccountId,
            req.PositionId,
            stopLoss,
            takeProfit);

        await _client.SendMessageAsync(req, 2110); // PROTO_OA_AMEND_POSITION_SLTP_REQ

        using var waitCts = new CancellationTokenSource();
        var observedErrorPayloadType = 2132; // observed on this server

        var errorTaskObserved = _client.WaitForResponseAsync<byte[]>(
            observedErrorPayloadType,
            TimeSpan.FromSeconds(5),
            waitCts.Token);

        var errorTaskEnum = _client.WaitForResponseAsync<byte[]>(
            (int)PayloadType.ProtoOaErrorRes,
            TimeSpan.FromSeconds(5),
            waitCts.Token);

        var delayTask = Task.Delay(TimeSpan.FromSeconds(2), waitCts.Token);
        var completed = await Task.WhenAny(errorTaskObserved, errorTaskEnum, delayTask);
        waitCts.Cancel();

        if (completed == delayTask)
        {
            _logger.LogInformation("✅ AmendPositionSLTP sent successfully (no immediate error observed): PositionId={PositionId}", positionId);
            return (true, null, null);
        }

        var payload = completed == errorTaskObserved ? await errorTaskObserved : await errorTaskEnum;
        if (payload != null && TryParseErrorPayload(payload, out var errorCode, out var description, out var accountId))
        {
            var msg = accountId.HasValue
                ? $"{errorCode}: {description} (AccountId={accountId})"
                : $"{errorCode}: {description}";

            return (false, errorCode, msg);
        }

        return (false, null, payload != null ? FormatErrorPayload(payload) : "cTrader returned an error response");
    }

    public async Task<bool> ClosePositionAsync(long positionId, double volume)
    {
        try
        {
            _logger.LogInformation("Closing position {PositionId}", positionId);
            // TODO: Implement ProtoOAClosePositionReq
            await Task.CompletedTask;
            return true;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to close position {PositionId}", positionId);
            return false;
        }
    }

    public async Task<double?> GetCurrentPriceAsync(string symbol)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(symbol))
                return null;

            if (!_client.IsConnected || !_client.IsAccountAuthenticated)
                return null;

            if (!_symbolService.HasSymbol(symbol))
                return null;

            var symbolId = GetSymbolId(symbol);
            if (symbolId <= 0)
                return null;

            await EnsureSpotSubscribedAsync(symbolId);

            var tcs = new TaskCompletionSource<double>(TaskCreationOptions.RunContinuationsAsynchronously);

            void Handler(object? sender, CTraderMessage msg)
            {
                if (msg.PayloadType != (int)PayloadType.ProtoOaSpotEvent)
                    return;

                try
                {
                    var spot = ProtoOASpotEvent.Parser.ParseFrom(msg.Payload);
                    var spotObj = (object)spot;
                    var spotType = spotObj.GetType();

                    var symbolIdObj = spotType.GetProperty("SymbolId")?.GetValue(spotObj);
                    var tickSymbolId = symbolIdObj is null ? 0L : Convert.ToInt64(symbolIdObj);
                    if (tickSymbolId != symbolId)
                        return;

                    var bidObj = spotType.GetProperty("Bid")?.GetValue(spotObj);
                    var askObj = spotType.GetProperty("Ask")?.GetValue(spotObj);

                    var bid = NormalizePrice(symbolId, bidObj);
                    var ask = NormalizePrice(symbolId, askObj);

                    var price = 0d;
                    if (bid > 0 && ask > 0)
                        price = (bid + ask) / 2d;
                    else if (ask > 0)
                        price = ask;
                    else if (bid > 0)
                        price = bid;

                    if (price > 0)
                        tcs.TrySetResult(price);
                }
                catch
                {
                    // ignore parse errors; wait for next tick
                }
            }

            _client.MessageReceived += Handler;

            try
            {
                using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
                using var reg = cts.Token.Register(() => tcs.TrySetCanceled(cts.Token));
                return await tcs.Task;
            }
            catch
            {
                return null;
            }
            finally
            {
                _client.MessageReceived -= Handler;
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to get price for {Symbol}", symbol);
            return null;
        }
    }

    public async Task<(double? Bid, double? Ask)> GetCurrentBidAskAsync(string symbol)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(symbol))
            {
                _logger.LogDebug("[BID/ASK] Symbol is null or whitespace");
                return (null, null);
            }

            if (!_client.IsConnected || !_client.IsAccountAuthenticated)
            {
                _logger.LogDebug("[BID/ASK] Client not connected/authenticated for {Symbol}", symbol);
                return (null, null);
            }

            if (!_symbolService.HasSymbol(symbol))
            {
                _logger.LogWarning("[BID/ASK] Symbol not found: {Symbol}", symbol);
                return (null, null);
            }

            var symbolId = GetSymbolId(symbol);
            if (symbolId <= 0)
            {
                _logger.LogWarning("[BID/ASK] Invalid symbolId for {Symbol}: {SymbolId}", symbol, symbolId);
                return (null, null);
            }

            _logger.LogDebug("[BID/ASK] Fetching bid/ask for {Symbol} (SymbolId={SymbolId})", symbol, symbolId);

            await EnsureSpotSubscribedAsync(symbolId);

            var tcs = new TaskCompletionSource<(double? Bid, double? Ask)>(TaskCreationOptions.RunContinuationsAsynchronously);

            var spotEventsReceived = 0;

            void Handler(object? sender, CTraderMessage msg)
            {
                if (msg.PayloadType != (int)PayloadType.ProtoOaSpotEvent)
                    return;

                try
                {
                    var spot = ProtoOASpotEvent.Parser.ParseFrom(msg.Payload);
                    var spotObj = (object)spot;
                    var spotType = spotObj.GetType();

                    var symbolIdObj = spotType.GetProperty("SymbolId")?.GetValue(spotObj);
                    var tickSymbolId = symbolIdObj is null ? 0L : Convert.ToInt64(symbolIdObj);

                    spotEventsReceived++;

                    if (tickSymbolId != symbolId)
                    {
                        // Only log first few mismatches to avoid spam
                        if (spotEventsReceived <= 3)
                            _logger.LogDebug("[BID/ASK] Spot event for different symbol: received={ReceivedId}, expected={ExpectedId}", tickSymbolId, symbolId);
                        return;
                    }

                    var bidObj = spotType.GetProperty("Bid")?.GetValue(spotObj);
                    var askObj = spotType.GetProperty("Ask")?.GetValue(spotObj);

                    var bid = NormalizePrice(symbolId, bidObj);
                    var ask = NormalizePrice(symbolId, askObj);

                    double? bidResult = bid > 0 ? bid : null;
                    double? askResult = ask > 0 ? ask : null;

                    _logger.LogDebug("[BID/ASK] Got spot event for {Symbol} (SymbolId={SymbolId}): Bid={Bid}, Ask={Ask}", symbol, symbolId, bidResult, askResult);

                    if (bidResult.HasValue || askResult.HasValue)
                        tcs.TrySetResult((bidResult, askResult));
                }
                catch (Exception ex)
                {
                    _logger.LogDebug(ex, "[BID/ASK] Error parsing spot event");
                }
            }

            _client.MessageReceived += Handler;
            try
            {
                using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
                using var reg = cts.Token.Register(() => tcs.TrySetCanceled(cts.Token));
                return await tcs.Task;
            }
            catch (OperationCanceledException)
            {
                _logger.LogWarning("[BID/ASK] Timeout waiting for spot event. Symbol={Symbol}, SymbolId={SymbolId}, SpotEventsReceived={Count}", symbol, symbolId, spotEventsReceived);
                return (null, null);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "[BID/ASK] Error waiting for spot event. Symbol={Symbol}", symbol);
                return (null, null);
            }
            finally
            {
                _client.MessageReceived -= Handler;
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to get bid/ask for {Symbol}", symbol);
            return (null, null);
        }
    }

    private async Task EnsureSpotSubscribedAsync(long symbolId)
    {
        if (symbolId <= 0)
        {
            _logger.LogWarning("[SPOT] EnsureSpotSubscribedAsync called with invalid symbolId={SymbolId}", symbolId);
            return;
        }

        if (_spotSubscribedSymbolIds.Contains(symbolId))
        {
            _logger.LogDebug("[SPOT] Already subscribed to SymbolId={SymbolId}", symbolId);
            return;
        }

        await _spotSubscribeLock.WaitAsync();
        try
        {
            if (_spotSubscribedSymbolIds.Contains(symbolId))
            {
                _logger.LogDebug("[SPOT] Already subscribed (after lock) to SymbolId={SymbolId}", symbolId);
                return;
            }

            if (!_client.IsConnected || !_client.IsAccountAuthenticated)
            {
                _logger.LogWarning("[SPOT] Cannot subscribe - client not connected/authenticated. SymbolId={SymbolId}", symbolId);
                return;
            }

            _logger.LogInformation("[SPOT] Sending spot subscription request for SymbolId={SymbolId}", symbolId);

            var req = new ProtoOASubscribeSpotsReq
            {
                CtidTraderAccountId = _client.AccountId
            };
            req.SymbolId.Add(symbolId);

            // Some cTrader/OpenAPI servers do not reliably correlate SubscribeSpotsRes with ClientMsgId.
            // If we wait on a specific ClientMsgId, we can silently fail to mark as subscribed and then
            // miss spot ticks when we need them (which breaks Limit/Stop inference and can cause marketable LIMITs).
            await _client.SendMessageAsync(req, (int)PayloadType.ProtoOaSubscribeSpotsReq);

            var res = await _client.WaitForResponseAsync<byte[]>(
                (int)PayloadType.ProtoOaSubscribeSpotsRes,
                TimeSpan.FromSeconds(10));

            // Even if the ack times out, we optimistically mark as subscribed; spot ticks may still arrive.
            if (res == null)
            {
                _logger.LogWarning("[SPOT] Spot subscription ack timed out for SymbolId={SymbolId}; continuing optimistically", symbolId);
            }
            else
            {
                _logger.LogInformation("[SPOT] Spot subscription acknowledged for SymbolId={SymbolId}", symbolId);
            }

            _spotSubscribedSymbolIds.Add(symbolId);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "[SPOT] Spot subscription failed for SymbolId={SymbolId}", symbolId);
        }
        finally
        {
            _spotSubscribeLock.Release();
        }
    }

    private double NormalizePrice(long symbolId, object? raw)
    {
        if (raw is null)
            return 0;

        if (raw is double d)
            return d;

        if (raw is float f)
            return f;

        if (raw is decimal m)
            return (double)m;

        var asLong = Convert.ToInt64(raw);

        if (_symbolService.TryGetSymbolDigits(symbolId, out var digits) && digits > 0)
        {
            return asLong / Math.Pow(10, digits);
        }

        if (Math.Abs(asLong) > 1000)
        {
            return asLong / 100000d;
        }

        return asLong;
    }

    private long GetAccountId()
    {
        var environment = _configuration.GetSection("CTrader")["Environment"] ?? "Demo";
        var accountIdKey = environment == "Live" ? "LiveAccountId" : "DemoAccountId";
        return long.Parse(_configuration.GetSection("CTrader")[accountIdKey] ?? "0");
    }

    private long GetSymbolId(string asset)
    {
        // Use the symbol service to get the correct symbol ID
        try
        {
            // Normalize asset for cTrader symbol lookup (handles VOLATILITY75, etc.)
            var normalized = NormalizeAssetForSymbolLookup(asset);
            return _symbolService.GetSymbolId(normalized);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to get symbol ID for {Asset}", asset);
            throw new ArgumentException($"Unknown or unsupported symbol: {asset}", ex);
        }
    }

    // Normalizes asset names like "VOLATILITY75" to "Volatility 75 Index" for cTrader symbol lookup
    private static string NormalizeAssetForSymbolLookup(string asset)
    {
        if (string.IsNullOrWhiteSpace(asset)) return asset;
        var upper = asset.ToUpperInvariant().Replace(" ", "");
        if (upper == "VOLATILITY75") return "Volatility 75 Index";
        if (upper == "VOLATILITY50") return "Volatility 50 Index";
        if (upper == "VOLATILITY100") return "Volatility 100 Index";
        if (upper == "VOLATILITY10") return "Volatility 10 Index";
        if (upper == "VOLATILITY25") return "Volatility 25 Index";
        // Add more mappings as needed
        return asset;
    }

    private long CalculateVolume(long symbolId, ParsedSignal signal)
    {
        // Heuristic: treat as synthetic if asset name contains "Volatility" or "Crash" or "Boom" or "Jump" or "Step" (case-insensitive)
        string assetName = signal.Asset ?? string.Empty;
        bool isSynthetic = assetName.IndexOf("volatility", StringComparison.OrdinalIgnoreCase) >= 0
            || assetName.IndexOf("crash", StringComparison.OrdinalIgnoreCase) >= 0
            || assetName.IndexOf("boom", StringComparison.OrdinalIgnoreCase) >= 0
            || assetName.IndexOf("jump", StringComparison.OrdinalIgnoreCase) >= 0
            || assetName.IndexOf("step", StringComparison.OrdinalIgnoreCase) >= 0;

        if (isSynthetic
            && _symbolService.TryGetSymbolVolumeConstraints(symbolId, out var minVolume, out var maxVolume, out var stepVolume)
            && _symbolService.TryGetSymbolTickValue(symbolId, out var tickValue)
            && _symbolService.TryGetSymbolContractSize(symbolId, out var contractSize)
            && _symbolService.TryGetSymbolMarginInitial(symbolId, out var marginInitial))
        {
            // Use risk-based lot sizing for synthetics
            // Inputs: riskUsd (from config or signal), stopLossTicks (from signal or config)
            double riskUsd = signal.RiskUsd > 0 ? signal.RiskUsd : 10; // fallback default
            double stopLossTicks = signal.StopLossTicks > 0 ? signal.StopLossTicks : 120; // fallback default
            if (tickValue <= 0 || contractSize <= 0 || marginInitial <= 0)
            {
                _logger.LogWarning("SymbolId={SymbolId} missing TickValue/ContractSize/MarginInitial, falling back to default lot size", symbolId);
                goto fallback;
            }

            double lots = riskUsd / (stopLossTicks * tickValue);
            // Clamp to max allowed
            double maxLots = maxVolume / contractSize;
            if (lots > maxLots)
                lots = maxLots;
            // Convert to volume units (cTrader wire: lots * contractSize * 100)
            long volume = (long)Math.Round(lots * contractSize * 100d, MidpointRounding.AwayFromZero);
            if (minVolume > 0)
                volume = Math.Max(volume, minVolume);
            if (maxVolume > 0)
                volume = Math.Min(volume, maxVolume);
            if (stepVolume > 0)
            {
                volume = (volume / stepVolume) * stepVolume;
                if (minVolume > 0 && volume < minVolume)
                    volume = minVolume;
            }
            if (maxVolume > 0 && volume > maxVolume)
                volume = maxVolume;
            _logger.LogInformation("[SYNTHETIC] Calculated risk-based volume: {Volume} (lots={Lots}, riskUsd={RiskUsd}, stopLossTicks={StopLossTicks}, tickValue={TickValue}, contractSize={ContractSize}, marginInitial={MarginInitial})", volume, lots, riskUsd, stopLossTicks, tickValue, contractSize, marginInitial);
            return volume;
        }
fallback:
        // Default: use configured lot size (forex/other)
        // cTrader wire units: lots * 100000 (no extra *100)
        var requested = (long)Math.Round(_defaultLotSize * 100_000d, MidpointRounding.AwayFromZero);
        if (requested <= 0)
            requested = 1;

        if (_symbolService.TryGetSymbolVolumeConstraints(symbolId, out var minV, out var maxV, out var stepV))
        {
            var volume = requested;
            if (minV > 0)
                volume = Math.Max(volume, minV);
            if (maxV > 0)
                volume = Math.Min(volume, maxV);
            if (stepV > 0)
            {
                volume = (volume / stepV) * stepV;
                if (minV > 0 && volume < minV)
                    volume = minV;
            }
            if (maxV > 0 && volume > maxV)
                volume = maxV;
            if (volume != requested)
            {
                _logger.LogWarning(
                    "Adjusted volume for SymbolId={SymbolId}: requested={Requested} -> final={Final} (min={Min}, max={Max}, step={Step})",
                    symbolId, requested, volume, minV, maxV, stepV);
            }
            return volume;
        }
        return requested;
    }

    private static string FormatErrorPayload(byte[] payload)
    {
        try
        {
            string? errorCode = null;
            string? description = null;
            long? accountId = null;

            var input = new CodedInputStream(payload);
            uint tag;
            while ((tag = input.ReadTag()) != 0)
            {
                switch (tag)
                {
                    case 0x12:
                        errorCode = input.ReadString();
                        break;
                    case 0x28:
                        accountId = input.ReadInt64();
                        break;
                    case 0x3A:
                        description = input.ReadString();
                        break;
                    default:
                        input.SkipLastField();
                        break;
                }
            }

            if (!string.IsNullOrWhiteSpace(errorCode) && !string.IsNullOrWhiteSpace(description))
            {
                return accountId.HasValue
                    ? $"{errorCode}: {description} (AccountId={accountId})"
                    : $"{errorCode}: {description}";
            }

            return BitConverter.ToString(payload).Replace("-", " ");
        }
        catch
        {
            return BitConverter.ToString(payload).Replace("-", " ");
        }
    }

    private static bool TryParseErrorPayload(byte[] payload, out string? errorCode, out string? description, out long? accountId)
    {
        errorCode = null;
        description = null;
        accountId = null;

        try
        {
            var input = new CodedInputStream(payload);
            uint tag;
            while ((tag = input.ReadTag()) != 0)
            {
                switch (tag)
                {
                    case 0x12:
                        errorCode = input.ReadString();
                        break;
                    case 0x28:
                        accountId = input.ReadInt64();
                        break;
                    case 0x3A:
                        description = input.ReadString();
                        break;
                    default:
                        input.SkipLastField();
                        break;
                }
            }

            return !string.IsNullOrWhiteSpace(errorCode) && !string.IsNullOrWhiteSpace(description);
        }
        catch
        {
            return false;
        }
    }

    private static bool TryParseMaxAllowedVolume(string description, out double maxAllowed)
    {
        maxAllowed = 0;
        if (string.IsNullOrWhiteSpace(description))
            return false;

        // Example:
        // "Order volume = 20000.00 is bigger than maximum allowed volume = 330.00."
        var m = System.Text.RegularExpressions.Regex.Match(
            description,
            @"maximum\s+allowed\s+volume\s*=\s*([0-9]+(?:\.[0-9]+)?)",
            System.Text.RegularExpressions.RegexOptions.IgnoreCase);

        if (!m.Success)
            return false;

        return double.TryParse(m.Groups[1].Value, NumberStyles.Float, CultureInfo.InvariantCulture, out maxAllowed);
    }

    private static ProtoOATradeSide MapTradeSide(TradeDirection direction)
    {
        return direction switch
        {
            TradeDirection.Buy => ProtoOATradeSide.Buy,
            TradeDirection.Sell => ProtoOATradeSide.Sell,
            TradeDirection.Call => ProtoOATradeSide.Buy,
            TradeDirection.Put => ProtoOATradeSide.Sell,
            _ => ProtoOATradeSide.Buy
        };
    }

    private static ProtoOATradeSide InvertTradeSide(ProtoOATradeSide side)
    {
        return side == ProtoOATradeSide.Buy ? ProtoOATradeSide.Sell : ProtoOATradeSide.Buy;
    }

    private static bool TryExtractSymbolId(object executionEvent, out long symbolId)
    {
        symbolId = 0;

        // Spotware schema: executionEvent.Order.TradeData.SymbolId
        if (TryExtractInt64Path(executionEvent, out symbolId, "Order", "TradeData", "SymbolId"))
            return true;

        // Common shapes across cTrader/OpenAPI protobuf versions.
        // 1) executionEvent.SymbolId
        // 2) executionEvent.Order.SymbolId
        // 3) executionEvent.TradeData.SymbolId
        var candidates = new (string Parent, string Child)[]
        {
            ("", "SymbolId"),
            ("Order", "SymbolId"),
            ("TradeData", "SymbolId")
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
                symbolId = Convert.ToInt64(value);
                if (symbolId != 0)
                    return true;
            }
            catch
            {
                // ignore and keep searching
            }
        }

        return false;
    }

    private static bool TryExtractPositionId(object executionEvent, out long positionId)
    {
        positionId = 0;

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
                positionId = Convert.ToInt64(value);
                if (positionId != 0)
                    return true;
            }
            catch
            {
                // ignore and keep searching
            }
        }

        return false;
    }

    private static long? TryParseNewOrderResOrderId(byte[] payload)
    {
        try
        {
            var type =
                Type.GetType("OpenAPI.Net.ProtoOANewOrderRes, Spotware.OpenAPI.Net") ??
                Type.GetType("OpenAPI.Net.Helpers.ProtoOANewOrderRes, Spotware.OpenAPI.Net");

            if (type == null)
                return null;

            var parserProp = type.GetProperty("Parser");
            var parser = parserProp?.GetValue(null);
            var parseFrom = parser?.GetType().GetMethod("ParseFrom", new[] { typeof(byte[]) });
            var msg = parseFrom?.Invoke(parser, new object[] { payload });
            if (msg == null)
                return null;

            var orderObj = msg.GetType().GetProperty("Order")?.GetValue(msg);
            if (orderObj == null)
                return null;

            var orderIdObj = orderObj.GetType().GetProperty("OrderId")?.GetValue(orderObj);
            return orderIdObj == null ? null : Convert.ToInt64(orderIdObj);
        }
        catch
        {
            return null;
        }
    }
}