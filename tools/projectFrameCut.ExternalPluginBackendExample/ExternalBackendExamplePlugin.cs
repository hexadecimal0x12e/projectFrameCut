using projectFrameCut.Render.RenderAPIBase.EffectAndMixture;
using projectFrameCut.Render.RenderAPIBase.Plugins;
using projectFrameCut.Shared;
using SomePublisher;

namespace projectFrameCut.ExternalPluginBackendExample;

internal sealed class ExternalBackendExamplePlugin : MyPlugin, IPluginBase
{
    Dictionary<string, Func<IEffectProvider>> IPluginBase.EffectProviderProvider
    {
        get
        {
            var providers = base.EffectProviderProvider;
            providers.Add(ExternalInvertEffectProvider.ProviderTypeName, () => new ExternalInvertEffectProvider());
            return providers;
        }
    }

    IReadOnlyDictionary<EffectImplementationKey, Func<IEffect>> IPluginBase.EffectImplementationProvider =>
        new Dictionary<EffectImplementationKey, Func<IEffect>>(base.EffectImplementationProvider)
        {
            [new(ExternalInvertEffectProvider.ProviderTypeName, EffectImplementType.IPicture)] = () => new ExternalInvertEffect(),
        };

    IReadOnlyDictionary<string, string> IPluginBase.Properties => new Dictionary<string, string>(base.Properties)
    {
        ["ProviderCount"] = "8",
    };

    Dictionary<string, Dictionary<string, string>> IPluginBase.LocalizationProvider
    {
        get
        {
            var locales = base.LocalizationProvider;
            locales["en-US"]["DisplayName_Effect_ExampleExternalInvert"] = "External editor invert";
            locales["en-US"]["Effect_ExampleExternalInvert_Settings"] = "Invert settings";
            locales["zh-CN"]["DisplayName_Effect_ExampleExternalInvert"] = "外部配置反色";
            locales["zh-CN"]["Effect_ExampleExternalInvert_Settings"] = "反色配置";
            return locales;
        }
    }
}
