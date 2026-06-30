using Microsoft.Maui.Graphics;
using projectFrameCut.Render.RenderAPIBase.Animation;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;

namespace projectFrameCut.ViewModels;

/// <summary>
/// Wraps a <see cref="VectorComponent"/> for editing in the storyboard editor UI.
/// Manages per-component tracks, shape properties, and visual configuration.
/// </summary>
public class VectorComponentItemViewModel : INotifyPropertyChanged
{
    private readonly VectorComponent _source;
    private readonly StoryboardEditorViewModel _owner;

    public VectorComponentItemViewModel(VectorComponent source, StoryboardEditorViewModel owner)
    {
        _source = source ?? throw new ArgumentNullException(nameof(source));
        _owner = owner ?? throw new ArgumentNullException(nameof(owner));

        // Build track child VMs
        foreach (var track in source.Storyboard.Tracks)
        {
            var trackVm = new AnimationTrackItemViewModel(track, owner);
            trackVm.PropertyChanged += (_, e) =>
            {
                if (e.PropertyName is nameof(AnimationTrackItemViewModel.KeyFrameCount))
                    _owner.InvalidateTimeline();
            };
            Tracks.Add(trackVm);
        }
    }

    /// <summary>Reference to the underlying model.</summary>
    public VectorComponent Source => _source;

    public System.Guid Id => _source.Definition.Id;

    public string DisplayName
    {
        get => _source.Definition.DisplayName;
        set
        {
            if (_source.Definition.DisplayName != value)
            {
                _source.Definition.DisplayName = value;
                OnPropertyChanged();
            }
        }
    }

    public VectorShapeType ShapeType => _source.Definition.ShapeType;

    public string ShapeIcon => ShapeDefaults.GetIcon(ShapeType);

    /// <summary>Whether this component was imported from an SVG file.</summary>
    public bool IsFromSvg => _source.Definition.ShapeType == VectorShapeType.ImportedSvg;

    /// <summary>Whether shape-specific properties (position, colours) are editable.</summary>
    public bool IsShapeEditable => !IsFromSvg;

    /// <summary>Number of visual elements this component produces.</summary>
    public int ElementCount
    {
        get
        {
            if (IsFromSvg)
                return _source.CachedElements?.Count ?? 0;
            return 1;
        }
    }

    /// <summary>Human-readable element count for display.</summary>
    public string ElementCountText => IsFromSvg
        ? $"{ElementCount} elements"
        : "1 shape";

    /// <summary>Source SVG file path (only for ImportedSvg components).</summary>
    public string? SourceFilePath
    {
        get => _source.Definition.SourceFilePath;
        set
        {
            if (_source.Definition.SourceFilePath != value)
            {
                _source.Definition.SourceFilePath = value;
                OnPropertyChanged();
            }
        }
    }

    public string ShapeTypeDisplayName => IsFromSvg
        ? System.IO.Path.GetFileName(SourceFilePath ?? "SVG")
        : ShapeDefaults.GetDisplayName(ShapeType);

    public uint DurationInFrames
    {
        get => _source.Storyboard.DurationInFrames;
        set
        {
            if (_source.Storyboard.DurationInFrames != value)
            {
                _source.Storyboard.DurationInFrames = Math.Max(1, value);
                OnPropertyChanged();
                OnPropertyChanged(nameof(DurationText));
                _owner.InvalidateTimeline();
            }
        }
    }

    public string DurationText => $"{DurationInFrames} frames";

    /// <summary>Animation tracks for this component.</summary>
    public ObservableCollection<AnimationTrackItemViewModel> Tracks { get; } = new();

    public int TrackCount => Tracks.Count;

    // ── Shape parameter accessors ───────────────────────────

    public Dictionary<string, float> ShapeParameters => _source.Definition.ShapeParameters;

    public float GetShapeParam(string key, float defaultValue)
    {
        if (ShapeParameters.TryGetValue(key, out float value))
            return value;
        return defaultValue;
    }

    public void SetShapeParam(string key, float value)
    {
        ShapeParameters[key] = value;
        OnPropertyChanged(nameof(ShapeParameters));
        _owner.RequestPreviewRefresh();
    }

    // ── Transform property accessors ────────────────────────

    public float RelativeX
    {
        get => _source.Definition.RelativeX;
        set { _source.Definition.RelativeX = Math.Clamp(value, 0f, 1f); OnPropertyChanged(); _owner.RequestPreviewRefresh(); }
    }

    public float RelativeY
    {
        get => _source.Definition.RelativeY;
        set { _source.Definition.RelativeY = Math.Clamp(value, 0f, 1f); OnPropertyChanged(); _owner.RequestPreviewRefresh(); }
    }

