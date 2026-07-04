using System.Text.Json.Serialization;

namespace projectFrameCut.Render.RenderAPIBase.VectorContent;

/// <summary>
/// A single keyframe that records a value at a normalised point in time
/// and the easing curve used to reach the <b>next</b> keyframe.
/// </summary>
public record VectorAnimationKeyFrame
{
    /// <summary>
    /// The target field that this keyframe animates.
    /// Runtime-only reference; not serialised.
    /// </summary>
    [JsonIgnore]
    public AnimatableField? TargetField
    {
        get => _targetField;
        set
        {
            _targetField = value;
            if (value is not null)
                _targetFieldId = value.Id;
        }
    }
    private AnimatableField? _targetField;

    /// <summary>
    /// The ID of the target field that this keyframe animates.
    /// Persisted during serialization so that tracks/groups survive save/reload.
    /// </summary>
    public string TargetFieldId
    {
        get => _targetFieldId;
        set => _targetFieldId = value;
    }
    private string _targetFieldId = string.Empty;

    /// <summary>
    /// Normalised time [0…1] within the track's parent duration.
    /// </summary>
    public float Time { get; set; }

    /// <summary>
    /// The animated (float) value at this keyframe.
    /// </summary>
    public float Value { get; set; }

    /// <summary>
    /// Easing function used for the segment <b>from</b> this keyframe
    /// <b>to</b> the next one. Ignored for the last keyframe.
    /// </summary>
    public EasingMode Easing { get; set; } = EasingMode.Linear;

    public VectorAnimationKeyFrame() { }

    public VectorAnimationKeyFrame(float time, float value, EasingMode easing = EasingMode.Linear)
    {
        Time = time;
        Value = value;
        Easing = easing;
    }
}
