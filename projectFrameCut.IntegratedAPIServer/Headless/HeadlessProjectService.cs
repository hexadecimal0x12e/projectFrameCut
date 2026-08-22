using projectFrameCut.McpCore;
using projectFrameCut.Render.Contracts;
using projectFrameCut.Render.RenderAPIBase.EffectAndMixture;
using projectFrameCut.Render.RenderAPIBase.Project;
using projectFrameCut.Render.RPCProtocol;
using System.Collections.Concurrent;
using System.Diagnostics.CodeAnalysis;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace projectFrameCut.IntegratedAPIServer.Headless;

[UnconditionalSuppressMessage("Trimming", "IL2026", Justification = "Project JSON types and their plugin extension data are preserved by the application render runtime.")]
public sealed class HeadlessProjectService : IRenderService, IAsyncDisposable
{
    private readonly RenderBackendService _renderService;
    private readonly bool _ownsRenderService;
    private readonly ConcurrentDictionary<Guid, HeadlessSession> _sessions = new();
    private readonly string? _globalAssetsDatabasePath;
    private Guid _defaultSessionId;

    public HeadlessProjectService(string? globalAssetsDatabasePath = null)
        : this(new RenderBackendService(), globalAssetsDatabasePath, ownsRenderService: true)
    {
    }

    /// <summary>
    /// Creates a headless project service over an externally owned render backend.
    /// The supplied <paramref name="renderService"/> is shared with its owner (for
    /// example a named-pipe render server), so sessions opened through either
    /// channel are visible to both; it is NOT disposed together with this service.
    /// </summary>
    public HeadlessProjectService(RenderBackendService renderService, string? globalAssetsDatabasePath = null)
        : this(renderService ?? throw new ArgumentNullException(nameof(renderService)), globalAssetsDatabasePath, ownsRenderService: false)
    {
    }

    private HeadlessProjectService(RenderBackendService renderService, string? globalAssetsDatabasePath, bool ownsRenderService)
    {
        _renderService = renderService;
        _ownsRenderService = ownsRenderService;
        _globalAssetsDatabasePath = string.IsNullOrWhiteSpace(globalAssetsDatabasePath)
            ? null
            : Path.GetFullPath(globalAssetsDatabasePath);
    }

    /// <summary>Whether a startup project has already been loaded into this service.</summary>
    public bool IsInitialized => _defaultSessionId != Guid.Empty;

    public async ValueTask InitializeAsync(string projectRoot, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(projectRoot);
        if (_defaultSessionId != Guid.Empty)
            throw new InvalidOperationException("The headless project service has already been initialized.");

        HeadlessProjectSnapshot snapshot = await OpenAsync(
            new OpenHeadlessProjectRequest { ProjectRoot = projectRoot },
            cancellationToken).ConfigureAwait(false);
        _defaultSessionId = snapshot.SessionId;
    }

