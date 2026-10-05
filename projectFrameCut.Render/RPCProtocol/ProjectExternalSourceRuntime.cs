using projectFrameCut.Render.Contracts;
using projectFrameCut.Render.PluginIsolation;
using System.Security.Cryptography;
using System.Collections.Concurrent;

namespace projectFrameCut.Render.RPCProtocol;

public static class ProjectExternalSourceRuntime
{
    private static readonly ProjectExternalSourceHost Default = new();
    private static readonly AsyncLocal<ProjectExternalSourceHost?> Context = new();
    public static ProjectExternalSourceHost Current => Context.Value ?? Default;
    public static Func<string, ProjectExternalSourceAsset, CancellationToken, ValueTask<IPluginIsolationSession>>? StartWorker { get; set; }
    public static Task<ProjectExternalSourceCatalog> SetAsync(SetProjectExternalSourcesRequest request, CancellationToken cancellationToken = default) => Default.SetAsync(request, cancellationToken);
    public static Task<ProjectExternalSourceCatalog> ListAsync(string root, CancellationToken cancellationToken = default) => Default.ListAsync(root, cancellationToken);
    public static Task CloseAsync() => Default.CloseAsync();
    public static Task<IAsyncDisposable> OpenAsync(string root, List<ProjectExternalSourceApproval> approvals, CancellationToken cancellationToken = default) => Default.OpenAsync(root, approvals, cancellationToken);
    public static PluginIsolationLaunchContext CreateContext(ProjectExternalSourceAsset asset, string sourceRoot, string sessionRoot, string instanceName) => new()
    {
        PluginId = asset.ImportId.ToString("N"), PluginRoot = sourceRoot, SessionRoot = sessionRoot,
        AuthenticationToken = Convert.ToHexString(RandomNumberGenerator.GetBytes(32)), PluginEncryptionKey = string.Empty,
        InstancePackageName = instanceName, ExternalSourceManifestHash = asset.ManifestSha256,
    };
    public static IDisposable Use(ProjectExternalSourceHost host)
    {
        var previous = Context.Value;
        Context.Value = host;
        return new Scope(previous);
    }
    private sealed class Scope(ProjectExternalSourceHost? previous) : IDisposable
    {
        public void Dispose() => Context.Value = previous;
    }
}

// Each decoder captures its host to keep source instances scoped to the project backend.
public sealed class ProjectExternalSourceHost : IAsyncDisposable
{
    public async Task<IAsyncDisposable> OpenAsync(string root, List<ProjectExternalSourceApproval> approvals, CancellationToken cancellationToken = default)
    {
        try { await SetAsync(new() { ProjectRoot = root, AllowedSources = approvals }, cancellationToken).ConfigureAwait(false); }
        catch { await CloseAsync().ConfigureAwait(false); throw; }
        return this;
    }
    public async ValueTask DisposeAsync() => await CloseAsync().ConfigureAwait(false);
    private readonly SemaphoreSlim Gate = new(1, 1);
    private readonly ConcurrentDictionary<Guid, Entry> Entries = new();
    private readonly ConcurrentDictionary<Guid, string> Errors = new();
    private string? activeRoot;
    private readonly string identity = Guid.NewGuid().ToString("N");
    private long generation;
    private bool hasSources;
    public string CacheToken => hasSources ? $"{identity}-{Volatile.Read(ref generation)}-{string.Join(',', Entries.OrderBy(x => x.Key).Select(x => $"{x.Key:N}:{x.Value.Client.IsAvailable}:{x.Value.Client.FrameRevision}"))}" : string.Empty;

