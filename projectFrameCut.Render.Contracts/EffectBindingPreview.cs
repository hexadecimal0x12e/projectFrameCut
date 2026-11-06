using ProtoBuf;

namespace projectFrameCut.Render.Contracts;

[ProtoContract]
public sealed class EffectBindingPreviewRequest
{
    [ProtoMember(1)] public Guid SessionId { get; set; }
    [ProtoMember(2)] public Guid ClipId { get; set; }
    [ProtoMember(3)] public string TimelineJson { get; set; } = "";
    [ProtoMember(4)] public uint FrameIndex { get; set; }
    [ProtoMember(5)] public int Width { get; set; }
    [ProtoMember(6)] public int Height { get; set; }
    [ProtoMember(7)] public int ProjectWidth { get; set; }
    [ProtoMember(8)] public int ProjectHeight { get; set; }
    [ProtoMember(9)] public int Stage { get; set; }
    [ProtoMember(10)] public bool RecordSteps { get; set; }
    [ProtoMember(11)] public List<EffectBindingPreviewTerminal> Terminals { get; set; } = [];
    [ProtoMember(12)] public List<string> BuiltInSources { get; set; } = [];
}

[ProtoContract]
public sealed class EffectBindingPreviewTerminal
{
    [ProtoMember(1)] public Guid NodeId { get; set; }
    [ProtoMember(2)] public Guid ProviderId { get; set; }
    [ProtoMember(3)] public List<string> Fields { get; set; } = [];
}

[ProtoContract]
public sealed class EffectBindingPreviewValue
{
    [ProtoMember(1)] public string Text { get; set; } = "";
    [ProtoMember(2)] public byte[] Png { get; set; } = [];
    [ProtoMember(3)] public bool Inactive { get; set; }
    [ProtoMember(4)] public bool IsVector { get; set; }
}

[ProtoContract]
public sealed class EffectBindingPreviewStep
{
    [ProtoMember(1)] public Guid ProviderId { get; set; }
    [ProtoMember(2)] public Guid NodeId { get; set; }
    [ProtoMember(3)] public Dictionary<string, EffectBindingPreviewValue> Inputs { get; set; } = [];
    [ProtoMember(4)] public Dictionary<string, EffectBindingPreviewValue> Outputs { get; set; } = [];
}

[ProtoContract]
public sealed class EffectBindingPreviewDiagnostic
{
    [ProtoMember(1)] public Guid ProviderId { get; set; }
    [ProtoMember(2)] public string? FieldId { get; set; }
    [ProtoMember(3)] public string Message { get; set; } = "";
}

[ProtoContract]
public sealed class EffectBindingPreviewResponse
{
    [ProtoMember(1)] public List<EffectBindingPreviewStep> Steps { get; set; } = [];
    [ProtoMember(2)] public Dictionary<string, EffectBindingPreviewValue> Sources { get; set; } = [];
    [ProtoMember(3)] public string? Error { get; set; }
    [ProtoMember(4)] public List<EffectBindingPreviewDiagnostic> Diagnostics { get; set; } = [];
}
