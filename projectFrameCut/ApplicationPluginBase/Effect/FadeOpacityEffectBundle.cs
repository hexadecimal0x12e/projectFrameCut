using projectFrameCut.ApplicationAPIBase.Effect;
using projectFrameCut.ApplicationAPIBase.Helpers;
using projectFrameCut.ApplicationAPIBase.Views.PropertyPanelBuilders;
using projectFrameCut.Render.Effect;
using projectFrameCut.Render.Plugin;
using projectFrameCut.Render.RenderAPIBase.EffectAndMixture;
using projectFrameCut.Shared;
using System;
using System.Collections.Generic;

namespace projectFrameCut.ApplicationPluginBase.Effect
{
    public class FadeOpacityEffectBundle : IEffectBundle
    {
        public Guid Id { get; set; } = Guid.NewGuid();
        public string Name { get; set; } = "FadeOpacity";
        public string FromPlugin => InternalPluginBase.InternalPluginBaseID;

        public Dictionary<string, object> Parameters { get; set; } = new Dictionary<string, object>
        {
            { "Opacity", 0.8f }
        };

        public List<string> ParametersNeeded => FadeOpacityEffect_IPicture.ParametersNeeded;
        public Dictionary<string, string> ParametersType => FadeOpacityEffect_IPicture.ParametersType;

        public string TypeName => "FadeOpacity";
        public bool IsNormalEffect => true;
        public bool IsContinuousEffect => false;
        public bool IsBindableEffect => false;
        public EffectType TypeOfEffect => EffectType.NormalEffect;
        public EffectTarget Target => EffectTarget.Video;

        public Guid BindedInputId { get; set; } = IEffectBundle.InputAnchorGUID;
        public Guid BindedOutputId { get; set; } = IEffectBundle.OutputAnchorGUID;
        public List<Guid>? BindedInputIds { get; set; }
        public bool IsMultiInput => false;
        public string InputAnchorDisplayName => string.Empty;
        public string[]? InputAnchorsDisplayName => null;
        public string OutputAnchorDisplayName => string.Empty;

        public bool Enabled { get; set; } = true;
        public int StartPoint { get; set; }
        public int EndPoint { get; set; }

        public IEffectFactory[] Create()
        {
            var factory = new FadeOpacityEffectFactory();
            this.ConfigureFactory(factory);
            return [factory];
        }

        public PropertyPanelBuilder CreateUI()
        {
            float opacity = EffectBundleUiHelper.GetFloat(Parameters, "Opacity", 0.8f);
            opacity = Math.Clamp(opacity, 0f, 1f);

            var panel = new PropertyPanelBuilder();
            panel.AddSlider(
                "Opacity",
                EffectBundleUiHelper.L("Effect_FadeOpacity_Opacity", "Opacity"),
                0,
                1,
                opacity,
                null,
                SliderUpdateEventCallMode.OnMouseUp);
            return panel;
        }

        public Dictionary<string, object> HandlePropertyPanelChange(PropertyPanelPropertyChangedEventArgs args)
        {
            if (args.Id == "Opacity" && EffectBundleUiHelper.TrySetFloat(Parameters, "Opacity", args.Value))
            {
                Parameters["Opacity"] = Math.Clamp(EffectBundleUiHelper.GetFloat(Parameters, "Opacity", 0.8f), 0f, 1f);
            }

            return Parameters;
        }

        public EffectBundleDisplayItem GetEffectBundleItem(string? locate = null)
        {
            return new EffectBundleDisplayItem
            {
                Name = EffectBundleUiHelper.L("DisplayName_Effect_FadeOpacity", "Fade Opacity"),
                Description = EffectBundleUiHelper.L("Description_Effect_FadeOpacity", "Apply a uniform opacity multiplier to the frame."),
                Thumbnail = ImageHelper.LoadFromAsset("icon_add")
            };
        }
    }
}
