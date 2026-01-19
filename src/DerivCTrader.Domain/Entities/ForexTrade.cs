using DerivCTrader.Domain.Enums;

namespace DerivCTrader.Domain.Entities;

public class ForexTrade
{
    public int TradeId { get; set; }

    /// <summary>
    /// cTrader Position ID (unique identifier for the filled position)
    /// </summary>
    public long? PositionId { get; set; }

    public string Symbol { get; set; } = string.Empty;
    public string Direction { get; set; } = string.Empty;
    public decimal? EntryPrice { get; set; }
    public decimal? ExitPrice { get; set; }

    /// <summary>
    /// Stop Loss price
    /// </summary>
    public decimal? SL { get; set; }

    /// <summary>
    /// Take Profit price
    /// </summary>
    public decimal? TP { get; set; }
    public DateTime? EntryTime { get; set; }
    public DateTime? ExitTime { get; set; }
    public decimal? PnL { get; set; }
    public decimal? PnLPercent { get; set; }
    public string? Status { get; set; }

    /// <summary>
    /// Trade outcome: "Profit", "Loss", or "Breakeven"
    /// </summary>
    public string? Outcome { get; set; }

    /// <summary>
    /// Risk:Reward ratio calculated at trade close (e.g., "1:2", "1:3")
    /// </summary>
    public string? RR { get; set; }

    /// <summary>
    /// Strategy/Provider name (e.g., "TestChannel")
    /// </summary>
    public string? Strategy { get; set; }

    public string? Notes { get; set; }
    public DateTime CreatedAt { get; set; }
    public bool? IndicatorsLinked { get; set; }
    
    /// <summary>
    /// Telegram message ID for the notification sent when order was created
    /// Used to thread fill/modify/close notifications as replies
    /// </summary>
    public int? TelegramMessageId { get; set; }
}
