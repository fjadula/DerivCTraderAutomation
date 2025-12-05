namespace DerivCTrader.Application.Interfaces;

/// <summary>
/// Calculates binary option expiry times based on signal type and asset
/// </summary>
public interface IBinaryExpiryCalculator
{
    /// <summary>
    /// Calculate expiry in minutes for a given signal type and asset
    /// </summary>
    /// <param name="signalType">Type of signal (e.g., "Binary", "Forex")</param>
    /// <param name="asset">Asset name (e.g., "EURUSD", "Volatility 100")</param>
    /// <returns>Expiry time in minutes</returns>
    int CalculateExpiry(string signalType, string asset);
}