    public async ValueTask<RenderResponseEnvelope> DispatchAsync(
        RenderRequestEnvelope request,
        CancellationToken cancellationToken = default)
    {
        if (request.ProtocolVersion < RenderProtocol.MinimumSupportedVersion ||
            request.ProtocolVersion > RenderProtocol.CurrentVersion)
        {
            return Failure(
                request,
                RenderErrorCode.ProtocolMismatch,
                $"Unsupported render protocol version {request.ProtocolVersion}.");
        }

        try
        {
            return request.Operation switch
            {
                RenderOperation.GetCapabilities => Success(request, await GetCapabilitiesAsync(request, cancellationToken).ConfigureAwait(false)),
                RenderOperation.OpenProject => Success(request, await OpenRenderProjectAsync(Read<OpenProjectRequest>(request), cancellationToken).ConfigureAwait(false)),
                RenderOperation.OpenHeadlessProject => Success(request, await OpenAsync(Read<OpenHeadlessProjectRequest>(request), cancellationToken).ConfigureAwait(false)),
                RenderOperation.CloseProject => Success(request, await CloseAsync(Read<SessionRequest>(request).SessionId, request, cancellationToken).ConfigureAwait(false)),
                RenderOperation.GetHeadlessProjectSnapshot => Success(request, await ReadSnapshotAsync(Read<HeadlessSessionRequest>(request).SessionId, cancellationToken).ConfigureAwait(false)),
                RenderOperation.ReloadHeadlessProject => Success(request, await ReloadAsync(Read<HeadlessMutationPrecondition>(request), cancellationToken).ConfigureAwait(false)),
                RenderOperation.ApplyHeadlessProjectSnapshot => Success(request, await ApplySnapshotAsync(Read<ApplyHeadlessProjectSnapshotRequest>(request), cancellationToken).ConfigureAwait(false)),
                RenderOperation.ListHeadlessClips => Success(request, await ReadJsonAsync(Read<HeadlessSessionRequest>(request).SessionId, static session => session.Editor.ListClips(), cancellationToken).ConfigureAwait(false)),
                RenderOperation.GetHeadlessClip => Success(request, await GetClipAsync(Read<HeadlessClipRequest>(request), cancellationToken).ConfigureAwait(false)),
                RenderOperation.UpsertHeadlessClip => Success(request, await MutateAsync(Read<HeadlessClipMutationRequest>(request), static (editor, value) => editor.UpsertClip(Deserialize<ClipDraftDTO>(value.Json)), cancellationToken).ConfigureAwait(false)),
                RenderOperation.MoveHeadlessClip => Success(request, await MoveClipAsync(Read<MoveHeadlessClipRequest>(request), cancellationToken).ConfigureAwait(false)),
                RenderOperation.PatchHeadlessClip => Success(request, await MutateAsync(Read<HeadlessClipMutationRequest>(request), static (editor, value) => editor.PatchClip(value.ClipId, Deserialize<Dictionary<string, object?>>(value.Json)), cancellationToken).ConfigureAwait(false)),
                RenderOperation.DeleteHeadlessClip => Success(request, await MutateAsync(Read<HeadlessClipMutationRequest>(request), static (editor, value) => editor.DeleteClip(value.ClipId), cancellationToken).ConfigureAwait(false)),
                RenderOperation.AddOrReplaceHeadlessEffect => Success(request, await MutateAsync(Read<HeadlessClipMutationRequest>(request), static (editor, value) => editor.AddOrReplaceEffect(value.ClipId, Deserialize<EffectAndMixtureJSONStructure>(value.Json)), cancellationToken).ConfigureAwait(false)),
                RenderOperation.RemoveHeadlessEffect => Success(request, await RemoveEffectAsync(Read<RemoveHeadlessEffectRequest>(request), cancellationToken).ConfigureAwait(false)),
                RenderOperation.AddOrReplaceHeadlessEffectBundle => Success(request, await MutateAsync(Read<HeadlessClipMutationRequest>(request), static (editor, value) => editor.AddOrReplaceEffectBundle(value.ClipId, Deserialize<EffectBundleJSONStructure>(value.Json)), cancellationToken).ConfigureAwait(false)),
                RenderOperation.RemoveHeadlessEffectBundle => Success(request, await RemoveEffectBundleAsync(Read<RemoveHeadlessEffectBundleRequest>(request), cancellationToken).ConfigureAwait(false)),
                RenderOperation.SaveHeadlessProject => Success(request, await SaveAsync(Read<HeadlessSaveProjectRequest>(request), cancellationToken).ConfigureAwait(false)),
                _ => await _renderService.DispatchAsync(request, cancellationToken).ConfigureAwait(false),
            };
        }
        catch (HeadlessVersionConflictException ex)
        {
            return Failure(request, RenderErrorCode.VersionConflict, ex, customDetails: ex.Details);
        }
        catch (HeadlessSessionNotFoundException ex)
        {
            return Failure(request, RenderErrorCode.SessionNotFound, ex);
        }
        catch (RenderRpcException ex)
        {
            return Failure(request, ex.Error.Code, ex.Error.Message, ex.Error.Details);
        }
        catch (Exception ex) when (ex.Data[nameof(RemoteError.Code)] is RenderErrorCode code)
        {
            return Failure(request, code, ex, customDetails: ex.Data[nameof(RemoteError.Details)] as string);
        }
        catch (FileNotFoundException ex)
        {
            return Failure(request, RenderErrorCode.ProjectNotFound, ex, customDetails: ex.FileName ?? string.Empty);
        }
        catch (DirectoryNotFoundException ex)
        {
            return Failure(request, RenderErrorCode.ProjectNotFound, ex);
        }
        catch (KeyNotFoundException ex)
        {
            return Failure(request, RenderErrorCode.ClipNotFound, ex);
        }
        catch (ArgumentException ex)
        {
            return Failure(request, RenderErrorCode.InvalidRequest, ex);
        }
        catch (FormatException ex)
        {
            return Failure(request, RenderErrorCode.InvalidRequest, ex);
        }
        catch (OverflowException ex)
        {
            return Failure(request, RenderErrorCode.InvalidRequest, ex);
        }
        catch (JsonException ex)
        {
            return Failure(request, RenderErrorCode.InvalidRequest, ex);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return Failure(request, RenderErrorCode.Canceled, "Headless RPC request was canceled.");
        }
        catch (Exception ex)
        {
            return Failure(request, RenderErrorCode.BackendFailure, ex);
        }
    }

