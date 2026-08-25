using CommunityToolkit.Maui;
using CommunityToolkit.Maui.Core;
using CommunityToolkit.Maui.Extensions;
using CommunityToolkit.Maui.Storage;
using LocalizedResources;
using Microsoft.Maui.Controls.Shapes;
using projectFrameCut.ApplicationAPIBase.Helpers;
using projectFrameCut.ApplicationAPIBase.Plugins;
using projectFrameCut.ApplicationAPIBase.Views.MultiWindowView;
using projectFrameCut.ApplicationAPIBase.Views.PropertyPanelBuilders;
using projectFrameCut.ApplicationPluginBase.VectorComponentHandler;
using projectFrameCut.Drawing.Base;
using projectFrameCut.Drawing.Vector;
using projectFrameCut.Drawing.Vector.ImportExport;
using projectFrameCut.InteractableEditor;
using projectFrameCut.Render.ClipsAndTracks;
using projectFrameCut.Render.Plugin;
using projectFrameCut.Render.RenderAPIBase.ClipAndTrack;
using projectFrameCut.Render.RenderAPIBase.VectorContent;
using projectFrameCut.Render.VectorContent;
using projectFrameCut.Render.VectorContent.Components;
using projectFrameCut.Services;
using projectFrameCut.Shared;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using System.Windows.Input;
using Color = Microsoft.Maui.Graphics.Color;
using IDispatcher = Microsoft.Maui.Dispatching.IDispatcher;
using IVectorComponentHandler = projectFrameCut.ApplicationAPIBase.VectorComponentHandler.IVectorComponentHandler;
using Point = projectFrameCut.Drawing.Vector.Point;
using ShapeHandlePositionType = projectFrameCut.ApplicationAPIBase.VectorComponentHandler.ShapeHandlePositionType;

namespace projectFrameCut.DraftStuff;

/// <summary>
/// A snapshot record with a GUID for history graph integration.
/// </summary>
internal readonly struct SnapshotRecord
{
    public readonly Guid Id;
    public readonly string Json;
    public readonly string Description;
    public readonly DateTime Timestamp;

    public SnapshotRecord(Guid id, string json, string description, DateTime timestamp)
    {
        Id = id;
        Json = json;
        Description = description;
        Timestamp = timestamp;
    }
}

/// <summary>
/// DTO for persisting a single snapshot record to disk.
/// </summary>
internal class PersistedSnapshotRecord
{
    public Guid Id { get; set; }
    public string Json { get; set; } = "";
    public string Description { get; set; } = "";
    public DateTime Timestamp { get; set; }
}

/// <summary>
/// DTO for the full persisted history file.
/// </summary>
internal class PersistedVectorHistory
{
    public Guid ClipId { get; set; }
    public Guid CurrentStateId { get; set; }
    public string InitialComponentsJson { get; set; } = "";
    public List<PersistedSnapshotRecord> UndoRecords { get; set; } = new();
    public List<PersistedSnapshotRecord> RedoRecords { get; set; } = new();
}

/// <summary>
/// Vector content editor — MVU-style self-contained page.
/// Manages component list, shape parameters, animation tracks, and interactive preview.
/// </summary>
public partial class VectorContentEditorPage : ContentPage, INotifyPropertyChanged, IHistoryGraphProvider
{
    private const string ComponentDragProperty = "VectorComponentId";

    // ═══════════════════════════════════════════════════════════
    // Injected state
    // ═══════════════════════════════════════════════════════════

    private readonly VectorCanvasClip _clip;
    private readonly IDispatcher _dispatcher;

    private List<IVectorComponent> _editingComponents = new();
    private List<IVectorComponent> _componentsBackup = new();
    private TimelineDrawable _timelineDrawable = null!;

    // ── InteractableEditor integration ──────────────────────
    private List<VectorComponentWrapperClip> _componentClips = new();
    private Dictionary<Guid, ClipElementUI> _clipElementUIs = new();
    private bool _suppressComponentPropertySync;

    // ── Undo / Redo (GUID-tracked for history graph) ──────────
    // Past states: pushed via PushUndoSnapshot before each change.
    private readonly List<SnapshotRecord> _undoRecords = new();
    // Future states: pushed via Undo(), popped via Redo().
    private readonly List<SnapshotRecord> _redoRecords = new();
    // Dedicated GUID for the current (live, visible) state.
    private Guid _currentStateId = Guid.NewGuid();

    // ── History Graph View ─────────────────────────────────────
    private HistoryGraphView? _historyGraphView;

    // ── Clipboard (Copy / Cut / Paste) ────────────────────────
    private List<IVectorComponent>? _clipboard;

    // ── Persistent history (disk-backed) ───────────────────────
    private string _historyDirectoryPath = string.Empty;

