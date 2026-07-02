using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using Microsoft.Maui.Graphics;
using projectFrameCut.Render.RenderAPIBase.Animation;

namespace projectFrameCut.DraftStuff;

// ═══════════════════════════════════════════════════════════════
// MVU Model types — pure data, no ViewModel overhead.
// These are the "Model" in MVU: mutable state containers with
// property-change notification for XAML two-way binding.
// ═══════════════════════════════════════════════════════════════

/// <summary>Display model for a shape in the shape gallery panel.</summary>
public class ShapeGalleryItem
{
    public VectorShapeType ShapeType { get; init; }
    public string DisplayName { get; init; } = "";
    public string Icon { get; init; } = "";
    public string Description { get; init; } = "";
}

/// <summary>Read-only element descriptor for the legacy SVG element picker.</summary>
public class ElementItem
{
    public int Index { get; init; }
    public string DisplayName { get; init; } = "";
    public string TypeName { get; init; } = "";
    public bool IsAnimatable { get; init; }

    public override string ToString() =>
        IsAnimatable ? DisplayName : $"{DisplayName} ({TypeName} — not animatable)";
}

// ═══════════════════════════════════════════════════════════════
// KeyFrameItem — wrappper around KeyFrame for two-way binding
// ═══════════════════════════════════════════════════════════════

/// <summary>
/// Wraps a <see cref="KeyFrame"/> for two-way binding in the storyboard editor UI.
/// </summary>
public class KeyFrameItem : INotifyPropertyChanged
{
    private KeyFrame _source;
    private AnimationTrackItem? _parentTrack;

    public KeyFrameItem(KeyFrame source, bool isLast, AnimationTrackItem? parentTrack = null)
    {
        _source = source;
        IsLast = isLast;
        _parentTrack = parentTrack;
    }

    public KeyFrame Source => _source;
    public AnimationTrackItem? ParentTrack => _parentTrack;

    public float Time
    {
        get => _source.Time;
        set
        {
            if (!Equals(_source.Time, value))
            {
                _source.Time = Math.Clamp(value, 0f, 1f);
                OnPropertyChanged();
                OnPropertyChanged(nameof(TimeDisplay));
                _parentTrack?.SortKeyFrames();
            }
        }
    }

    public float Value
    {
        get => _source.Value;
        set
        {
            if (!Equals(_source.Value, value))
            {
                _source.Value = value;
                OnPropertyChanged();
                OnPropertyChanged(nameof(ValueDisplay));
            }
        }
    }

    public EasingMode Easing
    {
        get => _source.Easing;
        set
        {
            if (_source.Easing != value)
            {
                _source.Easing = value;
                OnPropertyChanged();
                OnPropertyChanged(nameof(EasingDisplay));
            }
        }
    }

    public bool IsLast
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

    public string TimeDisplay => $"{Time * 100f:F0}%";

    public string ValueDisplay => Value switch
    {
        >= 0.01f or <= -0.01f => $"{Value:F3}",
        _ => $"{Value:F1}",
    };

    public string EasingDisplay => Easing switch
    {
        EasingMode.Linear => "Linear",
        EasingMode.QuadIn => "Quad In",
        EasingMode.QuadOut => "Quad Out",
        EasingMode.QuadInOut => "Quad InOut",
        EasingMode.CubicIn => "Cubic In",
        EasingMode.CubicOut => "Cubic Out",
        EasingMode.CubicInOut => "Cubic InOut",
        EasingMode.SineIn => "Sine In",
        EasingMode.SineOut => "Sine Out",
        EasingMode.SineInOut => "Sine InOut",
        EasingMode.ElasticIn => "Elastic In",
        EasingMode.ElasticOut => "Elastic Out",
        EasingMode.BounceOut => "Bounce Out",
        _ => Easing.ToString(),
    };

    public event PropertyChangedEventHandler? PropertyChanged;

    protected void OnPropertyChanged([CallerMemberName] string? propertyName = null)
    {
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
    }
}

// ═══════════════════════════════════════════════════════════════
// AnimationTrackItem — wrappper around AnimationTrack for binding
// ═══════════════════════════════════════════════════════════════

/// <summary>
/// Wraps an <see cref="AnimationTrack"/> for editing in the storyboard editor UI.
/// Owned by <see cref="StoryboardEditorView"/> (the page itself, not a ViewModel).
/// </summary>
public class AnimationTrackItem : INotifyPropertyChanged
{
    private readonly AnimationTrack _source;
    private readonly StoryboardEditorView _owner;

