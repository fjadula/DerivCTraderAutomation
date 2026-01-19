using DerivCTrader.Domain.Entities;

namespace DerivCTrader.Application.Strategies;

/// <summary>
/// Reusable signal evaluation logic for DowOpenGS strategy.
/// Used by both live trading (DowOpenGSService) and backtesting (DowOpenGSBacktestRunner).
/// </summary>
public static class DowOpenGSSignalEvaluator
{
    /// <summary>
    /// Evaluate a DowOpenGS signal based on GS and YM prices.
    /// </summary>
    /// <param name="gsPrevClose">Goldman Sachs previous close</param>
    /// <param name="gsLatest">Goldman Sachs latest price</param>
    /// <param name="ymPrevClose">Dow Futures previous close</param>
    /// <param name="ymLatest">Dow Futures latest price</param>
    /// <param name="defaultBinaryExpiry">Default binary expiry (minutes)</param>
    /// <param name="extendedBinaryExpiry">Extended binary expiry (minutes)</param>
    /// <param name="minGSMoveForExtendedExpiry">Minimum GS move for extended expiry (USD)</param>
    /// <returns>Evaluated signal with direction and expiry</returns>
    public static DowOpenGSSignal Evaluate(
        decimal gsPrevClose,
        decimal gsLatest,
        decimal ymPrevClose,
        decimal ymLatest,
        int defaultBinaryExpiry = 15,
        int extendedBinaryExpiry = 30,
        decimal minGSMoveForExtendedExpiry = 3.0m)
    {
        var gsChange = gsLatest - gsPrevClose;
        var ymChange = ymLatest - ymPrevClose;

        var gsDirection = GetDirection(gsChange);
        var ymDirection = GetDirection(ymChange);

        var signal = new DowOpenGSSignal
        {
            GS_PreviousClose = gsPrevClose,
            GS_LatestPrice = gsLatest,
            GS_Direction = gsDirection,
            GS_Change = gsChange,
            YM_PreviousClose = ymPrevClose,
            YM_LatestPrice = ymLatest,
            YM_Direction = ymDirection,
            YM_Change = ymChange
        };

        // Determine final signal
        if (gsDirection == "NEUTRAL" || ymDirection == "NEUTRAL")
        {
            signal.FinalSignal = "NO_TRADE";
            signal.NoTradeReason = "One or both instruments unchanged from previous close";
        }
        else if (gsDirection == ymDirection)
        {
            signal.FinalSignal = gsDirection;
        }
        else
        {
            signal.FinalSignal = "NO_TRADE";
            signal.NoTradeReason = $"Direction mismatch: GS={gsDirection}, YM={ymDirection}";
        }

        // Determine binary expiry
        var gsAbsMove = Math.Abs(gsChange);
        signal.BinaryExpiry = gsAbsMove >= minGSMoveForExtendedExpiry
            ? extendedBinaryExpiry
            : defaultBinaryExpiry;

        return signal;
    }

    /// <summary>
    /// Get direction string from price change.
    /// </summary>
    public static string GetDirection(decimal change)
    {
        return change > 0 ? "BUY" : change < 0 ? "SELL" : "NEUTRAL";
    }

    /// <summary>
    /// Simulate binary option result.
    /// </summary>
    /// <param name="direction">BUY or SELL</param>
    /// <param name="entryPrice">Entry price at market open</param>
    /// <param name="exitPrice">Exit price at expiry</param>
    /// <param name="stakeUSD">Stake amount in USD</param>
    /// <param name="payoutMultiplier">Payout multiplier (e.g., 0.85 for 85%)</param>
    /// <returns>Result (WIN/LOSS) and P&L</returns>
    public static (string Result, decimal PnL) SimulateBinaryResult(
        string direction,
        decimal entryPrice,
        decimal exitPrice,
        decimal stakeUSD,
        decimal payoutMultiplier = 0.85m)
    {
        bool isWin;

        if (direction == "BUY")
        {
            isWin = exitPrice > entryPrice;
        }
        else // SELL
        {
            isWin = exitPrice < entryPrice;
        }

        var result = isWin ? "WIN" : "LOSS";
        var pnl = isWin ? stakeUSD * payoutMultiplier : -stakeUSD;

        return (result, pnl);
    }

