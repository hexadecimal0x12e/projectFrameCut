using ProtoBuf;

namespace projectFrameCut.Render.Contracts;

public enum GuiProjectOperation
{
    GetInfo, Save, GetClip, AddClip, SetClip, RemoveClip, CopyClip,
    GetAsset, AddAsset, RemoveAsset, GetTrack, AddTrack,
    GetTextStyle, GetTextStyleField, AddTextClip, SetTextClipStyle,
    GetEffectProviderType, GetEffectProviderField, GetClipEffectProvider,
    AddClipEffectProvider, SetClipEffectProvider, RemoveClipEffectProvider,
    GetProjectHistory, UndoProjectHistory, RedoProjectHistory, RestoreProjectHistory,
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
    [ProtoMember(5)] public string ChangeReason { get; set; } = string.Empty;
    [ProtoMember(6)] public string ClientName { get; set; } = string.Empty;
}

[ProtoContract]
public sealed class GuiProjectWork
{
    [ProtoMember(1)] public Guid SessionId { get; set; }
    [ProtoMember(2)] public GuiProjectRequest? Request { get; set; }
    [ProtoMember(3)] public string ConnectedClientName { get; set; } = string.Empty;
}

[ProtoContract]
public sealed class GuiProjectResult
{
    [ProtoMember(1)] public Guid SessionId { get; set; }
    [ProtoMember(2)] public Guid RequestId { get; set; }
    [ProtoMember(3)] public string Json { get; set; } = "null";
    [ProtoMember(4)] public RemoteError? Error { get; set; }
}

[ProtoContract]
public sealed class ProjectHistoryRequest
{
    [ProtoMember(1)] public int TimeoutSeconds { get; set; } = 60;
}

[ProtoContract]
public sealed class RestoreProjectHistoryRequest
{
    [ProtoMember(1)] public Guid SnapshotId { get; set; }
    [ProtoMember(2)] public int TimeoutSeconds { get; set; } = 60;
}

[ProtoContract]
public sealed class ProjectHistoryState
{
    [ProtoMember(1)] public Guid CurrentSnapshotId { get; set; }
    [ProtoMember(2)] public bool CanUndo { get; set; }
    [ProtoMember(3)] public bool CanRedo { get; set; }
}

[ProtoContract]
public sealed class ProjectHistoryNode
{
    [ProtoMember(1)] public Guid SnapshotId { get; set; }
    [ProtoMember(2)] public Guid PreviousSnapshotId { get; set; }
    [ProtoMember(3)] public List<Guid> NextSnapshotIds { get; set; } = [];
    [ProtoMember(4)] public DateTime SavedAtUtc { get; set; }
    [ProtoMember(5)] public string ChangeReason { get; set; } = string.Empty;
    [ProtoMember(6)] public string ChangedBy { get; set; } = string.Empty;
    [ProtoMember(7)] public Guid ChangedByUserId { get; set; }
    [ProtoMember(8)] public bool IsCurrentSnapshot { get; set; }
}

[ProtoContract]
public sealed class ProjectHistory
{
    [ProtoMember(1)] public ProjectHistoryState State { get; set; } = new();
    [ProtoMember(2)] public List<ProjectHistoryNode> Nodes { get; set; } = [];
}