    public float Rotation
    {
        get => _source.Definition.Rotation;
        set { _source.Definition.Rotation = value; OnPropertyChanged(); _owner.RequestPreviewRefresh(); }
    }

    public int LayerIndex
    {
        get => _source.Definition.LayerIndex;
        set { _source.Definition.LayerIndex = value; OnPropertyChanged(); _owner.RequestPreviewRefresh(); }
    }

    // ── Visual property accessors ───────────────────────────

    public ushort StrokeR
    {
        get => _source.Definition.StrokeR;
        set { _source.Definition.StrokeR = value; OnPropertyChanged(); NotifyColorChanged(); _owner.RequestPreviewRefresh(); }
    }

    public ushort StrokeG
    {
        get => _source.Definition.StrokeG;
        set { _source.Definition.StrokeG = value; OnPropertyChanged(); NotifyColorChanged(); _owner.RequestPreviewRefresh(); }
    }

    public ushort StrokeB
    {
        get => _source.Definition.StrokeB;
        set { _source.Definition.StrokeB = value; OnPropertyChanged(); NotifyColorChanged(); _owner.RequestPreviewRefresh(); }
    }

    public float StrokeA
    {
        get => _source.Definition.StrokeA;
        set { _source.Definition.StrokeA = Math.Clamp(value, 0f, 1f); OnPropertyChanged(); NotifyColorChanged(); _owner.RequestPreviewRefresh(); }
    }

    public float Thickness
    {
        get => _source.Definition.Thickness;
        set { _source.Definition.Thickness = Math.Max(0, value); OnPropertyChanged(); _owner.RequestPreviewRefresh(); }
    }

    public ushort FillR
    {
        get => _source.Definition.FillR;
        set { _source.Definition.FillR = value; OnPropertyChanged(); NotifyFillColorChanged(); _owner.RequestPreviewRefresh(); }
    }

    public ushort FillG
    {
        get => _source.Definition.FillG;
        set { _source.Definition.FillG = value; OnPropertyChanged(); NotifyFillColorChanged(); _owner.RequestPreviewRefresh(); }
    }

    public ushort FillB
    {
        get => _source.Definition.FillB;
        set { _source.Definition.FillB = value; OnPropertyChanged(); NotifyFillColorChanged(); _owner.RequestPreviewRefresh(); }
    }

    public float FillA
    {
        get => _source.Definition.FillA;
        set { _source.Definition.FillA = Math.Clamp(value, 0f, 1f); OnPropertyChanged(); NotifyFillColorChanged(); _owner.RequestPreviewRefresh(); }
    }

    // ── Color preview helpers ────────────────────────────────

    /// <summary>Preview swatch for the current stroke color.</summary>
    public Color StrokeColorPreview => Color.FromRgba(StrokeR, StrokeG, StrokeB, (int)Math.Round(StrokeA * 255));

    /// <summary>Hex string e.g. "#FF5733" for the current stroke color.</summary>
    public string StrokeColorHex => $"#{StrokeR:X2}{StrokeG:X2}{StrokeB:X2}";

    /// <summary>Preview swatch for the current fill color.</summary>
    public Color FillColorPreview => Color.FromRgba(FillR, FillG, FillB, (int)Math.Round(FillA * 255));

    /// <summary>Hex string for the current fill color.</summary>
    public string FillColorHex => $"#{FillR:X2}{FillG:X2}{FillB:X2}";

    private void NotifyColorChanged()
    {
        OnPropertyChanged(nameof(StrokeColorPreview));
        OnPropertyChanged(nameof(StrokeColorHex));
    }

    private void NotifyFillColorChanged()
    {
        OnPropertyChanged(nameof(FillColorPreview));
        OnPropertyChanged(nameof(FillColorHex));
    }

    // ── Track management ────────────────────────────────────

    public void AddTrack(AnimationTrack track)
    {
        _source.Storyboard.Tracks.Add(track);

        var trackVm = new AnimationTrackItemViewModel(track, _owner);
        trackVm.PropertyChanged += (_, _) => _owner.InvalidateTimeline();
        Tracks.Add(trackVm);

        OnPropertyChanged(nameof(TrackCount));
        _owner.InvalidateTimeline();
    }

    public void RemoveTrack(AnimationTrackItemViewModel trackVm)
    {
        _source.Storyboard.Tracks.Remove(trackVm.Source);
        Tracks.Remove(trackVm);

        OnPropertyChanged(nameof(TrackCount));
        _owner.InvalidateTimeline();
    }

    // ── INotifyPropertyChanged ──────────────────────────────

    public event PropertyChangedEventHandler? PropertyChanged;

    protected void OnPropertyChanged([CallerMemberName] string? propertyName = null)
    {
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
    }
}
