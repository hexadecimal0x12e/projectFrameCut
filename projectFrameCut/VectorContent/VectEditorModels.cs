using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using projectFrameCut.Drawing.Vector;
using projectFrameCut.Render.RenderAPIBase.VectorContent;
using projectFrameCut.Render.VectorContent;
using projectFrameCut.Render.VectorContent.Components;
using Point = projectFrameCut.Drawing.Vector.Point;

namespace projectFrameCut.DraftStuff;

// ═══════════════════════════════════════════════════════════════
// Shape gallery provider — maps component TypeName to display info
// ═══════════════════════════════════════════════════════════════

public static class ShapeGalleryProvider
{
    public static readonly IReadOnlyList<ShapeGalleryItem> Items = new List<ShapeGalleryItem>
    {
        new() { TypeName = "Rectangle",         DisplayName = "Rectangle",        Icon = "▭" },
        new() { TypeName = "RoundedRectangle",  DisplayName = "Rounded Rect",     Icon = "▢" },
        new() { TypeName = "Ellipse",           DisplayName = "Ellipse",          Icon = "⬭" },
        new() { TypeName = "Line",              DisplayName = "Line",             Icon = "╱" },
        new() { TypeName = "CubicBezier",       DisplayName = "Cubic Bezier",     Icon = "∿" },
        new() { TypeName = "QuadraticBezier",   DisplayName = "Quad Bezier",      Icon = "⌈" },
        new() { TypeName = "Arc",               DisplayName = "Arc",              Icon = "⌒" },
        new() { TypeName = "Polygon",           DisplayName = "Polygon",          Icon = "⬣" },
        new() { TypeName = "Polyline",          DisplayName = "Polyline",         Icon = "⦚" },
    };

    public static string GetIcon(string typeName) =>
        Items.FirstOrDefault(i => i.TypeName == typeName)?.Icon ?? "□";

    public static string GetDisplayName(string typeName) =>
        Items.FirstOrDefault(i => i.TypeName == typeName)?.DisplayName ?? typeName;
}

/// <summary>Display model for a shape in the shape gallery panel.</summary>
public class ShapeGalleryItem
{
    public string TypeName { get; init; } = "";
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
// KeyFrameItem — wrapper around VectorAnimationKeyFrame for two-way binding
// ═══════════════════════════════════════════════════════════════

/// <summary>
/// Wraps a <see cref="VectorAnimationKeyFrame"/> for two-way binding in the
/// vector animation editor UI.
/// </summary>
public class KeyFrameItem : INotifyPropertyChanged
{
    private VectorAnimationKeyFrame _source;
    private AnimationTrackItem? _parentTrack;

    public KeyFrameItem(VectorAnimationKeyFrame source, bool isLast, AnimationTrackItem? parentTrack = null)
    {
        _source = source;
        IsLast = isLast;
        _parentTrack = parentTrack;
    }

    public VectorAnimationKeyFrame Source => _source;
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
// AnimationTrackItem — wraps keyframes for a single target field
// ═══════════════════════════════════════════════════════════════

/// <summary>
/// Represents a single animation track targeting one field.
/// Owned by <see cref="VectorComponentItem"/>.
/// </summary>
public class AnimationTrackItem : INotifyPropertyChanged
{
    private readonly List<VectorAnimationKeyFrame> _keyFrames;
    private readonly VectorComponentItem _owner;

    public AnimationTrackItem(VectorComponentItem owner, string targetFieldId, string fieldDisplayName)
    {
        _keyFrames = owner.Source.AnimationFrames
            .Where(kf => kf.TargetFieldId == targetFieldId)
            .ToList();
        _owner = owner;
        TargetFieldId = targetFieldId;
        FieldDisplayName = fieldDisplayName;

        // Build keyframe child items
        for (int i = 0; i < _keyFrames.Count; i++)
        {
            bool isLast = i == _keyFrames.Count - 1;
            var kfItem = new KeyFrameItem(_keyFrames[i], isLast, this);
            kfItem.PropertyChanged += OnKeyFramePropertyChanged;
            KeyFrames.Add(kfItem);
        }
    }

