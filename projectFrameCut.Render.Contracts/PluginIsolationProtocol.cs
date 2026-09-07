using ProtoBuf;

namespace projectFrameCut.Render.Contracts;

[ProtoContract]
public enum IsolationValueKind
{
    [ProtoEnum] Null = 0,
    [ProtoEnum] String = 1,
    [ProtoEnum] Boolean = 2,
    [ProtoEnum] Int32 = 3,
    [ProtoEnum] UInt32 = 4,
    [ProtoEnum] Int64 = 5,
    [ProtoEnum] UInt64 = 6,
    [ProtoEnum] Single = 7,
    [ProtoEnum] Double = 8,
    [ProtoEnum] Color = 9,
    [ProtoEnum] Position = 10,
}

[ProtoContract]
public sealed class IsolationValue
{
    [ProtoMember(1)] public IsolationValueKind Kind { get; set; }
    [ProtoMember(2)] public string StringValue { get; set; } = string.Empty;
    [ProtoMember(3)] public bool BooleanValue { get; set; }
    [ProtoMember(4)] public long SignedValue { get; set; }
    [ProtoMember(5)] public ulong UnsignedValue { get; set; }
    [ProtoMember(6)] public double NumberValue { get; set; }
    [ProtoMember(7)] public long X { get; set; }
    [ProtoMember(8)] public long Y { get; set; }
    [ProtoMember(9)] public long Width { get; set; }
    [ProtoMember(10)] public long Height { get; set; }
    [ProtoMember(11)] public float Alpha { get; set; }
    [ProtoMember(12)] public bool HasAlpha { get; set; }
}

[ProtoContract]
public sealed class IsolationNegotiateRequest
{
    [ProtoMember(1)] public int ProtocolVersion { get; set; } = PluginIsolationProtocol.CurrentVersion;
    [ProtoMember(2)] public string AuthenticationToken { get; set; } = string.Empty;
    [ProtoMember(3)] public int HostProcessId { get; set; }
    [ProtoMember(4)] public IsolationPayloadKind PreferredPayloadKind { get; set; } = IsolationPayloadKind.SharedMemory;
}

[ProtoContract]
public sealed class IsolationPluginAuthorizationRequest
{
    [ProtoMember(1)] public int ProtocolVersion { get; set; } = PluginIsolationProtocol.CurrentVersion;
    [ProtoMember(2)] public string AuthenticationToken { get; set; } = string.Empty;
    [ProtoMember(3)] public string PluginId { get; set; } = string.Empty;
    [ProtoMember(4)] public string InstancePackageName { get; set; } = string.Empty;
    [ProtoMember(5)] public string EncryptedAssemblyHash { get; set; } = string.Empty;
    [ProtoMember(6)] public byte[] Challenge { get; set; } = [];
}

[ProtoContract]
public sealed class IsolationPluginAuthorizationResponse
{
    [ProtoMember(1)] public string PluginId { get; set; } = string.Empty;
    [ProtoMember(2)] public byte[] Challenge { get; set; } = [];
    [ProtoMember(3)] public string DecryptionKey { get; set; } = string.Empty;
}

[ProtoContract]
public sealed class IsolationLoadPluginRequest
{
    [ProtoMember(1)] public string PluginId { get; set; } = string.Empty;
    [ProtoMember(2)] public string Locale { get; set; } = "en-US";
    [ProtoMember(3)] public IsolationPayloadReference Assembly { get; set; } = new();
    [ProtoMember(4)] public string DependencyDirectoryLocator { get; set; } = string.Empty;
    [ProtoMember(5)] public Dictionary<string, string> Configuration { get; set; } = [];
}

[ProtoContract]
public sealed class IsolationPluginDescriptor
{
    [ProtoMember(1)] public string PluginId { get; set; } = string.Empty;
    [ProtoMember(2)] public int PluginApiVersion { get; set; }
    [ProtoMember(3)] public List<IsolationProviderCatalogItem> Providers { get; set; } = [];
    [ProtoMember(4)] public List<IsolationVideoSourceDescriptor> VideoSources { get; set; } = [];
    [ProtoMember(5)] public List<string> ProjectTools { get; set; } = [];
}

