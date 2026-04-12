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
    public class BlurEffectBundle : IEffectBundle
    {
        public Guid Id { get; set; } = Guid.NewGuid();

        public string Name { get; set; } = "Blur";

        public string FromPlugin => InternalPluginBase.InternalPluginBaseID;

        public Dictionary<string, object> Parameters { get; set; } = new Dictionary<string, object>
        {
            {"Sigma", 4f }
        };

        public List<string> ParametersNeeded => new List<string>
        {
            "Sigma"
        };

        public Dictionary<string, string> ParametersType => new Dictionary<string, string>
        {
            {"Sigma", "float" }
        };

        public string TypeName => "Blur";

        public bool IsNormalEffect => true;

        public bool IsContinuousEffect => false;

        public bool IsBindableEffect => false;

        public Guid BindedInputId { get; set; } = IEffectBundle.InputAnchorGUID;

        public Guid BindedOutputId { get; set; } = IEffectBundle.OutputAnchorGUID;

        public bool IsMultiInput => false;
        public string InputAnchorDisplayName => string.Empty;
        public string[]? InputAnchorsDisplayName => null;
        public string OutputAnchorDisplayName => string.Empty;
        public List<Guid>? BindedInputIds { get; set; }

        public bool Enabled { get; set; } = true;

        public int StartPoint { get; set; }
        public int EndPoint { get; set; }

        public EffectType TypeOfEffect => EffectType.NormalEffect;

        public EffectTarget Target => EffectTarget.Video;

        public IEffectFactory[] Create()
        {
            var factory = new BlurEffectFactory();
            this.ConfigureFactory(factory);
            return new IEffectFactory[] { factory };
        }

        public PropertyPanelBuilder CreateUI()
        {
            float sigma = EffectBundleUiHelper.GetFloat(Parameters, "Sigma", 4f);
            if (sigma < 0f)
            {
                sigma = 0f;
            }

            var panel = new PropertyPanelBuilder();
            panel.AddSlider(
                "Sigma",
                EffectBundleUiHelper.L("Property_Sigma", "Sigma"),
                0,
                128,
                sigma,
                null,
                SliderUpdateEventCallMode.OnValueChanged);
            return panel;
        }

        public Dictionary<string, object> HandlePropertyPanelChange(PropertyPanelPropertyChangedEventArgs args)
        {
            if (args.Id == "Sigma" && EffectBundleUiHelper.TrySetFloat(Parameters, "Sigma", args.Value))
            {
                float sigma = EffectBundleUiHelper.GetFloat(Parameters, "Sigma", 4f);
                if (sigma < 0f)
                {
                    Parameters["Sigma"] = 0f;
                }
            }

            return Parameters;
        }

        public EffectBundleDisplayItem GetEffectBundleItem(string? locate = null)
        {
            return new EffectBundleDisplayItem
            {
                Name = EffectBundleUiHelper.L("DisplayName_Effect_Blur", "Blur"),
                Description = EffectBundleUiHelper.L("Description_Effect_Blur", "Apply Gaussian blur to the image."),
                Thumbnail = ImageHelper.LoadFromAsset("icon_effect_blur")
            };
        }
    }
}
