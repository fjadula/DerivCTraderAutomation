namespace DerivCTrader.Infrastructure.CTrader.Interfaces;

/// <summary>
/// Interface for managing cTrader symbol IDs
/// </summary>
public interface ICTraderSymbolService
{
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
}
