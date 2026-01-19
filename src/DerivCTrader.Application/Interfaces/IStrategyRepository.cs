using DerivCTrader.Domain.Entities;

namespace DerivCTrader.Application.Interfaces;

/// <summary>
/// Repository for strategy control, parameters, and signal logging
/// </summary>
public interface IStrategyRepository
{
    // ===== STRATEGY CONTROL =====

    /// <summary>Get strategy control by name</summary>
    Task<StrategyControl?> GetStrategyControlAsync(string strategyName);

    /// <summary>Update strategy control</summary>
    Task UpdateStrategyControlAsync(StrategyControl control);

    // ===== STRATEGY PARAMETERS =====

    /// <summary>Get all parameters for a strategy</summary>
    Task<List<StrategyParameter>> GetStrategyParametersAsync(string strategyName);

    /// <summary>Get a specific parameter value</summary>
    Task<string?> GetStrategyParameterAsync(string strategyName, string parameterName);

    /// <summary>Update a parameter value</summary>
    Task UpdateStrategyParameterAsync(string strategyName, string parameterName, string value);

    // ===== DOW OPEN GS SIGNALS =====

    /// <summary>Save a DowOpenGS signal log</summary>
    Task<int> SaveDowOpenGSSignalAsync(DowOpenGSSignal signal);

    /// <summary>Get signal for a specific date</summary>
    Task<DowOpenGSSignal?> GetDowOpenGSSignalByDateAsync(DateTime tradeDate);

    /// <summary>Get recent signals for review</summary>
    Task<List<DowOpenGSSignal>> GetRecentDowOpenGSSignalsAsync(int count = 10);

    /// <summary>Update signal with execution results</summary>
    Task UpdateDowOpenGSSignalExecutionAsync(int signalId, string? cfdOrderId, string? binaryContractId,
        decimal? cfdEntryPrice, decimal? cfdStopLoss, decimal? cfdTakeProfit, string? errorMessage);

    // ===== MARKET PREVIOUS CLOSE =====

    /// <summary>Get previous close for a symbol on a specific date</summary>
    Task<MarketPreviousClose?> GetPreviousCloseAsync(string symbol, DateTime closeDate);

    /// <summary>Get latest previous close for a symbol</summary>
    Task<MarketPreviousClose?> GetLatestPreviousCloseAsync(string symbol);

    /// <summary>Save or update a previous close</summary>
    Task UpsertPreviousCloseAsync(MarketPreviousClose previousClose);

    // ===== US MARKET HOLIDAYS =====

    /// <summary>Check if a date is a US market holiday</summary>
    Task<bool> IsUSMarketHolidayAsync(DateTime date);

    /// <summary>Get all holidays for a year</summary>
    Task<List<DateTime>> GetUSMarketHolidaysAsync(int year);
}
