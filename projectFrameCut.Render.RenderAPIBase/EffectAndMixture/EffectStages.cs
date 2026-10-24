using projectFrameCut.Render.RenderAPIBase.ClipAndTrack;
using projectFrameCut.Shared;

namespace projectFrameCut.Render.RenderAPIBase.EffectAndMixture;

public sealed record StageTerminal(string Id, string Name, IReadOnlyList<IEffectArgumentField> Inputs,
    Guid? ProviderId = null);

public interface IEffectStagePlan : IDisposable
{
    IClip Owner { get; }
    void Complete(IClip clip, Stage stage, uint frame);
}

public static class EffectStages
{
    public static IReadOnlyList<Stage> Values { get; } = [Stage.Picture, Stage.ClipArguments, Stage.Text,
        Stage.VectorComponent, Stage.VectorPicture, Stage.SourceReplacement, Stage.SpeedVariance,
        Stage.Audio, Stage.Transform, Stage.Mixture];
    public static IReadOnlyList<Stage> Picture { get; } = [Stage.ClipArguments, Stage.SpeedVariance,
        Stage.SourceReplacement, Stage.Picture, Stage.Transform, Stage.Mixture];
    public static IReadOnlyList<Stage> Text { get; } = [Stage.ClipArguments, Stage.SpeedVariance, Stage.Text,
        Stage.VectorPicture, Stage.SourceReplacement, Stage.Picture, Stage.Transform, Stage.Mixture];
    public static IReadOnlyList<Stage> Vector { get; } = [Stage.ClipArguments, Stage.SpeedVariance, Stage.VectorComponent,
        Stage.VectorPicture, Stage.SourceReplacement, Stage.Picture, Stage.Transform, Stage.Mixture];
    public static IReadOnlyList<Stage> VectorImage { get; } = [Stage.ClipArguments, Stage.SpeedVariance,
        Stage.VectorPicture, Stage.SourceReplacement, Stage.Picture, Stage.Transform, Stage.Mixture];
    public static IReadOnlyList<Stage> Audio { get; } = [Stage.Audio, Stage.Transform];

    public static bool HasContent(this Stage stage) => stage is Stage.Picture or Stage.VectorPicture
        or Stage.Text or Stage.VectorComponent or Stage.Audio;

    public static EffectArgumentFieldType GetContentType(this Stage stage) => stage switch
    {
        Stage.Picture or Stage.SourceReplacement or Stage.Transform or Stage.Mixture => EffectArgumentFieldType.IPicture,
        Stage.VectorPicture => EffectArgumentFieldType.VectorPicture,
        Stage.Text or Stage.VectorComponent => EffectArgumentFieldType.VectorPicture,
        _ => EffectArgumentFieldType.Unknown,
    };

    public static Stage GetStage(this EffectType type) => type switch
    {
        EffectType.TextEffect or EffectType.ContinuousTextEffect => Stage.Text,
        EffectType.VectorComponentEffect => Stage.VectorComponent,
        EffectType.VectorPictureEffect => Stage.VectorPicture,
        EffectType.AudioNormalEffect or EffectType.AudioContinuousEffect => Stage.Audio,
        EffectType.SpeedVarianceProvider => Stage.SpeedVariance,
        EffectType.SourceReplacement => Stage.SourceReplacement,
        EffectType.Transform => Stage.Transform,
        EffectType.MixtureProvider => Stage.Mixture,
        _ => Stage.Picture,
    };

    // Audio and timing have independent evaluation clocks; enum order is not execution order.
    public static bool Precedes(Stage source, Stage target, bool transformBeforeEffects = false)
    {
        if (source == target || source is Stage.Audio or Stage.SpeedVariance
            || target is Stage.Audio or Stage.SpeedVariance) return false;
        if (source == Stage.Transform) return target == Stage.Mixture || transformBeforeEffects && target == Stage.Picture;
        if (target == Stage.Transform) return source is Stage.ClipArguments or Stage.Text or Stage.VectorComponent
            or Stage.VectorPicture or Stage.SourceReplacement || !transformBeforeEffects && source == Stage.Picture;
        return source switch
        {
            Stage.ClipArguments => target is Stage.Text or Stage.VectorComponent or Stage.VectorPicture
                or Stage.SourceReplacement or Stage.Picture or Stage.Mixture,
            Stage.Text or Stage.VectorComponent => target is Stage.VectorPicture or Stage.SourceReplacement or Stage.Picture or Stage.Mixture,
            Stage.VectorPicture => target is Stage.SourceReplacement or Stage.Picture or Stage.Mixture,
            Stage.SourceReplacement => target is Stage.Picture or Stage.Mixture,
            Stage.Picture => target == Stage.Mixture,
            _ => false,
        };
    }

    public static IReadOnlyList<StageTerminal> GetTerminals(Stage stage, IEnumerable<IEffectProvider> providers)
    {
        if (stage == Stage.ClipArguments)
            return providers.OfType<ClipArgumentProvider>().SelectMany(p => p.InFields.Values.GroupBy(f => f.Id switch
            {
                nameof(IClip.TargetX) or nameof(IClip.TargetY) => "Position",
                nameof(IClip.TargetWidth) or nameof(IClip.TargetHeight) => "Size",
                _ => f.Id,
            }).Select(g => new StageTerminal(g.Key, g.Key, g.Cast<IEffectArgumentField>().ToArray(), p.Id))).ToArray();
        if (stage is Stage.Picture or Stage.VectorPicture or Stage.Text or Stage.VectorComponent)
            return [new("content", stage.ToString(), [new EffectArgumentFieldDescriptor
            {
                Id = EffectProviderAnchorExtensions.InputKey, FieldType = stage.GetContentType(),
            }])];
        return providers.Where(p => p.Stage == stage && !p.Target.HasFlag(EffectTarget.ValueProvider))
            .Select(p => new StageTerminal(p.Id.ToString(), p.Name,
                p.InFields.Values.Cast<IEffectArgumentField>().Concat(p.Fields.Values)
                    .Where(f => f.Id is not (EffectProviderAnchorExtensions.InputKey or EffectProviderAnchorExtensions.OutputKey))
                    .DistinctBy(f => f.Id).ToArray(), p.Id)).ToArray();
    }

    public static void Validate(IEffectProvider provider)
    {
        if (provider.SupportedStages.Count == 0 || provider.SupportedStages.Distinct().Count() != provider.SupportedStages.Count
            || provider.SupportedStages.Any(s => !Enum.IsDefined(s)) || !provider.SupportedStages.Contains(provider.Stage))
            throw new InvalidOperationException($"Provider {provider.TypeName} ({provider.Id}) does not support stage {provider.Stage}.");
    }
}
