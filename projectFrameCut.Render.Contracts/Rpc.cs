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

public interface IRenderDuplexTransport : IRenderTransport
{
    IRenderService? CallbackService { get; set; }
}

public interface IExternalVideoSourceProvider
{
    IReadOnlyList<ExternalVideoSourceDescriptor> Sources { get; }
    ValueTask<ExternalVideoSourceInstance> CreateAsync(ExternalVideoSourceCreateRequest request, CancellationToken cancellationToken = default);
    ValueTask<ExternalVideoSourceInstance> InitializeAsync(ExternalVideoSourceStateRequest request, CancellationToken cancellationToken = default);
    ValueTask<ExternalVideoFrame> ReadFrameAsync(ExternalVideoSourceReadRequest request, CancellationToken cancellationToken = default);
    ValueTask ReleaseAsync(ExternalVideoSourceStateRequest request, CancellationToken cancellationToken = default);
}

public interface IRenderClient : IAsyncDisposable
{
    string ClientId { get; }
    ValueTask<GuiProjectSession> RegisterGuiProjectAsync(GuiProjectSession request, CancellationToken cancellationToken = default);
    ValueTask<EmptyResponse> UnregisterGuiProjectAsync(GuiProjectSession request, CancellationToken cancellationToken = default);
    ValueTask<GuiProjectWork> GetGuiProjectWorkAsync(GuiProjectSession request, CancellationToken cancellationToken = default);
    ValueTask<EmptyResponse> CompleteGuiProjectWorkAsync(GuiProjectResult request, CancellationToken cancellationToken = default);
    ValueTask<GuiProjectResult> InvokeGuiProjectAsync(GuiProjectRequest request, CancellationToken cancellationToken = default);
    ValueTask<GuiProjectSession> GetGuiProjectSessionAsync(EmptyRequest request, CancellationToken cancellationToken = default);
    ValueTask<CreateAdditionalPipeResponse> CreateGuiProjectPipeAsync(GuiProjectSession request, CancellationToken cancellationToken = default);
    ValueTask<ProjectHistory> GetProjectHistoryAsync(ProjectHistoryRequest request, CancellationToken cancellationToken = default);
    ValueTask<ProjectHistoryState> UndoProjectHistoryAsync(ProjectHistoryRequest request, CancellationToken cancellationToken = default);
    ValueTask<ProjectHistoryState> RedoProjectHistoryAsync(ProjectHistoryRequest request, CancellationToken cancellationToken = default);
    ValueTask<ProjectHistoryState> RestoreProjectHistoryAsync(RestoreProjectHistoryRequest request, CancellationToken cancellationToken = default);
    ValueTask<CreateAdditionalPipeResponse> CreateAdditionalPipeAsync(CancellationToken cancellationToken = default);
    ValueTask<PendingExternalRpcRequest> GetExternalRpcRequestAsync(CancellationToken cancellationToken = default);
    ValueTask ResolveExternalRpcRequestAsync(ResolveExternalRpcRequest request, CancellationToken cancellationToken = default);
    ValueTask RegisterExternalVideoSourcesAsync(RegisterExternalVideoSourcesRequest request, CancellationToken cancellationToken = default);
    ValueTask UnregisterExternalVideoSourcesAsync(CancellationToken cancellationToken = default);
    ValueTask<ExternalVideoSourceCatalog> ListExternalVideoSourcesAsync(CancellationToken cancellationToken = default);
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
    ValueTask<List<RenderJob>> ListRenderJobsAsync(ListRenderJobsRequest request, CancellationToken cancellationToken = default);
    ValueTask ReleaseArtifactAsync(ArtifactRequest request, CancellationToken cancellationToken = default);
    ValueTask<HeadlessProjectSnapshot> OpenHeadlessProjectAsync(OpenHeadlessProjectRequest request, CancellationToken cancellationToken = default);
    ValueTask<HeadlessProjectSnapshot> GetHeadlessProjectSnapshotAsync(Guid sessionId, CancellationToken cancellationToken = default);
    ValueTask<HeadlessProjectSnapshot> ReloadHeadlessProjectAsync(HeadlessMutationPrecondition request, CancellationToken cancellationToken = default);
    ValueTask<HeadlessProjectSnapshot> ApplyHeadlessProjectSnapshotAsync(ApplyHeadlessProjectSnapshotRequest request, CancellationToken cancellationToken = default);
    ValueTask<HeadlessJsonResponse> ListHeadlessClipsAsync(Guid sessionId, CancellationToken cancellationToken = default);
    ValueTask<HeadlessJsonResponse> GetHeadlessClipAsync(HeadlessClipRequest request, CancellationToken cancellationToken = default);
    ValueTask<HeadlessJsonResponse> UpsertHeadlessClipAsync(HeadlessClipMutationRequest request, CancellationToken cancellationToken = default);
    ValueTask<HeadlessJsonResponse> MoveHeadlessClipAsync(MoveHeadlessClipRequest request, CancellationToken cancellationToken = default);
    ValueTask<HeadlessJsonResponse> PatchHeadlessClipAsync(HeadlessClipMutationRequest request, CancellationToken cancellationToken = default);
    ValueTask<HeadlessJsonResponse> DeleteHeadlessClipAsync(HeadlessClipMutationRequest request, CancellationToken cancellationToken = default);
    ValueTask<HeadlessJsonResponse> AddOrReplaceHeadlessEffectAsync(HeadlessClipMutationRequest request, CancellationToken cancellationToken = default);
    ValueTask<HeadlessJsonResponse> RemoveHeadlessEffectAsync(RemoveHeadlessEffectRequest request, CancellationToken cancellationToken = default);
    ValueTask<HeadlessJsonResponse> AddOrReplaceHeadlessEffectProviderAsync(HeadlessClipMutationRequest request, CancellationToken cancellationToken = default);
    ValueTask<HeadlessJsonResponse> RemoveHeadlessEffectProviderAsync(RemoveHeadlessEffectProviderRequest request, CancellationToken cancellationToken = default);
    ValueTask<HeadlessProjectSnapshot> SaveHeadlessProjectAsync(HeadlessSaveProjectRequest request, CancellationToken cancellationToken = default);
    ValueTask<HeadlessJsonResponse> ApplyHeadlessProjectEditAsync(HeadlessProjectEditRequest request, CancellationToken cancellationToken = default);
}

