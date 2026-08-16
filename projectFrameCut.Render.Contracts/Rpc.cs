using ProtoBuf;
using System.Diagnostics;

namespace projectFrameCut.Render.Contracts;

public interface IRenderService
{
    ValueTask<RenderResponseEnvelope> DispatchAsync(RenderRequestEnvelope request, CancellationToken cancellationToken = default);
}

public interface IRenderTransport : IAsyncDisposable
{
    ValueTask<RenderResponseEnvelope> SendAsync(RenderRequestEnvelope request, CancellationToken cancellationToken = default);
}

public interface IRenderClient : IAsyncDisposable
{
    string ClientId { get; }
    ValueTask<RenderCapabilities> GetCapabilitiesAsync(CancellationToken cancellationToken = default);
    ValueTask<RenderSession> OpenProjectAsync(OpenProjectRequest request, CancellationToken cancellationToken = default);
    ValueTask CloseProjectAsync(Guid sessionId, CancellationToken cancellationToken = default);
    ValueTask<ProjectSnapshot> GetProjectSnapshotAsync(Guid sessionId, CancellationToken cancellationToken = default);
    ValueTask<TimelineSnapshot> GetTimelineAsync(Guid sessionId, CancellationToken cancellationToken = default);
    ValueTask<AssetMetadata> GetAssetMetadataAsync(AssetMetadataRequest request, CancellationToken cancellationToken = default);
    ValueTask<EffectCatalog> GetAvailableEffectsAsync(Guid sessionId, CancellationToken cancellationToken = default);
    ValueTask<RenderArtifact> RenderTimelineFrameAsync(TimelineFrameRequest request, CancellationToken cancellationToken = default);
    ValueTask<RenderArtifact> RenderTimelineSegmentAsync(TimelineSegmentRequest request, CancellationToken cancellationToken = default);
    ValueTask<RenderArtifact> RenderAudioSegmentAsync(AudioSegmentRequest request, CancellationToken cancellationToken = default);
    ValueTask<RenderArtifact> RenderClipPreviewAsync(ClipPreviewRequest request, CancellationToken cancellationToken = default);
    ValueTask<ClipPreviewBatchResponse> RenderClipPreviewBatchAsync(ClipPreviewBatchRequest request, CancellationToken cancellationToken = default);
    ValueTask<RenderJob> RenderProjectAsync(RenderProjectRequest request, CancellationToken cancellationToken = default);
    ValueTask<RenderJob> GetJobStatusAsync(Guid jobId, CancellationToken cancellationToken = default);
    ValueTask<RenderJob> CancelJobAsync(Guid jobId, CancellationToken cancellationToken = default);
    ValueTask ReleaseArtifactAsync(ArtifactRequest request, CancellationToken cancellationToken = default);
}

public sealed class DirectRenderTransport(IRenderService service) : IRenderTransport
{
    private readonly IRenderService _service = service ?? throw new ArgumentNullException(nameof(service));

    public async ValueTask<RenderResponseEnvelope> SendAsync(RenderRequestEnvelope request, CancellationToken cancellationToken = default)
    {
        // Serialize at the direct boundary as well. This prevents accidental sharing of backend
        // object references and keeps direct and future network transports behaviorally equivalent.
        var isolatedRequest = RenderRpcSerializer.Clone(request);
        // The direct server must not inherit the UI caller's synchronization context. A future
        // network transport naturally has this boundary; Task.Run gives the in-process host the
        // same scheduling behavior and keeps decode/render work away from the UI thread.
        var response = await Task.Run(
            async () => 
            await _service.DispatchAsync(isolatedRequest, cancellationToken).ConfigureAwait(false),
            cancellationToken).ConfigureAwait(false);
        return RenderRpcSerializer.Clone(response);
    }

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;
}

public sealed class RenderClient(IRenderTransport transport, string? clientId = null) : IRenderClient
{
    private readonly IRenderTransport _transport = transport ?? throw new ArgumentNullException(nameof(transport));
    public string ClientId { get; } = string.IsNullOrWhiteSpace(clientId) ? $"client-{Guid.NewGuid():N}" : clientId;

