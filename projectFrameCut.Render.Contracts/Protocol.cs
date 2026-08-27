using ProtoBuf;
using System.Diagnostics.CodeAnalysis;
using System.Text.Json;

namespace projectFrameCut.Render.Contracts;

public static class RenderProtocol
{
    public const int CurrentVersion = 3;
    public const int MinimumSupportedVersion = 2;
    public const int PipeProtocolVersion = 1;
    public const int MaxPipeFrameBytes = 256 * 1024 * 1024;
}

[ProtoContract]
public sealed class RenderPipeHandshake
{
    [ProtoMember(1)] public int ProtocolVersion { get; set; } = RenderProtocol.PipeProtocolVersion;
    [ProtoMember(2)] public string ClientId { get; set; } = string.Empty;
    [ProtoMember(3)] public string Token { get; set; } = string.Empty;
    [ProtoMember(4)] public bool Accepted { get; set; }
    [ProtoMember(5)] public string Error { get; set; } = string.Empty;
    [ProtoMember(6)] public RenderCapabilities? Capabilities { get; set; }
}

[ProtoContract]
public enum RenderOperation
{
    [ProtoEnum] Unknown = 0,
    [ProtoEnum] GetCapabilities = 1,
    [ProtoEnum] OpenProject = 2,
    [ProtoEnum] CloseProject = 3,
    [ProtoEnum] GetProjectSnapshot = 4,
    [ProtoEnum] GetTimeline = 5,
    [ProtoEnum] GetAssetMetadata = 6,
    [ProtoEnum] GetAvailableEffects = 7,
    [ProtoEnum] RenderTimelineFrame = 8,
    [ProtoEnum] RenderTimelineSegment = 9,
    [ProtoEnum] RenderClipPreview = 10,
    [ProtoEnum] RenderClipPreviewBatch = 11,
    [ProtoEnum] RenderProject = 12,
    [ProtoEnum] GetJobStatus = 13,
    [ProtoEnum] CancelJob = 14,
    [ProtoEnum] ReleaseArtifact = 15,
    [ProtoEnum] RenderAudioSegment = 16,
    [ProtoEnum] ListRenderJobs = 17,
    [ProtoEnum] OpenHeadlessProject = 100,
    [ProtoEnum] GetHeadlessProjectSnapshot = 101,
    [ProtoEnum] ReloadHeadlessProject = 102,
    [ProtoEnum] ApplyHeadlessProjectSnapshot = 103,
    [ProtoEnum] ListHeadlessClips = 104,
    [ProtoEnum] GetHeadlessClip = 105,
    [ProtoEnum] UpsertHeadlessClip = 106,
    [ProtoEnum] MoveHeadlessClip = 107,
    [ProtoEnum] PatchHeadlessClip = 108,
    [ProtoEnum] DeleteHeadlessClip = 109,
    [ProtoEnum] AddOrReplaceHeadlessEffect = 110,
    [ProtoEnum] RemoveHeadlessEffect = 111,
    [ProtoEnum] AddOrReplaceHeadlessEffectBundle = 112,
    [ProtoEnum] RemoveHeadlessEffectBundle = 113,
    [ProtoEnum] SaveHeadlessProject = 114,
}

[ProtoContract]
public enum RenderJobState
{
    [ProtoEnum] Unknown = 0,
    [ProtoEnum] Queued = 1,
    [ProtoEnum] Running = 2,
    [ProtoEnum] Completed = 3,
    [ProtoEnum] Failed = 4,
    [ProtoEnum] Canceled = 5,
}

[ProtoContract]
public enum RenderErrorCode
{
    [ProtoEnum] None = 0,
    [ProtoEnum] InvalidRequest = 1,
    [ProtoEnum] ProtocolMismatch = 2,
    [ProtoEnum] SessionNotFound = 3,
    [ProtoEnum] ClipNotFound = 4,
    [ProtoEnum] ArtifactNotFound = 5,
    [ProtoEnum] Unsupported = 6,
    [ProtoEnum] Canceled = 7,
    [ProtoEnum] BackendFailure = 8,
    [ProtoEnum] Unauthorized = 9,
    [ProtoEnum] ProjectNotFound = 10,
    [ProtoEnum] VersionConflict = 11,
    [ProtoEnum] HeadlessUnsupported = 12,
}

