namespace DerivCTrader.Application.Interfaces;

/// <summary>
/// Result of a trade execution
/// </summary>
public class TradeExecutionResult
{
    /// <summary>Whether execution was successful</summary>
    public bool Success { get; set; }

    /// <summary>Order/Contract ID from the broker</summary>
    public string? OrderId { get; set; }

    /// <summary>Entry price achieved</summary>
    public decimal? EntryPrice { get; set; }

    /// <summary>Error message if execution failed</summary>
    public string? ErrorMessage { get; set; }

    /// <summary>Execution timestamp</summary>
    public DateTime ExecutedAt { get; set; } = DateTime.UtcNow;

    /// <summary>Broker/platform used</summary>
    public string? Platform { get; set; }
}

/// <summary>
/// CFD trade parameters
/// </summary>
public class CFDTradeRequest
{
    /// <summary>Symbol to trade (e.g., "WALLSTREET30")</summary>
    public string Symbol { get; set; } = string.Empty;

    /// <summary>Direction: "BUY" or "SELL"</summary>
    public string Direction { get; set; } = string.Empty;

    /// <summary>Volume/lot size</summary>
    public decimal Volume { get; set; }

    /// <summary>Stop loss price</summary>
    public decimal? StopLoss { get; set; }

    /// <summary>Take profit price</summary>
    public decimal? TakeProfit { get; set; }

    /// <summary>Max hold time in minutes (for auto-close)</summary>
    public int? MaxHoldMinutes { get; set; }
}

/// <summary>
/// Binary option trade parameters
/// </summary>
public class BinaryTradeRequest
{
    /// <summary>Symbol to trade (e.g., "WALLSTREET30")</summary>
    public string Symbol { get; set; } = string.Empty;

    /// <summary>Contract type: "CALL" or "PUT"</summary>
    public string ContractType { get; set; } = string.Empty;

    /// <summary>Stake amount in USD</summary>
    public decimal StakeUSD { get; set; }

    /// <summary>Expiry duration in minutes</summary>
    public int ExpiryMinutes { get; set; }
}

/// <summary>
/// Interface for executing trades on various platforms.
/// Supports Deriv CFD, Deriv Binary, and MT5 (stub for future).
/// </summary>
public interface IMarketExecutor
{
    /// <summary>
    /// Execute a CFD trade on Deriv
    /// </summary>
    Task<TradeExecutionResult> ExecuteDerivCFDAsync(CFDTradeRequest request);

    /// <summary>
    /// Execute a Binary option trade on Deriv
    /// </summary>
    Task<TradeExecutionResult> ExecuteDerivBinaryAsync(BinaryTradeRequest request);

    /// <summary>
    /// Execute a CFD trade on MT5 (stub for future implementation)
    /// </summary>
    Task<TradeExecutionResult> ExecuteMT5CFDAsync(CFDTradeRequest request);

    /// <summary>
    /// Check if Deriv connection is ready
    /// </summary>
    Task<bool> IsDerivConnectedAsync();

    /// <summary>
    /// Check if MT5 connection is ready (stub)
    /// </summary>
    Task<bool> IsMT5ConnectedAsync();
}
