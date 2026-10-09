using projectFrameCut.Render.RenderAPIBase.EffectAndMixture;
using System.Reflection;

namespace projectFrameCut.Render.RenderAPIBase.ClipAndTrack;

public record ClipArgumentFieldDescriptor : EffectArgumentFieldDescriptor
{
    [System.Text.Json.Serialization.JsonIgnore]
    public required Func<IClip, object> ReadValue { get; init; }
}

public static class ClipArgumentHandler
{
    private static readonly MethodInfo CloneMethod = typeof(object).GetMethod("MemberwiseClone", BindingFlags.Instance | BindingFlags.NonPublic)!;

    public static IReadOnlyDictionary<string, ClipArgumentFieldDescriptor> DefaultFields { get; } =
        new Dictionary<string, ClipArgumentFieldDescriptor>
        {
            [nameof(IClip.TargetX)] = Integer(nameof(IClip.TargetX), c => c.TargetX),
            [nameof(IClip.TargetY)] = Integer(nameof(IClip.TargetY), c => c.TargetY),
            [nameof(IClip.TargetWidth)] = Integer(nameof(IClip.TargetWidth), c => c.TargetWidth),
            [nameof(IClip.TargetHeight)] = Integer(nameof(IClip.TargetHeight), c => c.TargetHeight),
            [nameof(IClip.Rotation)] = new()
            {
                Id = nameof(IClip.Rotation),
                FieldType = EffectArgumentFieldType.Numeric,
                DefaultValue = "0",
                ReadValue = c => c.Rotation,
            },
        };

    private static ClipArgumentFieldDescriptor Integer(string id, Func<IClip, object> read) => new()
    {
        Id = id,
        FieldType = EffectArgumentFieldType.Integer,
        DefaultValue = "0",
        ReadValue = read,
    };

    // Frame copies retain their concrete interfaces and borrow the source's resources.
    public static IClip Copy(IClip clip) => (IClip)CloneMethod.Invoke(clip, null)!;

    public static IClip Handle(IClip clip, IReadOnlyDictionary<string, object> arguments)
    {
        var result = clip.CopyForFrame();
        foreach (var (key, value) in arguments)
        {
            if (key == nameof(IClip.Rotation))
            {
                if (!EffectParamConvert.TryConvertToFloat(value, out float angle) || !float.IsFinite(angle))
                    throw new ArgumentException($"Invalid clip argument '{key}': {value}.");
                result.Rotation = angle;
                continue;
            }
            if (!EffectParamConvert.TryConvertToInt(value, out int n))
                throw new ArgumentException($"Invalid clip argument '{key}': {value}.");
            switch (key)
            {
                case nameof(IClip.TargetX):
                    result.TargetX = n;
                    break;
                case nameof(IClip.TargetY):
                    result.TargetY = n;
                    break;
                case nameof(IClip.TargetWidth):
                    result.TargetWidth = n;
                    break;
                case nameof(IClip.TargetHeight):
                    result.TargetHeight = n;
                    break;
                default:
                    throw new ArgumentException($"Clip '{clip.TypeName}' has no handler for '{key}'.");
            }
        }
        return result;
    }
}
