using projectFrameCut.ApplicationAPIBase.Effect;
using projectFrameCut.ApplicationAPIBase.Plugins;
using projectFrameCut.Render.Plugin;
using System;
using System.Collections.Generic;
using System.Text;

namespace projectFrameCut
{
    internal class InternalApplicationPluginBase : InternalPluginBase, IApplicationPluginBase
    {
        public Dictionary<string, Func<IEffectBundle>> EffectBundleProvider => new Dictionary<string, Func<IEffectBundle>> { };

        public View? SettingPageProvider(ref IApplicationPluginBase instance)
        {
            return new Label { Text = "No settings available for this plugin." };
        }
    }
}
