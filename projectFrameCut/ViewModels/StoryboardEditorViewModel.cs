using projectFrameCut.ApplicationAPIBase.Helpers;
using projectFrameCut.Drawing.Base;
using projectFrameCut.Drawing.Vector;
using projectFrameCut.Drawing.Vector.ImportExport;
using projectFrameCut.Render.ClipsAndTracks;
using projectFrameCut.Render.RenderAPIBase.Animation;
using projectFrameCut.Render.RenderAPIBase.ClipAndTrack;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Windows.Input;
using IDispatcher = Microsoft.Maui.Dispatching.IDispatcher;
using IPicture = projectFrameCut.Drawing.Base.IPicture;

namespace projectFrameCut.ViewModels;

/// <summary>
/// ViewModel for <see cref="DraftStuff.StoryboardEditorView"/>.
/// Provides visual editing of a <see cref="Storyboard"/> attached to a
/// <see cref="VectorCanvasClip"/>, plus real-time animation preview.
/// </summary>
public partial class StoryboardEditorViewModel : INotifyPropertyChanged
{
    // ── Injected state ────────────────────────────────────

    private readonly VectorCanvasClip _clip;
    private VectorPicture? _sourcePicture;
    private Storyboard _storyboard; // Clone for editing

    private readonly IDispatcher _dispatcher;
    private readonly Func<Task<string?>>? _pickSvgFile;

    // Editing copies of components (so Cancel can discard)
    private List<VectorComponent> _editingComponents = new();
    private readonly List<VectorComponent> _componentsBackup;

    public StoryboardEditorViewModel(VectorCanvasClip clip, IDispatcher dispatcher,
        Func<Task<string?>>? pickSvgFile = null)
    {
        _clip = clip ?? throw new ArgumentNullException(nameof(clip));
        _sourcePicture = clip.SourcePicture; // May be null in composition-only mode
        _dispatcher = dispatcher;
        _pickSvgFile = pickSvgFile;

        // Work on a clone so Cancel can discard changes.
        // If no storyboard exists, use the clip's actual timeline duration
        // so the displayed "xxx fr" and progress mapping match reality.
        if (clip.AnimationStoryboard is not null)
        {
            _storyboard = CloneStoryboard(clip.AnimationStoryboard);
        }
        else
        {
            _storyboard = new Storyboard { DurationInFrames = Math.Max(1, clip.Duration) };
        }

        // Clone components for editing
        _editingComponents = CloneComponents(clip.Components);
        _componentsBackup = CloneComponents(clip.Components);

        RegisterCommands();
        Initialize();
    }

    // ── Properties ────────────────────────────────────────

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
    public ObservableCollection<ElementItemViewModel> Elements { get; } = new();

