using projectFrameCut.Asset;
using projectFrameCut.Drawing.Base;
using projectFrameCut.Drawing.Base.Picture;
using projectFrameCut.Render.ClipsAndTracks;
using projectFrameCut.Render.Plugin;
using projectFrameCut.Render.RenderAPIBase.Project;
using projectFrameCut.Shared;

namespace projectFrameCut.Services;

internal sealed class ProjectAssetFile
{
    internal static readonly StringComparer PathComparer = OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;
    public required string Name { get; init; }
    public required string Path { get; init; }
    public HashSet<string> AssetIds { get; } = new(StringComparer.Ordinal);
    public HashSet<ClipMode> ClipTypes { get; } = [];
    public int ReferenceCount { get; private set; }
    public bool IsMissing { get; private init; }

    public static string ResolvePath(string path, string projectRoot)
    {
        if (string.IsNullOrWhiteSpace(path) || path.StartsWith('#') || path.StartsWith('$')) return path;
        try { return System.IO.Path.GetFullPath(System.IO.Path.Combine(projectRoot, path)); }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or System.IO.PathTooLongException) { return path; }
    }

    public bool Matches(string? source, string projectRoot)
        => !string.IsNullOrWhiteSpace(source) && (source.StartsWith('$')
            ? AssetIds.Contains(source[1..])
            : PathComparer.Equals(Path, ResolvePath(source, projectRoot)));

    private static string? GetSource(string? path, Dictionary<string, object>? data)
        => path ?? data?.GetValueOrDefault("FilePath")?.ToString();

    public static List<ProjectAssetFile> Collect(DraftStructureJSON draft, IEnumerable<AssetItem> assets, string projectRoot)
    {
        var local = assets.Where(a => !string.IsNullOrWhiteSpace(a.AssetId)).ToDictionary(a => a.AssetId!, StringComparer.Ordinal);
        var files = new Dictionary<string, ProjectAssetFile>(PathComparer);

        void Add(string? path, string name, string? assetId, ClipMode type, bool reference)
        {
            if (string.IsNullOrWhiteSpace(path) || path.StartsWith('#')) return;
            if (path.StartsWith('$'))
            {
                assetId = path[1..];
                if ((local.TryGetValue(assetId, out var asset) || AssetDatabase.Assets.TryGetValue(assetId, out asset))
                    && !string.IsNullOrWhiteSpace(asset.Path))
                {
                    path = asset.Path;
                    name = asset.Name;
                }
            }
            if (path.StartsWith('#')) return;
            path = ResolvePath(path, projectRoot);
            if (!files.TryGetValue(path, out var file))
                files[path] = file = new ProjectAssetFile { Path = path, Name = name, IsMissing = path.StartsWith('$') || !File.Exists(path) };
            if (!string.IsNullOrWhiteSpace(assetId)) file.AssetIds.Add(assetId);
            file.ClipTypes.Add(type);
            if (reference) file.ReferenceCount++;
        }

        foreach (var asset in assets)
            Add(string.IsNullOrWhiteSpace(asset.Path) && !string.IsNullOrWhiteSpace(asset.AssetId) ? $"${asset.AssetId}" : asset.Path,
                asset.Name, asset.AssetId, asset.GetClipMode(), false);
        foreach (var clip in draft.Clips ?? [])
            if (clip.ClipType != ClipMode.ProjectReferenceClip && ProjectReferencePart.Read(clip.MetaData) is null)
                Add(GetSource(clip.FilePath, clip.MetaData), clip.Name, null, clip.ClipType, true);
        foreach (var track in draft.SoundTracks ?? [])
            if (ProjectReferencePart.Read(track.MetaData) is null)
                Add(GetSource(track.FilePath, track.MetaData), track.Name, null, ClipMode.AudioClip, true);
        return files.Values.OrderByDescending(f => f.IsMissing)
            .ThenByDescending(f => f.IsMissing && f.ReferenceCount > 0).ThenBy(f => f.Name).ToList();
    }

    public AssetItem ReadReplacement(string path)
    {
        path = System.IO.Path.GetFullPath(path);
        using (File.Open(path, FileMode.Open, FileAccess.Read, FileShare.Read)) { }
        var asset = new AssetItem { Name = Name, Path = path, IsReference = true, AssetType = AssetItem.GetAssetType(path) };
        if (ClipTypes.Contains(ClipMode.PhotoClip))
        {
            using var picture = PictureFP16.Load(path);
            asset.Width = picture.Width;
            asset.Height = picture.Height;
            asset.ThumbnailPath = path;
        }
        if (ClipTypes.Contains(ClipMode.VideoClip))
        {
            using var source = PluginManager.CreateVideoSource(path);
            source.Initialize();
            asset.Width = source.Width;
            asset.Height = source.Height;
            asset.Duration = source.TotalFrames;
            asset.SecondPerFrame = source.Fps > 0 ? (float)(1 / source.Fps) : -1;
        }
        if (ClipTypes.Contains(ClipMode.AudioClip))
        {
            using var source = PluginManager.CreateAudioSource(path);
            if (source.SamplePerSecond <= 0) throw new InvalidDataException("The selected file has no readable audio stream.");
            if (!ClipTypes.Contains(ClipMode.VideoClip))
            {
                asset.Duration = source.Duration;
                asset.SecondPerFrame = 1;
            }
        }
        return asset;
    }

    public void Remap(DraftStructureJSON draft, List<AssetItem> assets, AssetItem replacement, string projectRoot)
    {
        foreach (string id in AssetIds)
        {
            int index = assets.FindIndex(a => a.AssetId == id);
            var original = index >= 0 ? assets[index] : AssetDatabase.Assets.GetValueOrDefault(id);
            var updated = (original ?? replacement) with
            {
                AssetId = id, Path = replacement.Path, IsReference = true, SourceHash = null,
                ThumbnailPath = replacement.ThumbnailPath, Duration = replacement.Duration,
                SecondPerFrame = replacement.SecondPerFrame, Width = replacement.Width, Height = replacement.Height
            };
            if (index >= 0) assets[index] = updated;
            else assets.Add(updated);
        }
        for (int i = 0; i < assets.Count; i++)
            if (Matches(assets[i].Path, projectRoot) && !AssetIds.Contains(assets[i].AssetId ?? string.Empty))
                assets[i] = assets[i] with { Path = replacement.Path, IsReference = true, SourceHash = null, ThumbnailPath = replacement.ThumbnailPath };
        foreach (var clip in draft.Clips ?? [])
            if (clip.ClipType != ClipMode.ProjectReferenceClip && ProjectReferencePart.Read(clip.MetaData) is null && Matches(GetSource(clip.FilePath, clip.MetaData), projectRoot))
            {
                clip.FilePath = GetSource(clip.FilePath, clip.MetaData)?.StartsWith('$') == true ? GetSource(clip.FilePath, clip.MetaData) : replacement.Path;
                if (clip.MetaData?.ContainsKey("FilePath") == true) clip.MetaData["FilePath"] = clip.FilePath!;
                ResetSourceState(clip.MetaData);
            }
        foreach (var track in draft.SoundTracks ?? [])
            if (ProjectReferencePart.Read(track.MetaData) is null && Matches(GetSource(track.FilePath, track.MetaData), projectRoot))
            {
                track.FilePath = GetSource(track.FilePath, track.MetaData)?.StartsWith('$') == true ? GetSource(track.FilePath, track.MetaData) : replacement.Path;
                if (track.MetaData?.ContainsKey("FilePath") == true) track.MetaData["FilePath"] = track.FilePath!;
                ResetSourceState(track.MetaData);
            }
    }

    public static void ResetSourceState(Dictionary<string, object>? data)
    {
        if (data is null) return;
        if (!ClipInitializationFailure.HasDeferredFailures(data)
            && ClipInitializationFailure.GetStage(data) is "SourceNotFound" or "SourceReading" or "SourceNotMatch" or "Source or ResolveEffect")
            ClipInitializationFailure.Clear(data);
        data.Remove(SoundTrackMetadata.ProbeSourceKey);
        data.Remove(SoundTrackMetadata.ProbeHasStreamKey);
    }
}
