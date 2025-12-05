using Google.Protobuf;
using Google.Protobuf.Reflection;

namespace DerivCTrader.Infrastructure.CTrader.Models;

/// <summary>
/// Response to ProtoOAGetAccountListByAccessTokenReq
/// Payload type: 2149 (PROTO_OA_GET_ACCOUNTS_BY_ACCESS_TOKEN_RES)
/// </summary>
public class ProtoOAGetAccountListByAccessTokenRes : IMessage<ProtoOAGetAccountListByAccessTokenRes>
{
    public string AccessToken { get; set; } = string.Empty;
    public List<ProtoOACtidTraderAccount> CtidTraderAccounts { get; set; } = new();

    public MessageDescriptor Descriptor => throw new NotImplementedException();

    public int CalculateSize()
    {
        int size = 0;
        if (AccessToken.Length != 0)
        {
            size += 1 + CodedOutputStream.ComputeStringSize(AccessToken);
        }
        foreach (var account in CtidTraderAccounts)
        {
            size += 1 + CodedOutputStream.ComputeMessageSize(account);
        }
        return size;
    }

    public ProtoOAGetAccountListByAccessTokenRes Clone()
    {
        return new ProtoOAGetAccountListByAccessTokenRes
        {
            AccessToken = AccessToken,
            CtidTraderAccounts = new List<ProtoOACtidTraderAccount>(CtidTraderAccounts)
        };
    }

    public bool Equals(ProtoOAGetAccountListByAccessTokenRes? other)
    {
        return other != null && AccessToken == other.AccessToken;
    }

    public void MergeFrom(ProtoOAGetAccountListByAccessTokenRes message)
    {
        if (message == null) return;
        if (message.AccessToken.Length != 0) AccessToken = message.AccessToken;
        CtidTraderAccounts.AddRange(message.CtidTraderAccounts);
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
                case 34: // Field 4: ctidTraderAccount (repeated)
                    CtidTraderAccounts.Add(ProtoOACtidTraderAccount.Parser.ParseFrom(input));
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
            output.WriteRawTag(18); // Field 2
            output.WriteString(AccessToken);
        }
        foreach (var account in CtidTraderAccounts)
        {
            output.WriteRawTag(34); // Field 4
            output.WriteMessage(account);
        }
    }
}

/// <summary>
/// Trader account information
/// </summary>
public class ProtoOACtidTraderAccount : IMessage<ProtoOACtidTraderAccount>
{
    private static readonly MessageParser<ProtoOACtidTraderAccount> _parser = new(() => new ProtoOACtidTraderAccount());
    public static MessageParser<ProtoOACtidTraderAccount> Parser => _parser;

    public long CtidTraderAccountId { get; set; }
    public bool IsLive { get; set; }

    public MessageDescriptor Descriptor => throw new NotImplementedException();

    public int CalculateSize()
    {
        int size = 0;
        if (CtidTraderAccountId != 0)
        {
            size += 1 + CodedOutputStream.ComputeInt64Size(CtidTraderAccountId);
        }
        if (IsLive)
        {
            size += 1 + 1; // bool is 1 byte
        }
        return size;
    }

    public ProtoOACtidTraderAccount Clone()
    {
        return new ProtoOACtidTraderAccount
        {
            CtidTraderAccountId = CtidTraderAccountId,
            IsLive = IsLive
        };
    }

    public bool Equals(ProtoOACtidTraderAccount? other)
    {
        return other != null && CtidTraderAccountId == other.CtidTraderAccountId && IsLive == other.IsLive;
    }

    public void MergeFrom(ProtoOACtidTraderAccount message)
    {
        if (message == null) return;
        if (message.CtidTraderAccountId != 0) CtidTraderAccountId = message.CtidTraderAccountId;
        if (message.IsLive) IsLive = message.IsLive;
    }

    public void MergeFrom(CodedInputStream input)
    {
        uint tag;
        while ((tag = input.ReadTag()) != 0)
        {
            switch (tag)
            {
                case 8: // Field 1: ctidTraderAccountId (int64)
                    CtidTraderAccountId = input.ReadInt64();
                    break;
                case 16: // Field 2: isLive (bool)
                    IsLive = input.ReadBool();
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
            output.WriteRawTag(8); // Field 1
            output.WriteInt64(CtidTraderAccountId);
        }
        if (IsLive)
        {
            output.WriteRawTag(16); // Field 2
            output.WriteBool(IsLive);
        }
    }
}
