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
        };

        public int AppLevelPluginAPIVersion => IApplicationPluginBase.CurrentAppLevelPluginAPIVersion;

        public View? SettingPageProvider(ref IApplicationPluginBase instance)
        {
            return null;
        }

        internal string locateId = "en-US";

        void IApplicationPluginBase.OnApplicationPluginLoaded()
        {
            ApplicationAPIBase.LocalizedResources.APIBaseLocalizedResources.Localized = ApplicationAPIBaseLocalizerBase.GetMapping().TryGetValue(locateId, out var loc) ? loc : ApplicationAPIBaseLocalizerBase.GetMapping().First().Value;
        }


    }
}
