namespace DerivCTrader.Domain.Entities;

public class TradeExecutionQueue
{
    public int QueueId { get; set; }
    public string? CTraderOrderId { get; set; }
    public string Asset { get; set; } = string.Empty;
    public string Direction { get; set; } = string.Empty;
    public string? StrategyName { get; set; }
    public bool IsOpposite { get; set; }
    public DateTime CreatedAt { get; set; }
}
