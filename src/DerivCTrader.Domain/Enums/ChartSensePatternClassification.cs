namespace DerivCTrader.Domain.Enums;

/// <summary>
/// Pattern classification for ChartSense signals - affects Deriv expiry calculation
/// </summary>
public enum ChartSensePatternClassification
{
    /// <summary>
    /// Reaction patterns: Trendline touch, support/resistance, channel boundary, rectangle top/bottom
    /// Uses shorter expiry times
    /// </summary>
    Reaction,

    /// <summary>
    /// Breakout patterns: Trendline break, rectangle/wedge/flag breakout
    /// Uses longer expiry times for confirmation
    /// </summary>
    Breakout
}
