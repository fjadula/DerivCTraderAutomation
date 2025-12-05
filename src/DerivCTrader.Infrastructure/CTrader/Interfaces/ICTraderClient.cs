using DerivCTrader.Infrastructure.CTrader.Models;

namespace DerivCTrader.Infrastructure.CTrader.Interfaces;

/// <summary>
/// Interface for cTrader WebSocket client
/// </summary>
public interface ICTraderClient
{
    /// <summary>
    /// Indicates if the client is connected to cTrader
    /// </summary>
    bool IsConnected { get; }

    /// <summary>
    /// Indicates if the application is authenticated
    /// </summary>
    bool IsApplicationAuthenticated { get; }

    /// <summary>
    /// Indicates if the account is authenticated
    /// </summary>
    bool IsAccountAuthenticated { get; }

    /// <summary>
    /// Connect to cTrader WebSocket server
    /// </summary>
    Task ConnectAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Disconnect from cTrader server
    /// </summary>
    Task DisconnectAsync();

    /// <summary>
    /// Authenticate the application with cTrader
    /// </summary>
    Task AuthenticateApplicationAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Authenticate the trading account
    /// </summary>
    Task AuthenticateAccountAsync(long accountId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Get list of accounts accessible with the current access token
    /// </summary>
    Task<List<ProtoOACtidTraderAccount>> GetAccountListAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Send a message to cTrader server
    /// </summary>
    Task SendMessageAsync<T>(T message, int payloadType, CancellationToken cancellationToken = default);

    /// <summary>
    /// Wait for a response message of specific type
    /// </summary>
    Task<T?> WaitForResponseAsync<T>(int payloadType, TimeSpan timeout, CancellationToken cancellationToken = default);

    /// <summary>
    /// Event fired when connection state changes
    /// </summary>
    event EventHandler<bool>? ConnectionStateChanged;

    /// <summary>
    /// Event fired when a message is received
    /// </summary>
    event EventHandler<CTraderMessage>? MessageReceived;
}