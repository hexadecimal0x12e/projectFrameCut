using projectFrameCut.ApplicationAPIBase.Effect;
using projectFrameCut.ApplicationAPIBase.Plugins;
using projectFrameCut.Render.Plugin;
using projectFrameCut.Render.RenderAPIBase.Plugins;
using System;
using System.Collections.Generic;
using System.Text;

namespace projectFrameCut.ApplicationPluginBase
{
    internal class InternalApplicationPluginBase : InternalPluginBase, IApplicationPluginBase
    {
        public Dictionary<string, Func<IEffectBundle>> EffectBundleProvider => new Dictionary<string, Func<IEffectBundle>>
        {
            { "ZoomIn", () => new Effect.ZoominEffectBundle() },
            { "RemoveColor", () => new Effect.RemoveColorEffectBundle() },
            { "Jitter", () => new Effect.JitterEffectBundle() },
            { "Movement", () => new Effect.MovementEffectBundle()  },
            { "Blur", () => new Effect.BlurEffectBundle() },
#if DEBUG
            { "MockValueProvider", () => new MockValueProviderBundle() },
            { "MockOneToOneProcessor", () => new MockOneToOneProcessorBundle() },
            { "MockManyToOneProcessor", () => new MockManyToOneProcessorBundle() },
            { "MockOneInputResultGenerator", () => new MockOneInputResultGeneratorBundle() },
            { "MockManyInputResultGenerator", () => new MockManyInputResultGeneratorBundle() },
#endif
        };

        public int AppLevelPluginAPIVersion => IApplicationPluginBase.CurrentAppLevelPluginAPIVersion;

        public View? SettingPageProvider(ref IApplicationPluginBase instance)
        {
            return null;
        }

        void IApplicationPluginBase.OnApplicationPluginLoaded()
        {
            ApplicationAPIBase.LocalizedResources.APIBaseLocalizedResources.Localized = ApplicationAPIBaseLocalizerBase.GetMapping().TryGetValue(Localized._LocaleId_, out var loc) ? loc : ApplicationAPIBaseLocalizerBase.GetMapping().First().Value;
        }


    }
}
