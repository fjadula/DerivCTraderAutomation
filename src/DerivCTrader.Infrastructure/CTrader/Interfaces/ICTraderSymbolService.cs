namespace DerivCTrader.Infrastructure.CTrader.Interfaces;

/// <summary>
/// Interface for managing cTrader symbol IDs
/// </summary>
public interface ICTraderSymbolService
{
    /// <summary>
    /// Try to resolve TickValue for a symbol (per tick PnL in quote currency).
    /// </summary>
    bool TryGetSymbolTickValue(long symbolId, out double tickValue);

    /// <summary>
    /// Try to resolve ContractSize for a symbol (units per lot).
    /// </summary>
    bool TryGetSymbolContractSize(long symbolId, out double contractSize);

    /// <summary>
    /// Try to resolve MarginInitial for a symbol (margin per lot in account currency).
    /// </summary>
    bool TryGetSymbolMarginInitial(long symbolId, out double marginInitial);
    /// <summary>
    /// True once the symbol map has been fetched and is safe to use.
    /// </summary>
    bool IsInitialized { get; }

    /// <summary>
    /// Initialize the symbol mapping by fetching from cTrader
    /// Must be called once after authentication
    /// </summary>
    Task InitializeAsync();

    /// <summary>
    /// Get the numeric symbol ID for an asset name (e.g., EURUSD)
    /// </summary>
    /// <param name="assetName">Asset name like "EURUSD", "EUR/USD", or "EUR USD"</param>
    /// <returns>cTrader symbol ID</returns>
    /// <exception cref="InvalidOperationException">If service not initialized</exception>
    /// <exception cref="ArgumentException">If symbol not found</exception>
    long GetSymbolId(string assetName);

    /// <summary>
    /// Check if a symbol is available
    /// </summary>
    bool HasSymbol(string assetName);

    /// <summary>
    /// Try to resolve a cTrader symbol name by numeric SymbolId.
    /// Useful for sanity checks to prevent wrong-symbol executions.
    /// </summary>
    bool TryGetSymbolName(long symbolId, out string symbolName);

    /// <summary>
    /// Try to resolve the number of digits used by a symbol price (for fixed-point spot prices).
    /// </summary>
    bool TryGetSymbolDigits(long symbolId, out int digits);

    /// <summary>
    /// Get the number of digits for a symbol, or throw if unknown.
    /// </summary>
    int GetDigits(long symbolId);

    /// <summary>
    /// Try to resolve volume constraints for a symbol (wire units as expected by cTrader Open API).
    /// </summary>
    bool TryGetSymbolVolumeConstraints(long symbolId, out long minVolume, out long maxVolume, out long stepVolume);

    /// <summary>
    /// Ensure volume constraints are fetched and cached for the given SymbolId.
    /// </summary>
    Task EnsureSymbolVolumeConstraintsAsync(long symbolId, CancellationToken cancellationToken = default);
}