[ProtoContract]
public sealed class RemoteError
{
    [ProtoMember(1)] public RenderErrorCode Code { get; set; }
    [ProtoMember(2)] public string Message { get; set; } = string.Empty;
    [ProtoMember(3)] public string Details { get; set; } = string.Empty;
    [ProtoMember(4)] public bool Retryable { get; set; }
    [ProtoMember(5)] public bool HasException { get; set; }
    [ProtoMember(6)] public string ExceptionTypeFullName { get; set; } = string.Empty;
    [ProtoMember(7)] public string ExceptionMessage { get; set; } = string.Empty;
    [ProtoMember(8)] public string ExceptionStackTrace { get; set; } = string.Empty;

    public Exception AsException()
    {
        if (!JsonSerializer.IsReflectionEnabledByDefault || !HasException) return new RemoteRenderException(this);

        var type = Type.GetType(ExceptionTypeFullName);
        if (type != null)
        {
            var exception = (Exception?)Activator.CreateInstance(type, ExceptionMessage);
            if (exception != null)
            {
                exception.Data["OriginStackTrace"] = ExceptionStackTrace;
                exception.Data["RenderErrorCode"] = Code;
                exception.Data[nameof(Code)] = Code;
                exception.Data["RenderErrorDetails"] = Details;
                exception.Data[nameof(Details)] = Details;
                exception.Data["RemoteErrorInfo"] = Message;
                return exception;
            }
        }

        return new RemoteRenderException(this);
    }

    [DoesNotReturn]
    public void ThrowAsException()
    {
        throw AsException();
    }

    public RemoteError(Exception ex, RenderErrorCode code = RenderErrorCode.BackendFailure, bool retryable = false, string? customMessage = null, string? customDetails = null)
    {
        Code = code;
        Message = customMessage ?? ex.Message;
        Details = customDetails ?? ex.ToString();
        Retryable = retryable;
        HasException = true;
        ExceptionTypeFullName = ex.GetType().FullName ?? string.Empty;
        ExceptionMessage = ex.Message;
        ExceptionStackTrace = ex.StackTrace ?? string.Empty;
    }

    public RemoteError()
    {
    }
}

[ProtoContract]
public sealed class RenderRequestEnvelope
{
    [ProtoMember(1)] public int ProtocolVersion { get; set; } = RenderProtocol.CurrentVersion;
    [ProtoMember(2)] public Guid RequestId { get; set; } = Guid.NewGuid();
    [ProtoMember(3)] public string ClientId { get; set; } = string.Empty;
    [ProtoMember(4)] public RenderOperation Operation { get; set; }
    [ProtoMember(5)] public byte[] Payload { get; set; } = [];
}

[ProtoContract]
public sealed class RenderResponseEnvelope
{
    [ProtoMember(1)] public int ProtocolVersion { get; set; } = RenderProtocol.CurrentVersion;
    [ProtoMember(2)] public Guid RequestId { get; set; }
    [ProtoMember(3)] public byte[] Payload { get; set; } = [];
    [ProtoMember(4)] public RemoteError? Error { get; set; }
}

[ProtoContract]
public sealed class EmptyRequest
{
}

[ProtoContract]
public sealed class EmptyResponse
{
    [ProtoMember(1)] public bool Success { get; set; } = true;
}

[ProtoContract]
public sealed class RenderCapabilities
{
    [ProtoMember(1)] public int ProtocolVersion { get; set; }
    [ProtoMember(2)] public int MinimumProtocolVersion { get; set; }
    [ProtoMember(3)] public string BackendVersion { get; set; } = string.Empty;
    [ProtoMember(4)] public List<string> Operations { get; set; } = [];
    [ProtoMember(5)] public List<string> Encoders { get; set; } = [];
    [ProtoMember(6)] public List<string> Features { get; set; } = [];
}

[ProtoContract]
public sealed class AssetPathEntry
{
    [ProtoMember(1)] public string AssetId { get; set; } = string.Empty;
    [ProtoMember(2)] public string Path { get; set; } = string.Empty;
}

