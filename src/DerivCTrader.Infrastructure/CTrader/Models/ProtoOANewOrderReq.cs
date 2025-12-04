using Google.Protobuf;
using Google.Protobuf.Reflection;

namespace DerivCTrader.Infrastructure.CTrader.Models;

public class ProtoOANewOrderReq : IMessage<ProtoOANewOrderReq>
{
    public long CtidTraderAccountId { get; set; }
    public long SymbolId { get; set; }
    public string OrderType { get; set; } = string.Empty;
    public string TradeSide { get; set; } = string.Empty;
    public long Volume { get; set; }
    public decimal StopPrice { get; set; }
    public decimal? StopLoss { get; set; }
    public decimal? TakeProfit { get; set; }
    public string? Label { get; set; }

    public MessageDescriptor Descriptor => throw new NotImplementedException();

    public int CalculateSize()
    {
        return sizeof(long) * 3 + OrderType.Length + TradeSide.Length + 32;
    }

    public ProtoOANewOrderReq Clone()
    {
        return new ProtoOANewOrderReq
        {
            CtidTraderAccountId = CtidTraderAccountId,
            SymbolId = SymbolId,
            OrderType = OrderType,
            TradeSide = TradeSide,
            Volume = Volume,
            StopPrice = StopPrice,
            StopLoss = StopLoss,
            TakeProfit = TakeProfit,
            Label = Label
        };
    }

    public bool Equals(ProtoOANewOrderReq? other)
    {
        return other != null && 
               CtidTraderAccountId == other.CtidTraderAccountId &&
               SymbolId == other.SymbolId;
    }

    public void MergeFrom(ProtoOANewOrderReq message)
    {
        if (message == null) return;
        if (message.CtidTraderAccountId != 0) CtidTraderAccountId = message.CtidTraderAccountId;
        if (message.SymbolId != 0) SymbolId = message.SymbolId;
        if (message.OrderType.Length != 0) OrderType = message.OrderType;
        if (message.TradeSide.Length != 0) TradeSide = message.TradeSide;
        if (message.Volume != 0) Volume = message.Volume;
    }

    public void MergeFrom(CodedInputStream input)
    {
        throw new NotImplementedException();
    }

    public void WriteTo(CodedOutputStream output)
    {
        if (CtidTraderAccountId != 0)
        {
            output.WriteInt64(CtidTraderAccountId);
        }
        if (SymbolId != 0)
        {
            output.WriteInt64(SymbolId);
        }
        if (OrderType.Length != 0)
        {
            output.WriteString(OrderType);
        }
        if (TradeSide.Length != 0)
        {
            output.WriteString(TradeSide);
        }
        if (Volume != 0)
        {
            output.WriteInt64(Volume);
        }
    }
}