public sealed class DirectRenderTransport(IRenderService service) : IRenderTransport
{
    private readonly IRenderService _service = service ?? throw new ArgumentNullException(nameof(service));

    public async ValueTask<RenderResponseEnvelope> SendAsync(RenderRequestEnvelope request, CancellationToken cancellationToken = default)
    {
        var isolatedRequest = RenderRpcSerializer.Clone(request);
        var response = await Task.Run(async () => await _service.DispatchAsync(isolatedRequest, cancellationToken).ConfigureAwait(false), cancellationToken).ConfigureAwait(false);
        return RenderRpcSerializer.Clone(response);
    }

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;
}

public sealed class RenderClient : IRenderClient
{
    private readonly IRenderTransport _transport;
    private readonly ExternalVideoSourceCallbackService? _callbackService;

    public RenderClient(IRenderTransport transport, string? clientId = null, IExternalVideoSourceProvider? externalVideoSourceProvider = null)
    {
        _transport = transport ?? throw new ArgumentNullException(nameof(transport));
        ClientId = string.IsNullOrWhiteSpace(clientId) ? $"client-{Guid.NewGuid():N}" : clientId;
        if (externalVideoSourceProvider is not null)
        {
            if (transport is not IRenderDuplexTransport duplex)
                throw new NotSupportedException("External video sources require a duplex render transport.");
            _callbackService = new(externalVideoSourceProvider);
            duplex.CallbackService = _callbackService;
        }
    }

