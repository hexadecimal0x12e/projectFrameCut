using projectFrameCut.Render.Plugin;
using projectFrameCut.Render.RenderAPIBase.EffectAndMixture;
using projectFrameCut.Shared;

namespace projectFrameCut.Render.Effect;

public sealed class WindowsVideoSuperResolutionEffectProvider : EffectProviderBase
{
    public static Func<Dictionary<string, object>, IEffect>? EffectFactory { get; set; }

    public WindowsVideoSuperResolutionEffectProvider()
    {
        Name = "Windows Video Super Resolution";
    }

    public override string TypeName => "WindowsVideoSuperResolution";
    public override string FromPlugin => InternalPluginBase.InternalPluginBaseID;
    public override EffectType TypeOfEffect => EffectType.SourceReplacement;
    public override EffectTarget Target => EffectTarget.Video | EffectTarget.SourceReplacement;

    protected override IReadOnlyList<EffectArgumentFieldDescriptor> DefineFields()
        => [];

    protected override EffectImplementType[] SupportedImplementTypes() => [EffectImplementType.IPicture];

    protected override IEffect[] BuildEffects(EffectImplementType implementType, Dictionary<string, object> parameters)
        => EffectFactory is { } factory
            ? [factory(parameters)]
            : throw new PlatformNotSupportedException("Windows Video Super Resolution is not available on this platform.");
}
