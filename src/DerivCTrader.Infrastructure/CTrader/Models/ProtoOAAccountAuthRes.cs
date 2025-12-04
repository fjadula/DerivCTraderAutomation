using Google.Protobuf;
using Google.Protobuf.Reflection;

namespace DerivCTrader.Infrastructure.CTrader.Models;

public class ProtoOAAccountAuthRes : IMessage<ProtoOAAccountAuthRes>
{
    public static MessageParser<ProtoOAAccountAuthRes> Parser { get; } = new MessageParser<ProtoOAAccountAuthRes>(() => new ProtoOAAccountAuthRes());

    public MessageDescriptor Descriptor => throw new NotImplementedException();

    public int CalculateSize() => 0;

    public ProtoOAAccountAuthRes Clone() => new();

    public bool Equals(ProtoOAAccountAuthRes? other) => other != null;

    public void MergeFrom(ProtoOAAccountAuthRes message) { }

    public void MergeFrom(CodedInputStream input) { }

    public void WriteTo(CodedOutputStream output) { }
}