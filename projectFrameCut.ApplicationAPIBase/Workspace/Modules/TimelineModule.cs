using projectFrameCut.Render.RenderAPIBase.Project;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace projectFrameCut.ApplicationAPIBase.Workspace.Modules;

public enum TimelineChangeKind { Reset, Added, Removed, Moved, Resized, Split, Duplicated, PropertyChanged }

public sealed class TimelineChangedEventArgs(TimelineChangeKind kind, IReadOnlyCollection<Guid> clipIds, string? detail = null) : EventArgs
{
    public TimelineChangeKind Kind { get; } = kind;
    public IReadOnlyCollection<Guid> ClipIds { get; } = clipIds;
    public string? Detail { get; } = detail;
}

public sealed class TimelineModule(DraftStructureJSON? draft = null) : WorkspaceModuleBase, IWorkspaceModuleDependencies
{
    private static readonly JsonSerializerOptions Options = new() { WriteIndented = true, NumberHandling = JsonNumberHandling.AllowNamedFloatingPointLiterals };
    private readonly object _gate = new();
    private readonly List<ClipDraftDTO> _clips = draft?.Clips?.Select(Clone).ToList() ?? [];
    public const string ModuleId = "timeline.core";
    public override string Id => ModuleId;
    public IReadOnlyCollection<Type> Dependencies { get; } = [typeof(ProjectModule)];
    public IReadOnlyList<ClipDraftDTO> Clips { get { lock (_gate) return _clips.Select(Clone).ToList(); } }
    public event EventHandler<TimelineChangedEventArgs>? Changed;

    public void Reset(IEnumerable<ClipDraftDTO> clips)
    {
        ArgumentNullException.ThrowIfNull(clips);
        lock (_gate) { _clips.Clear(); _clips.AddRange(clips.Select(Clone)); }
        Notify(TimelineChangeKind.Reset, _clips.Select(x => x.Id));
    }

    public ClipDraftDTO Get(Guid id) { lock (_gate) return Clone(Find(id)); }
    public void Add(ClipDraftDTO clip)
    {
        ArgumentNullException.ThrowIfNull(clip);
        lock (_gate)
        {
            if (clip.Id == Guid.Empty) clip.Id = Guid.CreateVersion7();
            if (_clips.Any(x => x.Id == clip.Id)) throw new InvalidOperationException($"Clip '{clip.Id}' already exists.");
            _clips.Add(Clone(clip));
        }
        Notify(TimelineChangeKind.Added, [clip.Id]);
    }
    public bool Remove(Guid id)
    {
        bool removed;
        lock (_gate) removed = _clips.RemoveAll(x => x.Id == id) > 0;
        if (removed) Notify(TimelineChangeKind.Removed, [id]);
        return removed;
    }
    public void Move(Guid id, uint layerIndex, uint startFrame)
    {
        lock (_gate) { var clip = Find(id); clip.LayerIndex = layerIndex; clip.StartFrame = startFrame; }
        Notify(TimelineChangeKind.Moved, [id]);
    }
    public void Resize(Guid id, uint startFrame, uint duration)
    {
        if (duration == 0) throw new ArgumentOutOfRangeException(nameof(duration));
        lock (_gate) { var clip = Find(id); clip.StartFrame = startFrame; clip.Duration = duration; }
        Notify(TimelineChangeKind.Resized, [id]);
    }
    public ClipDraftDTO Duplicate(Guid id, uint? startFrame = null, uint? layerIndex = null)
    {
        ClipDraftDTO copy;
        lock (_gate)
        {
            copy = Clone(Find(id)); copy.Id = Guid.CreateVersion7(); copy.Name += " Copy";
            copy.StartFrame = startFrame ?? copy.StartFrame; copy.LayerIndex = layerIndex ?? copy.LayerIndex; _clips.Add(copy);
        }
        Notify(TimelineChangeKind.Duplicated, [copy.Id]);
        return Clone(copy);
    }
    public (ClipDraftDTO Left, ClipDraftDTO Right) Split(Guid id, uint splitFrame)
    {
        ClipDraftDTO left, right;
        lock (_gate)
        {
            var clip = Find(id);
            if (splitFrame <= clip.StartFrame || splitFrame >= clip.StartFrame + clip.Duration) throw new ArgumentOutOfRangeException(nameof(splitFrame));
            var originalEnd = clip.StartFrame + clip.Duration;
            clip.Duration = splitFrame - clip.StartFrame;
            right = Clone(clip); right.Id = Guid.CreateVersion7(); right.StartFrame = splitFrame; right.Duration = originalEnd - splitFrame;
            _clips.Add(right); left = Clone(clip);
        }
        Notify(TimelineChangeKind.Split, [left.Id, right.Id]);
        return (left, Clone(right));
    }
    public DraftStructureJSON Export(Guid snapshotId = default, Guid previousSnapshot = default, string? reason = null)
    {
        lock (_gate) return new DraftStructureJSON
        {
            SnapshotID = snapshotId, PreviousSnapshot = previousSnapshot, Clips = _clips.Select(Clone).ToArray(),
            Duration = _clips.Count == 0 ? 0 : _clips.Max(x => x.StartFrame + x.Duration), SavedAt = DateTime.UtcNow, ChangeReason = reason ?? string.Empty
        };
    }
    public Task SaveAsync(string relativePath = "timeline.json", CancellationToken cancellationToken = default)
        => SaveAsync(Export(), relativePath, cancellationToken);
    public Task SaveAsync(DraftStructureJSON draft, string relativePath = "timeline.json", CancellationToken cancellationToken = default)
        => Context?.Storage.WriteTextAsync(relativePath, JsonSerializer.Serialize(draft, Options), cancellationToken)
            ?? throw new InvalidOperationException("The module is not initialized.");
    private ClipDraftDTO Find(Guid id) => _clips.FirstOrDefault(x => x.Id == id) ?? throw new KeyNotFoundException($"Clip '{id}' was not found.");
    private void Notify(TimelineChangeKind kind, IEnumerable<Guid> ids, string? detail = null)
    {
        Context?.Modules.Get<ProjectModule>().MarkDirty(detail ?? kind.ToString());
        Changed?.Invoke(this, new TimelineChangedEventArgs(kind, ids.ToArray(), detail));
    }
    private static ClipDraftDTO Clone(ClipDraftDTO value) => JsonSerializer.Deserialize<ClipDraftDTO>(JsonSerializer.Serialize(value))!;
}
