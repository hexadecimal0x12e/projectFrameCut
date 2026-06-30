using projectFrameCut.Render.RenderAPIBase.Animation;
using System.ComponentModel;
using System.Runtime.CompilerServices;

namespace projectFrameCut.ViewModels;

/// <summary>
/// Wraps a <see cref="KeyFrame"/> for two-way binding in the storyboard editor UI.
/// </summary>
public class KeyFrameItemViewModel : INotifyPropertyChanged
{
    private KeyFrame _source;
    private AnimationTrackItemViewModel? _parentTrack;

    public KeyFrameItemViewModel(KeyFrame source, bool isLast,
        AnimationTrackItemViewModel? parentTrack = null)
    {
        _source = source;
        IsLast = isLast;
        _parentTrack = parentTrack;
    }

    /// <summary>Reference to the underlying model — edits apply directly.</summary>
    public KeyFrame Source => _source;

    /// <summary>The track that owns this keyframe (null for legacy SVG tracks).</summary>
    public AnimationTrackItemViewModel? ParentTrack => _parentTrack;

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
                // Re-sort keyframes in the parent track when time changes
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