    private static readonly JsonSerializerOptions UndoJsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        PropertyNamingPolicy = null,
    };

    private static readonly JsonSerializerOptions HistoryJsonOptions = new()
    {
        WriteIndented = false,
        PropertyNamingPolicy = null,
        PropertyNameCaseInsensitive = true,
    };

    // ═══════════════════════════════════════════════════════════
    // Constructors
    // ═══════════════════════════════════════════════════════════

    /// <summary>Parameterless constructor required by XAML parser.</summary>
    public VectorContentEditorPage()
    {
        InitializeComponent();

        InteractiveEditor.Init(
            updateCallback: OnInteractiveEditorChanged,
            videoWidth: 1920,
            videoHeight: 1080);

        string errorText =
            $"""
            VectorContentEditorView: No VectorCanvasClip provided, this is not a excepted behavior. 
            If you see this, feedback this bug to us.

            StackTrace:
            {Environment.StackTrace}

            Parent:
            {Parent?.GetType()?.Name ?? "null"}
            """;

        InteractiveEditor.ApplyPreparedPreviews(
            [new projectFrameCut.ApplicationAPIBase.Interaction.PreparedPreview(Guid.Empty, () => new Label { Text = errorText, HorizontalOptions = LayoutOptions.Center, VerticalOptions = LayoutOptions.Center }, null, null)]);

    }

    /// <summary>
    /// Create the editor for the given <paramref name="clip"/>.
    /// Works for both SVG-backed and composition-only clips.
    /// </summary>
    public VectorContentEditorPage(VectorCanvasClip clip, int projectWidth, int projectHeight, string? workingPath = null)
    {
        InitializeComponent();

        _clip = clip ?? throw new ArgumentNullException(nameof(clip));
        _dispatcher = Dispatcher;

        // ── Set up persistent history directory ──
        if (!string.IsNullOrWhiteSpace(workingPath))
            _historyDirectoryPath = System.IO.Path.Combine(workingPath, "vectorContent", "history");

        int previewProjectWidth = Math.Max(1, projectWidth);
        int previewProjectHeight = Math.Max(1, projectHeight);
        PreviewWidth = clip.TargetWidth > 0 ? clip.TargetWidth : previewProjectWidth;
        PreviewHeight = clip.TargetHeight > 0 ? clip.TargetHeight : previewProjectHeight;

        // Work on a clone so Cancel can discard changes
        _editingComponents = clip.Components.ToList();
        _componentsBackup = clip.Components.ToList();

        // Set up timeline rendering
        _timelineDrawable = new TimelineDrawable { View = this };
        TimelineCanvas.Drawable = _timelineDrawable;

        // Wire up timeline invalidation
        _timelineInvalidateAction = () =>
            MainThread.BeginInvokeOnMainThread(() => TimelineCanvas.Invalidate());

        // Invalidate timeline when selected component/track/progress changes
        PropertyChanged += (_, e) =>
        {
            if (e.PropertyName is nameof(CurrentProgress)
                or nameof(SelectedTrack)
                or nameof(SelectedComponent))
            {
                TimelineCanvas.Invalidate();
            }
        };

        // ── Initialize InteractableEditor ──
        int canvasW = Math.Max(1, PreviewWidth);
        int canvasH = Math.Max(1, PreviewHeight);

        InteractiveEditor.Init(
            updateCallback: OnInteractiveEditorChanged,
            videoWidth: canvasW,
            videoHeight: canvasH);

        // ── Build initial component clips BEFORE canvas size update ──
        // so that ProvideHandlesViaHandler has _componentClips populated
        // when UpdateVisuals fires its first handle query.
        RebuildComponentClips();

        Action? ReferenceLinesHandler =
            () => DraftPage.ShowManageReferenceLinesPopup(
                InteractiveEditor,
                async (v) =>
                {
                    await Dispatcher.DispatchAsync(async () =>
                    {
                        try
                        {
                            await (Window?.Page?.ShowPopupAsync(v, new PopupOptions { Shape = new RoundRectangle { CornerRadius = new CornerRadius(UIServices.GetWindowCornerRadius()), Background = Colors.Transparent } }) ?? Task.CompletedTask);
                        }
                        catch
                        {
                        }
                    });
                },
                async () =>
                {
                    await Dispatcher.DispatchAsync(async () =>
                    {
                        try
                        {
                            await (Window?.Page?.ClosePopupAsync() ?? Task.CompletedTask);
                        }
                        catch
                        {
                        }
                    });
                });

        InteractiveEditor.ConfigureOverlayClipTap(OnComponentClipTapped)
                         .ConfigureBlankAreaTap(OnBlankAreaTapped)
                         .ConfigurePreviewRefresh(OnPreviewRefreshRequested)
                         .ConfigureGetClipInstanceCallback(GetClipInstanceForEditor)
                         .ConfigureCustomHandles(ProvideHandlesViaHandler, OnHandlerDrag)
                         .ConfigureManageReferenceLinesRequested(ReferenceLinesHandler)
                         .ConfigureKeyframeCandidateCaptured(OnComponentKeyframeCandidateCaptured)
                         .UpdateCanvasSize(canvasW, canvasH);

        InteractiveEditor.EditorCanvasBackground = Colors.Transparent;

        // ── Subscribe to progress changes for preview refresh ──
        PropertyChanged += (_, e) =>
        {
            if (e.PropertyName is nameof(CurrentProgress))
            {
                InteractiveEditor.SetCurrentFrame(GetCurrentFrameNumber());
                _ = RefreshInteractivePreviewsAsync();
            }
            else if (e.PropertyName is nameof(SelectedComponent))
            {
                SyncSelectedComponentToEditor();
                RebuildDynamicPropertyPanel();
            }
        };

        // ── Subscribe to component collection changes ──
        Components.CollectionChanged += (_, _) =>
        {
            MainThread.BeginInvokeOnMainThread(RebuildComponentClips);
            ApplyChanges();
        };

        // ── Subscribe to individual component property changes (right panel → editor) ──
        SubscribeToComponentPropertyChanges();

        // ── Initialize state ──
        InitializeState();

        // ── Restore persisted undo/redo history for this clip ──
        LoadAndRestoreHistoryFromDisk();

        // ── Apply the default MDI layout once the MultiWindowView has a real size ──
        MainMultiWindowView.SizeChanged += OnMainMultiWindowViewSizeChanged;

        // ── Complete view setup ──
        BindingContext = this;

        if (Parent is MultiWindowItem mvi)
        {
            mvi.IsPopOutVisible = false;
        }

        MainMultiWindowView.CloseWindow(HistorySubWindow);
    }

    private void OnMainMultiWindowViewSizeChanged(object? sender, EventArgs e)
    {
        if (_defaultLayoutApplied) return;
        ApplyDefaultMultiWindowLayout();
    }

    // ═══════════════════════════════════════════════════════════
    // Public events for host page
    // ═══════════════════════════════════════════════════════════

    public event Action<Dictionary<string, object>?>? ChangesApplied;
    public event EventHandler? ChangesCancelled;

    private void OnChangesAppliedForward(Dictionary<string, object>? e) =>
        ChangesApplied?.Invoke(e);

    private void OnChangesCancelledForward(object? sender, EventArgs e) =>
        ChangesCancelled?.Invoke(this, EventArgs.Empty);

    /// <summary>
    /// Save history to disk when the page is navigated away from.
    /// </summary>
    protected override void OnDisappearing()
    {
        base.OnDisappearing();
        SaveHistoryToDisk();
    }

    /// <summary>点击组件行将其设为当前选中组件（用于右侧属性面板）。</summary>
    private void OnComponentItemTapped(object? sender, TappedEventArgs e)
    {
        if (sender is VisualElement ve && ve.BindingContext is VectorComponentItem item)
        {
            SelectedComponent = item;
        }
        OnPropertyChanged(nameof(CanGroupComponents));
        OnPropertyChanged(nameof(CanUngroupComponent));
        OnPropertyChanged(nameof(CanGroupOrUngroup));
        OnPropertyChanged(nameof(GroupButtonText));
    }

    private void OnComponentDragStarting(object? sender, DragStartingEventArgs e)
    {
        if (sender is VisualElement ve && ve.BindingContext is VectorComponentItem item)
        {
            e.Data.Properties[ComponentDragProperty] = item.Id.ToString("D");
        }
    }

    private void OnComponentDrop(object? sender, DropEventArgs e)
    {
        if (sender is not VisualElement ve
            || ve.BindingContext is not VectorComponentItem target
            || !e.Data.Properties.TryGetValue(ComponentDragProperty, out var sourceValue)
            || sourceValue is not string sourceIdText
            || !Guid.TryParse(sourceIdText, out var sourceId))
        {
            return;
        }

        var source = Components.FirstOrDefault(item => item.Id == sourceId);
        if (source is null || source == target)
            return;

        int sourceIndex = Components.IndexOf(source);
        int targetIndex = Components.IndexOf(target);
        if (sourceIndex < 0 || targetIndex < 0 || sourceIndex == targetIndex)
            return;

        PushUndoSnapshot(Localized.VectorContentEditorView_History_DefaultAction);

        _editingComponents.Remove(source.Source);
        _editingComponents.Insert(targetIndex, source.Source);
        for (int i = 0; i < _editingComponents.Count; i++)
        {
            var item = Components.First(component => component.Source == _editingComponents[i]);
            item.LayerIndex = i;
        }

        Components.Move(sourceIndex, targetIndex);

        RebuildComponentClips();
        _ = RefreshInteractivePreviewsAsync();
    }

    /// <summary>由 VectorComponentItem.IsChecked 通知，更新多选集合。 </summary>
    public void OnComponentCheckedChanged(VectorComponentItem item, bool isChecked)
    {
        if (isChecked)
        {
            if (!SelectedComponents.Contains(item))
                SelectedComponents.Add(item);
        }
        else
        {
            SelectedComponents.Remove(item);
        }

        OnPropertyChanged(nameof(CanGroupComponents));
        OnPropertyChanged(nameof(CanUngroupComponent));
        OnPropertyChanged(nameof(CanGroupOrUngroup));
        OnPropertyChanged(nameof(GroupButtonText));
    }

    // ═══════════════════════════════════════════════════════════
    // Properties — INotifyPropertyChanged
    // ═══════════════════════════════════════════════════════════

    public VectorCanvasClip Clip => _clip;

    public string ClipName => _clip.Name;

    /// <summary>Clip-level duration in frames. Component durations may differ.</summary>
    public uint DurationInFrames
    {
        get => _clip.Duration;
        set
        {
            if (_clip.Duration != value)
            {
                // Duration is set on each component item; the clip duration
                // is updated when Apply is pressed.
                OnPropertyChanged();
            }
        }
    }

    /// <summary>All elements from the source picture (read-only index reference).</summary>
    public ObservableCollection<ElementItem> Elements { get; } = new();

    /// <summary>Flat list of element names for Picker ItemsSource.</summary>
    public List<string> ElementNames { get; } = new();

    /// <summary>Index into the Elements list, for Picker.SelectedIndex binding.</summary>
    public int SelectedElementIndex
    {
        get;
        set
        {
            if (field != value)
            {
                field = value;
                OnPropertyChanged();
            }
        }
    }

    /// <summary>Derived from SelectedElementIndex.</summary>
    public ElementItem? SelectedElement =>
        SelectedElementIndex >= 0 && SelectedElementIndex < Elements.Count
            ? Elements[SelectedElementIndex]
            : null;

    public bool CanAddTrack => SelectedComponent is not null;
    public bool CanRemoveTrack => SelectedTrack is not null;
    public bool CanAddKeyFrame => SelectedTrack is not null;

    public KeyFrameItem? SelectedKeyFrame
    {
        get;
        set
        {
            if (field != value)
            {
                field = value;
                OnPropertyChanged();
                OnPropertyChanged(nameof(HasSelectedKeyFrame));
                _timelineInvalidateAction?.Invoke();
            }
        }
    }

    public bool HasSelectedKeyFrame => SelectedKeyFrame is not null;

    /// <summary>All animation tracks from the currently selected component.</summary>
    public ObservableCollection<AnimationTrackItem> Tracks => SelectedComponent?.Tracks ?? new();

    public AnimationTrackItem? SelectedTrack
    {
        get;
        set
        {
            if (field != value)
            {
                field = value;
                SelectedKeyFrame = null;
                OnPropertyChanged();
                OnPropertyChanged(nameof(CanRemoveTrack));
                OnPropertyChanged(nameof(CanAddKeyFrame));
                _timelineInvalidateAction?.Invoke();
            }
        }
    }

    /// <summary>Selected field ID for new tracks.</summary>
    public string SelectedFieldIdForNewTrack
    {
        get;
        set { if (field != value) { field = value; OnPropertyChanged(); } }
    } = "";

    private List<string> _availableFieldIds = new();
    /// <summary>All available field IDs for the track property picker.</summary>
    public List<string> AvailableFieldIds => _availableFieldIds;

    /// <summary>All available easing modes for the keyframe easing Picker.</summary>
    public List<EasingMode> AllEasingModes { get; } =
        new List<EasingMode>(Enum.GetValues<EasingMode>());

    // ── Preview state ──

    public float CurrentProgress
    {
        get;
        set
        {
            if (!Equals(field, value))
            {
                field = Math.Clamp(value, 0f, 1f);
                OnPropertyChanged();
                _timelineInvalidateAction?.Invoke();
            }
        }
    }

    public bool IsPlaying
    {
        get;
        set { if (field != value) { field = value; OnPropertyChanged(); } }
    }


    public bool ShowAllComponentBounds { get; set; } = false;


    public int PreviewWidth { get; set; } = 320;
    public int PreviewHeight { get; set; } = 240;

    private bool HasAnyTracks => Components.Any(c => c.Tracks.Count > 0);

    // ── Component management ──

    /// <summary>User-created vector components being edited.</summary>
    public ObservableCollection<VectorComponentItem> Components { get; } = new();

    public VectorComponentItem? SelectedComponent
    {
        get;
        set
        {
            if (field != value)
            {
                // 清除旧选项的高亮
                if (field is not null)
                    field.IsSelected = false;

                field = value;

                // 标记新选项为高亮
                if (field is not null)
                    field.IsSelected = true;

                SelectedTrack = value?.Tracks.FirstOrDefault();
                SelectedKeyFrame = null;
                RebuildAvailableFieldIds();
                OnPropertyChanged();
                OnPropertyChanged(nameof(SelectedComponent));
                OnPropertyChanged(nameof(HasSelectedComponent));
                OnPropertyChanged(nameof(CanAddTrack));
                OnPropertyChanged(nameof(CanRemoveTrack));
                OnPropertyChanged(nameof(CanAddKeyFrame));
                OnPropertyChanged(nameof(Tracks));
                OnPropertyChanged(nameof(CanGroupComponents));
                OnPropertyChanged(nameof(CanUngroupComponent));
                OnPropertyChanged(nameof(CanGroupOrUngroup));
                OnPropertyChanged(nameof(GroupButtonText));
                OnPropertyChanged(nameof(AvailableFieldIds));
                // Also notify Command.CanExecute so the button re-evaluates its enabled state.
                // The CheckBox-based OnComponentCheckedChanged may not always fire at the right
                // time when SelectedComponent changes, so we explicitly notify here.
                if (GroupComponentsCommand is Command groupCmd) groupCmd.ChangeCanExecute();
                SelectedFieldIdForNewTrack = AvailableFieldIds.Count > 0 ? AvailableFieldIds[0] : "";
                _timelineInvalidateAction?.Invoke();
            }
        }
    }

    /// <summary>All currently selected components in the list (supports multi-select for grouping).</summary>
    public ObservableCollection<VectorComponentItem> SelectedComponents { get; } = new();

    public bool HasSelectedComponent => SelectedComponent is not null;

    // ═══════════════════════════════════════════════════════════
    // IHistoryGraphProvider implementation
    // ═══════════════════════════════════════════════════════════

    /// <summary>Display name shown in the history graph's info label.</summary>
    string IHistoryGraphProvider.ProviderName => "VectorContentEditor";

    /// <summary>GUID of the currently active state (the live visible state).</summary>
    Guid IHistoryGraphProvider.CurrentSnapshotID => _currentStateId;

    /// <summary>Raised when the current snapshot changes externally.</summary>
    public event EventHandler<EventArgs>? CurrentSnapshotChanged;

    void IHistoryGraphProvider.NotifyExternalSnapshotChanged()
    {
        CurrentSnapshotChanged?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>
    /// Builds graph nodes and edges from the undo records + current state + redo records.
    /// </summary>
    (List<HistoryGraphNode> Nodes, List<HistoryGraphEdge> Edges) IHistoryGraphProvider.BuildGraphData()
    {
        var nodes = new List<HistoryGraphNode>();
        var edges = new List<HistoryGraphEdge>();
        Guid currentId = ((IHistoryGraphProvider)this).CurrentSnapshotID;

        // ── Collect all restorable states in chronological order ──
        // 1. Undo records (past states, oldest first)
        // 2. Current live state
        // 3. Redo records (future states, from earliest undone to latest)
        var allStates = new List<(Guid Id, string Description, DateTime Timestamp)>();
        var guidToState = new Dictionary<Guid, int>(); // state GUID → index in allStates

        int idx = 0;
        foreach (var rec in _undoRecords)
        {
            allStates.Add((rec.Id, rec.Description, rec.Timestamp));
            guidToState[rec.Id] = idx++;
        }

        // Current state
        allStates.Add((_currentStateId, "(current)", DateTime.Now));
        guidToState[_currentStateId] = idx++;

        foreach (var rec in _redoRecords)
        {
            allStates.Add((rec.Id, rec.Description, rec.Timestamp));
            guidToState[rec.Id] = idx++;
        }

        // ── Build nodes ──
        for (int i = 0; i < allStates.Count; i++)
        {
            var (id, desc, ts) = allStates[i];
            Guid prevId = i > 0 ? allStates[i - 1].Id : Guid.Empty;
            bool isCurrent = id == currentId;

            List<Guid> nextIds = i < allStates.Count - 1
                ? new() { allStates[i + 1].Id }
                : new();

            nodes.Add(new HistoryGraphNode
            {
                SnapshotID = id,
                PreviousSnapshotID = prevId,
                NextSnapshotIDs = nextIds,
                SavedAt = ts,
                ChangeReason = desc,
                ChangedByUserDisplayName = "User",
                IsCurrentSnapshot = isCurrent,
                IsHead = nextIds.Count == 0,
            });
        }

        // ── Build edges ──
        for (int i = 0; i < allStates.Count - 1; i++)
        {
            var fromId = allStates[i].Id;
            var toId = allStates[i + 1].Id;
            bool isCurrentPath = fromId == currentId || toId == currentId;
            edges.Add(new HistoryGraphEdge
            {
                FromSnapshotID = fromId,
                ToSnapshotID = toId,
                IsCurrentPath = isCurrentPath,
            });
        }

        return (nodes, edges);
    }

    /// <summary>
    /// Restores the editor state to the given snapshot GUID.
    /// Searches undo records, then redo records.
    /// </summary>
    async Task<bool> IHistoryGraphProvider.ApplySnapshotAsync(Guid snapshotId)
    {
        // Is it the current state?
        if (snapshotId == _currentStateId)
            return true;

        // Search undo records
        for (int i = 0; i < _undoRecords.Count; i++)
        {
            if (_undoRecords[i].Id == snapshotId)
            {
                // Save current state as redo, then undo until we reach the target
                SaveCurrentStateAsRedoRecord();
                while (_undoRecords.Count > 0 && _undoRecords.Count > i)
                {
                    var rec = _undoRecords[^1];
                    _redoRecords.Add(rec);
                    _undoRecords.RemoveAt(_undoRecords.Count - 1);
                }
                // Restore target (it's now in redo, pop it back)
                var target = _redoRecords[^1];
                _redoRecords.RemoveAt(_redoRecords.Count - 1);
                _undoRecords.Add(target);
                RestoreComponentsFromJson(target.Json);
                _currentStateId = target.Id;

                ((Command)UndoCommand).ChangeCanExecute();
                ((Command)RedoCommand).ChangeCanExecute();
                CurrentSnapshotChanged?.Invoke(this, EventArgs.Empty);
                return true;
            }
        }

        // Search redo records
        for (int i = 0; i < _redoRecords.Count; i++)
        {
            if (_redoRecords[i].Id == snapshotId)
            {
                // Redo until we reach the target
                SaveCurrentStateAsUndoRecord();
                while (_redoRecords.Count > 0 && _redoRecords.Count > i)
                {
                    var rec = _redoRecords[0];
                    _undoRecords.Add(rec);
                    _redoRecords.RemoveAt(0);
                }
                // i has been redo'd to become the last undo record
                var target = _undoRecords[^1];
                RestoreComponentsFromJson(target.Json);
                _currentStateId = target.Id;

                ((Command)UndoCommand).ChangeCanExecute();
                ((Command)RedoCommand).ChangeCanExecute();
                CurrentSnapshotChanged?.Invoke(this, EventArgs.Empty);
                return true;
            }
        }

        return false;
    }

    /// <summary>Snapshot current state and save as a new undo record.</summary>
    private void SaveCurrentStateAsUndoRecord()
    {
        var group = new ComponentGroup { Name = "Undo Snapshot" };
        group.SetChildren(_editingComponents.ToList());
        var json = JsonSerializer.Serialize(group, UndoJsonOptions);
        _undoRecords.Add(new SnapshotRecord(
            _currentStateId, json,
            Localized.VectorContentEditorView_History_DefaultAction,
            DateTime.Now));
        _currentStateId = Guid.NewGuid();
    }

    /// <summary>Snapshot current state and save as a new redo record.</summary>
    private void SaveCurrentStateAsRedoRecord()
    {
        var group = new ComponentGroup { Name = "Redo Snapshot" };
        group.SetChildren(_editingComponents.ToList());
        var json = JsonSerializer.Serialize(group, UndoJsonOptions);
        _redoRecords.Insert(0, new SnapshotRecord(
            _currentStateId, json,
            Localized.VectorContentEditorView_History_DefaultAction,
            DateTime.Now));
        _currentStateId = Guid.NewGuid();
    }

    void IHistoryGraphProvider.OnNodeSelected(HistoryGraphNode node) { }
    void IHistoryGraphProvider.OnNodeDeselected(HistoryGraphNode node) { }

    /// <summary>Number of undo steps available.</summary>
    public int UndoCount => _undoRecords.Count;
    /// <summary>Number of redo steps available.</summary>
    public int RedoCount => _redoRecords.Count;

    public bool CanGroupComponents => SelectedComponents.Count >= 2 && SelectedComponents.All(c => c.Source is not ComponentGroup);

    public bool CanUngroupComponent => SelectedComponents.Count == 1 && SelectedComponent?.Source is ComponentGroup;

    public bool CanGroupOrUngroup
    {
        get
        {
            if (SelectedComponents.Count >= 2)
            {
                return !SelectedComponents.Any(c => c.Source is ComponentGroup);
            }
            else
            {
                return SelectedComponent?.Source is ComponentGroup g && !g.IsSVG;
            }
        }
    }

    /// <summary>Localized text for the Group/Ungroup button.</summary>
    public string GroupButtonText => CanGroupComponents
        ? Localized.VectorContentEditorView_Group
        : Localized.VectorContentEditorView_Ungroup;

    // ── Shape gallery ──

    /// <summary>Available shapes for adding to the composition.</summary>
    public List<ShapeGalleryItem> ShapeGalleryItems { get; } = new();

    // ═══════════════════════════════════════════════════════════
    // Timeline invalidation
    // ═══════════════════════════════════════════════════════════

    private Action? _timelineInvalidateAction;

    public void RegisterTimelineInvalidate(Action invalidate) => _timelineInvalidateAction = invalidate;
    public void InvalidateTimeline() => _timelineInvalidateAction?.Invoke();

    // ═══════════════════════════════════════════════════════════
    // Commands
    // ═══════════════════════════════════════════════════════════

    public ICommand AddTrackCommand { get; private set; } = null!;
    public ICommand RemoveTrackCommand { get; private set; } = null!;
    public ICommand AddKeyFrameAtCurrentTimeCommand { get; private set; } = null!;
    public ICommand PlayPauseCommand { get; private set; } = null!;
    public ICommand StopCommand { get; private set; } = null!;
    public ICommand ApplyChangesCommand { get; private set; } = null!;
    public ICommand CancelCommand { get; private set; } = null!;
    public ICommand AddComponentCommand { get; private set; } = null!;
    public ICommand RemoveComponentCommand { get; private set; } = null!;
    public ICommand GroupComponentsCommand { get; private set; } = null!;
    public ICommand ImportSvgCommand { get; private set; } = null!;
    public ICommand DeleteKeyFrameCommand { get; private set; } = null!;
    public ICommand ExportJsonCommand { get; private set; } = null!;
    public ICommand SelectComponentCommand { get; private set; } = null!;

    // ── Menu bar commands ──
    public ICommand SaveCommand { get; private set; } = null!;
    public ICommand UndoCommand { get; private set; } = null!;
    public ICommand RedoCommand { get; private set; } = null!;
    public ICommand CopyCommand { get; private set; } = null!;
    public ICommand CutCommand { get; private set; } = null!;
    public ICommand PasteCommand { get; private set; } = null!;
    public ICommand DuplicateCommand { get; private set; } = null!;
    public ICommand ExportSvgCommand { get; private set; } = null!;
    public ICommand ShowHistoryCommand { get; private set; } = null!;

    private void RegisterCommands()
    {
        AddTrackCommand = new Command(AddTrack);
        RemoveTrackCommand = new Command(RemoveTrack);
        AddKeyFrameAtCurrentTimeCommand = new Command(AddKeyFrameAtCurrentTime);
        PlayPauseCommand = new Command(PlayPause);
        StopCommand = new Command(Stop);
        ApplyChangesCommand = new Command(ApplyChanges);
        CancelCommand = new Command(Cancel);
        AddComponentCommand = new Command<string>(AddComponent);
        RemoveComponentCommand = new Command(RemoveComponent);
        GroupComponentsCommand = new Command(GroupUngroupComponents);
        ImportSvgCommand = new Command(async () => await ImportSvg());
        DeleteKeyFrameCommand = new Command(DeleteKeyFrame);
        ExportJsonCommand = new Command(async () => await ExportToJsonAsync());
        SelectComponentCommand = new Command<VectorComponentItem>(item => SelectedComponent = item);

        // ── Menu bar commands ──
        SaveCommand = new Command(ApplyChanges);
        UndoCommand = new Command(Undo, () => _undoRecords.Count > 0);
        RedoCommand = new Command(Redo, () => _redoRecords.Count > 0);
        CopyCommand = new Command(Copy, () => HasSelectedComponent);
        CutCommand = new Command(Cut, () => HasSelectedComponent);
        PasteCommand = new Command(Paste, () => _clipboard is { Count: > 0 });
        DuplicateCommand = new Command(Duplicate, () => HasSelectedComponent);
        ExportSvgCommand = new Command(async () => await ExportAsSvg(), () => HasSelectedComponent);
        ShowHistoryCommand = new Command(ToggleHistoryWindow);
    }

    // ═══════════════════════════════════════════════════════════
    // Persistent history (disk I/O)
    // ═══════════════════════════════════════════════════════════

    private string GetHistoryFilePath()
    {
        if (string.IsNullOrWhiteSpace(_historyDirectoryPath)) return string.Empty;
        return System.IO.Path.Combine(_historyDirectoryPath, $"{_clip.Id}.json");
    }

    /// <summary>Save the full undo/redo state to disk.</summary>
    private void SaveHistoryToDisk()
    {
        string filePath = GetHistoryFilePath();
        if (string.IsNullOrWhiteSpace(filePath)) return;

        try
        {
            Directory.CreateDirectory(_historyDirectoryPath);

            var data = new PersistedVectorHistory
            {
                ClipId = _clip.Id,
                CurrentStateId = _currentStateId,
                InitialComponentsJson = SerializeEditingComponentsAsJson(),
                UndoRecords = _undoRecords.ConvertAll(r => new PersistedSnapshotRecord
                {
                    Id = r.Id,
                    Json = r.Json,
                    Description = r.Description,
                    Timestamp = r.Timestamp,
                }),
                RedoRecords = _redoRecords.ConvertAll(r => new PersistedSnapshotRecord
                {
                    Id = r.Id,
                    Json = r.Json,
                    Description = r.Description,
                    Timestamp = r.Timestamp,
                }),
            };

            string json = JsonSerializer.Serialize(data, HistoryJsonOptions);
            File.WriteAllText(filePath, json);
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[VectorContentEditorPage] Failed to save history: {ex.Message}");
        }
    }

    /// <summary>
    /// Load persisted history from disk and restore the undo/redo stacks.
    /// Only restores if the clip's current components match the saved initial state.
    /// </summary>
    private void LoadAndRestoreHistoryFromDisk()
    {
        string filePath = GetHistoryFilePath();
        if (string.IsNullOrWhiteSpace(filePath) || !File.Exists(filePath))
        {
            SaveHistoryToDisk(); // persist initial state for future loads
            return;
        }

        try
        {
            string json = File.ReadAllText(filePath);
            var data = JsonSerializer.Deserialize<PersistedVectorHistory>(json, HistoryJsonOptions);
            if (data is null) { SaveHistoryToDisk(); return; }

            // Consistency check: clip's current components must match saved initial state
            string currentJson = SerializeEditingComponentsAsJson();
            if (data.InitialComponentsJson != currentJson)
            {
                // Clip was changed externally — discard old history, start fresh
                SafeDeleteFile(filePath);
                SaveHistoryToDisk();
                return;
            }

            _undoRecords.Clear();
            _redoRecords.Clear();

            foreach (var r in data.UndoRecords)
                _undoRecords.Add(new SnapshotRecord(r.Id, r.Json, r.Description, r.Timestamp));
            foreach (var r in data.RedoRecords)
                _redoRecords.Add(new SnapshotRecord(r.Id, r.Json, r.Description, r.Timestamp));

            _currentStateId = data.CurrentStateId;

            ((Command)UndoCommand).ChangeCanExecute();
            ((Command)RedoCommand).ChangeCanExecute();
            RefreshHistoryGraph();
            CurrentSnapshotChanged?.Invoke(this, EventArgs.Empty);
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[VectorContentEditorPage] Failed to load history: {ex.Message}");
            // Corrupted file — remove and start fresh
            SafeDeleteFile(filePath);
            SaveHistoryToDisk();
        }
    }

    /// <summary>Serialize current <see cref="_editingComponents"/> to JSON (as a ComponentGroup).</summary>
    private string SerializeEditingComponentsAsJson()
    {
        var group = new ComponentGroup { Name = "History State" };
        group.SetChildren(_editingComponents.ToList());
        return JsonSerializer.Serialize(group, UndoJsonOptions);
    }

    private static void SafeDeleteFile(string path)
    {
        try { if (File.Exists(path)) File.Delete(path); }
        catch { /* best effort */ }
    }

    // ═══════════════════════════════════════════════════════════
    // Undo / Redo
    // ═══════════════════════════════════════════════════════════

    /// <summary>Save a snapshot of the current component state onto the undo stack.</summary>
    private void PushUndoSnapshot(string description = "")
    {
        var group = new ComponentGroup { Name = "Undo Snapshot" };
        group.SetChildren(_editingComponents.ToList());
        var json = JsonSerializer.Serialize(group, UndoJsonOptions);
        var guid = Guid.NewGuid();

        _undoRecords.Add(new SnapshotRecord(
            guid, json,
            string.IsNullOrWhiteSpace(description)
                ? Localized.VectorContentEditorView_History_DefaultAction
                : description,
            DateTime.Now));
        _currentStateId = guid;
        _redoRecords.Clear();

        ((Command)UndoCommand).ChangeCanExecute();
        ((Command)RedoCommand).ChangeCanExecute();
        RefreshHistoryGraph();

        // Notify the history graph that data changed
        CurrentSnapshotChanged?.Invoke(this, EventArgs.Empty);

        // Persist to disk
        SaveHistoryToDisk();
    }

    private void Undo()
    {
        if (_undoRecords.Count == 0) return;

        // Snapshot the current visible state as a redo record
        var currentGroup = new ComponentGroup { Name = "Redo Snapshot" };
        currentGroup.SetChildren(_editingComponents.ToList());
        var currentJson = JsonSerializer.Serialize(currentGroup, UndoJsonOptions);

        // Move the last undo record to become the current state
        var lastUndo = _undoRecords[^1];
        _undoRecords.RemoveAt(_undoRecords.Count - 1);

        // Save current state as the newest redo record
        _redoRecords.Insert(0, new SnapshotRecord(
            _currentStateId, currentJson,
            Localized.VectorContentEditorView_History_DefaultAction,
            DateTime.Now));

        // Restore the undone state
        _currentStateId = lastUndo.Id;
        RestoreComponentsFromJson(lastUndo.Json);

        ((Command)UndoCommand).ChangeCanExecute();
        ((Command)RedoCommand).ChangeCanExecute();
        RefreshHistoryGraph();
        CurrentSnapshotChanged?.Invoke(this, EventArgs.Empty);

        // Persist to disk
        SaveHistoryToDisk();
    }

    private void Redo()
    {
        if (_redoRecords.Count == 0) return;

        // Snapshot the current visible state as an undo record
        var currentGroup = new ComponentGroup { Name = "Undo Snapshot" };
        currentGroup.SetChildren(_editingComponents.ToList());
        var currentJson = JsonSerializer.Serialize(currentGroup, UndoJsonOptions);

        // Move the first redo record to become the current state
        var firstRedo = _redoRecords[0];
        _redoRecords.RemoveAt(0);

        // Save current state as a new undo record
        _undoRecords.Add(new SnapshotRecord(
            _currentStateId, currentJson,
            Localized.VectorContentEditorView_History_DefaultAction,
            DateTime.Now));

        // Restore the redo state
        _currentStateId = firstRedo.Id;
        RestoreComponentsFromJson(firstRedo.Json);

        ((Command)UndoCommand).ChangeCanExecute();
        ((Command)RedoCommand).ChangeCanExecute();
        RefreshHistoryGraph();
        CurrentSnapshotChanged?.Invoke(this, EventArgs.Empty);

        // Persist to disk
        SaveHistoryToDisk();
    }

    /// <summary>
    /// Deserializes a JSON snapshot of components and replaces the current editing state.
    /// </summary>
    private void RestoreComponentsFromJson(string json)
    {
        var group = JsonSerializer.Deserialize<ComponentGroup>(json, UndoJsonOptions);
        if (group is null) return;

        var restored = group.Children.ToList();
        _editingComponents = restored;

        Components.Clear();
        foreach (var comp in restored)
            Components.Add(new VectorComponentItem(comp, this));

        SelectedComponent = Components.FirstOrDefault();
        RebuildComponentClips();
        _ = RefreshInteractivePreviewsAsync();
    }

    // ═══════════════════════════════════════════════════════════
    // History panel — uses HistoryGraphView
    // ═══════════════════════════════════════════════════════════

    /// <summary>
    /// Opens or closes the history sub-window with the HistoryGraphView.
    /// </summary>
    private void ToggleHistoryWindow()
    {
        bool isOpen = MainMultiWindowView.Windows.Contains(HistorySubWindow);
        if (isOpen)
        {
            MainMultiWindowView.CloseWindow(HistorySubWindow);
        }
        else
        {
            EnsureHistoryGraphViewCreated();
            RefreshHistoryGraph();
            MainMultiWindowView.AddWindow(HistorySubWindow);
            MainMultiWindowView.BringToFront(HistorySubWindow);
        }
    }

    /// <summary>
    /// Lazily creates the HistoryGraphView and hooks it up to the provider.
    /// </summary>
    private void EnsureHistoryGraphViewCreated()
    {
        if (_historyGraphView is not null) return;
        _historyGraphView = new HistoryGraphView((IHistoryGraphProvider)this);
        HistorySubWindow.Content = _historyGraphView;
    }

    /// <summary>
    /// Rebuilds the graph data and pushes it to the HistoryGraphView.
    /// </summary>
    private void RefreshHistoryGraph()
    {
        if (_historyGraphView is null) return;
        var provider = (IHistoryGraphProvider)this;
        var (nodes, edges) = provider.BuildGraphData();
        _historyGraphView.LoadHistory(nodes, edges, provider.CurrentSnapshotID);
        OnPropertyChanged(nameof(UndoCount));
        OnPropertyChanged(nameof(RedoCount));
    }

    // ═══════════════════════════════════════════════════════════
    // Copy / Cut / Paste / Duplicate
    // ═══════════════════════════════════════════════════════════

    /// <summary>Deep-clones the selected component(s) into the internal clipboard.</summary>
    private void Copy()
    {
        var sources = SelectedComponents.Count > 0
            ? SelectedComponents.Select(c => c.Source).ToList()
            : SelectedComponent is not null
                ? new List<IVectorComponent> { SelectedComponent.Source }
                : null;

        if (sources is null || sources.Count == 0) return;

        var group = new ComponentGroup();
        group.SetChildren(sources);
        var json = JsonSerializer.Serialize(group, UndoJsonOptions);
        var restored = JsonSerializer.Deserialize<ComponentGroup>(json, UndoJsonOptions);
        _clipboard = restored?.Children.ToList();

        ((Command)PasteCommand).ChangeCanExecute();
    }

    private async void Cut()
    {
        if (!HasSelectedComponent) return;
        Copy();
        RemoveComponent(); // RemoveComponent() already calls PushUndoSnapshot()
        await Task.CompletedTask;
    }

    private async void Paste()
    {
        if (_clipboard is null || _clipboard.Count == 0) return;
        PushUndoSnapshot(Localized.VectorContentEditorView_History_Paste);

        var group = new ComponentGroup();
        group.SetChildren(_clipboard);
        var json = JsonSerializer.Serialize(group, UndoJsonOptions);
        var restored = JsonSerializer.Deserialize<ComponentGroup>(json, UndoJsonOptions);
        if (restored is null) return;

        var clones = restored.Children.ToList();
        foreach (var comp in clones)
        {
            // Offset position slightly to indicate a fresh paste
            var x = comp.Parameters.GetFloat("RelativeX", 0.5f) + 0.05f;
            var y = comp.Parameters.GetFloat("RelativeY", 0.5f) + 0.05f;
            comp.Parameters["RelativeX"] = Math.Clamp(x, 0f, 1f);
            comp.Parameters["RelativeY"] = Math.Clamp(y, 0f, 1f);

            _editingComponents.Add(comp);
            var item = new VectorComponentItem(comp, this);
            Components.Add(item);
        }

        SelectedComponent = Components.LastOrDefault();
        RebuildComponentClips();
        await RefreshInteractivePreviewsAsync();
    }

    private async void Duplicate()
    {
        if (SelectedComponent is null) return;
        PushUndoSnapshot(Localized.VectorContentEditorView_History_Duplicate);

        var group = new ComponentGroup();
        group.SetChildren(new List<IVectorComponent> { SelectedComponent.Source });
        var json = JsonSerializer.Serialize(group, UndoJsonOptions);
        var restored = JsonSerializer.Deserialize<ComponentGroup>(json, UndoJsonOptions);
        if (restored?.Children is not { Count: > 0 }) return;

        var clone = restored.Children[0];
        clone.Parameters["RelativeX"] = Math.Clamp(
            clone.Parameters.GetFloat("RelativeX", 0.5f) + 0.05f, 0f, 1f);
        clone.Parameters["RelativeY"] = Math.Clamp(
            clone.Parameters.GetFloat("RelativeY", 0.5f) + 0.05f, 0f, 1f);

        _editingComponents.Add(clone);
        var item = new VectorComponentItem(clone, this);
        Components.Add(item);
        SelectedComponent = item;

        RebuildComponentClips();
        await RefreshInteractivePreviewsAsync();
    }

    // ═══════════════════════════════════════════════════════════
    // Export as SVG
    // ═══════════════════════════════════════════════════════════

    /// <summary>
    /// Exports the current composition as an SVG file by computing all animated
    /// elements from every component and rendering them through
    /// <see cref="SVGToVectorElement.ExportToSvg"/>.
    /// </summary>
    private async Task ExportAsSvg()
    {
        try
        {
            var allElements = new List<VectorCanvasElement>();
            uint duration = Math.Max(1, _clip.Duration);

            foreach (var compItem in Components)
            {
                var comp = compItem.Source;
                // Use progress=0 (first frame) for a static export
                float progress = 0f;
                allElements.AddRange(comp.ComputeAll(progress));
            }

            if (allElements.Count == 0)
                return;

            var picture = new VectorPicture { Elements = allElements };
            int w = Math.Max(1, PreviewWidth);
            int h = Math.Max(1, PreviewHeight);
            string svgContent = SVGToVectorElement.ExportToSvg(picture, w, h);

            var safeName = SanitizeFileName(_clip.Name ?? "VectorComponents");
            var fileName = $"{safeName}.svg";

            using var stream = new MemoryStream(Encoding.UTF8.GetBytes(svgContent));
            var result = await FileSaver.Default.SaveAsync(fileName, stream);

            if (!result.IsSuccessful)
            {
                // Export cancelled or failed
            }
        }
        catch (Exception ex)
        {
            Log(ex, "export SVG", this);
        }
    }

    // ═══════════════════════════════════════════════════════════
    // Initialization
    // ═══════════════════════════════════════════════════════════

    private void InitializeState()
    {
        RegisterCommands();

        // Build shape gallery items from ShapeGalleryProvider
        foreach (var item in ShapeGalleryProvider.Items.Where(c => !c.ExcludeInNewComponent))
        {
            ShapeGalleryItems.Add(new ShapeGalleryItem
            {
                TypeName = item.TypeName,
                DisplayName = item.DisplayName,
                Icon = item.Icon,
                Description = Localized.VectorContentEditorView_AddShapeDescription(item.DisplayName),
            });
        }
        foreach (var item in PluginManager.LoadedPlugins.Select(c => c.Value).Where(c => c.PluginID != InternalPluginBase.InternalPluginBaseID).OfType<IApplicationPluginBase>().SelectMany(c => c.VectorComponentHandlerProvider.Values).Select(c => c()))
        {
            ShapeGalleryItems.Add(new ShapeGalleryItem
            {
                TypeName = item.TypeName,
                DisplayName = item.DisplayName,
                Icon = item.Icon,
                Description = Localized.VectorContentEditorView_AddShapeDescription(item.DisplayName),
            });
        }

        // Build component items from editing copies
        foreach (var component in _editingComponents)
        {
            var item = new VectorComponentItem(component, this);
            Components.Add(item);
        }

        // Auto-select first component or first SVG element
        if (Components.Count > 0)
            SelectedComponent = Components[0];
        else if (Elements.Count > 0)
            SelectedElementIndex = 0;

        // Refresh initial preview
        _ = RefreshInteractivePreviewsAsync();
    }

    /// <summary>
    /// Rebuilds the available field IDs for the track picker from the
    /// currently selected component's <see cref="IVectorComponent.AnimatableFields"/>.
    /// Called whenever <see cref="SelectedComponent"/> changes.
    /// Creates a new <see cref="List{T}"/> so the reference changes, ensuring
    /// MAUI's binding engine propagates the update to the Picker (a same-reference
    /// in-place <c>Clear</c>+<c>Add</c> would be treated as "unchanged" and skipped).
    /// </summary>
    private void RebuildAvailableFieldIds()
    {
        var ids = new List<string>();
        if (SelectedComponent?.Source.AnimatableFields is { } fields)
        {
            foreach (var key in fields.Keys)
                ids.Add(key);
        }
        _availableFieldIds = ids;
    }

    // ═══════════════════════════════════════════════════════════
    // Track management
    // ═══════════════════════════════════════════════════════════

    private void AddTrack()
    {
        if (SelectedComponent is null) return;
        if (string.IsNullOrWhiteSpace(SelectedFieldIdForNewTrack)) return;

        SelectedComponent.AddTrack(SelectedFieldIdForNewTrack);
        SelectedTrack = SelectedComponent.Tracks.LastOrDefault();
        OnPropertyChanged(nameof(HasAnyTracks));
        InvalidateTimeline();
    }

    private void RemoveTrack()
    {
        if (SelectedTrack is null || SelectedComponent is null) return;

        SelectedComponent.RemoveTrack(SelectedTrack);
        SelectedTrack = SelectedComponent.Tracks.FirstOrDefault();
        OnPropertyChanged(nameof(HasAnyTracks));
        InvalidateTimeline();
    }

    private void AddKeyFrameAtCurrentTime()
    {
        if (SelectedTrack is null) return;

        float value = SelectedTrack.GetValue(CurrentProgress);
        SelectedTrack.AddKeyFrame(CurrentProgress, value);
        SelectedKeyFrame = null;
        InvalidateTimeline();
    }

    private void DeleteKeyFrame()
    {
        if (SelectedTrack is null || SelectedKeyFrame is null) return;

        int index = SelectedTrack.KeyFrames.IndexOf(SelectedKeyFrame);
        SelectedTrack.RemoveKeyFrameAt(index);
        SelectedKeyFrame = null;
    }

    // ═══════════════════════════════════════════════════════════
    // Play control
    // ═══════════════════════════════════════════════════════════

    private IDispatcherTimer? _playTimer;

    private void PlayPause()
    {
        if (!HasAnyTracks) return;
        if (IsPlaying) Pause(); else Play();
    }

    private void Play()
    {
        if (!HasAnyTracks || IsPlaying) return;

        IsPlaying = true;
        _playTimer ??= _dispatcher.CreateTimer();
        _playTimer.IsRepeating = true;
        _playTimer.Interval = TimeSpan.FromMilliseconds(1000.0 / DeviceDisplay.MainDisplayInfo.RefreshRate);

        _playTimer.Tick -= OnPlayTick;
        _playTimer.Tick += OnPlayTick;
        _playTimer.Start();
    }

    private void Pause()
    {
        IsPlaying = false;
        if (_playTimer is not null)
        {
            _playTimer.Tick -= OnPlayTick;
            _playTimer.Stop();
        }
    }

    private void Stop()
    {
        Pause();
        CurrentProgress = 0f;
        _ = RefreshInteractivePreviewsAsync();
    }

    private async void OnPlayTick(object? sender, EventArgs e)
    {
        if (!IsPlaying) return;

        float step = _clip.Duration > 0
            ? 1f / _clip.Duration
            : 0.033f;

        CurrentProgress += step;
        if (CurrentProgress >= 1f)
            CurrentProgress = 0f;
    }

    // ═══════════════════════════════════════════════════════════
    // Apply / Cancel
    // ═══════════════════════════════════════════════════════════

    private void ApplyChanges()
    {
        var finalComponents = Components.Select(vm => vm.Source).ToList();
        _clip.Components = finalComponents;
        _clip.SerializeComponents(finalComponents);

        // Update clip duration from the first component's duration, or keep default
        if (Components.Count > 0)
        {
            // Duration is now managed at the clip level
        }

        ChangesApplied?.Invoke(_clip.ExtraData);
    }

    private async void Cancel()
    {
        if (IsPlaying) Stop();
        _clip.Components = _componentsBackup.ToList();
        ChangesCancelled?.Invoke(this, EventArgs.Empty);
    }

    // ═══════════════════════════════════════════════════════════
    // Export to JSON
    // ═══════════════════════════════════════════════════════════

    /// <summary>
    /// 将当前的 Components 导出为原始 JSON 文件，方便调试、备份或迁移。
    /// 序列化结果与 <see cref="VectorCanvasClip.SerializeComponents"/> 内部格式一致，
    /// 并经过美化排版（缩进），保存为 .json 文件。
    /// </summary>
    private async Task ExportToJsonAsync()
    {
        try
        {
            if (_editingComponents.Count == 0)
            {
                //await Toast.Make("没有可导出的组件。", ToastDuration.Short).Show();
                return;
            }

            var options = new JsonSerializerOptions
            {
                WriteIndented = true,
                PropertyNameCaseInsensitive = true,
                PropertyNamingPolicy = null,
            };

            var group = new ComponentGroup
            {
                Id = Guid.NewGuid(),
                Name = Localized.VectorContentEditorView_ExportShapesName,
                IsImportedGroup = true,
                IsSVG = false,
            };

            group.SetChildren(_editingComponents);

            var jsonBytes = JsonSerializer.SerializeToUtf8Bytes(group, options);
            using var stream = new MemoryStream(jsonBytes);

            var safeName = SanitizeFileName(_clip.Name ?? "VectorComponents");
            var fileName = $"{safeName}.shapes";

            var result = await FileSaver.Default.SaveAsync(fileName, stream);

            if (result.IsSuccessful)
            {
                //await Toast.Make($"已导出到 {result.FilePath}", ToastDuration.Long).Show();
            }
            else
            {
                //await Toast.Make("导出已取消或失败。", ToastDuration.Short).Show();
            }
        }
        catch (Exception ex)
        {
            Log(ex, "export Components JSON", this);
            //await Toast.Make($"导出失败: {ex.Message}", ToastDuration.Long).Show();
        }
    }

    /// <summary>将非法文件名字符替换为下划线。</summary>
    private static string SanitizeFileName(string name)
    {
        if (string.IsNullOrWhiteSpace(name)) return "VectorComponents";
        var invalid = System.IO.Path.GetInvalidFileNameChars();
        var sb = new System.Text.StringBuilder(name.Length);
        foreach (var ch in name)
            sb.Append(invalid.Contains(ch) ? '_' : ch);
        var result = sb.ToString().Trim();
        return result.Length > 0 ? result : "VectorComponents";
    }

    // ═══════════════════════════════════════════════════════════
    // Component management
    // ═══════════════════════════════════════════════════════════

    private void AddComponent(string typeName)
    {
        PushUndoSnapshot(Localized.VectorContentEditorView_History_AddComponent);
        var component = CreateComponent(typeName, Components.Count + 1);
        if (component is null) return;

        _editingComponents.Add(component);

        var item = new VectorComponentItem(component, this);
        Components.Add(item);
        SelectedComponent = item;

        _ = RefreshInteractivePreviewsAsync();
    }

    /// <summary>
    /// Creates a new <see cref="IVectorComponent"/> with default parameters
    /// for the given <paramref name="typeName"/>.
    /// </summary>
    private static IVectorComponent? CreateComponent(string typeName, int index)
    {
        // Use the plugin system to create the component
        var plugin = Render.Plugin.PluginManager.LoadedPlugins.GetValueOrDefault(
            Render.Plugin.InternalPluginBase.InternalPluginBaseID);
        if (plugin is null) return null;

        // Build a minimal JSON element for the component
        using var doc = JsonDocument.Parse($$"""
            {
                "TypeName": "{{typeName}}",
                "Id": "{{Guid.NewGuid()}}",
                "Name": "{{ShapeGalleryProvider.GetDisplayName(typeName)}} {{index}}",
                "Index": {{index}},
                "FromPlugin": "{{Render.Plugin.InternalPluginBase.InternalPluginBaseID}}"
            }
            """);
        var component = plugin.VectComponentCreator(doc.RootElement);
        return component;
    }

    private void RemoveComponent()
    {
        if (SelectedComponent is null) return;
        PushUndoSnapshot(Localized.VectorContentEditorView_History_Remove);

        var item = SelectedComponent;
        _editingComponents.Remove(item.Source);
        Components.Remove(item);
        SelectedComponents.Remove(item);

        SelectedComponent = Components.FirstOrDefault();
        _ = RefreshInteractivePreviewsAsync();
    }

    public void GroupUngroupComponents()
    {
        if (SelectedComponents.Count < 2 || SelectedComponent is ComponentGroup)
        {
            UngroupComponent();
        }
        else
        {
            GroupComponents();
        }
    }

    /// <summary>
    /// Groups the currently selected components into a new <see cref="ComponentGroup"/>.
    /// The group captures the current bounding box as its reference frame.
    /// </summary>
    private void GroupComponents()
    {
        if (SelectedComponents.Count < 2) return;
        PushUndoSnapshot(Localized.VectorContentEditorView_History_Group);
        if (SelectedComponents.Any(c => c.Source is ComponentGroup)) return;

        var itemsToGroup = SelectedComponents.ToList();
        var sourcesToGroup = itemsToGroup.Select(vm => vm.Source).ToList();

        // Compute the union bounds of the selected components at the current progress.
        var bounds = ComputeComponentBounds(sourcesToGroup);
        if (!bounds.IsValid)
        {
            // Fallback to a small default centered on the first component.
            bounds = (sourcesToGroup[0].Parameters.GetFloat("RelativeX", 0.5f),
                      sourcesToGroup[0].Parameters.GetFloat("RelativeY", 0.5f),
                      0.1f, 0.1f, true);
        }

        float groupCenterX = bounds.relX + bounds.width / 2f;
        float groupCenterY = bounds.relY + bounds.height / 2f;

        var group = new ComponentGroup
        {
            Name = $"Group {Components.Count(c => c.Source is ComponentGroup) + 1}",
            Index = itemsToGroup.Min(vm => vm.Source.Index),
        };
        group.Parameters["RelativeX"] = groupCenterX;
        group.Parameters["RelativeY"] = groupCenterY;
        group.Parameters["Width"] = bounds.width;
        group.Parameters["Height"] = bounds.height;
        group.SetInitialBounds(groupCenterX, groupCenterY, bounds.width, bounds.height);
        group.SetChildren(sourcesToGroup);

        foreach (var item in itemsToGroup)
        {
            _editingComponents.Remove(item.Source);
            Components.Remove(item);
        }

        _editingComponents.Add(group);
        var groupItem = new VectorComponentItem(group, this);
        Components.Add(groupItem);

        // 清除所有组件的 CheckBox 状态
        foreach (var c in Components) c.IsChecked = false;
        SelectedComponents.Clear();
        SelectedComponent = groupItem;

        RebuildComponentClips();
        _ = RefreshInteractivePreviewsAsync();
    }

    /// <summary>
    /// Ungroups the selected <see cref="ComponentGroup"/> back into individual components.
    /// </summary>
    private void UngroupComponent()
    {
        if (SelectedComponent?.Source is not ComponentGroup group) return;
        PushUndoSnapshot(Localized.VectorContentEditorView_History_Ungroup);

        var groupItem = SelectedComponent;
        var children = group.Children.ToList();

        _editingComponents.Remove(group);
        Components.Remove(groupItem);

        foreach (var child in children)
        {
            _editingComponents.Add(child);
            Components.Add(new VectorComponentItem(child, this));
        }

        // 清除所有组件的 CheckBox 状态
        foreach (var c in Components) c.IsChecked = false;
        SelectedComponents.Clear();
        SelectedComponent = Components.FirstOrDefault(c => children.Contains(c.Source));

        RebuildComponentClips();
        _ = RefreshInteractivePreviewsAsync();
    }

    private (float relX, float relY, float width, float height, bool IsValid) ComputeComponentBounds(IReadOnlyList<IVectorComponent> components)
    {
        if (components.Count == 0) return (0f, 0f, 0f, 0f, false);

        uint duration = Math.Max(1, _clip.Duration);
        uint currentFrame = (uint)Math.Round(CurrentProgress * Math.Max(0, duration - 1));
        float progress = duration <= 1 ? 0f : Math.Clamp(currentFrame / (float)(duration - 1), 0f, 1f);
        float canvasW = Math.Max(1f, PreviewWidth);
        float canvasH = Math.Max(1f, PreviewHeight);

        float minX = float.MaxValue, minY = float.MaxValue;
        float maxX = float.MinValue, maxY = float.MinValue;
        bool found = false;

        foreach (var component in components)
        {
            var elements = component.ComputeAll(progress);
            foreach (var element in elements)
            {
                var segments = element.Draw();
                if (segments is null) continue;

                foreach (var segment in segments)
                {
                    var points = GetSegmentPoints(segment);
                    foreach (var pt in points)
                    {
                        var (x, y) = MapElementLocalPointToCanvasNormalized(element, pt, canvasW, canvasH);
                        minX = Math.Min(minX, x);
                        minY = Math.Min(minY, y);
                        maxX = Math.Max(maxX, x);
                        maxY = Math.Max(maxY, y);
                        found = true;
                    }
                }
            }
        }

        if (!found) return (0f, 0f, 0f, 0f, false);

        return (minX, minY, maxX - minX, maxY - minY, true);
    }

    private static IEnumerable<Point> GetSegmentPoints(VectorSegment segment)
    {
        return segment switch
        {
            StraightLineVectorSegment l => [new Point(l.X1, l.Y1), new Point(l.X2, l.Y2)],
            RoundedRectangleVectorSegment rr => [new Point(rr.X, rr.Y), new Point(rr.X + rr.Width, rr.Y + rr.Height)],
            RectangleVectorSegment r => [new Point(r.X, r.Y), new Point(r.X + r.Width, r.Y + r.Height)],
            EllipseVectorSegment e => [new Point(e.X - e.RadiusX, e.Y - e.RadiusY), new Point(e.X + e.RadiusX, e.Y + e.RadiusY)],
            ArcVectorSegment a => [new Point(a.X - a.RadiusX, a.Y - a.RadiusY), new Point(a.X + a.RadiusX, a.Y + a.RadiusY)],
            CubicBezierVectorSegment b => [new Point(b.X1, b.Y1), new Point(b.X2, b.Y2), new Point(b.X3, b.Y3), new Point(b.X4, b.Y4)],
            QuadraticBezierVectorSegment q => [new Point(q.X1, q.Y1), new Point(q.X2, q.Y2), new Point(q.X3, q.Y3)],
            PolygonVectorSegment p => p.Points,
            PolylineVectorSegment p => p.Points,
            _ => Array.Empty<Point>(),
        };
    }

    private static (float x, float y) MapElementLocalPointToCanvasNormalized(
        VectorCanvasElement element,
        Point localPoint,
        float canvasW,
        float canvasH)
    {
        if (element.UseUniformScale)
        {
            float uniform = MathF.Min(canvasW, canvasH);
            float canvasX = element.BaseX * canvasW + element.RelativeX * uniform + localPoint.X * uniform;
            float canvasY = element.BaseY * canvasH + element.RelativeY * uniform + localPoint.Y * uniform;
            return (canvasX / canvasW, canvasY / canvasH);
        }

        float x = element.RelativeX + localPoint.X;
        float y = element.RelativeY + localPoint.Y;
        return (x, y);
    }

    /// <summary>
    /// Called by VectorComponentItem when a shape property changes
    /// and the preview needs to be refreshed.
    /// </summary>
    public void RequestPreviewRefresh()
    {
        _ = RefreshInteractivePreviewsAsync();
    }

    /// <summary>
    /// Called by VectorComponentItem when a shape parameter changes
    /// and the component clip bounds need to be recalculated.
    /// </summary>
    public void RequestComponentClipsRebuild()
    {
        MainThread.BeginInvokeOnMainThread(RebuildComponentClips);
    }

    // ═══════════════════════════════════════════════════════════
    // SVG import
    // ═══════════════════════════════════════════════════════════

    private async Task ImportSvg()
    {

        string? filePath;
        try { filePath = await OpenSvgFilePickerAsync(); }
        catch (Exception ex)
        {
            Log(ex, "SVG file picker failed", this);
            return;
        }

        if (string.IsNullOrEmpty(filePath)) return;

        try
        {
            var individualComponents = new List<IVectorComponent>();
            var fileName = System.IO.Path.GetFileNameWithoutExtension(filePath);
            switch (System.IO.Path.GetExtension(filePath).ToLower())
            {
                case ".svg":
                case ".xml":
                    var svgPicture = SVGToVectorElement.ImportFromFile(filePath);
                    if (svgPicture is null || svgPicture.Elements.Count == 0) return;

                    var handlerCache = EnsureHandlerCache();

                    // Convert each SVG element to an IVectorComponent
                    for (int i = 0; i < svgPicture.Elements.Count; i++)
                    {
                        var component = ConvertElementToComponent(svgPicture.Elements[i], i, handlerCache);
                        if (component is not null)
                        {
                            individualComponents.Add(component);
                        }
                    }

                    if (individualComponents.Count == 0) return;
                    break;
                case ".shapes":
                case ".json":
                    try
                    {
                        var comp = JsonSerializer.Deserialize<ComponentGroup>(File.ReadAllText(filePath));
                        individualComponents = comp?.Children ?? throw new InvalidDataException("Invalid .shapes file.");
                    }
                    catch { }

                    break;
                default:
                    //await Toast.Make($"不支持的文件类型: {filePath}", ToastDuration.Short).Show();
                    return;
            }

            // Wrap all components in a group for unified movement / scaling / rotation
            var group = WrapComponentsInGroup(individualComponents, fileName, isSVG: true, isImportedGroup: true, srcFileName: filePath);
            if (group is null) return;

            PushUndoSnapshot(Localized.VectorContentEditorView_History_Import);
            _editingComponents.Add(group);

            var item = new VectorComponentItem(group, this)
            {
                EditorSourceFilePath = filePath,
            };
            Components.Add(item);
            SelectedComponent = item;

            RebuildComponentClips();
            _ = RefreshInteractivePreviewsAsync();
        }
        catch (Exception ex)
        {
            Log(ex, $"Failed to import SVG from '{filePath}'", this);
        }
    }

    /// <summary>
    /// Converts a single SVG <see cref="VectorCanvasElement"/> to an <see cref="IVectorComponent"/>
    /// by inspecting the drawn segments and dispatching to the matching component type.
    /// </summary>
    private IVectorComponent? ConvertElementToComponent(
        VectorCanvasElement element, int index,
        Dictionary<string, IVectorComponentHandler> handlerCache)
    {
        var segments = element.Draw();
        if (segments is null || segments.Length == 0) return null;

        // Multi-segment elements are rare; we create one component per segment.
        // For the common case (single segment), this loop runs once.
        // If multiple segments exist, we only process the first one — the caller
        // should iterate elements, not individual segments within an element.
        var seg = segments[0];
        var visualProps = ExtractVisualProperties(seg);

        string typeName;
        Dictionary<string, object> shapeParams;

        switch (seg)
        {
            case RoundedRectangleVectorSegment rr:
                typeName = "RoundedRectangle";
                shapeParams = new Dictionary<string, object>(visualProps)
                {
                    ["RelativeX"] = element.RelativeX + rr.X,
                    ["RelativeY"] = element.RelativeY + rr.Y,
                    ["Width"] = rr.Width,
                    ["Height"] = rr.Height,
                    ["CornerRadius"] = rr.CornerRadius,
                };
                break;

            case RectangleVectorSegment r:
                typeName = "Rectangle";
                shapeParams = new Dictionary<string, object>(visualProps)
                {
                    ["RelativeX"] = element.RelativeX + r.X,
                    ["RelativeY"] = element.RelativeY + r.Y,
                    ["Width"] = r.Width,
                    ["Height"] = r.Height,
                };
                break;

            case EllipseVectorSegment e:
                typeName = "Ellipse";
                shapeParams = new Dictionary<string, object>(visualProps)
                {
                    ["RelativeX"] = element.RelativeX + e.X,
                    ["RelativeY"] = element.RelativeY + e.Y,
                    ["RadiusX"] = e.RadiusX,
                    ["RadiusY"] = e.RadiusY,
                };
                break;

            case StraightLineVectorSegment l:
                typeName = "Line";
                {
                    float lcx = (l.X1 + l.X2) / 2f;
                    float lcy = (l.Y1 + l.Y2) / 2f;
                    shapeParams = new Dictionary<string, object>(visualProps)
                    {
                        ["RelativeX"] = element.RelativeX + lcx,
                        ["RelativeY"] = element.RelativeY + lcy,
                        ["X1"] = l.X1 - lcx,
                        ["Y1"] = l.Y1 - lcy,
                        ["X2"] = l.X2 - lcx,
                        ["Y2"] = l.Y2 - lcy,
                    };
                }
                break;

            case CubicBezierVectorSegment b:
                typeName = "CubicBezier";
                {
                    float bcx = (b.X1 + b.X2 + b.X3 + b.X4) / 4f;
                    float bcy = (b.Y1 + b.Y2 + b.Y3 + b.Y4) / 4f;
                    shapeParams = new Dictionary<string, object>(visualProps)
                    {
                        ["RelativeX"] = element.RelativeX + bcx,
                        ["RelativeY"] = element.RelativeY + bcy,
                        ["X1"] = b.X1 - bcx,
                        ["Y1"] = b.Y1 - bcy,
                        ["X2"] = b.X2 - bcx,
                        ["Y2"] = b.Y2 - bcy,
                        ["X3"] = b.X3 - bcx,
                        ["Y3"] = b.Y3 - bcy,
                        ["X4"] = b.X4 - bcx,
                        ["Y4"] = b.Y4 - bcy,
                    };
                }
                break;

            case QuadraticBezierVectorSegment q:
                typeName = "QuadraticBezier";
                {
                    float qcx = (q.X1 + q.X2 + q.X3) / 3f;
                    float qcy = (q.Y1 + q.Y2 + q.Y3) / 3f;
                    shapeParams = new Dictionary<string, object>(visualProps)
                    {
                        ["RelativeX"] = element.RelativeX + qcx,
                        ["RelativeY"] = element.RelativeY + qcy,
                        ["X1"] = q.X1 - qcx,
                        ["Y1"] = q.Y1 - qcy,
                        ["X2"] = q.X2 - qcx,
                        ["Y2"] = q.Y2 - qcy,
                        ["X3"] = q.X3 - qcx,
                        ["Y3"] = q.Y3 - qcy,
                    };
                }
                break;

            case ArcVectorSegment a:
                typeName = "Arc";
                shapeParams = new Dictionary<string, object>(visualProps)
                {
                    ["RelativeX"] = element.RelativeX + a.X,
                    ["RelativeY"] = element.RelativeY + a.Y,
                    ["CenterX"] = 0f,
                    ["CenterY"] = 0f,
                    ["RadiusX"] = a.RadiusX,
                    ["RadiusY"] = a.RadiusY,
                    ["StartAngle"] = a.StartAngle,
                    ["SweepAngle"] = a.SweepAngle,
                };
                break;

            case PolygonVectorSegment p:
                typeName = "Polygon";
                {
                    var pts = p.Points;
                    float px = 0f, py = 0f;
                    if (pts is { Length: > 0 })
                    {
                        foreach (var pt in pts) { px += pt.X; py += pt.Y; }
                        px /= pts.Length;
                        py /= pts.Length;
                    }
                    shapeParams = new Dictionary<string, object>(visualProps)
                    {
                        ["RelativeX"] = element.RelativeX + px,
                        ["RelativeY"] = element.RelativeY + py,
                        ["Points"] = pts.Select(pt => new Point(pt.X - px, pt.Y - py)).ToList(),
                    };
                }
                break;

            case PolylineVectorSegment pl:
                typeName = "Polyline";
                {
                    var pts = pl.Points;
                    float px = 0f, py = 0f;
                    if (pts is { Length: > 0 })
                    {
                        foreach (var pt in pts) { px += pt.X; py += pt.Y; }
                        px /= pts.Length;
                        py /= pts.Length;
                    }
                    shapeParams = new Dictionary<string, object>(visualProps)
                    {
                        ["RelativeX"] = element.RelativeX + px,
                        ["RelativeY"] = element.RelativeY + py,
                        ["Points"] = pts.Select(pt => new Point(pt.X - px, pt.Y - py)).ToList(),
                    };
                }
                break;

            default:
                Log(null, $"Unknown SVG segment type '{seg.GetType().Name}' — skipping element.", this);
                return null;
        }

        // Apply element-level rotation and layer
        shapeParams["Rotation"] = element.Rotation;
        shapeParams["LayerIndex"] = element.LayerIndex;

        if (!handlerCache.TryGetValue(typeName, out var handler))
        {
            Log(null, $"No handler found for component type '{typeName}'.", this);
            return null;
        }

        var component = handler.Create(shapeParams);
        component.Name = $"{handler.DisplayName} {index + 1}";
        return component;
    }

    /// <summary>
    /// Extracts stroke, fill, and thickness values from a <see cref="VectorSegment"/>
    /// into a parameter dictionary suitable for <see cref="IVectorComponentHandler.Create"/>.
    /// </summary>
    private static Dictionary<string, object> ExtractVisualProperties(VectorSegment seg)
    {
        return new Dictionary<string, object>
        {
            ["StrokeR"] = (float)seg.StrokeR,
            ["StrokeG"] = (float)seg.StrokeG,
            ["StrokeB"] = (float)seg.StrokeB,
            ["StrokeA"] = seg.StrokeA,
            ["FillR"] = (float)seg.FillR,
            ["FillG"] = (float)seg.FillG,
            ["FillB"] = (float)seg.FillB,
            ["FillA"] = seg.FillA,
            ["Thickness"] = seg.Thickness,
        };
    }

    /// <summary>
    /// Wraps a list of <see cref="IVectorComponent"/>s into a <see cref="ComponentGroup"/>
    /// that provides unified movement, scaling, and rotation.
    /// Follows the same pattern as <see cref="GroupComponents"/>.
    /// </summary>
    private ComponentGroup? WrapComponentsInGroup(List<IVectorComponent> components, string groupName, bool isSVG = false, bool isImportedGroup = false, string? srcFileName = null)
    {
        if (components.Count == 0) return null;

        // Compute the union bounds of all components.
        var bounds = ComputeComponentBounds(components);
        if (!bounds.IsValid)
        {
            // Fallback to a small default centered on the first component.
            bounds = (components[0].Parameters.GetFloat("RelativeX", 0.5f),
                      components[0].Parameters.GetFloat("RelativeY", 0.5f),
                      0.1f, 0.1f, true);
        }

        float groupCenterX = bounds.relX + bounds.width / 2f;
        float groupCenterY = bounds.relY + bounds.height / 2f;

        var group = new ComponentGroup
        {
            Name = groupName,
            Index = components.Min(c => c.Index),
            IsSVG = isSVG,
            IsImportedGroup = isImportedGroup,
            SourceFile = srcFileName ?? "",
        };
        group.Parameters["RelativeX"] = groupCenterX;
        group.Parameters["RelativeY"] = groupCenterY;
        group.Parameters["Width"] = bounds.width;
        group.Parameters["Height"] = bounds.height;
        group.SetInitialBounds(groupCenterX, groupCenterY, bounds.width, bounds.height);
        group.SetChildren(components);

        return group;
    }

    // ═══════════════════════════════════════════════════════════
    // Position helpers for interactive preview
    // ═══════════════════════════════════════════════════════════

    /// <summary>
    /// Returns the animated (X,Y) position of a component at the current progress.
    /// </summary>
    public (float x, float y) GetAnimatedPosition(VectorComponentItem component)
    {
        uint duration = Math.Max(1, _clip.Duration);
        uint currentFrame = (uint)Math.Round(CurrentProgress * Math.Max(0, duration - 1));
        float localProgress = duration <= 1 ? 0f : Math.Clamp(currentFrame / (float)(duration - 1), 0f, 1f);

        float x = component.RelativeX;
        float y = component.RelativeY;

        // Apply any animation tracks targeting RelativeX / RelativeY
        foreach (var trackItem in component.Tracks)
        {
            if (trackItem.TargetFieldId == "RelativeX")
                x = trackItem.GetValue(localProgress);
            else if (trackItem.TargetFieldId == "RelativeY")
                y = trackItem.GetValue(localProgress);
        }

        return (x, y);
    }

    /// <summary>
    /// Finds the keyframe for the given field whose time is within tolerance.
    /// </summary>
    public KeyFrameItem? FindKeyFrameAtProgress(VectorComponentItem component, string fieldId, float progress)
    {
        const float tolerance = 0.015f;
        var track = component.Tracks.FirstOrDefault(t => t.TargetFieldId == fieldId);
        return track?.KeyFrames.FirstOrDefault(kf => MathF.Abs(kf.Time - progress) <= tolerance);
    }

    /// <summary>
    /// Updates component position from a drag interaction.
    /// </summary>
    public void UpdateComponentPositionFromDrag(VectorComponentItem component, float normalizedX, float normalizedY, float progress)
    {
        normalizedX = Math.Clamp(normalizedX, 0f, 1f);
        normalizedY = Math.Clamp(normalizedY, 0f, 1f);

        // ── RelativeX ──
        var xKf = FindKeyFrameAtProgress(component, "RelativeX", progress);
        if (xKf is not null)
            xKf.Value = normalizedX;
        else if (component.Tracks.Any(t => t.TargetFieldId == "RelativeX"))
            component.Tracks.First(t => t.TargetFieldId == "RelativeX").AddKeyFrame(progress, normalizedX);
        else
            component.RelativeX = normalizedX;

        // ── RelativeY ──
        var yKf = FindKeyFrameAtProgress(component, "RelativeY", progress);
        if (yKf is not null)
            yKf.Value = normalizedY;
        else if (component.Tracks.Any(t => t.TargetFieldId == "RelativeY"))
            component.Tracks.First(t => t.TargetFieldId == "RelativeY").AddKeyFrame(progress, normalizedY);
        else
            component.RelativeY = normalizedY;

        RequestPreviewRefresh();
    }

    // ── Keyframe recording from InteractableEditor drag ─────

    /// <summary>
    /// Callback fired by <see cref="InteractableEditor.NotifyKeyframeCandidateCaptured"/>
    /// when the user completes a clip drag/resize while <see cref="InteractableEditor.EnableKeyframeRecording"/>
    /// is enabled.  Records keyframes on the relevant axes (RelativeX, RelativeY,
    /// and for ComponentGroup also Width/Height) at the current timeline position.
    /// </summary>
    private void OnComponentKeyframeCandidateCaptured(string clipIdStr, uint frame, ClipPositionTuple position, InteractableEditor.InteractableEditor.ResizeHandle handle)
    {
        if (!Guid.TryParse(clipIdStr, out var clipId))
            return;

        var component = Components.FirstOrDefault(c => c.Id == clipId);
        if (component is null)
            return;

        uint duration = Math.Max(1, _clip.Duration);
        float progress = duration <= 1 ? 0f : Math.Clamp(frame / (float)(duration - 1), 0f, 1f);

        float centerX = position.TargetX + position.TargetWidth / 2f;
        float centerY = position.TargetY + position.TargetHeight / 2f;
        float normalizedX = Math.Clamp(centerX / Math.Max(1, PreviewWidth), 0f, 1f);
        float normalizedY = Math.Clamp(centerY / Math.Max(1, PreviewHeight), 0f, 1f);
        float normW = Math.Clamp(position.TargetWidth / (float)Math.Max(1, PreviewWidth), 0.001f, 4f);
        float normH = Math.Clamp(position.TargetHeight / (float)Math.Max(1, PreviewHeight), 0.001f, 4f);
        switch (handle)
        {
            case InteractableEditor.InteractableEditor.ResizeHandle.TopLeft:
            case InteractableEditor.InteractableEditor.ResizeHandle.TopRight:
            case InteractableEditor.InteractableEditor.ResizeHandle.BottomLeft:
            case InteractableEditor.InteractableEditor.ResizeHandle.BottomRight:
                if (component.Source is ComponentGroup)
                {
                    RecordOrUpdateKeyframe(component, "Width", progress, normW);
                    RecordOrUpdateKeyframe(component, "Height", progress, normH);
                }
                else
                {
                    RecordOrUpdateKeyframe(component, "BaseX", progress, normW);
                    RecordOrUpdateKeyframe(component, "BaseY", progress, normH);
                }
                break;
            case InteractableEditor.InteractableEditor.ResizeHandle.ClipPan:
                RecordOrUpdateKeyframe(component, "RelativeX", progress, normalizedX);
                RecordOrUpdateKeyframe(component, "RelativeY", progress, normalizedY);
                break;
        }

        RequestPreviewRefresh();
        InvalidateTimeline();
    }

    private const float KeyframeSearchTolerance = 0.015f;

    /// <summary>
    /// Ensures a keyframe for <paramref name="fieldId"/> exists at the given
    /// <paramref name="progress"/> with the given <paramref name="value"/>.
    /// If the component already has a track for this field the keyframe is added
    /// or updated in-place.  If no track exists a new one is auto-created with
    /// start/end keyframes pinned to the current parameter value so the component
    /// animates from its original position to the dragged position and back.
    /// </summary>
    private void RecordOrUpdateKeyframe(VectorComponentItem component, string fieldId, float progress, float value)
    {
        var track = component.Tracks.FirstOrDefault(t => t.TargetFieldId == fieldId);
        if (track is null)
        {
            // ── Auto-create a track ──────────────────────
            // AddTrack creates two keyframes (time=0, time=1) with the field's
            // midpoint default.  Overwrite them with the current parameter value
            // (the "pre-drag" position) so the animation starts/ends from where
            // the component was before the user dragged it.
            component.AddTrack(fieldId);
            track = component.Tracks.FirstOrDefault(t => t.TargetFieldId == fieldId);
            if (track is null) return;

            float oldValue = component.Source.Parameters.GetFloat(fieldId, 0.5f);
            if (track.KeyFrames.Count >= 2)
            {
                track.KeyFrames[0].Value = oldValue;
                track.KeyFrames[^1].Value = oldValue;
            }

            track.AddKeyFrame(progress, value);
        }
        else
        {
            // ── Track already exists — update or insert ──
            var existingKf = track.KeyFrames
                .FirstOrDefault(kf => MathF.Abs(kf.Time - progress) <= KeyframeSearchTolerance);
            if (existingKf is not null)
            {
                existingKf.Value = value;
            }
            else
            {
                track.AddKeyFrame(progress, value);
            }
        }
    }

    /// <summary>
    /// Builds the full list of animated elements for the current frame.
    /// </summary>
    public List<VectorCanvasElement> GetCurrentFrameAnimatedElements()
    {
        var elements = new List<VectorCanvasElement>();
        uint duration = Math.Max(1, _clip.Duration);
        uint currentFrame = (uint)Math.Round(CurrentProgress * Math.Max(0, duration - 1));

        foreach (var compItem in Components)
        {
            var comp = compItem.Source;
            float progress = duration <= 1
                ? 0f
                : Math.Clamp(currentFrame / (float)(duration - 1), 0f, 1f);
            elements.AddRange(comp.ComputeAll(progress));
        }

        return elements;
    }

    // ═══════════════════════════════════════════════════════════
    // SVG file picker
    // ═══════════════════════════════════════════════════════════

    private async Task<string?> OpenSvgFilePickerAsync()
    {
        try
        {
            var customFileType = new FilePickerFileType(
                new Dictionary<DevicePlatform, IEnumerable<string>>
                {
                    { DevicePlatform.WinUI, new[] { ".svg", ".shapes" } },
                    { DevicePlatform.Android, new[] { "image/svg+xml", "text/json", "application/json", "application/shapes" } },
                    { DevicePlatform.iOS, new[] { "public.svg-image", "public.json", "public.shapes" } },
                    { DevicePlatform.macOS, new[] { "public.svg-image", "public.json", "public.shapes" } },
                });

            var result = await FilePicker.PickAsync(new PickOptions
            {
                PickerTitle = Localized.VectorContentEditorView_SelectSvgTitle,
                FileTypes = customFileType,
            });

            return result?.FullPath;
        }
        catch (Exception ex)
        {
            Log(ex, "SVG file picker failed", this);
            return null;
        }
    }

    // ═══════════════════════════════════════════════════════════
    // Timeline tap gesture
    // ═══════════════════════════════════════════════════════════

    private void OnTimelineTapped(object? sender, TappedEventArgs e)
    {
        var pos = e.GetPosition(TimelineCanvas);
        if (pos is null) return;

        float tapX = (float)pos.Value.X;
        float tapY = (float)pos.Value.Y;

        var activeTracks = GetActiveTracksForHitTest();
        float timelineWidth = (float)Math.Max(1, TimelineCanvas.Width
            - TimelineDrawable.LeftMargin - TimelineDrawable.RightMargin);
        float contentTop = TimelineDrawable.RulerHeight + 4;
        const float hitRadius = 14f;

        for (int t = 0; t < activeTracks.Count; t++)
        {
            float trackCenterY = contentTop + t * TimelineDrawable.TrackRowHeight
                + TimelineDrawable.TrackRowHeight / 2f;

            if (Math.Abs(tapY - trackCenterY) > hitRadius) continue;

            for (int k = 0; k < activeTracks[t].KeyFrames.Count; k++)
            {
                var kf = activeTracks[t].KeyFrames[k];
                float kfX = TimelineDrawable.LeftMargin
                    + Math.Clamp(kf.Time, 0f, 1f) * timelineWidth;

                if (Math.Abs(tapX - kfX) <= hitRadius && Math.Abs(tapY - trackCenterY) <= hitRadius)
                {
                    SelectedTrack = activeTracks[t];
                    SelectedKeyFrame = kf;
                    return;
                }
            }
        }

        if (SelectedTrack is null) return;

        float time = (float)Math.Clamp(
            (tapX - TimelineDrawable.LeftMargin) / timelineWidth, 0f, 1f);
        float value = SelectedTrack.GetValue(time);
        SelectedTrack.AddKeyFrame(time, value);
        SelectedKeyFrame = null;
        InvalidateTimeline();
    }

    private ObservableCollection<AnimationTrackItem> GetActiveTracksForHitTest()
    {
        return SelectedComponent?.Tracks ?? new();
    }

    // ═══════════════════════════════════════════════════════════
    // InteractableEditor integration
    // ═══════════════════════════════════════════════════════════

    private uint GetCurrentFrameNumber()
    {
        uint duration = Math.Max(1, _clip.Duration);
        return (uint)Math.Round(CurrentProgress * (duration - 1));
    }

    private void RebuildComponentClips()
    {
        // Apply HasDefaultHandles from component handlers to control default corner handle visibility.
        // When a handler's HasDefaultHandles is false, the four corner resize handles are hidden,
        // allowing the component's custom shape handles to be the sole interaction affordances.
        var handlerCache = EnsureHandlerCache();

        int canvasW = Math.Max(1, PreviewWidth);
        int canvasH = Math.Max(1, PreviewHeight);
        _componentClips = Components
            .Select(vm =>
            {
                var cc = new VectorComponentWrapperClip(vm.Source)
                {
                    ParentCanvasWidth = canvasW,
                    ParentCanvasHeight = canvasH,
                    DurationInFrames = vm.EditorDurationInFrames,
                    CachedSvgElements = vm.EditorCachedElements,
                };
                cc.EffectsInstances =
                [
                    new DynamicPositionProviderEffect(i =>
                        cc.TryComputeAnimatedFrameBounds(i, Math.Max(1, _clip.Duration), out var animatedBounds)
                            ? animatedBounds
                            : new ClipPositionTuple(cc.TargetX, cc.TargetY, cc.TargetWidth, cc.TargetHeight, false))
                ];
                cc.ExtraData["ShowDefaultBorder"] = GetHandlerForType(vm.Source.TypeName)?.HasDefaultHandles ?? true;
                cc.SyncFromDefinition();
                return cc;
            })
            .ToList();

        _clipElementUIs = VectorComponentWrapperClip.ToClipElementUIDictionary(_componentClips, ui => { ui.ShowDefaultBorder = Convert.ToBoolean(ui.ExtraData.TryGetValue("ShowDefaultBorder", out var value) ? value : true); });

        foreach (var (id, ui) in _clipElementUIs)
        {
            var component = Components.FirstOrDefault(c => c.Id == id);
            if (component is null) continue;

            if (handlerCache.TryGetValue(component.TypeName, out var handler) && !handler.HasDefaultHandles)
            {
                ui.IsHorizontalResizable = false;
                ui.IsVerticalResizable = false;
            }
            else if (handler is TextComponentHandler textHandler
                     && component.Source is TextComponent textComp)
            {
                var (h, v) = TextComponentHandler.GetResizability(textComp);
                ui.IsHorizontalResizable = h;
                ui.IsVerticalResizable = v;
            }
        }

        InteractiveEditor.SetCurrentFrame(GetCurrentFrameNumber());
        InteractiveEditor.SetClipsFromDraftPage(_clipElementUIs, 1.0);

        SubscribeToComponentPropertyChanges();

        InteractiveEditor.SetCurrentFrame(GetCurrentFrameNumber());
        SyncSelectedComponentToEditor();
        _ = RefreshInteractivePreviewsAsync();
    }

    private async Task OnInteractiveEditorChanged()
    {
        _suppressComponentPropertySync = true;
        try
        {
            foreach (var (id, ui) in _clipElementUIs)
            {
                var cc = _componentClips.FirstOrDefault(c => c.Id == id);
                if (cc is null) continue;

                VectorComponentWrapperClip.SyncToComponentClip(ui, cc);
                cc.SyncToDefinition();
                cc.SyncLayerToDefinition();
            }

            RequestPreviewRefresh();
        }
        finally
        {
            _suppressComponentPropertySync = false;
        }

        await Task.CompletedTask;
    }

    private Task OnComponentClipTapped(Guid clipId)
    {
        MainThread.BeginInvokeOnMainThread(() =>
        {
            var item = Components.FirstOrDefault(c => c.Id == clipId);
            if (item is not null)
                SelectedComponent = item;
        });

        return Task.CompletedTask;
    }

    private Task OnBlankAreaTapped()
    {
        MainThread.BeginInvokeOnMainThread(() =>
        {
            SelectedComponent = null;
        });

        return Task.CompletedTask;
    }

    private async Task OnPreviewRefreshRequested()
    {
        await RefreshInteractivePreviewsAsync();
    }

    private IClip? GetClipInstanceForEditor(ClipElementUI ui)
    {
        return _componentClips.FirstOrDefault(c => c.Id == ui.Id);
    }

    private void SyncSelectedComponentToEditor()
    {
        InteractiveEditor.SelectClip(SelectedComponent?.Id);
    }

    // ── Property sync: right panel → VectorComponentWrapperClip → InteractableEditor ──

    private void SubscribeToComponentPropertyChanges()
    {
        foreach (var item in Components)
        {
            SubscribeToSingleComponent(item);
        }
    }

    private void SubscribeToSingleComponent(VectorComponentItem item)
    {
        item.PropertyChanged += (_, e) =>
        {
            if (_suppressComponentPropertySync) return;

            if (e.PropertyName is nameof(VectorComponentItem.RelativeX)
                or nameof(VectorComponentItem.RelativeY)
                or nameof(VectorComponentItem.LayerIndex))
            {
                var cc = _componentClips.FirstOrDefault(c => c.Id == item.Id);
                if (cc is not null)
                {
                    cc.SyncFromDefinition();
                    if (_clipElementUIs.TryGetValue(item.Id, out var ui))
                    {
                        ui.TargetX = cc.TargetX;
                        ui.TargetY = cc.TargetY;
                        ui.TargetWidth = cc.TargetWidth;
                        ui.TargetHeight = cc.TargetHeight;
                        ui.SubLayerIndex = (int)cc.LayerIndex;
                    }
                    InteractiveEditor.UpdateClips(
                        new System.Collections.Concurrent.ConcurrentDictionary<Guid, ClipElementUI>(_clipElementUIs));
                }
            }
        };
    }

    // ── Direct preview generation ──

    private async Task RefreshInteractivePreviewsAsync()
    {
        try
        {
            uint frame = GetCurrentFrameNumber();
            uint duration = Math.Max(1, _clip.Duration);
            int canvasW = Math.Max(1, PreviewWidth);
            int canvasH = Math.Max(1, PreviewHeight);

            var previews = new projectFrameCut.ApplicationAPIBase.Interaction.PreparedPreview[_componentClips.Count];

            for (int i = 0; i < _componentClips.Count; i++)
            {
                var cc = _componentClips[i];
                int viewportX = cc.TargetX;
                int viewportY = cc.TargetY;
                int viewportW = Math.Max(1, cc.TargetWidth);
                int viewportH = Math.Max(1, cc.TargetHeight);

                if (cc.TryComputeAnimatedFrameBounds(frame, duration, out var animatedBounds))
                {
                    viewportX = animatedBounds.TargetX;
                    viewportY = animatedBounds.TargetY;
                    viewportW = Math.Max(1, animatedBounds.TargetWidth);
                    viewportH = Math.Max(1, animatedBounds.TargetHeight);
                }

                var elements = VectorComponentWrapperClip.ComputeAnimatedElements(cc, frame, duration);

                if (elements is null || elements.Count == 0)
                {
                    previews[i] = new projectFrameCut.ApplicationAPIBase.Interaction.PreparedPreview(cc.Id, null, Localized.VectorContentEditorView_NoComponentsPreview, cc);
                    continue;
                }

                // ── Rasterization fallback for large ComponentGroups ────────────────
                // When a ComponentGroup contains many child components, converting every
                // segment to a MAUI Path object can overload the GPU renderer or exceed
                // platform path-count limits.  Instead we rasterise the whole set of
                // elements to a single bitmap and display an Image view.
                bool shouldRasterize = cc.Component is ComponentGroup group &&
                    group.Children.Count >= DynamicPreview.GroupRasterizationChildThreshold;

                if (shouldRasterize)
                {
                    previews[i] = new projectFrameCut.ApplicationAPIBase.Interaction.PreparedPreview(
                        cc.Id,
                        () => DynamicPreview.BuildRasterizedGroupPreviewView(
                            elements,
                            canvasW, canvasH,
                            viewportX, viewportY,
                            viewportW, viewportH),
                        errorMessage: null,
                        source: cc);
                }
                else
                {
                    previews[i] = new projectFrameCut.ApplicationAPIBase.Interaction.PreparedPreview(
                        cc.Id,
                        () => DynamicPreview.BuildViewportVectorPreviewView(
                            elements,
                            canvasW, canvasH,
                            viewportX, viewportY,
                            viewportW, viewportH),
                        errorMessage: null,
                        source: cc);
                }
            }

            await InteractiveEditor.UpdateClips(
                new System.Collections.Concurrent.ConcurrentDictionary<Guid, ClipElementUI>(_clipElementUIs));
            await InteractiveEditor.ApplyPreparedPreviewsAsync(previews);
        }
        catch (Exception ex)
        {
            Log(ex, "Failed to refresh interactive previews", this);
            throw;
        }
    }

    // ═══════════════════════════════════════════════════════════
    // Shape handle integration — delegates to IVectorComponentHandler
    // ═══════════════════════════════════════════════════════════

    /// <summary>
    /// Frozen snapshot of the gesture reference frame, captured once at
    /// <see cref="GestureStatus.Started"/> and read-only for the rest of the gesture.
    /// All coordinates are recorded at gesture start so that live layout changes
    /// during drag (RC3) and repeated re-queries of the component clip (RC5) do not
    /// destabilise the gesture calculation.
    /// </summary>
    private class HandleDragOrigin
    {
        /// <summary>Clip-local normalised position of the handle at gesture start (no clamp).</summary>
        public float ClipX { get; init; }
        public float ClipY { get; init; }

        /// <summary>Frozen element-local → canvas-pixel transform (from TryGetElementTransform).</summary>
        public float OriginX { get; init; }
        public float OriginY { get; init; }
        public float ScaleX { get; init; }
        public float ScaleY { get; init; }

        /// <summary>Frozen clip bounding box in canvas pixels (from ComponentClip.Target*).</summary>
        public float TargetX { get; init; }
        public float TargetY { get; init; }
        public float TargetWidth { get; init; }
        public float TargetHeight { get; init; }

        /// <summary>
        /// Frozen screen-layout dimensions captured from
        /// <see cref="ShapeHandleDragContext.DisplayW"/> / <see cref="ShapeHandleDragContext.DisplayH"/>
        /// at gesture start.  These stay constant for the whole gesture, fixing RC3.
        /// </summary>
        public double DisplayW { get; init; }
        public double DisplayH { get; init; }

        /// <summary>
        /// The total change in the X direction since the beginning of the gesture.
        /// </summary>
        public float TotalPanX { get; set; }
        /// <summary>
        /// The total change in the Y direction since the beginning of the gesture.
        /// </summary>
        public float TotalPanY { get; set; }
    }

    private readonly Dictionary<ComponentHandleIdentifier, HandleDragOrigin> _handleDragOrigins = new();
    private Dictionary<string, IVectorComponentHandler>? _handlerCache;

    private static readonly Dictionary<ShapeHandlePositionType, Color> HandleColors = new()
    {
        [ShapeHandlePositionType.Anchor] = Color.FromRgba(255, 152, 0, 230),
        [ShapeHandlePositionType.Control] = Color.FromRgba(255, 235, 59, 230),
        [ShapeHandlePositionType.Radius] = Color.FromRgba(0, 188, 212, 230),
        [ShapeHandlePositionType.Center] = Color.FromRgba(244, 67, 54, 230),
        [ShapeHandlePositionType.Angle] = Color.FromRgba(233, 30, 99, 230),
        [ShapeHandlePositionType.Corner] = Color.FromRgba(0, 150, 136, 230),
    };

    private static readonly Dictionary<ShapeHandlePositionType, double> HandleSizes = new()
    {
        [ShapeHandlePositionType.Anchor] = 14,
        [ShapeHandlePositionType.Control] = 12,
        [ShapeHandlePositionType.Radius] = 12,
        [ShapeHandlePositionType.Center] = 14,
        [ShapeHandlePositionType.Angle] = 12,
        [ShapeHandlePositionType.Corner] = 12,
    };

    /// <summary>
    /// Provides shape-specific handles for a clip by delegating to the
    /// component's registered <see cref="IVectorComponentHandler"/>.
    /// </summary>
    private IReadOnlyList<ShapeHandleDescriptor> ProvideHandlesViaHandler(Guid clipId)
    {
        var component = Components.FirstOrDefault(c => c.Id == clipId);
        if (component is null || !component.IsShapeEditable)
            return Array.Empty<ShapeHandleDescriptor>();

        var handler = GetHandlerForType(component.TypeName);
        if (handler is null)
            return Array.Empty<ShapeHandleDescriptor>();

        var apiHandles = handler.CreateHandles(component.Source);
        if (apiHandles is null || apiHandles.Count == 0)
            return Array.Empty<ShapeHandleDescriptor>();

        var result = new List<ShapeHandleDescriptor>(apiHandles.Count);
        foreach (var apiHandle in apiHandles)
        {
            // Contract: Handler coordinates are always element-local.
            // Use the existing ElementToClipLocal for the rendering path.
            var (clipX, clipY) = ElementToClipLocal(component, apiHandle.NormalizedX, apiHandle.NormalizedY);
            result.Add(new ShapeHandleDescriptor
            {
                Id = apiHandle.Id,
                // [0,1] clamp here is only a rendering safety net (keeps handles
                // visually inside the viewport) — it has no effect on gesture math.
                NormalizedX = Math.Clamp(clipX, 0f, 1f),
                NormalizedY = Math.Clamp(clipY, 0f, 1f),
                FillColor = HandleColors.GetValueOrDefault(apiHandle.PositionType, HandleColors[ShapeHandlePositionType.Anchor]),
                Size = HandleSizes.GetValueOrDefault(apiHandle.PositionType, 12),
                HandleGetter = apiHandle.CustomHandleFactory
            });
        }
        return result;
    }

    /// <summary>
    /// Handles shape handle drag gestures by delegating to the component's
    /// registered <see cref="IVectorComponentHandler"/>.
    /// </summary>
    private void OnHandlerDrag(Guid clipId, string handleId, PanUpdatedEventArgs e, ShapeHandleDragContext context)
    {
        var component = Components.FirstOrDefault(c => c.Id == clipId);
        if (component is null || !component.IsShapeEditable) return;

        var handler = GetHandlerForType(component.TypeName);
        if (handler is null) return;

        switch (e.StatusType)
        {
            case GestureStatus.Started:
                RecordDragOriginFromHandler(component, handleId, handler, context);
                break;
            case GestureStatus.Running:
                ComputeAndApplyHandlerDrag(component, handleId, e.TotalX, e.TotalY, handler, true);
                break;
            case GestureStatus.Completed:
            case GestureStatus.Canceled:
                ComputeAndApplyHandlerDrag(component, handleId, double.PositiveInfinity, double.PositiveInfinity, handler, false); // in .NET MAUI, PanUpdatedEventArgs.TotalX and TotalY are ZERO at Completed/Canceled, so give a impossible value to avoid misuse of the PanUpdatedEventArgs's value
                _handleDragOrigins.Remove((clipId, handleId));
                SyncComponentClipLayoutToEditor(component);
                RequestComponentClipsRebuild();
                _ = RefreshInteractivePreviewsAsync();
                break;
        }
    }

    /// <summary>
    /// Freeze the entire gesture reference frame at <see cref="GestureStatus.Started"/>.
    /// All transforms and layout dimensions are captured once and never re-read during
    /// the gesture
    /// </summary>
    private void RecordDragOriginFromHandler(
        VectorComponentItem comp, string handleId, IVectorComponentHandler handler, ShapeHandleDragContext ctx)
    {
        var handles = handler.CreateHandles(comp.Source);
        var h = handles?.FirstOrDefault(x => x.Id == handleId);
        if (h is null) return;

        var cc = _componentClips.FirstOrDefault(c => c.Id == comp.Id);
        if (cc is null) return;
        if (!TryGetElementTransform(comp, out var originX, out var originY, out var scaleX, out var scaleY))
            return;

        float targetW = Math.Max(1f, cc.TargetWidth);
        float targetH = Math.Max(1f, cc.TargetHeight);

        // Contract: Handler coordinates are element-local.
        // Convert to clip-local using the frozen transform.
        float canvasX = originX + h.NormalizedX * scaleX;
        float canvasY = originY + h.NormalizedY * scaleY;
        float clipX = (canvasX - cc.TargetX) / targetW;
        float clipY = (canvasY - cc.TargetY) / targetH;

        _handleDragOrigins[(comp.Id, handleId)] = new HandleDragOrigin
        {
            ClipX = clipX,
            ClipY = clipY,
            OriginX = originX,
            OriginY = originY,
            ScaleX = scaleX,
            ScaleY = scaleY,
            TargetX = cc.TargetX,
            TargetY = cc.TargetY,
            TargetWidth = targetW,
            TargetHeight = targetH,
            DisplayW = ctx.DisplayW,
            DisplayH = ctx.DisplayH,
        };
    }

    /// <summary>
    /// Compute and apply a handle drag delta using ONLY the frozen
    /// <see cref="HandleDragOrigin"/> data.  No live re-queries of layout,
    /// clip bounds, or element transform are performed, fixing RC3 and RC5.
    /// The two previous [0,1] clamps on the clip-local and element-local
    /// targets have been removed — range limiting is now solely the
    /// responsibility of each <see cref="IVectorComponentHandler.ApplyHandleDrag"/>
    /// implementation.
    /// </summary>
    private void ComputeAndApplyHandlerDrag(
        VectorComponentItem comp, string handleId,
        double totalX, double totalY,
        IVectorComponentHandler handler, bool isLive)
    {
        if (!_handleDragOrigins.TryGetValue((comp.Id, handleId), out var origin))
            return;

        if (isLive)
        {
            // Use the frozen DisplayW/H to convert accumulated pixel displacement
            // into a clip-local delta.
            float deltaClipX = origin.DisplayW > 0 ? (float)(totalX / origin.DisplayW) : 0f;
            float deltaClipY = origin.DisplayH > 0 ? (float)(totalY / origin.DisplayH) : 0f;

            // No [0,1] clamp — clip-local target may exceed [0,1] when the handle
            // is expanding the bounding box (essential for Anchor-type handles on
            // Polygon/Polyline/Bezier). 
            float targetClipX = origin.ClipX + deltaClipX;
            float targetClipY = origin.ClipY + deltaClipY;

            // clip-local → element-local using the frozen origin/scale/bbox.
            float canvasX = origin.TargetX + targetClipX * origin.TargetWidth;
            float canvasY = origin.TargetY + targetClipY * origin.TargetHeight;
            float newX = origin.ScaleX > 0.000001f ? (canvasX - origin.OriginX) / origin.ScaleX : targetClipX;
            float newY = origin.ScaleY > 0.000001f ? (canvasY - origin.OriginY) / origin.ScaleY : targetClipY;

            origin.TotalPanX = newX;
            origin.TotalPanY = newY;
            if ((GetHandlerForType(comp.TypeName)?.HasDefaultHandles ?? false) || ShowAllComponentBounds) // Keep overlay bounds in sync with edited geometry
            {
                // No extra [0,1] clamp on newX/newY — range limiting is delegated to
                // each Handler's ApplyHandleDrag (which already uses Math.Clamp /
                // Math.Max tailored to the shape).
                handler.ApplyHandleDrag(comp.Source, handleId, newX, newY, isLive);

                SyncComponentClipLayoutToEditor(comp);
                _ = RefreshInteractivePreviewsAsync();
            }
        }
        else
        {
            handler.ApplyHandleDrag(comp.Source, handleId, origin.TotalPanX, origin.TotalPanY, isLive);
            SyncComponentClipLayoutToEditor(comp);
            _ = RefreshInteractivePreviewsAsync();
        }


    }

    /// <summary>
    /// Gets (or lazily creates) the handler cache for all registered component types.
    /// </summary>
    private Dictionary<string, IVectorComponentHandler> EnsureHandlerCache()
    {
        if (_handlerCache is null)
        {
            _handlerCache = new Dictionary<string, IVectorComponentHandler>();
            foreach (var (tn, factory) in VectorComponentHandlerServices.GetAvailableHandlers())
            {
                _handlerCache[tn] = factory();
            }
        }
        return _handlerCache;
    }

    private IVectorComponentHandler? GetHandlerForType(string typeName)
    {
        return EnsureHandlerCache().GetValueOrDefault(typeName);
    }

    /// <summary>
    /// Maps an element-local point to clip-local normalized coordinates [0..1]
    /// using the same origin/scale transform as DynamicPreview.BuildViewportVectorPreviewView.
    /// </summary>
    private (float x, float y) ElementToClipLocal(VectorComponentItem component, float localX, float localY)
    {
        var cc = _componentClips.FirstOrDefault(c => c.Id == component.Id);
        if (cc is null) return (localX, localY);
        if (!TryGetElementTransform(component, out var originX, out var originY, out var scaleX, out var scaleY))
            return (localX, localY);

        float canvasX = originX + localX * scaleX;
        float canvasY = originY + localY * scaleY;

        return (
            (canvasX - cc.TargetX) / Math.Max(1, cc.TargetWidth),
            (canvasY - cc.TargetY) / Math.Max(1, cc.TargetHeight)
        );
    }

    private bool TryGetElementTransform(
        VectorComponentItem component,
        out float originX,
        out float originY,
        out float scaleX,
        out float scaleY)
    {
        originX = originY = 0f;
        scaleX = scaleY = 1f;

        // Groups do not have a single element transform for shape handles.
        if (component.Source is ComponentGroup)
        {
            return false;
        }

        float canvasW = Math.Max(1, PreviewWidth);
        float canvasH = Math.Max(1, PreviewHeight);
        uint frame = GetCurrentFrameNumber();
        uint duration = Math.Max(1, _clip.Duration);
        float progress = duration <= 1
            ? 0f
            : Math.Clamp(frame / (float)(duration - 1), 0f, 1f);

        var element = component.Source.Compute(progress);
        if (element is null)
        {
            return false;
        }

        if (element.UseUniformScale)
        {
            float uniform = MathF.Min(canvasW, canvasH);
            scaleX = uniform;
            scaleY = uniform;
            originX = element.BaseX * canvasW + element.RelativeX * uniform;
            originY = element.BaseY * canvasH + element.RelativeY * uniform;
        }
        else
        {
            scaleX = canvasW;
            scaleY = canvasH;
            originX = element.RelativeX * canvasW;
            originY = element.RelativeY * canvasH;
        }

        return true;
    }

    private void SyncComponentClipLayoutToEditor(VectorComponentItem component)
    {
        var cc = _componentClips.FirstOrDefault(c => c.Id == component.Id);
        if (cc is null)
            return;

        cc.SyncFromDefinition();
        if (_clipElementUIs.TryGetValue(component.Id, out var ui))
        {
            ui.TargetX = cc.TargetX;
            ui.TargetY = cc.TargetY;
            ui.TargetWidth = cc.TargetWidth;
            ui.TargetHeight = cc.TargetHeight;
        }

        _ = InteractiveEditor.UpdateClips(
            new System.Collections.Concurrent.ConcurrentDictionary<Guid, ClipElementUI>(_clipElementUIs));
    }

    // ═══════════════════════════════════════════════════════════
    // Dynamic property panel — delegates to IVectorComponentHandler
    // ═══════════════════════════════════════════════════════════

    private Layout? _currentDynamicPanel;
    private PropertyPanelBuilder? _currentPropertyBuilder;

    /// <summary>
    /// Rebuilds the dynamic property panel from the currently selected component's handler.
    /// Called whenever <see cref="SelectedComponent"/> changes.
    /// </summary>
    private void RebuildDynamicPropertyPanel()
    {
        // Clean up old panel
        if (_currentDynamicPanel is not null)
        {
            DynamicPropertyContainer.Children.Clear();
            _currentDynamicPanel = null;
        }

        if (_currentPropertyBuilder is not null)
        {
            _currentPropertyBuilder.PropertyChanged -= OnPropertyPanelChanged;
            _currentPropertyBuilder = null;
        }

        var component = SelectedComponent;
        if (component is null || !component.IsShapeEditable) return;

        PropertyPanelBuilder builder;
        if (component.Source is ComponentGroup group)
        {
            builder = CreateGroupPropertyPanelBuilder(group);
        }
        else
        {
            var handler = GetHandlerForType(component.TypeName);
            if (handler is null) return;
            builder = handler.CreatePropertyUI(component.Source);
        }

        _currentPropertyBuilder = builder;
        builder.PropertyChanged += OnPropertyPanelChanged;

        var view = builder.Build();
        _currentDynamicPanel = view;
        MainThread.BeginInvokeOnMainThread(() =>
        {
            DynamicPropertyContainer.Children.Clear();
            DynamicPropertyContainer.Children.Add(view);
        });
    }

    private static PropertyPanelBuilder CreateGroupPropertyPanelBuilder(ComponentGroup group)
    {
        var builder = new PropertyPanelBuilder();
        builder.AddCollapsibleSection(Localized.VectorContentEditorView_Components_GroupProperties, b =>
        {
            b.AddSlider("RelativeX", "X:", 0.0, 1.0, group.Parameters.GetFloat("RelativeX", 0.5f),
                eventCallMode: SliderUpdateEventCallMode.OnValueChanged);
            b.AddSlider("RelativeY", "Y:", 0.0, 1.0, group.Parameters.GetFloat("RelativeY", 0.5f),
                eventCallMode: SliderUpdateEventCallMode.OnValueChanged);
            b.AddSlider("Width", Localized.VectorContentEditorView_Property_Width, 0.0, 2.0, group.Parameters.GetFloat("Width", 0.3f),
                eventCallMode: SliderUpdateEventCallMode.OnValueChanged);
            b.AddSlider("Height", Localized.VectorContentEditorView_Property_Height, 0.0, 2.0, group.Parameters.GetFloat("Height", 0.3f),
                eventCallMode: SliderUpdateEventCallMode.OnValueChanged);
            b.AddSlider("Rotation", Localized.VectorContentEditorView_Property_Rotation, -3.1416, 3.1416, group.Parameters.GetFloat("Rotation", 0.0f),
                eventCallMode: SliderUpdateEventCallMode.OnValueChanged);
        }, defaultExpanded: true);
        return builder;
    }

    /// <summary>
    /// Handles property changes from the dynamic property panel.
    /// Delegates to the handler's <see cref="IVectorComponentHandler.HandlePropertyChange"/>
    /// and refreshes the preview and component clips.
    /// </summary>
    private void OnPropertyPanelChanged(object? sender, PropertyPanelPropertyChangedEventArgs args)
    {
        var component = SelectedComponent;
        if (component is null) return;

        if (component.Source is ComponentGroup group)
        {
            group.Parameters[args.Id] = args.Value ?? 0f;
        }
        else
        {
            var handler = GetHandlerForType(component.TypeName);
            if (handler is null) return;
            handler.HandlePropertyChange(component.Source, args);
        }

        RequestPreviewRefresh();
        RequestComponentClipsRebuild();
    }

    // ═══════════════════════════════════════════════════════════
    // INotifyPropertyChanged
    // ═══════════════════════════════════════════════════════════

    public event PropertyChangedEventHandler? PropertyChanged;

    protected override void OnPropertyChanged([CallerMemberName] string? propertyName = null)
    {
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
    }

    internal record struct ComponentHandleIdentifier(Guid clipId, string handleId)
    {
        public static implicit operator (Guid clipId, string handleId)(ComponentHandleIdentifier value)
        {
            return (value.clipId, value.handleId);
        }

        public static implicit operator ComponentHandleIdentifier((Guid clipId, string handleId) value)
        {
            return new ComponentHandleIdentifier(value.clipId, value.handleId);
        }
    }

    // ═══════════════════════════════════════════════════════════
    // Default MDI layout (matches the drawn boxes in the design)
    // ═══════════════════════════════════════════════════════════

    private bool _defaultLayoutApplied;

    /// <summary>
    /// Snap each <see cref="MultiWindowItem"/> into the layout shown by the design
    /// mockup: Components on the left, Editor on the upper middle, Timeline on the
    /// lower middle, Properties on the upper right, Animation Tracks on the lower
    /// right.  Applied once after the <see cref="MainMultiWindowView"/> has a
    /// non-zero size so the windows land in the same positions on every launch.
    /// </summary>
    private void ApplyDefaultMultiWindowLayout()
    {
        if (_defaultLayoutApplied) return;
        if (MainMultiWindowView.Width <= 0 || MainMultiWindowView.Height <= 0) return;
        if (!MainMultiWindowView.Children.Contains(ComponentsWindow) ||
            !MainMultiWindowView.Children.Contains(AddComponentWindow) ||
            !MainMultiWindowView.Children.Contains(EditorWindow) ||
            !MainMultiWindowView.Children.Contains(TimelineWindow) ||
            !MainMultiWindowView.Children.Contains(PropertiesWindow) ||
            !MainMultiWindowView.Children.Contains(AnimationTracksWindow))
        {
            return;
        }

        const double Gap = 4;
        const double EditorRatio = 0.62;   // Editor takes ~62% of vertical space
        const double LeftRatio = 0.20;     // Left rail takes ~20% of width
        const double RightRatio = 0.24;    // Right rail takes ~24% of width

        double totalW = MainMultiWindowView.Width;
        double totalH = MainMultiWindowView.Height;

        double leftW = Math.Max(220, Math.Round(totalW * LeftRatio));
        double rightW = Math.Max(260, Math.Round(totalW * RightRatio));
        double centerW = Math.Max(200, totalW - leftW - rightW - Gap * 2);

        double editorH = Math.Max(160, Math.Round(totalH * EditorRatio) - Gap);
        double timelineH = Math.Max(180, totalH - editorH - Gap);
        double rightH = Math.Max(160, Math.Round((totalH - Gap) / 2.0));

        // Left rail — existing components and new component selector.
        double leftComponentsH = Math.Max(180, Math.Round((totalH - Gap) * 0.62));
        double leftAddComponentH = Math.Max(140, totalH - leftComponentsH - Gap);
        SetWindowBounds(ComponentsWindow, 0, 0, leftW, leftComponentsH);
        SetWindowBounds(AddComponentWindow, 0, leftComponentsH + Gap, leftW, leftAddComponentH);

        // Center top — editor
        SetWindowBounds(EditorWindow,
            leftW + Gap, 0,
            centerW, editorH);

        // Yellow box — timeline (center bottom)
        SetWindowBounds(TimelineWindow,
            leftW + Gap, editorH + Gap,
            centerW, timelineH);

        // Green box — properties (right top)
        SetWindowBounds(PropertiesWindow,
            leftW + Gap + centerW + Gap, 0,
            rightW, rightH);

        // Blue box — animation tracks (right bottom)
        SetWindowBounds(AnimationTracksWindow,
            leftW + Gap + centerW + Gap, rightH + Gap,
            rightW, Math.Max(160, totalH - rightH - Gap));

        // Lift Properties on top so the user sees the property editor first.
        MainMultiWindowView.BringToFront(PropertiesWindow);

        _defaultLayoutApplied = true;
    }

    private static void SetWindowBounds(MultiWindowItem item, double x, double y, double width, double height)
    {
        if (item is null) return;

        item.HorizontalOptions = LayoutOptions.Start;
        item.VerticalOptions = LayoutOptions.Start;
        item.Margin = new Thickness(0);
        item.TranslationX = x;
        item.TranslationY = y;
        item.WidthRequest = Math.Max(160, width);
        item.HeightRequest = Math.Max(120, height);
    }
}
