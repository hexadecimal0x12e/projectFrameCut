using projectFrameCut.Render.RenderAPIBase.ClipAndTrack;
using projectFrameCut.Shared;

namespace projectFrameCut.Render.RenderAPIBase.EffectAndMixture;

public sealed record StageTerminal(string Id, string Name, IReadOnlyList<IEffectArgumentField> Inputs,
    Guid? ProviderId = null);

public interface IEffectStagePlan : IDisposable
{
    IClip Owner { get; }
    void Complete(IClip clip, EffectStage stage, uint frame);
}

public static class EffectStages
{
    public static IReadOnlyList<EffectStage> Values { get; } = [EffectStage.Render, EffectStage.Composition, EffectStage.Text, EffectStage.VectorContent, EffectStage.SourceReplacement];
    public static IReadOnlyList<EffectStage> Picture { get; } = [EffectStage.SourceReplacement, EffectStage.Render, EffectStage.Composition];
    public static IReadOnlyList<EffectStage> Text { get; } = [EffectStage.Text, EffectStage.VectorContent, EffectStage.SourceReplacement, EffectStage.Render, EffectStage.Composition];
    public static IReadOnlyList<EffectStage> Vector { get; } = [EffectStage.VectorContent, EffectStage.SourceReplacement, EffectStage.Render, EffectStage.Composition];
    public static IReadOnlyList<EffectStage> VectorImage => Vector;
    public static IReadOnlyList<EffectStage> Audio { get; } = [EffectStage.Render, EffectStage.Composition];

    public static bool HasContent(this EffectStage stage) => stage is EffectStage.Render or EffectStage.Text or EffectStage.VectorContent;

    public static EffectArgumentFieldType GetContentType(this EffectStage stage) => stage switch
    {
        EffectStage.Text or EffectStage.VectorContent => EffectArgumentFieldType.VectorPicture,
        _ => EffectArgumentFieldType.IPicture,
    };

    public static EffectStage GetStage(this EffectType type) => type switch
    {
        EffectType.TextEffect or EffectType.ContinuousTextEffect => EffectStage.Text,
        EffectType.VectorComponentEffect or EffectType.VectorPictureEffect => EffectStage.VectorContent,
        EffectType.SpeedVarianceProvider or EffectType.Transform or EffectType.MixtureProvider => EffectStage.Composition,
        EffectType.SourceReplacement => EffectStage.SourceReplacement,
        _ => EffectStage.Render,
    };

    public static bool Precedes(EffectStage source, EffectStage target, bool transformBeforeEffects = false)
        => source != target && ((source, target) switch
        {
            (EffectStage.Text, _) => true,
            (EffectStage.VectorContent, EffectStage.SourceReplacement or EffectStage.Render or EffectStage.Composition) => true,
            (EffectStage.SourceReplacement, EffectStage.Render or EffectStage.Composition) => true,
            (EffectStage.Render, EffectStage.Composition) => !transformBeforeEffects,
            _ => false,
        });

    public static IReadOnlyList<StageTerminal> GetTerminals(EffectStage stage, IEnumerable<IEffectProvider> providers)
        => providers.Where(p => p.Stage == stage && p is ClipArgumentProvider or RenderOutputProvider)
            .SelectMany(p => p.InFields.Values.Select(f => new StageTerminal(f.Id, f.Id, [f], p.Id))).ToArray();

    public static void Validate(IEffectProvider provider)
    {
        if (provider.SupportedStages.Count == 0 || provider.SupportedStages.Distinct().Count() != provider.SupportedStages.Count
            || provider.SupportedStages.Any(s => !Enum.IsDefined(s)) || !provider.SupportedStages.Contains(provider.Stage))
            throw new InvalidOperationException($"Provider {provider.TypeName} ({provider.Id}) does not support stage {provider.Stage}.");
    }
}
