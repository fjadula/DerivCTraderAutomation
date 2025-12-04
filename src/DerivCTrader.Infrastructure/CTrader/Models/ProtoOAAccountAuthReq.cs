using Google.Protobuf;
using Google.Protobuf.Reflection;

namespace DerivCTrader.Infrastructure.CTrader.Models;

public class ProtoOAAccountAuthReq : IMessage<ProtoOAAccountAuthReq>
{
    public long CtidTraderAccountId { get; set; }
    public string AccessToken { get; set; } = string.Empty;

    public MessageDescriptor Descriptor => throw new NotImplementedException();

    public int CalculateSize()
    {
        return sizeof(long) + AccessToken.Length + 4;
    }

    public ProtoOAAccountAuthReq Clone()
    {
        return new ProtoOAAccountAuthReq
        {
            CtidTraderAccountId = CtidTraderAccountId,
            AccessToken = AccessToken
        };
    }

    public bool Equals(ProtoOAAccountAuthReq? other)
    {
        return other != null && CtidTraderAccountId == other.CtidTraderAccountId && AccessToken == other.AccessToken;
    }

    public void MergeFrom(ProtoOAAccountAuthReq message)
    {
        if (message == null) return;
        if (message.CtidTraderAccountId != 0) CtidTraderAccountId = message.CtidTraderAccountId;
        if (message.AccessToken.Length != 0) AccessToken = message.AccessToken;
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
        if (AccessToken.Length != 0)
        {
            output.WriteString(AccessToken);
        }
    }
}