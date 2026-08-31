using System.Text.Json;
using System.Security.Cryptography;
using projectFrameCut.Render.Contracts;
using projectFrameCut.Render.RenderAPIBase.Project;
using projectFrameCut.Render.RPCProtocol;
using projectFrameCut.Setting.SettingManager;

namespace projectFrameCut.Services;

/// <summary>
/// Owns a headless project session on a remote projectFrameCut device.
/// The UI edits its normal in-memory model and commits a complete snapshot when
/// the page is saved, while the server provides optimistic concurrency checks.
/// </summary>
internal sealed class RemoteProjectSession : IAsyncDisposable
{
    private readonly IRenderClient _client;
    private readonly Func<RenderArtifact, string, CancellationToken, Task<string>> _artifactResolver;
    private readonly bool _ownsClient;
    private readonly bool _closeSession;
    private readonly bool _monitorChanges;
    private bool _closed;

    private RemoteProjectSession(
        IRenderClient client,
        HeadlessProjectSnapshot snapshot,
        Func<RenderArtifact, string, CancellationToken, Task<string>> artifactResolver,
        bool ownsClient,
        bool closeSession,
        bool monitorChanges)
    {
        _client = client;
        Snapshot = snapshot;
        _artifactResolver = artifactResolver;
        _ownsClient = ownsClient;
        _closeSession = closeSession;
        _monitorChanges = monitorChanges;
    }

    public IRenderClient Client => _client;
    public HeadlessProjectSnapshot Snapshot { get; private set; }
    public long LastSavedRevision { get; private set; } = -1;
    public bool MonitorChanges => _monitorChanges;
    public string ProjectRoot => Snapshot.ProjectRoot;
    public ProjectJSONStructure Project => Deserialize<ProjectJSONStructure>(Snapshot.ProjectJson);
    public DraftStructureJSON Draft => Deserialize<DraftStructureJSON>(Snapshot.TimelineJson);
    public List<AssetItem> Assets => Deserialize<List<AssetItem>>(Snapshot.AssetsJson);

    public static async Task<RemoteProjectSession> OpenAsync(
        Uri serverUri,
        string token,
        string? clientId = null,
        CancellationToken cancellationToken = default)
    {
        string effectiveClientId = string.IsNullOrWhiteSpace(clientId) ? $"remote-editor-{Guid.NewGuid():N}" : clientId;
        var transport = new HttpRenderClientTransport(serverUri, token, effectiveClientId);
        var client = new RenderClient(transport, effectiveClientId);
        try
        {
            // The headless server owns the project lifecycle. An empty session ID
            // asks it for the project loaded during RPC server startup.
            HeadlessProjectSnapshot snapshot;
            try
            {
                snapshot = await client.GetHeadlessProjectSnapshotAsync(
                    Guid.Empty, cancellationToken).ConfigureAwait(false);
            }
            catch (RemoteRenderException ex) when (ex.ErrorCode == RenderErrorCode.SessionNotFound)
            {
                // The server may have started without a preloaded project (for
                // example when the GUI launches rpc_server with --http but no
                // --projectRoot). The caller needs to open one explicitly first.
                string? message = null;
                try { message = SettingsManager.SettingLocalizedResources.Remote_RpcServer_NoDefaultProject; } catch { }
                throw new InvalidOperationException(
                    message ?? "The remote RPC server has no project loaded. Open a project on the remote device first (or start it with --projectRoot), then try again.",
                    ex);
            }
            return new RemoteProjectSession(
                client,
                snapshot,
                (artifact, localProjectRoot, ct) => DownloadArtifactAsync(transport, artifact, localProjectRoot, ct),
                ownsClient: true,
                closeSession: true,
                monitorChanges: true);
        }
        catch
        {
            await client.DisposeAsync().ConfigureAwait(false);
            throw;
        }
    }

    public static RemoteProjectSession CreateNamedPipeSession(
        IRenderClient client,
        HeadlessProjectSnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(client);
        ArgumentNullException.ThrowIfNull(snapshot);
        return new RemoteProjectSession(
            client,
            snapshot,
            static (artifact, projectRoot, _) => Task.FromResult(ResolveLocalArtifact(artifact, projectRoot)),
            ownsClient: false,
            closeSession: false,
            monitorChanges: false);
    }

    public async Task<string> MaterializeArtifactAsync(
        RenderArtifact artifact,
        string localProjectRoot,
        CancellationToken cancellationToken = default)
    {
        ThrowIfClosed();
        return await _artifactResolver(artifact, localProjectRoot, cancellationToken).ConfigureAwait(false);
    }

