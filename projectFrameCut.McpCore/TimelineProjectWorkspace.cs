using projectFrameCut.Render.RenderAPIBase.Project;
using projectFrameCut.Shared;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace projectFrameCut.McpCore;

public sealed class TimelineProjectWorkspace
{
    public static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        NumberHandling = JsonNumberHandling.AllowNamedFloatingPointLiterals,
    };

    public string ProjectRoot { get; }
    public ProjectJSONStructure ProjectInfo { get; private set; }
    public DraftStructureJSON Draft { get; private set; }
    public List<AssetItem> Assets { get; private set; }

    private TimelineProjectWorkspace(string projectRoot, ProjectJSONStructure projectInfo, DraftStructureJSON draft, List<AssetItem> assets)
    {
        ProjectRoot = projectRoot;
        ProjectInfo = projectInfo;
        Draft = draft;
        Assets = assets;
    }

    public static TimelineProjectWorkspace Load(string projectRoot)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(projectRoot);
        string root = Path.GetFullPath(projectRoot);

        string projectFile = File.Exists(Path.Combine(root, "project.pjfc"))
            ? Path.Combine(root, "project.pjfc")
            : Path.Combine(root, "project.json");
        string timelineFile = Path.Combine(root, "timeline.json");
        string assetsFile = Path.Combine(root, "assets.json");

        if (!File.Exists(projectFile))
        {
            throw new FileNotFoundException("Project file not found.", projectFile);
        }

        ProjectJSONStructure project = JsonSerializer.Deserialize<ProjectJSONStructure>(File.ReadAllText(projectFile), JsonOptions)
            ?? throw new InvalidOperationException("Failed to load project info.");

        DraftStructureJSON? draft = null;
        if (File.Exists(timelineFile))
        {
            draft = JsonSerializer.Deserialize<DraftStructureJSON>(File.ReadAllText(timelineFile), JsonOptions);
        }

        if (draft is null)
        {
            draft = LoadLatestDraftFromSlots(root);
        }

        if (draft is null)
        {
            throw new FileNotFoundException("No timeline.json or save slot could be loaded.", timelineFile);
        }

        List<AssetItem> assets = [];
        if (File.Exists(assetsFile))
        {
            assets = JsonSerializer.Deserialize<List<AssetItem>>(File.ReadAllText(assetsFile), JsonOptions) ?? [];
        }
        else
        {
            var slotAssets = LoadAssetsFromLatestSlot(root, draft.SnapshotID);
            if (slotAssets is not null)
            {
                assets = slotAssets;
            }
        }

        return new TimelineProjectWorkspace(root, project, draft, assets);
    }

    public void Save(string? changeReason = null)
    {
        Directory.CreateDirectory(ProjectRoot);

        if (Draft.SnapshotID == Guid.Empty)
        {
            Draft.SnapshotID = Guid.NewGuid();
        }

        Draft.SavedAt = DateTime.Now;
        if (!string.IsNullOrWhiteSpace(changeReason))
        {
            Draft.ChangeReason = changeReason.Trim();
        }

        ProjectInfo.LastChanged = DateTime.Now;
        ProjectInfo.LastSnapshotID = Draft.SnapshotID;

        File.WriteAllText(Path.Combine(ProjectRoot, "timeline.json"), JsonSerializer.Serialize(Draft, JsonOptions));
        File.WriteAllText(Path.Combine(ProjectRoot, "project.pjfc"), JsonSerializer.Serialize(ProjectInfo, JsonOptions));
        File.WriteAllText(Path.Combine(ProjectRoot, "assets.json"), JsonSerializer.Serialize(Assets, JsonOptions));

        string slotDir = Path.Combine(ProjectRoot, "saveSlots", $"slot_{Draft.SnapshotID}");
        Directory.CreateDirectory(slotDir);
        File.WriteAllText(Path.Combine(slotDir, "timeline.json"), JsonSerializer.Serialize(Draft, JsonOptions));
        File.WriteAllText(Path.Combine(slotDir, "assets.json"), JsonSerializer.Serialize(Assets, JsonOptions));
    }

    public void ReplaceDraft(DraftStructureJSON draft)
    {
        Draft = draft ?? throw new ArgumentNullException(nameof(draft));
    }

    public void ReplaceProjectInfo(ProjectJSONStructure projectInfo)
        => ProjectInfo = projectInfo ?? throw new ArgumentNullException(nameof(projectInfo));

    public void ReplaceAssets(List<AssetItem> assets)
        => Assets = assets ?? throw new ArgumentNullException(nameof(assets));

    private static DraftStructureJSON? LoadLatestDraftFromSlots(string root)
    {
        string saveRoot = Path.Combine(root, "saveSlots");
        if (!Directory.Exists(saveRoot))
        {
            return null;
        }

        DraftStructureJSON? latest = null;
        DateTime latestTime = DateTime.MinValue;

        foreach (string dir in Directory.GetDirectories(saveRoot, "slot_*"))
        {
            string timelinePath = Path.Combine(dir, "timeline.json");
            if (!File.Exists(timelinePath))
            {
                continue;
            }

            try
            {
                DraftStructureJSON? draft = JsonSerializer.Deserialize<DraftStructureJSON>(File.ReadAllText(timelinePath), JsonOptions);
                if (draft is null)
                {
                    continue;
                }

                DateTime savedAt = draft.SavedAt == default ? File.GetLastWriteTime(timelinePath) : draft.SavedAt;
                if (savedAt > latestTime)
                {
                    latestTime = savedAt;
                    latest = draft;
                }
            }
            catch
            {
            }
        }

        return latest;
    }

    private static List<AssetItem>? LoadAssetsFromLatestSlot(string root, Guid snapshotId)
    {
        string saveRoot = Path.Combine(root, "saveSlots");
        if (!Directory.Exists(saveRoot))
        {
            return null;
        }

        string direct = Path.Combine(saveRoot, $"slot_{snapshotId}", "assets.json");
        if (File.Exists(direct))
        {
            return JsonSerializer.Deserialize<List<AssetItem>>(File.ReadAllText(direct), JsonOptions);
        }

        foreach (string dir in Directory.GetDirectories(saveRoot, "slot_*"))
        {
            string assetsPath = Path.Combine(dir, "assets.json");
            if (!File.Exists(assetsPath))
            {
                continue;
            }

            try
            {
                return JsonSerializer.Deserialize<List<AssetItem>>(File.ReadAllText(assetsPath), JsonOptions);
            }
            catch
            {
            }
        }

        return null;
    }
}
