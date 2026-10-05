using ProtoBuf;

namespace projectFrameCut.Render.Contracts;

public sealed class ProjectExternalSourceManifest
{
    public int FormatVersion { get; set; } = 1;
    public string Id { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public string Version { get; set; } = string.Empty;
    public string Author { get; set; } = string.Empty;
    public string? Description { get; set; }
    public string? AuthorUrl { get; set; }
    public string Assembly { get; set; } = string.Empty;
    public string EntryPoint { get; set; } = string.Empty;
    public Dictionary<string, string> Files { get; set; } = new(StringComparer.Ordinal);
}

public sealed class ProjectExternalSourceAsset
{
    public Guid ImportId { get; set; }
    public string Directory { get; set; } = string.Empty;
    public string ManifestSha256 { get; set; } = string.Empty;
    public ProjectExternalSourceManifest Manifest { get; set; } = new();
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public List<ExternalVideoSourceDescriptor> Sources { get; set; } = [];
}

public sealed class ProjectExternalSourceIndex
{
    public int Version { get; set; } = 1;
    public List<ProjectExternalSourceAsset> Assets { get; set; } = [];
}

[ProtoContract]
public sealed class ProjectExternalSourceApproval
{
    [ProtoMember(1)] public Guid ImportId { get; set; }
    [ProtoMember(2)] public string ManifestSha256 { get; set; } = string.Empty;
}

[ProtoContract]
public sealed class SetProjectExternalSourcesRequest
{
    [ProtoMember(1)] public string ProjectRoot { get; set; } = string.Empty;
    [ProtoMember(2)] public List<ProjectExternalSourceApproval> AllowedSources { get; set; } = [];
}

[ProtoContract]
public sealed class ProjectExternalSourceCatalogRequest
{
    [ProtoMember(1)] public string ProjectRoot { get; set; } = string.Empty;
}

[ProtoContract]
public sealed class ProjectExternalSourceStatus
{
    [ProtoMember(1)] public Guid ImportId { get; set; }
    [ProtoMember(2)] public bool Loaded { get; set; }
    [ProtoMember(3)] public string Error { get; set; } = string.Empty;
    [ProtoMember(4)] public List<ExternalVideoSourceDescriptor> Sources { get; set; } = [];
    [ProtoMember(5)] public string Name { get; set; } = string.Empty;
    [ProtoMember(6)] public string Version { get; set; } = string.Empty;
    [ProtoMember(7)] public string Author { get; set; } = string.Empty;
    [ProtoMember(8)] public string ManifestId { get; set; } = string.Empty;
    [ProtoMember(9)] public string Description { get; set; } = string.Empty;
    [ProtoMember(10)] public string AuthorUrl { get; set; } = string.Empty;
    [ProtoMember(11)] public string ManifestSha256 { get; set; } = string.Empty;
    [ProtoMember(12)] public string Directory { get; set; } = string.Empty;
    [ProtoMember(13)] public DateTime ImportedAt { get; set; }
}

[ProtoContract]
public sealed class ProjectExternalSourceCatalog
{
    [ProtoMember(1)] public List<ProjectExternalSourceStatus> Sources { get; set; } = [];
}

[ProtoContract]
public sealed class ProjectExternalSourceAuthorization
{
    [ProtoMember(1)] public string Token { get; set; } = string.Empty;
    [ProtoMember(2)] public string SourceId { get; set; } = string.Empty;
    [ProtoMember(3)] public string ManifestSha256 { get; set; } = string.Empty;
    [ProtoMember(4)] public byte[] Challenge { get; set; } = [];
}

[ProtoContract]
public sealed class ProjectExternalSourceFrame
{
    [ProtoMember(1)] public IsolationPayloadReference Payload { get; set; } = new();
}