    public ValueTask<GuiProjectSession> RegisterGuiProjectAsync(GuiProjectSession request, CancellationToken ct = default) => SendAsync<GuiProjectSession, GuiProjectSession>(RenderOperation.RegisterGuiProject, request, ct);
    public ValueTask<EmptyResponse> UnregisterGuiProjectAsync(GuiProjectSession request, CancellationToken ct = default) => SendAsync<GuiProjectSession, EmptyResponse>(RenderOperation.UnregisterGuiProject, request, ct);
    public ValueTask<GuiProjectWork> GetGuiProjectWorkAsync(GuiProjectSession request, CancellationToken ct = default) => SendAsync<GuiProjectSession, GuiProjectWork>(RenderOperation.GetGuiProjectWork, request, ct);
    public ValueTask<EmptyResponse> CompleteGuiProjectWorkAsync(GuiProjectResult request, CancellationToken ct = default) => SendAsync<GuiProjectResult, EmptyResponse>(RenderOperation.CompleteGuiProjectWork, request, ct);
    public ValueTask<GuiProjectResult> InvokeGuiProjectAsync(GuiProjectRequest request, CancellationToken ct = default) => SendAsync<GuiProjectRequest, GuiProjectResult>(RenderOperation.InvokeGuiProject, request, ct);
    public ValueTask<GuiProjectSession> GetGuiProjectSessionAsync(EmptyRequest request, CancellationToken ct = default) => SendAsync<EmptyRequest, GuiProjectSession>(RenderOperation.GetGuiProjectSession, request, ct);
    public ValueTask<CreateAdditionalPipeResponse> CreateGuiProjectPipeAsync(GuiProjectSession request, CancellationToken ct = default) => SendAsync<GuiProjectSession, CreateAdditionalPipeResponse>(RenderOperation.CreateGuiProjectPipe, request, ct);
    public ValueTask<ProjectHistory> GetProjectHistoryAsync(ProjectHistoryRequest request, CancellationToken ct = default) => SendAsync<ProjectHistoryRequest, ProjectHistory>(RenderOperation.GetProjectHistory, request, ct);
    public ValueTask<ProjectHistoryState> UndoProjectHistoryAsync(ProjectHistoryRequest request, CancellationToken ct = default) => SendAsync<ProjectHistoryRequest, ProjectHistoryState>(RenderOperation.UndoProjectHistory, request, ct);
    public ValueTask<ProjectHistoryState> RedoProjectHistoryAsync(ProjectHistoryRequest request, CancellationToken ct = default) => SendAsync<ProjectHistoryRequest, ProjectHistoryState>(RenderOperation.RedoProjectHistory, request, ct);
    public ValueTask<ProjectHistoryState> RestoreProjectHistoryAsync(RestoreProjectHistoryRequest request, CancellationToken ct = default) => SendAsync<RestoreProjectHistoryRequest, ProjectHistoryState>(RenderOperation.RestoreProjectHistory, request, ct);
    public ValueTask<CreateAdditionalPipeResponse> CreateAdditionalPipeAsync(CancellationToken ct = default) => SendAsync<EmptyRequest, CreateAdditionalPipeResponse>(RenderOperation.CreateAdditionalPipe, new(), ct);
    public ValueTask<PendingExternalRpcRequest> GetExternalRpcRequestAsync(CancellationToken ct = default) => SendAsync<EmptyRequest, PendingExternalRpcRequest>(RenderOperation.GetExternalRpcRequest, new(), ct);
    public async ValueTask ResolveExternalRpcRequestAsync(ResolveExternalRpcRequest request, CancellationToken ct = default) => _ = await SendAsync<ResolveExternalRpcRequest, EmptyResponse>(RenderOperation.ResolveExternalRpcRequest, request, ct).ConfigureAwait(false);
    public async ValueTask RegisterExternalVideoSourcesAsync(RegisterExternalVideoSourcesRequest request, CancellationToken ct = default) => _ = await SendAsync<RegisterExternalVideoSourcesRequest, EmptyResponse>(RenderOperation.RegisterExternalVideoSources, request, ct).ConfigureAwait(false);
    public async ValueTask UnregisterExternalVideoSourcesAsync(CancellationToken ct = default) => _ = await SendAsync<EmptyRequest, EmptyResponse>(RenderOperation.UnregisterExternalVideoSources, new(), ct).ConfigureAwait(false);
    public ValueTask<ExternalVideoSourceCatalog> ListExternalVideoSourcesAsync(CancellationToken ct = default) => SendAsync<EmptyRequest, ExternalVideoSourceCatalog>(RenderOperation.ListExternalVideoSources, new(), ct);
    public string ClientId { get; }