    public string TargetFieldId { get; }
    public string FieldDisplayName { get; }

    public string DisplayName
    {
        get
        {
            string compName = _owner.DisplayName;
            return $"{compName} — {FieldDisplayName}";
        }
    }

    public int KeyFrameCount => KeyFrames.Count;

    public ObservableCollection<KeyFrameItem> KeyFrames { get; } = new();

    // ── Keyframe operations ─────────────────────────────────

    public void AddKeyFrame(float time, float value, EasingMode easing = default)
    {
        time = Math.Clamp(time, 0f, 1f);
        if (easing == default) easing = EasingMode.Linear;

        var kf = new VectorAnimationKeyFrame(time, value, easing);
        kf.TargetField = _owner.Source.AnimatableFields?.GetValueOrDefault(TargetFieldId) as AnimatableField;
        _keyFrames.Add(kf);
        _owner.Source.AnimationFrames.Add(kf);

        SortKeyFrames();
        OnPropertyChanged(nameof(KeyFrameCount));
    }

    public void RemoveKeyFrameAt(int index)
    {
        if (index < 0 || index >= _keyFrames.Count) return;
        if (_keyFrames.Count <= 1) return;

        var removed = _keyFrames[index];
        _keyFrames.RemoveAt(index);
        _owner.Source.AnimationFrames.Remove(removed);
        RebuildKeyFrameItems();

        OnPropertyChanged(nameof(KeyFrameCount));
    }

    public void SortKeyFrames()
    {
        _keyFrames.Sort((a, b) => a.Time.CompareTo(b.Time));

        var sorted = KeyFrames.OrderBy(vm => vm.Time).ToList();
        for (int i = 0; i < sorted.Count; i++)
        {
            int oldIndex = KeyFrames.IndexOf(sorted[i]);
            if (oldIndex != i)
                KeyFrames.Move(oldIndex, i);
            sorted[i].IsLast = i == sorted.Count - 1;
        }

        OnPropertyChanged(nameof(KeyFrameCount));
    }

    public void MoveKeyFrame(int index, float newTime)
    {
        if (index < 0 || index >= _keyFrames.Count) return;
        newTime = Math.Clamp(newTime, 0f, 1f);
        _keyFrames[index].Time = newTime;
        SortKeyFrames();
    }

    // ── Evaluate track value at a given progress ────────────

    public float GetValue(float progress)
    {
        if (_keyFrames.Count == 0) return 0f;

        if (_keyFrames.Count == 1)
            return _keyFrames[0].Value;

        progress = Math.Clamp(progress, 0f, 1f);

        if (progress <= _keyFrames[0].Time)
            return _keyFrames[0].Value;

        var last = _keyFrames[^1];
        if (progress >= last.Time)
            return last.Value;

        for (int i = 1; i < _keyFrames.Count; i++)
        {
            var prev = _keyFrames[i - 1];
            var next = _keyFrames[i];

            if (progress >= next.Time)
                continue;

            float span = next.Time - prev.Time;
            if (span <= 0f)
                return next.Value;

            float t = (progress - prev.Time) / span;
            float eased = EasingFunctions.Apply(prev.Easing, t);
            return prev.Value + (next.Value - prev.Value) * eased;
        }

        return last.Value;
    }

    // ── Helpers ───────────────────────────────────────────

    private void RebuildKeyFrameItems()
    {
        foreach (var item in KeyFrames)
            item.PropertyChanged -= OnKeyFramePropertyChanged;

        KeyFrames.Clear();

        for (int i = 0; i < _keyFrames.Count; i++)
        {
            bool isLast = i == _keyFrames.Count - 1;
            var item = new KeyFrameItem(_keyFrames[i], isLast, this);
            item.PropertyChanged += OnKeyFramePropertyChanged;
            KeyFrames.Add(item);
        }
    }

