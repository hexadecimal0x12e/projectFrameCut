using ProtoBuf;

namespace projectFrameCut.Render.Contracts;

[ProtoContract]
public sealed class IsolationCreatePluginChannelRequest
{
    [ProtoMember(1)] public string SourcePluginId { get; set; } = string.Empty;
    [ProtoMember(2)] public string TargetPluginId { get; set; } = string.Empty;
}

[ProtoContract]
public sealed class IsolationRegisterPluginChannelRequest
{
    [ProtoMember(1)] public string SourcePluginId { get; set; } = string.Empty;
    [ProtoMember(2)] public PluginChannelDescriptor Descriptor { get; set; } = new();
}

[ProtoContract]
public sealed class PluginChannelDescriptor
{
    [ProtoMember(1)] public string ChannelId { get; set; } = string.Empty;
    [ProtoMember(2)] public string PipeName { get; set; } = string.Empty;
    [ProtoMember(3)] public string AuthenticationToken { get; set; } = string.Empty;
    [ProtoMember(4)] public string SourcePluginId { get; set; } = string.Empty;
    [ProtoMember(5)] public string TargetPluginId { get; set; } = string.Empty;
}

[ProtoContract]
public sealed class PluginChannelWireRequest
{
    [ProtoMember(1)] public int ProtocolVersion { get; set; } = PluginIsolationProtocol.CurrentVersion;
    [ProtoMember(2)] public string ChannelId { get; set; } = string.Empty;
    [ProtoMember(3)] public string AuthenticationToken { get; set; } = string.Empty;
    [ProtoMember(4)] public string SourcePluginId { get; set; } = string.Empty;
    [ProtoMember(5)] public string TargetPluginId { get; set; } = string.Empty;
    [ProtoMember(6)] public string Command { get; set; } = string.Empty;
    [ProtoMember(7)] public string JsonPayload { get; set; } = "{}";
    [ProtoMember(8)] public byte[] BinaryPayload { get; set; } = [];
}

[ProtoContract]
public sealed class PluginChannelWireResponse
{
    [ProtoMember(1)] public bool Accepted { get; set; }
    [ProtoMember(2)] public string Error { get; set; } = string.Empty;
    [ProtoMember(3)] public string JsonPayload { get; set; } = "{}";
    [ProtoMember(4)] public byte[] BinaryPayload { get; set; } = [];
}
