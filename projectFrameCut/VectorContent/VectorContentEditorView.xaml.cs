using projectFrameCut.ApplicationAPIBase.Helpers;
using projectFrameCut.ApplicationAPIBase.Views.PropertyPanelBuilders;
using IVectorComponentHandler = projectFrameCut.ApplicationAPIBase.VectorComponentHandler.IVectorComponentHandler;
using ShapeHandlePositionType = projectFrameCut.ApplicationAPIBase.VectorComponentHandler.ShapeHandlePositionType;
using projectFrameCut.Drawing.Base;
using projectFrameCut.Services;
using projectFrameCut.Drawing.Vector;
using projectFrameCut.Drawing.Vector.ImportExport;
using projectFrameCut.InteractableEditor;
using projectFrameCut.Render.ClipsAndTracks;
using projectFrameCut.Render.RenderAPIBase.ClipAndTrack;
using projectFrameCut.Render.RenderAPIBase.VectorContent;
using projectFrameCut.Render.VectorContent;
using projectFrameCut.Shared;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Windows.Input;
using Color = Microsoft.Maui.Graphics.Color;
using IDispatcher = Microsoft.Maui.Dispatching.IDispatcher;
using System.Diagnostics;

namespace projectFrameCut.DraftStuff;

/// <summary>
/// Vector content editor — MVU-style self-contained page.
/// Manages component list, shape parameters, animation tracks, and interactive preview.
/// </summary>
public partial class VectorContentEditorView : ContentView, INotifyPropertyChanged
{
    // ═══════════════════════════════════════════════════════════
    // Injected state
    // ═══════════════════════════════════════════════════════════

    private readonly VectorCanvasClip _clip;
    private VectorPicture? _sourcePicture;
    private readonly IDispatcher _dispatcher;
    private readonly Func<Task<string?>>? _pickSvgFile;

    private List<IVectorComponent> _editingComponents = new();
    private List<IVectorComponent> _componentsBackup = new();
    private TimelineDrawable _timelineDrawable = null!;

    // ── InteractableEditor integration ──────────────────────
    private List<VectorComponentWrapperClip> _componentClips = new();
    private Dictionary<Guid, ClipElementUI> _clipElementUIs = new();
    private bool _suppressComponentPropertySync;

    // ═══════════════════════════════════════════════════════════
    // Constructors
    // ═══════════════════════════════════════════════════════════