[ProtoContract]
public sealed class IsolationInvokeProjectToolRequest
{
    [ProtoMember(1)] public string ToolId { get; set; } = string.Empty;
    [ProtoMember(2)] public string InputJson { get; set; } = "{}";
}

[ProtoContract]
public sealed class IsolationInvokeProjectToolResponse
{
    [ProtoMember(1)] public string OutputJson { get; set; } = string.Empty;
}

[ProtoContract]
public sealed class IsolationUpdateProjectPluginConfigurationRequest
{
    [ProtoMember(1)] public Dictionary<string, string> Configuration { get; set; } = [];
}

[ProtoContract]
public sealed class IsolationProviderCatalogItem
{
    [ProtoMember(1)] public string TypeName { get; set; } = string.Empty;
}

[ProtoContract]
public sealed class IsolationCreateProviderRequest
{
    [ProtoMember(1)] public string TypeName { get; set; } = string.Empty;
}

[ProtoContract]
public sealed class IsolationFieldDescriptor
{
    [ProtoMember(1)] public string Id { get; set; } = string.Empty;
    [ProtoMember(2)] public string TypeName { get; set; } = string.Empty;
    [ProtoMember(3)] public string FromPlugin { get; set; } = string.Empty;
    [ProtoMember(4)] public ulong FieldType { get; set; }
    [ProtoMember(5)] public string DefaultValue { get; set; } = string.Empty;
    [ProtoMember(6)] public string MinimumValue { get; set; } = string.Empty;
    [ProtoMember(7)] public string MaximumValue { get; set; } = string.Empty;
    [ProtoMember(8)] public List<string> PresetOptions { get; set; } = [];
    [ProtoMember(9)] public string Remarks { get; set; } = string.Empty;
    [ProtoMember(10)] public bool IsDynamic { get; set; }
    [ProtoMember(11)] public string BoundProviderId { get; set; } = string.Empty;
    [ProtoMember(12)] public IsolationValue Value { get; set; } = new();
}

[ProtoContract]
public sealed class IsolationProviderDescriptor
{
    [ProtoMember(1)] public long ObjectId { get; set; }
    [ProtoMember(2)] public string TypeName { get; set; } = string.Empty;
    [ProtoMember(3)] public string FromPlugin { get; set; } = string.Empty;
    [ProtoMember(4)] public int EffectType { get; set; }
    [ProtoMember(5)] public int Target { get; set; }
    [ProtoMember(6)] public bool Enabled { get; set; }
    [ProtoMember(7)] public string InstanceId { get; set; } = string.Empty;
    [ProtoMember(8)] public string Name { get; set; } = string.Empty;
    [ProtoMember(9)] public List<IsolationFieldDescriptor> InputFields { get; set; } = [];
    [ProtoMember(10)] public IsolationFieldDescriptor OutputField { get; set; } = new();
    [ProtoMember(11)] public List<int> SupportedImplementTypes { get; set; } = [];
    [ProtoMember(12)] public int DefaultImplementType { get; set; }
}

[ProtoContract]
public sealed class IsolationProviderState
{
    [ProtoMember(1)] public long ObjectId { get; set; }
    [ProtoMember(2)] public bool Enabled { get; set; }
    [ProtoMember(3)] public string InstanceId { get; set; } = string.Empty;
    [ProtoMember(4)] public string Name { get; set; } = string.Empty;
    [ProtoMember(5)] public Dictionary<string, string> AnchorBindings { get; set; } = [];
    [ProtoMember(6)] public List<IsolationFieldDescriptor> Fields { get; set; } = [];
    [ProtoMember(7)] public Dictionary<string, IsolationValue> Metadata { get; set; } = [];
}

[ProtoContract]
public sealed class IsolationBuildProviderRequest
{
    [ProtoMember(1)] public IsolationProviderState Provider { get; set; } = new();
}

