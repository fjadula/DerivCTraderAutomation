namespace DerivCTrader.Domain.Entities;

/// <summary>
/// Configurable parameters for strategies - DB-driven to avoid redeployment.
/// </summary>
public class StrategyParameter
{
    /// <summary>Database primary key</summary>
    public int Id { get; set; }

    /// <summary>Strategy identifier (e.g., "DowOpenGS")</summary>
    public string StrategyName { get; set; } = string.Empty;

    /// <summary>Parameter name</summary>
    public string ParameterName { get; set; } = string.Empty;

    /// <summary>Parameter value (stored as string, parsed by consumer)</summary>
    public string ParameterValue { get; set; } = string.Empty;

    /// <summary>Description of what the parameter controls</summary>
    public string? Description { get; set; }

    /// <summary>When the parameter was created</summary>
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

    /// <summary>Last update timestamp</summary>
    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;
}

/// <summary>
/// Typed configuration for DowOpenGS strategy parameters.
/// Parsed from StrategyParameter table.
/// </summary>
public class DowOpenGSConfig
{
    /// <summary>Binary options stake in USD (default: 10)</summary>
    public decimal BinaryStakeUSD { get; set; } = 10m;

    /// <summary>CFD volume/lot size (default: 1.0)</summary>
    public decimal CFDVolume { get; set; } = 1.0m;

    /// <summary>CFD Stop Loss percentage (default: 0.35%)</summary>
    public decimal CFDStopLossPercent { get; set; } = 0.35m;

    /// <summary>CFD Take Profit percentage (default: 0.70%)</summary>
    public decimal CFDTakeProfitPercent { get; set; } = 0.70m;

    /// <summary>CFD max hold time in minutes (default: 60)</summary>
    public int CFDMaxHoldMinutes { get; set; } = 60;

    /// <summary>Default binary expiry in minutes (default: 60)</summary>
    public int DefaultBinaryExpiry { get; set; } = 60;

    /// <summary>Extended binary expiry in minutes (default: 60)</summary>
    public int ExtendedBinaryExpiry { get; set; } = 60;

    /// <summary>Minimum GS move (USD) to trigger extended expiry (default: 3.0)</summary>
    public decimal MinGSMoveForExtendedExpiry { get; set; } = 3.0m;

    /// <summary>Snapshot time offset in seconds before market open (default: 10)</summary>
    public int SnapshotOffsetSeconds { get; set; } = 10;

    /// <summary>Deriv symbol for Wall Street 30 CFD</summary>
    public string DerivCFDSymbol { get; set; } = "WALLSTREET30";

    /// <summary>Deriv symbol for Wall Street 30 Binary</summary>
    public string DerivBinarySymbol { get; set; } = "WALLSTREET30";
}
