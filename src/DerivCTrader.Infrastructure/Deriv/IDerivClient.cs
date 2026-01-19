namespace DerivCTrader.Infrastructure.Deriv;

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

    /// <summary>
    /// Get current spot price for a symbol using tick history API.
    /// Used for Dasha Trade entry/exit price snapshots.
    /// </summary>
    /// <param name="symbol">Raw symbol like "USDJPY" (will be converted to Deriv format)</param>
    /// <returns>Current spot price or null if unavailable</returns>
    Task<decimal?> GetSpotPriceAsync(string symbol, CancellationToken cancellationToken = default);

    /// <summary>
    /// Get historical spot price for a symbol at a specific time.
    /// Used for backtesting.
    /// </summary>
    /// <param name="symbol">Raw symbol like "EURUSD" (will be converted to Deriv format)</param>
    /// <param name="timestamp">The UTC time to get the price for</param>
    /// <returns>Spot price at or near the specified time, or null if unavailable</returns>
    Task<decimal?> GetHistoricalPriceAsync(string symbol, DateTime timestamp, CancellationToken cancellationToken = default);
}