[ProtoContract]
public sealed class IsolationEffectDescriptor
{
    [ProtoMember(1)] public long ObjectId { get; set; }
    [ProtoMember(2)] public string FromPlugin { get; set; } = string.Empty;
    [ProtoMember(3)] public string TypeName { get; set; } = string.Empty;
    [ProtoMember(4)] public int EffectType { get; set; }
    [ProtoMember(5)] public int ImplementType { get; set; }
    [ProtoMember(6)] public string Name { get; set; } = string.Empty;
    [ProtoMember(7)] public string InstanceId { get; set; } = string.Empty;
    [ProtoMember(8)] public bool Enabled { get; set; }
    [ProtoMember(9)] public int Index { get; set; }
    [ProtoMember(10)] public bool IsReorderable { get; set; }
    [ProtoMember(11)] public bool CanProcessFromCanvas { get; set; }
    [ProtoMember(12)] public string NeedComputer { get; set; } = string.Empty;
    [ProtoMember(13)] public int RelativeWidth { get; set; }
    [ProtoMember(14)] public int RelativeHeight { get; set; }
    [ProtoMember(15)] public int StartPoint { get; set; }
    [ProtoMember(16)] public int EndPoint { get; set; }
    [ProtoMember(17)] public bool IsScoped { get; set; }
    [ProtoMember(18)] public int ProjectFrameRate { get; set; }
    [ProtoMember(19)] public Dictionary<string, IsolationValue> Parameters { get; set; } = [];
    [ProtoMember(20)] public List<string> DynamicProviderIds { get; set; } = [];
    [ProtoMember(21)] public bool IsColorAdjust { get; set; }
}

[ProtoContract]
public sealed class IsolationEffectList
{
    [ProtoMember(1)] public List<IsolationEffectDescriptor> Effects { get; set; } = [];
}

[ProtoContract]
public sealed class IsolationCloneEffectRequest
{
    [ProtoMember(1)] public long ObjectId { get; set; }
    [ProtoMember(2)] public Dictionary<string, IsolationValue> Parameters { get; set; } = [];
}

[ProtoContract]
public sealed class IsolationReleaseObjectRequest
{
    [ProtoMember(1)] public long ObjectId { get; set; }
}

[ProtoContract]
public sealed class IsolationEffectFrameRequest
{
    [ProtoMember(1)] public long ObjectId { get; set; }
    [ProtoMember(2)] public IsolationPayloadReference Source { get; set; } = new();
    [ProtoMember(3)] public IsolationPayloadReference? SecondSource { get; set; }
    [ProtoMember(4)] public int TargetWidth { get; set; }
    [ProtoMember(5)] public int TargetHeight { get; set; }
    [ProtoMember(6)] public int TargetPixelMode { get; set; }
    [ProtoMember(7)] public float Progress { get; set; }
    [ProtoMember(8)] public int TopStartX { get; set; }
    [ProtoMember(9)] public int TopStartY { get; set; }
    [ProtoMember(10)] public bool UsePositionedMixture { get; set; }
    [ProtoMember(11)] public uint TargetFrame { get; set; }
    [ProtoMember(12)] public IsolationClipSnapshot? Clip { get; set; }
    [ProtoMember(13)] public Dictionary<string, IsolationValue> DynamicValues { get; set; } = [];
}

[ProtoContract]
public sealed class IsolationPictureResponse
{
    [ProtoMember(1)] public IsolationPayloadReference Picture { get; set; } = new();
}

[ProtoContract]
public sealed class IsolationSupportsSourceReplacementRequest
{
    [ProtoMember(1)] public long ObjectId { get; set; }
    [ProtoMember(2)] public IsolationClipSnapshot Clip { get; set; } = new();
    [ProtoMember(3)] public int TargetWidth { get; set; }
    [ProtoMember(4)] public int TargetHeight { get; set; }
}

[ProtoContract]
public sealed class IsolationBooleanResponse
{
    [ProtoMember(1)] public bool Value { get; set; }
}