[ProtoContract]
public sealed class OpenProjectRequest
{
    [ProtoMember(1)] public Guid SessionId { get; set; }
    [ProtoMember(2)] public string ProjectRoot { get; set; } = string.Empty;
    [ProtoMember(3)] public string ProjectJson { get; set; } = string.Empty;
    [ProtoMember(4)] public string TimelineJson { get; set; } = string.Empty;
    [ProtoMember(5)] public string ProxyRoot { get; set; } = string.Empty;
    [ProtoMember(6)] public List<AssetPathEntry> Assets { get; set; } = [];
    [ProtoMember(7)] public int ProjectWidth { get; set; }
    [ProtoMember(8)] public int ProjectHeight { get; set; }
    [ProtoMember(9)] public int FrameRate { get; set; }
    [ProtoMember(10)] public string CacheNamespace { get; set; } = string.Empty;
}

[ProtoContract]
public sealed class RenderSession
{
    [ProtoMember(1)] public Guid SessionId { get; set; }
    [ProtoMember(2)] public string ProjectName { get; set; } = string.Empty;
    [ProtoMember(3)] public int ProjectWidth { get; set; }
    [ProtoMember(4)] public int ProjectHeight { get; set; }
    [ProtoMember(5)] public int FrameRate { get; set; }
    [ProtoMember(6)] public uint Duration { get; set; }
    [ProtoMember(7)] public int ClipCount { get; set; }
    [ProtoMember(8)] public string SnapshotHash { get; set; } = string.Empty;
    [ProtoMember(9)] public FrameHashIndex HashIndex { get; set; } = new();
}

[ProtoContract]
public sealed class FrameHashIndex
{
    [ProtoMember(1)] public string Version { get; set; } = string.Empty;
    [ProtoMember(2)] public string SnapshotHash { get; set; } = string.Empty;
    [ProtoMember(3)] public List<FrameHashEntry> FrameHashes { get; set; } = [];
    [ProtoMember(4)] public List<ClipFrameHashIndex> ClipHashes { get; set; } = [];
}

[ProtoContract]
public sealed class ClipFrameHashIndex
{
    [ProtoMember(1)] public Guid ClipId { get; set; }
    [ProtoMember(2)] public List<FrameHashEntry> FrameHashes { get; set; } = [];
}

[ProtoContract]
public sealed class FrameHashEntry
{
    [ProtoMember(1)] public uint FrameIndex { get; set; }
    [ProtoMember(2)] public string Hash { get; set; } = string.Empty;
}

[ProtoContract]
public sealed class SessionRequest
{
    [ProtoMember(1)] public Guid SessionId { get; set; }
}

[ProtoContract]
public sealed class ProjectSnapshot
{
    [ProtoMember(1)] public RenderSession Session { get; set; } = new();
    [ProtoMember(2)] public string ProjectJson { get; set; } = string.Empty;
    [ProtoMember(3)] public string TimelineJson { get; set; } = string.Empty;
}

[ProtoContract]
public sealed class TimelineSnapshot
{
    [ProtoMember(1)] public Guid SessionId { get; set; }
    [ProtoMember(2)] public uint Duration { get; set; }
    [ProtoMember(3)] public int ClipCount { get; set; }
    [ProtoMember(4)] public List<TimelineClip> Clips { get; set; } = [];
}

[ProtoContract]
public sealed class TimelineClip
{
    [ProtoMember(1)] public Guid ClipId { get; set; }
    [ProtoMember(2)] public string Name { get; set; } = string.Empty;
    [ProtoMember(3)] public string TypeName { get; set; } = string.Empty;
    [ProtoMember(4)] public uint LayerIndex { get; set; }
    [ProtoMember(5)] public uint SubLayerIndex { get; set; }
    [ProtoMember(6)] public uint StartFrame { get; set; }
    [ProtoMember(7)] public uint Duration { get; set; }
}

[ProtoContract]
public sealed class RenderArtifact
{
    [ProtoMember(1)] public Guid ArtifactId { get; set; } = Guid.NewGuid();
    [ProtoMember(2)] public Guid SessionId { get; set; }
    [ProtoMember(3)] public string ProjectRelativePath { get; set; } = string.Empty;
    [ProtoMember(4)] public string MediaType { get; set; } = "application/octet-stream";
    [ProtoMember(5)] public long Size { get; set; }
    [ProtoMember(6)] public string ContentHash { get; set; } = string.Empty;
    [ProtoMember(7)] public int Width { get; set; }
    [ProtoMember(8)] public int Height { get; set; }
    [ProtoMember(9)] public double FrameRate { get; set; }
    [ProtoMember(10)] public bool CacheHit { get; set; }
    [ProtoMember(11)] public bool IsPreview { get; set; }
    [ProtoMember(12)] public PreviewPixelFormat PixelFormat { get; set; } = PreviewPixelFormat.EncodedImage;
    [ProtoMember(13)] public int Stride { get; set; }
    [ProtoMember(14)] public string ColorSpace { get; set; } = string.Empty;
}