    private void OnKeyFramePropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(KeyFrameItem.Time)
            or nameof(KeyFrameItem.Value))
        {
            _owner.InvalidateTimeline?.Invoke();
        }
    }

    // ── INotifyPropertyChanged ────────────────────────────

    public event PropertyChangedEventHandler? PropertyChanged;

    protected void OnPropertyChanged([CallerMemberName] string? propertyName = null)
    {
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
    }
}

// ═══════════════════════════════════════════════════════════════
// VectorComponentItem — wraps an IVectorComponent for editing
// ═══════════════════════════════════════════════════════════════

/// <summary>
/// Wraps an <see cref="IVectorComponent"/> for editing in the vector animation editor UI.
/// Manages per-component animation tracks and shape parameters.
/// Owned by <see cref="VectorContentEditorView"/>.
/// </summary>
public class VectorComponentItem : INotifyPropertyChanged
{
    private readonly IVectorComponent _source;
    private readonly VectorContentEditorView _owner;

    /// <summary>Callback invoked when timeline needs repainting.</summary>
    public Action? InvalidateTimeline { get; set; }

    // ── Editor-side storage for concepts not on IVectorComponent ──

    /// <summary>Per-component animation duration in frames.</summary>
    public uint EditorDurationInFrames { get; set; } = 30;

    /// <summary>SVG import: cached parsed elements.</summary>
    public List<VectorCanvasElement>? EditorCachedElements { get; set; }

    /// <summary>SVG import: source file path.</summary>
    public string? EditorSourceFilePath { get; set; }

    /// <summary>Polygon/Polyline vertices.</summary>
    public List<Point> EditorPoints { get; set; } = new();

    public VectorComponentItem(IVectorComponent source, VectorContentEditorView owner)
    {
        _source = source ?? throw new ArgumentNullException(nameof(source));
        _owner = owner ?? throw new ArgumentNullException(nameof(owner));
        InvalidateTimeline = owner.InvalidateTimeline;

        // Build tracks from animation frames grouped by target field
        RebuildTracks();
    }

    public IVectorComponent Source => _source;
    public Guid Id => _source.Id;

    public string DisplayName
    {
        get => _source.Name;
        set
        {
            if (_source.Name != value)
            {
                _source.Name = value;
                OnPropertyChanged();
            }
        }
    }

    public string TypeName => _source.TypeName;
    public string ShapeIcon => ShapeGalleryProvider.GetIcon(TypeName);

    public bool IsFromSvg => TypeName == "ImportedSvg";
    public bool IsShapeEditable => !IsFromSvg;

    public int ElementCount
    {
        get
        {
            if (_source is ComponentGroup group)
                return group.Children.Count;
            if (IsFromSvg)
                return EditorCachedElements?.Count ?? 0;
            return 1;
        }
    }

    public string ElementCountText => _source switch
    {
        ComponentGroup group => $"{group.Children.Count} items",
        _ when IsFromSvg => $"{ElementCount} elements",
        _ => "1 shape",
    };

    // ── SVG-specific properties ──────────────────────────────

    public string? SourceFilePath
    {
        get => EditorSourceFilePath;
        set
        {
            if (EditorSourceFilePath != value)
            {
                EditorSourceFilePath = value;
                OnPropertyChanged();
            }
        }
    }

    public string ShapeTypeDisplayName => IsFromSvg
        ? System.IO.Path.GetFileName(SourceFilePath ?? "SVG")
        : ShapeGalleryProvider.GetDisplayName(TypeName);

    // ── Parameter helpers (generic shape access — used by handlers) ──

    private float GetParam(string key, float defaultValue)
    {
        if (_source.Parameters.TryGetValue(key, out var val))
        {
            return val switch
            {
                float f => f,
                double d => (float)d,
                int i => i,
                uint u => u,
                _ => defaultValue,
            };
        }
        return defaultValue;
    }

