using System.Net.WebSockets;
using DerivCTrader.Application.Interfaces;
using DerivCTrader.Infrastructure.CTrader.Models;
using Microsoft.Extensions.Logging;

namespace DerivCTrader.Infrastructure.CTrader;

internal class CTraderPositionMonitor
{
    private readonly ILogger<CTraderPositionMonitor> _logger;
    private readonly ClientWebSocket _webSocket;

    public event EventHandler<OrderExecutedEventArgs>? OrderExecuted;

    public CTraderPositionMonitor(ILogger<CTraderPositionMonitor> logger, ClientWebSocket webSocket)
    {
        _logger = logger;
        _webSocket = webSocket;
    }

    public async Task StartMonitoringAsync(CancellationToken cancellationToken)
    {
        try
        {
            var buffer = new byte[8192];

            while (!cancellationToken.IsCancellationRequested && _webSocket.State == WebSocketState.Open)
            {
                var result = await _webSocket.ReceiveAsync(new ArraySegment<byte>(buffer), cancellationToken);

                if (result.MessageType == WebSocketMessageType.Binary)
                {
                    await ProcessMessageAsync(buffer, result.Count);
                }
                else if (result.MessageType == WebSocketMessageType.Close)
                {
                    _logger.LogWarning("WebSocket connection closed by server");
                    break;
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error in position monitoring");
        }
    }

    private async Task ProcessMessageAsync(byte[] buffer, int length)
    {
        try
        {
            // Extract payload type (first 4 bytes)
            var payloadType = (ProtoOAPayloadType)BitConverter.ToInt32(buffer, 0);

            // Extract payload (remaining bytes)
            var payload = new byte[length - sizeof(int)];
            Array.Copy(buffer, sizeof(int), payload, 0, payload.Length);

            if (payloadType == ProtoOAPayloadType.ProtoOaExecutionEvent)
            {
                var executionEvent = ProtoOAExecutionEvent.Parser.ParseFrom(payload);
                await HandleExecutionEventAsync(executionEvent);
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to process incoming message");
        }
    }

    private Task HandleExecutionEventAsync(ProtoOAExecutionEvent executionEvent)
    {
        _logger.LogInformation("Execution event received: ExecutionType={ExecutionType}",
            executionEvent.ExecutionType);

        if (executionEvent.ExecutionType == ProtoOAExecutionType.OrderFilled)
        {
            // ProtoOAExecutionEvent has Order or Position nested objects
            var order = executionEvent.Order;
            if (order != null)
            {
                var eventArgs = new OrderExecutedEventArgs
                {
                    OrderId = order.OrderId.ToString(),
                    Symbol = order.TradeData?.SymbolId.ToString() ?? "UNKNOWN",
                    Direction = order.TradeData?.TradeSide.ToString() ?? "UNKNOWN",
                    ExecutionPrice = (decimal)order.ExecutionPrice,
                    ExecutionTime = DateTime.UtcNow
                };

                OrderExecuted?.Invoke(this, eventArgs);
            }
        }

        return Task.CompletedTask;
    }
}