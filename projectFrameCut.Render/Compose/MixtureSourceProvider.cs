using projectFrameCut.Render.RenderAPIBase.EffectAndMixture;
using projectFrameCut.Shared;

namespace projectFrameCut.Render.Compose;

public sealed class MixtureSourceProvider(bool isUnder) : IEffectProvider
{
    public const string SelfTypeName = "__Self__";
    public const string UnderTypeName = "__Under__";
    public bool IsUnder => isUnder;
    public bool IsDeterministic => true;
    public string TypeName => isUnder ? UnderTypeName : SelfTypeName;
    public string FromPlugin => Plugin.InternalPluginBase.InternalPluginBaseID;
    public EffectType TypeOfEffect => EffectType.NonIPictureOutputValueProvider;
    public EffectTarget Target => EffectTarget.ValueProvider | EffectTarget.InternalUse | EffectTarget.IsNotVisibleInNewEffectSelector;
    public IReadOnlyList<Stage> SupportedStages => [Stage.Mixture];
    public Stage Stage
    {
        get => Stage.Mixture;
        set { if (value != Stage.Mixture) throw new ArgumentOutOfRangeException(nameof(value)); }
    }
    public bool Enabled { get; set; } = true;
    public Guid Id { get; set; } = Guid.NewGuid();
    public string Name { get; set; } = isUnder ? "Underlying Clip" : "Current Clip";
    public Dictionary<string, string> AnchorsBindingState { get; set; } = new();
    public Dictionary<string, object> MetaData { get; set; } = new();
    public Dictionary<string, IEffectArgumentField> Fields { get; set; } = new();
    public IReadOnlyDictionary<string, EffectArgumentFieldDescriptor> InFields => new Dictionary<string, EffectArgumentFieldDescriptor>();
    public EffectArgumentFieldDescriptor OutField => new()
    {
        Id = EffectProviderAnchorExtensions.OutputKey, FieldType = EffectArgumentFieldType.IPicture,
    };
    public IEffect[] Build() => [];
    public IEffect RestoreInstance(EffectImplementType implementType, Dictionary<string, object>? parameters = null) =>
        throw new NotSupportedException("Mixture sources are supplied by the compositor.");
}

public static class MixtureBindings
{
    public static void EnsureSources(IDictionary<Guid, IEffectProvider> providers, bool addDefault = false)
    {
        bool initialize = !providers.Values.OfType<MixtureSourceProvider>().Any();
        var self = providers.Values.OfType<MixtureSourceProvider>().FirstOrDefault(p => !p.IsUnder);
        var under = providers.Values.OfType<MixtureSourceProvider>().FirstOrDefault(p => p.IsUnder);
        if (self is null)
        {
            self = new(false);
            providers[self.Id] = self;
        }
        if (under is null)
        {
            under = new(true);
            providers[under.Id] = under;
        }
        if (initialize && addDefault && !providers.Values.Any(p => p.TypeOfEffect == EffectType.MixtureProvider))
        {
            var mixer = new ClassicOverlayMixtureProvider();
            providers[mixer.Id] = mixer;
        }
        if (!initialize) return;
        var mixers = providers.Values.Where(p => p.TypeOfEffect == EffectType.MixtureProvider).ToArray();
        foreach (var mixer in mixers)
        {
            if (!mixer.AnchorsBindingState.ContainsKey(MixtureProviderBase.UpperInputKey))
                mixer.SetFieldBinding(MixtureProviderBase.UpperInputKey, self.Id.ToString());
            if (!mixer.AnchorsBindingState.ContainsKey(MixtureProviderBase.LowerInputKey))
                mixer.SetFieldBinding(MixtureProviderBase.LowerInputKey, under.Id.ToString());
        }
        if (mixers.Length == 1 && !providers.Values.Any(p => p.Stage == Stage.Mixture && p.IsFinalOutputSource()))
            mixers[0].SetFinalOutputSource(true);
    }
}