    /// <summary>
    /// Simulate CFD trade result using 1-minute candles.
    /// Exits on first hit of: TP, SL, or time limit.
    /// </summary>
    /// <param name="direction">BUY or SELL</param>
    /// <param name="entryPrice">CFD entry price</param>
    /// <param name="stopLossPercent">Stop loss percentage (e.g., 0.35)</param>
    /// <param name="takeProfitPercent">Take profit percentage (e.g., 0.70)</param>
    /// <param name="lotSize">Lot size (volume)</param>
    /// <param name="candles">1-minute candles from entry to max hold time</param>
    /// <param name="maxHoldMinutes">Maximum hold time in minutes</param>
    /// <returns>Exit details: reason, price, P&L, exit time</returns>
    public static CFDSimulationResult SimulateCFDResult(
        string direction,
        decimal entryPrice,
        decimal stopLossPercent,
        decimal takeProfitPercent,
        decimal lotSize,
        IEnumerable<MarketPriceCandle> candles,
        int maxHoldMinutes = 30)
    {
        var result = new CFDSimulationResult
        {
            EntryPrice = entryPrice,
            Direction = direction
        };

        // Calculate SL/TP prices
        decimal stopLoss, takeProfit;

        if (direction == "BUY")
        {
            stopLoss = entryPrice * (1 - stopLossPercent / 100);
            takeProfit = entryPrice * (1 + takeProfitPercent / 100);
        }
        else // SELL
        {
            stopLoss = entryPrice * (1 + stopLossPercent / 100);
            takeProfit = entryPrice * (1 - takeProfitPercent / 100);
        }

        result.StopLoss = stopLoss;
        result.TakeProfit = takeProfit;

        var candleList = candles.ToList();
        var entryTime = candleList.FirstOrDefault()?.TimeUtc ?? DateTime.MinValue;
        var maxExitTime = entryTime.AddMinutes(maxHoldMinutes);

        foreach (var candle in candleList)
        {
            // Check time limit
            if (candle.TimeUtc > maxExitTime)
            {
                break;
            }

            // Check for SL/TP hit within candle
            if (direction == "BUY")
            {
                // For BUY: check if Low hit SL (bad), or High hit TP (good)
                // Order matters: check SL first (more conservative)
                if (candle.Low <= stopLoss)
                {
                    result.ExitReason = "SL_HIT";
                    result.ExitPrice = stopLoss;
                    result.ExitTime = candle.TimeUtc;
                    result.Result = "LOSS";
                    break;
                }

                if (candle.High >= takeProfit)
                {
                    result.ExitReason = "TP_HIT";
                    result.ExitPrice = takeProfit;
                    result.ExitTime = candle.TimeUtc;
                    result.Result = "WIN";
                    break;
                }
            }
            else // SELL
            {
                // For SELL: check if High hit SL (bad), or Low hit TP (good)
                if (candle.High >= stopLoss)
                {
                    result.ExitReason = "SL_HIT";
                    result.ExitPrice = stopLoss;
                    result.ExitTime = candle.TimeUtc;
                    result.Result = "LOSS";
                    break;
                }

                if (candle.Low <= takeProfit)
                {
                    result.ExitReason = "TP_HIT";
                    result.ExitPrice = takeProfit;
                    result.ExitTime = candle.TimeUtc;
                    result.Result = "WIN";
                    break;
                }
            }
        }

        // If neither SL nor TP hit, exit at time limit
        if (string.IsNullOrEmpty(result.ExitReason))
        {
            var lastCandle = candleList.LastOrDefault(c => c.TimeUtc <= maxExitTime);
            if (lastCandle != null)
            {
                result.ExitReason = "TIME_EXIT";
                result.ExitPrice = lastCandle.Close;
                result.ExitTime = lastCandle.TimeUtc;

                // Determine win/loss based on exit price
                if (direction == "BUY")
                {
                    result.Result = result.ExitPrice > entryPrice ? "WIN" : "LOSS";
                }
                else
                {
                    result.Result = result.ExitPrice < entryPrice ? "WIN" : "LOSS";
                }
            }
            else
            {
                result.ExitReason = "NO_DATA";
                result.Result = "UNKNOWN";
            }
        }

        // Calculate P&L
        // WS30: Each point = $1 per lot (approximate)
        if (result.ExitPrice.HasValue)
        {
            var pointsMove = direction == "BUY"
                ? result.ExitPrice.Value - entryPrice
                : entryPrice - result.ExitPrice.Value;

            // WS30 is quoted in points, so $1/point/lot is a reasonable approximation
            result.PnL = pointsMove * lotSize;
        }

        return result;
    }
}

/// <summary>
/// Result of CFD trade simulation.
/// </summary>
public class CFDSimulationResult
{
    public string Direction { get; set; } = string.Empty;
    public decimal EntryPrice { get; set; }
    public decimal? ExitPrice { get; set; }
    public decimal? StopLoss { get; set; }
    public decimal? TakeProfit { get; set; }
    public string? ExitReason { get; set; }
    public string? Result { get; set; }
    public decimal? PnL { get; set; }
    public DateTime? ExitTime { get; set; }
}