    private async ValueTask<RenderCapabilities> GetCapabilitiesAsync(
        RenderRequestEnvelope request,
        CancellationToken cancellationToken)
    {
        RenderResponseEnvelope response = await _renderService.DispatchAsync(request, cancellationToken).ConfigureAwait(false);
        if (response.Error is not null) response.Error.ThrowAsException();
        RenderCapabilities capabilities = RenderRpcSerializer.Deserialize<RenderCapabilities>(response.Payload);
        foreach (string feature in new[] { "http-protobuf", "headless-project", "optimistic-concurrency", "project-editing" })
        {
            if (!capabilities.Features.Contains(feature, StringComparer.Ordinal)) capabilities.Features.Add(feature);
        }
        foreach (RenderOperation operation in Enum.GetValues<RenderOperation>().Where(static operation => (int)operation >= 100))
        {
            string name = operation.ToString();
            if (!capabilities.Operations.Contains(name, StringComparer.Ordinal)) capabilities.Operations.Add(name);
        }
        return capabilities;
    }

    private async ValueTask<RenderSession> OpenRenderProjectAsync(
        OpenProjectRequest request,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(request.ProjectRoot);
        ArgumentException.ThrowIfNullOrWhiteSpace(request.TimelineJson);
        string projectRoot = Path.GetFullPath(request.ProjectRoot);
        DraftStructureJSON draft = Deserialize<DraftStructureJSON>(request.TimelineJson);
        List<AssetPathEntry> renderAssets = BuildRenderAssets(projectRoot, draft, request.Assets);

        request.Assets = renderAssets;
        request.CacheNamespace = ComputeSnapshotHash(
            request.CacheNamespace,
            request.ProjectJson,
            request.TimelineJson,
            Serialize(renderAssets));

        var innerRequest = new RenderRequestEnvelope
        {
            RequestId = Guid.NewGuid(),
            ClientId = "headless-render-session",
            Operation = RenderOperation.OpenProject,
            Payload = RenderRpcSerializer.Serialize(request),
        };
        RenderResponseEnvelope response = await _renderService.DispatchAsync(innerRequest, cancellationToken).ConfigureAwait(false);
        if (response.Error is not null) response.Error.ThrowAsException();
        return RenderRpcSerializer.Deserialize<RenderSession>(response.Payload);
    }

