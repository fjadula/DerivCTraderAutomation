using Google.Protobuf;
using Google.Protobuf.Reflection;

namespace DerivCTrader.Infrastructure.CTrader.Models;

/// <summary>
/// ProtoMessage wrapper for cTrader Open API
/// Each message sent over TCP must be wrapped in this structure
/// </summary>
public class ProtoMessage : IMessage<ProtoMessage>
{
    public static MessageParser<ProtoMessage> Parser { get; } = new MessageParser<ProtoMessage>(() => new ProtoMessage());

    public uint PayloadType { get; set; }
    public byte[] Payload { get; set; } = Array.Empty<byte>();
    public string? ClientMsgId { get; set; }

    public MessageDescriptor Descriptor => throw new NotImplementedException();

    public int CalculateSize()
    {
        int size = 0;
        
        // Field 1: payloadType (uint32) - 1 byte tag + varint encoding
        size += 1 + CodedOutputStream.ComputeUInt32Size(PayloadType);
        
        // Field 2: payload (bytes) - 1 byte tag + length + data
        if (Payload.Length > 0)
        {
            size += 1 + CodedOutputStream.ComputeBytesSize(ByteString.CopyFrom(Payload));
        }
        
        // Field 3: clientMsgId (string, optional)
        if (!string.IsNullOrEmpty(ClientMsgId))
        {
            size += 1 + CodedOutputStream.ComputeStringSize(ClientMsgId);
        }
        
        return size;
    }

    public ProtoMessage Clone()
    {
        return new ProtoMessage
        {
            PayloadType = PayloadType,
            Payload = (byte[])Payload.Clone(),
            ClientMsgId = ClientMsgId
        };
    }

    public bool Equals(ProtoMessage? other)
    {
        return other != null 
            && PayloadType == other.PayloadType 
            && Payload.SequenceEqual(other.Payload)
            && ClientMsgId == other.ClientMsgId;
    }

    public void MergeFrom(ProtoMessage message)
    {
        if (message == null) return;
        PayloadType = message.PayloadType;
        if (message.Payload.Length > 0)
            Payload = message.Payload;
        if (!string.IsNullOrEmpty(message.ClientMsgId))
            ClientMsgId = message.ClientMsgId;
    }

    public void MergeFrom(CodedInputStream input)
    {
        uint tag;
        while ((tag = input.ReadTag()) != 0)
        {
            switch (tag)
            {
                case 8: // Field 1, wire type 0 (varint)
                    PayloadType = input.ReadUInt32();
                    break;
                case 18: // Field 2, wire type 2 (length-delimited)
                    Payload = input.ReadBytes().ToByteArray();
                    break;
                case 26: // Field 3, wire type 2 (length-delimited)
                    ClientMsgId = input.ReadString();
                    break;
                default:
                    input.SkipLastField();
                    break;
            }
        }
    }

    public void WriteTo(CodedOutputStream output)
    {
        // Field 1: payloadType (required uint32)
        output.WriteRawTag(8); // Field 1, wire type 0 (varint)
        output.WriteUInt32(PayloadType);
        
        // Field 2: payload (optional bytes)
        if (Payload.Length > 0)
        {
            output.WriteRawTag(18); // Field 2, wire type 2 (length-delimited)
            output.WriteBytes(ByteString.CopyFrom(Payload));
        }
        
        // Field 3: clientMsgId (optional string)
        if (!string.IsNullOrEmpty(ClientMsgId))
        {
            output.WriteRawTag(26); // Field 3, wire type 2 (length-delimited)
            output.WriteString(ClientMsgId);
        }
    }
}
