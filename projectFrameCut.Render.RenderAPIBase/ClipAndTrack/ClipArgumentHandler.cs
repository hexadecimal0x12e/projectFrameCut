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
    public const string ShouldRenderKey = "ShouldRender";
    private static readonly MethodInfo CloneMethod = typeof(object).GetMethod("MemberwiseClone", BindingFlags.Instance | BindingFlags.NonPublic)!;

    public static IReadOnlyDictionary<string, ClipArgumentFieldDescriptor> DefaultFields { get; } =
        new Dictionary<string, ClipArgumentFieldDescriptor>();
    public static IReadOnlyDictionary<string, ClipArgumentFieldDescriptor> SourceCropFields => DefaultFields;

    // Frame copies retain their concrete interfaces and borrow the source's resources.
    public static IClip Copy(IClip clip) => (IClip)CloneMethod.Invoke(clip, null)!;

    public static IClip Handle(IClip clip, IReadOnlyDictionary<string, object> arguments)
    {
        var result = clip.CopyForFrame();
        foreach (var (key, value) in arguments)
        {
            if (key is nameof(IClip.Rotation) or nameof(IClip.Opacity))
            {
                if (!EffectParamConvert.TryConvertToFloat(value, out float f) || !float.IsFinite(f))
                    throw new ArgumentException($"Invalid clip argument '{key}': {value}.");
                if (key == nameof(IClip.Opacity)) result.Opacity = Math.Clamp(f, 0, 1);
                else result.Rotation = f;
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
                case nameof(IClip.StartingX):
                    result.StartingX = Math.Max(0, n);
                    break;
                case nameof(IClip.StartingY):
                    result.StartingY = Math.Max(0, n);
                    break;
                default:
                    throw new ArgumentException($"Clip '{clip.TypeName}' has no handler for '{key}'.");
            }
        }
        return result;
    }
}