    private async ValueTask<HeadlessProjectSnapshot> OpenAsync(OpenHeadlessProjectRequest request, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(request.ProjectRoot);
        string root = Path.GetFullPath(request.ProjectRoot);
        if (!Directory.Exists(root)) throw new DirectoryNotFoundException($"Project root '{root}' does not exist.");

        var sessionId = request.SessionId == Guid.Empty ? Guid.NewGuid() : request.SessionId;
        var session = new HeadlessSession(sessionId, TimelineProjectWorkspace.Load(root));
        await session.Gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        if (!_sessions.TryAdd(sessionId, session))
        {
            session.Gate.Release();
            session.Dispose();
            throw new ArgumentException($"Headless project session '{sessionId}' is already open.");
        }
        bool opened = false;
        try
        {
            await RefreshRenderSessionAsync(session, cancellationToken).ConfigureAwait(false);
            opened = true;
            return CreateSnapshot(session);
        }
        catch
        {
            _sessions.TryRemove(sessionId, out _);
            throw;
        }
        finally
        {
            session.Gate.Release();
            if (!opened) session.Dispose();
        }
    }

    private async ValueTask<EmptyResponse> CloseAsync(
        Guid sessionId,
        RenderRequestEnvelope originalRequest,
        CancellationToken cancellationToken)
    {
        // The startup project belongs to the RPC service, rather than to an
        // individual client. Keep it available for the next client after a
        // client disconnects; it is disposed together with the service.
        if (sessionId == _defaultSessionId)
            return new EmptyResponse();

        if (_sessions.TryRemove(sessionId, out var session))
        {
            await session.Gate.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                RenderResponseEnvelope renderResponse = await _renderService.DispatchAsync(originalRequest, cancellationToken).ConfigureAwait(false);
                if (renderResponse.Error is not null) renderResponse.Error.ThrowAsException();
            }
            finally
            {
                session.Gate.Release();
                session.Dispose();
            }
            return new EmptyResponse();
        }

