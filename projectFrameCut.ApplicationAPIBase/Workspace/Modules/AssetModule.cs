using projectFrameCut.Render.RenderAPIBase.Project;
using System.Security.Cryptography;
using System.Text.Json;

namespace projectFrameCut.ApplicationAPIBase.Workspace.Modules;

public sealed class AssetModule(IEnumerable<AssetItem>? assets = null) : WorkspaceModuleBase, IWorkspaceModuleDependencies
{
    private readonly Dictionary<string, AssetItem> _assets = (assets ?? []).Where(x => !string.IsNullOrWhiteSpace(x.AssetId)).ToDictionary(x => x.AssetId!, StringComparer.Ordinal);
    public const string ModuleId = "assets.core";
    public override string Id => ModuleId;
    public IReadOnlyCollection<Type> Dependencies { get; } = [typeof(ProjectModule)];
    public IReadOnlyCollection<AssetItem> Assets => _assets.Values.ToList().AsReadOnly();
    public event EventHandler? Changed;
    public void Reset(IEnumerable<AssetItem> assets)
    {
        _assets.Clear();
        foreach (var asset in assets)
        {
            asset.AssetId ??= Guid.CreateVersion7().ToString("N");
            _assets[asset.AssetId] = asset;
        }
        Changed?.Invoke(this, EventArgs.Empty);
    }
    public AssetItem Add(AssetItem asset)
    {
        ArgumentNullException.ThrowIfNull(asset);
        asset.AssetId ??= Guid.CreateVersion7().ToString("N");
        if (!string.IsNullOrWhiteSpace(asset.SourceHash) && _assets.Values.FirstOrDefault(x => x.SourceHash == asset.SourceHash) is { } duplicate) return duplicate;
        _assets[asset.AssetId] = asset; Context?.Modules.Get<ProjectModule>().MarkDirty("Asset added"); Changed?.Invoke(this, EventArgs.Empty); return asset;
    }
    public bool Remove(string assetId)
    {
        var removed = _assets.Remove(assetId);
        if (removed) { Context?.Modules.Get<ProjectModule>().MarkDirty("Asset removed"); Changed?.Invoke(this, EventArgs.Empty); }
        return removed;
    }
    public static async Task<string> ComputeHashAsync(string path, CancellationToken cancellationToken = default)
    {
        await using var stream = File.OpenRead(path); var hash = await SHA256.HashDataAsync(stream, cancellationToken); return Convert.ToHexString(hash);
    }
    public Task SaveAsync(string relativePath = "assets.json", CancellationToken cancellationToken = default)
        => Context?.Storage.WriteTextAsync(relativePath, JsonSerializer.Serialize(_assets.Values, new JsonSerializerOptions { WriteIndented = true }), cancellationToken)
            ?? throw new InvalidOperationException("The module is not initialized.");
}
