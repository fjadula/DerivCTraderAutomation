using Google.Protobuf;
using Google.Protobuf.Reflection;

namespace DerivCTrader.Infrastructure.CTrader.Models;

public class ProtoOAApplicationAuthRes : IMessage<ProtoOAApplicationAuthRes>
{
    public static MessageParser<ProtoOAApplicationAuthRes> Parser { get; } = new MessageParser<ProtoOAApplicationAuthRes>(() => new ProtoOAApplicationAuthRes());

    public MessageDescriptor Descriptor => throw new NotImplementedException();

    public int CalculateSize() => 0;

    public ProtoOAApplicationAuthRes Clone() => new();

    public bool Equals(ProtoOAApplicationAuthRes? other) => other != null;

    public void MergeFrom(ProtoOAApplicationAuthRes message) { }

    public void MergeFrom(CodedInputStream input) { }

    public void WriteTo(CodedOutputStream output) { }
}