[ProtoContract]
public enum PreviewPixelFormat
{
    [ProtoEnum] EncodedImage = 0,
    [ProtoEnum] Rgba16FloatScRgb = 1,
}

[ProtoContract]
public sealed class TimelineFrameRequest
{
    [ProtoMember(1)] public Guid SessionId { get; set; }
    [ProtoMember(2)] public uint FrameIndex { get; set; }
    [ProtoMember(3)] public int Width { get; set; }
    [ProtoMember(4)] public int Height { get; set; }
    [ProtoMember(5)] public PreviewPixelFormat PreferredPixelFormat { get; set; } = PreviewPixelFormat.EncodedImage;
}

[ProtoContract]
public sealed class ClipPreviewRequest
{
    [ProtoMember(1)] public Guid SessionId { get; set; }
    [ProtoMember(2)] public Guid ClipId { get; set; }
    [ProtoMember(3)] public uint FrameIndex { get; set; }
    [ProtoMember(4)] public int CanvasWidth { get; set; }
    [ProtoMember(5)] public int CanvasHeight { get; set; }
    [ProtoMember(6)] public int ProjectWidth { get; set; }
    [ProtoMember(7)] public int ProjectHeight { get; set; }
    [ProtoMember(8)] public PreviewPixelFormat PreferredPixelFormat { get; set; } = PreviewPixelFormat.EncodedImage;
}

[ProtoContract]
public sealed class ClipPreviewBatchRequest
{
    [ProtoMember(1)] public List<ClipPreviewRequest> Requests { get; set; } = [];
}

[ProtoContract]
public sealed class ClipPreviewBatchResponse
{
    [ProtoMember(1)] public List<RenderArtifact> Artifacts { get; set; } = [];
    [ProtoMember(2)] public List<RemoteError> Errors { get; set; } = [];
}

[ProtoContract]
public sealed class TimelineSegmentRequest
{
    [ProtoMember(1)] public Guid SessionId { get; set; }
    [ProtoMember(2)] public uint StartFrame { get; set; }
    [ProtoMember(3)] public uint Length { get; set; }
    [ProtoMember(4)] public int Width { get; set; }
    [ProtoMember(5)] public int Height { get; set; }
    [ProtoMember(6)] public int FrameRate { get; set; }
    [ProtoMember(7)] public bool IncludeAudio { get; set; } = true;
    [ProtoMember(8)] public int AudioSampleRate { get; set; } = 96000;
    [ProtoMember(9)] public int AudioChannels { get; set; } = 2;
}

[ProtoContract]
public sealed class AudioSegmentRequest
{
    [ProtoMember(1)] public Guid SessionId { get; set; }
    [ProtoMember(2)] public uint StartFrame { get; set; }
    [ProtoMember(3)] public uint Length { get; set; }
    [ProtoMember(4)] public int FrameRate { get; set; }
    [ProtoMember(5)] public int SampleRate { get; set; } = 96000;
    [ProtoMember(6)] public int Channels { get; set; } = 2;
}

[ProtoContract]
public sealed class RenderProjectRequest
{
    [ProtoMember(1)] public Guid SessionId { get; set; }
    [ProtoMember(2)] public int Width { get; set; }
    [ProtoMember(3)] public int Height { get; set; }
    [ProtoMember(4)] public int FrameRate { get; set; }
    [ProtoMember(5)] public string Encoder { get; set; } = "libx264";
    [ProtoMember(6)] public string PixelFormat { get; set; } = "AV_PIX_FMT_YUV420P";
    [ProtoMember(7)] public bool IncludeAudio { get; set; } = true;
    [ProtoMember(8)] public string OutputFileName { get; set; } = "render.mp4";
    [ProtoMember(9)] public string ProjectRoot { get; set; } = string.Empty;
    [ProtoMember(10)] public string ProjectName { get; set; } = string.Empty;
    [ProtoMember(11)] public string OutputPath { get; set; } = string.Empty;
    [ProtoMember(12)] public bool Background { get; set; }
}

