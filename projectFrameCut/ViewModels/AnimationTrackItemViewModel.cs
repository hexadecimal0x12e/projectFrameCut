using projectFrameCut.Render.RenderAPIBase.Animation;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;

namespace projectFrameCut.ViewModels;

/// <summary>
/// Wraps an <see cref="AnimationTrack"/> for editing in the storyboard editor UI.
/// Tracks are owned by a parent <see cref="StoryboardEditorViewModel"/>.
/// </summary>
public class AnimationTrackItemViewModel : INotifyPropertyChanged
{
    private readonly AnimationTrack _source;
    private readonly StoryboardEditorViewModel _owner;

    public AnimationTrackItemViewModel(AnimationTrack source, StoryboardEditorViewModel owner)
    {
        _source = source;
        _owner = owner;

        // Build keyframe child VMs
        for (int i = 0; i < source.KeyFrames.Count; i++)
        {
            bool isLast = i == source.KeyFrames.Count - 1;
            var kfVm = new KeyFrameItemViewModel(source.KeyFrames[i], isLast, this);
            kfVm.PropertyChanged += OnKeyFramePropertyChanged;
            KeyFrames.Add(kfVm);
        }
    }

    /// <summary>Reference to the underlying model.</summary>
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

            // SVG component tracks: show element index within the SVG
            if (_owner.SelectedComponent?.IsFromSvg == true)
            {
                string compName = _owner.SelectedComponent.DisplayName;
                elemName = ElementIndex < _owner.SelectedComponent.ElementCount
                    ? $"{compName}[{ElementIndex}]"
                    : $"{compName}[?]";
            }
            // Manual component tracks: show component name
            else if (_owner.SelectedComponent is not null)
            {
                elemName = _owner.SelectedComponent.DisplayName;
            }
            // Legacy SVG global tracks: show element from Elements list
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

    /// <summary>Child keyframe ViewModels.</summary>
    public ObservableCollection<KeyFrameItemViewModel> KeyFrames { get; } = new();

    // ── Commands ──────────────────────────────────────────

    /// <summary>Add a keyframe at the specified normalised time, using
    /// interpolated value from the surrounding keyframes.</summary>
    public void AddKeyFrame(float time, float value, EasingMode easing = default)
    {
        time = Math.Clamp(time, 0f, 1f);

        if (easing == default)
            easing = EasingMode.Linear;

        var kf = new KeyFrame(time, value, easing);
        _source.KeyFrames.Add(kf);

        // Re-sort keyframes by time
        _source.KeyFrames.Sort((a, b) => a.Time.CompareTo(b.Time));

        // Rebuild child VMs
        RebuildKeyFrameVMs();

        OnPropertyChanged(nameof(KeyFrameCount));
        _owner.InvalidateTimeline();
    }

    public void RemoveKeyFrameAt(int index)
    {
        if (index < 0 || index >= _source.KeyFrames.Count)
            return;
        if (_source.KeyFrames.Count <= 1)
            return; // Must have at least one keyframe

        _source.KeyFrames.RemoveAt(index);
        RebuildKeyFrameVMs();

        OnPropertyChanged(nameof(KeyFrameCount));
        _owner.InvalidateTimeline();
    }

    /// <summary>
    /// Re-sort keyframes by time within the child VM collection,
    /// preserving existing VM references so that selection is not lost.
    /// Called after a keyframe's Time property is directly edited.
    /// </summary>
    public void SortKeyFrames()
    {
        // Sort the source model first
        _source.KeyFrames.Sort((a, b) => a.Time.CompareTo(b.Time));

        // Re-order VMs to match, preserving instances
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

    /// <summary>
    /// Move a keyframe to a new normalised time by dragging.
    /// </summary>
    public void MoveKeyFrame(int index, float newTime)
    {
        if (index < 0 || index >= _source.KeyFrames.Count)
            return;

        newTime = Math.Clamp(newTime, 0f, 1f);
        _source.KeyFrames[index].Time = newTime;

        // Re-sort preserving VM references
        SortKeyFrames();
    }

    // ── Helpers ───────────────────────────────────────────

    private void RebuildKeyFrameVMs()
    {
        // Unsubscribe old
        foreach (var vm in KeyFrames)
            vm.PropertyChanged -= OnKeyFramePropertyChanged;

        KeyFrames.Clear();

        for (int i = 0; i < _source.KeyFrames.Count; i++)
        {
            bool isLast = i == _source.KeyFrames.Count - 1;
            var vm = new KeyFrameItemViewModel(_source.KeyFrames[i], isLast, this);
            vm.PropertyChanged += OnKeyFramePropertyChanged;
            KeyFrames.Add(vm);
        }
    }

    private void OnKeyFramePropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(KeyFrameItemViewModel.Time)
            or nameof(KeyFrameItemViewModel.Value))
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
        _ => p.ToString(),
    };
}
