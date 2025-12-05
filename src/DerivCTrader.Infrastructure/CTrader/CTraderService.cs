using DerivCTrader.Application.Interfaces;
using DerivCTrader.Domain.Entities;
using DerivCTrader.Infrastructure.CTrader.Interfaces;
using DerivCTrader.Infrastructure.CTrader.Models;
using Microsoft.Extensions.Logging;

namespace DerivCTrader.Infrastructure.CTrader;

/// <summary>
/// Implementation of ICTraderService that uses the low-level CTraderClient
/// This bridges the Application layer abstraction with Infrastructure implementation
/// </summary>
public class CTraderService : ICTraderService
{
    private readonly ILogger<CTraderService> _logger;
    private readonly ICTraderClient _client;
    private readonly ICTraderOrderManager _orderManager;

    public bool IsConnected => _client.IsConnected;

    #pragma warning disable CS0067
    public event EventHandler<OrderExecutedEventArgs>? OrderExecuted;
    #pragma warning restore CS0067

    public CTraderService(
        ILogger<CTraderService> logger,
        ICTraderClient client,
        ICTraderOrderManager orderManager)
    {
        _logger = logger;
        _client = client;
        _orderManager = orderManager;

        // Subscribe to client connection events
        _client.ConnectionStateChanged += OnConnectionStateChanged;
    }

    public async Task<CTraderTradeResult> PlaceMarketOrderAsync(ParsedSignal signal, bool isOpposite = false)
    {
        try
        {
            _logger.LogInformation("Placing market order for {Asset}", signal.Asset);

            var result = await _orderManager.CreateOrderAsync(signal, CTraderOrderType.Market, isOpposite);

            return new CTraderTradeResult
            {
                Success = result.Success,
                OrderId = result.OrderId?.ToString(),
                ErrorMessage = result.ErrorMessage,
                ExecutedPrice = (decimal)(result.ExecutedPrice ?? 0),
                ExecutedAt = result.ExecutedAt
            };
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to place market order");
            return new CTraderTradeResult
            {
                Success = false,
                ErrorMessage = ex.Message
            };
        }
    }

    public async Task<CTraderTradeResult> PlaceLimitOrderAsync(ParsedSignal signal, double limitPrice, bool isOpposite = false)
    {
        try
        {
            _logger.LogInformation("Placing limit order for {Asset} at {Price}", signal.Asset, limitPrice);

            var result = await _orderManager.CreateOrderAsync(signal, CTraderOrderType.Limit, isOpposite);

            return new CTraderTradeResult
            {
                Success = result.Success,
                OrderId = result.OrderId?.ToString(),
                ErrorMessage = result.ErrorMessage,
                ExecutedPrice = (decimal)(result.ExecutedPrice ?? 0),
                ExecutedAt = result.ExecutedAt
            };
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to place limit order");
            return new CTraderTradeResult
            {
                Success = false,
                ErrorMessage = ex.Message
            };
        }
    }

    public async Task<bool> CancelOrderAsync(string orderId)
    {
        try
        {
            if (long.TryParse(orderId, out var id))
            {
                return await _orderManager.CancelOrderAsync(id);
            }
            return false;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to cancel order {OrderId}", orderId);
            return false;
        }
    }

    public async Task<decimal> GetCurrentPriceAsync(string symbol)
    {
        try
        {
            var price = await _orderManager.GetCurrentPriceAsync(symbol);
            return (decimal)(price ?? 0);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to get price for {Symbol}", symbol);
            return 0;
        }
    }

    private void OnConnectionStateChanged(object? sender, bool isConnected)
    {
        _logger.LogInformation("cTrader connection state changed: {IsConnected}", isConnected);
    }
}