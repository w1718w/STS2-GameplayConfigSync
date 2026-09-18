using BaseLib.Abstracts;
using MegaCrit.Sts2.Core.Logging;
using MegaCrit.Sts2.Core.Multiplayer.Serialization;
using MegaCrit.Sts2.Core.Multiplayer.Transport;

namespace GameplayConfigSync;

public sealed class ConfigRequestMessage : ICustomMessage
{
    public int Protocol;
    public string RequestId = "";
    public string ClientVersion = "";
    public bool ShouldBroadcast => false;
    public bool ShouldBuffer => true;
    public NetTransferMode Mode => NetTransferMode.Reliable;
    public LogLevel LogLevel => LogLevel.Debug;
    public void Serialize(PacketWriter writer)
    {
        writer.WriteInt(Protocol);
        writer.WriteString(RequestId);
        writer.WriteString(ClientVersion);
    }
    public void Deserialize(PacketReader reader)
    {
        Protocol = reader.ReadInt();
        RequestId = reader.ReadString();
        ClientVersion = reader.ReadString();
    }
    public void HandleMessage(ulong senderId) =>
        ConfigSyncSession.HandleRequest(senderId, Protocol, RequestId, ClientVersion);
}

public sealed class ConfigSnapshotMessage : ICustomMessage
{
    public int Protocol;
    public string RequestId = "";
    public string HostVersion = "";
    public string PayloadHash = "";
    public string Payload = "";
    public bool ShouldBroadcast => false;
    public bool ShouldBuffer => true;
    public NetTransferMode Mode => NetTransferMode.Reliable;
    public LogLevel LogLevel => LogLevel.Debug;
    public void Serialize(PacketWriter writer)
    {
        writer.WriteInt(Protocol);
        writer.WriteString(RequestId);
        writer.WriteString(HostVersion);
        writer.WriteString(PayloadHash);
        writer.WriteString(Payload);
    }
    public void Deserialize(PacketReader reader)
    {
        Protocol = reader.ReadInt();
        RequestId = reader.ReadString();
        HostVersion = reader.ReadString();
        PayloadHash = reader.ReadString();
        Payload = reader.ReadString();
    }
    public void HandleMessage(ulong senderId) => ConfigSyncSession.HandleSnapshot(
        senderId, Protocol, RequestId, HostVersion, PayloadHash, Payload);
}
