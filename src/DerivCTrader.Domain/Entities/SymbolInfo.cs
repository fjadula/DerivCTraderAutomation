namespace DerivCTrader.Domain.Entities;

/// <summary>
/// Stores cTrader symbol configuration for proper lot sizing and pip calculations.
/// Populated from ProtoOASymbol via cTrader Open API.
/// </summary>
public class SymbolInfo
{
    public int SymbolInfoId { get; set; }

    /// <summary>
    /// cTrader Symbol ID (unique identifier in cTrader)
    /// </summary>
    public long CTraderSymbolId { get; set; }

    /// <summary>
    /// Symbol name as displayed (e.g., "Volatility 25 Index", "EURUSD")
    /// </summary>
    public string SymbolName { get; set; } = string.Empty;

    /// <summary>
    /// Base asset code (e.g., "R_25", "EUR")
    /// </summary>
    public string? BaseAsset { get; set; }

    /// <summary>
    /// Quote asset code (e.g., "USD")
    /// </summary>
    public string? QuoteAsset { get; set; }

    /// <summary>
    /// Number of decimal places for price display (e.g., 3 for synthetics, 5 for forex)
    /// Used for pip calculations: 1 pip = 10^(-PipPosition)
    /// </summary>
    public int PipPosition { get; set; }

    /// <summary>
    /// Minimum price change (e.g., 0.001)
    /// </summary>
    public decimal MinChange { get; set; }

    /// <summary>
    /// Lot size in base units (e.g., 1 for Index Unit, 100000 for standard forex lot)
    /// </summary>
    public decimal LotSize { get; set; }

    /// <summary>
    /// Minimum allowed trade quantity in lots (e.g., 0.50)
    /// </summary>
    public decimal MinTradeQuantity { get; set; }

    /// <summary>
    /// Maximum allowed trade quantity in lots (e.g., 330.00)
    /// </summary>
    public decimal MaxTradeQuantity { get; set; }

    /// <summary>
    /// Step size for volume changes (e.g., 0.01 lots)
    /// </summary>
    public decimal StepVolume { get; set; }

    /// <summary>
    /// Minimum stop-loss distance in pips (e.g., 423)
    /// </summary>
    public int MinSlDistancePips { get; set; }

    /// <summary>
    /// Minimum take-profit distance in pips (e.g., 423)
    /// </summary>
    public int MinTpDistancePips { get; set; }

    /// <summary>
    /// Commission per lot (if any)
    /// </summary>
    public decimal? Commission { get; set; }

    /// <summary>
    /// Swap rate for long positions (percentage)
    /// </summary>
    public decimal? SwapLong { get; set; }

    /// <summary>
    /// Swap rate for short positions (percentage)
    /// </summary>
    public decimal? SwapShort { get; set; }

    /// <summary>
    /// Whether this symbol is currently tradeable
    /// </summary>
    public bool IsEnabled { get; set; } = true;

    /// <summary>
    /// Asset category (e.g., "Synthetic", "Forex", "Crypto")
    /// </summary>
    public string? Category { get; set; }

    /// <summary>
    /// When this record was last updated from cTrader
    /// </summary>
    public DateTime LastUpdatedUtc { get; set; }

    /// <summary>
    /// When this record was created
    /// </summary>
    public DateTime CreatedAtUtc { get; set; }

    // ===== Helper Methods =====

    /// <summary>
    /// Calculate pip value from price movement
    /// </summary>
    public decimal PriceToPips(decimal priceMovement)
    {
        var pipSize = (decimal)Math.Pow(10, -PipPosition);
        return priceMovement / pipSize;
    }

    /// <summary>
    /// Calculate price movement from pips
    /// </summary>
    public decimal PipsToPrice(decimal pips)
    {
        var pipSize = (decimal)Math.Pow(10, -PipPosition);
        return pips * pipSize;
    }

    /// <summary>
    /// Validate if the given volume is valid for this symbol
    /// </summary>
    public bool IsValidVolume(decimal volume)
    {
        return volume >= MinTradeQuantity && volume <= MaxTradeQuantity;
    }
}
