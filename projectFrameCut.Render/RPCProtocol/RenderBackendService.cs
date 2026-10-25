using projectFrameCut.Drawing.Processing.Converting;
using projectFrameCut.Drawing.Base.ReadWriteConvert;
using projectFrameCut.Render.ClipsAndTracks;
using projectFrameCut.Render.Compose;
using projectFrameCut.Render.Contracts;
using projectFrameCut.Render.Effect;
using projectFrameCut.Render.EncodeAndDecode;
using projectFrameCut.Render.Plugin;
using projectFrameCut.Render.PreviewAudio;
using projectFrameCut.Render.RenderAPIBase.ClipAndTrack;
using projectFrameCut.Render.RenderAPIBase.Plugins;
using projectFrameCut.Render.RenderAPIBase.Project;
using projectFrameCut.Render.Rendering;
using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;

namespace projectFrameCut.Render.RPCProtocol;

public sealed partial class RenderBackendService(IRenderArtifactStore? artifactStore = null, string? stateRoot = null, Action<RenderJob>? completionSink = null, Action<RenderJob>? progressSink = null, IAudioPreviewSinkFactory? previewAudioSinkFactory = null) : IRenderService, IAsyncDisposable
{
    //FOR AGENTS: DO NOT change these configuration values.
    public const string ClipPreviewCacheVersion = $"vfd-raw-cache_APIv8";
    public const string TimelineSegmentCacheVersion = $"audio-source_APIv8";
    public const string AudioSegmentCacheVersion = $"track-source_APIv8";
    public const string FrameHashIndexVersion = $"frame-hash-index_APIv8";
    public const string PreviewCacheManifestFileName = CacheIndexDatabase.FileName;
    public const string TimelineFrameCacheVersion = ClipPreviewCacheVersion;
    private static readonly TimeSpan PreviewCacheRetention = TimeSpan.FromHours(240);
    private static readonly VfdV2PictureEncoder PreviewVfdEncoder = new(VfdCompression.Zstandard, zstandardLevel: 1);

