using DerivCTrader.Domain.Entities;

namespace DerivCTrader.Application.Interfaces;

public interface IDerivClient
{
    Task<string?> ExecuteBinaryTradeAsync(
        string asset, 
        string direction, 
        decimal stake, 
        int expiryMinutes,
        string? timeframe = null,
        string? pattern = null);
    
    Task<bool> IsConnectedAsync();
    Task ConnectAsync();
    Task DisconnectAsync();
}
