using Google.Protobuf;
using Google.Protobuf.Reflection;

namespace DerivCTrader.Infrastructure.CTrader.Models;

/// <summary>
/// Request for getting the list of granted trader's accounts for the access token
/// Payload type: 2148 (PROTO_OA_GET_ACCOUNTS_BY_ACCESS_TOKEN_REQ)
/// </summary>
public class ProtoOAGetAccountListByAccessTokenReq : IMessage<ProtoOAGetAccountListByAccessTokenReq>
{
    public string AccessToken { get; set; } = string.Empty;

    public MessageDescriptor Descriptor => throw new NotImplementedException();

    public int CalculateSize()
    {
        int size = 0;
        if (AccessToken.Length != 0)
        {
            size += 1 + CodedOutputStream.ComputeStringSize(AccessToken);
        }
        return size;
    }

    public ProtoOAGetAccountListByAccessTokenReq Clone()
    {
        return new ProtoOAGetAccountListByAccessTokenReq
        {
            AccessToken = AccessToken
        };
    }

    public bool Equals(ProtoOAGetAccountListByAccessTokenReq? other)
    {
        return other != null && AccessToken == other.AccessToken;
    }

    public void MergeFrom(ProtoOAGetAccountListByAccessTokenReq message)
    {
        if (message == null) return;
        if (message.AccessToken.Length != 0) AccessToken = message.AccessToken;
    }

    public void MergeFrom(CodedInputStream input)
    {
        uint tag;
        while ((tag = input.ReadTag()) != 0)
        {
            switch (tag)
            {
                case 18: // Field 2: accessToken (string)
                    AccessToken = input.ReadString();
                    break;
                default:
                    input.SkipLastField();
                    break;
            }
        }
    }

    public void WriteTo(CodedOutputStream output)
    {
        if (AccessToken.Length != 0)
        {
            output.WriteRawTag(18); // Field 2, wire type 2 (length-delimited)
            output.WriteString(AccessToken);
        }
    }
}