    public async Task<ProjectExternalSourceCatalog> SetAsync(SetProjectExternalSourcesRequest request, CancellationToken cancellationToken = default)
    {
        var root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(request.ProjectRoot));
        await Gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ProjectExternalSourceIndex index;
            try { index = ProjectExternalSourceDatabase.Read(root); }
            catch (Exception ex) when (ex is IOException or InvalidDataException or System.Text.Json.JsonException or NotSupportedException)
            {
                Log(ex, "Read project external source index.");
                foreach (var id in Entries.Keys.ToArray()) await RemoveAsync(id).ConfigureAwait(false);
                hasSources = true;
                return new();
            }
            hasSources = index.Assets.Count > 0;
            if (index.Assets.Count == 0 && Entries.Count == 0) return new();
            if (activeRoot is not null && !SameRoot(activeRoot, root))
                throw new NotSupportedException("A render process cannot host external sources from multiple projects.");
            var allowed = request.AllowedSources.ToDictionary(x => x.ImportId);
            if (allowed.Keys.Except(index.Assets.Select(x => x.ImportId)).Any()) throw new ArgumentException("Unknown project external source ImportId.");
            activeRoot = root;
            foreach (var id in Entries.Keys.ToArray())
                if (!allowed.TryGetValue(id, out var approval) || !string.Equals(approval.ManifestSha256, Entries[id].Asset.ManifestSha256, StringComparison.OrdinalIgnoreCase))
                    await RemoveAsync(id).ConfigureAwait(false);
            Errors.Clear();
            foreach (var asset in index.Assets.Where(x => allowed.ContainsKey(x.ImportId)))
            {
                ProjectExternalSourceClient? client = null;
                try
                {
                    if (!string.Equals(allowed[asset.ImportId].ManifestSha256, asset.ManifestSha256, StringComparison.OrdinalIgnoreCase))
                        throw new InvalidDataException("External source approval does not match the indexed manifest.");
                    if (Entries.TryGetValue(asset.ImportId, out var existing))
                    {
                        if (existing.Client.IsAvailable && existing.Client.FrameError is null) continue;
                        await RemoveAsync(asset.ImportId).ConfigureAwait(false);
                    }
                    await ProjectExternalSourceDatabase.ValidateAsync(ProjectExternalSourceDatabase.ResolveAssetDirectory(root, asset), asset.ManifestSha256, cancellationToken).ConfigureAwait(false);
                    if (ProjectExternalSourceRuntime.StartWorker is null) throw new PlatformNotSupportedException("This render host does not provide external source isolation.");
                    client = new(await ProjectExternalSourceRuntime.StartWorker(root, asset, cancellationToken).ConfigureAwait(false));
                    await client.LoadAsync(cancellationToken).ConfigureAwait(false);
                    asset.Sources = client.Sources.Select(x => RenderRpcSerializer.Clone(x)).ToList();
                    if (asset.Sources.Any(x => string.IsNullOrWhiteSpace(x.SourceId) || string.IsNullOrWhiteSpace(x.DecoderName))
                        || asset.Sources.Select(x => x.SourceId).Distinct(StringComparer.Ordinal).Count() != asset.Sources.Count)
                        throw new InvalidDataException("External source provider returned an invalid catalog.");
                    foreach (var source in asset.Sources) { source.ClientId = asset.ImportId; source.ClientName = asset.Manifest.Name; }
                    await ProjectExternalSourceDatabase.UpdateSourcesAsync(root, asset.ImportId, asset.Sources, cancellationToken).ConfigureAwait(false);
                    Entries[asset.ImportId] = new(asset, client);
                    Interlocked.Increment(ref generation);
                    client = null;
                    Log($"Loaded project external source '{asset.Manifest.Name}' ({asset.ImportId}).");
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
                catch (Exception ex) { Errors[asset.ImportId] = ex.Message; Log(ex, $"Load project external source '{asset.Manifest.Id}'."); }
                finally
                {
                    if (client is not null) await client.DisposeAsync().ConfigureAwait(false);
                }
            }
            return Catalog(index);
        }
        finally { Gate.Release(); }
    }

    public async Task<ProjectExternalSourceCatalog> ListAsync(string projectRoot, CancellationToken cancellationToken = default)
    {
        await Gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try { return Catalog(ProjectExternalSourceDatabase.Read(projectRoot), activeRoot is not null && SameRoot(activeRoot, projectRoot)); }
        finally { Gate.Release(); }
    }

    private ProjectExternalSourceCatalog Catalog(ProjectExternalSourceIndex index, bool current = true) => new()
    {
        Sources = index.Assets.Select(x => new ProjectExternalSourceStatus
        { ImportId = x.ImportId, Loaded = current && Entries.TryGetValue(x.ImportId, out var loaded) && loaded.Client.IsAvailable,
            Error = current ? Errors.GetValueOrDefault(x.ImportId) ?? (Entries.TryGetValue(x.ImportId, out var worker) ? worker.Client.FrameError ?? (!worker.Client.IsAvailable ? "External source worker has exited." : string.Empty) : string.Empty) : string.Empty,
            Name = x.Manifest.Name, Version = x.Manifest.Version, Author = x.Manifest.Author, ManifestId = x.Manifest.Id,
            Description = x.Manifest.Description ?? string.Empty, AuthorUrl = x.Manifest.AuthorUrl ?? string.Empty,
            ManifestSha256 = x.ManifestSha256, Directory = x.Directory, ImportedAt = x.CreatedAt,
            Sources = current && Entries.TryGetValue(x.ImportId, out var entry) ? entry.Asset.Sources : x.Sources }).ToList(),
    };

    public async Task CloseAsync()
    {
        await Gate.WaitAsync().ConfigureAwait(false);
        try
        {
            foreach (var id in Entries.Keys.ToArray()) await RemoveAsync(id).ConfigureAwait(false);
            Errors.Clear();
            activeRoot = null;
            hasSources = false;
        }
        finally { Gate.Release(); }
    }

    private async Task RemoveAsync(Guid id)
    {
        if (!Entries.TryRemove(id, out var entry)) return;
        Interlocked.Increment(ref generation);
        try { await entry.Client.DisposeAsync().ConfigureAwait(false); }
        catch (Exception ex) { Log(ex, $"Close project external source {id}."); }
    }

    private static bool SameRoot(string a, string b) => string.Equals(Path.TrimEndingDirectorySeparator(Path.GetFullPath(a)), Path.TrimEndingDirectorySeparator(Path.GetFullPath(b)), OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal);

    public ProjectExternalSourceClient GetClient(Guid importId)
    {
        if (Entries.TryGetValue(importId, out var entry) && entry.Client.IsAvailable) return entry.Client;
        throw new IOException(Errors.GetValueOrDefault(importId) ?? $"Project external source {importId} was skipped, is missing, or its worker has exited.");
    }

    private sealed record Entry(ProjectExternalSourceAsset Asset, ProjectExternalSourceClient Client);
}
