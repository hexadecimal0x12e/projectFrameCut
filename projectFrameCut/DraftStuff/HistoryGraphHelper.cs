using LocalizedResources;
using projectFrameCut.Render.RenderAPIBase.Project;
using System.Text.Json.Serialization;
using Color = Microsoft.Maui.Graphics.Color;

namespace projectFrameCut.DraftStuff;

#region history graph

public sealed class HistoryGraphNode
{
    public Guid SnapshotID { get; init; }
    public Guid PreviousSnapshotID { get; set; }
    public List<Guid> NextSnapshotIDs { get; set; } = new();
    public DateTime SavedAt { get; init; }
    public string ChangeReason { get; init; } = string.Empty;
    public string ChangedByUserDisplayName { get; init; } = string.Empty;
    public Guid ChangedByUser { get; init; }
    public bool IsCurrentSnapshot { get; set; }
    public bool IsHead { get; set; }
    public bool IsBranchNode { get; set; }

    [JsonIgnore]
    public Guid NextSnapshotID => NextSnapshotIDs?.FirstOrDefault() ?? Guid.Empty;

    public string DisplayLabel => IsCurrentSnapshot ? $"* {ChangeReason}" : ChangeReason;

    public string RelativeTimeDisplay
    {
        get => DateTime.Now.Ticks - SavedAt.Ticks >= 0 ?
               TimeSpan.FromTicks(DateTime.Now.Ticks - SavedAt.Ticks) switch
               {
                   var t when t.TotalMinutes < 1 => Localized.DraftSettingPage_Tab_History_Now,
                   var t when t.TotalHours < 2 => Localized.DraftSettingPage_Tab_History_MinutesAgo(t.Minutes),
                   var t when t.TotalHours < 48 => Localized.DraftSettingPage_Tab_History_HoursAgo((int)t.TotalHours),
                   var t when t.TotalDays < 14 => Localized.DraftSettingPage_Tab_History_DaysAgo((int)t.TotalDays),
                   _ => Localized.DraftSettingPage_Tab_History_VeryLongAgo
               }
               : Localized.HomePage_LastChangedOnFuture;
    }
}

public sealed class HistoryGraphRowDrawable : IDrawable
{
    public bool HasPredecessor { get; set; }
    public bool HasSuccessor { get; set; }
    public bool IsCurrentSnapshot { get; set; }
    public bool IsSelected { get; set; }

    const float NodeRadius = 9f;
    const float SelectedNodeRadius = 12f;
    const float LineWidth = 2.5f;
    static readonly Color LineColor = Color.FromArgb("#555555");
    static readonly Color CurrentLineColor = Color.FromArgb("#4A9EFF");
    static readonly Color NodeFillColor = Color.FromArgb("#666666");
    static readonly Color CurrentNodeFillColor = Color.FromArgb("#4A9EFF");
    static readonly Color SelectedRingColor = Color.FromArgb("#FFB74D");
    static readonly Color CurrentNodeStrokeColor = Color.FromArgb("#1A6DD4");

    public void Draw(ICanvas canvas, RectF dirtyRect)
    {
        canvas.Antialias = true;
        float centerX = dirtyRect.Width / 2f;
        float centerY = dirtyRect.Height / 2f;
        float radius = IsSelected ? SelectedNodeRadius : NodeRadius;

        Color lineClr = IsCurrentSnapshot ? CurrentLineColor : LineColor;

        canvas.StrokeColor = lineClr;
        canvas.StrokeSize = LineWidth;
        canvas.StrokeLineCap = LineCap.Round;

        if (HasSuccessor)
        {
            canvas.DrawLine(centerX, centerY, centerX, dirtyRect.Bottom);
        }

        if (HasPredecessor)
        {
            canvas.DrawLine(centerX, dirtyRect.Top, centerX, centerY);
        }

        if (IsSelected)
        {
            canvas.StrokeColor = SelectedRingColor;
            canvas.StrokeSize = 3;
            canvas.DrawCircle(centerX, centerY, radius + 3);
        }

        Color fillClr = IsCurrentSnapshot ? CurrentNodeFillColor : NodeFillColor;
        canvas.FillColor = fillClr;
        canvas.FillCircle(centerX, centerY, radius);

        if (IsCurrentSnapshot)
        {
            canvas.StrokeColor = CurrentNodeStrokeColor;
            canvas.StrokeSize = 1.5f;
            canvas.DrawCircle(centerX, centerY, radius);
        }
    }
}