        RenderResponseEnvelope response = await _renderService.DispatchAsync(originalRequest, cancellationToken).ConfigureAwait(false);
        if (response.Error is not null) response.Error.ThrowAsException();
        return RenderRpcSerializer.Deserialize<EmptyResponse>(response.Payload);
    }

    private async ValueTask<HeadlessProjectSnapshot> ReadSnapshotAsync(Guid sessionId, CancellationToken cancellationToken)
    {
        var session = GetSession(sessionId == Guid.Empty ? _defaultSessionId : sessionId);
        await session.Gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try { return CreateSnapshot(session); }
        finally { session.Gate.Release(); }
    }

    private async ValueTask<HeadlessProjectSnapshot> ReloadAsync(HeadlessMutationPrecondition precondition, CancellationToken cancellationToken)
    {
        var session = GetSession(precondition.SessionId);
        await session.Gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        WorkspaceState backup = CaptureWorkspace(session);
        try
        {
            ValidatePrecondition(session, precondition);
            session.ReplaceWorkspace(TimelineProjectWorkspace.Load(session.Workspace.ProjectRoot));
            await CommitMutationAsync(session, cancellationToken).ConfigureAwait(false);
            return CreateSnapshot(session);
        }
        catch
        {
            RestoreWorkspace(session, backup);
            throw;
        }
        finally { session.Gate.Release(); }
    }

    private async ValueTask<HeadlessProjectSnapshot> ApplySnapshotAsync(ApplyHeadlessProjectSnapshotRequest request, CancellationToken cancellationToken)
    {
        var session = GetSession(request.Precondition.SessionId);
        var project = Deserialize<ProjectJSONStructure>(request.ProjectJson);
        var draft = Deserialize<DraftStructureJSON>(request.TimelineJson);
        var assets = Deserialize<List<AssetItem>>(request.AssetsJson);

        await session.Gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        WorkspaceState backup = CaptureWorkspace(session);
        try
        {
            ValidatePrecondition(session, request.Precondition);
            session.Workspace.ReplaceProjectInfo(project);
            session.Workspace.ReplaceDraft(draft);
            session.Workspace.ReplaceAssets(assets);
            session.ReplaceWorkspace(session.Workspace);
            await CommitMutationAsync(session, cancellationToken).ConfigureAwait(false);
            return CreateSnapshot(session);
        }
        catch
        {
            RestoreWorkspace(session, backup);
            throw;
        }
        finally { session.Gate.Release(); }
    }

    private async ValueTask<HeadlessJsonResponse> ReadJsonAsync(Guid sessionId, Func<HeadlessSession, object?> read, CancellationToken cancellationToken)
    {
        var session = GetSession(sessionId);
        await session.Gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try { return JsonResponse(session, read(session), changed: false); }
        finally { session.Gate.Release(); }
    }

    private ValueTask<HeadlessJsonResponse> GetClipAsync(HeadlessClipRequest request, CancellationToken cancellationToken)
        => ReadJsonAsync(request.SessionId, session => session.Editor.GetClip(request.ClipId), cancellationToken);

    private async ValueTask<HeadlessJsonResponse> MutateAsync(
        HeadlessClipMutationRequest request,
        Func<TimelineProjectEditor, HeadlessClipMutationRequest, object?> mutation,
        CancellationToken cancellationToken)
        => await MutateCoreAsync(request.Precondition, session => mutation(session.Editor, request), cancellationToken).ConfigureAwait(false);

    private ValueTask<HeadlessJsonResponse> MoveClipAsync(MoveHeadlessClipRequest request, CancellationToken cancellationToken)
        => MutateCoreAsync(request.Precondition, session => session.Editor.MoveClip(
            request.ClipId, request.LayerIndex, request.StartFrame,
            request.HasSubLayerIndex ? request.SubLayerIndex : null), cancellationToken);

    private ValueTask<HeadlessJsonResponse> RemoveEffectAsync(RemoveHeadlessEffectRequest request, CancellationToken cancellationToken)
        => MutateCoreAsync(request.Precondition, session => session.Editor.RemoveEffect(request.ClipId, request.EffectKey), cancellationToken);

    private ValueTask<HeadlessJsonResponse> RemoveEffectBundleAsync(RemoveHeadlessEffectBundleRequest request, CancellationToken cancellationToken)
        => MutateCoreAsync(request.Precondition, session => session.Editor.RemoveEffectBundle(request.ClipId, request.BundleId), cancellationToken);

    private async ValueTask<HeadlessJsonResponse> MutateCoreAsync(
        HeadlessMutationPrecondition precondition,
        Func<HeadlessSession, object?> mutation,
        CancellationToken cancellationToken)
    {
        var session = GetSession(precondition.SessionId);
        await session.Gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        WorkspaceState backup = CaptureWorkspace(session);
        try
        {
            ValidatePrecondition(session, precondition);
            object? result = mutation(session);
            bool changed = result is not bool booleanResult || booleanResult;
            if (changed) await CommitMutationAsync(session, cancellationToken).ConfigureAwait(false);
            return JsonResponse(session, result, changed);
        }
        catch
        {
            RestoreWorkspace(session, backup);
            throw;
        }
        finally { session.Gate.Release(); }
    }

    private async ValueTask<HeadlessProjectSnapshot> SaveAsync(HeadlessSaveProjectRequest request, CancellationToken cancellationToken)
    {
        var session = GetSession(request.Precondition.SessionId);
        await session.Gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ValidatePrecondition(session, request.Precondition);
            session.Workspace.Save(request.ChangeReason);
            await CommitMutationAsync(session, cancellationToken).ConfigureAwait(false);
            return CreateSnapshot(session);
        }
        finally { session.Gate.Release(); }
    }

    private async ValueTask CommitMutationAsync(HeadlessSession session, CancellationToken cancellationToken)
    {
        await RefreshRenderSessionAsync(session, cancellationToken).ConfigureAwait(false);
        session.Revision++;
    }

    private async ValueTask RefreshRenderSessionAsync(HeadlessSession session, CancellationToken cancellationToken)
    {
        string projectJson = Serialize(session.Workspace.ProjectInfo);
        string timelineJson = Serialize(session.Workspace.Draft);
        var assets = BuildRenderAssets(session.Workspace);
        string renderCacheNamespace = ComputeSnapshotHash(projectJson, timelineJson, Serialize(assets));

        var response = await _renderService.DispatchAsync(new RenderRequestEnvelope
        {
            RequestId = Guid.NewGuid(),
            ClientId = "headless-project-service",
            Operation = RenderOperation.OpenProject,
            Payload = RenderRpcSerializer.Serialize(new OpenProjectRequest
            {
                SessionId = session.Id,
                ProjectRoot = session.Workspace.ProjectRoot,
                ProjectJson = projectJson,
                TimelineJson = timelineJson,
                ProxyRoot = Path.Combine(session.Workspace.ProjectRoot, "proxy"),
                Assets = assets,
                ProjectWidth = session.Workspace.ProjectInfo.RelativeWidth,
                ProjectHeight = session.Workspace.ProjectInfo.RelativeHeight,
                FrameRate = checked((int)session.Workspace.ProjectInfo.TargetFrameRate),
                CacheNamespace = renderCacheNamespace,
            }),
        }, cancellationToken).ConfigureAwait(false);

        if (response.Error is not null) response.Error.ThrowAsException();
        session.RenderSession = RenderRpcSerializer.Deserialize<RenderSession>(response.Payload);
        session.SnapshotHash = ComputeSnapshotHash(projectJson, timelineJson, Serialize(session.Workspace.Assets));
    }

    private List<AssetPathEntry> BuildRenderAssets(TimelineProjectWorkspace workspace)
    {
        IEnumerable<AssetPathEntry> projectAssets = workspace.Assets.Select(static asset => new AssetPathEntry
        {
            AssetId = asset.AssetId ?? string.Empty,
            Path = asset.Path ?? string.Empty,
        });
        return BuildRenderAssets(workspace.ProjectRoot, workspace.Draft, projectAssets);
    }

    private List<AssetPathEntry> BuildRenderAssets(
        string projectRoot,
        DraftStructureJSON draft,
        IEnumerable<AssetPathEntry> projectAssets)
    {
        var assets = new Dictionary<string, AssetPathEntry>(StringComparer.Ordinal);
        foreach (AssetPathEntry asset in projectAssets)
        {
            AddRenderAsset(assets, asset.AssetId, asset.Path, projectRoot);
        }

        var referencedAssetIds = draft.Clips
            .Select(static clip => clip.FilePath)
            .Concat(draft.SoundTracks.Select(static track => track.FilePath))
            .Where(static path => path?.StartsWith('$') == true && path.Length > 1)
            .Select(static path => path![1..])
            .ToHashSet(StringComparer.Ordinal);
        referencedAssetIds.ExceptWith(assets.Keys);

        foreach (string databasePath in GetGlobalAssetDatabaseCandidates(projectRoot))
        {
            if (referencedAssetIds.Count == 0 || !File.Exists(databasePath)) continue;

            Dictionary<string, AssetItem>? globalAssets;
            try
            {
                globalAssets = JsonSerializer.Deserialize<Dictionary<string, AssetItem>>(
                    File.ReadAllText(databasePath), TimelineProjectWorkspace.JsonOptions);
            }
            catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException)
            {
                continue;
            }

            if (globalAssets is null) continue;
            string globalAssetsRoot = Directory.GetParent(Path.GetDirectoryName(databasePath)!)?.FullName
                ?? Path.GetDirectoryName(databasePath)!;
            foreach (string assetId in referencedAssetIds.ToArray())
            {
                if (!globalAssets.TryGetValue(assetId, out AssetItem? asset)) continue;
                AddRenderAsset(assets, assetId, asset.Path, globalAssetsRoot);
                if (assets.ContainsKey(assetId)) referencedAssetIds.Remove(assetId);
            }
        }

        return assets.Values
            .OrderBy(static asset => asset.AssetId, StringComparer.Ordinal)
            .ToList();
    }

    private IEnumerable<string> GetGlobalAssetDatabaseCandidates(string projectRoot)
    {
        var yielded = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (_globalAssetsDatabasePath is not null && yielded.Add(_globalAssetsDatabasePath))
            yield return _globalAssetsDatabasePath;

        DirectoryInfo? projectDirectory = new(projectRoot);
        if (projectDirectory.Parent is { Name: "My Drafts", Parent: { } dataRoot })
        {
            string inferred = Path.Combine(dataRoot.FullName, "My Assets", ".database", "database.json");
            inferred = Path.GetFullPath(inferred);
            if (yielded.Add(inferred)) yield return inferred;
        }
    }

    private static void AddRenderAsset(
        IDictionary<string, AssetPathEntry> assets,
        string? assetId,
        string? path,
        string relativeRoot)
    {
        if (string.IsNullOrWhiteSpace(assetId) || string.IsNullOrWhiteSpace(path)) return;
        string resolvedPath = Path.IsPathRooted(path)
            ? Path.GetFullPath(path)
            : Path.GetFullPath(Path.Combine(relativeRoot, path));
        assets[assetId] = new AssetPathEntry { AssetId = assetId, Path = resolvedPath };
    }

    private static HeadlessProjectSnapshot CreateSnapshot(HeadlessSession session) => new()
    {
        SessionId = session.Id,
        ProjectRoot = session.Workspace.ProjectRoot,
        Revision = session.Revision,
        SnapshotHash = session.SnapshotHash,
        ProjectJson = Serialize(session.Workspace.ProjectInfo),
        TimelineJson = Serialize(session.Workspace.Draft),
        AssetsJson = Serialize(session.Workspace.Assets),
        RenderSession = session.RenderSession,
    };

    private static HeadlessJsonResponse JsonResponse(HeadlessSession session, object? value, bool changed) => new()
    {
        Snapshot = CreateSnapshot(session),
        Json = value is null ? "null" : Serialize(value),
        Changed = changed,
    };

    private HeadlessSession GetSession(Guid sessionId)
    {
        if (sessionId == Guid.Empty && _defaultSessionId != Guid.Empty && _sessions.TryGetValue(_defaultSessionId, out var defaultSession))
            return defaultSession;
        if (_sessions.TryGetValue(sessionId, out var session))
            return session;
        throw new HeadlessSessionNotFoundException($"Headless project session '{sessionId}' was not found.");
    }

    public async ValueTask<(byte[] Content, string ContentType)> ReadArtifactAsync(
        Guid sessionId,
        Guid artifactId,
        CancellationToken cancellationToken = default)
    {
        _ = GetSession(sessionId);
        (byte[] content, string path) = await Task.Run(
            () => _renderService.ReadArtifact(new ArtifactRequest { SessionId = sessionId, ArtifactId = artifactId }),
            cancellationToken).ConfigureAwait(false);
        return (content, ResolveArtifactContentType(path));
    }

    private static string ResolveArtifactContentType(string path)
        => Path.GetExtension(path).ToLowerInvariant() switch
        {
            ".png" => "image/png",
            ".jpg" or ".jpeg" => "image/jpeg",
            ".wav" => "audio/wav",
            ".mp4" => "video/mp4",
            _ => "application/octet-stream",
        };

    private static void ValidatePrecondition(HeadlessSession session, HeadlessMutationPrecondition precondition)
    {
        if (precondition.BaseRevision == session.Revision &&
            string.Equals(precondition.BaseSnapshotHash, session.SnapshotHash, StringComparison.Ordinal)) return;

        string details = JsonSerializer.Serialize(new
        {
            sessionId = session.Id,
            currentRevision = session.Revision,
            currentSnapshotHash = session.SnapshotHash,
            clipCount = session.Workspace.Draft.Clips.Length,
        });
        throw new HeadlessVersionConflictException(
            $"Project session '{session.Id}' changed after revision {precondition.BaseRevision}.", details);
    }

    private static T Read<T>(RenderRequestEnvelope request)
        => RenderRpcSerializer.Deserialize<T>(request.Payload);

    private static T Deserialize<T>(string json)
        => JsonSerializer.Deserialize<T>(json, TimelineProjectWorkspace.JsonOptions)
            ?? throw new ArgumentException($"Invalid {typeof(T).Name} JSON.");

    private static string Serialize<T>(T value)
        => JsonSerializer.Serialize(value, TimelineProjectWorkspace.JsonOptions);

    private static string ComputeSnapshotHash(params string[] values)
        => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(string.Join("\n", values))));

    private static WorkspaceState CaptureWorkspace(HeadlessSession session) => new(
        Serialize(session.Workspace.ProjectInfo),
        Serialize(session.Workspace.Draft),
        Serialize(session.Workspace.Assets));

    private static void RestoreWorkspace(HeadlessSession session, WorkspaceState backup)
    {
        session.Workspace.ReplaceProjectInfo(Deserialize<ProjectJSONStructure>(backup.ProjectJson));
        session.Workspace.ReplaceDraft(Deserialize<DraftStructureJSON>(backup.TimelineJson));
        session.Workspace.ReplaceAssets(Deserialize<List<AssetItem>>(backup.AssetsJson));
        session.ReplaceWorkspace(session.Workspace);
    }

    private static RenderResponseEnvelope Success<T>(RenderRequestEnvelope request, T response) => new()
    {
        RequestId = request.RequestId,
        Payload = RenderRpcSerializer.Serialize(response),
    };

    private static RenderResponseEnvelope Failure(RenderRequestEnvelope request, RenderErrorCode code, string message, string details = "") => new()
    {
        RequestId = request.RequestId,
        Error = new RemoteError { Code = code, Message = message, Details = details },
    };

    private static RenderResponseEnvelope Failure(RenderRequestEnvelope request, RenderErrorCode code, Exception exception, string? customMessage = null, string? customDetails = null) => new()
    {
        RequestId = request.RequestId,
        Error = new RemoteError(exception, code, customMessage: customMessage, customDetails: customDetails),
    };

    public async ValueTask DisposeAsync()
    {
        foreach (var session in _sessions.Values) session.Dispose();
        _sessions.Clear();
        _defaultSessionId = Guid.Empty;
        if (_ownsRenderService) await _renderService.DisposeAsync().ConfigureAwait(false);
    }

    private sealed class HeadlessSession(Guid id, TimelineProjectWorkspace workspace) : IDisposable
    {
        public Guid Id { get; } = id;
        public TimelineProjectWorkspace Workspace { get; private set; } = workspace;
        public TimelineProjectEditor Editor { get; private set; } = new(workspace);
        public SemaphoreSlim Gate { get; } = new(1, 1);
        public long Revision { get; set; } = 1;
        public string SnapshotHash { get; set; } = string.Empty;
        public RenderSession RenderSession { get; set; } = new();

        public void ReplaceWorkspace(TimelineProjectWorkspace replacement)
        {
            Workspace = replacement;
            Editor = new TimelineProjectEditor(replacement);
        }

        public void Dispose() => Gate.Dispose();
    }

    private sealed class HeadlessVersionConflictException(string message, string details) : Exception(message)
    {
        public string Details { get; } = details;
    }

    private sealed class HeadlessSessionNotFoundException(string message) : Exception(message);

    private sealed record WorkspaceState(string ProjectJson, string TimelineJson, string AssetsJson);
}
