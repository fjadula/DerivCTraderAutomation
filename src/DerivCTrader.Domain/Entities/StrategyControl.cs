namespace DerivCTrader.Domain.Entities;

/// <summary>
/// Controls whether a strategy executes without redeploying code.
/// DB-driven on/off switch for strategies.
/// </summary>
public class StrategyControl
{
    /// <summary>Database primary key</summary>
    public int Id { get; set; }

    /// <summary>Strategy identifier (e.g., "DowOpenGS")</summary>
    public string StrategyName { get; set; } = string.Empty;

    /// <summary>Master on/off switch - if false, strategy does nothing</summary>
    public bool IsEnabled { get; set; }

    /// <summary>Enable CFD execution (Deriv Wall Street 30 CFD)</summary>
    public bool ExecuteCFD { get; set; }

    /// <summary>Enable Binary execution (Deriv Rise/Fall)</summary>
    public bool ExecuteBinary { get; set; }

    /// <summary>Enable MT5 execution (placeholder for future)</summary>
    public bool ExecuteMT5 { get; set; }

    /// <summary>Dry run mode - log signals but don't execute trades</summary>
    public bool DryRun { get; set; }

    /// <summary>When the control record was created</summary>
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

    /// <summary>Last update timestamp</summary>
    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;
}
