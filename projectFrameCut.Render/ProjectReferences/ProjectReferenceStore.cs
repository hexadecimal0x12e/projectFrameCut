using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using projectFrameCut.Render.RenderAPIBase.Project;

namespace projectFrameCut.Render.ProjectReferences;

public static class ProjectReferenceStore
{
    public static JsonSerializerOptions JsonOptions { get; } = new()
    {
        WriteIndented = true,
        NumberHandling = JsonNumberHandling.AllowNamedFloatingPointLiterals,
    };

    public static string Root(string path) => Path.TrimEndingDirectorySeparator(Path.GetFullPath(
        File.Exists(path) ? Path.GetDirectoryName(path)! : path));

    public static string Resolve(string root, string path) => Path.GetFullPath(Path.Combine(root, path));

    public static (ProjectJSONStructure Project, DraftStructureJSON Draft) Load(string root)
    {
        var project = JsonSerializer.Deserialize<ProjectJSONStructure>(File.ReadAllText(Path.Combine(root, "project.pjfc")), JsonOptions)
            ?? throw new InvalidDataException("Invalid referenced project.");
        var draft = JsonSerializer.Deserialize<DraftStructureJSON>(File.ReadAllText(Path.Combine(root, "timeline.json")), JsonOptions)
            ?? throw new InvalidDataException("Invalid referenced timeline.");
        draft.ProjectSettings?.ApplyTo(project);
        if (project.TargetFrameRate == 0 || project.RelativeWidth <= 0 || project.RelativeHeight <= 0)
            throw new InvalidDataException("Referenced project has invalid frame rate or canvas size.");
        return (project, draft);
    }

    public static ProjectReference Capture(string source, string parentRoot, Guid parentId, ProjectReferenceMode mode)
    {
        source = Root(source);
        parentRoot = Root(parentRoot);
        var chain = new HashSet<string>(OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal) { parentRoot };
        var ids = new HashSet<Guid>();
        if (parentId != Guid.Empty) ids.Add(parentId);
        var destination = Path.Combine(parentRoot, "references", Guid.NewGuid().ToString("N"));
        var result = CaptureCore(source, destination, chain, ids);
        result.Mode = mode;
        result.SourcePath = source;
        result.SnapshotPath = Path.GetRelativePath(parentRoot, destination);
        Log($"Captured project reference {result.ProjectId}, mode={mode}, snapshot={result.SnapshotId}.");
        return result;
    }

