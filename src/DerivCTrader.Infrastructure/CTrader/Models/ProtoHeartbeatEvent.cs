using Google.Protobuf;
using Google.Protobuf.Reflection;

namespace DerivCTrader.Infrastructure.CTrader.Models;

public class ProtoHeartbeatEvent : IMessage<ProtoHeartbeatEvent>
{
    public static MessageParser<ProtoHeartbeatEvent> Parser { get; } = new MessageParser<ProtoHeartbeatEvent>(() => new ProtoHeartbeatEvent());

    public MessageDescriptor Descriptor => throw new NotImplementedException();

    public int CalculateSize() => 0;

    public ProtoHeartbeatEvent Clone() => new();

    public bool Equals(ProtoHeartbeatEvent? other) => other != null;

    public void MergeFrom(ProtoHeartbeatEvent message) { }

    public void MergeFrom(CodedInputStream input) { }

    public void WriteTo(CodedOutputStream output) { }
}