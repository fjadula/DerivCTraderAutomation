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
        int size = 0;
        if (CtidTraderAccountId != 0)
        {
            size += 1 + CodedOutputStream.ComputeInt64Size(CtidTraderAccountId);
        }
        if (SymbolId != 0)
        {
            size += 1 + CodedOutputStream.ComputeInt64Size(SymbolId);
        }
        if (OrderType.Length != 0)
        {
            size += 1 + CodedOutputStream.ComputeStringSize(OrderType);
        }
        if (TradeSide.Length != 0)
        {
            size += 1 + CodedOutputStream.ComputeStringSize(TradeSide);
        }
        if (Volume != 0)
        {
            size += 1 + CodedOutputStream.ComputeInt64Size(Volume);
        }
        // StopPrice is not used in our implementation - always skip it
        // if (StopPrice != 0)
        // {
        //     size += 1 + sizeof(double);
        // }
        if (StopLoss.HasValue)
        {
            size += 1 + sizeof(double);
        }
        if (TakeProfit.HasValue)
        {
            size += 1 + sizeof(double);
        }
        if (!string.IsNullOrEmpty(Label))
        {
            size += 1 + CodedOutputStream.ComputeStringSize(Label);
        }
        return size;
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
            output.WriteRawTag(8); // Field 1, wire type 0 (varint)
            output.WriteInt64(CtidTraderAccountId);
        }
        if (SymbolId != 0)
        {
            output.WriteRawTag(16); // Field 2, wire type 0 (varint)
            output.WriteInt64(SymbolId);
        }
        if (OrderType.Length != 0)
        {
            output.WriteRawTag(26); // Field 3, wire type 2 (length-delimited)
            output.WriteString(OrderType);
        }
        if (TradeSide.Length != 0)
        {
            output.WriteRawTag(34); // Field 4, wire type 2 (length-delimited)
            output.WriteString(TradeSide);
        }
        if (Volume != 0)
        {
            output.WriteRawTag(40); // Field 5, wire type 0 (varint)
            output.WriteInt64(Volume);
        }
        if (StopPrice != 0)
        {
            output.WriteRawTag(57); // Field 7, wire type 1 (fixed64/double)
            output.WriteDouble((double)StopPrice);
        }
        if (StopLoss.HasValue)
        {
            output.WriteRawTag(65); // Field 8, wire type 1 (fixed64/double)
            output.WriteDouble((double)StopLoss.Value);
        }
        if (TakeProfit.HasValue)
        {
            output.WriteRawTag(73); // Field 9, wire type 1 (fixed64/double)
            output.WriteDouble((double)TakeProfit.Value);
        }
        if (!string.IsNullOrEmpty(Label))
        {
            output.WriteRawTag(90); // Field 11, wire type 2 (length-delimited)
            output.WriteString(Label);
        }
    }
}