using DerivCTrader.Domain.Entities;
using DerivCTrader.Domain.Enums;
using DerivCTrader.Infrastructure.CTrader.Interfaces;
using DerivCTrader.Infrastructure.CTrader.Models;
using Microsoft.Extensions.Logging;
using OpenAPI.Net;

using PayloadType = DerivCTrader.Infrastructure.CTrader.Models.ProtoOAPayloadType;

namespace DerivCTrader.Infrastructure.CTrader;

/// <summary>
/// Monitors cTrader price ticks and detects when pending orders should execute
/// </summary>
public class CTraderPriceMonitor : ICTraderPriceMonitor
{
    private readonly ICTraderClient _client;
    private readonly ICTraderSymbolService _symbolService;
    private readonly ILogger<CTraderPriceMonitor> _logger;
    private readonly Dictionary<long, PendingOrderWatch> _watchedOrders = new();
    private readonly HashSet<long> _subscribedSymbols = new();
    private readonly SemaphoreSlim _subscriptionLock = new(1, 1);

    public event EventHandler<OrderCrossedEventArgs>? OrderCrossed;

    public CTraderPriceMonitor(ICTraderClient client, ICTraderSymbolService symbolService, ILogger<CTraderPriceMonitor> logger)
    {
        _client = client;
        _symbolService = symbolService;
        _logger = logger;

        // Subscribe to price updates
        _client.MessageReceived += OnMessageReceived;
    }

    /// <summary>
    /// Start watching a pending order for price cross
    /// </summary>
    public void WatchOrder(long orderId, long symbolId, ParsedSignal signal, bool isOpposite = false)
    {
        var watch = new PendingOrderWatch
        {
            OrderId = orderId,
            SymbolId = symbolId,
            Signal = signal,
            IsOpposite = isOpposite,
            CreatedAt = DateTime.UtcNow
        };

        _watchedOrders[orderId] = watch;

        _ = EnsureSubscribedAsync(symbolId);

        _logger.LogInformation("👁️ Watching order {OrderId}: {Asset} {Direction} @ {Entry}",
            orderId, signal.Asset, signal.Direction, signal.EntryPrice);
    }

    /// <summary>
    /// Stop watching an order (e.g., if cancelled)
    /// </summary>
    public void StopWatching(long orderId)
    {
        if (_watchedOrders.Remove(orderId))
        {
            _logger.LogInformation("Stopped watching order {OrderId}", orderId);
        }
    }

    /// <summary>
    /// Get all currently watched orders
    /// </summary>
    public IReadOnlyDictionary<long, PendingOrderWatch> WatchedOrders => _watchedOrders.AsReadOnly();

    private void OnMessageReceived(object? sender, CTraderMessage message)
    {
        // Check if this is a spot event (price tick)
        if (message.PayloadType == (int)PayloadType.ProtoOaSpotEvent)
        {
            ProcessSpotEvent(message);
        }
    }

