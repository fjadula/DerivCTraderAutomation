using DerivCTrader.Infrastructure.Deriv.Models;

namespace DerivCTrader.Application.Interfaces;

/// <summary>
/// Interface for Deriv API WebSocket client
/// </summary>
public interface IDerivClient
{
    /// <summary>
    /// Is connected to Deriv WebSocket
    /// </summary>
    bool IsConnected { get; }

    /// <summary>
    /// Is authorized with Deriv
    /// </summary>
    bool IsAuthorized { get; }

    /// <summary>
    /// Connect to Deriv WebSocket
    /// </summary>
    Task ConnectAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Authorize with API token
    /// </summary>
    Task AuthorizeAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Disconnect from Deriv
    /// </summary>
    Task DisconnectAsync();

    /// <summary>
    /// Place a binary option (CALL/PUT)
    /// </summary>
    Task<DerivTradeResult> PlaceBinaryOptionAsync(
        string asset,
        string direction,
        decimal stake,
        int durationMinutes,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Get contract outcome after expiry
    /// </summary>
    Task<DerivContractOutcome> GetContractOutcomeAsync(
        string contractId,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Get account balance
    /// </summary>
    Task<decimal> GetBalanceAsync(CancellationToken cancellationToken = default);
}