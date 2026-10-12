using projectFrameCut.Drawing.Vector;
using projectFrameCut.Shared;

namespace projectFrameCut.Render.RenderAPIBase.EffectAndMixture;

/// <summary>Processes frame-owned, clip-local vector data before rasterization.</summary>
public interface IVectorPictureEffect : IEffect
{
    EffectType IEffect.TypeOfEffect => EffectType.VectorPictureEffect;
    VectorPicture Process(VectorPicture source, float progress);
}

public static class EffectPipelineExtensions
{
    public static EffectPipeline GetPipeline(this EffectType type) => type is
        EffectType.VectorComponentEffect or EffectType.TextEffect or EffectType.ContinuousTextEffect or EffectType.VectorPictureEffect
            ? EffectPipeline.NativeContent : EffectPipeline.Picture;

    public static bool CanConnectContent(this IEffectProvider source, IEffectProvider target)
        => source.CanConnectContent(target, null);

    public static bool CanConnectContent(this IEffectProvider source, IEffectProvider target, string? outputId)
        => source.CanConnectContent(target, outputId, null);

    public static bool CanConnectContent(this IEffectProvider source, IEffectProvider target, string? outputId, EffectFieldTypeResolution? types)
    {
        if (!target.InFields.TryGetValue(EffectProviderAnchorExtensions.InputKey, out var input)
            || source.TypeOfEffect == EffectType.Transform || source is ClipArgumentProvider) return false;
        if (target.TypeOfEffect.GetPipeline() == EffectPipeline.Picture)
            return source.TypeOfEffect.GetPipeline() == EffectPipeline.Picture
                && source.TryGetOutputField(outputId, out var output)
                && ((types?.GetOutputType(source, outputId) ?? output.FieldType).HasFlag(EffectArgumentFieldType.Any)
                    || EffectFieldTypes.AreCompatible(types?.GetOutputType(source, outputId) ?? output.FieldType,
                        types?.GetInputType(target, EffectProviderAnchorExtensions.InputKey) ?? input.FieldType));
        if (!source.InFields.TryGetValue(EffectProviderAnchorExtensions.InputKey, out var sourceInput)
            || !(sourceInput.FieldType.IsPicture() || sourceInput.FieldType.HasFlag(EffectArgumentFieldType.Any))
            || !(input.FieldType.IsPicture() || input.FieldType.HasFlag(EffectArgumentFieldType.Any))
            || source.TypeOfEffect.GetPipeline() != target.TypeOfEffect.GetPipeline()) return false;
        return target.TypeOfEffect == EffectType.VectorPictureEffect
            || source.TypeOfEffect == target.TypeOfEffect
            || (source.TypeOfEffect is EffectType.TextEffect or EffectType.ContinuousTextEffect)
                && (target.TypeOfEffect is EffectType.TextEffect or EffectType.ContinuousTextEffect);
    }
}