public sealed class SaveSlotHistoryItem
{
    public Guid SnapshotID { get; init; }
    public DateTime SavedAt { get; init; }
    public string ChangeReason { get; init; } = string.Empty;
    public string ChangedBy { get; internal set; } = string.Empty;
    public Guid ChangedByUserID { get; internal set; }
}

public sealed class HistoryGraphEdge
{
    public Guid FromSnapshotID { get; init; }
    public Guid ToSnapshotID { get; init; }
    public bool IsCurrentPath { get; set; }
}

#endregion

#region interface 
/// <summary>
/// Provides history graph data and handles snapshot operations for <see cref="HistoryGraphView"/>.
/// Implement this interface to integrate any editor/page with the visual history browser.
/// </summary>
public interface IHistoryGraphProvider
{
    /// <summary>Display name shown in the info label.</summary>
    string ProviderName { get; }

    /// <summary>
    /// Current active snapshot ID. Used to highlight the current node and style
    /// the current-path connections / list rows.
    /// </summary>
    Guid CurrentSnapshotID { get; }

    /// <summary>
    /// Build the current set of graph nodes and edges for display.
    /// Called when the view loads or reloads history data.
    /// </summary>
    (List<HistoryGraphNode> Nodes, List<HistoryGraphEdge> Edges) BuildGraphData();

    /// <summary>
    /// Apply / restore the given snapshot. The provider is responsible for all
    /// UI state management (busy indicators, error handling, status messages).
    /// Returns true if the snapshot was applied successfully.
    /// </summary>
    Task<bool> ApplySnapshotAsync(Guid snapshotId);

    /// <summary>
    /// Raised when the provider's current snapshot changes externally
    /// (e.g. via undo/redo in the page). The view listens to this event
    /// to refresh selection highlighting and connection styling.
    /// </summary>
    event EventHandler<EventArgs>? CurrentSnapshotChanged;

    /// <summary>
    /// Called by the host page (e.g. DraftPage) to notify the provider
    /// that an external snapshot change has occurred, so the provider
    /// can raise <see cref="CurrentSnapshotChanged"/> for any subscribers.
    /// </summary>
    void NotifyExternalSnapshotChanged() { }

    /// <summary>
    /// Optional: provides additional content to append below the default details panel.
    /// Return null for no additional content.
    /// </summary>
    View? GetDetailsPanelExtension(HistoryGraphNode node) => null;

    /// <summary>
    /// Called when the user selects a node in the graph or list view.
    /// </summary>
    void OnNodeSelected(HistoryGraphNode node) { }

    /// <summary>
    /// Called when the user deselects a node in the graph or list view.
    /// </summary>
    void OnNodeDeselected(HistoryGraphNode node) { }
}
#endregion

#region data builder
/// <summary>
/// Static utility for building <see cref="HistoryGraphNode"/> and <see cref="HistoryGraphEdge"/>
/// collections from various data sources.
/// </summary>
public static class HistoryGraphDataBuilder
{
    private const string SaveSlotDirectoryName = "saveSlots";

    /// <summary>
    /// Build graph data from a DraftPage's snapshot mapping and on-disk save slots.
    /// </summary>
    public static (List<HistoryGraphNode> Nodes, List<HistoryGraphEdge> Edges) BuildFromDraftPage(
        DraftPage page)
    {
        var mapping = page.ProjectInfo.SnapshotIDMapping;
        if (mapping is null || mapping.Count == 0)
            return (new(), new());

        var historyItems = ReadSaveSlotHistoryFromPath(page.WorkingPath);
        return BuildFromSnapshotMapping(mapping, page.CurrentSnapshotID, historyItems);
    }

