using Google.Protobuf;
using Google.Protobuf.Reflection;

namespace DerivCTrader.Infrastructure.CTrader.Models;

/// <summary>
/// ProtoOAErrorRes message for cTrader Open API
/// Payload type: 2142 (PROTO_OA_ERROR_RES)
/// </summary>
public class ProtoOAErrorRes : IMessage<ProtoOAErrorRes>
{
    private static readonly MessageParser<ProtoOAErrorRes> _parser = new MessageParser<ProtoOAErrorRes>(() => new ProtoOAErrorRes());

    public static MessageParser<ProtoOAErrorRes> Parser => _parser;

    public string CtidTraderAccountId { get; set; } = string.Empty;
    public string ErrorCode { get; set; } = string.Empty;
    public string Description { get; set; } = string.Empty;

    public MessageDescriptor Descriptor => throw new NotImplementedException();

    public int CalculateSize()
    {
        int size = 0;
        if (!string.IsNullOrEmpty(CtidTraderAccountId))
        {
            size += 1 + CodedOutputStream.ComputeStringSize(CtidTraderAccountId);
        }
        if (!string.IsNullOrEmpty(ErrorCode))
        {
            size += 1 + CodedOutputStream.ComputeStringSize(ErrorCode);
        }
        if (!string.IsNullOrEmpty(Description))
        {
            size += 1 + CodedOutputStream.ComputeStringSize(Description);
        }
        return size;
    }

    public ProtoOAErrorRes Clone()
    {
        return new ProtoOAErrorRes
        {
            CtidTraderAccountId = CtidTraderAccountId,
            ErrorCode = ErrorCode,
            Description = Description
        };
    }

    public bool Equals(ProtoOAErrorRes? other)
    {
        if (other == null) return false;
        return CtidTraderAccountId == other.CtidTraderAccountId &&
               ErrorCode == other.ErrorCode &&
               Description == other.Description;
    }

    public void MergeFrom(ProtoOAErrorRes message)
    {
        if (message == null) return;
        if (!string.IsNullOrEmpty(message.CtidTraderAccountId))
            CtidTraderAccountId = message.CtidTraderAccountId;
        if (!string.IsNullOrEmpty(message.ErrorCode))
            ErrorCode = message.ErrorCode;
        if (!string.IsNullOrEmpty(message.Description))
            Description = message.Description;
    }

    public void MergeFrom(CodedInputStream input)    {
        uint tag;
        while ((tag = input.ReadTag()) != 0)
        {
            switch (tag)
            {
                case 16: // Field 2: ctidTraderAccountId (int64)
                    CtidTraderAccountId = input.ReadInt64().ToString();
                    break;
                case 26: // Field 3: errorCode (string)
                    ErrorCode = input.ReadString();
                    break;
                case 34: // Field 4: description (string)
                    Description = input.ReadString();
                    break;
                default:
                    input.SkipLastField();
                    break;
            }
        }
    }

    public void WriteTo(CodedOutputStream output)
    {
        if (!string.IsNullOrEmpty(CtidTraderAccountId))
        {
            output.WriteRawTag(10);
            output.WriteString(CtidTraderAccountId);
        }
        if (!string.IsNullOrEmpty(ErrorCode))
        {
            output.WriteRawTag(18);
            output.WriteString(ErrorCode);
        }
        if (!string.IsNullOrEmpty(Description))
        {
            output.WriteRawTag(26);
            output.WriteString(Description);
        }
    }

    public byte[] ToByteArray()
    {
        using var stream = new MemoryStream();
        using var output = new CodedOutputStream(stream);
        WriteTo(output);
        output.Flush();
        return stream.ToArray();
    }
}