    public AnimationTrackItem(AnimationTrack source, StoryboardEditorView owner)
    {
        _source = source;
        _owner = owner;

        // Build keyframe child items
        for (int i = 0; i < source.KeyFrames.Count; i++)
        {
            bool isLast = i == source.KeyFrames.Count - 1;
            var kfItem = new KeyFrameItem(source.KeyFrames[i], isLast, this);
            kfItem.PropertyChanged += OnKeyFramePropertyChanged;
            KeyFrames.Add(kfItem);
        }
    }

    public AnimationTrack Source => _source;

    public int ElementIndex
    {
        get => _source.ElementIndex;
        set
        {
            if (_source.ElementIndex != value)
            {
                _source.ElementIndex = value;
                OnPropertyChanged();
                OnPropertyChanged(nameof(DisplayName));
            }
        }
    }

    public AnimatableProperty Property
    {
        get => _source.Property;
        set
        {
            if (_source.Property != value)
            {
                _source.Property = value;
                OnPropertyChanged();
                OnPropertyChanged(nameof(DisplayName));
            }
        }
    }

    public string DisplayName
    {
        get
        {
            string elemName;

            if (_owner.SelectedComponent?.IsFromSvg == true)
            {
                string compName = _owner.SelectedComponent.DisplayName;
                elemName = ElementIndex < _owner.SelectedComponent.ElementCount
                    ? $"{compName}[{ElementIndex}]"
                    : $"{compName}[?]";
            }
            else if (_owner.SelectedComponent is not null)
            {
                elemName = _owner.SelectedComponent.DisplayName;
            }
            else if (ElementIndex < _owner.Elements.Count)
            {
                elemName = _owner.Elements[ElementIndex].DisplayName;
            }
            else
            {
                elemName = $"Elem {ElementIndex}";
            }

            return $"{elemName} — {PropertyName(Property)}";
        }
    }

    public int KeyFrameCount => KeyFrames.Count;

    public ObservableCollection<KeyFrameItem> KeyFrames { get; } = new();

    // ── Keyframe operations ─────────────────────────────────

    public void AddKeyFrame(float time, float value, EasingMode easing = default)
    {
        time = Math.Clamp(time, 0f, 1f);
        if (easing == default) easing = EasingMode.Linear;

        var kf = new KeyFrame(time, value, easing);
        _source.KeyFrames.Add(kf);
        _source.KeyFrames.Sort((a, b) => a.Time.CompareTo(b.Time));

        RebuildKeyFrameItems();

        OnPropertyChanged(nameof(KeyFrameCount));
        _owner.InvalidateTimeline();
    }

    public void RemoveKeyFrameAt(int index)
    {
        if (index < 0 || index >= _source.KeyFrames.Count) return;
        if (_source.KeyFrames.Count <= 1) return;

        _source.KeyFrames.RemoveAt(index);
        RebuildKeyFrameItems();

        OnPropertyChanged(nameof(KeyFrameCount));
        _owner.InvalidateTimeline();
    }

    public void SortKeyFrames()
    {
        _source.KeyFrames.Sort((a, b) => a.Time.CompareTo(b.Time));

        var sorted = KeyFrames.OrderBy(vm => vm.Time).ToList();
        for (int i = 0; i < sorted.Count; i++)
        {
            int oldIndex = KeyFrames.IndexOf(sorted[i]);
            if (oldIndex != i)
                KeyFrames.Move(oldIndex, i);
            sorted[i].IsLast = i == sorted.Count - 1;
        }

        OnPropertyChanged(nameof(KeyFrameCount));
        _owner.InvalidateTimeline();
    }

    public void MoveKeyFrame(int index, float newTime)
    {
        if (index < 0 || index >= _source.KeyFrames.Count) return;
        newTime = Math.Clamp(newTime, 0f, 1f);
        _source.KeyFrames[index].Time = newTime;
        SortKeyFrames();
    }

    // ── Helpers ───────────────────────────────────────────

    private void RebuildKeyFrameItems()
    {
        foreach (var item in KeyFrames)
            item.PropertyChanged -= OnKeyFramePropertyChanged;

        KeyFrames.Clear();

        for (int i = 0; i < _source.KeyFrames.Count; i++)
        {
            bool isLast = i == _source.KeyFrames.Count - 1;
            var item = new KeyFrameItem(_source.KeyFrames[i], isLast, this);
            item.PropertyChanged += OnKeyFramePropertyChanged;
            KeyFrames.Add(item);
        }
    }