    /// <summary>
    /// Build graph data from a standalone project path.
    /// </summary>
    public static (List<HistoryGraphNode> Nodes, List<HistoryGraphEdge> Edges) BuildStandalone(
        string standaloneProjectPath)
    {
        var nodes = new List<HistoryGraphNode>();
        var edges = new List<HistoryGraphEdge>();

        if (string.IsNullOrWhiteSpace(standaloneProjectPath))
            return (nodes, edges);

        if (!TryLoadStandaloneProjectInfo(standaloneProjectPath, out var projectInfo, out _))
            return (nodes, edges);

        var mapping = projectInfo.SnapshotIDMapping;
        if (mapping is null || mapping.Count == 0)
            return (nodes, edges);

        string saveSlotsPath = System.IO.Path.Combine(standaloneProjectPath, SaveSlotDirectoryName);
        var slotMetaById = new Dictionary<Guid, (DateTime SavedAt, string Reason, string UserName, Guid UserId)>();
        if (System.IO.Directory.Exists(saveSlotsPath))
        {
            foreach (var slotDir in System.IO.Directory.GetDirectories(saveSlotsPath, "slot_*"))
            {
                string timelinePath = System.IO.Path.Combine(slotDir, "timeline.json");
                if (!System.IO.File.Exists(timelinePath)) continue;
                try
                {
                    string json = System.IO.File.ReadAllText(timelinePath);
                    var draft = System.Text.Json.JsonSerializer.Deserialize<DraftStructureJSON>(json, DraftPage.DraftJSONOption);
                    if (draft is null || draft.SnapshotID == Guid.Empty) continue;
                    slotMetaById[draft.SnapshotID] = (draft.SavedAt, draft.ChangeReason,
                        string.IsNullOrWhiteSpace(draft.ChangedByUserDisplayName) ? "Anonymous" : draft.ChangedByUserDisplayName,
                        draft.ChangedByUser);
                }
                catch { }
            }
        }

        Guid lastId = projectInfo.LastSnapshotID;

        foreach (var kv in mapping)
        {
            slotMetaById.TryGetValue(kv.Key, out var meta);
            nodes.Add(new HistoryGraphNode
            {
                SnapshotID = kv.Key,
                PreviousSnapshotID = kv.Value.Previous,
                NextSnapshotIDs = kv.Value.Next,
                SavedAt = meta.SavedAt,
                ChangeReason = meta.Reason,
                ChangedByUserDisplayName = meta.UserName,
                ChangedByUser = meta.UserId,
                IsCurrentSnapshot = kv.Key == lastId,
                IsHead = kv.Value.Next.Count == 0
            });
        }

        foreach (var node in nodes)
        {
            foreach (var nextId in node.NextSnapshotIDs)
            {
                bool isCurrentPath = IsSnapshotOnCurrentPath(node.SnapshotID, lastId, mapping)
                                   && IsSnapshotOnCurrentPath(nextId, lastId, mapping);
                edges.Add(new HistoryGraphEdge
                {
                    FromSnapshotID = node.SnapshotID,
                    ToSnapshotID = nextId,
                    IsCurrentPath = isCurrentPath
                });
            }
        }

        nodes = nodes.OrderByDescending(n => n.SavedAt == DateTime.MinValue ? DateTime.MaxValue.Ticks : n.SavedAt.Ticks)
                     .ThenByDescending(n => n.SnapshotID.ToString())
                     .ToList();

        return (nodes, edges);
    }

    /// <summary>
    /// Core builder from raw mapping + slot metadata. Used by both DraftPage and standalone paths.
    /// </summary>
    public static (List<HistoryGraphNode> Nodes, List<HistoryGraphEdge> Edges) BuildFromSnapshotMapping(
        Dictionary<Guid, ProjectJSONStructure.SnapshotIDMappingStructure> mapping,
        Guid currentSnapshotId,
        List<SaveSlotHistoryItem> slotHistory)
    {
        var nodes = new List<HistoryGraphNode>();
        var edges = new List<HistoryGraphEdge>();

        if (mapping is null || mapping.Count == 0)
            return (nodes, edges);

        var historyById = slotHistory?.ToDictionary(h => h.SnapshotID) ?? new();

        foreach (var kv in mapping)
        {
            historyById.TryGetValue(kv.Key, out var item);
            nodes.Add(new HistoryGraphNode
            {
                SnapshotID = kv.Key,
                PreviousSnapshotID = kv.Value.Previous,
                NextSnapshotIDs = kv.Value.Next,
                SavedAt = item?.SavedAt ?? DateTime.MinValue,
                ChangeReason = item?.ChangeReason ?? string.Empty,
                ChangedByUserDisplayName = item?.ChangedBy ?? "Anonymous",
                ChangedByUser = item?.ChangedByUserID ?? Guid.Empty,
                IsCurrentSnapshot = kv.Key == currentSnapshotId,
                IsHead = kv.Value.Next.Count == 0
            });
        }

        foreach (var node in nodes)
        {
            foreach (var nextId in node.NextSnapshotIDs)
            {
                bool isCurrentPath = IsSnapshotOnCurrentPath(node.SnapshotID, currentSnapshotId, mapping)
                                   && IsSnapshotOnCurrentPath(nextId, currentSnapshotId, mapping);
                edges.Add(new HistoryGraphEdge
                {
                    FromSnapshotID = node.SnapshotID,
                    ToSnapshotID = nextId,
                    IsCurrentPath = isCurrentPath
                });
            }
        }

        nodes = nodes.OrderByDescending(n => n.SavedAt == DateTime.MinValue ? DateTime.MaxValue.Ticks : n.SavedAt.Ticks)
                     .ThenByDescending(n => n.SnapshotID.ToString())
                     .ToList();

        return (nodes, edges);
    }

