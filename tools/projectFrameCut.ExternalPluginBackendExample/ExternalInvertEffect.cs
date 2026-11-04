using projectFrameCut.Drawing.Base;
using projectFrameCut.Render.RenderAPIBase.EffectAndMixture;
using projectFrameCut.Shared;
using SomePublisher;

namespace projectFrameCut.ExternalPluginBackendExample;

internal sealed class ExternalInvertEffect : INormalEffect
{
    private readonly InvertSettings _settings;
    private readonly ExampleInvertEffect _invert;

    public ExternalInvertEffect() : this(new InvertSettings()) { }

    private ExternalInvertEffect(InvertSettings settings)
    {
        _settings = settings;
        _invert = new ExampleInvertEffect(settings.Amount);
        Parameters = new() { [ExternalInvertEffectProvider.SettingsFieldId] = ExternalInvertEffectProvider.SettingsContract.CreateValue(settings) };
    }

    public string FromPlugin => ExamplePluginConstants.PluginId;
    public string TypeName => ExternalInvertEffectProvider.ProviderTypeName;
    public EffectImplementType ImplementType => EffectImplementType.IPicture;
    public bool IsDeterministic => true;
    public string Name { get; set; } = "External editor invert";
    public string Id { get; set; } = Guid.NewGuid().ToString();
    public Dictionary<string, object> Parameters { get; }
    public bool Enabled { get; set; } = true;
    public int Index { get; set; }
    public bool IsReorderable => true;
    public int RelativeWidth { get; set; }
    public int RelativeHeight { get; set; }
    public string? BindedEffectProvidingSystemID { get; set; }

    public IEffect WithParameters(Dictionary<string, object> parameters)
    {
        var value = parameters.GetValueOrDefault(ExternalInvertEffectProvider.SettingsFieldId);
        if (value is Func<object> getter) value = getter();
        value = EffectFieldTypes.ConvertInput(EffectArgumentFieldType.CustomType, value);
        if (value is not null and not CustomEffectPortValue)
            throw new ArgumentException("Invert settings must be a custom port value.", nameof(parameters));
        return new ExternalInvertEffect(ExternalInvertEffectProvider.ReadSettings((CustomEffectPortValue?)value));
    }

    public IPicture Render(IPicture source, int targetWidth, int targetHeight)
    {
        var result = _invert.Render(source, targetWidth, targetHeight);
        if (source is IPicture<Half> sf && result is IPicture<Half> rf)
        {
            if (!_settings.Red) Array.Copy(sf.r, rf.r, sf.r.Length);
            if (!_settings.Green) Array.Copy(sf.g, rf.g, sf.g.Length);
            if (!_settings.Blue) Array.Copy(sf.b, rf.b, sf.b.Length);
        }
        else if (source is IPicture<byte> s8 && result is IPicture<byte> r8)
        {
            if (!_settings.Red) Array.Copy(s8.r, r8.r, s8.r.Length);
            if (!_settings.Green) Array.Copy(s8.g, r8.g, s8.g.Length);
            if (!_settings.Blue) Array.Copy(s8.b, r8.b, s8.b.Length);
        }
        else if (source is IPicture<ushort> s16 && result is IPicture<ushort> r16)
        {
            if (!_settings.Red) Array.Copy(s16.r, r16.r, s16.r.Length);
            if (!_settings.Green) Array.Copy(s16.g, r16.g, s16.g.Length);
            if (!_settings.Blue) Array.Copy(s16.b, r16.b, s16.b.Length);
        }
        return result;
    }
}