    private void OnKeyFramePropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(KeyFrameItem.Time)
            or nameof(KeyFrameItem.Value))
        {
            _owner.InvalidateTimeline();
        }
    }

    // ── INotifyPropertyChanged ────────────────────────────

    public event PropertyChangedEventHandler? PropertyChanged;

    protected void OnPropertyChanged([CallerMemberName] string? propertyName = null)
    {
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
    }

    private static string PropertyName(AnimatableProperty p) => p switch
    {
        AnimatableProperty.RelativeX => "Relative X",
        AnimatableProperty.RelativeY => "Relative Y",
        AnimatableProperty.Rotation => "Rotation",
        AnimatableProperty.BaseX => "Base X",
        AnimatableProperty.BaseY => "Base Y",
        AnimatableProperty.FillColorA => "Fill Opacity",
        AnimatableProperty.StrokeColorA => "Stroke Opacity",
        AnimatableProperty.ShapeWidth => "Width",
        AnimatableProperty.ShapeHeight => "Height",
        AnimatableProperty.ShapeCornerRadius => "Corner Radius",
        AnimatableProperty.ShapeRadiusX => "Radius X",
        AnimatableProperty.ShapeRadiusY => "Radius Y",
        AnimatableProperty.ShapeStartAngle => "Start Angle",
        AnimatableProperty.ShapeSweepAngle => "Sweep Angle",
        AnimatableProperty.ShapeCenterX => "Center X",
        AnimatableProperty.ShapeCenterY => "Center Y",
        AnimatableProperty.ShapePointX1 => "Point X1",
        AnimatableProperty.ShapePointY1 => "Point Y1",
        AnimatableProperty.ShapePointX2 => "Point X2",
        AnimatableProperty.ShapePointY2 => "Point Y2",
        AnimatableProperty.ShapePointX3 => "Point X3",
        AnimatableProperty.ShapePointY3 => "Point Y3",
        AnimatableProperty.ShapePointX4 => "Point X4",
        AnimatableProperty.ShapePointY4 => "Point Y4",
        _ => p.ToString(),
    };
}

// ═══════════════════════════════════════════════════════════════
// VectorComponentItem — wraps a VectorComponent for editing
// ═══════════════════════════════════════════════════════════════

/// <summary>
/// Wraps a <see cref="VectorComponent"/> for editing in the storyboard editor UI.
/// Manages per-component tracks, shape properties, and visual configuration.
/// Owned by <see cref="StoryboardEditorView"/> (the page itself).
/// </summary>
public class VectorComponentItem : INotifyPropertyChanged
{
    private readonly VectorComponent _source;
    private readonly StoryboardEditorView _owner;

    public VectorComponentItem(VectorComponent source, StoryboardEditorView owner)
    {
        _source = source ?? throw new ArgumentNullException(nameof(source));
        _owner = owner ?? throw new ArgumentNullException(nameof(owner));

        foreach (var track in source.Storyboard.Tracks)
        {
            var trackItem = new AnimationTrackItem(track, owner);
            trackItem.PropertyChanged += (_, e) =>
            {
                if (e.PropertyName is nameof(AnimationTrackItem.KeyFrameCount))
                    _owner.InvalidateTimeline();
            };
            Tracks.Add(trackItem);
        }
    }

    public VectorComponent Source => _source;
    public Guid Id => _source.Definition.Id;

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
    public bool IsFromSvg => _source.Definition.ShapeType == VectorShapeType.ImportedSvg;
    public bool IsShapeEditable => !IsFromSvg;

    public int ElementCount
    {
        get
        {
            if (IsFromSvg)
                return _source.CachedElements?.Count ?? 0;
            return 1;
        }
    }

    public string ElementCountText => IsFromSvg
        ? $"{ElementCount} elements"
        : "1 shape";

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

    // ── Shape parameter visibility ──────────────────────────

    /// <summary>Whether the shape has Width/Height dimension parameters.</summary>
    public bool HasWidthHeight => ShapeType is VectorShapeType.Rectangle or VectorShapeType.RoundedRectangle;

    /// <summary>Whether the shape has a CornerRadius parameter.</summary>
    public bool HasCornerRadius => ShapeType is VectorShapeType.RoundedRectangle;

