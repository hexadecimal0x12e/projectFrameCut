using ProtoBuf;

namespace projectFrameCut.Render.Contracts;

public enum GuiProjectOperation
{
    GetInfo, Save, GetClip, AddClip, SetClip, RemoveClip, CopyClip,
    GetAsset, AddAsset, RemoveAsset, GetTrack, AddTrack,
    GetTextStyle, GetTextStyleField, AddTextClip, SetTextClipStyle,
    GetEffectProviderType, GetEffectProviderField, GetClipEffectProvider,
    AddClipEffectProvider, SetClipEffectProvider, RemoveClipEffectProvider,
}

[ProtoContract]
public sealed class GuiProjectSession
{
    [ProtoMember(1)] public Guid SessionId { get; set; }
}

[ProtoContract]
public sealed class GuiProjectRequest
{
    [ProtoMember(1)] public Guid RequestId { get; set; } = Guid.NewGuid();
    [ProtoMember(2)] public GuiProjectOperation Operation { get; set; }
    [ProtoMember(3)] public string ParametersJson { get; set; } = "{}";
    [ProtoMember(4)] public int TimeoutSeconds { get; set; } = 60;
}

[ProtoContract]
public sealed class GuiProjectWork
{
    [ProtoMember(1)] public Guid SessionId { get; set; }
    [ProtoMember(2)] public GuiProjectRequest? Request { get; set; }
}

[ProtoContract]
public sealed class GuiProjectResult
{
    [ProtoMember(1)] public Guid SessionId { get; set; }
    [ProtoMember(2)] public Guid RequestId { get; set; }
    [ProtoMember(3)] public string Json { get; set; } = "null";
    [ProtoMember(4)] public RemoteError? Error { get; set; }
}
