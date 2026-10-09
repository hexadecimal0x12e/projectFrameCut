using projectFrameCut.Drawing.Processing.Converting;
using projectFrameCut.Render.ClipsAndTracks;
using projectFrameCut.Render.Compose;
using projectFrameCut.Render.Contracts;
using projectFrameCut.Render.Effect;
using projectFrameCut.Render.EncodeAndDecode;
using projectFrameCut.Render.Plugin;
using projectFrameCut.Render.PreviewAudio;
using projectFrameCut.Render.RenderAPIBase.ClipAndTrack;
using projectFrameCut.Render.RenderAPIBase.Project;
using projectFrameCut.Render.Rendering;
using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;

namespace projectFrameCut.Render.RPCProtocol;

public sealed class RenderBackendService(IRenderArtifactStore? artifactStore = null, string? stateRoot = null, Action<RenderJob>? completionSink = null, Action<RenderJob>? progressSink = null, IAudioPreviewSinkFactory? previewAudioSinkFactory = null) : IRenderService, IAsyncDisposable
{
    private const string TimelineFrameCacheVersion = "v5-vfd";
    private const string ClipPreviewCacheVersion = "v5-vfd";
    private const string TimelineSegmentCacheVersion = "v2-audio-source";
    private const string AudioSegmentCacheVersion = "v2-track-source";
    private const string FrameHashIndexVersion = "v2-lazy-frame-clip";
    private const string PreviewCacheManifestFileName = "render-preview-cache.json";
    private static readonly TimeSpan PreviewCacheRetention = TimeSpan.FromHours(48);
    private static readonly JsonSerializerOptions PreviewCacheJsonOptions = new() { PropertyNameCaseInsensitive = true, WriteIndented = true };
    private readonly IRenderArtifactStore _artifacts = artifactStore ?? new RenderArtifactStore();
    private readonly string? _stateRoot = string.IsNullOrWhiteSpace(stateRoot) ? null : Path.GetFullPath(stateRoot);
    private readonly Action<RenderJob>? _completionSink = completionSink;
    private readonly Action<RenderJob>? _progressSink = progressSink;
    private readonly IAudioPreviewSinkFactory? _previewAudioSinkFactory = previewAudioSinkFactory;
    private readonly ConcurrentDictionary<Guid, BackendSession> _sessions = new();
    private readonly ConcurrentDictionary<Guid, JobEntry> _jobs = new();
    private readonly ConcurrentDictionary<string, object> _previewCacheGates = new(StringComparer.OrdinalIgnoreCase);
    private readonly object _persistGate = new();
    private readonly SemaphoreSlim _projectLifecycleGate = new(1, 1);
    private int _disposed;
    private readonly ConcurrentDictionary<string, ProjectExternalSourceHost> _externalSources = new(OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal);
    private ProjectExternalSourceHost GetSourceHost(string root) => _externalSources.GetOrAdd(Path.TrimEndingDirectorySeparator(Path.GetFullPath(root)), _ => new());
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
                RenderOperation.RenderClipPreviewBatch => Success(request, await RenderClipPreviewBatchAsync(Read<ClipPreviewBatchRequest>(request), cancellationToken).ConfigureAwait(false)),
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
                && ((int)operation < 18 || (int)operation > 40)
                && (_previewAudioSinkFactory is not null || operation is not (RenderOperation.ControlPreviewAudio or RenderOperation.GetPreviewAudioClock)))
            .Select(static operation => operation.ToString())
            .ToList(),
        Encoders = ["libx264"],
        Features = _previewAudioSinkFactory is null
            ? ["direct-transport", "named-pipe", "unix-socket", "timeline-preview", "clip-preview-without-layout", "clip-preview-before-layout", "thumbs-cache", "vfd-preview", "png-preview", "render-jobs", "persistent-render-jobs", "artifact-files"]
            : ["direct-transport", "named-pipe", "unix-socket", "timeline-preview", "clip-preview-without-layout", "clip-preview-before-layout", "thumbs-cache", "vfd-preview", "png-preview", "render-jobs", "persistent-render-jobs", "artifact-files", "preview-audio-device-clock"],
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
        if (!_sessions.Values.Any(x => string.Equals(x.ProjectRoot, root, OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal))
            && _externalSources.TryRemove(root, out var sources))
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
            Effects = EffectHelper.ConfigurationRevision,
        }, _jsonOptions));
        var reusable = previous is not null && previous.SourceConfiguration == sourceConfiguration && previous.ProjectRoot == root ? previous : null;
        var snapshotHash = ComputeTextHash($"{request.ProjectJson}\n{request.TimelineJson}");
        if (reusable?.SnapshotHash == snapshotHash && reusable.Clips.All(clip => !ClipInitializationFailure.IsMarked(clip)))
            return reusable.ToContract();
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
            await session.DisposeAsync().ConfigureAwait(false);
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
        var width = Math.Max(1, request.Width);
        var height = Math.Max(1, request.Height);
        await session.RenderGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        { 
            cancellationToken.ThrowIfCancellationRequested();
            var frameHash = session.GetFrameHash(request.FrameIndex);
            var namespacePrefix = string.IsNullOrEmpty(session.CacheNamespace) ? string.Empty : $"{session.CacheNamespace}_";
            var wantsScRgb = request.PreferredPixelFormat == PreviewPixelFormat.Rgba16FloatScRgb;
            var formatSuffix = wantsScRgb ? "vfd16" : "vfd8";
            var cacheKey = $"{TimelineFrameCacheVersion}_{namespacePrefix}{frameHash}_{width}x{height}_{formatSuffix}";
            var allowCaching = Timeline.CanCacheFrame(GetVisualClips(session.Clips), request.FrameIndex);
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
                    var visualClips = GetVisualClips(session.Clips);
                    foreach (var clip in visualClips)
                    {
                        cancellationToken.ThrowIfCancellationRequested();
                        try { clip.ReInit(wantsScRgb ? IPicture.PicturePixelMode.UShortPicture : IPicture.PicturePixelMode.BytePicture); }
                        catch (OperationCanceledException) { throw; }
                        catch (Exception ex) { ClipInitializationFailure.Mark(clip, "Source or ResolveEffect", ex); }
                    }
                    var layers = Timeline.GetFramesInOneFrame(visualClips, request.FrameIndex, width, height, projectRelativeWidth: session.Width, projectRelativeHeight: session.Height);
                    picture = Timeline.MixtureLayers(layers, request.FrameIndex, width, height, autoCenterImplicitClip: true, projectRelativeWidth: session.Width, projectRelativeHeight: session.Height);
                    cancellationToken.ThrowIfCancellationRequested();
                    picture.SaveToDisk(temporaryPath, Drawing.Base.PictureExtensions.SharedVfdPictureEncoder);
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
            session.RenderGate.Release();
        }
    }

    private async ValueTask<RenderArtifact> RenderClipPreviewAsync(ClipPreviewRequest request, CancellationToken cancellationToken)
    {
        var session = GetSession(request.SessionId);
        using var operation = session.BeginOperation(cancellationToken);
        cancellationToken = operation.Token;
        using var sourceScope = ProjectExternalSourceRuntime.Use(session.ExternalSources);
        var savedClip = session.Clips.FirstOrDefault(candidate => candidate.Id == request.ClipId)
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

        await session.RenderGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            // Use the same full-timeline content hash as static previews. This is
            // important for transform clips whose output depends on bound clips
            // outside the requested clip itself.
            var clips = snapshot is null ? session.Clips : session.Clips.Select(c => c.Id == clip.Id ? clip : c).ToArray();
            if (request.BeforeLayout && clip.GetRelativeFrameIndex(request.FrameIndex) is null)
                throw new ArgumentOutOfRangeException(nameof(request.FrameIndex), "The frame is outside the clip's timeline interval.");
            if (!request.BeforeLayout && TransformProcessing.HasActiveTransform(clip, GetVisualClips(clips), request.FrameIndex))
            {
                previewWidth = canvasWidth;
                previewHeight = canvasHeight;
            }
            var clipHash = snapshot is null ? session.GetClipFrameHash(clip.Id, request.FrameIndex)
                : Timeline.GetClipFrameHash(clips, clip, request.FrameIndex);
            var namespacePrefix = string.IsNullOrEmpty(session.CacheNamespace) ? string.Empty : $"{session.CacheNamespace}_";
            var wantsScRgb = request.PreferredPixelFormat == PreviewPixelFormat.Rgba16FloatScRgb;
            var formatSuffix = wantsScRgb ? "vfd16" : "vfd8";
            var allowCaching = Timeline.CanCacheClipFrame(clips, clip);
            var layoutPrefix = request.BeforeLayout ? "before-layout_" : string.Empty;
            var relativePath = allowCaching
                ? $"thumbs/perClip/{clip.Id}/dynamic/dynamic_{ClipPreviewCacheVersion}_{layoutPrefix}{namespacePrefix}{clipHash}_{projectWidth}x{projectHeight}_{canvasWidth}x{canvasHeight}_{formatSuffix}.vfd"
                : $"thumbs/unCacheablePerClip/{session.Id:N}/clip_{clip.Id:N}_{Guid.NewGuid():N}.vfd";
            var finalPath = _artifacts.ResolveProjectPath(session.ProjectRoot, relativePath);
            var cacheHit = allowCaching && File.Exists(finalPath);
            if (!cacheHit)
            {
                var temporaryPath = _artifacts.CreateTemporaryPath(finalPath);
                IPicture? picture = null;
                try
                {
                    if (wantsScRgb)
                    {
                        try { clip.ReInit(IPicture.PicturePixelMode.UShortPicture); }
                        catch (OperationCanceledException) { throw; }
                        catch (Exception ex) { ClipInitializationFailure.Mark(clip, "Source or ResolveEffect", ex); }
                    }
                    picture = ClipPreviewRenderer.Render(
                        clip,
                        GetVisualClips(clips),
                        canvasWidth,
                        canvasHeight,
                        projectWidth,
                        projectHeight,
                        request.FrameIndex,
                        cancellationToken,
                        wantsScRgb ? IPicture.PicturePixelMode.UShortPicture : IPicture.PicturePixelMode.BytePicture,
                        request.BeforeLayout)
                        ?? throw new InvalidOperationException($"Clip '{clip.Id}' did not produce a preview frame.");
                    cancellationToken.ThrowIfCancellationRequested();
                    picture.SaveToDisk(temporaryPath, Drawing.Base.PictureExtensions.SharedVfdPictureEncoder);
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
            using (var stream = File.OpenRead(finalPath))
            {
                var header = new byte[18];
                stream.ReadExactly(header);
                if (!header.AsSpan(0, 4).SequenceEqual("VFCD"u8)) throw new InvalidDataException("Invalid VFD clip preview header.");
                int offset = header[4] == 0xFF ? 10 : 5;
                previewWidth = BinaryPrimitives.ReadInt32LittleEndian(header.AsSpan(offset, 4));
                previewHeight = BinaryPrimitives.ReadInt32LittleEndian(header.AsSpan(offset + 4, 4));
            }
            if (allowCaching)
                TrackPreviewCacheAccess(session.ProjectRoot, relativePath, clip.Id, request.FrameIndex, $"project:{projectWidth}x{projectHeight};canvas:{canvasWidth}x{canvasHeight};output:{previewWidth}x{previewHeight};format:{formatSuffix}");
            if (request.PreferredPixelFormat == PreviewPixelFormat.PngImage)
                return MaterializePreviewPng(session, relativePath, clip.Id, request.FrameIndex, previewWidth, previewHeight, cacheHit, allowCaching, cancellationToken);
            return _artifacts.Register(session.Id, session.ProjectRoot, relativePath, "application/x-projectframecut-vfd",
                cacheHit, isPreview: true, previewWidth, previewHeight, session.FrameRate, PreviewPixelFormat.VfdPicture, 0, "vfd-native");
        }
        finally
        {
            session.RenderGate.Release();
        }
    }

    private RenderArtifact MaterializePreviewPng(BackendSession session, string vfdRelativePath, Guid? clipId, uint frameIndex,
        int width, int height, bool sourceCacheHit, bool allowCaching, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var relativePath = Path.ChangeExtension(vfdRelativePath, ".png");
        var finalPath = _artifacts.ResolveProjectPath(session.ProjectRoot, relativePath);
        var cacheHit = allowCaching && File.Exists(finalPath);
        if (!cacheHit)
        {
            var temporaryPath = _artifacts.CreateTemporaryPath(finalPath);
            try
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
        var keySource = $"{TimelineSegmentCacheVersion}|{session.CacheNamespace}|{session.SnapshotHash}|{request.StartFrame}|{request.Length}|{request.Width}|{request.Height}|{request.FrameRate}|{request.IncludeAudio}";
        var relativePath = GetVisualClips(session.Clips).All(Timeline.CanCacheClipSource)
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

    private string ResolveAssetPath(BackendSession session, AssetMetadataRequest request)
    {
        if (!string.IsNullOrWhiteSpace(request.AssetId) && session.Assets.TryGetValue(request.AssetId, out var assetPath))
            return ProjectExternalVideoSource.IsPath(assetPath) ? assetPath : Path.GetFullPath(assetPath);
        if (!string.IsNullOrWhiteSpace(request.ProjectRelativePath)) return _artifacts.ResolveProjectPath(session.ProjectRoot, request.ProjectRelativePath);
        throw new ArgumentException("Either AssetId or ProjectRelativePath is required.");
    }

    private void CancelJobsForSession(Guid sessionId)
    {
        foreach (var entry in _jobs.Values.Where(entry => entry.SessionId == sessionId)) entry.Cancellation.Cancel();
    }

    private static string ResolveMediaType(string path) => Path.GetExtension(path).ToLowerInvariant() switch
    {
        ".vfd" => "application/x-projectframecut-vfd", ".png" => "image/png", ".jpg" or ".jpeg" => "image/jpeg", ".wav" => "audio/wav", ".mp3" => "audio/mpeg",
        ".mp4" => "video/mp4", ".mov" => "video/quicktime", ".webm" => "video/webm", _ => "application/octet-stream",
    };

    private static string SanitizeFileName(string value, string fallback)
    {
        var name = Path.GetFileName(string.IsNullOrWhiteSpace(value) ? fallback : value);
        foreach (var invalid in Path.GetInvalidFileNameChars()) name = name.Replace(invalid, '_');
        return string.IsNullOrWhiteSpace(name) ? fallback : name;
    }

    private void TrackPreviewCacheAccess(string projectRoot, string relativePath, Guid? clipId, uint frameIndex, string configuration)
    {
        try
        {
            var normalizedPath = relativePath.Replace(Path.DirectorySeparatorChar, '/');
            if (!TryResolveTrackedPreviewPath(projectRoot, normalizedPath, out _)) return;
            var cacheId = ComputeTextHash(normalizedPath);
            lock (_previewCacheGates.GetOrAdd(projectRoot, static _ => new object()))
            {
                var now = DateTimeOffset.UtcNow;
                var manifest = LoadPreviewCacheManifest(projectRoot);
                manifest.Entries[cacheId] = new PreviewCacheEntry
                {
                    CacheId = cacheId,
                    ClipId = clipId,
                    FrameIndex = frameIndex,
                    Configuration = configuration,
                    ProjectRelativePath = normalizedPath,
                    LastAccessedAtUtc = now,
                };
                CleanupPreviewCache(projectRoot, manifest, now);
                SavePreviewCacheManifest(projectRoot, manifest);
            }
        }
        catch (Exception ex)
        {
            Log(ex, "Track preview cache access", this);
        }
    }

    private void TouchTrackedPreviewCache(string projectRoot, string fullPath)
    {
        try
        {
            var relativePath = Path.GetRelativePath(projectRoot, fullPath).Replace(Path.DirectorySeparatorChar, '/');
            if (!TryResolveTrackedPreviewPath(projectRoot, relativePath, out _)) return;
            lock (_previewCacheGates.GetOrAdd(projectRoot, static _ => new object()))
            {
                var manifest = LoadPreviewCacheManifest(projectRoot);
                var cacheId = ComputeTextHash(relativePath);
                if (!manifest.Entries.TryGetValue(cacheId, out var entry)) return;
                var now = DateTimeOffset.UtcNow;
                entry.LastAccessedAtUtc = now;
                CleanupPreviewCache(projectRoot, manifest, now);
                SavePreviewCacheManifest(projectRoot, manifest);
            }
        }
        catch (Exception ex)
        {
            Log(ex, "Touch preview cache artifact", this);
        }
    }

    private void MaintainPreviewCache(string projectRoot)
    {
        try
        {
            lock (_previewCacheGates.GetOrAdd(projectRoot, static _ => new object()))
            {
                var manifest = LoadPreviewCacheManifest(projectRoot);
                DiscoverPreviewCacheEntries(projectRoot, manifest);
                CleanupPreviewCache(projectRoot, manifest, DateTimeOffset.UtcNow);
                SavePreviewCacheManifest(projectRoot, manifest);
            }
        }
        catch (Exception ex)
        {
            Log(ex, "Maintain preview cache", this);
        }
    }

    private PreviewCacheManifest LoadPreviewCacheManifest(string projectRoot)
    {
        var path = GetPreviewCacheManifestPath(projectRoot);
        if (!File.Exists(path)) return new();
        try
        {
            var manifest = JsonSerializer.Deserialize<PreviewCacheManifest>(File.ReadAllText(path), PreviewCacheJsonOptions) ?? new();
            manifest.Entries ??= new(StringComparer.Ordinal);
            return manifest;
        }
        catch (Exception ex)
        {
            Log(ex, "Read preview cache manifest", this);
            return new();
        }
    }

    private void SavePreviewCacheManifest(string projectRoot, PreviewCacheManifest manifest)
    {
        var path = GetPreviewCacheManifestPath(projectRoot);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var temporaryPath = $"{path}.tmp-{Guid.NewGuid():N}";
        try
        {
            File.WriteAllText(temporaryPath, JsonSerializer.Serialize(manifest, PreviewCacheJsonOptions));
            File.Move(temporaryPath, path, overwrite: true);
        }
        finally
        {
            try { File.Delete(temporaryPath); } catch { }
        }
    }

    private void CleanupPreviewCache(string projectRoot, PreviewCacheManifest manifest, DateTimeOffset now)
    {
        var cutoff = now - PreviewCacheRetention;
        var deleted = 0;
        foreach (var pair in manifest.Entries.ToArray())
        {
            if (pair.Value is null)
            {
                manifest.Entries.Remove(pair.Key);
                continue;
            }
            if (!TryResolveTrackedPreviewPath(projectRoot, pair.Value.ProjectRelativePath, out var path))
            {
                manifest.Entries.Remove(pair.Key);
                continue;
            }
            if (!File.Exists(path))
            {
                manifest.Entries.Remove(pair.Key);
                continue;
            }
            if (pair.Value.LastAccessedAtUtc > cutoff) continue;
            try
            {
                File.Delete(path);
                manifest.Entries.Remove(pair.Key);
                deleted++;
            }
            catch (Exception ex)
            {
                Log(ex, $"Delete expired preview cache '{pair.Value.ProjectRelativePath}'", this);
            }
        }
        if (deleted > 0) Log($"[RenderRPC] Deleted {deleted} preview cache file(s) unused for more than {PreviewCacheRetention.TotalHours:0} hours.");
    }

    private void DiscoverPreviewCacheEntries(string projectRoot, PreviewCacheManifest manifest)
    {
        var thumbsRoot = Path.Combine(projectRoot, "thumbs");
        if (!Directory.Exists(thumbsRoot)) return;
        IEnumerable<string> files = Directory.EnumerateFiles(thumbsRoot, "projectFrameCut_Render_*", SearchOption.TopDirectoryOnly)
            .Concat(Directory.Exists(Path.Combine(thumbsRoot, "perClip"))
                ? Directory.EnumerateFiles(Path.Combine(thumbsRoot, "perClip"), "dynamic_*", SearchOption.AllDirectories)
                : Enumerable.Empty<string>());
        foreach (var path in files.Where(static path => Path.GetExtension(path).Equals(".vfd", StringComparison.OrdinalIgnoreCase)
            || Path.GetExtension(path).Equals(".png", StringComparison.OrdinalIgnoreCase)
            || Path.GetExtension(path).Equals(".rgba16f", StringComparison.OrdinalIgnoreCase)))
        {
            var directory = Directory.GetParent(path);
            Guid? clipId = null;
            var isTimelineFrame = string.Equals(directory?.FullName, thumbsRoot, OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal);
            if (!isTimelineFrame)
            {
                if (!string.Equals(directory?.Name, "dynamic", StringComparison.OrdinalIgnoreCase)
                    || !Guid.TryParse(directory.Parent?.Name, out var parsedClipId))
                    continue;
                clipId = parsedClipId;
            }
            var relativePath = Path.GetRelativePath(projectRoot, path).Replace(Path.DirectorySeparatorChar, '/');
            var cacheId = ComputeTextHash(relativePath);
            if (manifest.Entries.ContainsKey(cacheId)) continue;
            manifest.Entries[cacheId] = new PreviewCacheEntry
            {
                CacheId = cacheId,
                ClipId = clipId,
                Configuration = "discovered",
                ProjectRelativePath = relativePath,
                LastAccessedAtUtc = File.GetLastWriteTimeUtc(path),
            };
        }
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
                && !string.Equals(fullPath, GetPreviewCacheManifestPath(projectRoot), comparison);
        }
        catch
        {
            return false;
        }
    }

    private static string GetPreviewCacheManifestPath(string projectRoot)
        => Path.Combine(projectRoot, "thumbs", PreviewCacheManifestFileName);

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
            foreach (var session in _sessions.Values) await session.DisposeAsync().ConfigureAwait(false);
            _sessions.Clear();
            foreach (var sources in _externalSources.Values) await sources.CloseAsync().ConfigureAwait(false);
            _externalSources.Clear();
            if (PluginManager.ProjectPluginIds.Count > 0 && PluginManager.ProjectPluginUnloader is not null)
                await PluginManager.ProjectPluginUnloader().ConfigureAwait(false);
            PersistJobs();
        }
        finally { _projectLifecycleGate.Release(); }
    }

    private sealed class PreviewCacheManifest
    {
        public int Version { get; set; } = 1;
        public Dictionary<string, PreviewCacheEntry> Entries { get; set; } = new(StringComparer.Ordinal);
    }

    private sealed class PreviewCacheEntry
    {
        public string CacheId { get; set; } = string.Empty;
        public Guid? ClipId { get; set; }
        public uint FrameIndex { get; set; }
        public string Configuration { get; set; } = string.Empty;
        public string ProjectRelativePath { get; set; } = string.Empty;
        public DateTimeOffset LastAccessedAtUtc { get; set; }
    }

    private sealed class BackendSession(
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
        public IReadOnlyDictionary<string, string> Assets { get; } = assets;
        public string SnapshotHash { get; } = snapshotHash;
        public FrameHashIndex HashIndex { get; } = hashIndex;
        public string SourceConfiguration { get; } = sourceConfiguration;
        public IReadOnlyDictionary<Guid, string> ClipSignatures { get; } = clipSignatures;
        public IReadOnlyDictionary<string, string> TrackSignatures { get; } = trackSignatures;
        public ProjectExternalSourceHost ExternalSources { get; set; } = null!;
        public string CacheNamespace => string.IsNullOrEmpty(ExternalSources.CacheToken) && string.IsNullOrEmpty(ExternalVideoSourceRegistry.CacheToken)
            ? cacheNamespace : ComputeTextHash(cacheNamespace + ExternalSources.CacheToken + ExternalVideoSourceRegistry.CacheToken);
        public SemaphoreSlim RenderGate { get; } = new(1, 1);
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
            lock (_previewAudioGate) return _previewAudio ??= new PreviewAudioSession(factory, SoundTracks, Duration);
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