    /// <summary>Whether the shape has RadiusX/RadiusY parameters.</summary>
    public bool HasRadiusXY => ShapeType is VectorShapeType.Ellipse or VectorShapeType.Arc;

    /// <summary>Whether the shape has Arc-specific params (center, angles).</summary>
    public bool HasArcParams => ShapeType is VectorShapeType.Arc;

    /// <summary>Whether the shape has line endpoint parameters (X1/Y1, X2/Y2).</summary>
    public bool HasLinePoints => ShapeType is VectorShapeType.Line;

    /// <summary>Whether the shape has cubic bezier control points (X1..X4, Y1..Y4).</summary>
    public bool HasCubicBezierPoints => ShapeType is VectorShapeType.CubicBezier;

    /// <summary>Whether the shape has quadratic bezier control points (X1..X3, Y1..Y3).</summary>
    public bool HasQuadraticBezierPoints => ShapeType is VectorShapeType.QuadraticBezier;

    /// <summary>Whether the shape has bezier control points (cubic or quadratic).</summary>
    public bool HasAnyBezierPoints => HasCubicBezierPoints || HasQuadraticBezierPoints;

    /// <summary>Whether the shape has a vertex point list (Polygon/Polyline).</summary>
    public bool HasPointsList => ShapeType is VectorShapeType.Polygon or VectorShapeType.Polyline;

    /// <summary>Number of vertices for Polygon/Polyline shapes.</summary>
    public int VertexCount => _source.Definition.Points?.Count ?? 0;

    /// <summary>Human-readable vertex count label.</summary>
    public string VertexCountText => ShapeType switch
    {
        VectorShapeType.Polygon => $"{VertexCount} vertices (min 3)",
        VectorShapeType.Polyline => $"{VertexCount} vertices (min 2)",
        _ => "",
    };

    // ── Shape-specific parameter accessors ──────────────────

    /// <summary>Width of Rectangle / RoundedRectangle shapes.</summary>
    public float ShapeWidth
    {
        get => GetShapeParam("Width", 0.3f);
        set
        {
            float clamped = Math.Max(0.001f, value);
            if (!Equals(GetShapeParam("Width", 0.3f), clamped))
            {
                ShapeParameters["Width"] = clamped;
                OnPropertyChanged();
                _owner.RequestPreviewRefresh();
                _owner.RequestComponentClipsRebuild();
            }
        }
    }

    /// <summary>Height of Rectangle / RoundedRectangle shapes.</summary>
    public float ShapeHeight
    {
        get => GetShapeParam("Height", 0.3f);
        set
        {
            float clamped = Math.Max(0.001f, value);
            if (!Equals(GetShapeParam("Height", 0.3f), clamped))
            {
                ShapeParameters["Height"] = clamped;
                OnPropertyChanged();
                _owner.RequestPreviewRefresh();
                _owner.RequestComponentClipsRebuild();
            }
        }
    }

    /// <summary>Corner radius of RoundedRectangle shapes.</summary>
    public float CornerRadius
    {
        get => GetShapeParam("CornerRadius", 0.05f);
        set
        {
            float clamped = Math.Max(0f, value);
            if (!Equals(GetShapeParam("CornerRadius", 0.05f), clamped))
            {
                ShapeParameters["CornerRadius"] = clamped;
                OnPropertyChanged();
                _owner.RequestPreviewRefresh();
                _owner.RequestComponentClipsRebuild();
            }
        }
    }

    /// <summary>X-radius of Ellipse / Arc shapes.</summary>
    public float RadiusX
    {
        get => GetShapeParam("RadiusX", 0.15f);
        set
        {
            float clamped = Math.Max(0.001f, value);
            if (!Equals(GetShapeParam("RadiusX", 0.15f), clamped))
            {
                ShapeParameters["RadiusX"] = clamped;
                OnPropertyChanged();
                _owner.RequestPreviewRefresh();
                _owner.RequestComponentClipsRebuild();
            }
        }
    }

    /// <summary>Y-radius of Ellipse / Arc shapes.</summary>
    public float RadiusY
    {
        get => GetShapeParam("RadiusY", 0.15f);
        set
        {
            float clamped = Math.Max(0.001f, value);
            if (!Equals(GetShapeParam("RadiusY", 0.15f), clamped))
            {
                ShapeParameters["RadiusY"] = clamped;
                OnPropertyChanged();
                _owner.RequestPreviewRefresh();
                _owner.RequestComponentClipsRebuild();
            }
        }
    }

