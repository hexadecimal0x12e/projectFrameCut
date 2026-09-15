namespace projectFrameCut.ApplicationAPIBase.Workspace.Modules;

public sealed class WorkspaceSelectionChangedEventArgs(IReadOnlyCollection<Guid> selectedClipIds) : EventArgs
{
    public IReadOnlyCollection<Guid> SelectedClipIds { get; } = selectedClipIds;
}

public sealed class PropertyModule : WorkspaceModuleBase, IWorkspaceModuleDependencies
{
    private IReadOnlyCollection<Guid> _selectedClipIds = Array.Empty<Guid>();
    public const string ModuleId = "properties.core";
    public override string Id => ModuleId;
    public IReadOnlyCollection<Type> Dependencies { get; } = [typeof(TimelineModule)];
    public IReadOnlyCollection<Guid> SelectedClipIds => _selectedClipIds;
    public event EventHandler<WorkspaceSelectionChangedEventArgs>? SelectionChanged;
    public void SetSelection(IEnumerable<Guid> clipIds)
    {
        _selectedClipIds = clipIds.Distinct().ToArray();
        SelectionChanged?.Invoke(this, new WorkspaceSelectionChangedEventArgs(_selectedClipIds));
    }
}