    /// <summary>
    /// Trace back from currentId through previous links to check whether snapshotId is on the current path.
    /// </summary>
    public static bool IsSnapshotOnCurrentPath(Guid snapshotId, Guid currentId,
        Dictionary<Guid, ProjectJSONStructure.SnapshotIDMappingStructure> mapping)
    {
        if (snapshotId == currentId) return true;
        var visited = new HashSet<Guid>();
        var cursor = currentId;
        while (cursor != Guid.Empty && visited.Add(cursor))
        {
            if (cursor == snapshotId) return true;
            if (!mapping.TryGetValue(cursor, out var entry)) break;
            cursor = entry.Previous;
        }
        return false;
    }

    private static List<SaveSlotHistoryItem> ReadSaveSlotHistoryFromPath(string? workingPath)
    {
        if (string.IsNullOrWhiteSpace(workingPath))
            return [];

        string saveSlotsPath = System.IO.Path.Combine(workingPath, SaveSlotDirectoryName);
        if (!System.IO.Directory.Exists(saveSlotsPath))
            return [];

        var result = new List<SaveSlotHistoryItem>();
        foreach (var slotPath in System.IO.Directory.GetDirectories(saveSlotsPath, "slot_*"))
        {
            string timelinePath = System.IO.Path.Combine(slotPath, "timeline.json");
            if (!System.IO.File.Exists(timelinePath))
                continue;

            try
            {
                string json = System.IO.File.ReadAllText(timelinePath);
                var draft = System.Text.Json.JsonSerializer.Deserialize<DraftStructureJSON>(json, DraftPage.DraftJSONOption);
                if (draft is null || draft.SnapshotID == Guid.Empty)
                    continue;

                result.Add(new SaveSlotHistoryItem
                {
                    SnapshotID = draft.SnapshotID,
                    SavedAt = draft.SavedAt,
                    ChangeReason = draft.ChangeReason,
                    ChangedBy = string.IsNullOrWhiteSpace(draft.ChangedByUserDisplayName) ? "Anonymous" : draft.ChangedByUserDisplayName,
                    ChangedByUserID = draft.ChangedByUser
                });
            }
            catch
            {
                // Ignore broken slot files and continue loading other records.
            }
        }

        return result
            .OrderByDescending(i => i.SavedAt)
            .ThenByDescending(i => i.SnapshotID)
            .ToList();
    }

    private static bool TryLoadStandaloneProjectInfo(string projectPath, out ProjectJSONStructure projectInfo, out string error)
    {
        projectInfo = null!;
        error = "";

        string projectFile = System.IO.Path.Combine(projectPath, "project.pjfc");
        if (!System.IO.File.Exists(projectFile))
        {
            error = "Project file not found.";
            return false;
        }

        try
        {
            string json = System.IO.File.ReadAllText(projectFile);
            projectInfo = System.Text.Json.JsonSerializer.Deserialize<ProjectJSONStructure>(json, DraftPage.DraftJSONOption) ?? null!;
            if (projectInfo is null)
            {
                error = "Failed to deserialize project info.";
                return false;
            }
            return true;
        }
        catch (Exception ex)
        {
            error = ex.Message;
            return false;
        }
    }
}

#endregion

#region data provider
/// <summary>
/// <see cref="IHistoryGraphProvider"/> implementation that wraps a <see cref="DraftPage"/>.
/// </summary>
public sealed class DraftHistoryGraphProvider : IHistoryGraphProvider
{
    private readonly DraftPage _page;

    public string ProviderName => "DraftPage";

    public DraftHistoryGraphProvider(DraftPage page)
    {
        _page = page ?? throw new ArgumentNullException(nameof(page));
    }

    public Guid CurrentSnapshotID => _page.CurrentSnapshotID;

    public event EventHandler<EventArgs>? CurrentSnapshotChanged;

    public (List<HistoryGraphNode> Nodes, List<HistoryGraphEdge> Edges) BuildGraphData()
    {
        return HistoryGraphDataBuilder.BuildFromDraftPage(_page);
    }

    public void NotifyExternalSnapshotChanged()
    {
        CurrentSnapshotChanged?.Invoke(this, EventArgs.Empty);
    }

    public async Task<bool> ApplySnapshotAsync(Guid snapshotId)
    {
        try
        {
            _page.SetStateBusy();
            _page.ApplySlot(snapshotId);
            return true;
        }
        catch (Exception ex)
        {
            _page.SetStateFail();
            _page.SetStatusText($"Restore failed: {ex.Message}");
            return false;
        }
    }
}

#endregion