    // ── Arc-specific parameters ─────────────────────────────

    /// <summary>Center X of Arc shapes.</summary>
    public float ArcCenterX
    {
        get => GetShapeParam("CenterX", 0.5f);
        set
        {
            if (!Equals(GetShapeParam("CenterX", 0.5f), value))
            {
                ShapeParameters["CenterX"] = value;
                OnPropertyChanged();
                _owner.RequestPreviewRefresh();
                _owner.RequestComponentClipsRebuild();
            }
        }
    }

    /// <summary>Center Y of Arc shapes.</summary>
    public float ArcCenterY
    {
        get => GetShapeParam("CenterY", 0.5f);
        set
        {
            if (!Equals(GetShapeParam("CenterY", 0.5f), value))
            {
                ShapeParameters["CenterY"] = value;
                OnPropertyChanged();
                _owner.RequestPreviewRefresh();
                _owner.RequestComponentClipsRebuild();
            }
        }
    }

    /// <summary>Start angle (radians) of Arc shapes.</summary>
    public float ArcStartAngle
    {
        get => GetShapeParam("StartAngle", 0f);
        set
        {
            if (!Equals(GetShapeParam("StartAngle", 0f), value))
            {
                ShapeParameters["StartAngle"] = value;
                OnPropertyChanged();
                _owner.RequestPreviewRefresh();
                _owner.RequestComponentClipsRebuild();
            }
        }
    }

    /// <summary>Sweep angle (radians) of Arc shapes.</summary>
    public float ArcSweepAngle
    {
        get => GetShapeParam("SweepAngle", MathF.PI);
        set
        {
            if (!Equals(GetShapeParam("SweepAngle", MathF.PI), value))
            {
                ShapeParameters["SweepAngle"] = value;
                OnPropertyChanged();
                _owner.RequestPreviewRefresh();
                _owner.RequestComponentClipsRebuild();
            }
        }
    }

    // ── Line parameters ─────────────────────────────────────

    public float LineX1 { get => GetShapeParam("X1", 0.1f); set { if (!Equals(GetShapeParam("X1", 0.1f), value)) { ShapeParameters["X1"] = value; OnPropertyChanged(); _owner.RequestPreviewRefresh(); _owner.RequestComponentClipsRebuild(); } } }
    public float LineY1 { get => GetShapeParam("Y1", 0.1f); set { if (!Equals(GetShapeParam("Y1", 0.1f), value)) { ShapeParameters["Y1"] = value; OnPropertyChanged(); _owner.RequestPreviewRefresh(); _owner.RequestComponentClipsRebuild(); } } }
    public float LineX2 { get => GetShapeParam("X2", 0.9f); set { if (!Equals(GetShapeParam("X2", 0.9f), value)) { ShapeParameters["X2"] = value; OnPropertyChanged(); _owner.RequestPreviewRefresh(); _owner.RequestComponentClipsRebuild(); } } }
    public float LineY2 { get => GetShapeParam("Y2", 0.9f); set { if (!Equals(GetShapeParam("Y2", 0.9f), value)) { ShapeParameters["Y2"] = value; OnPropertyChanged(); _owner.RequestPreviewRefresh(); _owner.RequestComponentClipsRebuild(); } } }

    // ── Cubic Bezier parameters ─────────────────────────────