    private void SetParam(string key, float value)
    {
        _source.Parameters[key] = value;
        OnPropertyChanged();
    }

    public Dictionary<string, object> Parameters => _source.Parameters;

    public float GetShapeParam(string key, float defaultValue)
    {
        return GetParam(key, defaultValue);
    }

    public void SetShapeParam(string key, float value)
    {
        SetParam(key, value);
        OnPropertyChanged(nameof(Parameters));
        _owner.RequestPreviewRefresh();
    }

    // ── Transform property accessors (used by interactive editor) ──

    public float RelativeX
    {
        get => GetParam("RelativeX", 0.5f);
        set { SetParam("RelativeX", Math.Clamp(value, 0f, 1f)); _owner.RequestPreviewRefresh(); }
    }

    public float RelativeY
    {
        get => GetParam("RelativeY", 0.5f);
        set { SetParam("RelativeY", Math.Clamp(value, 0f, 1f)); _owner.RequestPreviewRefresh(); }
    }

    public float Rotation
    {
        get => GetParam("Rotation", 0f);
        set { SetParam("Rotation", value); _owner.RequestPreviewRefresh(); }
    }

    public int LayerIndex
    {
        get => _source.Index;
        set { _source.Index = value; OnPropertyChanged(); _owner.RequestPreviewRefresh(); }
    }

    // ── Duration ────────────────────────────────────────────

    public uint DurationInFrames
    {
        get => EditorDurationInFrames;
        set
        {
            if (EditorDurationInFrames != value)
            {
                EditorDurationInFrames = Math.Max(1, value);
                OnPropertyChanged();
                OnPropertyChanged(nameof(DurationText));
                _owner.InvalidateTimeline();
            }
        }
    }

    public string DurationText => $"{DurationInFrames} frames";

    // ── Tracks ──────────────────────────────────────────────

    public ObservableCollection<AnimationTrackItem> Tracks { get; } = new();
    public int TrackCount => Tracks.Count;

    public void RebuildTracks()
    {
        Tracks.Clear();

        var grouped = _source.AnimationFrames
            .Where(kf => !string.IsNullOrWhiteSpace(kf.TargetFieldId))
            .GroupBy(kf => kf.TargetFieldId);

        foreach (var group in grouped)
        {
            var fieldDisplayName = _source.AnimatableFields?
                .GetValueOrDefault(group.Key)?.DisplayName ?? group.Key;
            var track = new AnimationTrackItem(this, group.Key, fieldDisplayName);
            track.PropertyChanged += (_, e) =>
            {
                if (e.PropertyName is nameof(AnimationTrackItem.KeyFrameCount))
                    _owner.InvalidateTimeline();
            };
            Tracks.Add(track);
        }

        OnPropertyChanged(nameof(TrackCount));
    }

    // ── Track management ────────────────────────────────────

    public void AddTrack(string targetFieldId)
    {
        var field = _source.AnimatableFields?.GetValueOrDefault(targetFieldId) as AnimatableField;
        float defaultValue = field is not null
            ? (field.MinimumValue + field.MaximumValue) / 2f
            : 0.5f;

        var kf1 = new VectorAnimationKeyFrame(0f, defaultValue, EasingMode.Linear);
        kf1.TargetField = field;
        var kf2 = new VectorAnimationKeyFrame(1f, defaultValue, EasingMode.Linear);
        kf2.TargetField = field;

        _source.AnimationFrames.Add(kf1);
        _source.AnimationFrames.Add(kf2);

        RebuildTracks();
        OnPropertyChanged(nameof(TrackCount));
        _owner.InvalidateTimeline();
    }

    public void RemoveTrack(AnimationTrackItem trackItem)
    {
        var toRemove = _source.AnimationFrames
            .Where(kf => kf.TargetFieldId == trackItem.TargetFieldId)
            .ToList();

        foreach (var kf in toRemove)
            _source.AnimationFrames.Remove(kf);

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
