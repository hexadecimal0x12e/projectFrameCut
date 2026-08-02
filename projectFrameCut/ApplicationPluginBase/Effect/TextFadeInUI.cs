using projectFrameCut.ApplicationAPIBase.Effect;
using projectFrameCut.ApplicationAPIBase.Views.PropertyPanelBuilders;
using projectFrameCut.Render.RenderAPIBase.EffectAndMixture;
using System;
using System.Collections.Generic;

namespace projectFrameCut.ApplicationPluginBase.Effect
{
    /// <summary>
    /// Custom property UI of the TextFadeIn effect: a short explanatory text.
    /// </summary>
    public class TextFadeInUI : EffectProviderUI
    {
        public TextFadeInUI(IEffectProvider inner) : base(inner)
        {
        }

        public override PropertyPanelBuilder CreateUI()
        {
            var panel = new PropertyPanelBuilder();
            panel.AddText(
                EffectProviderHelper.L("_TextFadeIn_Desc",
                    "Fades the text from transparent to fully visible over the specified duration."));
            return panel;
        }
    }
}