[ProtoContract]
public sealed class IsolationClipSnapshot
{
    [ProtoMember(1)] public string FromPlugin { get; set; } = string.Empty;
    [ProtoMember(2)] public int ClipType { get; set; }
    [ProtoMember(3)] public string TypeName { get; set; } = string.Empty;
    [ProtoMember(4)] public string Id { get; set; } = string.Empty;
    [ProtoMember(5)] public string Name { get; set; } = string.Empty;
    [ProtoMember(6)] public string BoundSoundTrack { get; set; } = string.Empty;
    [ProtoMember(7)] public uint LayerIndex { get; set; }
    [ProtoMember(8)] public uint SubLayerIndex { get; set; }
    [ProtoMember(9)] public uint StartFrame { get; set; }
    [ProtoMember(10)] public uint RelativeStartFrame { get; set; }
    [ProtoMember(11)] public uint Duration { get; set; }
    [ProtoMember(12)] public int TargetWidth { get; set; }
    [ProtoMember(13)] public int TargetHeight { get; set; }
    [ProtoMember(14)] public int TargetX { get; set; }
    [ProtoMember(15)] public int TargetY { get; set; }
    [ProtoMember(16)] public int StartingX { get; set; }
    [ProtoMember(17)] public int StartingY { get; set; }
    [ProtoMember(18)] public float FrameTime { get; set; }
    [ProtoMember(19)] public bool ExtendToWholeDraft { get; set; }
    [ProtoMember(20)] public string FileName { get; set; } = string.Empty;
    [ProtoMember(21)] public bool NeedFilePath { get; set; }
    [ProtoMember(22)] public Dictionary<string, IsolationValue> Metadata { get; set; } = [];
}

[ProtoContract]
public sealed class IsolationVideoSourceDescriptor
{
    [ProtoMember(1)] public long ObjectId { get; set; }
    [ProtoMember(2)] public string TypeName { get; set; } = string.Empty;
    [ProtoMember(3)] public List<string> PreferredExtensions { get; set; } = [];
    [ProtoMember(4)] public int ResultBitsPerPixel { get; set; }
    [ProtoMember(5)] public bool HasKnownResultBitsPerPixel { get; set; }
    [ProtoMember(6)] public long TotalFrames { get; set; }
    [ProtoMember(7)] public double Fps { get; set; }
    [ProtoMember(8)] public int Width { get; set; }
    [ProtoMember(9)] public int Height { get; set; }
    [ProtoMember(10)] public bool SupportsHdr { get; set; }
}

[ProtoContract]
public sealed class IsolationCreateVideoSourceRequest
{
    [ProtoMember(1)] public string TypeName { get; set; } = string.Empty;
    [ProtoMember(2)] public IsolationPayloadReference Source { get; set; } = new();
}

[ProtoContract]
public sealed class IsolationVideoSourceStateRequest
{
    [ProtoMember(1)] public long ObjectId { get; set; }
    [ProtoMember(2)] public uint Index { get; set; }
    [ProtoMember(3)] public bool EnableLock { get; set; }
    [ProtoMember(4)] public bool StrictMode { get; set; }
}

[ProtoContract]
public sealed class IsolationReadVideoFrameRequest
{
    [ProtoMember(1)] public IsolationVideoSourceStateRequest State { get; set; } = new();
    [ProtoMember(2)] public uint TargetFrame { get; set; }
    [ProtoMember(3)] public int SourceX { get; set; }
    [ProtoMember(4)] public int SourceY { get; set; }
    [ProtoMember(5)] public int SourceWidth { get; set; }
    [ProtoMember(6)] public int SourceHeight { get; set; }
    [ProtoMember(7)] public int TargetWidth { get; set; }
    [ProtoMember(8)] public int TargetHeight { get; set; }
    [ProtoMember(9)] public bool UseRegion { get; set; }
    [ProtoMember(10)] public bool RequestHdr { get; set; }
    [ProtoMember(11)] public bool HasAlpha { get; set; }
}
