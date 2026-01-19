using DerivCTrader.Domain.Entities;

namespace DerivCTrader.Application.Interfaces;

/// <summary>
/// Repository interface for Dasha Trade selective martingale system.
/// Handles all database operations for pending signals, trades, and compounding state.
/// </summary>
public interface IDashaTradeRepository
{
    // =============================================
    // Provider Configuration
    // =============================================

    /// <summary>
    /// Gets the provider configuration for a channel.
    /// </summary>
    Task<DashaProviderConfig?> GetProviderConfigAsync(string providerChannelId);

    /// <summary>
    /// Gets all active provider configurations.
    /// </summary>
    Task<List<DashaProviderConfig>> GetActiveProviderConfigsAsync();

    // =============================================
    // Pending Signals
    // =============================================

    /// <summary>
    /// Saves a new pending signal awaiting expiry evaluation.
    /// </summary>
    Task<int> SavePendingSignalAsync(DashaPendingSignal signal);

    /// <summary>
    /// Gets pending signals that have reached expiry and need evaluation.
    /// </summary>
    Task<List<DashaPendingSignal>> GetSignalsAwaitingEvaluationAsync();

    /// <summary>
    /// Gets pending signals that need entry price to be filled in (EntryPrice = 0).
    /// </summary>
    Task<List<DashaPendingSignal>> GetSignalsNeedingEntryPriceAsync();

    /// <summary>
    /// Gets a pending signal by ID.
    /// </summary>
    Task<DashaPendingSignal?> GetPendingSignalByIdAsync(int pendingSignalId);

    /// <summary>
    /// Updates a pending signal after evaluation.
    /// </summary>
    Task UpdatePendingSignalAsync(DashaPendingSignal signal);

    /// <summary>
    /// Checks if a similar signal already exists (deduplication).
    /// </summary>
    Task<bool> SignalExistsAsync(string asset, string direction, DateTime signalTime, string providerChannelId);

    // =============================================
    // Compounding State
    // =============================================

    /// <summary>
    /// Gets the compounding state for a provider.
    /// </summary>
    Task<DashaCompoundingState?> GetCompoundingStateAsync(string providerChannelId);

    /// <summary>
    /// Creates initial compounding state for a provider.
    /// </summary>
    Task<int> CreateCompoundingStateAsync(DashaCompoundingState state);

    /// <summary>
    /// Updates the compounding state after a trade.
    /// </summary>
    Task UpdateCompoundingStateAsync(DashaCompoundingState state);

    // =============================================
    // Trades
    // =============================================

    /// <summary>
    /// Creates a new trade record.
    /// </summary>
    Task<int> CreateTradeAsync(DashaTrade trade);

    /// <summary>
    /// Updates a trade after execution or settlement.
    /// </summary>
    Task UpdateTradeAsync(DashaTrade trade);

    /// <summary>
    /// Gets a trade by ID.
    /// </summary>
    Task<DashaTrade?> GetTradeByIdAsync(int tradeId);

    /// <summary>
    /// Gets a trade by Deriv contract ID.
    /// </summary>
    Task<DashaTrade?> GetTradeByContractIdAsync(string derivContractId);

    /// <summary>
    /// Gets unsettled trades that need outcome monitoring.
    /// </summary>
    Task<List<DashaTrade>> GetUnsettledTradesAsync();

    /// <summary>
    /// Gets recent trades for a provider (for analytics).
    /// </summary>
    Task<List<DashaTrade>> GetRecentTradesAsync(string providerChannelId, int count = 50);

    // =============================================
    // Analytics
    // =============================================

    /// <summary>
    /// Gets provider statistics (win rate, profit, etc.).
    /// </summary>
    Task<DashaProviderStats> GetProviderStatsAsync(string providerChannelId);
}

/// <summary>
/// Statistics for a Dasha provider.
/// </summary>
public class DashaProviderStats
{
    public string ProviderChannelId { get; set; } = string.Empty;
    public int TotalSignalsReceived { get; set; }
    public int ProviderWins { get; set; }
    public int ProviderLosses { get; set; }
    public decimal ProviderLossRate => TotalSignalsReceived > 0
        ? (decimal)ProviderLosses / TotalSignalsReceived * 100
        : 0;

    public int TradesExecuted { get; set; }
    public int OurWins { get; set; }
    public int OurLosses { get; set; }
    public decimal OurWinRate => TradesExecuted > 0
        ? (decimal)OurWins / TradesExecuted * 100
        : 0;

    public decimal TotalProfit { get; set; }
    public decimal TotalStaked { get; set; }
    public decimal ROI => TotalStaked > 0
        ? TotalProfit / TotalStaked * 100
        : 0;
}