    public ValueTask<RenderCapabilities> GetCapabilitiesAsync(CancellationToken ct = default) => SendAsync<EmptyRequest, RenderCapabilities>(RenderOperation.GetCapabilities, new(), ct);
    public ValueTask<RenderSession> OpenProjectAsync(OpenProjectRequest request, CancellationToken ct = default) => SendAsync<OpenProjectRequest, RenderSession>(RenderOperation.OpenProject, request, ct);
    public async ValueTask CloseProjectAsync(Guid sessionId, CancellationToken ct = default)
    {
        _ = await SendAsync<SessionRequest, EmptyResponse>(RenderOperation.CloseProject, new() { SessionId = sessionId }, ct).ConfigureAwait(false);
    }
    public ValueTask<ProjectSnapshot> GetProjectSnapshotAsync(Guid sessionId, CancellationToken ct = default) => SendAsync<SessionRequest, ProjectSnapshot>(RenderOperation.GetProjectSnapshot, new() { SessionId = sessionId }, ct);
    public ValueTask<TimelineSnapshot> GetTimelineAsync(Guid sessionId, CancellationToken ct = default) => SendAsync<SessionRequest, TimelineSnapshot>(RenderOperation.GetTimeline, new() { SessionId = sessionId }, ct);
    public ValueTask<AssetMetadata> GetAssetMetadataAsync(AssetMetadataRequest request, CancellationToken ct = default) => SendAsync<AssetMetadataRequest, AssetMetadata>(RenderOperation.GetAssetMetadata, request, ct);
    public ValueTask<EffectCatalog> GetAvailableEffectsAsync(Guid sessionId, CancellationToken ct = default) => SendAsync<EffectCatalogRequest, EffectCatalog>(RenderOperation.GetAvailableEffects, new() { SessionId = sessionId }, ct);
    public ValueTask<RenderArtifact> RenderTimelineFrameAsync(TimelineFrameRequest request, CancellationToken ct = default) => SendAsync<TimelineFrameRequest, RenderArtifact>(RenderOperation.RenderTimelineFrame, request, ct);
    public ValueTask<RenderArtifact> RenderTimelineSegmentAsync(TimelineSegmentRequest request, CancellationToken ct = default) => SendAsync<TimelineSegmentRequest, RenderArtifact>(RenderOperation.RenderTimelineSegment, request, ct);
    public ValueTask<RenderArtifact> RenderAudioSegmentAsync(AudioSegmentRequest request, CancellationToken ct = default) => SendAsync<AudioSegmentRequest, RenderArtifact>(RenderOperation.RenderAudioSegment, request, ct);
    public ValueTask<RenderArtifact> RenderClipPreviewAsync(ClipPreviewRequest request, CancellationToken ct = default) => SendAsync<ClipPreviewRequest, RenderArtifact>(RenderOperation.RenderClipPreview, request, ct);
    public ValueTask<ClipPreviewBatchResponse> RenderClipPreviewBatchAsync(ClipPreviewBatchRequest request, CancellationToken ct = default) => SendAsync<ClipPreviewBatchRequest, ClipPreviewBatchResponse>(RenderOperation.RenderClipPreviewBatch, request, ct);
    public ValueTask<RenderJob> RenderProjectAsync(RenderProjectRequest request, CancellationToken ct = default) => SendAsync<RenderProjectRequest, RenderJob>(RenderOperation.RenderProject, request, ct);
    public ValueTask<RenderJob> GetJobStatusAsync(Guid jobId, CancellationToken ct = default) => SendAsync<JobRequest, RenderJob>(RenderOperation.GetJobStatus, new() { JobId = jobId }, ct);
    public ValueTask<RenderJob> CancelJobAsync(Guid jobId, CancellationToken ct = default) => SendAsync<JobRequest, RenderJob>(RenderOperation.CancelJob, new() { JobId = jobId }, ct);
    public async ValueTask ReleaseArtifactAsync(ArtifactRequest request, CancellationToken ct = default)
    {
        _ = await SendAsync<ArtifactRequest, EmptyResponse>(RenderOperation.ReleaseArtifact, request, ct).ConfigureAwait(false);
    }

    private async ValueTask<TResponse> SendAsync<TRequest, TResponse>(RenderOperation operation, TRequest request, CancellationToken cancellationToken)
    {
        var requestId = Guid.NewGuid();
        var response = await _transport.SendAsync(new RenderRequestEnvelope
        {
            RequestId = requestId,
            ClientId = ClientId,
            Operation = operation,
            Payload = RenderRpcSerializer.Serialize(request),
        }, cancellationToken).ConfigureAwait(false);

        if (response.RequestId != requestId)
        {
            throw new RenderRpcException(new RenderError { Code = RenderErrorCode.BackendFailure, Message = "Render RPC response request ID mismatch." });
        }

        if (response.Error is not null)
        {
            throw new RenderRpcException(response.Error);
        }

        return RenderRpcSerializer.Deserialize<TResponse>(response.Payload);
    }

    public ValueTask DisposeAsync() => _transport.DisposeAsync();
}

public sealed class RenderRpcException : Exception
{
    public RenderRpcException(RenderError error)
        : base(string.IsNullOrWhiteSpace(error.Details) ? error.Message : $"{error.Message}{Environment.NewLine}{error.Details}")
    {
        Error = error;
        Data[nameof(RenderError.Code)] = error.Code;
        Data[nameof(RenderError.Retryable)] = error.Retryable;
        if (!string.IsNullOrWhiteSpace(error.Details)) Data[nameof(RenderError.Details)] = error.Details;
    }

    public RenderError Error { get; }
}

public sealed class RenderPipeException : IOException
{
    public RenderPipeException(string message) : base(message) { }
    public RenderPipeException(string message, Exception innerException) : base(message, innerException) { }
}

[DebuggerNonUserCode]
public static class RenderRpcSerializer
{
    public static byte[] Serialize<T>(T value)
    {
        using var stream = new MemoryStream();
        Serializer.Serialize(stream, value);
        return stream.ToArray();
    }

    public static T Deserialize<T>(byte[] payload)
    {
        using var stream = new MemoryStream(payload, writable: false);
        return Serializer.Deserialize<T>(stream);
    }

    public static T Clone<T>(T value) => Deserialize<T>(Serialize(value));
}