    /// <summary>Flat list of element names for Picker ItemsSource (Picker needs IList).</summary>
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
                // Note: Do NOT fire OnPropertyChanged(nameof(SelectedElement))
                // or ChangeCanExecute() here — both can cause Picker freeze
                // in MAUI compiled-binding mode due to re-entrant UI updates.
            }
        }
    }

    /// <summary>Derived from SelectedElementIndex.</summary>
    public ElementItemViewModel? SelectedElement =>
        SelectedElementIndex >= 0 && SelectedElementIndex < Elements.Count
            ? Elements[SelectedElementIndex]
            : null;

    public bool CanAddTrack => SelectedElement is not null || SelectedComponent is not null;
    public bool CanRemoveTrack => SelectedTrack is not null;
    public bool CanAddKeyFrame => SelectedTrack is not null;

    public KeyFrameItemViewModel? SelectedKeyFrame
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

    /// <summary>All animation track VMs.</summary>
    public ObservableCollection<AnimationTrackItemViewModel> Tracks { get; } = new();

    public AnimationTrackItemViewModel? SelectedTrack
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

    // ── Selected property for new tracks ─────────────────

    public AnimatableProperty SelectedPropertyForNewTrack
    {
        get;
        set { if (field != value) { field = value; OnPropertyChanged(); } }
    }

    /// <summary>All AnimatableProperty values for the picker.</summary>
    public IReadOnlyList<AnimatableProperty> AllProperties { get; } =
        Enum.GetValues<AnimatableProperty>();

    /// <summary>Flat list version for Picker ItemsSource compatibility.</summary>
    public List<AnimatableProperty> AllPropertiesList { get; } =
        new List<AnimatableProperty>(Enum.GetValues<AnimatableProperty>());

    /// <summary>All available easing modes for the keyframe easing Picker.</summary>
    public List<EasingMode> AllEasingModes { get; } =
        new List<EasingMode>(Enum.GetValues<EasingMode>());

    // ── Preview state ────────────────────────────────────

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
                // During playback OnPlayTick handles refresh directly —
                // skip debounced refresh to avoid double-render and race conditions.
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

    public bool HasTracks => Tracks.Count > 0 || Components.Any(c => c.Tracks.Count > 0);

    /// <summary>Whether any track exists — SVG or component-level.</summary>
    private bool HasAnyTracks => Tracks.Count > 0 || Components.Any(c => c.Tracks.Count > 0);

    // ── Component management ───────────────────────────────

    /// <summary>User-created vector components being edited.</summary>
    public ObservableCollection<VectorComponentItemViewModel> Components { get; } = new();

    public VectorComponentItemViewModel? SelectedComponent
    {
        get;
        set
        {
            if (field != value)
            {
                field = value;
                // Reset track selection when switching components, then
                // auto-select the first track of the newly selected component
                // (if any), so Add KeyFrame / Remove Track has a target.
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

    /// <summary>Whether legacy SVG source elements exist (old projects with SourcePicture).</summary>
    public bool HasLegacySvgSource => _sourcePicture is { Elements.Count: > 0 } && !IsCompositionMode;

    // ── Shape gallery ──────────────────────────────────────

    /// <summary>Available shapes for adding to the composition.</summary>
    public List<ShapeGalleryItemViewModel> ShapeGalleryItems { get; } = new();

    // ── Timeline invalidation ────────────────────────────

    private Action? _timelineInvalidateAction;
    /// <summary>Called by the view to register its GraphicsView.Invalidate callback.</summary>
    public void RegisterTimelineInvalidate(Action invalidate) => _timelineInvalidateAction = invalidate;

    public void InvalidateTimeline() => _timelineInvalidateAction?.Invoke();

    // ── Commands ──────────────────────────────────────────

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
        // Note: CanExecute delegates intentionally omitted — button enabled/disabled
        // is driven via XAML {Binding CanAddTrack/CanRemoveTrack/CanAddKeyFrame}
        // to avoid Picker freeze in MAUI compiled-binding mode.
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

    // ── Init ──────────────────────────────────────────────

    private void Initialize()
    {
        // Build shape gallery items
        foreach (VectorShapeType shape in Enum.GetValues<VectorShapeType>())
        {
            ShapeGalleryItems.Add(new ShapeGalleryItemViewModel
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
                var vm = new ElementItemViewModel
                {
                    Index = i,
                    DisplayName = $"SVG Element {i}",
                    TypeName = elem.GetType().Name,
                    IsAnimatable = animatable,
                };
                Elements.Add(vm);
                ElementNames.Add(vm.DisplayName);
            }
        }

        // Build track VMs (SVG storyboard tracks)
        foreach (var track in _storyboard.Tracks)
        {
            var trackVm = new AnimationTrackItemViewModel(track, this);
            trackVm.PropertyChanged += (_, e) =>
            {
                if (e.PropertyName is nameof(AnimationTrackItemViewModel.KeyFrameCount))
                    InvalidateTimeline();
            };
            Tracks.Add(trackVm);
        }

        // Build component VMs from editing copies
        foreach (var component in _editingComponents)
        {
            var vm = new VectorComponentItemViewModel(component, this);
            Components.Add(vm);
        }

        // Auto-select first component or first SVG element
        if (Components.Count > 0)
            SelectedComponent = Components[0];
        else if (Elements.Count > 0)
            SelectedElementIndex = 0;

        // Refresh initial preview
        _ = RefreshPreview();
    }

    // ── Track management ──────────────────────────────────

    private void AddTrack()
    {
        // Component mode: delegate to selected component
        if (SelectedComponent is not null)
        {
            var track = new AnimationTrack
            {
                ElementIndex = 0, // Single-shape component: always index 0
                Property = SelectedPropertyForNewTrack,
                KeyFrames = new()
                {
                    new KeyFrame(0f, DefaultValueForProperty(SelectedPropertyForNewTrack), EasingMode.Linear),
                    new KeyFrame(1f, DefaultValueForProperty(SelectedPropertyForNewTrack), EasingMode.Linear),
                },
            };

            SelectedComponent.AddTrack(track);
            SelectedTrack = SelectedComponent.Tracks.LastOrDefault();
            OnPropertyChanged(nameof(HasTracks));
            InvalidateTimeline();
            return;
        }

        // SVG mode: existing logic
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

        var svgTrackVm = new AnimationTrackItemViewModel(svgTrack, this);
        svgTrackVm.PropertyChanged += (_, _) => InvalidateTimeline();
        Tracks.Add(svgTrackVm);

        SelectedTrack = svgTrackVm;
        OnPropertyChanged(nameof(HasTracks));
        InvalidateTimeline();
    }

    private void RemoveTrack()
    {
        if (SelectedTrack is null) return;

        // Component mode
        if (SelectedComponent is not null)
        {
            SelectedComponent.RemoveTrack(SelectedTrack);
            SelectedTrack = SelectedComponent.Tracks.FirstOrDefault();
            OnPropertyChanged(nameof(HasTracks));
            InvalidateTimeline();
            return;
        }

        // SVG mode
        var source = SelectedTrack.Source;
        _storyboard.Tracks.Remove(source);
        Tracks.Remove(SelectedTrack);
        SelectedTrack = Tracks.FirstOrDefault();

        OnPropertyChanged(nameof(HasTracks));
        InvalidateTimeline();
    }

    private void AddKeyFrameAtCurrentTime()
    {
        if (SelectedTrack is null) return;

        // Interpolate value at current time from the track's own evaluation
        float value = SelectedTrack.Source.GetValue(CurrentProgress);
        SelectedTrack.AddKeyFrame(CurrentProgress, value);

        // Clear keyframe selection — AddKeyFrame rebuilds VMs so the old
        // SelectedKeyFrame reference would be stale
        SelectedKeyFrame = null;
    }

    private void DeleteKeyFrame()
    {
        if (SelectedTrack is null || SelectedKeyFrame is null) return;

        int index = SelectedTrack.KeyFrames.IndexOf(SelectedKeyFrame);
        SelectedTrack.RemoveKeyFrameAt(index);
        SelectedKeyFrame = null;
    }

    // ── Play control ──────────────────────────────────────

    private IDispatcherTimer? _playTimer;

    private void PlayPause()
    {
        if (!HasAnyTracks) return;

        if (IsPlaying)
        {
            Pause();
        }
        else
        {
            Play();
        }
    }

    private void Play()
    {
        if (!HasAnyTracks) return;

        // Prevent double-start
        if (IsPlaying) return;

        IsPlaying = true;
        _playTimer ??= _dispatcher.CreateTimer();
        _playTimer.IsRepeating = true;
        _playTimer.Interval = TimeSpan.FromMilliseconds(1000.0 / DeviceDisplay.MainDisplayInfo.RefreshRate); 

        // Guard against double-subscription: remove first, then add
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
        {
            CurrentProgress = 0f; // Loop
        }

        // Skip if a refresh is still in progress (avoid stacking up renders)
        if (_refreshInProgress) return;
        _refreshInProgress = true;
        try
        {
            await RefreshPreview();
        }
        finally
        {
            _refreshInProgress = false;
        }
    }

    // ── Preview ───────────────────────────────────────────

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
                {
                    resultPicture = _storyboard.Apply(sourcePic, CurrentProgress);
                }
                else
                {
                    resultPicture.Elements.AddRange(sourcePic.Elements);
                }
            }

            // Stage 2: Editing components with their storyboards
            uint clipDuration = Math.Max(1, _clip.Duration);
            uint currentFrame = (uint)Math.Round(CurrentProgress * Math.Max(1, clipDuration - 1));
            foreach (var compVm in Components)
            {
                var comp = compVm.Source;
                var animatedElements = comp.GetAnimatedElements(currentFrame, clipDuration);
                resultPicture.Elements.AddRange(animatedElements);
            }

            // Nothing to render — clear preview and show placeholder
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

            var rasterizer = IVectorContentClip.GlobalDefaultRasterizer;

            await Task.Run(() =>
            {
                var raster = rasterizer.Convert(
                    resultPicture,
                    PreviewWidth,
                    PreviewHeight,
                    transparentBackground: true,
                    aaMode: AntiAliasMode.SSAA2x);

                if (raster is null)
                {
                    _dispatcher.Dispatch(() =>
                    {
                        PreviewImage = null;
                        PreviewPlaceholder = "Rasterizer returned no output.";
                    });
                    return;
                }

                var imageSource = raster.ToImageSource();

                _dispatcher.Dispatch(() =>
                {
                    PreviewImage = imageSource;
                });
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

    // ── Apply / Cancel ────────────────────────────────────

    private void ApplyChanges()
    {
        // Persist SVG storyboard back to clip
        _clip.AnimationStoryboard = _storyboard;

        // Persist components back to clip
        var finalComponents = Components.Select(vm => vm.Source).ToList();
        _clip.Components = finalComponents;
        _clip.SerializeComponents(finalComponents);
        _clip.SerializeStoryboard(_storyboard);

        ChangesApplied?.Invoke(_clip.ExtraData);
    }

    private void Cancel()
    {
        // Stop any ongoing playback
        if (IsPlaying)
            Stop();

        // Restore original components
        _clip.Components = _componentsBackup;

        ChangesCancelled?.Invoke(this, EventArgs.Empty);
    }

    // ── Events ────────────────────────────────────────────

    public event Action<Dictionary<string, object>?>? ChangesApplied;
    public event EventHandler? ChangesCancelled;
    public event PropertyChangedEventHandler? PropertyChanged;

    protected void OnPropertyChanged([CallerMemberName] string? propertyName = null)
    {
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
    }

    // ── Helpers ───────────────────────────────────────────

    private static Storyboard CloneStoryboard(Storyboard original)
    {
        // Deep clone via JSON (simple and reliable for these POCOs)
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

    // ── Component management ───────────────────────────────

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

        var vm = new VectorComponentItemViewModel(component, this);
        Components.Add(vm);
        SelectedComponent = vm;

        _ = DebouncedRefreshPreview();
    }

    private void RemoveComponent()
    {
        if (SelectedComponent is null) return;

        var vm = SelectedComponent;
        _editingComponents.Remove(vm.Source);
        Components.Remove(vm);

        SelectedComponent = Components.FirstOrDefault();
        _ = DebouncedRefreshPreview();
    }

    /// <summary>
    /// Called by <see cref="VectorComponentItemViewModel"/> when a shape
    /// property changes and the preview needs to be refreshed.
    /// </summary>
    public void RequestPreviewRefresh()
    {
        _ = DebouncedRefreshPreview();
    }

    // ── SVG import ─────────────────────────────────────────

    private async Task ImportSvg()
    {
        if (_pickSvgFile is null) return;

        string? filePath;
        try
        {
            filePath = await _pickSvgFile();
        }
        catch (Exception ex)
        {
            Log(ex, "SVG file picker failed", this);
            return;
        }

        if (string.IsNullOrEmpty(filePath))
            return;

        try
        {
            var svgPicture = SVGToVectorElement.ImportFromFile(filePath);
            if (svgPicture is null || svgPicture.Elements.Count == 0)
                return;

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

            var vm = new VectorComponentItemViewModel(component, this);
            Components.Add(vm);
            SelectedComponent = vm;

            _ = DebouncedRefreshPreview();
        }
        catch (Exception ex)
        {
            Log(ex, $"Failed to import SVG from '{filePath}'", this);
        }
    }

    // ── Component cloning ──────────────────────────────────

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
}

// ── ElementItemViewModel ──────────────────────────────────

/// <summary>Simple read-only element descriptor for the element picker.</summary>
public class ElementItemViewModel
{
    public int Index { get; init; }
    public string DisplayName { get; init; } = "";
    public string TypeName { get; init; } = "";
    /// <summary>Only <see cref="ShapeCanvasElement"/> supports animation.</summary>
    public bool IsAnimatable { get; init; }

    public override string ToString() =>
        IsAnimatable ? DisplayName : $"{DisplayName} ({TypeName} — not animatable)";
}
