using projectFrameCut.ApplicationAPIBase.Helpers;
using projectFrameCut.Drawing.Base;
using projectFrameCut.Drawing.Vector;
using projectFrameCut.Drawing.Vector.ImportExport;
using projectFrameCut.InteractableEditor;
using projectFrameCut.Render.ClipsAndTracks;
using projectFrameCut.Render.RenderAPIBase.Animation;
using projectFrameCut.Render.RenderAPIBase.ClipAndTrack;
using projectFrameCut.Shared;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Windows.Input;
using Color = Microsoft.Maui.Graphics.Color;
using IDispatcher = Microsoft.Maui.Dispatching.IDispatcher;
using IPicture = projectFrameCut.Drawing.Base.IPicture;
using PointF = Microsoft.Maui.Graphics.PointF;
using RectF = Microsoft.Maui.Graphics.RectF;

namespace projectFrameCut.DraftStuff;

/// <summary>
/// Storyboard 编辑器——MVU 风格自包含页面。
/// </summary>
public partial class StoryboardEditorView : ContentView, INotifyPropertyChanged
{
    // ═══════════════════════════════════════════════════════════
    // Injected state
    // ═══════════════════════════════════════════════════════════

    private readonly VectorCanvasClip _clip;
    private VectorPicture? _sourcePicture;
    private Storyboard _storyboard;
    private readonly IDispatcher _dispatcher;
    private readonly Func<Task<string?>>? _pickSvgFile;

    private List<VectorComponent> _editingComponents = new();
    private readonly List<VectorComponent> _componentsBackup;
    private TimelineDrawable _timelineDrawable = null!;

    // ── InteractableEditor integration ──────────────────────
    private List<ComponentClip> _componentClips = new();
    private Dictionary<Guid, ClipElementUI> _clipElementUIs = new();
    private bool _suppressComponentPropertySync;

    // ═══════════════════════════════════════════════════════════
    // Constructors
    // ═══════════════════════════════════════════════════════════

    /// <summary>Parameterless constructor required by XAML parser.</summary>
    public StoryboardEditorView()
    {
        InitializeComponent();
    }

    /// <summary>
    /// Create the editor for the given <paramref name="clip"/>.
    /// Works for both SVG-backed and composition-only clips.
    /// </summary>
    public StoryboardEditorView(VectorCanvasClip clip, int projectWidth, int projectHeight) : this()
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
        if (clip.AnimationStoryboard is not null)
            _storyboard = CloneStoryboard(clip.AnimationStoryboard);
        else
            _storyboard = new Storyboard { DurationInFrames = Math.Max(1, clip.Duration) };

        _editingComponents = CloneComponents(clip.Components);
        _componentsBackup = CloneComponents(clip.Components);

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

        InteractiveEditor.UpdateCanvasSize(canvasW, canvasH);

        InteractiveEditor.ConfigureOverlayClipTap(OnComponentClipTapped);
        InteractiveEditor.ConfigureBlankAreaTap(OnBlankAreaTapped);
        InteractiveEditor.ConfigurePreviewRefresh(OnPreviewRefreshRequested);
        InteractiveEditor.ConfigureGetClipInstanceCallback(GetClipInstanceForEditor);

        InteractiveEditor.EditorCanvasBackground = Colors.Transparent;

        // ── Build initial component clips ──
        RebuildComponentClips();

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

    public uint DurationInFrames
    {
        get => _storyboard.DurationInFrames;
        set
        {
            if (_storyboard.DurationInFrames != value)
            {
                _storyboard.DurationInFrames = value < 1 ? 1 : value;
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

    public bool CanAddTrack => SelectedElement is not null || SelectedComponent is not null;
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

    /// <summary>All animation tracks (legacy SVG mode).</summary>
    public ObservableCollection<AnimationTrackItem> Tracks { get; } = new();

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

    /// <summary>Selected property for new tracks.</summary>
    public AnimatableProperty SelectedPropertyForNewTrack
    {
        get;
        set { if (field != value) { field = value; OnPropertyChanged(); } }
    }

    /// <summary>All AnimatableProperty values for the picker.</summary>
    public List<AnimatableProperty> AllPropertiesList { get; } =
        new List<AnimatableProperty>(Enum.GetValues<AnimatableProperty>());

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
                if (!IsPlaying)
                    _ = DebouncedRefreshPreview();
            }
        }
    }