    private static async Task<string> DownloadArtifactAsync(
        HttpRenderClientTransport transport,
        RenderArtifact artifact,
        string localProjectRoot,
        CancellationToken cancellationToken)
    {
        string localPath = RenderRpcBootstrap.ResolveArtifactPath(localProjectRoot, artifact);
        string? directory = Path.GetDirectoryName(localPath);
        if (directory is null) throw new InvalidDataException("The remote artifact path has no parent directory.");
        Directory.CreateDirectory(directory);

        if (File.Exists(localPath) && await MatchesArtifactAsync(localPath, artifact, cancellationToken).ConfigureAwait(false))
            return localPath;

        byte[] content = await transport.DownloadArtifactAsync(
            new ArtifactRequest { SessionId = artifact.SessionId, ArtifactId = artifact.ArtifactId },
            cancellationToken).ConfigureAwait(false);
        if (artifact.Size >= 0 && content.LongLength != artifact.Size)
            throw new InvalidDataException($"Remote artifact size mismatch for '{artifact.ProjectRelativePath}'.");
        if (!string.IsNullOrWhiteSpace(artifact.ContentHash) &&
            !string.Equals(Convert.ToHexString(SHA256.HashData(content)), artifact.ContentHash, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException($"Remote artifact hash mismatch for '{artifact.ProjectRelativePath}'.");

        string temporaryPath = Path.Combine(directory, $".{Path.GetFileName(localPath)}.download-{Guid.NewGuid():N}");
        try
        {
            await File.WriteAllBytesAsync(temporaryPath, content, cancellationToken).ConfigureAwait(false);
            File.Move(temporaryPath, localPath, overwrite: true);
        }
        finally
        {
            try { if (File.Exists(temporaryPath)) File.Delete(temporaryPath); } catch { }
        }
        return localPath;
    }

    private static string ResolveLocalArtifact(RenderArtifact artifact, string projectRoot)
    {
        string path = RenderRpcBootstrap.ResolveArtifactPath(projectRoot, artifact);
        if (!File.Exists(path)) throw new FileNotFoundException("The named-pipe render artifact was not found.", path);
        return path;
    }

    public async Task ApplyAndSaveAsync(
        ProjectJSONStructure project,
        DraftStructureJSON draft,
        IEnumerable<AssetItem> assets,
        string changeReason,
        CancellationToken cancellationToken = default)
    {
        ThrowIfClosed();
        var precondition = CreatePrecondition();
        Snapshot = await _client.ApplyHeadlessProjectSnapshotAsync(new ApplyHeadlessProjectSnapshotRequest
        {
            Precondition = precondition,
            ProjectJson = Serialize(project),
            TimelineJson = Serialize(draft),
            AssetsJson = Serialize(assets.ToList()),
        }, cancellationToken).ConfigureAwait(false);

        Snapshot = await _client.SaveHeadlessProjectAsync(new HeadlessSaveProjectRequest
        {
            Precondition = CreatePrecondition(),
            ChangeReason = changeReason ?? string.Empty,
        }, cancellationToken).ConfigureAwait(false);
        LastSavedRevision = Snapshot.Revision;
    }

    /// <summary>
    /// Pulls the latest project snapshot from the server and replaces the cached
    /// one. Returns true when the server-side project was modified by someone
    /// else since the last snapshot this session saw.
    /// </summary>
    public async Task<bool> SyncFromServerAsync(CancellationToken cancellationToken = default)
    {
        HeadlessProjectSnapshot latest = await ReadLatestSnapshotAsync(cancellationToken).ConfigureAwait(false);
        bool changed = !MatchesSnapshot(latest);
        AdoptSnapshot(latest);
        return changed;
    }

    public async Task<HeadlessProjectSnapshot> ReadLatestSnapshotAsync(CancellationToken cancellationToken = default)
    {
        ThrowIfClosed();
        return await _client.GetHeadlessProjectSnapshotAsync(
            Snapshot.SessionId, cancellationToken).ConfigureAwait(false);
    }

    public bool MatchesSnapshot(HeadlessProjectSnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        return snapshot.SessionId == Snapshot.SessionId &&
            snapshot.Revision == Snapshot.Revision &&
            string.Equals(snapshot.SnapshotHash, Snapshot.SnapshotHash, StringComparison.Ordinal);
    }

    public void AdoptSnapshot(HeadlessProjectSnapshot snapshot)
    {
        ThrowIfClosed();
        ArgumentNullException.ThrowIfNull(snapshot);
        if (snapshot.SessionId != Snapshot.SessionId)
            throw new InvalidOperationException("Cannot adopt a snapshot from another remote project session.");
        Snapshot = snapshot;
    }

    public async Task CloseAsync(CancellationToken cancellationToken = default)
    {
        if (_closed) return;
        _closed = true;
        try
        {
            if (_closeSession)
                await _client.CloseProjectAsync(Snapshot.SessionId, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            if (_ownsClient) await _client.DisposeAsync().ConfigureAwait(false);
        }
    }

    public async Task CloseSharedServerSessionAsync(CancellationToken cancellationToken = default)
    {
        if (_closed) return;
        await _client.CloseProjectAsync(Snapshot.SessionId, cancellationToken).ConfigureAwait(false);
        _closed = true;
    }

    public async ValueTask DisposeAsync() => await CloseAsync().ConfigureAwait(false);

    private HeadlessMutationPrecondition CreatePrecondition() => new()
    {
        SessionId = Snapshot.SessionId,
        BaseRevision = Snapshot.Revision,
        BaseSnapshotHash = Snapshot.SnapshotHash,
    };

    private void ThrowIfClosed()
    {
        if (_closed) throw new ObjectDisposedException(nameof(RemoteProjectSession));
    }

    private static async Task<bool> MatchesArtifactAsync(
        string path,
        RenderArtifact artifact,
        CancellationToken cancellationToken)
    {
        if (artifact.Size >= 0 && new FileInfo(path).Length != artifact.Size) return false;
        if (string.IsNullOrWhiteSpace(artifact.ContentHash)) return true;
        await using var stream = File.OpenRead(path);
        byte[] hash = await SHA256.HashDataAsync(stream, cancellationToken).ConfigureAwait(false);
        return string.Equals(Convert.ToHexString(hash), artifact.ContentHash, StringComparison.OrdinalIgnoreCase);
    }

    private static T Deserialize<T>(string json)
        => JsonSerializer.Deserialize<T>(json, DraftPage.DraftJSONOption)
            ?? throw new InvalidDataException($"Remote project returned invalid {typeof(T).Name} JSON.");

    private static string Serialize<T>(T value) => JsonSerializer.Serialize(value, DraftPage.DraftJSONOption);
}