    public ValueTask<RenderCapabilities> GetCapabilitiesAsync(CancellationToken ct = default) => SendAsync<EmptyRequest, RenderCapabilities>(RenderOperation.GetCapabilities, new(), ct);
    public ValueTask<RenderSession> OpenProjectAsync(OpenProjectRequest request, CancellationToken ct = default) => SendAsync<OpenProjectRequest, RenderSession>(RenderOperation.OpenProject, request, ct);
    public async ValueTask CloseProjectAsync(Guid sessionId, CancellationToken ct = default) { _ = await SendAsync<SessionRequest, EmptyResponse>(RenderOperation.CloseProject, new() { SessionId = sessionId }, ct).ConfigureAwait(false); }
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
    public ValueTask<List<RenderJob>> ListRenderJobsAsync(ListRenderJobsRequest request, CancellationToken ct = default) => SendAsync<ListRenderJobsRequest, List<RenderJob>>(RenderOperation.ListRenderJobs, request, ct);
    public async ValueTask ReleaseArtifactAsync(ArtifactRequest request, CancellationToken ct = default) { _ = await SendAsync<ArtifactRequest, EmptyResponse>(RenderOperation.ReleaseArtifact, request, ct).ConfigureAwait(false); }
    public ValueTask<HeadlessProjectSnapshot> OpenHeadlessProjectAsync(OpenHeadlessProjectRequest request, CancellationToken ct = default) => SendAsync<OpenHeadlessProjectRequest, HeadlessProjectSnapshot>(RenderOperation.OpenHeadlessProject, request, ct);
    public ValueTask<HeadlessProjectSnapshot> GetHeadlessProjectSnapshotAsync(Guid sessionId, CancellationToken ct = default) => SendAsync<HeadlessSessionRequest, HeadlessProjectSnapshot>(RenderOperation.GetHeadlessProjectSnapshot, new() { SessionId = sessionId }, ct);
    public ValueTask<HeadlessProjectSnapshot> ReloadHeadlessProjectAsync(HeadlessMutationPrecondition request, CancellationToken ct = default) => SendAsync<HeadlessMutationPrecondition, HeadlessProjectSnapshot>(RenderOperation.ReloadHeadlessProject, request, ct);
    public ValueTask<HeadlessProjectSnapshot> ApplyHeadlessProjectSnapshotAsync(ApplyHeadlessProjectSnapshotRequest request, CancellationToken ct = default) => SendAsync<ApplyHeadlessProjectSnapshotRequest, HeadlessProjectSnapshot>(RenderOperation.ApplyHeadlessProjectSnapshot, request, ct);
    public ValueTask<HeadlessJsonResponse> ListHeadlessClipsAsync(Guid sessionId, CancellationToken ct = default) => SendAsync<HeadlessSessionRequest, HeadlessJsonResponse>(RenderOperation.ListHeadlessClips, new() { SessionId = sessionId }, ct);
    public ValueTask<HeadlessJsonResponse> GetHeadlessClipAsync(HeadlessClipRequest request, CancellationToken ct = default) => SendAsync<HeadlessClipRequest, HeadlessJsonResponse>(RenderOperation.GetHeadlessClip, request, ct);
    public ValueTask<HeadlessJsonResponse> UpsertHeadlessClipAsync(HeadlessClipMutationRequest request, CancellationToken ct = default) => SendAsync<HeadlessClipMutationRequest, HeadlessJsonResponse>(RenderOperation.UpsertHeadlessClip, request, ct);
    public ValueTask<HeadlessJsonResponse> MoveHeadlessClipAsync(MoveHeadlessClipRequest request, CancellationToken ct = default) => SendAsync<MoveHeadlessClipRequest, HeadlessJsonResponse>(RenderOperation.MoveHeadlessClip, request, ct);
    public ValueTask<HeadlessJsonResponse> PatchHeadlessClipAsync(HeadlessClipMutationRequest request, CancellationToken ct = default) => SendAsync<HeadlessClipMutationRequest, HeadlessJsonResponse>(RenderOperation.PatchHeadlessClip, request, ct);
    public ValueTask<HeadlessJsonResponse> DeleteHeadlessClipAsync(HeadlessClipMutationRequest request, CancellationToken ct = default) => SendAsync<HeadlessClipMutationRequest, HeadlessJsonResponse>(RenderOperation.DeleteHeadlessClip, request, ct);
    public ValueTask<HeadlessJsonResponse> AddOrReplaceHeadlessEffectAsync(HeadlessClipMutationRequest request, CancellationToken ct = default) => SendAsync<HeadlessClipMutationRequest, HeadlessJsonResponse>(RenderOperation.AddOrReplaceHeadlessEffect, request, ct);
    public ValueTask<HeadlessJsonResponse> RemoveHeadlessEffectAsync(RemoveHeadlessEffectRequest request, CancellationToken ct = default) => SendAsync<RemoveHeadlessEffectRequest, HeadlessJsonResponse>(RenderOperation.RemoveHeadlessEffect, request, ct);
    public ValueTask<HeadlessJsonResponse> AddOrReplaceHeadlessEffectProviderAsync(HeadlessClipMutationRequest request, CancellationToken ct = default) => SendAsync<HeadlessClipMutationRequest, HeadlessJsonResponse>(RenderOperation.AddOrReplaceHeadlessEffectProvider, request, ct);
    public ValueTask<HeadlessJsonResponse> RemoveHeadlessEffectProviderAsync(RemoveHeadlessEffectProviderRequest request, CancellationToken ct = default) => SendAsync<RemoveHeadlessEffectProviderRequest, HeadlessJsonResponse>(RenderOperation.RemoveHeadlessEffectProvider, request, ct);
    public ValueTask<HeadlessProjectSnapshot> SaveHeadlessProjectAsync(HeadlessSaveProjectRequest request, CancellationToken ct = default) => SendAsync<HeadlessSaveProjectRequest, HeadlessProjectSnapshot>(RenderOperation.SaveHeadlessProject, request, ct);
    public ValueTask<HeadlessJsonResponse> ApplyHeadlessProjectEditAsync(HeadlessProjectEditRequest request, CancellationToken ct = default) => SendAsync<HeadlessProjectEditRequest, HeadlessJsonResponse>(RenderOperation.ApplyHeadlessProjectEdit, request, ct);

