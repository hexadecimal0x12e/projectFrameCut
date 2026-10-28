using projectFrameCut.Shared;
using System.Text.Json;

namespace projectFrameCut.Render.RenderAPIBase.Project;

internal sealed class CurrentSnapshotRecord
{
    public long Sequence { get; set; }
    public DateTime UpdatedAtUtc { get; set; }
    public bool IsDirty { get; set; }
    public Guid SnapshotID { get; set; }
    public Guid PreviousSnapshot { get; set; }
    public string SlotName { get; set; } = string.Empty;
    public DateTime SavedAt { get; set; }
    public string ChangeReason { get; set; } = string.Empty;
    public string DetailedChangeReason { get; set; } = string.Empty;
    public Guid ChangedByUser { get; set; }
    public string ChangedByUserDisplayName { get; set; } = string.Empty;
    public ClipChangeOperatorKind Operator { get; set; }
    public string OperatorDetailName { get; set; } = string.Empty;

    public void CopyTo(DraftStructureJSON draft)
    {
        draft.SnapshotID = SnapshotID;
        draft.PreviousSnapshot = PreviousSnapshot;
        draft.SavedAt = SavedAt;
        draft.ChangeReason = ChangeReason;
        draft.DetailedChangeReason = DetailedChangeReason;
        draft.ChangedByUser = ChangedByUser;
        draft.ChangedByUserDisplayName = ChangedByUserDisplayName;
        draft.Operator = Operator;
        draft.OperatorDetailName = OperatorDetailName;
    }
}

internal static class ProjectSnapshotStore
{
    internal const int CurrentSnapshotSlotCount = 3;

    internal static List<CurrentSnapshotRecord> ReadCurrentRecords(string projectPath, JsonSerializerOptions options, out bool hasInvalidRecords)
    {
        hasInvalidRecords = false;
        List<CurrentSnapshotRecord> records = [];
        for (int i = 0; i < CurrentSnapshotSlotCount; i++)
        {
            var path = Path.Combine(projectPath, "saveSlots", $"current-{i}.json");
            if (!File.Exists(path)) continue;
            try
            {
                var record = JsonSerializer.Deserialize<CurrentSnapshotRecord>(File.ReadAllText(path), options);
                if (record is null || record.Sequence <= 0 || record.SnapshotID == Guid.Empty
                    || !record.SlotName.StartsWith("slot_", StringComparison.Ordinal)
                    || Path.GetFileName(record.SlotName) != record.SlotName)
                    throw new InvalidDataException($"Invalid current snapshot record: {path}");
                records.Add(record);
            }
            catch (Exception ex)
            {
                hasInvalidRecords = true;
                Logger.Log(ex, "read current snapshot record", nameof(ProjectSnapshotStore));
            }
        }
        return records.OrderByDescending(r => r.Sequence).ThenByDescending(r => r.UpdatedAtUtc).ToList();
    }

    internal static (DraftStructureJSON Timeline, List<AssetItem> Assets) ReadForRender(
        string projectPath, ProjectJSONStructure project, JsonSerializerOptions options, string source = "timeline")
    {
        if (string.Equals(source, "timeline", StringComparison.OrdinalIgnoreCase))
            return ReadTimeline(projectPath, options);

        if (source.StartsWith("snapshot:", StringComparison.OrdinalIgnoreCase))
        {
            if (!Guid.TryParse(source["snapshot:".Length..], out var snapshotId) || snapshotId == Guid.Empty)
                throw new ArgumentException("timelineSource requires a valid snapshot ID after 'snapshot:'.");
            return ReadSnapshot(projectPath, snapshotId, options);
        }

        if (!string.Equals(source, "latest", StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException("timelineSource must be 'timeline', 'latest', or 'snapshot:<id>'.");

        var records = ReadCurrentRecords(projectPath, options, out bool invalidRecords);
        if (invalidRecords)
            throw new InvalidDataException("Current project snapshot records are invalid. Open the project to recover it before rendering the latest state.");
        var current = records.FirstOrDefault();
        if (current is null)
        {
            var root = Path.Combine(projectPath, "saveSlots");
            if (!project.NormallyExited && Directory.Exists(root) && Directory.EnumerateDirectories(root, "slot_*").Any())
                throw new InvalidDataException("The current project snapshot is unknown. Open the project to recover it before rendering the latest state.");
            return ReadTimeline(projectPath, options);
        }
        var timelinePath = Path.Combine(projectPath, "timeline.json");
        var assetsPath = Path.Combine(projectPath, "assets.json");
        if (project.NormallyExited && !current.IsDirty
            || current.UpdatedAtUtc != default && File.Exists(timelinePath) && File.Exists(assetsPath)
                && File.GetLastWriteTimeUtc(timelinePath) > current.UpdatedAtUtc.ToUniversalTime()
                && File.GetLastWriteTimeUtc(assetsPath) > current.UpdatedAtUtc.ToUniversalTime())
            return ReadTimeline(projectPath, options);
        var path = Path.Combine(projectPath, "saveSlots", current.SlotName);
        var result = Read(path, current.SnapshotID, options);
        Logger.Log($"[Render] Using current project snapshot {current.SnapshotID} from '{path}'.");
        return result;
    }

    private static (DraftStructureJSON Timeline, List<AssetItem> Assets) ReadTimeline(string projectPath, JsonSerializerOptions options)
    {
        var result = Read(projectPath, Guid.Empty, options);
        Logger.Log($"[Render] Using saved timeline from '{projectPath}'.");
        return result;
    }

    private static (DraftStructureJSON Timeline, List<AssetItem> Assets) ReadSnapshot(
        string projectPath, Guid snapshotId, JsonSerializerOptions options)
    {
        var root = Path.Combine(projectPath, "saveSlots");
        var path = Path.Combine(root, $"slot_{snapshotId}");
        if (!Directory.Exists(path) && Directory.Exists(root))
        {
            foreach (var candidate in Directory.GetDirectories(root, "slot_*"))
            {
                var timelinePath = Path.Combine(candidate, "timeline.json");
                if (!File.Exists(timelinePath)) continue;
                DraftStructureJSON? draft;
                try { draft = JsonSerializer.Deserialize<DraftStructureJSON>(File.ReadAllText(timelinePath), options); }
                catch (JsonException) { continue; }
                if (draft?.SnapshotID != snapshotId) continue;
                path = candidate;
                break;
            }
        }
        if (!Directory.Exists(path)) throw new DirectoryNotFoundException($"Project snapshot '{snapshotId}' was not found.");
        var result = Read(path, snapshotId, options);
        Logger.Log($"[Render] Using project snapshot {snapshotId} from '{path}'.");
        return result;
    }

    private static (DraftStructureJSON Timeline, List<AssetItem> Assets) Read(string path, Guid snapshotId, JsonSerializerOptions options)
    {
        var timeline = JsonSerializer.Deserialize<DraftStructureJSON>(File.ReadAllText(Path.Combine(path, "timeline.json")), options)
            ?? throw new InvalidDataException("Project timeline is invalid.");
        if (timeline.Clips is null || timeline.SoundTracks is null || snapshotId != Guid.Empty && timeline.SnapshotID != snapshotId)
            throw new InvalidDataException("Project snapshot timeline is invalid.");
        var assetPath = Path.Combine(path, "assets.json");
        var assets = File.Exists(assetPath)
            ? JsonSerializer.Deserialize<List<AssetItem>>(File.ReadAllText(assetPath), options) : null;
        if (assets is null && snapshotId != Guid.Empty) throw new InvalidDataException("Project assets are invalid.");
        return (timeline, assets ?? []);
    }
}
