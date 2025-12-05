using DerivCTrader.Domain.Entities;
using DerivCTrader.Domain.Enums;
using DerivCTrader.Infrastructure.CTrader.Interfaces;
using DerivCTrader.Infrastructure.CTrader.Models;
using Microsoft.Extensions.Logging;

namespace DerivCTrader.Infrastructure.CTrader;

/// <summary>
/// Monitors cTrader price ticks and detects when pending orders should execute
/// </summary>
public class CTraderPriceMonitor : ICTraderPriceMonitor
{
    private readonly ICTraderClient _client;
    private readonly ILogger<CTraderPriceMonitor> _logger;
    private readonly Dictionary<long, PendingOrderWatch> _watchedOrders = new();

    public event EventHandler<OrderCrossedEventArgs>? OrderCrossed;

    public CTraderPriceMonitor(ICTraderClient client, ILogger<CTraderPriceMonitor> logger)
    {
        _client = client;
        _logger = logger;

        // Subscribe to price updates
        _client.MessageReceived += OnMessageReceived;
    }

    /// <summary>
    /// Start watching a pending order for price cross
    /// </summary>
    public void WatchOrder(long orderId, long symbolId, ParsedSignal signal)
    {
        var watch = new PendingOrderWatch
        {
            OrderId = orderId,
            SymbolId = symbolId,
            Signal = signal,
            CreatedAt = DateTime.UtcNow
        };

        _watchedOrders[orderId] = watch;

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
        if (message.PayloadType == (int)ProtoOAPayloadType.ProtoOaSpotEvent)
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
        // TODO: Implement proper Protobuf parsing for ProtoOASpotEvent
        // For now, return mock data
        return new SpotEventData
        {
            SymbolId = 1,
            Bid = 1.0850,
            Ask = 1.0852,
            Timestamp = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()
        };
    }
}
