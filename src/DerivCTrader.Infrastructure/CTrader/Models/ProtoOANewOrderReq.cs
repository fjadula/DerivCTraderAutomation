using Google.Protobuf;
using Google.Protobuf.Reflection;

namespace DerivCTrader.Infrastructure.CTrader.Models;

public class ProtoOANewOrderReq : IMessage<ProtoOANewOrderReq>
{
    public long CtidTraderAccountId { get; set; }
    public long SymbolId { get; set; }
    public int OrderType { get; set; } // 1=MARKET, 2=LIMIT, 3=STOP, etc.
    public int TradeSide { get; set; } // 1=BUY, 2=SELL
    public long Volume { get; set; }
    public decimal? LimitPrice { get; set; }
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
        if (OrderType != 0)
        {
            size += 1 + CodedOutputStream.ComputeInt32Size(OrderType);
        }
        if (TradeSide != 0)
        {
            size += 1 + CodedOutputStream.ComputeInt32Size(TradeSide);
        }
        if (Volume != 0)
        {
            size += 1 + CodedOutputStream.ComputeInt64Size(Volume);
        }
        if (LimitPrice.HasValue)
        {
            size += 1 + sizeof(double);
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
            LimitPrice = LimitPrice,
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
        if (message.OrderType != 0) OrderType = message.OrderType;
        if (message.TradeSide != 0) TradeSide = message.TradeSide;
        if (message.Volume != 0) Volume = message.Volume;
    }

    public void MergeFrom(CodedInputStream input)
    {
        throw new NotImplementedException();
    }

    public void WriteTo(CodedOutputStream output)
    {
        // Field 1: ctidTraderAccountId (required)
        if (CtidTraderAccountId != 0)
        {
            output.WriteRawTag(8); // Field 1, wire type 0 (varint)
            output.WriteInt64(CtidTraderAccountId);
        }
        // Field 2: symbolId (required)
        if (SymbolId != 0)
        {
            output.WriteRawTag(16); // Field 2, wire type 0 (varint)
            output.WriteInt64(SymbolId);
        }
        // Field 3: orderType (required) - ALWAYS send as enum int
        output.WriteRawTag(24); // Field 3, wire type 0 (varint)
        output.WriteInt32(OrderType);
        
        // Field 4: tradeSide (required) - ALWAYS send as enum int
        output.WriteRawTag(32); // Field 4, wire type 0 (varint)
        output.WriteInt32(TradeSide);
        
        // Field 5: volume (required) - ALWAYS send
        output.WriteRawTag(40); // Field 5, wire type 0 (varint)
        output.WriteInt64(Volume);
        
        // Field 6: limitPrice (optional double) - for LIMIT orders
        if (LimitPrice.HasValue)
        {
            output.WriteRawTag(49); // Field 6, wire type 1 (fixed64)
            output.WriteDouble((double)LimitPrice.Value);
        }
        // Field 10: stopLoss (optional double)
        if (StopLoss.HasValue)
        {
            output.WriteRawTag(81); // Field 10, wire type 1 (fixed64)
            output.WriteDouble((double)StopLoss.Value);
        }
        // Field 11: takeProfit (optional double)
        if (TakeProfit.HasValue)
        {
            output.WriteRawTag(89); // Field 11, wire type 1 (fixed64)
            output.WriteDouble((double)TakeProfit.Value);
        }
        if (!string.IsNullOrEmpty(Label))
        {
            output.WriteRawTag(90); // Field 11, wire type 2 (length-delimited)
            output.WriteString(Label);
        }
    }
}