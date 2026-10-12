using projectFrameCut.Drawing.Vector;
using projectFrameCut.Shared;

namespace projectFrameCut.Render.RenderAPIBase.EffectAndMixture;

/// <summary>Processes frame-owned, clip-local vector data before rasterization.</summary>
public interface IVectorPictureEffect : IEffect
{
    EffectType IEffect.TypeOfEffect => EffectType.VectorPictureEffect;
    VectorPicture Process(VectorPicture source, float progress);

    VectorPicture Process(VectorEffectFrameContext context, VectorPicture source) => Process(source, context.Progress);
}

public sealed record VectorEffectFrameContext(projectFrameCut.Render.RenderAPIBase.ClipAndTrack.IClip Clip,
    uint FrameIndex, float Progress, int Width, int Height);

public static class EffectPipelineExtensions
{
    public const string PipelineKey = "__EffectPipeline__";

    public static EffectPipeline GetPipeline(this IEffectProvider provider) =>
        provider.Target.HasFlag(EffectTarget.ValueProvider)
            && Enum.TryParse<EffectPipeline>(provider.MetaData.GetValueOrDefault(PipelineKey)?.ToString(), out var pipeline)
            && Enum.IsDefined(pipeline) ? pipeline : provider.TypeOfEffect.GetPipeline();

    public static void SetPipeline(this IEffectProvider provider, EffectPipeline pipeline)
    {
        if (provider.Target.HasFlag(EffectTarget.ValueProvider)) provider.MetaData[PipelineKey] = pipeline.ToString();
    }

    public static EffectArgumentFieldType GetContentType(this EffectPipeline pipeline) =>
        pipeline == EffectPipeline.NativeContent ? EffectArgumentFieldType.VectorPicture : EffectArgumentFieldType.IPicture;

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
        if (target.GetPipeline() == EffectPipeline.Picture || target.TypeOfEffect == EffectType.VectorPictureEffect
            || target.Target.HasFlag(EffectTarget.ValueProvider))
            return source.GetPipeline() == target.GetPipeline()
                && source.TryGetOutputField(outputId, out var output)
                && ((types?.GetOutputType(source, outputId) ?? output.FieldType).HasFlag(EffectArgumentFieldType.Any)
                    || EffectFieldTypes.AreCompatible(types?.GetOutputType(source, outputId) ?? output.FieldType,
                        types?.GetInputType(target, EffectProviderAnchorExtensions.InputKey) ?? input.FieldType));
        if (!source.InFields.TryGetValue(EffectProviderAnchorExtensions.InputKey, out var sourceInput)
            || !(sourceInput.FieldType.IsContent() || sourceInput.FieldType.HasFlag(EffectArgumentFieldType.Any))
            || !(input.FieldType.IsContent() || input.FieldType.HasFlag(EffectArgumentFieldType.Any))
            || source.GetPipeline() != target.GetPipeline()) return false;
        return target.TypeOfEffect == EffectType.VectorPictureEffect
            || source.TypeOfEffect == target.TypeOfEffect
            || (source.TypeOfEffect is EffectType.TextEffect or EffectType.ContinuousTextEffect)
                && (target.TypeOfEffect is EffectType.TextEffect or EffectType.ContinuousTextEffect);
    }
}
