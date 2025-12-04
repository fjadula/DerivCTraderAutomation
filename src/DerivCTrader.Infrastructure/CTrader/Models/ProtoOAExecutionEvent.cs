using Google.Protobuf;
using Google.Protobuf.Reflection;

namespace DerivCTrader.Infrastructure.CTrader.Models;

public class ProtoOAExecutionEvent : IMessage<ProtoOAExecutionEvent>
{
    public static MessageParser<ProtoOAExecutionEvent> Parser { get; } = new MessageParser<ProtoOAExecutionEvent>(() => new ProtoOAExecutionEvent());

    public long CtidTraderAccountId { get; set; }
    public string OrderId { get; set; } = string.Empty;
    public long SymbolId { get; set; }
    public string ExecutionType { get; set; } = string.Empty;
    public string TradeSide { get; set; } = string.Empty;
    public decimal ExecutionPrice { get; set; }
    public long ExecutedVolume { get; set; }
    public long PositionId { get; set; }

    public MessageDescriptor Descriptor => throw new NotImplementedException();

    public int CalculateSize()
    {
        return sizeof(long) * 4 + OrderId.Length + ExecutionType.Length + TradeSide.Length + 16;
    }

    public ProtoOAExecutionEvent Clone()
    {
        return new ProtoOAExecutionEvent
        {
            CtidTraderAccountId = CtidTraderAccountId,
            OrderId = OrderId,
            SymbolId = SymbolId,
            ExecutionType = ExecutionType,
            TradeSide = TradeSide,
            ExecutionPrice = ExecutionPrice,
            ExecutedVolume = ExecutedVolume,
            PositionId = PositionId
        };
    }

    public bool Equals(ProtoOAExecutionEvent? other)
    {
        return other != null && 
               OrderId == other.OrderId && 
               CtidTraderAccountId == other.CtidTraderAccountId;
    }

    public void MergeFrom(ProtoOAExecutionEvent message)
    {
        if (message == null) return;
        if (message.CtidTraderAccountId != 0) CtidTraderAccountId = message.CtidTraderAccountId;
        if (message.OrderId.Length != 0) OrderId = message.OrderId;
        if (message.SymbolId != 0) SymbolId = message.SymbolId;
        if (message.ExecutionType.Length != 0) ExecutionType = message.ExecutionType;
        if (message.TradeSide.Length != 0) TradeSide = message.TradeSide;
    }

    public void MergeFrom(CodedInputStream input)
    {
        uint tag;
        while ((tag = input.ReadTag()) != 0)
        {
            switch (tag)
            {
                case 8:
                    CtidTraderAccountId = input.ReadInt64();
                    break;
                case 18:
                    OrderId = input.ReadString();
                    break;
                case 24:
                    SymbolId = input.ReadInt64();
                    break;
                case 34:
                    ExecutionType = input.ReadString();
                    break;
                case 42:
                    TradeSide = input.ReadString();
                    break;
                default:
                    input.SkipLastField();
                    break;
            }
        }
    }

    public void WriteTo(CodedOutputStream output)
    {
        if (CtidTraderAccountId != 0)
        {
            output.WriteInt64(CtidTraderAccountId);
        }
        if (OrderId.Length != 0)
        {
            output.WriteString(OrderId);
        }
        if (SymbolId != 0)
        {
            output.WriteInt64(SymbolId);
        }
        if (ExecutionType.Length != 0)
        {
            output.WriteString(ExecutionType);
        }
        if (TradeSide.Length != 0)
        {
            output.WriteString(TradeSide);
        }
    }
}   