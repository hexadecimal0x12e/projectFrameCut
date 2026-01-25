using projectFrameCut.ApplicationAPIBase.Effect;
using projectFrameCut.ApplicationAPIBase.Plugins;
using projectFrameCut.Render.Plugin;
using System;
using System.Collections.Generic;
using System.Text;

namespace projectFrameCut.ApplicationPluginBase
{
    internal class InternalApplicationPluginBase : InternalPluginBase, IApplicationPluginBase
    {
        public Dictionary<string, Func<IEffectBundle>> EffectBundleProvider => new Dictionary<string, Func<IEffectBundle>>
        {
            {"ZoomIn", () => new Effect.ZoominEffectBundle() },
            {"Jitter", () => new Effect.JitterEffectBundle() },
            {"Movement", () => new Effect.MovementEffectBundle()  },
        };

        public View? SettingPageProvider(ref IApplicationPluginBase instance)
        {
            return null;
        }
    }
}
