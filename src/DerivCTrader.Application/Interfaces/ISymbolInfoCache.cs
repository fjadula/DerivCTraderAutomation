using DerivCTrader.Domain.Entities;

namespace DerivCTrader.Application.Interfaces;

/// <summary>
/// Cache for SymbolInfo data loaded from database at startup.
/// Provides fast, in-memory lookups for symbol configuration during trading.
/// </summary>
public interface ISymbolInfoCache
{
    /// <summary>
    /// Load all enabled symbols from database into memory.
    /// Call once at startup.
    /// </summary>
    Task InitializeAsync();

    /// <summary>
    /// Check if the cache has been initialized.
    /// </summary>
    bool IsInitialized { get; }

    /// <summary>
    /// Get SymbolInfo by symbol name (case-insensitive, fuzzy matching).
    /// </summary>
    SymbolInfo? GetByName(string symbolName);

    /// <summary>
    /// Get SymbolInfo by cTrader symbol ID.
    /// </summary>
    SymbolInfo? GetByCTraderId(long cTraderSymbolId);

    /// <summary>
    /// Try to get volume constraints for a symbol.
    /// Returns values in cTrader wire format (scaled by 100).
    /// </summary>
    bool TryGetVolumeConstraints(long cTraderSymbolId, out long minVolume, out long maxVolume, out long stepVolume);

    /// <summary>
    /// Try to get volume constraints by symbol name.
    /// </summary>
    bool TryGetVolumeConstraintsByName(string symbolName, out long minVolume, out long maxVolume, out long stepVolume);

    /// <summary>
    /// Get all cached symbols.
    /// </summary>
    IReadOnlyList<SymbolInfo> GetAll();

    /// <summary>
    /// Get the number of symbols in the cache.
    /// </summary>
    int Count { get; }
}
