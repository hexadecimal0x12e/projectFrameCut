using projectFrameCut.Render.RenderAPIBase.Project;
using System.Text.Json;

namespace projectFrameCut.ApplicationAPIBase.Workspace.Modules;

public sealed class HistoryChangedEventArgs(Guid currentSnapshotId, bool canUndo, bool canRedo) : EventArgs
{
    public Guid CurrentSnapshotId { get; } = currentSnapshotId;
    public bool CanUndo { get; } = canUndo;
    public bool CanRedo { get; } = canRedo;
}

public sealed class HistoryModule(int maximumSnapshots = 100) : WorkspaceModuleBase, IWorkspaceModuleDependencies
{
    private readonly List<DraftStructureJSON> _snapshots = [];
    private int _index = -1;
    public const string ModuleId = "history.core";
    public override string Id => ModuleId;
    public IReadOnlyCollection<Type> Dependencies { get; } = [typeof(ProjectModule), typeof(TimelineModule)];
    public bool CanUndo => _index > 0;
    public bool CanRedo => _index >= 0 && _index < _snapshots.Count - 1;
    public Guid CurrentSnapshotId => _index >= 0 ? _snapshots[_index].SnapshotID : Guid.Empty;
    public event EventHandler<HistoryChangedEventArgs>? Changed;

    public DraftStructureJSON Capture(string? reason = null)
    {
        var timeline = Context?.Modules.Get<TimelineModule>() ?? throw new InvalidOperationException("The module is not initialized.");
        if (CanRedo) _snapshots.RemoveRange(_index + 1, _snapshots.Count - _index - 1);
        var snapshot = timeline.Export(Guid.CreateVersion7(), CurrentSnapshotId, reason);
        _snapshots.Add(snapshot); _index = _snapshots.Count - 1;
        while (_snapshots.Count > Math.Max(1, maximumSnapshots)) { _snapshots.RemoveAt(0); _index--; }
        RaiseChanged(); return snapshot;
    }
    public DraftStructureJSON Undo() { if (!CanUndo) throw new InvalidOperationException("No earlier snapshot is available."); return Apply(--_index); }
    public DraftStructureJSON Redo() { if (!CanRedo) throw new InvalidOperationException("No later snapshot is available."); return Apply(++_index); }
    private DraftStructureJSON Apply(int index)
    {
        var snapshot = _snapshots[index]; Context!.Modules.Get<TimelineModule>().Reset(snapshot.Clips); RaiseChanged(); return snapshot;
    }
    public Task SaveSnapshotAsync(DraftStructureJSON snapshot, string slotName, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(slotName);
        return Context?.Storage.WriteTextAsync(Path.Combine("saveSlots", slotName, "timeline.json"), JsonSerializer.Serialize(snapshot, new JsonSerializerOptions { WriteIndented = true }), cancellationToken)
            ?? throw new InvalidOperationException("The module is not initialized.");
    }
    private void RaiseChanged() => Changed?.Invoke(this, new HistoryChangedEventArgs(CurrentSnapshotId, CanUndo, CanRedo));
}