[ProtoContract]
public sealed class RenderJob
{
    [ProtoMember(1)] public Guid JobId { get; set; }
    [ProtoMember(2)] public Guid SessionId { get; set; }
    [ProtoMember(3)] public RenderJobState State { get; set; }
    [ProtoMember(4)] public double Progress { get; set; }
    [ProtoMember(5)] public long EstimatedRemainingTicks { get; set; }
    [ProtoMember(6)] public RenderArtifact? Artifact { get; set; }
    [ProtoMember(7)] public RemoteError? Error { get; set; }
    [ProtoMember(8)] public DateTime CreatedAtUtc { get; set; }
    [ProtoMember(9)] public DateTime UpdatedAtUtc { get; set; }
    [ProtoMember(10)] public string ProjectRoot { get; set; } = string.Empty;
    [ProtoMember(11)] public string ProjectName { get; set; } = string.Empty;
    [ProtoMember(12)] public string OutputPath { get; set; } = string.Empty;
    [ProtoMember(13)] public bool Background { get; set; }
    [ProtoMember(14)] public double CurrentFps { get; set; }
    [ProtoMember(15)] public string Stage { get; set; } = string.Empty;
}

[ProtoContract]
public sealed class ListRenderJobsRequest
{
    [ProtoMember(1)] public string ProjectRoot { get; set; } = string.Empty;
    [ProtoMember(2)] public bool IncludeCompleted { get; set; } = true;
}

[ProtoContract]
public sealed class JobRequest
{
    [ProtoMember(1)] public Guid JobId { get; set; }
}

[ProtoContract]
public sealed class ArtifactRequest
{
    [ProtoMember(1)] public Guid SessionId { get; set; }
    [ProtoMember(2)] public Guid ArtifactId { get; set; }
}

[ProtoContract]
public sealed class AssetMetadataRequest
{
    [ProtoMember(1)] public Guid SessionId { get; set; }
    [ProtoMember(2)] public string AssetId { get; set; } = string.Empty;
    [ProtoMember(3)] public string ProjectRelativePath { get; set; } = string.Empty;
}

[ProtoContract]
public sealed class AssetMetadata
{
    [ProtoMember(1)] public string AssetId { get; set; } = string.Empty;
    [ProtoMember(2)] public string MediaType { get; set; } = string.Empty;
    [ProtoMember(3)] public long Size { get; set; }
    [ProtoMember(4)] public int Width { get; set; }
    [ProtoMember(5)] public int Height { get; set; }
    [ProtoMember(6)] public double FrameRate { get; set; }
    [ProtoMember(7)] public long FrameCount { get; set; }
    [ProtoMember(8)] public string ContentHash { get; set; } = string.Empty;
}

[ProtoContract]
public sealed class EffectCatalogRequest
{
    [ProtoMember(1)] public Guid SessionId { get; set; }
}

[ProtoContract]
public sealed class EffectCatalog
{
    [ProtoMember(1)] public List<EffectDescriptor> Effects { get; set; } = [];
    [ProtoMember(2)] public List<PluginDescriptor> Plugins { get; set; } = [];
}

[ProtoContract]
public sealed class EffectDescriptor
{
    [ProtoMember(1)] public string TypeName { get; set; } = string.Empty;
    [ProtoMember(2)] public string Name { get; set; } = string.Empty;
    [ProtoMember(3)] public string PluginId { get; set; } = string.Empty;
    [ProtoMember(4)] public string EffectType { get; set; } = string.Empty;
    [ProtoMember(5)] public string Description { get; set; } = string.Empty;
}

[ProtoContract]
public sealed class PluginDescriptor
{
    [ProtoMember(1)] public string PluginId { get; set; } = string.Empty;
    [ProtoMember(2)] public string Name { get; set; } = string.Empty;
    [ProtoMember(3)] public string Version { get; set; } = string.Empty;
}

[ProtoContract]
public sealed class OpenHeadlessProjectRequest
{
    [ProtoMember(1)] public string ProjectRoot { get; set; } = string.Empty;
    [ProtoMember(2)] public Guid SessionId { get; set; }
}

[ProtoContract]
public sealed class HeadlessSessionRequest
{
    [ProtoMember(1)] public Guid SessionId { get; set; }
}