    private readonly IRenderArtifactStore _artifacts = artifactStore ?? new RenderArtifactStore();
    private readonly string? _stateRoot = string.IsNullOrWhiteSpace(stateRoot) ? null : Path.GetFullPath(stateRoot);
    private readonly Action<RenderJob>? _completionSink = completionSink;
    private readonly Action<RenderJob>? _progressSink = progressSink;
    private readonly IAudioPreviewSinkFactory? _previewAudioSinkFactory = previewAudioSinkFactory;
    private readonly ConcurrentDictionary<Guid, BackendSession> _sessions = new();
    private readonly ConcurrentDictionary<Guid, JobEntry> _jobs = new();
    private readonly ConcurrentDictionary<string, PreviewCacheIndex> _previewCacheIndexes = new(OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal);
    private readonly object _previewCachePersistGate = new();
    private Timer? _previewCacheTimer;
    private readonly ConditionalWeakTable<IClip, PreviewClipInitialization> _previewClipInitializations = new();
    private long _lastSlowPreviewLog;
    private readonly object _persistGate = new();
    private readonly SemaphoreSlim _projectLifecycleGate = new(1, 1);
    private int _disposed;
    private readonly ConcurrentDictionary<string, ProjectExternalSourceHost> _externalSources = new(OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal);
    private ProjectExternalSourceHost GetSourceHost(string root) => _externalSources.GetOrAdd(Path.TrimEndingDirectorySeparator(Path.GetFullPath(root)), _ => new());
    private readonly ConcurrentDictionary<string, PreviewVideoProxies> _previewProxies = new(OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal);
    private PreviewVideoProxies GetPreviewProxies(BackendSession session) => _previewProxies.GetOrAdd(session.ProjectRoot, static root => new(root));
    private readonly JsonSerializerOptions _jsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        NumberHandling = JsonNumberHandling.AllowNamedFloatingPointLiterals,
    };

    public async ValueTask<RenderResponseEnvelope> DispatchAsync(RenderRequestEnvelope request, CancellationToken cancellationToken = default)
    {
        if (request.ProtocolVersion < RenderProtocol.MinimumSupportedVersion || request.ProtocolVersion > RenderProtocol.CurrentVersion)
        {
            return Failure(request, RenderErrorCode.ProtocolMismatch, $"Unsupported render protocol version {request.ProtocolVersion}.");
        }

        try
        {
            ThrowIfClosing();
            return request.Operation switch
            {
                RenderOperation.GetCapabilities => Success(request, GetCapabilities()),
                RenderOperation.ListProjectExternalSources => Success(request, await ListProjectExternalSourcesAsync(Read<ProjectExternalSourceCatalogRequest>(request), cancellationToken).ConfigureAwait(false)),
                RenderOperation.SetProjectExternalSources => Success(request, await SetProjectExternalSourcesAsync(Read<SetProjectExternalSourcesRequest>(request), cancellationToken).ConfigureAwait(false)),
                RenderOperation.OpenProject => Success(request, await OpenProjectAsync(Read<OpenProjectRequest>(request), cancellationToken).ConfigureAwait(false)),
                RenderOperation.CloseProject => Success(request, await CloseProjectAsync(Read<SessionRequest>(request), cancellationToken).ConfigureAwait(false)),
                RenderOperation.GetProjectSnapshot => Success(request, GetProjectSnapshot(Read<SessionRequest>(request))),
                RenderOperation.GetTimeline => Success(request, GetTimeline(Read<SessionRequest>(request))),
                RenderOperation.GetAssetMetadata => Success(request, await GetAssetMetadataAsync(Read<AssetMetadataRequest>(request), cancellationToken).ConfigureAwait(false)),
                RenderOperation.GetAvailableEffects => Success(request, GetAvailableEffects(Read<EffectCatalogRequest>(request))),
                RenderOperation.RenderTimelineFrame => Success(request, await RenderTimelineFrameAsync(Read<TimelineFrameRequest>(request), cancellationToken).ConfigureAwait(false)),
                RenderOperation.RenderTimelineSegment => Success(request, await RenderTimelineSegmentAsync(Read<TimelineSegmentRequest>(request), cancellationToken).ConfigureAwait(false)),
                RenderOperation.RenderAudioSegment => Success(request, await RenderAudioSegmentAsync(Read<AudioSegmentRequest>(request), cancellationToken).ConfigureAwait(false)),
                RenderOperation.ControlPreviewAudio => Success(request, await ControlPreviewAudioAsync(Read<PreviewAudioCommandRequest>(request), cancellationToken).ConfigureAwait(false)),
                RenderOperation.GetPreviewAudioClock => Success(request, GetPreviewAudioClock(Read<PreviewAudioClockRequest>(request))),
                RenderOperation.RenderClipPreview => Success(request, await RenderClipPreviewAsync(Read<ClipPreviewRequest>(request), cancellationToken).ConfigureAwait(false)),
                RenderOperation.GenerateTimelineThumbnails => Success(request, await GenerateTimelineThumbnailsAsync(Read<TimelineThumbnailsRequest>(request), cancellationToken).ConfigureAwait(false)),
                RenderOperation.RenderClipPreviewBatch => Success(request, await RenderClipPreviewBatchAsync(Read<ClipPreviewBatchRequest>(request), cancellationToken).ConfigureAwait(false)),
                RenderOperation.StartDynamicPreview => Success(request, await StartDynamicPreviewAsync(Read<StartDynamicPreviewRequest>(request), cancellationToken).ConfigureAwait(false)),
                RenderOperation.StopDynamicPreview => Success(request, await StopDynamicPreviewAsync(Read<StopDynamicPreviewRequest>(request), cancellationToken).ConfigureAwait(false)),
                RenderOperation.RenderProject => Success(request, StartRenderProject(Read<RenderProjectRequest>(request))),
                RenderOperation.GetJobStatus => Success(request, GetJob(Read<JobRequest>(request).JobId)),
                RenderOperation.CancelJob => Success(request, CancelJob(Read<JobRequest>(request).JobId)),
                RenderOperation.ListRenderJobs => Success(request, ListJobs(Read<ListRenderJobsRequest>(request))),
                RenderOperation.ReleaseArtifact => Success(request, ReleaseArtifact(Read<ArtifactRequest>(request))),
                _ => Failure(request, RenderErrorCode.Unsupported, $"Render operation '{request.Operation}' is not supported."),
            };
        }
        catch (OperationCanceledException)
        {
            return Failure(request, RenderErrorCode.Canceled, "Render request was canceled.");
        }
        catch (KeyNotFoundException ex)
        {
            return Failure(request, RenderErrorCode.SessionNotFound, ex);
        }
        catch (ArgumentException ex)
        {
            return Failure(request, RenderErrorCode.InvalidRequest, ex);
        }
        catch (Exception ex)
        {
            Log(ex, $"Render RPC {request.Operation}", this);
            return Failure(request, RenderErrorCode.BackendFailure, ex, retryable: false);
        }
    }

    private RenderCapabilities GetCapabilities() => new()
    {
        ProtocolVersion = RenderProtocol.CurrentVersion,
        MinimumProtocolVersion = RenderProtocol.MinimumSupportedVersion,
        BackendVersion = typeof(Renderer).Assembly.GetName().Version?.ToString() ?? "unknown",
        Operations = Enum.GetValues<RenderOperation>()
            .Where(operation => operation != RenderOperation.Unknown && (int)operation < 100
                && operation is not (RenderOperation.ListExternalVideoSourceClients or RenderOperation.ManageExternalVideoSourceClient)
                && operation is not (RenderOperation.RegisterExternalAudioSources or RenderOperation.UnregisterExternalAudioSources
                    or RenderOperation.ListExternalAudioSources or RenderOperation.ExternalAudioSourceCreate
                    or RenderOperation.ExternalAudioSourceInitialize or RenderOperation.ExternalAudioSourceReadSamples or RenderOperation.ExternalAudioSourceRelease)
                && ((int)operation < 18 || (int)operation > 40)
                && (_previewAudioSinkFactory is not null || operation is not (RenderOperation.ControlPreviewAudio or RenderOperation.GetPreviewAudioClock)))
            .Select(static operation => operation.ToString())
            .ToList(),
        Encoders = ["libx264"],
        Features = _previewAudioSinkFactory is null
            ? ["direct-transport", "named-pipe", "unix-socket", "timeline-preview", "clip-preview-without-layout", "clip-preview-before-layout", "thumbs-cache", "vfd-preview", "png-preview", "render-jobs", "persistent-render-jobs", "artifact-files", "continuous-preview-cache"]
            : ["direct-transport", "named-pipe", "unix-socket", "timeline-preview", "clip-preview-without-layout", "clip-preview-before-layout", "thumbs-cache", "vfd-preview", "png-preview", "render-jobs", "persistent-render-jobs", "artifact-files", "preview-audio-device-clock", "continuous-preview-cache"],
    };

    private async Task<ProjectExternalSourceCatalog> SetProjectExternalSourcesAsync(SetProjectExternalSourcesRequest request, CancellationToken cancellationToken)
    {
        await _projectLifecycleGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ThrowIfClosing();
            return await GetSourceHost(request.ProjectRoot).SetAsync(request, cancellationToken).ConfigureAwait(false);
        }
        finally { _projectLifecycleGate.Release(); }
    }

    private async Task<ProjectExternalSourceCatalog> ListProjectExternalSourcesAsync(ProjectExternalSourceCatalogRequest request, CancellationToken cancellationToken)
    {
        await _projectLifecycleGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ThrowIfClosing();
            return await GetSourceHost(request.ProjectRoot).ListAsync(request.ProjectRoot, cancellationToken).ConfigureAwait(false);
        }
        finally { _projectLifecycleGate.Release(); }
    }

    private async Task ReleaseUnusedSourcesAsync(string root)
    {
        if (_sessions.Values.Any(x => string.Equals(x.ProjectRoot, root, OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal))) return;
        if (_previewProxies.TryRemove(root, out var proxies)) await proxies.DisposeAsync().ConfigureAwait(false);
        if (_externalSources.TryRemove(root, out var sources))
            await sources.CloseAsync().ConfigureAwait(false);
    }

    private async ValueTask<PreviewAudioClock> ControlPreviewAudioAsync(PreviewAudioCommandRequest request, CancellationToken cancellationToken)
    {
        var session = GetSession(request.SessionId);
        using var operation = session.BeginOperation(cancellationToken);
        cancellationToken = operation.Token;
        using var sourceScope = ProjectExternalSourceRuntime.Use(session.ExternalSources);
        if (_previewAudioSinkFactory is null)
            throw new PlatformNotSupportedException("This render host has no preview audio sink.");
        var audio = session.GetPreviewAudio(_previewAudioSinkFactory);
        var state = request.Command switch
        {
            PreviewAudioCommand.Start => await audio.StartAsync(request.StartFrame, request.FrameRate, cancellationToken).ConfigureAwait(false),
            PreviewAudioCommand.Seek => await audio.SeekAsync(request.StartFrame, request.FrameRate, cancellationToken).ConfigureAwait(false),
            PreviewAudioCommand.Pause => await audio.PauseAsync(cancellationToken).ConfigureAwait(false),
            PreviewAudioCommand.Resume => await audio.ResumeAsync(cancellationToken).ConfigureAwait(false),
            PreviewAudioCommand.Stop => await audio.StopAsync(cancellationToken).ConfigureAwait(false),
            _ => throw new ArgumentOutOfRangeException(nameof(request.Command)),
        };
        return ToContract(state);
    }

    private PreviewAudioClock GetPreviewAudioClock(PreviewAudioClockRequest request)
    {
        var session = GetSession(request.SessionId);
        using var operation = session.BeginOperation();
        using var sourceScope = ProjectExternalSourceRuntime.Use(session.ExternalSources);
        return session.TryGetPreviewAudio(out var audio)
            ? ToContract(audio.GetState(request.Generation))
            : new PreviewAudioClock { Generation = request.Generation, SampleRate = PreviewAudioSession.SampleRate, Channels = PreviewAudioSession.Channels };
    }

    private static PreviewAudioClock ToContract(PreviewAudioState state) => new()
    {
        Generation = state.Generation,
        StartFrame = state.StartFrame,
        SampleRate = state.SampleRate,
        Channels = state.Channels,
        PlayedSamples = state.PlayedSamples,
        BufferedSamples = state.BufferedSamples,
        IsRunning = state.IsRunning,
        IsPaused = state.IsPaused,
        HasAudio = state.HasAudio,
    };

    private async ValueTask<RenderSession> OpenProjectAsync(OpenProjectRequest request, CancellationToken cancellationToken)
    {
        await _projectLifecycleGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ThrowIfClosing();
            return await OpenProjectCoreAsync(request, cancellationToken).ConfigureAwait(false);
        }
        finally { _projectLifecycleGate.Release(); }
    }

    private async ValueTask<RenderSession> OpenProjectCoreAsync(OpenProjectRequest request, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(request.ProjectRoot);
        ArgumentException.ThrowIfNullOrWhiteSpace(request.TimelineJson);
        var root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(request.ProjectRoot));
        if (!Directory.Exists(root)) throw new DirectoryNotFoundException($"Project root '{root}' does not exist.");
        var sessionId = request.SessionId == Guid.Empty ? Guid.NewGuid() : request.SessionId;
        _sessions.TryGetValue(sessionId, out var previous);
        if (previous is null || !string.Equals(previous.ProjectRoot, root, OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal))
            MaintainPreviewCache(root);
        if (PluginManager.ProjectPluginIds.Count > 0 &&
            _sessions.Values.Any(x => !string.Equals(x.ProjectRoot, root, OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal)))
            throw new NotSupportedException("A render backend with active project plugins cannot host a different project.");

        var project = string.IsNullOrWhiteSpace(request.ProjectJson)
            ? null
            : JsonSerializer.Deserialize<ProjectJSONStructure>(request.ProjectJson, _jsonOptions);
        var hasProjectPlugins = project?.ProjectPlugins?.Any(x => x.Enabled) == true;
        var loadedProjectPluginsForOpen = false;
        if (hasProjectPlugins)
        {
            if (_sessions.Values.Any(x => !string.Equals(x.ProjectRoot, root, OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal)))
                throw new NotSupportedException("A render backend cannot host project plugins from multiple projects at the same time.");
            if (PluginManager.ProjectPluginLoader is null)
                throw new NotSupportedException("This render host does not provide project plugin isolation.");
            if (PluginManager.ProjectPluginIds.Count == 0)
            {
                await PluginManager.ProjectPluginLoader(root, project!, cancellationToken).ConfigureAwait(false);
                loadedProjectPluginsForOpen = true;
            }
        }

        var draft = JsonSerializer.Deserialize<DraftStructureJSON>(request.TimelineJson, _jsonOptions)
            ?? throw new ArgumentException("Timeline JSON is invalid.");
        var externalSources = GetSourceHost(root);
        try { await externalSources.SetAsync(new() { ProjectRoot = root, AllowedSources = request.AllowedExternalSources }, cancellationToken).ConfigureAwait(false); }
        catch { await ReleaseUnusedSourcesAsync(root).ConfigureAwait(false); throw; }
        using var sourceScope = ProjectExternalSourceRuntime.Use(externalSources);
        var assets = request.Assets
            .Where(static item => !string.IsNullOrWhiteSpace(item.AssetId))
            .GroupBy(static item => item.AssetId, StringComparer.Ordinal)
            .ToDictionary(
                static group => group.Key,
                group => ResolveProjectSourcePath(root, group.Last().Path) ?? string.Empty,
                StringComparer.Ordinal);

        var sourceConfiguration = ComputeTextHash(JsonSerializer.Serialize(new
        {
            request.ProjectJson, request.ProxyRoot, request.ProjectWidth, request.ProjectHeight, request.FrameRate,
            request.CacheNamespace,
            Assets = assets.OrderBy(item => item.Key, StringComparer.Ordinal).ToArray(),
            ProjectSources = externalSources.CacheToken,
            RpcSources = ExternalVideoSourceRegistry.CacheToken,
            RpcAudioSources = ExternalAudioSourceRegistry.CacheToken,
            RpcMaskSources = ExternalMaskSourceRegistry.CacheToken,
            Effects = EffectHelper.ConfigurationRevision,
        }, _jsonOptions));
        var reusable = previous is not null && previous.SourceConfiguration == sourceConfiguration && previous.ProjectRoot == root ? previous : null;
        var snapshotHash = ComputeTextHash($"{request.ProjectJson}\n{request.TimelineJson}");
        if (reusable?.SnapshotHash == snapshotHash && reusable.Clips.All(clip => !ClipInitializationFailure.IsMarked(clip)))
            return reusable.ToContract();
        await StopDynamicPreviewAsync(new() { SessionId = sessionId }, cancellationToken).ConfigureAwait(false);
        var clipSignatures = draft.Clips.Where(dto => dto.ClipType != ClipMode.MarkingClip)
            .ToDictionary(dto => dto.Id, GetClipSignature);
        var trackSignatures = draft.SoundTracks.ToDictionary(dto => dto.Id, dto => JsonSerializer.Serialize(dto, _jsonOptions));

        IClip[] clips = [];
        ISoundTrack[] soundTracks = [];
        try
        {
            clips = await Task.Run(() => CreateClips(draft, assets, request.ProxyRoot, root, cancellationToken, reusable, clipSignatures), cancellationToken).ConfigureAwait(false);
            soundTracks = await Task.Run(() => CreateSoundTracks(draft, clips, assets, root, cancellationToken, reusable, trackSignatures), cancellationToken).ConfigureAwait(false);
        }
        catch
        {
            DisposeResources(clips, soundTracks, reusable);
            await ReleaseUnusedSourcesAsync(root).ConfigureAwait(false);
            if (loadedProjectPluginsForOpen && _sessions.IsEmpty && PluginManager.ProjectPluginUnloader is not null)
                await PluginManager.ProjectPluginUnloader().ConfigureAwait(false);
            throw;
        }
        // Project duration is a timeline property. Do not derive it from runtime
        // GetEffectiveDuration(): speed providers and open-ended sources may deliberately
        // report UInt32.MaxValue even though the clip has a finite UI/timeline duration.
        var duration = ResolveDuration(draft);
        FrameHashIndex hashIndex;
        try
        {
            hashIndex = CreateFrameHashIndex(snapshotHash, cancellationToken);
        }
        catch
        {
            DisposeResources(clips, soundTracks, reusable);
            await ReleaseUnusedSourcesAsync(root).ConfigureAwait(false);
            if (loadedProjectPluginsForOpen && _sessions.IsEmpty && PluginManager.ProjectPluginUnloader is not null)
                await PluginManager.ProjectPluginUnloader().ConfigureAwait(false);
            throw;
        }

        var cacheNamespace = string.IsNullOrWhiteSpace(request.CacheNamespace)
            ? string.Empty
            : ComputeTextHash(request.CacheNamespace);
        var backendSession = new BackendSession(
            sessionId,
            root,
            request.ProjectJson,
            request.TimelineJson,
            project?.ProjectName ?? Path.GetFileName(root),
            Math.Max(1, request.ProjectWidth > 0 ? request.ProjectWidth : project?.RelativeWidth ?? 1),
            Math.Max(1, request.ProjectHeight > 0 ? request.ProjectHeight : project?.RelativeHeight ?? 1),
            Math.Max(1, request.FrameRate > 0 ? request.FrameRate : (int)(project?.TargetFrameRate ?? 30)),
            duration,
            clips,
            soundTracks,
            assets,
            snapshotHash,
            hashIndex,
            cacheNamespace, sourceConfiguration, clipSignatures, trackSignatures);
        backendSession.ExternalSources = externalSources;
        backendSession.AudioSamples.Revision = () => backendSession.CacheNamespace;

        if (previous is not null)
        {
            CancelJobsForSession(sessionId);
            await previous.DisposeAsync(backendSession).ConfigureAwait(false);
        }
        foreach (var dto in draft.Clips)
        {
            if (clips.FirstOrDefault(clip => clip.Id == dto.Id) is not { } clip) continue;
            if (reusable?.Clips.Contains(clip, ReferenceEqualityComparer.Instance) != true) continue;
            clip.Rotation = VideoClipRotation.Normalize(dto.Rotation);
            if (dto.FromPlugin != InternalPluginBase.InternalPluginBaseID || dto.ClipType != ClipMode.VideoClip) continue;
            clip.TargetX = dto.TargetX;
            clip.TargetY = dto.TargetY;
            clip.TargetWidth = dto.TargetWidth;
            clip.TargetHeight = dto.TargetHeight;
        }
        _sessions[sessionId] = backendSession;
        StartPreviewHashWarmup(backendSession);
        LogDiagnostic($"[RenderRPC] Updated session {sessionId}: reused {clips.Count(clip => reusable?.Clips.Contains(clip, ReferenceEqualityComparer.Instance) == true)}/{clips.Length} clips and {soundTracks.Count(track => reusable?.SoundTracks.Contains(track, ReferenceEqualityComparer.Instance) == true)}/{soundTracks.Length} soundtracks.");
        return backendSession.ToContract();
    }

    private string GetClipSignature(ClipDraftDTO dto)
    {
        VideoClipRotation.Migrate(dto);
        var json = (JsonObject)JsonSerializer.SerializeToNode(dto, _jsonOptions)!;
        json.Remove(nameof(dto.Rotation));
        if (dto.FromPlugin == InternalPluginBase.InternalPluginBaseID && dto.ClipType == ClipMode.VideoClip)
        {
            json.Remove(nameof(dto.TargetX));
            json.Remove(nameof(dto.TargetY));
            json.Remove(nameof(dto.TargetWidth));
            json.Remove(nameof(dto.TargetHeight));
        }
        return json.ToJsonString(_jsonOptions);
    }

    private static void DisposeResources(IEnumerable<IClip> clips, IEnumerable<ISoundTrack> tracks, BackendSession? retained)
    {
        foreach (var clip in clips.Where(clip => retained?.Clips.Contains(clip, ReferenceEqualityComparer.Instance) != true))
        {
            try { clip.Dispose(); }
            catch (Exception ex) { Log(ex, $"Dispose clip {clip.Id}"); }
        }
        foreach (var track in tracks.Where(track => retained?.SoundTracks.Contains(track, ReferenceEqualityComparer.Instance) != true))
        {
            try { track.Dispose(); }
            catch (Exception ex) { Log(ex, $"Dispose soundtrack {track.Id}"); }
        }
    }

    private static FrameHashIndex CreateFrameHashIndex(string snapshotHash, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return new FrameHashIndex
        {
            Version = FrameHashIndexVersion,
            SnapshotHash = snapshotHash,
        };
    }

    private async ValueTask<EmptyResponse> CloseProjectAsync(SessionRequest request, CancellationToken cancellationToken)
    {
        await _projectLifecycleGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ThrowIfClosing();
            return await CloseProjectCoreAsync(request, cancellationToken).ConfigureAwait(false);
        }
        finally { _projectLifecycleGate.Release(); }
    }

    private async ValueTask<EmptyResponse> CloseProjectCoreAsync(SessionRequest request, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        CancelJobsForSession(request.SessionId);
        if (_sessions.TryRemove(request.SessionId, out var session))
        {
            await StopDynamicPreviewAsync(new() { SessionId = session.Id }, CancellationToken.None).ConfigureAwait(false);
            await session.DisposeAsync().ConfigureAwait(false);
            FlushPreviewCache(session.ProjectRoot);
            await ReleaseUnusedSourcesAsync(session.ProjectRoot).ConfigureAwait(false);
        }
        if (_sessions.IsEmpty && PluginManager.ProjectPluginIds.Count > 0 && PluginManager.ProjectPluginUnloader is not null)
            await PluginManager.ProjectPluginUnloader().ConfigureAwait(false);
        return new();
    }

    private ProjectSnapshot GetProjectSnapshot(SessionRequest request)
    {
        var session = GetSession(request.SessionId);
        using var operation = session.BeginOperation();
        using var sourceScope = ProjectExternalSourceRuntime.Use(session.ExternalSources);
        return new ProjectSnapshot { Session = session.ToContract(), ProjectJson = session.ProjectJson, TimelineJson = session.TimelineJson };
    }

    private TimelineSnapshot GetTimeline(SessionRequest request)
    {
        var session = GetSession(request.SessionId);
        using var operation = session.BeginOperation();
        using var sourceScope = ProjectExternalSourceRuntime.Use(session.ExternalSources);
        return new TimelineSnapshot
        {
            SessionId = session.Id,
            Duration = session.Duration,
            ClipCount = session.Clips.Length,
            Clips = session.Clips.Select(static clip => new TimelineClip
            {
                ClipId = clip.Id,
                Name = clip.Name,
                TypeName = clip.TypeName,
                LayerIndex = clip.LayerIndex,
                SubLayerIndex = clip.SubLayerIndex,
                StartFrame = clip.StartFrame,
                Duration = clip.Duration,
            }).ToList(),
        };
    }

    private async ValueTask<AssetMetadata> GetAssetMetadataAsync(AssetMetadataRequest request, CancellationToken cancellationToken)
    {
        var session = GetSession(request.SessionId);
        using var operation = session.BeginOperation(cancellationToken);
        cancellationToken = operation.Token;
        using var sourceScope = ProjectExternalSourceRuntime.Use(session.ExternalSources);
        var path = ResolveAssetPath(session, request);
        return await Task.Run(() =>
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (ProjectExternalVideoSource.TryGetDescriptor(path, out var descriptor))
                return new AssetMetadata
                {
                    AssetId = request.AssetId, MediaType = "video/x-project-external-source", ContentHash = ComputeTextHash(path + session.ExternalSources.CacheToken),
                    Width = descriptor.Width, Height = descriptor.Height, FrameRate = descriptor.Fps, FrameCount = descriptor.TotalFrames,
                };
            if (ProjectExternalAudioSource.TryGetDescriptor(path, out _) || RemoteRpcAudioSource.TryGetDescriptor(path, out _))
                return new AssetMetadata
                {
                    AssetId = request.AssetId, MediaType = "audio/x-external-source",
                    ContentHash = ComputeTextHash(path + session.ExternalSources.CacheToken + ExternalAudioSourceRegistry.CacheToken),
                };
            var info = new FileInfo(path);
            var result = new AssetMetadata
            {
                AssetId = request.AssetId,
                MediaType = ResolveMediaType(path),
                Size = info.Length,
                ContentHash = ComputeFileHash(path),
            };
            try
            {
                using var source = PluginManager.CreateVideoSource(path);
                source.Initialize();
                result.Width = source.Width;
                result.Height = source.Height;
                result.FrameRate = source.Fps;
                result.FrameCount = source.TotalFrames;
            }
            catch
            {
                // Non-video assets still return stable file metadata.
            }
            return result;
        }, cancellationToken).ConfigureAwait(false);
    }

    private EffectCatalog GetAvailableEffects(EffectCatalogRequest request)
    {
        using var operation = GetSession(request.SessionId).BeginOperation();
        var catalog = new EffectCatalog();
        foreach (var (typeName, creator) in EffectHelper.EffectsProviderEnum)
        {
            try
            {
                var effect = creator().RestoreInstanceWithDefaultType();
                catalog.Effects.Add(new EffectDescriptor
                {
                    TypeName = typeName,
                    Name = effect.Name,
                    PluginId = effect.FromPlugin,
                    EffectType = effect.TypeOfEffect.ToString(),
                    Description = effect.GetInfo().Description ?? string.Empty,
                });
            }
            catch (Exception ex)
            {
                Log(ex, $"Describe effect {typeName}", this);
            }
        }
        foreach (var plugin in PluginManager.LoadedPlugins.Values)
        {
            catalog.Plugins.Add(new PluginDescriptor
            {
                PluginId = plugin.PluginID,
                Name = plugin.Name,
                Version = plugin.Version?.ToString() ?? string.Empty,
            });
        }
        return catalog;
    }

    private async ValueTask<RenderArtifact> RenderTimelineFrameAsync(TimelineFrameRequest request, CancellationToken cancellationToken)
    {
        var session = GetSession(request.SessionId);
        using var operation = session.BeginOperation(cancellationToken);
        cancellationToken = operation.Token;
        using var sourceScope = ProjectExternalSourceRuntime.Use(session.ExternalSources);
        using var audioScope = EffectProcessing.OpenAudioFrame(session.AudioSamples);
        var width = Math.Max(1, request.Width);
        var height = Math.Max(1, request.Height);
        await session.RenderGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        IPicture? renderedPicture = null;
        try
        { 
            cancellationToken.ThrowIfCancellationRequested();
            var visualClips = GetVisualClips(session.Clips);
            var pixelMode = request.PreferredPixelFormat == PreviewPixelFormat.Rgba16FloatScRgb
                ? IPicture.PicturePixelMode.UShortPicture : IPicture.PicturePixelMode.BytePicture;
            InitializePreviewClips(visualClips, pixelMode, request.FrameIndex, cancellationToken);
            using var proxies = GetPreviewProxies(session).Enter(visualClips, width, height, session.Width, session.Height, request.FrameIndex);
            var hashOptions = new FrameHashOptions
            {
                Mode = request.FrameHashMode, Width = width, Height = height, ProjectWidth = session.Width,
                ProjectHeight = session.Height, FrameRate = session.FrameRate, PixelMode = pixelMode.Value,
                Stage = "timeline", ProcessFromCanvas = Timeline.ProcessEffectFromCanvas, AutoCenter = true,
                PreviewSourceIdentity = proxies.Identity,
            };
            using var hashContext = new FrameHashContext(hashOptions, cancellationToken, session.NodeCache);
            using var hashScope = hashContext.Enter();
            IPicture RenderTimeline()
            {
                if (renderedPicture is not null) return renderedPicture;
                var layers = Timeline.GetFramesInOneFrame(visualClips, request.FrameIndex, width, height, pixelMode,
                    projectRelativeWidth: session.Width, projectRelativeHeight: session.Height, initializeClips: false);
                return renderedPicture = Timeline.MixtureLayers(layers, request.FrameIndex, width, height, (int)pixelMode,
                    autoCenterImplicitClip: true, projectRelativeWidth: session.Width, projectRelativeHeight: session.Height,
                    disposeIntermediateFrames: true, cancellationToken: cancellationToken);
            }
            var identity = FrameHashHelper.GetFrameHash(visualClips, request.FrameIndex, hashOptions, () => RenderTimeline(), cancellationToken);
            var frameHash = identity.Hash;
            var namespacePrefix = string.IsNullOrEmpty(session.CacheNamespace) ? string.Empty : $"{session.CacheNamespace}_";
            var wantsScRgb = request.PreferredPixelFormat == PreviewPixelFormat.Rgba16FloatScRgb;
            var formatSuffix = wantsScRgb ? "vfd16" : "vfd8";
            var cacheKey = $"{TimelineFrameCacheVersion}_{namespacePrefix}{frameHash}_{width}x{height}_{formatSuffix}";
            var allowCaching = identity.CanReuse;
            if (!allowCaching)
                LogDiagnostic($"[RenderRPC] Bypassing timeline preview cache at frame {request.FrameIndex}.");
            var relativePath = allowCaching ? $"thumbs/projectFrameCut_Render_{cacheKey}.vfd"
                : $"thumbs/unCacheablePerClip/{session.Id:N}/frame_{Guid.NewGuid():N}.vfd";
            var finalPath = _artifacts.ResolveProjectPath(session.ProjectRoot, relativePath);
            var cacheHit = allowCaching && File.Exists(finalPath);
            if (!cacheHit)
            {
                var temporaryPath = _artifacts.CreateTemporaryPath(finalPath);
                IPicture? picture = null;
                try
                {
                    picture = RenderTimeline();
                    renderedPicture = null;
                    cancellationToken.ThrowIfCancellationRequested();
                    if (allowCaching && (visualClips.Any(ClipInitializationFailure.IsMarked) || proxies.Failed))
                    {
                        allowCaching = false;
                        relativePath = $"thumbs/unCacheablePerClip/{session.Id:N}/frame_{Guid.NewGuid():N}.vfd";
                        finalPath = _artifacts.ResolveProjectPath(session.ProjectRoot, relativePath);
                        temporaryPath = _artifacts.CreateTemporaryPath(finalPath);
                        LogDiagnostic($"[RenderRPC] Bypassing timeline preview cache after rendering frame {request.FrameIndex}.");
                    }
                    picture.SaveToDisk(temporaryPath, PreviewVfdEncoder);
                    cancellationToken.ThrowIfCancellationRequested();
                    _artifacts.CommitTemporaryFile(temporaryPath, finalPath);
                }
                catch
                {
                    try { File.Delete(temporaryPath); } catch { }
                    throw;
                }
                finally
                {
                    try { picture?.Dispose(); } catch { }
                }
            }
            if (allowCaching)
                TrackPreviewCacheAccess(session.ProjectRoot, relativePath, null, request.FrameIndex, $"{width}x{height}:{formatSuffix}");
            if (request.PreferredPixelFormat == PreviewPixelFormat.PngImage)
                return MaterializePreviewPng(session, relativePath, null, request.FrameIndex, width, height, cacheHit, allowCaching, cancellationToken);
            return _artifacts.Register(session.Id, session.ProjectRoot, relativePath, "application/x-projectframecut-vfd",
                cacheHit, isPreview: true, width, height, session.FrameRate, PreviewPixelFormat.VfdPicture, 0, "vfd-native");
        }
        finally
        {
            renderedPicture?.Dispose();
            session.RenderGate.Release();
        }
    }

    private async ValueTask<RenderArtifact> RenderClipPreviewAsync(ClipPreviewRequest request, CancellationToken cancellationToken, bool registerArtifact = true, BackendSession? previewSession = null, IClip[]? previewClips = null)
    {
        var session = previewSession ?? GetSession(request.SessionId);
        using var operation = session.BeginOperation(cancellationToken);
        cancellationToken = operation.Token;
        using var sourceScope = ProjectExternalSourceRuntime.Use(session.ExternalSources);
        using var audioScope = EffectProcessing.OpenAudioFrame(session.AudioSamples);
        var sessionClips = previewClips ?? session.Clips;
        var savedClip = sessionClips.FirstOrDefault(candidate => candidate.Id == request.ClipId)
            ?? throw new KeyNotFoundException($"Clip '{request.ClipId}' was not found in render session '{request.SessionId}'.");
        using var snapshot = string.IsNullOrEmpty(request.VectorClipJson) ? null : CreateVectorPreviewSnapshot(request, savedClip, session);
        var clip = snapshot ?? savedClip;
        if (clip.ClipType == ClipMode.AudioClip)
            throw new NotSupportedException($"Clip '{request.ClipId}' is an audio clip and cannot produce a picture preview.");
        var canvasWidth = Math.Max(1, request.CanvasWidth);
        var canvasHeight = Math.Max(1, request.CanvasHeight);
        var projectWidth = Math.Max(1, request.ProjectWidth > 0 ? request.ProjectWidth : session.Width);
        var projectHeight = Math.Max(1, request.ProjectHeight > 0 ? request.ProjectHeight : session.Height);
        var previewWidth = ResolveClipPreviewDimension(clip.TargetWidth, projectWidth, canvasWidth);
        var previewHeight = ResolveClipPreviewDimension(clip.TargetHeight, projectHeight, canvasHeight);

        var startedAt = Stopwatch.GetTimestamp();
        double lookupMs = 0, initializeMs = 0, hashMs = 0, renderMs = 0, writeMs = 0, trackMs = 0;
        var previewCacheHit = false;
        IPicture? renderedPicture = null;
        // Playback workers own separate clip runtimes; other requests share the session gate.
        var renderGate = previewClips is null ? session.RenderGate : null;
        if (renderGate is not null) await renderGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        var gateMs = Stopwatch.GetElapsedTime(startedAt).TotalMilliseconds;
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            // Use the same full-timeline content hash as static previews. This is
            // important for transform clips whose output depends on bound clips
            // outside the requested clip itself.
            var clips = snapshot is null ? sessionClips : sessionClips.Select(c => c.Id == clip.Id ? clip : c).ToArray();
            if (request.BeforeLayout && clip.GetRelativeFrameIndex(request.FrameIndex) is null)
                throw new ArgumentOutOfRangeException(nameof(request.FrameIndex), "The frame is outside the clip's timeline interval.");
            if (!request.BeforeLayout && TransformProcessing.HasActiveTransform(clip, GetVisualClips(clips), request.FrameIndex))
            {
                previewWidth = canvasWidth;
                previewHeight = canvasHeight;
            }
            var visualClips = GetVisualClips(clips);
            var pixelMode = request.PreferredPixelFormat == PreviewPixelFormat.Rgba16FloatScRgb
                ? IPicture.PicturePixelMode.UShortPicture : IPicture.PicturePixelMode.BytePicture;
            using var proxies = PreviewVideoProxies.Scope.Current is null
                ? GetPreviewProxies(session).Enter(visualClips, canvasWidth, canvasHeight, projectWidth, projectHeight, request.FrameIndex) : null;
            var hashOptions = new FrameHashOptions
            {
                Mode = request.FrameHashMode, Width = canvasWidth, Height = canvasHeight,
                ProjectWidth = projectWidth, ProjectHeight = projectHeight, FrameRate = session.FrameRate,
                PixelMode = pixelMode.Value, Stage = request.BeforeLayout ? "before-layout" : "clip",
                ProcessFromCanvas = Timeline.ProcessEffectFromCanvas,
                PreviewSourceIdentity = PreviewVideoProxies.Scope.Current?.Identity,
            };
            using var ownedHashContext = FrameHashContext.Current is null
                ? new FrameHashContext(hashOptions, cancellationToken, session.NodeCache) : null;
            using var hashScope = ownedHashContext?.Enter();
            var stageStarted = Stopwatch.GetTimestamp();
            IClip[] hashDependencies = [clip, .. Timeline.CollectHashDependencies(visualClips, clip, new HashSet<Guid> { clip.Id })];
            InitializePreviewClips(hashDependencies, pixelMode, request.FrameIndex, cancellationToken);
            initializeMs = Stopwatch.GetElapsedTime(stageStarted).TotalMilliseconds;
            using var previewCache = request.UsePreviewMetadata
                ? new PreviewFrameCache(id => Path.Combine(session.ProjectRoot, "thumbs", "perClip", id.ToString(), "dynamic"),
                request.FrameIndex, canvasWidth, canvasHeight, projectWidth, projectHeight,
                request.PreferredPixelFormat == PreviewPixelFormat.Rgba16FloatScRgb, request.BeforeLayout, cancellationToken) : null;
            IPicture RenderPreview()
            {
                if (renderedPicture is not null) return renderedPicture;
                long started = Stopwatch.GetTimestamp();
                try
                {
                    return renderedPicture = ClipPreviewRenderer.Render(clip, visualClips, canvasWidth,
                        canvasHeight, projectWidth, projectHeight, request.FrameIndex, cancellationToken, pixelMode,
                        request.BeforeLayout, initializeClips: false)
                        ?? throw new InvalidOperationException($"Clip '{clip.Id}' did not produce a preview frame.");
                }
                finally { renderMs += Stopwatch.GetElapsedTime(started).TotalMilliseconds; }
            }
            var hashStarted = Stopwatch.GetTimestamp();
            var identity = snapshot is null && hashDependencies.All(c => !ClipInitializationFailure.IsMarked(c) && !FrameHashHelper.NeedsRendering(c))
                && TryGetPreparedPreviewHash(session, clip, request.FrameIndex, hashOptions, out var preparedHash)
                ? preparedHash
                : FrameHashHelper.GetClipHash(visualClips, clip, request.FrameIndex, hashOptions, () => RenderPreview(), cancellationToken);
            hashMs = Math.Max(0, Stopwatch.GetElapsedTime(hashStarted).TotalMilliseconds - renderMs);
            var lookupStarted = Stopwatch.GetTimestamp();
            IPicture TakePreview()
            {
                var picture = RenderPreview();
                renderedPicture = null;
                return picture;
            }
            bool CanPersist() => PreviewVideoProxies.Scope.Current?.Failed != true
                && hashDependencies.All(c => !ClipInitializationFailure.IsMarked(c) && Timeline.CanCacheClipSource(c));
            if (!request.UsePreviewMetadata)
            {
                var legacy = RenderLegacyClipPreview(session, request, identity, projectWidth, projectHeight,
                    canvasWidth, canvasHeight, TakePreview, CanPersist, registerArtifact, cancellationToken);
                previewCacheHit = legacy.CacheHit;
                return legacy;
            }
            var clipHash = FrameHashHelper.HashValue(new { Identity = identity.Hash, session.CacheNamespace });
            var wantsScRgb = request.PreferredPixelFormat == PreviewPixelFormat.Rgba16FloatScRgb;
            var formatSuffix = wantsScRgb ? "scrgb16" : "png";
            var allowCaching = identity.CanReuse;
            var cached = previewCache!.GetOrCreate(clip, visualClips, clipHash, allowCaching,
                _artifacts.ResolveProjectPath(session.ProjectRoot, $"thumbs/unCacheablePerClip/{session.Id:N}/clip_{clip.Id:N}_{Guid.NewGuid():N}.vfd2m"),
                TakePreview, CanPersist);
            var relativePath = Path.GetRelativePath(session.ProjectRoot, cached.Path).Replace(Path.DirectorySeparatorChar, '/');
            var cacheHit = cached.CacheHit;
            allowCaching = cached.Persistent;
            previewCacheHit = cacheHit;
            previewWidth = cached.Data.Width;
            previewHeight = cached.Data.Height;
            writeMs = Math.Max(0, Stopwatch.GetElapsedTime(lookupStarted).TotalMilliseconds - renderMs);
            var trackStartedAt = Stopwatch.GetTimestamp();
            if (allowCaching)
                TrackPreviewCacheAccess(session.ProjectRoot, relativePath, clip.Id, request.FrameIndex, $"project:{projectWidth}x{projectHeight};canvas:{canvasWidth}x{canvasHeight};output:{previewWidth}x{previewHeight};format:{formatSuffix}");
            trackMs = Stopwatch.GetElapsedTime(trackStartedAt).TotalMilliseconds;
            if (request.PreferredPixelFormat == PreviewPixelFormat.PngImage)
                return MaterializePreviewPng(session, relativePath, clip.Id, request.FrameIndex, previewWidth, previewHeight, cacheHit, allowCaching, cancellationToken, cached.Data);
            var artifact = registerArtifact
                ? _artifacts.Register(session.Id, session.ProjectRoot, relativePath, "application/x-projectframecut-vfd2m",
                    cacheHit, isPreview: true, previewWidth, previewHeight, session.FrameRate, PreviewPixelFormat.VfdPicture,
                    cached.Data.Stride, wantsScRgb ? "scrgb-linear-premultiplied" : "srgb")
                : new RenderArtifact
                {
                    SessionId = session.Id, ProjectRelativePath = relativePath, Width = previewWidth, Height = previewHeight,
                    PixelFormat = PreviewPixelFormat.VfdPicture, CacheHit = cacheHit, IsPreview = true,
                };
            artifact.PreviewContainer = "vfd2m";
            artifact.PreviewEffectHash = clipHash;
            return artifact;
        }
        finally
        {
            renderedPicture?.Dispose();
            renderGate?.Release();
            var now = Stopwatch.GetTimestamp();
            var elapsedMs = Stopwatch.GetElapsedTime(startedAt, now).TotalMilliseconds;
            var lastLog = Interlocked.Read(ref _lastSlowPreviewLog);
            if (elapsedMs >= 250 && now - lastLog >= Stopwatch.Frequency * 5
                && Interlocked.CompareExchange(ref _lastSlowPreviewLog, now, lastLog) == lastLog)
                LogDiagnostic($"[RenderRPC] Slow clip preview: clip={clip.Id}, frame={request.FrameIndex}, size={canvasWidth}x{canvasHeight}, cacheHit={previewCacheHit}, total={elapsedMs:F1}ms, gate={gateMs:F1}ms, lookup={lookupMs:F1}ms, init={initializeMs:F1}ms, hash={hashMs:F1}ms, render={renderMs:F1}ms, write={writeMs:F1}ms, track={trackMs:F1}ms; {session.NodeCache.GetStatusInfo()}.");
        }
    }

    private RenderArtifact MaterializePreviewPng(BackendSession session, string vfdRelativePath, Guid? clipId, uint frameIndex,
        int width, int height, bool sourceCacheHit, bool allowCaching, CancellationToken cancellationToken, PreviewFrameData? data = null)
    {
        cancellationToken.ThrowIfCancellationRequested();
        bool container = Path.GetExtension(vfdRelativePath).Equals(".vfd2m", StringComparison.OrdinalIgnoreCase);
        data ??= container ? PreviewFrameContainer.Read(_artifacts.ResolveProjectPath(session.ProjectRoot, vfdRelativePath)) : null;
        var relativePath = container
            ? Path.ChangeExtension(vfdRelativePath, $".{data!.EffectHash}.png")
            : Path.ChangeExtension(vfdRelativePath, ".png");
        var finalPath = _artifacts.ResolveProjectPath(session.ProjectRoot, relativePath);
        var cacheHit = allowCaching && File.Exists(finalPath);
        if (!cacheHit)
        {
            var temporaryPath = _artifacts.CreateTemporaryPath(finalPath);
            try
            {
                if (data is not null)
                    File.WriteAllBytes(temporaryPath, data.ToPng());
                else
                {
                    using var stream = File.OpenRead(_artifacts.ResolveProjectPath(session.ProjectRoot, vfdRelativePath));
                    if (!PictureExtensions.SharedVfdPictureDecoder.TryLoad(stream, out IPicture? picture) || picture is null)
                        throw new InvalidDataException("The cached preview is not a valid VFD picture.");
                    using (picture)
                    {
                        if (picture is IHDRPicture<ushort> hdr)
                        {
                            using var sdr = hdr.DegradeToSDR(HDRImageDegradeToSDRMode.NormalizeBrightnessToRGB);
                            sdr.SaveToPng(temporaryPath);
                        }
                        else picture.SaveToPng(temporaryPath);
                    }
                }
                cancellationToken.ThrowIfCancellationRequested();
                _artifacts.CommitTemporaryFile(temporaryPath, finalPath);
            }
            catch
            {
                try { File.Delete(temporaryPath); } catch { }
                throw;
            }
        }
        if (allowCaching)
            TrackPreviewCacheAccess(session.ProjectRoot, relativePath, clipId, frameIndex, $"{width}x{height}:png");
        LogDiagnostic($"[RenderRPC] PNG preview: clip={clipId}, frame={frameIndex}, sourceCacheHit={sourceCacheHit}, pngCacheHit={cacheHit}, path={relativePath}.");
        return _artifacts.Register(session.Id, session.ProjectRoot, relativePath, "image/png",
            cacheHit || sourceCacheHit, isPreview: true, width, height, session.FrameRate, PreviewPixelFormat.EncodedImage);
    }

    private IClip CreateVectorPreviewSnapshot(ClipPreviewRequest request, IClip savedClip, BackendSession session)
    {
        var dto = JsonSerializer.Deserialize<ClipDraftDTO>(request.VectorClipJson!, _jsonOptions)
            ?? throw new InvalidDataException("The vector preview snapshot is empty.");
        if (savedClip.ClipType != ClipMode.VectorComponentClip || dto.ClipType != savedClip.ClipType
            || dto.Id != savedClip.Id || dto.FromPlugin != savedClip.FromPlugin || dto.TypeName != savedClip.TypeName)
            throw new InvalidDataException("The vector preview snapshot does not match the session clip.");
        var clip = PluginManager.CreateClip(JsonSerializer.SerializeToElement(dto, _jsonOptions));
        try
        {
            ResolveSourcePath(clip, dto.FilePath, session.Assets, string.Empty, session.ProjectRoot);
            clip.ReInit(IPicture.PicturePixelMode.BytePicture);
            clip.EffectsInstances = EffectHelper.GetClipEffectsInstances(clip);
            return clip;
        }
        catch
        {
            clip.Dispose();
            throw;
        }
    }

    private async ValueTask<ClipPreviewBatchResponse> RenderClipPreviewBatchAsync(ClipPreviewBatchRequest request, CancellationToken cancellationToken)
    {
        var response = new ClipPreviewBatchResponse();
        foreach (var item in request.Requests)
        {
            try
            {
                response.Artifacts.Add(await RenderClipPreviewAsync(item, cancellationToken).ConfigureAwait(false));
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                response.Errors.Add(new RemoteError(ex));
            }
        }
        return response;
    }

    private static int ResolveClipPreviewDimension(int clipDimension, int projectDimension, int canvasDimension)
        => clipDimension <= 0 || projectDimension <= 0
            ? Math.Max(1, canvasDimension)
            : Math.Max(1, (int)Math.Round((double)clipDimension * canvasDimension / projectDimension, MidpointRounding.AwayFromZero));

    private async ValueTask<RenderArtifact> RenderTimelineSegmentAsync(TimelineSegmentRequest request, CancellationToken cancellationToken)
    {
        var session = GetSession(request.SessionId);
        using var operation = session.BeginOperation(cancellationToken);
        cancellationToken = operation.Token;
        using var sourceScope = ProjectExternalSourceRuntime.Use(session.ExternalSources);
        var clips = GetVisualClips(session.Clips);
        var hashes = clips.Select(c => FrameHashHelper.GetClipHash(clips, c, request.StartFrame)).ToArray();
        var keySource = $"frame-content|{TimelineSegmentCacheVersion}|{session.CacheNamespace}|{session.SnapshotHash}|{request.FrameHashMode}|{request.StartFrame}|{request.Length}|{request.Width}|{request.Height}|{request.FrameRate}|{request.IncludeAudio}|{string.Join(',', hashes.Select(h => h.Hash))}";
        var relativePath = hashes.All(h => h.CanReuse)
            ? $"thumbs/projectFrameCut_Render_segment_{ComputeTextHash(keySource)}.mp4"
            : $"thumbs/unCacheablePerClip/{session.Id:N}/segment_{Guid.NewGuid():N}.mp4";
        return await RenderSegmentInternalAsync(session, request, relativePath, isPreview: true, cancellationToken).ConfigureAwait(false);
    }

    private async ValueTask<RenderArtifact> RenderAudioSegmentAsync(AudioSegmentRequest request, CancellationToken cancellationToken)
    {
        var session = GetSession(request.SessionId);
        using var operation = session.BeginOperation(cancellationToken);
        cancellationToken = operation.Token;
        using var sourceScope = ProjectExternalSourceRuntime.Use(session.ExternalSources);
        if (!HasAudio(session)) throw new InvalidOperationException("The project does not contain audio sources.");
        var sampleRate = Math.Max(8000, request.SampleRate);
        var channels = Math.Clamp(request.Channels, 1, 8);
        var frameRate = Math.Max(1, request.FrameRate);
        var length = request.Length > 0 ? request.Length : session.Duration;
        var key = ComputeTextHash($"{AudioSegmentCacheVersion}|{session.CacheNamespace}|{session.SnapshotHash}|audio|{request.StartFrame}|{length}|{frameRate}|{sampleRate}|{channels}");
        var relativePath = $"thumbs/projectFrameCut_Render_audio_{key}.wav";
        var finalPath = _artifacts.ResolveProjectPath(session.ProjectRoot, relativePath);
        var cacheHit = File.Exists(finalPath);
        if (!cacheHit)
        {
            await session.RenderGate.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                if (!File.Exists(finalPath))
                {
                    var temporaryPath = _artifacts.CreateTemporaryPath(finalPath);
                    try
                    {
                        using (var writer = new AudioWriter(temporaryPath, sampleRate, channels, "pcm_s16le"))
                        {
                            var composer = new AudioComposer<float>
                            {
                                Clips = [],
                                SoundTracks = session.SoundTracks,
                                Writer = writer,
                                StartFrame = request.StartFrame,
                                Duration = length,
                            };
                            await Task.Run(() => composer.Compose(frameRate, sampleRate, channels, 40960, cancellationToken), cancellationToken).ConfigureAwait(false);
                            writer.Finish();
                        }
                        _artifacts.CommitTemporaryFile(temporaryPath, finalPath);
                    }
                    finally
                    {
                        try { File.Delete(temporaryPath); } catch { }
                    }
                }
            }
            finally
            {
                session.RenderGate.Release();
            }
        }
        return _artifacts.Register(session.Id, session.ProjectRoot, relativePath, "audio/wav", cacheHit, isPreview: true, frameRate: frameRate);
    }

    private RenderJob StartRenderProject(RenderProjectRequest request)
    {
        var session = GetSession(request.SessionId);
        using var operation = session.BeginOperation();
        using var sourceScope = ProjectExternalSourceRuntime.Use(session.ExternalSources);
        var jobId = Guid.NewGuid();
        var entry = new JobEntry(new RenderJob
        {
            JobId = jobId,
            SessionId = session.Id,
            State = RenderJobState.Queued,
            CreatedAtUtc = DateTime.UtcNow,
            UpdatedAtUtc = DateTime.UtcNow,
            ProjectRoot = session.ProjectRoot,
            ProjectName = string.IsNullOrWhiteSpace(request.ProjectName) ? session.ProjectName : request.ProjectName,
            OutputPath = request.OutputPath,
            Background = request.Background,
        }, PersistJobs);
        _jobs[jobId] = entry;
        PersistJobs();
        _ = Task.Run(() => RunProjectJobAsync(entry, request, session));
        return entry.Snapshot();
    }

    private async Task RunProjectJobAsync(JobEntry entry, RenderProjectRequest request, BackendSession session)
    {
        try
        {
            using var operation = session.BeginOperation(entry.Cancellation.Token);
            using var sourceScope = ProjectExternalSourceRuntime.Use(session.ExternalSources);
            entry.Update(job => job.State = RenderJobState.Running);
            ReportProgress(entry);
            var safeName = SanitizeFileName(request.OutputFileName, "render.mp4");
            var relativePath = $"cache/render/{entry.JobId:N}/{safeName}";
            var segment = new TimelineSegmentRequest
            {
                FrameHashMode = request.FrameHashMode,
                SessionId = request.SessionId,
                StartFrame = 0,
                Length = session.Duration,
                Width = request.Width,
                Height = request.Height,
                FrameRate = request.FrameRate,
                IncludeAudio = request.IncludeAudio,
            };
            var artifact = await RenderSegmentInternalAsync(session, segment, relativePath, isPreview: false, operation.Token,
                (progress, eta) =>
                {
                    entry.Update(job =>
                    {
                        job.Progress = progress;
                        job.EstimatedRemainingTicks = eta.Ticks;
                    });
                    ReportProgress(entry);
                }, request.Encoder, request.PixelFormat).ConfigureAwait(false);
            entry.Update(job =>
            {
                job.State = RenderJobState.Completed;
                job.Progress = 1;
                job.Artifact = artifact;
            });
        }
        catch (OperationCanceledException)
        {
            entry.Update(job => job.State = RenderJobState.Canceled);
        }
        catch (Exception ex)
        {
            Log(ex, $"Render job {entry.JobId}", this);
            entry.Update(job =>
            {
                job.State = RenderJobState.Failed;
                job.Error = new RemoteError(ex);
            });
        }
        finally
        {
            PersistJobs();
            var snapshot = entry.Snapshot();
            if (snapshot.State is RenderJobState.Completed or RenderJobState.Failed or RenderJobState.Canceled)
            {
                try { _completionSink?.Invoke(snapshot); } catch (Exception ex) { Log(ex, "Render completion notification", this); }
            }
        }
    }

    private async ValueTask<RenderArtifact> RenderSegmentInternalAsync(BackendSession session, TimelineSegmentRequest request, string relativePath, bool isPreview, CancellationToken cancellationToken, Action<double, TimeSpan>? progress = null, string encoder = "libx264", string pixelFormat = "AV_PIX_FMT_YUV420P")
    {
        var width = Math.Max(2, request.Width + (request.Width & 1));
        var height = Math.Max(2, request.Height + (request.Height & 1));
        var frameRate = Math.Max(1, request.FrameRate);
        var length = request.Length > 0 ? request.Length : session.Duration;
        var finalPath = _artifacts.ResolveProjectPath(session.ProjectRoot, relativePath);
        var cacheHit = File.Exists(finalPath);
        if (cacheHit) return _artifacts.Register(session.Id, session.ProjectRoot, relativePath, "video/mp4", true, isPreview, width, height, frameRate);

        await session.RenderGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (File.Exists(finalPath)) return _artifacts.Register(session.Id, session.ProjectRoot, relativePath, "video/mp4", true, isPreview, width, height, frameRate);

            var outputTemporaryPath = _artifacts.CreateTemporaryPath(finalPath);
            var videoTemporaryPath = Path.Combine(Path.GetDirectoryName(outputTemporaryPath)!, $".video-{Guid.NewGuid():N}.mp4");
            var audioTemporaryPath = Path.Combine(Path.GetDirectoryName(outputTemporaryPath)!, $".audio-{Guid.NewGuid():N}.wav");
            VideoBuilder? builder = null;
            try
            {
                builder = new VideoBuilder(videoTemporaryPath, width, height, frameRate, encoder, pixelFormat)
                {
                    Duration = uint.MaxValue,
                    BlockWrite = true,
                };
                var renderer = new Renderer
                {
                    AudioSamples = new AudioSampleSource(session.Clips, session.SoundTracks, frameRate,
                        Math.Max(8000, request.AudioSampleRate), Math.Clamp(request.AudioChannels, 1, 8)),
                    FrameHashMode = request.FrameHashMode,
                    StartFrame = request.StartFrame,
                    Duration = length,
                    builder = builder,
                    Clips = GetVisualClips(session.Clips),
                    Use16Bit = false,
                    AutoCenterImplicitClip = true,
                    MaxThreads = 1,
                    ProjectRelativeWidth = session.Width,
                    ProjectRelativeHeight = session.Height,
                };
                if (progress is not null) renderer.OnProgressChanged += progress;
                foreach (var clip in renderer.Clips) _previewClipInitializations.Remove(clip);
                renderer.PrepareRender(cancellationToken);
                await renderer.GoRender(cancellationToken).ConfigureAwait(false);
                if (progress is not null) renderer.OnProgressChanged -= progress;
                builder.Writer.Finish();
                builder.Dispose();
                builder = null;

                var hasAudio = request.IncludeAudio && HasAudio(session);
                if (hasAudio)
                {
                    using var writer = new AudioWriter(audioTemporaryPath, Math.Max(8000, request.AudioSampleRate), Math.Clamp(request.AudioChannels, 1, 8), "pcm_s16le");
                    var composer = new AudioComposer<float>
                    {
                        Clips = [],
                        SoundTracks = session.SoundTracks,
                        Writer = writer,
                        StartFrame = request.StartFrame,
                        Duration = length,
                        SampleSource = renderer.AudioSamples,
                    };
                    await Task.Run(() => composer.Compose(frameRate, request.AudioSampleRate, request.AudioChannels, 40960, cancellationToken), cancellationToken).ConfigureAwait(false);
                    writer.Finish();
                    await Task.Run(() => VideoAudioMuxer.MuxFromFiles(videoTemporaryPath, audioTemporaryPath, outputTemporaryPath, true), cancellationToken).ConfigureAwait(false);
                }
                else
                {
                    File.Move(videoTemporaryPath, outputTemporaryPath, overwrite: true);
                }

                _artifacts.CommitTemporaryFile(outputTemporaryPath, finalPath);
            }
            finally
            {
                try { builder?.Dispose(); } catch { }
                try { File.Delete(outputTemporaryPath); } catch { }
                try { File.Delete(videoTemporaryPath); } catch { }
                try { File.Delete(audioTemporaryPath); } catch { }
            }
            return _artifacts.Register(session.Id, session.ProjectRoot, relativePath, "video/mp4", false, isPreview, width, height, frameRate);
        }
        finally
        {
            session.RenderGate.Release();
        }
    }

    private void ReportProgress(JobEntry entry)
    {
        try { _progressSink?.Invoke(entry.Snapshot()); }
        catch (Exception ex) { Log(ex, "Render progress notification", this); }
    }

    private RenderJob GetJob(Guid jobId)
        => _jobs.TryGetValue(jobId, out var entry) ? entry.Snapshot() : throw new KeyNotFoundException($"Render job '{jobId}' was not found.");

    private RenderJob CancelJob(Guid jobId)
    {
        if (!_jobs.TryGetValue(jobId, out var entry)) throw new KeyNotFoundException($"Render job '{jobId}' was not found.");
        entry.Cancellation.Cancel();
        PersistJobs();
        return entry.Snapshot();
    }

    private List<RenderJob> ListJobs(ListRenderJobsRequest request)
    {
        LoadPersistedJobs();
        var jobs = _jobs.Values.Select(static entry => entry.Snapshot());
        if (!string.IsNullOrWhiteSpace(request.ProjectRoot))
        {
            var root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(request.ProjectRoot));
            jobs = jobs.Where(job => string.Equals(Path.GetFullPath(job.ProjectRoot), root,
                OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal));
        }
        if (!request.IncludeCompleted)
            jobs = jobs.Where(static job => job.State is RenderJobState.Queued or RenderJobState.Running);
        return jobs.OrderByDescending(static job => job.UpdatedAtUtc).ToList();
    }

    private int _loadedPersistedJobs;
    private void LoadPersistedJobs()
    {
        if (_loadedPersistedJobs != 0) return;
        _loadedPersistedJobs = 1;
        var path = JobsStatePath;
        if (path is null || !File.Exists(path)) return;
        try
        {
            var jobs = JsonSerializer.Deserialize<List<RenderJob>>(File.ReadAllText(path), _jsonOptions) ?? [];
            foreach (var job in jobs)
            {
                if (job.State is RenderJobState.Queued or RenderJobState.Running)
                {
                    job.State = RenderJobState.Failed;
                    job.Error = new RemoteError { Code = RenderErrorCode.BackendFailure, Message = "The render worker was restarted before this job completed." };
                }
                _jobs.TryAdd(job.JobId, new JobEntry(job, PersistJobs));
            }
        }
        catch (Exception ex) { Log(ex, "Load persisted render jobs", this); }
    }

    private string? JobsStatePath => _stateRoot is null ? null : Path.Combine(_stateRoot, "RenderJobs", "jobs.json");

    private void PersistJobs()
    {
        var path = JobsStatePath;
        if (path is null) return;
        try
        {
            lock (_persistGate)
            {
                Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                var temp = path + ".tmp";
                File.WriteAllText(temp, JsonSerializer.Serialize(_jobs.Values.Select(static entry => entry.Snapshot()).ToList(), _jsonOptions));
                File.Move(temp, path, overwrite: true);
            }
        }
        catch (Exception ex)
        {
            Log(ex, "Persist render jobs", this);
        }
    }

    private EmptyResponse ReleaseArtifact(ArtifactRequest request)
    {
        using var operation = GetSession(request.SessionId).BeginOperation();
        if (!_artifacts.Release(request.SessionId, request.ArtifactId)) throw new FileNotFoundException($"Artifact '{request.ArtifactId}' was not found for this session.");
        return new();
    }

    public (byte[] Content, string Path) ReadArtifact(ArtifactRequest request)
    {
        var session = GetSession(request.SessionId);
        using var operation = session.BeginOperation();
        using var sourceScope = ProjectExternalSourceRuntime.Use(session.ExternalSources);
        if (!_artifacts.TryGetPath(request.SessionId, request.ArtifactId, out var path))
            throw new FileNotFoundException($"Artifact '{request.ArtifactId}' was not found for this session.");
        var content = File.ReadAllBytes(path);
        TouchTrackedPreviewCache(session.ProjectRoot, path);
        return (content, path);
    }

    private BackendSession GetSession(Guid sessionId)
    {
        ThrowIfClosing();
        return _sessions.TryGetValue(sessionId, out var session) ? session : throw new KeyNotFoundException($"Render session '{sessionId}' was not found.");
    }

    private void ThrowIfClosing()
    {
        if (Volatile.Read(ref _disposed) != 0) throw new OperationCanceledException("Render backend is closing.");
    }

    private IClip[] CreateClips(DraftStructureJSON draft, IReadOnlyDictionary<string, string> assets, string proxyRoot, string projectRoot,
        CancellationToken cancellationToken, BackendSession? previous, IReadOnlyDictionary<Guid, string> signatures)
    {
        var clips = new List<IClip>();
        var oldClips = previous?.Clips.ToDictionary(clip => clip.Id);
        try
        {
            foreach (var dto in draft.Clips)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (dto.ClipType == ClipMode.MarkingClip) continue;
                if (previous is not null && previous.ClipSignatures.TryGetValue(dto.Id, out var signature)
                    && signature == signatures[dto.Id] && oldClips!.TryGetValue(dto.Id, out var oldClip)
                    && !ClipInitializationFailure.IsMarked(oldClip))
                {
                    clips.Add(oldClip);
                    continue;
                }
                var clip = PluginManager.CreateClip(JsonSerializer.SerializeToElement(dto, _jsonOptions));
                clips.Add(clip);
                ResolveSourcePath(clip, dto.FilePath, assets, proxyRoot, projectRoot);
                if (clip.ClipType == ClipMode.AudioClip) continue;
                try
                {
                    clip.ReInit(IPicture.PicturePixelMode.BytePicture);
                    clip.EffectsInstances = EffectHelper.GetClipEffectsInstances(clip);
                    ClipInitializationFailure.Clear(clip);
                    _previewClipInitializations.GetValue(clip, static _ => new()).PixelMode = IPicture.PicturePixelMode.BytePicture.Value;
                }
                catch (OperationCanceledException) { throw; }
                catch (Exception ex)
                {
                    ClipInitializationFailure.Mark(clip, "Source or ResolveEffect", ex);
                }
                cancellationToken.ThrowIfCancellationRequested();
            }
            return clips.ToArray();
        }
        catch
        {
            DisposeResources(clips, [], previous);
            throw;
        }
    }

    private ISoundTrack[] CreateSoundTracks(DraftStructureJSON draft, IReadOnlyCollection<IClip> clips, IReadOnlyDictionary<string, string> assets,
        string projectRoot, CancellationToken cancellationToken, BackendSession? previous, IReadOnlyDictionary<string, string> signatures)
    {
        var tracks = new List<ISoundTrack>();
        var oldTracks = previous?.SoundTracks.ToDictionary(track => track.Id);
        try
        {
            foreach (var dto in draft.SoundTracks)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (previous is not null && previous.TrackSignatures.TryGetValue(dto.Id, out var signature)
                    && signature == signatures[dto.Id] && oldTracks!.TryGetValue(dto.Id, out var oldTrack))
                {
                    tracks.Add(oldTrack);
                    continue;
                }
                var track = PluginManager.CreateSoundTrack(JsonSerializer.SerializeToElement(dto, _jsonOptions));
                tracks.Add(track);
                track.ExtraData = dto.MetaData ?? new();
                track.Ratio = dto.SecondPerFrameRatio > 0 ? dto.SecondPerFrameRatio : 1f;
                if (track.ExtraData.TryGetValue("Volume", out object? volumeValue))
                {
                    track.Volume = volumeValue switch
                    {
                        double value => (float)value,
                        float value => value,
                        JsonElement value when value.TryGetDouble(out double parsedDouble) => (float)parsedDouble,
                        _ when float.TryParse(volumeValue?.ToString(), System.Globalization.NumberStyles.Any,
                            System.Globalization.CultureInfo.InvariantCulture, out float parsedFloat) => parsedFloat,
                        _ => 1f,
                    };
                }
                ResolveSourcePath(track, dto.FilePath, assets, projectRoot);
                if (SoundTrackMetadata.ReadBool(track.ExtraData, SoundTrackMetadata.EnabledKey, true)) SoundTrackMetadata.ReInit(track);
            }

            if (previous is not null)
            {
                tracks.AddRange(previous.SoundTracks.Where(track => !previous.TrackSignatures.ContainsKey(track.Id)
                    && SoundTrackMetadata.ReadSourceClipId(track.ExtraData) is Guid id
                    && !tracks.Any(candidate => candidate.Id == track.Id || SoundTrackMetadata.ReadSourceClipId(candidate.ExtraData) == id)
                    && clips.FirstOrDefault(clip => clip.Id == id) is { } clip && previous.Clips.Contains(clip, ReferenceEqualityComparer.Instance)));
            }
            cancellationToken.ThrowIfCancellationRequested();
            SoundTrackMetadata.AddMissingLegacyTracks(clips, tracks, message => Log($"[RenderRPC] {message}", "warn"), cancellationToken: cancellationToken);
            return tracks.ToArray();
        }
        catch
        {
            DisposeResources([], tracks, previous);
            throw;
        }
    }

    private static void ResolveSourcePath(IClip clip, string? dtoPath, IReadOnlyDictionary<string, string> assets, string proxyRoot, string projectRoot)
    {
        var path = clip.FilePath ?? dtoPath;
        if (path?.StartsWith('$') == true && assets.TryGetValue(path[1..], out var assetPath)) path = assetPath;
        path = ResolveProjectSourcePath(projectRoot, path);
        if (!string.IsNullOrWhiteSpace(path) && path[0] != '#' && !string.IsNullOrWhiteSpace(proxyRoot))
        {
            var proxy = Path.Combine(proxyRoot, $"{Path.GetFileNameWithoutExtension(path)}.proxy.mp4");
            if (File.Exists(proxy)) path = proxy;
        }
        if (!string.IsNullOrWhiteSpace(path))
        {
            try { clip.FilePath = path; } catch (InvalidOperationException) { }
        }
    }

    private static void ResolveSourcePath(ISoundTrack track, string? dtoPath, IReadOnlyDictionary<string, string> assets, string projectRoot)
    {
        var path = track.FilePath ?? dtoPath;
        if (path?.StartsWith('$') == true && assets.TryGetValue(path[1..], out var assetPath)) path = assetPath;
        if (path?.StartsWith('$') == true)
        {
            throw new FileNotFoundException(
                $"Soundtrack '{track.Name}' references asset '{path[1..]}', but that asset was not supplied to the render backend.",
                path);
        }
        path = ResolveProjectSourcePath(projectRoot, path);
        if (!string.IsNullOrWhiteSpace(path))
        {
            try { track.FilePath = path; } catch (InvalidOperationException) { }
        }
    }

    private static string? ResolveProjectSourcePath(string projectRoot, string? path)
    {
        if (string.IsNullOrWhiteSpace(path) || path.StartsWith('$') || path.StartsWith('#')) return path;
        return Path.IsPathRooted(path) ? Path.GetFullPath(path) : Path.GetFullPath(Path.Combine(projectRoot, path));
    }

    private static uint ResolveDuration(DraftStructureJSON draft)
    {
        ulong duration = Math.Max(draft.Duration, draft.AudioDuration);
        foreach (var clip in draft.Clips)
        {
            // ClipDraftDTO.Duration is the finite timeline width exported by DraftPage,
            // including for assets whose source itself is infinite/open-ended.
            var end = (ulong)clip.StartFrame + clip.Duration;
            if (end > uint.MaxValue)
            {
                Log($"[RenderRPC] Ignoring overflowing timeline end for clip {clip.Id}/{clip.Name}: start={clip.StartFrame}, duration={clip.Duration}, draftDuration={draft.Duration}.", "warn");
                duration = Math.Max(duration, (ulong)clip.StartFrame + 1);
                continue;
            }
            duration = Math.Max(duration, end);
        }
        foreach (var track in draft.SoundTracks)
        {
            var end = (ulong)track.StartFrame + track.Duration;
            if (end > uint.MaxValue)
            {
                Log($"[RenderRPC] Ignoring overflowing timeline end for soundtrack {track.Id}/{track.Name}: start={track.StartFrame}, duration={track.Duration}, draftDuration={draft.Duration}.", "warn");
                duration = Math.Max(duration, (ulong)track.StartFrame + 1);
                continue;
            }
            duration = Math.Max(duration, end);
        }
        // Every value contributing to duration is now range-checked. Keep this clamp as a
        // last-resort compatibility guard for malformed legacy project data.
        duration = Math.Min(duration, uint.MaxValue);
        return (uint)duration;
    }

    private static bool HasAudio(BackendSession session)
        => session.SoundTracks.Any(track => SoundTrackMetadata.ReadBool(track.ExtraData, SoundTrackMetadata.EnabledKey, true));

    private static IClip[] GetVisualClips(IEnumerable<IClip> clips)
        => clips.Where(static clip => clip.ClipType != ClipMode.AudioClip).ToArray();

    private void InitializePreviewClips(IReadOnlyList<IClip> clips, IPicture.PicturePixelMode pixelMode, uint frameIndex, CancellationToken token)
    {
        // Preserve source and effect caches while a clip's output pixel format stays unchanged.
        foreach (var clip in clips)
        {
            token.ThrowIfCancellationRequested();
            var state = _previewClipInitializations.GetValue(clip, static _ => new());
            if (state.PixelMode == pixelMode.Value && !ClipInitializationFailure.IsMarked(clip) && !clip.ContainsFrame(frameIndex)) continue;
            if (clip.NeedFilePath && clip.FilePath is { Length: > 0 } path && path[0] != '#' && !File.Exists(path))
            {
                if (!ClipInitializationFailure.IsMarked(clip))
                    ClipInitializationFailure.Mark(clip, "SourceNotFound", new FileNotFoundException($"Source for clip '{clip.Name}' is not available.", path));
                state.PixelMode = 0;
                continue;
            }
            if ((state.PixelMode == pixelMode.Value && !ClipInitializationFailure.IsMarked(clip))
                || ClipInitializationFailure.HasDeferredFailures(clip.ExtraData)) continue;
            try
            {
                ClipInitializationFailure.Clear(clip);
                clip.ReInit(pixelMode);
                if (!ClipInitializationFailure.IsMarked(clip)) state.PixelMode = pixelMode.Value;
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex)
            {
                ClipInitializationFailure.Mark(clip, "Source or ResolveEffect", ex);
                Log(ex, $"Initialize preview clip {clip.Id}", this);
            }
        }
    }

    private string ResolveAssetPath(BackendSession session, AssetMetadataRequest request)
    {
        if (!string.IsNullOrWhiteSpace(request.AssetId) && session.Assets.TryGetValue(request.AssetId, out var assetPath))
            return RemoteRpcVideoSource.IsExternalPath(assetPath) || RemoteRpcAudioSource.IsExternalPath(assetPath) ? assetPath : Path.GetFullPath(assetPath);
        if (!string.IsNullOrWhiteSpace(request.ProjectRelativePath)) return _artifacts.ResolveProjectPath(session.ProjectRoot, request.ProjectRelativePath);
        throw new ArgumentException("Either AssetId or ProjectRelativePath is required.");
    }

    private void CancelJobsForSession(Guid sessionId)
    {
        foreach (var entry in _jobs.Values.Where(entry => entry.SessionId == sessionId)) entry.Cancellation.Cancel();
    }

    private static string ResolveMediaType(string path) => Path.GetExtension(path).ToLowerInvariant() switch
    {
        ".vfd" => "application/x-projectframecut-vfd", ".vfd2m" => "application/x-projectframecut-vfd2m", ".png" => "image/png", ".jpg" or ".jpeg" => "image/jpeg", ".wav" => "audio/wav", ".mp3" => "audio/mpeg",
        ".mp4" => "video/mp4", ".mov" => "video/quicktime", ".webm" => "video/webm", _ => "application/octet-stream",
    };

    private static string SanitizeFileName(string value, string fallback)
    {
        var name = Path.GetFileName(string.IsNullOrWhiteSpace(value) ? fallback : value);
        foreach (var invalid in Path.GetInvalidFileNameChars()) name = name.Replace(invalid, '_');
        return string.IsNullOrWhiteSpace(name) ? fallback : name;
    }

    private PreviewCacheIndex GetPreviewCacheIndex(string projectRoot)
        => _previewCacheIndexes.GetOrAdd(projectRoot, static root => new(Path.Combine(root, "thumbs")));

    private void TrackPreviewCacheAccess(string projectRoot, string relativePath, Guid? clipId, uint frameIndex, string configuration)
    {
        try
        {
            relativePath = relativePath.Replace(Path.DirectorySeparatorChar, '/');
            if (TryResolveTrackedPreviewPath(projectRoot, relativePath, out _))
                GetPreviewCacheIndex(projectRoot).Track(relativePath, clipId, frameIndex, configuration);
        }
        catch (Exception ex) { Log(ex, "Track preview cache access", this); }
    }

    private void TouchTrackedPreviewCache(string projectRoot, string fullPath)
    {
        try
        {
            var path = Path.GetRelativePath(projectRoot, fullPath).Replace(Path.DirectorySeparatorChar, '/');
            if (TryResolveTrackedPreviewPath(projectRoot, path, out _)) GetPreviewCacheIndex(projectRoot).Touch(path);
        }
        catch (Exception ex) { Log(ex, "Touch preview cache artifact", this); }
    }

    private void MaintainPreviewCache(string projectRoot)
    {
        try
        {
            GetPreviewCacheIndex(projectRoot);
            FlushPreviewCache(projectRoot);
            lock (_previewCachePersistGate)
                _previewCacheTimer ??= new Timer(_ => FlushPreviewCaches(), null, TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(5));
        }
        catch (Exception ex) { Log(ex, "Maintain preview cache", this); }
    }

    private void FlushPreviewCaches()
    {
        if (!Monitor.TryEnter(_previewCachePersistGate)) return;
        try
        {
            foreach (var root in _previewCacheIndexes.Keys) FlushPreviewCache(root);
        }
        finally { Monitor.Exit(_previewCachePersistGate); }
    }

    private void FlushPreviewCache(string projectRoot)
    {
        try
        {
            if (_previewCacheIndexes.TryGetValue(projectRoot, out var index))
                index.Maintain(PreviewCacheRetention,
                    path => TryResolveTrackedPreviewPath(projectRoot, path, out var fullPath) ? fullPath : null);
        }
        catch (Exception ex) { Log(ex, "Save preview cache index", this); }
    }

    private bool TryResolveTrackedPreviewPath(string projectRoot, string relativePath, out string fullPath)
    {
        fullPath = string.Empty;
        if (string.IsNullOrWhiteSpace(relativePath)) return false;
        try
        {
            fullPath = _artifacts.ResolveProjectPath(projectRoot, relativePath);
            var thumbsRoot = Path.GetFullPath(Path.Combine(projectRoot, "thumbs"))
                .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
                + Path.DirectorySeparatorChar;
            var comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
            return fullPath.StartsWith(thumbsRoot, comparison)
                && !CacheIndexDatabase.IsIndexFile(fullPath);
        }
        catch
        {
            return false;
        }
    }

    private static string ComputeTextHash(string value) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value))).ToLowerInvariant();
    private static string ComputeFileHash(string path) { using var stream = File.OpenRead(path); return Convert.ToHexString(SHA256.HashData(stream)); }
    private static T Read<T>(RenderRequestEnvelope request) => RenderRpcSerializer.Deserialize<T>(request.Payload);
    private static RenderResponseEnvelope Success<T>(RenderRequestEnvelope request, T payload) => new() { RequestId = request.RequestId, Payload = RenderRpcSerializer.Serialize(payload) };
    private static RenderResponseEnvelope Failure(RenderRequestEnvelope request, RenderErrorCode code, string message, string details = "", bool retryable = false) => new()
    {
        RequestId = request.RequestId,
        Error = new RemoteError { Code = code, Message = message, Details = details, Retryable = retryable },
    };

    private static RenderResponseEnvelope Failure(RenderRequestEnvelope request, RenderErrorCode code, Exception exception, bool retryable = false, string? customMessage = null, string? customDetails = null) => new()
    {
        RequestId = request.RequestId,
        Error = new RemoteError(exception, code, retryable, customMessage, customDetails),
    };

    public async ValueTask DisposeAsync()
    {
        await _projectLifecycleGate.WaitAsync().ConfigureAwait(false);
        try
        {
            if (_disposed != 0) return;
            Volatile.Write(ref _disposed, 1);
            foreach (var job in _jobs.Values) job.Cancellation.Cancel();
            await StopAllDynamicPreviewsAsync().ConfigureAwait(false);
            foreach (var session in _sessions.Values) await session.DisposeAsync().ConfigureAwait(false);
            _sessions.Clear();
            foreach (var proxies in _previewProxies.Values) await proxies.DisposeAsync().ConfigureAwait(false);
            _previewProxies.Clear();
            if (_previewCacheTimer is not null) await _previewCacheTimer.DisposeAsync().ConfigureAwait(false);
            FlushPreviewCaches();
            _previewCacheIndexes.Clear();
            foreach (var sources in _externalSources.Values) await sources.CloseAsync().ConfigureAwait(false);
            _externalSources.Clear();
            if (PluginManager.ProjectPluginIds.Count > 0 && PluginManager.ProjectPluginUnloader is not null)
                await PluginManager.ProjectPluginUnloader().ConfigureAwait(false);
            PersistJobs();
        }
        finally { _projectLifecycleGate.Release(); }
    }

    private sealed class PreviewClipInitialization
    {
        public int PixelMode { get; set; }
    }

    private sealed partial class BackendSession(
        Guid id, string projectRoot, string projectJson, string timelineJson, string projectName,
        int width, int height, int frameRate, uint duration, IClip[] clips, ISoundTrack[] soundTracks,
        IReadOnlyDictionary<string, string> assets, string snapshotHash, FrameHashIndex hashIndex,
        string cacheNamespace, string sourceConfiguration, IReadOnlyDictionary<Guid, string> clipSignatures,
        IReadOnlyDictionary<string, string> trackSignatures) : IAsyncDisposable
    {
        public Guid Id { get; } = id;
        public string ProjectRoot { get; } = projectRoot;
        public string ProjectJson { get; } = projectJson;
        public string TimelineJson { get; } = timelineJson;
        public string ProjectName { get; } = projectName;
        public int Width { get; } = width;
        public int Height { get; } = height;
        public int FrameRate { get; } = frameRate;
        public uint Duration { get; } = duration;
        public IClip[] Clips { get; } = clips;
        public ISoundTrack[] SoundTracks { get; } = soundTracks;
        public AudioSampleSource AudioSamples { get; } = new(clips, soundTracks, frameRate);
        public IReadOnlyDictionary<string, string> Assets { get; } = assets;
        public string SnapshotHash { get; } = snapshotHash;
        public FrameHashIndex HashIndex { get; } = hashIndex;
        public string SourceConfiguration { get; } = sourceConfiguration;
        public IReadOnlyDictionary<Guid, string> ClipSignatures { get; } = clipSignatures;
        public IReadOnlyDictionary<string, string> TrackSignatures { get; } = trackSignatures;
        public ProjectExternalSourceHost ExternalSources { get; set; } = null!;
        public string CacheNamespace => string.IsNullOrEmpty(ExternalSources.CacheToken) && string.IsNullOrEmpty(ExternalVideoSourceRegistry.CacheToken)
            && string.IsNullOrEmpty(ExternalAudioSourceRegistry.CacheToken) && string.IsNullOrEmpty(ExternalMaskSourceRegistry.CacheToken)
            ? cacheNamespace : ComputeTextHash(cacheNamespace + ExternalSources.CacheToken + ExternalVideoSourceRegistry.CacheToken
                + ExternalAudioSourceRegistry.CacheToken + ExternalMaskSourceRegistry.CacheToken);
        public SemaphoreSlim RenderGate { get; } = new(1, 1);
        public FrameHashCache NodeCache { get; } = new();
        private readonly object _lifetimeGate = new();
        private readonly CancellationTokenSource _lifetime = new();
        private readonly TaskCompletionSource _operationsCompleted = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int _activeOperations;
        private Task? _disposeTask;
        private readonly object _previewAudioGate = new();
        private PreviewAudioSession? _previewAudio;
        private readonly IReadOnlyDictionary<uint, string> _frameHashLookup = hashIndex.FrameHashes
            .GroupBy(entry => entry.FrameIndex)
            .ToDictionary(group => group.Key, group => group.Last().Hash);
        private readonly IReadOnlyDictionary<Guid, IReadOnlyDictionary<uint, string>> _clipHashLookup = hashIndex.ClipHashes
            .ToDictionary(
                entry => entry.ClipId,
                entry => (IReadOnlyDictionary<uint, string>)entry.FrameHashes
                    .GroupBy(frame => frame.FrameIndex)
                    .ToDictionary(group => group.Key, group => group.Last().Hash));

        public string GetFrameHash(uint frameIndex)
            => _frameHashLookup.TryGetValue(frameIndex, out var hash)
                ? hash
                : Timeline.GetFrameHash(GetVisualClips(Clips), frameIndex);

        public string GetClipFrameHash(Guid clipId, uint frameIndex)
            => _clipHashLookup.TryGetValue(clipId, out var clipHashes)
                && clipHashes.TryGetValue(frameIndex, out var hash)
                ? hash
                : Clips.FirstOrDefault(clip => clip.Id == clipId) is { } clip
                    ? Timeline.GetClipFrameHash(GetVisualClips(Clips), clip, frameIndex)
                    : "__error__";

        public RenderSession ToContract() => new()
        {
            SessionId = Id, ProjectName = ProjectName, ProjectWidth = Width, ProjectHeight = Height,
            FrameRate = FrameRate, Duration = Duration, ClipCount = Clips.Length, SnapshotHash = SnapshotHash,
            HashIndex = HashIndex,
            CacheableClipIds = Clips.Where(clip => Timeline.CanCacheClipFrame(Clips, clip)).Select(clip => clip.Id).ToList(),
        };

        public PreviewAudioSession GetPreviewAudio(IAudioPreviewSinkFactory factory)
        {
            lock (_previewAudioGate) return _previewAudio ??= new PreviewAudioSession(factory, SoundTracks, Duration, AudioSamples);
        }

        public bool TryGetPreviewAudio([System.Diagnostics.CodeAnalysis.NotNullWhen(true)] out PreviewAudioSession? audio)
        {
            lock (_previewAudioGate) { audio = _previewAudio; return audio is not null; }
        }

        public SessionOperation BeginOperation(CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            lock (_lifetimeGate)
            {
                if (_disposeTask is not null) throw new OperationCanceledException($"Render session '{Id}' is closing.");
                var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _lifetime.Token);
                _activeOperations++;
                return new(this, cts);
            }
        }

        private void EndOperation()
        {
            lock (_lifetimeGate)
            {
                if (--_activeOperations == 0 && _disposeTask is not null) _operationsCompleted.TrySetResult();
            }
        }

        public ValueTask DisposeAsync() => DisposeAsync(null);

        public ValueTask DisposeAsync(BackendSession? retained)
        {
            lock (_lifetimeGate)
            {
                if (_disposeTask is not null) return new(_disposeTask);
                if (_activeOperations == 0) _operationsCompleted.TrySetResult();
                return new(_disposeTask = Task.Run(() => DisposeCoreAsync(retained)));
            }
        }

        private async Task DisposeCoreAsync(BackendSession? retained)
        {
            try { _lifetime.Cancel(); }
            catch (Exception ex) { Log(ex, $"Cancel render session {Id}"); }
            await _operationsCompleted.Task.ConfigureAwait(false);
            if (_previewAudio is not null)
            {
                try { await _previewAudio.DisposeAsync().ConfigureAwait(false); }
                catch (Exception ex) { Log(ex, $"Dispose preview audio for render session {Id}"); }
            }
            DisposeResources(Clips, SoundTracks, retained);
            NodeCache.Dispose();
            RenderGate.Dispose();
            _lifetime.Dispose();
            var materializedRoot = Path.Combine(ProjectRoot, "thumbs", "unCacheablePerClip", Id.ToString("N"));
            try
            {
                if ((retained is null || retained.Id != Id || retained.ProjectRoot != ProjectRoot) && Directory.Exists(materializedRoot))
                    Directory.Delete(materializedRoot, true);
            }
            catch (Exception ex) { Log(ex, $"Delete temporary previews from {materializedRoot}"); }
            if (retained is null) LogDiagnostic($"[RenderRPC] Disposed render session {Id} after its requests completed.");
        }

        public sealed class SessionOperation(BackendSession session, CancellationTokenSource cancellation) : IDisposable
        {
            public CancellationToken Token => cancellation.Token;

            public void Dispose()
            {
                cancellation.Dispose();
                session.EndOperation();
            }
        }
    }

    private sealed class JobEntry
    {
        private readonly object _gate = new();
        private readonly RenderJob _job;
        private readonly Action _changed;
        public JobEntry(RenderJob job, Action? changed = null) { _job = job; _changed = changed ?? new Action(() => { }); Cancellation = new CancellationTokenSource(); }
        public Guid JobId => _job.JobId;
        public Guid SessionId => _job.SessionId;
        public CancellationTokenSource Cancellation { get; }
        public void Update(Action<RenderJob> update) { lock (_gate) { update(_job); _job.UpdatedAtUtc = DateTime.UtcNow; } _changed(); }
        public RenderJob Snapshot() { lock (_gate) return RenderRpcSerializer.Clone(_job); }
    }
}

public sealed class RenderServiceHost : IAsyncDisposable
{
    private readonly RenderBackendService _service;
    public RenderServiceHost(string? clientId = null, IRenderArtifactStore? artifactStore = null, IAudioPreviewSinkFactory? previewAudioSinkFactory = null)
    {
        _service = new RenderBackendService(artifactStore, previewAudioSinkFactory: previewAudioSinkFactory);
        Transport = new DirectRenderTransport(_service);
        Client = new RenderClient(Transport, clientId);
    }

    public IRenderTransport Transport { get; }
    public IRenderClient Client { get; }
    public IRenderService Service => _service;

    public async ValueTask DisposeAsync()
    {
        await Client.DisposeAsync().ConfigureAwait(false);
        await _service.DisposeAsync().ConfigureAwait(false);
    }
}
