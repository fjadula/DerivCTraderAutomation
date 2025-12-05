using Google.Protobuf;
using Google.Protobuf.Reflection;

namespace DerivCTrader.Infrastructure.CTrader.Models;

public class ProtoOAApplicationAuthReq : IMessage<ProtoOAApplicationAuthReq>
{
    public string ClientId { get; set; } = string.Empty;
    public string ClientSecret { get; set; } = string.Empty;

    public MessageDescriptor Descriptor => throw new NotImplementedException();

    public int CalculateSize()
    {
        int size = 0;
        if (ClientId.Length != 0)
        {
            size += 1 + CodedOutputStream.ComputeStringSize(ClientId);
        }
        if (ClientSecret.Length != 0)
        {
            size += 1 + CodedOutputStream.ComputeStringSize(ClientSecret);
        }
        return size;
    }

    public ProtoOAApplicationAuthReq Clone()
    {
        return new ProtoOAApplicationAuthReq
        {
            ClientId = ClientId,
            ClientSecret = ClientSecret
        };
    }

    public bool Equals(ProtoOAApplicationAuthReq? other)
    {
        return other != null && ClientId == other.ClientId && ClientSecret == other.ClientSecret;
    }

    public void MergeFrom(ProtoOAApplicationAuthReq message)
    {
        if (message == null) return;
        if (message.ClientId.Length != 0) ClientId = message.ClientId;
        if (message.ClientSecret.Length != 0) ClientSecret = message.ClientSecret;
    }

    public void MergeFrom(CodedInputStream input)
    {
        throw new NotImplementedException();
    }

    public void WriteTo(CodedOutputStream output)
    {
        if (ClientId.Length != 0)
        {
            output.WriteRawTag(18); // Field 2, wire type 2 (length-delimited)
            output.WriteString(ClientId);
        }
        if (ClientSecret.Length != 0)
        {
            output.WriteRawTag(26); // Field 3, wire type 2 (length-delimited)
            output.WriteString(ClientSecret);
        }
    }
}