    public bool IsPlaying
    {
        get;
        set { if (field != value) { field = value; OnPropertyChanged(); } }
    }

    public ImageSource? PreviewImage
    {
        get;
        set { if (field != value) { field = value; OnPropertyChanged(); OnPropertyChanged(nameof(HasPreview)); } }
    }

    public bool HasPreview => PreviewImage is not null;

    public string PreviewPlaceholder
    {
        get;
        set
        {
            if (field != value) { field = value; OnPropertyChanged(); }
        }
    } = "Add a shape or import SVG to see preview.";

    public int PreviewWidth { get; set; } = 320;
    public int PreviewHeight { get; set; } = 240;

    private bool HasAnyTracks => Tracks.Count > 0 || Components.Any(c => c.Tracks.Count > 0);

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
                _timelineInvalidateAction?.Invoke();
                _ = DebouncedRefreshPreview();
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
        AddComponentCommand = new Command<VectorShapeType>(AddComponent);
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

        // Build shape gallery items
        foreach (VectorShapeType shape in Enum.GetValues<VectorShapeType>())
        {
            ShapeGalleryItems.Add(new ShapeGalleryItem
            {
                ShapeType = shape,
                DisplayName = ShapeDefaults.GetDisplayName(shape),
                Icon = ShapeDefaults.GetIcon(shape),
                Description = $"Add a {ShapeDefaults.GetDisplayName(shape)} shape",
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

        // Build track items (SVG storyboard tracks)
        foreach (var track in _storyboard.Tracks)
        {
            var trackItem = new AnimationTrackItem(track, this);
            trackItem.PropertyChanged += (_, e) =>
            {
                if (e.PropertyName is nameof(AnimationTrackItem.KeyFrameCount))
                    InvalidateTimeline();
            };
            Tracks.Add(trackItem);
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
        _ = RefreshPreview();
    }

    // ═══════════════════════════════════════════════════════════
    // Track management
    // ═══════════════════════════════════════════════════════════

    private void AddTrack()
    {
        if (SelectedComponent is not null)
        {
            var track = new AnimationTrack
            {
                ElementIndex = 0,
                Property = SelectedPropertyForNewTrack,
                KeyFrames = new()
                {
                    new KeyFrame(0f, DefaultValueForProperty(SelectedPropertyForNewTrack), EasingMode.Linear),
                    new KeyFrame(1f, DefaultValueForProperty(SelectedPropertyForNewTrack), EasingMode.Linear),
                },
            };

            SelectedComponent.AddTrack(track);
            SelectedTrack = SelectedComponent.Tracks.LastOrDefault();
            OnPropertyChanged(nameof(HasAnyTracks));
            InvalidateTimeline();
            return;
        }

        if (SelectedElement is null) return;

        var svgTrack = new AnimationTrack
        {
            ElementIndex = SelectedElement.Index,
            Property = SelectedPropertyForNewTrack,
            KeyFrames = new()
            {
                new KeyFrame(0f, DefaultValueForProperty(SelectedPropertyForNewTrack), EasingMode.Linear),
                new KeyFrame(1f, DefaultValueForProperty(SelectedPropertyForNewTrack), EasingMode.Linear),
            },
        };

        _storyboard.Tracks.Add(svgTrack);

        var svgTrackItem = new AnimationTrackItem(svgTrack, this);
        svgTrackItem.PropertyChanged += (_, _) => InvalidateTimeline();
        Tracks.Add(svgTrackItem);

        SelectedTrack = svgTrackItem;
        OnPropertyChanged(nameof(HasAnyTracks));
        InvalidateTimeline();
    }

    private void RemoveTrack()
    {
        if (SelectedTrack is null) return;

        if (SelectedComponent is not null)
        {
            SelectedComponent.RemoveTrack(SelectedTrack);
            SelectedTrack = SelectedComponent.Tracks.FirstOrDefault();
            OnPropertyChanged(nameof(HasAnyTracks));
            InvalidateTimeline();
            return;
        }

        var source = SelectedTrack.Source;
        _storyboard.Tracks.Remove(source);
        Tracks.Remove(SelectedTrack);
        SelectedTrack = Tracks.FirstOrDefault();

        OnPropertyChanged(nameof(HasAnyTracks));
        InvalidateTimeline();
    }

    private void AddKeyFrameAtCurrentTime()
    {
        if (SelectedTrack is null) return;

        float value = SelectedTrack.Source.GetValue(CurrentProgress);
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
        _ = RefreshPreview();
    }

    private bool _refreshInProgress;

    private async void OnPlayTick(object? sender, EventArgs e)
    {
        if (!IsPlaying) return;

        float step = _storyboard.DurationInFrames > 0
            ? 1f / _storyboard.DurationInFrames
            : 0.033f;

        CurrentProgress += step;
        if (CurrentProgress >= 1f)
            CurrentProgress = 0f;

        if (_refreshInProgress) return;
        _refreshInProgress = true;
        try { await RefreshPreview(); }
        finally { _refreshInProgress = false; }
    }

    // ═══════════════════════════════════════════════════════════
    // Preview rendering
    // ═══════════════════════════════════════════════════════════

    private bool _refreshScheduled;
    private const double DebounceMs = 50;

    private async Task DebouncedRefreshPreview()
    {
        if (_refreshScheduled) return;
        _refreshScheduled = true;
        await Task.Delay(TimeSpan.FromMilliseconds(DebounceMs));
        _refreshScheduled = false;
        await RefreshPreview();
    }

    public async Task RefreshPreview()
    {
        try
        {
            var sourcePic = _sourcePicture;
            var resultPicture = new VectorPicture();

            // Stage 1: SVG source with editing storyboard
            if (sourcePic is not null)
            {
                if (_storyboard.Tracks.Count > 0)
                    resultPicture = _storyboard.Apply(sourcePic, CurrentProgress);
                else
                    resultPicture.Elements.AddRange(sourcePic.Elements);
            }

            // Stage 2: Editing components with their storyboards
            uint clipDuration = Math.Max(1, _clip.Duration);
            uint currentFrame = (uint)Math.Round(CurrentProgress * Math.Max(1, clipDuration - 1));
            foreach (var compItem in Components)
            {
                var comp = compItem.Source;
                var animatedElements = comp.GetAnimatedElements(currentFrame, clipDuration);
                resultPicture.Elements.AddRange(animatedElements);
            }

            if (resultPicture.Elements.Count == 0)
            {
                _dispatcher.Dispatch(() =>
                {
                    PreviewImage = null;
                    PreviewPlaceholder = HasSvgSource || Components.Count > 0
                        ? "No visible elements at this frame."
                        : "Add a shape or import SVG to see preview.";
                });
                return;
            }

            await Task.Run(() =>
            {
                var raster = IVectorContentClip.GlobalDefaultRasterizer.Convert(
                    resultPicture,
                    PreviewWidth,
                    PreviewHeight,
                    transparentBackground: true,
                    aaMode: IVectorContentClip.GlobalDefaultAntiAliasMode);

                if (raster is null)
                {
                    _dispatcher.Dispatch(() => { PreviewImage = null; PreviewPlaceholder = "Rasterizer returned no output."; });
                    return;
                }

                if (raster.GetSpecificChannel(IPicture.ChannelId.Alpha) switch
                {
                    float[] fa => fa.Average() <= float.Epsilon,
                    _ => true
                })
                {
                    _dispatcher.Dispatch(() => { PreviewImage = null; PreviewPlaceholder = "Canvas is empty."; });
                    return;
                }

                var imageSource = raster.ToImageSource();
                _dispatcher.Dispatch(() => { PreviewImage = imageSource; });
            });
        }
        catch (Exception ex)
        {
            Log(ex, "StoryboardEditor preview render", this);
            _dispatcher.Dispatch(() =>
            {
                PreviewImage = null;
                PreviewPlaceholder = $"Preview error: {ex.Message}";
            });
        }
    }

    // ═══════════════════════════════════════════════════════════
    // Apply / Cancel
    // ═══════════════════════════════════════════════════════════

    private void ApplyChanges()
    {
        _clip.AnimationStoryboard = _storyboard;

        var finalComponents = Components.Select(vm => vm.Source).ToList();
        _clip.Components = finalComponents;
        _clip.SerializeComponents(finalComponents);
        _clip.SerializeStoryboard(_storyboard);

        ChangesApplied?.Invoke(_clip.ExtraData);
    }

    private void Cancel()
    {
        if (IsPlaying) Stop();
        _clip.Components = _componentsBackup;
        ChangesCancelled?.Invoke(this, EventArgs.Empty);
    }

    // ═══════════════════════════════════════════════════════════
    // Component management
    // ═══════════════════════════════════════════════════════════

    private void AddComponent(VectorShapeType shapeType)
    {
        var def = new VectorComponentDefinition
        {
            ShapeType = shapeType,
            DisplayName = $"{ShapeDefaults.GetDisplayName(shapeType)} {Components.Count + 1}",
            ShapeParameters = ShapeDefaults.GetDefaults(shapeType),
            Points = ShapeDefaults.GetDefaultPoints(shapeType).ToList(),
        };

        var component = new VectorComponent
        {
            Definition = def,
            Storyboard = new ComponentStoryboard
            {
                DurationInFrames = Math.Max(1, _clip.Duration),
            },
        };

        _editingComponents.Add(component);

        var item = new VectorComponentItem(component, this);
        Components.Add(item);
        SelectedComponent = item;

        _ = DebouncedRefreshPreview();
    }

    private void RemoveComponent()
    {
        if (SelectedComponent is null) return;

        var item = SelectedComponent;
        _editingComponents.Remove(item.Source);
        Components.Remove(item);

        SelectedComponent = Components.FirstOrDefault();
        _ = DebouncedRefreshPreview();
    }

    /// <summary>
    /// Called by VectorComponentItem when a shape property changes
    /// and the preview needs to be refreshed.
    /// </summary>
    public void RequestPreviewRefresh()
    {
        _ = DebouncedRefreshPreview();
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

            var def = new VectorComponentDefinition
            {
                ShapeType = VectorShapeType.ImportedSvg,
                DisplayName = fileName,
                SourceFilePath = filePath,
            };

            var component = new VectorComponent
            {
                Definition = def,
                Storyboard = new ComponentStoryboard
                {
                    DurationInFrames = Math.Max(1, _clip.Duration),
                },
                CachedElements = svgPicture.Elements.ToList(),
            };

            _editingComponents.Add(component);

            var item = new VectorComponentItem(component, this);
            Components.Add(item);
            SelectedComponent = item;

            _ = DebouncedRefreshPreview();
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
        float localProgress = component.Source.Storyboard.CalculateLocalProgress(currentFrame, duration);

        float x = component.RelativeX;
        float y = component.RelativeY;

        foreach (var trackItem in component.Tracks)
        {
            if (trackItem.Property == AnimatableProperty.RelativeX)
                x = trackItem.Source.GetValue(localProgress);
            else if (trackItem.Property == AnimatableProperty.RelativeY)
                y = trackItem.Source.GetValue(localProgress);
        }

        return (x, y);
    }

    /// <summary>
    /// Finds the keyframe for the given property whose time is within tolerance.
    /// </summary>
    public KeyFrameItem? FindKeyFrameAtProgress(VectorComponentItem component, AnimatableProperty property, float progress)
    {
        const float tolerance = 0.015f;
        var track = component.Tracks.FirstOrDefault(t => t.Property == property);
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
        var xKf = FindKeyFrameAtProgress(component, AnimatableProperty.RelativeX, progress);
        if (xKf is not null)
            xKf.Value = normalizedX;
        else if (component.Tracks.Any(t => t.Property == AnimatableProperty.RelativeX))
            component.Tracks.First(t => t.Property == AnimatableProperty.RelativeX).AddKeyFrame(progress, normalizedX);
        else
            component.RelativeX = normalizedX;

        // ── RelativeY ──
        var yKf = FindKeyFrameAtProgress(component, AnimatableProperty.RelativeY, progress);
        if (yKf is not null)
            yKf.Value = normalizedY;
        else if (component.Tracks.Any(t => t.Property == AnimatableProperty.RelativeY))
            component.Tracks.First(t => t.Property == AnimatableProperty.RelativeY).AddKeyFrame(progress, normalizedY);
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
            if (_storyboard.Tracks.Count > 0)
                elements.AddRange(_storyboard.Apply(sourcePic, CurrentProgress).Elements);
            else
                elements.AddRange(sourcePic.Elements);
        }

        foreach (var compItem in Components)
        {
            var comp = compItem.Source;
            var animated = comp.GetAnimatedElements(currentFrame, duration);
            elements.AddRange(animated);
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
        float value = SelectedTrack.Source.GetValue(time);
        SelectedTrack.AddKeyFrame(time, value);
        SelectedKeyFrame = null;
    }

    private ObservableCollection<AnimationTrackItem> GetActiveTracksForHitTest()
    {
        if (SelectedComponent?.Tracks is { Count: > 0 } compTracks)
            return compTracks;
        return Tracks;
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
        int canvasW = Math.Max(1, PreviewWidth);
        int canvasH = Math.Max(1, PreviewHeight);
        _componentClips = Components
            .Select(vm =>
            {
                var cc = new ComponentClip(vm.Source)
                {
                    ParentCanvasWidth = canvasW,
                    ParentCanvasHeight = canvasH,
                };
                cc.EffectsInstances =
                [
                    new DynamicPositionProviderEffect(i =>
                        cc.TryComputeAnimatedFrameBounds(i, Math.Max(1, _clip.Duration), out var animatedBounds)
                            ? animatedBounds
                            : new ClipPositionTuple(cc.TargetX, cc.TargetY, cc.TargetWidth, cc.TargetHeight, false))
                ];
                cc.SyncFromDefinition();
                return cc;
            })
            .ToList();

        _clipElementUIs = _componentClips.ToClipElementUIDictionary();

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

                ui.SyncToComponentClip(cc);
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

    // ── Property sync: right panel → ComponentClip → InteractableEditor ──

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
            else if (e.PropertyName is nameof(VectorComponentItem.StrokeR)
                or nameof(VectorComponentItem.StrokeG)
                or nameof(VectorComponentItem.StrokeB)
                or nameof(VectorComponentItem.StrokeA)
                or nameof(VectorComponentItem.FillR)
                or nameof(VectorComponentItem.FillG)
                or nameof(VectorComponentItem.FillB)
                or nameof(VectorComponentItem.FillA)
                or nameof(VectorComponentItem.Thickness))
            {
                _ = RefreshInteractivePreviewsAsync();
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

                var component = cc.Component;
                var elements = component.GetAnimatedElements(frame, duration);

                if (elements is null || elements.Count == 0)
                {
                    previews[i] = new DynamicPreview.PreparedPreview(cc.Id, null, "No elements", cc);
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
        }
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

    private static Storyboard CloneStoryboard(Storyboard original)
    {
        try
        {
            var json = JsonSerializer.Serialize(original);
            return JsonSerializer.Deserialize<Storyboard>(json,
                new JsonSerializerOptions { PropertyNameCaseInsensitive = true })!;
        }
        catch
        {
            return new Storyboard
            {
                DurationInFrames = original.DurationInFrames,
                Tracks = new(),
            };
        }
    }

    private static List<VectorComponent> CloneComponents(List<VectorComponent> original)
    {
        try
        {
            var json = JsonSerializer.Serialize(original);
            return JsonSerializer.Deserialize<List<VectorComponent>>(json,
                new JsonSerializerOptions { PropertyNameCaseInsensitive = true })!;
        }
        catch
        {
            return new();
        }
    }

    private static float DefaultValueForProperty(AnimatableProperty p) => p switch
    {
        AnimatableProperty.RelativeX => 0.5f,
        AnimatableProperty.RelativeY => 0.5f,
        AnimatableProperty.Rotation => 0f,
        AnimatableProperty.BaseX => 0f,
        AnimatableProperty.BaseY => 0f,
        AnimatableProperty.FillColorA => 1f,
        AnimatableProperty.StrokeColorA => 1f,
        _ => 0f,
    };
}