    /// <summary>Parameterless constructor required by XAML parser.</summary>
    public VectorContentEditorView()
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
            [new DynamicPreview.PreparedPreview(Guid.Empty, () => new Label { Text = errorText, HorizontalOptions = LayoutOptions.Center, VerticalOptions = LayoutOptions.Center }, null, null)]);

    }

    /// <summary>
    /// Create the editor for the given <paramref name="clip"/>.
    /// Works for both SVG-backed and composition-only clips.
    /// </summary>
    public VectorContentEditorView(VectorCanvasClip clip, int projectWidth, int projectHeight) : this()
    {
        _clip = clip ?? throw new ArgumentNullException(nameof(clip));
        _sourcePicture = clip.SourcePicture;
        _dispatcher = Dispatcher;
        _pickSvgFile = OpenSvgFilePickerAsync;

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

        InteractiveEditor.ConfigureOverlayClipTap(OnComponentClipTapped)
                         .ConfigureBlankAreaTap(OnBlankAreaTapped)
                         .ConfigurePreviewRefresh(OnPreviewRefreshRequested)
                         .ConfigureGetClipInstanceCallback(GetClipInstanceForEditor)
                         .ConfigureCustomHandles(ProvideHandlesViaHandler, OnHandlerDrag)
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
        };

        // ── Subscribe to individual component property changes (right panel → editor) ──
        SubscribeToComponentPropertyChanges();

        // ── Initialize state ──
        InitializeState();

        // ── Complete view setup ──
        BindingContext = this;
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

    /// <summary>All available field IDs for the track property picker.</summary>
    public List<string> AvailableFieldIds { get; } = new();

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
                field = value;
                SelectedTrack = value?.Tracks.FirstOrDefault();
                SelectedKeyFrame = null;
                OnPropertyChanged();
                OnPropertyChanged(nameof(HasSelectedComponent));
                OnPropertyChanged(nameof(CanAddTrack));
                OnPropertyChanged(nameof(CanRemoveTrack));
                OnPropertyChanged(nameof(CanAddKeyFrame));
                OnPropertyChanged(nameof(Tracks));
                _timelineInvalidateAction?.Invoke();
            }
        }
    }

    public bool HasSelectedComponent => SelectedComponent is not null;

    /// <summary>Whether there is an SVG source picture with elements.</summary>
    public bool HasSvgSource => _sourcePicture is { Elements.Count: > 0 };

    /// <summary>Whether this is a composition-only clip (no SVG file).</summary>
    public bool IsCompositionMode => _sourcePicture is null;

    /// <summary>Whether legacy SVG source elements exist.</summary>
    public bool HasLegacySvgSource => _sourcePicture is { Elements.Count: > 0 } && !IsCompositionMode;

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
    public ICommand ImportSvgCommand { get; private set; } = null!;
    public ICommand DeleteKeyFrameCommand { get; private set; } = null!;

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
        ImportSvgCommand = new Command(async () => await ImportSvg());
        DeleteKeyFrameCommand = new Command(DeleteKeyFrame);
    }

    // ═══════════════════════════════════════════════════════════
    // Initialization
    // ═══════════════════════════════════════════════════════════

    private void InitializeState()
    {
        RegisterCommands();

        // Build shape gallery items from ShapeGalleryProvider
        foreach (var item in ShapeGalleryProvider.Items)
        {
            ShapeGalleryItems.Add(new ShapeGalleryItem
            {
                TypeName = item.TypeName,
                DisplayName = item.DisplayName,
                Icon = item.Icon,
                Description = $"Add a {item.DisplayName} shape",
            });
        }

        // Build SVG element list (read-only reference)
        if (_sourcePicture is not null)
        {
            for (int i = 0; i < _sourcePicture.Elements.Count; i++)
            {
                var elem = _sourcePicture.Elements[i];
                bool animatable = elem is ShapeCanvasElement;
                var item = new ElementItem
                {
                    Index = i,
                    DisplayName = $"SVG Element {i}",
                    TypeName = elem.GetType().Name,
                    IsAnimatable = animatable,
                };
                Elements.Add(item);
                ElementNames.Add(item.DisplayName);
            }
        }

        // Build component items from editing copies
        foreach (var component in _editingComponents)
        {
            var item = new VectorComponentItem(component, this);
            Components.Add(item);
        }

        // Build available field IDs from component animatable fields
        BuildAvailableFieldIds();

        // Auto-select first component or first SVG element
        if (Components.Count > 0)
            SelectedComponent = Components[0];
        else if (Elements.Count > 0)
            SelectedElementIndex = 0;

        // Refresh initial preview
        _ = RefreshInteractivePreviewsAsync();
    }

    /// <summary>
    /// Builds the list of available animatable field IDs for the track picker.
    /// Aggregates fields from all components and the global AnimatableFieldMap.
    /// </summary>
    private void BuildAvailableFieldIds()
    {
        var seen = new HashSet<string>();
        foreach (var field in AnimatableFieldMap.CommonFields.Values)
        {
            if (seen.Add(field.Id))
                AvailableFieldIds.Add(field.Id);
        }
        foreach (var field in AnimatableFieldMap.ShapeFields.Values)
        {
            if (seen.Add(field.Id))
                AvailableFieldIds.Add(field.Id);
        }
        if (AvailableFieldIds.Count > 0)
            SelectedFieldIdForNewTrack = AvailableFieldIds[0];
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

    private void Cancel()
    {
        if (IsPlaying) Stop();
        _clip.Components = _componentsBackup.ToList();
        ChangesCancelled?.Invoke(this, EventArgs.Empty);
    }

    // ═══════════════════════════════════════════════════════════
    // Component management
    // ═══════════════════════════════════════════════════════════

    private void AddComponent(string typeName)
    {
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

        var item = SelectedComponent;
        _editingComponents.Remove(item.Source);
        Components.Remove(item);

        SelectedComponent = Components.FirstOrDefault();
        _ = RefreshInteractivePreviewsAsync();
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
        if (_pickSvgFile is null) return;

        string? filePath;
        try { filePath = await _pickSvgFile(); }
        catch (Exception ex)
        {
            Log(ex, "SVG file picker failed", this);
            return;
        }

        if (string.IsNullOrEmpty(filePath)) return;

        try
        {
            var svgPicture = SVGToVectorElement.ImportFromFile(filePath);
            if (svgPicture is null || svgPicture.Elements.Count == 0) return;

            var fileName = System.IO.Path.GetFileNameWithoutExtension(filePath);

            // Create a component with ImportedSvg type through the plugin system
            using var doc = JsonDocument.Parse($$"""
                {
                    "TypeName": "ImportedSvg",
                    "Id": "{{Guid.NewGuid()}}",
                    "Name": "{{fileName}}",
                    "Index": {{Components.Count + 1}},
                    "FromPlugin": "{{Render.Plugin.InternalPluginBase.InternalPluginBaseID}}"
                }
                """);

            var plugin = Render.Plugin.PluginManager.LoadedPlugins.GetValueOrDefault(
                Render.Plugin.InternalPluginBase.InternalPluginBaseID);
            if (plugin is null) return;

            var component = plugin.VectComponentCreator(doc.RootElement);
            if (component is null) return;

            _editingComponents.Add(component);

            var item = new VectorComponentItem(component, this)
            {
                EditorSourceFilePath = filePath,
                EditorCachedElements = svgPicture.Elements.ToList(),
            };
            Components.Add(item);
            SelectedComponent = item;

            _ = RefreshInteractivePreviewsAsync();
        }
        catch (Exception ex)
        {
            Log(ex, $"Failed to import SVG from '{filePath}'", this);
        }
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

    /// <summary>
    /// Builds the full list of animated elements for the current frame.
    /// </summary>
    public List<VectorCanvasElement> GetCurrentFrameAnimatedElements()
    {
        var elements = new List<VectorCanvasElement>();
        uint duration = Math.Max(1, _clip.Duration);
        uint currentFrame = (uint)Math.Round(CurrentProgress * Math.Max(0, duration - 1));

        var sourcePic = _sourcePicture;
        if (sourcePic is not null)
        {
            elements.AddRange(sourcePic.Elements);
        }

        foreach (var compItem in Components)
        {
            var comp = compItem.Source;
            float progress = duration <= 1
                ? 0f
                : Math.Clamp(currentFrame / (float)(duration - 1), 0f, 1f);
            var elem = comp.Compute(progress);
            if (elem is not null)
                elements.Add(elem);
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
                    { DevicePlatform.WinUI, new[] { ".svg" } },
                    { DevicePlatform.Android, new[] { "image/svg+xml" } },
                    { DevicePlatform.iOS, new[] { "public.svg-image" } },
                    { DevicePlatform.macOS, new[] { "public.svg-image" } },
                });

            var result = await FilePicker.PickAsync(new PickOptions
            {
                PickerTitle = "Select SVG file to import",
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

            var previews = new DynamicPreview.PreparedPreview[_componentClips.Count];

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
                    previews[i] = new DynamicPreview.PreparedPreview(cc.Id, null, "No components", cc);
                    continue;
                }

                previews[i] = new DynamicPreview.PreparedPreview(
                    cc.Id,
                    () =>
                    {
                        return DynamicPreview.BuildViewportVectorPreviewView(
                            elements,
                            canvasW,
                            canvasH,
                            viewportX,
                            viewportY,
                            viewportW,
                            viewportH);
                    },
                    errorMessage: null,
                    source: cc);
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
            originX = originY = 0f;
            scaleX = scaleY = 1f;
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

        var handler = GetHandlerForType(component.TypeName);
        if (handler is null) return;

        var builder = handler.CreatePropertyUI(component.Source);
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

    /// <summary>
    /// Handles property changes from the dynamic property panel.
    /// Delegates to the handler's <see cref="IVectorComponentHandler.HandlePropertyChange"/>
    /// and refreshes the preview and component clips.
    /// </summary>
    private void OnPropertyPanelChanged(object? sender, PropertyPanelPropertyChangedEventArgs args)
    {
        var component = SelectedComponent;
        if (component is null) return;

        var handler = GetHandlerForType(component.TypeName);
        if (handler is null) return;

        handler.HandlePropertyChange(component.Source, args);
        RequestPreviewRefresh();
        RequestComponentClipsRebuild();
    }

    // ═══════════════════════════════════════════════════════════
    // INotifyPropertyChanged
    // ═══════════════════════════════════════════════════════════

    public event PropertyChangedEventHandler? PropertyChanged;

    protected void OnPropertyChanged([CallerMemberName] string? propertyName = null)
    {
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
    }

    // ═══════════════════════════════════════════════════════════
    // Static helpers — cloning
    // ═══════════════════════════════════════════════════════════

    private static List<IVectorComponent> CloneComponents(List<IVectorComponent> original)
    {
        try
        {
            var json = JsonSerializer.Serialize(original);
            return JsonSerializer.Deserialize<List<IVectorComponent>>(json,
                new JsonSerializerOptions { PropertyNameCaseInsensitive = true })!;
        }
        catch
        {
            return new();
        }
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
}

