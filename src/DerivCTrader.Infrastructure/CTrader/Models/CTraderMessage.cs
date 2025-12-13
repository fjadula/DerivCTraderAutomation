namespace DerivCTrader.Infrastructure.CTrader.Models;

public class CTraderMessage
{
    public int PayloadType { get; set; }
    public byte[] Payload { get; set; } = Array.Empty<byte>();
    public string? ClientMsgId { get; set; }
    public DateTime ReceivedAt { get; set; }
}