    private async ValueTask<TResponse> SendAsync<TRequest, TResponse>(RenderOperation operation, TRequest request, CancellationToken cancellationToken)
    {
        var requestId = Guid.NewGuid();
        var response = await _transport.SendAsync(new RenderRequestEnvelope { RequestId = requestId, ClientId = ClientId, Operation = operation, Payload = RenderRpcSerializer.Serialize(request) }, cancellationToken).ConfigureAwait(false);
        bool unauthenticatedHttpResponse = response.RequestId == Guid.Empty && response.Error?.Code == RenderErrorCode.Unauthorized;
        if (response.RequestId != requestId && !unauthenticatedHttpResponse)
            throw new RenderRpcException(new RemoteError { Code = RenderErrorCode.BackendFailure, Message = "Render RPC response request ID mismatch." });
        if (response.Error is not null) response.Error.ThrowAsException();
        return RenderRpcSerializer.Deserialize<TResponse>(response.Payload);
    }

    public ValueTask DisposeAsync() => _transport.DisposeAsync();

    private sealed class ExternalVideoSourceCallbackService(IExternalVideoSourceProvider provider) : IRenderService
    {
        public async ValueTask<RenderResponseEnvelope> DispatchAsync(RenderRequestEnvelope request, CancellationToken cancellationToken = default)
        {
            try
            {
                byte[] payload = request.Operation switch
                {
                    RenderOperation.ExternalVideoSourceCreate => RenderRpcSerializer.Serialize(await provider.CreateAsync(RenderRpcSerializer.Deserialize<ExternalVideoSourceCreateRequest>(request.Payload), cancellationToken).ConfigureAwait(false)),
                    RenderOperation.ExternalVideoSourceInitialize => RenderRpcSerializer.Serialize(await provider.InitializeAsync(RenderRpcSerializer.Deserialize<ExternalVideoSourceStateRequest>(request.Payload), cancellationToken).ConfigureAwait(false)),
                    RenderOperation.ExternalVideoSourceReadFrame => RenderRpcSerializer.Serialize(await provider.ReadFrameAsync(RenderRpcSerializer.Deserialize<ExternalVideoSourceReadRequest>(request.Payload), cancellationToken).ConfigureAwait(false)),
                    RenderOperation.ExternalVideoSourceRelease => await ReleaseAsync(request, cancellationToken).ConfigureAwait(false),
                    _ => throw new NotSupportedException($"Render callback operation '{request.Operation}' is not supported."),
                };
                return new() { RequestId = request.RequestId, Payload = payload };
            }
            catch (OperationCanceledException ex)
            {
                return new() { RequestId = request.RequestId, Error = new(ex, RenderErrorCode.Canceled) };
            }
            catch (Exception ex)
            {
                return new() { RequestId = request.RequestId, Error = new(ex) };
            }
        }