    private void ProcessSpotEvent(CTraderMessage message)
    {
        try
        {
            // Parse spot event (price tick)
            var spotEvent = ParseSpotEvent(message.Payload);

            // Check if any watched orders should trigger
            foreach (var watch in _watchedOrders.Values.ToList())
            {
                if (watch.SymbolId != spotEvent.SymbolId)
                    continue;

                if (ShouldTrigger(watch, spotEvent))
                {
                    _logger.LogInformation("🎯 PRICE CROSSED! Order {OrderId}: {Asset} @ {Price}",
                        watch.OrderId, watch.Signal.Asset, spotEvent.Bid);

                    // Fire event
                    OrderCrossed?.Invoke(this, new OrderCrossedEventArgs
                    {
                        OrderId = watch.OrderId,
                        Signal = watch.Signal,
                        IsOpposite = watch.IsOpposite,
                        ExecutionPrice = GetExecutionPrice(watch, spotEvent)
                    });

                    // Stop watching this order
                    _watchedOrders.Remove(watch.OrderId);
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error processing spot event");
        }
    }

    /// <summary>
    /// Determine if pending order should trigger based on current price
    /// CRITICAL: This implements the "correct direction" logic
    /// </summary>
    private bool ShouldTrigger(PendingOrderWatch watch, SpotEventData spotEvent)
    {
        if (!watch.Signal.EntryPrice.HasValue)
            return false;

        var entry = (double)watch.Signal.EntryPrice.Value;

        // BUY signal: Price must cross UP through entry
        if (watch.Signal.Direction == TradeDirection.Buy || watch.Signal.Direction == TradeDirection.Call)
        {
            // Check if bid price crossed above entry
            return spotEvent.Bid >= entry && !watch.HasCrossedAbove;
        }

        // SELL signal: Price must cross DOWN through entry
        if (watch.Signal.Direction == TradeDirection.Sell || watch.Signal.Direction == TradeDirection.Put)
        {
            // Check if ask price crossed below entry
            return spotEvent.Ask <= entry && !watch.HasCrossedBelow;
        }

        return false;
    }

    private double GetExecutionPrice(PendingOrderWatch watch, SpotEventData spotEvent)
    {
        // BUY: Execute at ask price (seller's price)
        if (watch.Signal.Direction == TradeDirection.Buy || watch.Signal.Direction == TradeDirection.Call)
            return spotEvent.Ask;

        // SELL: Execute at bid price (buyer's price)
        return spotEvent.Bid;
    }

    private SpotEventData ParseSpotEvent(byte[] payload)
    {
        // Spot events are sent as inner protobuf payload of ProtoOASpotEvent (PayloadType=2136)
        var spot = ProtoOASpotEvent.Parser.ParseFrom(payload);

        // Use reflection to tolerate minor schema/version differences.
        var spotObj = (object)spot;
        var spotType = spotObj.GetType();

        var symbolIdObj = spotType.GetProperty("SymbolId")?.GetValue(spotObj);
        var symbolId = symbolIdObj is null ? 0L : Convert.ToInt64(symbolIdObj);

        var bidObj = spotType.GetProperty("Bid")?.GetValue(spotObj);
        var askObj = spotType.GetProperty("Ask")?.GetValue(spotObj);

        var bid = NormalizePrice(symbolId, bidObj);
        var ask = NormalizePrice(symbolId, askObj);

        var tsObj =
            spotType.GetProperty("Timestamp")?.GetValue(spotObj) ??
            spotType.GetProperty("UtcTimestamp")?.GetValue(spotObj);
        var ts = tsObj is null ? DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() : Convert.ToInt64(tsObj);

        return new SpotEventData
        {
            SymbolId = symbolId,
            Bid = bid,
            Ask = ask,
            Timestamp = ts
        };
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

        // Fixed-point integer path (common in some Open API streams)
        var asLong = Convert.ToInt64(raw);

        if (_symbolService.TryGetSymbolDigits(symbolId, out var digits) && digits > 0)
        {
            return asLong / Math.Pow(10, digits);
        }

        // Fallback heuristic: FX often arrives as price * 100000
        if (Math.Abs(asLong) > 1000)
        {
            return asLong / 100000d;
        }

        return asLong;
    }

    private async Task EnsureSubscribedAsync(long symbolId)
    {
        if (symbolId <= 0)
            return;

        if (_subscribedSymbols.Contains(symbolId))
            return;

        await _subscriptionLock.WaitAsync();
        try
        {
            if (_subscribedSymbols.Contains(symbolId))
                return;

            if (!_client.IsConnected || !_client.IsAccountAuthenticated)
            {
                _logger.LogWarning("Cannot subscribe to spots; cTrader client not connected/account-authenticated");
                return;
            }

            var req = new ProtoOASubscribeSpotsReq
            {
                CtidTraderAccountId = _client.AccountId
            };
            req.SymbolId.Add(symbolId);

            _logger.LogInformation("📡 Subscribing to spot events: SymbolId={SymbolId}", symbolId);
            await _client.SendMessageAsync(req, PayloadType.ProtoOaSubscribeSpotsReq);

            var res = await _client.WaitForResponseAsync<byte[]>(
                (int)PayloadType.ProtoOaSubscribeSpotsRes,
                TimeSpan.FromSeconds(10));

            if (res == null)
            {
                _logger.LogWarning("Spot subscription timed out for SymbolId={SymbolId}", symbolId);
                return;
            }

            _subscribedSymbols.Add(symbolId);
            _logger.LogInformation("✅ Spot subscription confirmed for SymbolId={SymbolId}", symbolId);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to subscribe to spot events for SymbolId={SymbolId}", symbolId);
        }
        finally
        {
            _subscriptionLock.Release();
        }
    }
}
