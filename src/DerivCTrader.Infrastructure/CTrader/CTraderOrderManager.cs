using System.Globalization;
using System.Net.WebSockets;
using DerivCTrader.Domain.Entities;
using DerivCTrader.Domain.Enums;
using DerivCTrader.Infrastructure.CTrader.Interfaces;
using DerivCTrader.Infrastructure.CTrader.Models;
using Google.Protobuf;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

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

            var direction = isOpposite 
                ? (signal.Direction == TradeDirection.Call ? "SELL" : "BUY")
                : (signal.Direction == TradeDirection.Call ? "BUY" : "SELL");

            var orderReq = new ProtoOANewOrderReq
            {
                CtidTraderAccountId = GetAccountId(),
                SymbolId = GetSymbolId(signal.Asset),
                OrderType = orderType == CTraderOrderType.Market ? "MARKET" : "LIMIT",
                TradeSide = direction,
                Volume = CalculateVolume(signal),
                StopLoss = signal.StopLoss,
                TakeProfit = signal.TakeProfit
            };

            if (orderType == CTraderOrderType.Stop && signal.EntryPrice.HasValue)
            {
                orderReq.StopPrice = (decimal)signal.EntryPrice.Value;
            }

            await _client.SendMessageAsync(orderReq, (int)ProtoOAPayloadType.ProtoOaNewOrderReq);

            _logger.LogInformation("Order created for {Asset}: {Direction} at {Type}", 
                signal.Asset, direction, orderType);

            return new CTraderOrderResult
            {
                Success = true,
                OrderId = 0, // Will be updated when response received
                ExecutedAt = DateTime.UtcNow
            };
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
            // TODO: Implement ProtoOAAmendPositionSLTPReq
            await Task.CompletedTask;
            return true;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to modify position {PositionId}", positionId);
            return false;
        }
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
            // TODO: Implement price fetching
            await Task.CompletedTask;
            return null;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to get price for {Symbol}", symbol);
            return null;
        }
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
            return _symbolService.GetSymbolId(asset);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to get symbol ID for {Asset}", asset);
            throw new ArgumentException($"Unknown or unsupported symbol: {asset}", ex);
        }
    }

    private long CalculateVolume(ParsedSignal signal)
    {
        // Convert lot size to cTrader volume units
        // 1 lot = 100,000 units in cTrader
        return (long)(_defaultLotSize * 100000);
    }
}