        private async ValueTask<byte[]> ReleaseAsync(RenderRequestEnvelope request, CancellationToken cancellationToken)
        {
            await provider.ReleaseAsync(RenderRpcSerializer.Deserialize<ExternalVideoSourceStateRequest>(request.Payload), cancellationToken).ConfigureAwait(false);
            return RenderRpcSerializer.Serialize(new EmptyResponse());
        }
    }
}

public sealed class RenderRpcException : Exception
{
    public RenderRpcException(RemoteError error) : base(string.IsNullOrWhiteSpace(error.Details) ? error.Message : $"{error.Message}{Environment.NewLine}{error.Details}")
    { Error = error; Data[nameof(RemoteError.Code)] = error.Code; Data[nameof(RemoteError.Retryable)] = error.Retryable; if (!string.IsNullOrWhiteSpace(error.Details)) Data[nameof(RemoteError.Details)] = error.Details; }
    public RemoteError Error { get; }
}

public sealed class RenderPipeException : IOException
{
    public RenderPipeException(string message) : base(message) { }
    public RenderPipeException(string message, Exception innerException) : base(message, innerException) { }
}

[DebuggerNonUserCode]
public static class RenderRpcSerializer
{
    public static byte[] Serialize<T>(T value) { using var stream = new MemoryStream(); Serializer.Serialize(stream, value); return stream.ToArray(); }
    public static T Deserialize<T>(byte[] payload) { using var stream = new MemoryStream(payload, writable: false); return Serializer.Deserialize<T>(stream); }
    public static T Clone<T>(T value) => Deserialize<T>(Serialize(value));
}
