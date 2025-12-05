namespace DerivCTrader.Infrastructure.CTrader.Models;

/// <summary>
/// Request to get the list of symbols available for trading
/// </summary>
public class ProtoOASymbolsListReq
{
    /// <summary>
    /// The trader account ID
    /// </summary>
    public long CtidTraderAccountId { get; set; }
}

/// <summary>
/// Response containing the list of available symbols
/// </summary>
public class ProtoOASymbolsListRes
{
    /// <summary>
    /// List of available symbols
    /// </summary>
    public List<ProtoOASymbol> Symbols { get; set; } = new();
}

/// <summary>
/// Represents a single symbol available on cTrader
/// </summary>
public class ProtoOASymbol
{
    /// <summary>
    /// Numeric identifier for the symbol (e.g., 1 for EURUSD)
    /// </summary>
    public long SymbolId { get; set; }

    /// <summary>
    /// Symbol name (e.g., "EURUSD")
    /// </summary>
    public string SymbolName { get; set; } = string.Empty;

    /// <summary>
    /// Whether the symbol is enabled for trading
    /// </summary>
    public bool Enabled { get; set; }

    /// <summary>
    /// Number of decimal places for price quotes
    /// </summary>
    public int Digits { get; set; }
}