[ProtoContract]
public sealed class HeadlessMutationPrecondition
{
    [ProtoMember(1)] public Guid SessionId { get; set; }
    [ProtoMember(2)] public long BaseRevision { get; set; }
    [ProtoMember(3)] public string BaseSnapshotHash { get; set; } = string.Empty;
}

[ProtoContract]
public sealed class HeadlessProjectSnapshot
{
    [ProtoMember(1)] public Guid SessionId { get; set; }
    [ProtoMember(2)] public string ProjectRoot { get; set; } = string.Empty;
    [ProtoMember(3)] public long Revision { get; set; }
    [ProtoMember(4)] public string SnapshotHash { get; set; } = string.Empty;
    [ProtoMember(5)] public string ProjectJson { get; set; } = string.Empty;
    [ProtoMember(6)] public string TimelineJson { get; set; } = string.Empty;
    [ProtoMember(7)] public string AssetsJson { get; set; } = string.Empty;
    [ProtoMember(8)] public RenderSession RenderSession { get; set; } = new();
}

[ProtoContract]
public sealed class ApplyHeadlessProjectSnapshotRequest
{
    [ProtoMember(1)] public HeadlessMutationPrecondition Precondition { get; set; } = new();
    [ProtoMember(2)] public string ProjectJson { get; set; } = string.Empty;
    [ProtoMember(3)] public string TimelineJson { get; set; } = string.Empty;
    [ProtoMember(4)] public string AssetsJson { get; set; } = string.Empty;
}

[ProtoContract]
public sealed class HeadlessClipRequest
{
    [ProtoMember(1)] public Guid SessionId { get; set; }
    [ProtoMember(2)] public string ClipId { get; set; } = string.Empty;
}

[ProtoContract]
public sealed class HeadlessClipMutationRequest
{
    [ProtoMember(1)] public HeadlessMutationPrecondition Precondition { get; set; } = new();
    [ProtoMember(2)] public string ClipId { get; set; } = string.Empty;
    [ProtoMember(3)] public string Json { get; set; } = string.Empty;
}

[ProtoContract]
public sealed class MoveHeadlessClipRequest
{
    [ProtoMember(1)] public HeadlessMutationPrecondition Precondition { get; set; } = new();
    [ProtoMember(2)] public string ClipId { get; set; } = string.Empty;
    [ProtoMember(3)] public uint LayerIndex { get; set; }
    [ProtoMember(4)] public uint StartFrame { get; set; }
    [ProtoMember(5)] public uint SubLayerIndex { get; set; }
    [ProtoMember(6)] public bool HasSubLayerIndex { get; set; }
}

[ProtoContract]
public sealed class RemoveHeadlessEffectRequest
{
    [ProtoMember(1)] public HeadlessMutationPrecondition Precondition { get; set; } = new();
    [ProtoMember(2)] public string ClipId { get; set; } = string.Empty;
    [ProtoMember(3)] public string EffectKey { get; set; } = string.Empty;
}

[ProtoContract]
public sealed class RemoveHeadlessEffectBundleRequest
{
    [ProtoMember(1)] public HeadlessMutationPrecondition Precondition { get; set; } = new();
    [ProtoMember(2)] public string ClipId { get; set; } = string.Empty;
    [ProtoMember(3)] public Guid BundleId { get; set; }
}

[ProtoContract]
public sealed class HeadlessSaveProjectRequest
{
    [ProtoMember(1)] public HeadlessMutationPrecondition Precondition { get; set; } = new();
    [ProtoMember(2)] public string ChangeReason { get; set; } = string.Empty;
}

[ProtoContract]
public sealed class HeadlessJsonResponse
{
    [ProtoMember(1)] public HeadlessProjectSnapshot Snapshot { get; set; } = new();
    [ProtoMember(2)] public string Json { get; set; } = string.Empty;
    [ProtoMember(3)] public bool Changed { get; set; }
}


public class RemoteRenderException : Exception
{
    public RemoteRenderException(RemoteError error) : base(error.Message)
    {
        ErrorCode = error.Code;
        Details = error.Details;
        Data["RenderErrorCode"] = error.Code;
        Data[nameof(RemoteError.Code)] = error.Code;
        Data["RenderErrorDetails"] = error.Details;
        Data[nameof(RemoteError.Details)] = error.Details;
        Data[nameof(RemoteError.Retryable)] = error.Retryable;
    }

    public RenderErrorCode ErrorCode { get; }
    public string Details { get; }
}
