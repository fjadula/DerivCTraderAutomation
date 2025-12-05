using DerivCTrader.Application.Interfaces;
using DerivCTrader.Domain.Entities;
using DerivCTrader.Domain.Enums;
using DerivCTrader.Infrastructure.CTrader.Interfaces;
using DerivCTrader.Infrastructure.CTrader.Models;
using Microsoft.Extensions.Logging;

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
    private readonly ICTraderPriceMonitor _priceMonitor;
    private readonly ITradeRepository _repository;

    public CTraderPendingOrderService(
        ILogger<CTraderPendingOrderService> logger,
        ICTraderClient client,
        ICTraderOrderManager orderManager,
        ICTraderSymbolService symbolService,
        ICTraderPriceMonitor priceMonitor,
        ITradeRepository repository)
    {
        _logger = logger;
        _client = client;
        _orderManager = orderManager;
        _symbolService = symbolService;
        _priceMonitor = priceMonitor;
        _repository = repository;

        // Subscribe to price cross events
        _priceMonitor.OrderCrossed += OnOrderCrossed;
    }

    /// <summary>
    /// Process a parsed signal:
    /// 1. Validate symbol exists
    /// 2. Place pending order at entry price
    /// 3. Start monitoring for price cross
    /// </summary>
    public async Task<bool> ProcessSignalAsync(ParsedSignal signal, bool isOpposite = false)
    {
        try
        {
            _logger.LogInformation("📝 Processing cTrader signal: {Asset} {Direction} @ {Entry}",
                signal.Asset, signal.Direction, signal.EntryPrice);

            // Check if we have this symbol
            if (!_symbolService.HasSymbol(signal.Asset))
            {
                _logger.LogWarning("Unknown symbol: {Asset}", signal.Asset);
                return false;
            }

            // Get symbol ID
            var symbolId = _symbolService.GetSymbolId(signal.Asset);

            // Determine order direction (possibly opposite)
            var direction = isOpposite
                ? (signal.Direction == TradeDirection.Buy ? TradeDirection.Sell : TradeDirection.Buy)
                : signal.Direction;

            // Place pending order (BuyLimit or SellLimit)
            var orderType = direction == TradeDirection.Buy
                ? CTraderOrderType.Limit  // BuyLimit
                : CTraderOrderType.Limit; // SellLimit

            var result = await _orderManager.CreateOrderAsync(signal, orderType, isOpposite);

            if (!result.Success || !result.OrderId.HasValue)
            {
                _logger.LogError("Failed to place pending order: {Error}", result.ErrorMessage);
                return false;
            }

            _logger.LogInformation("✅ Pending order placed: OrderId={OrderId}", result.OrderId);

            // Start watching for price cross
            _priceMonitor.WatchOrder(result.OrderId.Value, symbolId, signal);

            return true;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error processing cTrader signal");
            return false;
        }
    }

    /// <summary>
    /// Called when price crosses entry and order executes
    /// This writes the executed trade to TradeExecutionQueue for Deriv processing
    /// </summary>
    private async void OnOrderCrossed(object? sender, OrderCrossedEventArgs e)
    {
        try
        {
            _logger.LogInformation("🎯 Order executed: OrderId={OrderId} at {Price}",
                e.OrderId, e.ExecutionPrice);

            // Write to TradeExecutionQueue (only after execution)
            // This is a MATCHING queue: cTrader writes here, then KhulaFxTradeMonitor reads & matches
            var queueItem = new TradeExecutionQueue
            {
                CTraderOrderId = e.OrderId.ToString(),
                Asset = e.Signal.Asset,
                Direction = e.Signal.Direction.ToString(),
                StrategyName = BuildStrategyName(e.Signal),
                ProviderChannelId = e.Signal.ProviderChannelId,
                IsOpposite = false, // TODO: Track this from signal
                CreatedAt = DateTime.UtcNow
            };

            var queueId = await _repository.EnqueueTradeAsync(queueItem);

            _logger.LogInformation("💾 Written to TradeExecutionQueue: QueueId={QueueId} (matching queue for Deriv)", queueId);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to write to TradeExecutionQueue");
        }
    }

    /// <summary>
    /// Get the count of currently monitored orders
    /// </summary>
    public int GetMonitoredOrderCount()
    {
        return _priceMonitor.WatchedOrders.Count;
    }

    private string BuildStrategyName(ParsedSignal signal)
    {
        // Format: ProviderName_Asset_Timestamp
        return $"{signal.ProviderName}_{signal.Asset}_{DateTime.UtcNow:yyyyMMddHHmmss}";
    }
}