    private static ProjectReference CaptureCore(string source, string destination, HashSet<string> chain, HashSet<Guid> ids)
    {
        var (project, draft) = Load(source);
        if (!chain.Add(source)) throw new InvalidDataException("Circular project reference.");
        if (project.ProjectUniqueId != Guid.Empty && !ids.Add(project.ProjectUniqueId))
        {
            chain.Remove(source);
            throw new InvalidDataException("Circular project reference.");
        }
        try
        {
            Directory.CreateDirectory(destination);
            var assets = File.Exists(Path.Combine(source, "assets.json"))
                ? JsonNode.Parse(File.ReadAllText(Path.Combine(source, "assets.json"))) as JsonArray : null;
            var assetPaths = assets?.OfType<JsonObject>().Where(a => a["AssetId"] is not null && a["Path"] is not null)
                .ToDictionary(a => a["AssetId"]!.GetValue<string>(), a => a["Path"]!.GetValue<string>()) ?? new();
            var copied = new Dictionary<string, string>(OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal);
            string CopyPath(string value)
            {
                if (value.StartsWith('$'))
                    value = assetPaths.TryGetValue(value[1..], out var path) ? path : value;
                if (string.IsNullOrWhiteSpace(value) || value.StartsWith('#') || value.Contains("://")) return value;
                string full;
                try { full = Resolve(source, value); }
                catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException) { return value; }
                if (!File.Exists(full)) return value;
                if (copied.TryGetValue(full, out var existing)) return existing;
                var relative = Path.Combine("asset", Guid.NewGuid().ToString("N") + Path.GetExtension(full));
                Directory.CreateDirectory(Path.Combine(destination, "asset"));
                File.Copy(full, Path.Combine(destination, relative));
                copied.Add(full, relative);
                return relative;
            }

            foreach (var clip in draft.Clips)
            {
                if (ProjectReference.Read(clip.MetaData) is not { } reference) continue;
                var nestedRoot = reference.Mode == ProjectReferenceMode.Linked
                    ? Resolve(source, reference.SourcePath) : Resolve(source, reference.SnapshotPath);
                var nestedDir = Path.Combine(destination, "references", Guid.NewGuid().ToString("N"));
                var nested = CaptureCore(Root(nestedRoot), nestedDir, chain, ids);
                nested.SnapshotPath = Path.GetRelativePath(destination, nestedDir);
                clip.MetaData![ProjectReference.MetadataKey] = nested;
            }
            // Detached clips can still depend on a captured evaluation context.
            foreach (var metadata in draft.Clips.Select(c => c.MetaData).Concat(draft.SoundTracks.Select(t => t.MetaData)))
            {
                if (ProjectReferencePart.Read(metadata) is not { } part) continue;
                var nestedDir = Path.Combine(destination, "references", Guid.NewGuid().ToString("N"));
                part.Reference = CaptureCore(Resolve(source, part.Reference.SnapshotPath), nestedDir, chain, ids);
                part.Reference.SnapshotPath = Path.GetRelativePath(destination, nestedDir);
                metadata![ProjectReferencePart.MetadataKey] = part;
            }
            var draftNode = JsonSerializer.SerializeToNode(draft, JsonOptions)!;
            RewritePaths(draftNode, CopyPath);
            var projectNode = JsonSerializer.SerializeToNode(project, JsonOptions)!;
            RewritePaths(projectNode, CopyPath);
            File.WriteAllText(Path.Combine(destination, "timeline.json"), draftNode.ToJsonString(JsonOptions));
            File.WriteAllText(Path.Combine(destination, "project.pjfc"), projectNode.ToJsonString(JsonOptions));
            // Paths in the timeline are resolved directly; asset IDs never leak into the parent catalog.
            File.WriteAllText(Path.Combine(destination, "assets.json"), "[]");
            return new() { ProjectId = project.ProjectUniqueId, SnapshotId = draft.SnapshotID };
        }
        finally
        {
            chain.Remove(source);
            ids.Remove(project.ProjectUniqueId);
        }
    }

    public static void RewritePaths(JsonNode node, Func<string, string> rewrite, Func<string, string>? rewriteKey = null,
        bool preserveDetachedScopes = false)
    {
        if (node is JsonObject obj)
        {
            foreach (var (key, value) in obj.ToArray())
            {
                if (key is ProjectReference.MetadataKey or ProjectReferencePart.MetadataKey) continue;
                if (preserveDetachedScopes && obj.ContainsKey(ProjectReferencePart.MetadataKey)
                    && key is not ("Id" or "BindedSoundTrack" or "SourceClipId")) continue;
                string targetKey = rewriteKey?.Invoke(key) ?? key;
                if (targetKey != key) { obj.Remove(key); obj[targetKey] = value; }
                if (value is JsonValue v && v.TryGetValue<string>(out var text)) obj[targetKey] = rewrite(text);
                else if (value is not null) RewritePaths(value, rewrite, rewriteKey, preserveDetachedScopes);
            }
        }
        else if (node is JsonArray array)
            for (int i = 0; i < array.Count; i++)
                if (array[i] is JsonValue v && v.TryGetValue<string>(out var text)) array[i] = rewrite(text);
                else if (array[i] is { } child) RewritePaths(child, rewrite, rewriteKey, preserveDetachedScopes);
    }

    public static void RefreshLinked(DraftStructureJSON draft, string root, Guid projectId)
    {
        foreach (var clip in draft.Clips.Where(c => c.ClipType == ClipMode.ProjectReferenceClip))
        {
            if (ProjectReference.Read(clip.MetaData) is not { Mode: ProjectReferenceMode.Linked } reference) continue;
            try
            {
                var refreshed = Capture(Resolve(root, reference.SourcePath), root, projectId, reference.Mode);
                clip.MetaData![ProjectReference.MetadataKey] = refreshed;
                clip.FilePath = refreshed.SnapshotPath;
                clip.MetaData.Remove("ProjectReferenceError");
            }
            catch (Exception ex)
            {
                clip.MetaData!["ProjectReferenceError"] = ex.Message;
                Log(ex, $"Refresh project reference {clip.Id}", nameof(ProjectReferenceStore));
            }
        }
    }
}