    public float CubicX1 { get => GetShapeParam("X1", 0.1f); set { if (!Equals(GetShapeParam("X1", 0.1f), value)) { ShapeParameters["X1"] = value; OnPropertyChanged(); _owner.RequestPreviewRefresh(); _owner.RequestComponentClipsRebuild(); } } }
    public float CubicY1 { get => GetShapeParam("Y1", 0.3f); set { if (!Equals(GetShapeParam("Y1", 0.3f), value)) { ShapeParameters["Y1"] = value; OnPropertyChanged(); _owner.RequestPreviewRefresh(); _owner.RequestComponentClipsRebuild(); } } }
    public float CubicX2 { get => GetShapeParam("X2", 0.3f); set { if (!Equals(GetShapeParam("X2", 0.3f), value)) { ShapeParameters["X2"] = value; OnPropertyChanged(); _owner.RequestPreviewRefresh(); _owner.RequestComponentClipsRebuild(); } } }
    public float CubicY2 { get => GetShapeParam("Y2", 0.7f); set { if (!Equals(GetShapeParam("Y2", 0.7f), value)) { ShapeParameters["Y2"] = value; OnPropertyChanged(); _owner.RequestPreviewRefresh(); _owner.RequestComponentClipsRebuild(); } } }
    public float CubicX3 { get => GetShapeParam("X3", 0.7f); set { if (!Equals(GetShapeParam("X3", 0.7f), value)) { ShapeParameters["X3"] = value; OnPropertyChanged(); _owner.RequestPreviewRefresh(); _owner.RequestComponentClipsRebuild(); } } }
    public float CubicY3 { get => GetShapeParam("Y3", 0.3f); set { if (!Equals(GetShapeParam("Y3", 0.3f), value)) { ShapeParameters["Y3"] = value; OnPropertyChanged(); _owner.RequestPreviewRefresh(); _owner.RequestComponentClipsRebuild(); } } }
    public float CubicX4 { get => GetShapeParam("X4", 0.9f); set { if (!Equals(GetShapeParam("X4", 0.9f), value)) { ShapeParameters["X4"] = value; OnPropertyChanged(); _owner.RequestPreviewRefresh(); _owner.RequestComponentClipsRebuild(); } } }
    public float CubicY4 { get => GetShapeParam("Y4", 0.7f); set { if (!Equals(GetShapeParam("Y4", 0.7f), value)) { ShapeParameters["Y4"] = value; OnPropertyChanged(); _owner.RequestPreviewRefresh(); _owner.RequestComponentClipsRebuild(); } } }

    // ── Quadratic Bezier parameters ─────────────────────────

    public float QuadX1 { get => GetShapeParam("X1", 0.1f); set { if (!Equals(GetShapeParam("X1", 0.1f), value)) { ShapeParameters["X1"] = value; OnPropertyChanged(); _owner.RequestPreviewRefresh(); _owner.RequestComponentClipsRebuild(); } } }
    public float QuadY1 { get => GetShapeParam("Y1", 0.1f); set { if (!Equals(GetShapeParam("Y1", 0.1f), value)) { ShapeParameters["Y1"] = value; OnPropertyChanged(); _owner.RequestPreviewRefresh(); _owner.RequestComponentClipsRebuild(); } } }
    public float QuadX2 { get => GetShapeParam("X2", 0.5f); set { if (!Equals(GetShapeParam("X2", 0.5f), value)) { ShapeParameters["X2"] = value; OnPropertyChanged(); _owner.RequestPreviewRefresh(); _owner.RequestComponentClipsRebuild(); } } }
    public float QuadY2 { get => GetShapeParam("Y2", 0.9f); set { if (!Equals(GetShapeParam("Y2", 0.9f), value)) { ShapeParameters["Y2"] = value; OnPropertyChanged(); _owner.RequestPreviewRefresh(); _owner.RequestComponentClipsRebuild(); } } }
    public float QuadX3 { get => GetShapeParam("X3", 0.9f); set { if (!Equals(GetShapeParam("X3", 0.9f), value)) { ShapeParameters["X3"] = value; OnPropertyChanged(); _owner.RequestPreviewRefresh(); _owner.RequestComponentClipsRebuild(); } } }
    public float QuadY3 { get => GetShapeParam("Y3", 0.1f); set { if (!Equals(GetShapeParam("Y3", 0.1f), value)) { ShapeParameters["Y3"] = value; OnPropertyChanged(); _owner.RequestPreviewRefresh(); _owner.RequestComponentClipsRebuild(); } } }

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

    public ObservableCollection<AnimationTrackItem> Tracks { get; } = new();
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

    public Color StrokeColorPreview => Color.FromRgba(StrokeR, StrokeG, StrokeB, (int)Math.Round(StrokeA * 255));
    public string StrokeColorHex => $"#{StrokeR:X2}{StrokeG:X2}{StrokeB:X2}";
    public Color FillColorPreview => Color.FromRgba(FillR, FillG, FillB, (int)Math.Round(FillA * 255));
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

        var trackItem = new AnimationTrackItem(track, _owner);
        trackItem.PropertyChanged += (_, _) => _owner.InvalidateTimeline();
        Tracks.Add(trackItem);

        OnPropertyChanged(nameof(TrackCount));
        _owner.InvalidateTimeline();
    }

    public void RemoveTrack(AnimationTrackItem trackItem)
    {
        _source.Storyboard.Tracks.Remove(trackItem.Source);
        Tracks.Remove(trackItem);

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
