using projectFrameCut.ApplicationAPIBase.Effect;
using projectFrameCut.ApplicationAPIBase.Helpers;
using projectFrameCut.ApplicationAPIBase.Views.PropertyPanelBuilders;
using projectFrameCut.Render.Plugin;
using projectFrameCut.Render.RenderAPIBase.EffectAndMixture;
using projectFrameCut.Shared;
using System;
using System.Collections.Generic;

namespace projectFrameCut.ApplicationPluginBase.Effect
{
    public class ClassicSpeedVarianceProviderEffectBundle : IEffectBundle
    {
        private static readonly List<string> s_ParametersNeeded = ["Ratio"];
        private static readonly Dictionary<string, string> s_ParametersType = new Dictionary<string, string>
        {
            { "Ratio", "float" }
        };

        private static readonly Dictionary<string, EffectBundleSettableFields> s_settableFields = new()
        {
            { "Ratio", EffectBundleHelper.FloatField("Ratio", "Ratio", "Speed ratio applied to the clip", 1f, 0.05f, 8f) }
        };

        public Guid Id { get; set; } = Guid.NewGuid();
        public string Name { get; set; } = "Classic Speed";
        public string FromPlugin => InternalPluginBase.InternalPluginBaseID;
        public string TypeName => "ClassicSpeedVarianceProvider";

        public EffectType TypeOfEffect => EffectType.SpeedVarianceProvider;
        public EffectTarget Target => EffectTarget.SpeedVariance;

        public bool Enabled { get; set; } = true;

        public bool IsNormalEffect => false;
        public bool IsContinuousEffect => false;
        public bool IsBindableEffect => false;

        public string InputAnchorDisplayName => string.Empty;
        public string[]? InputAnchorsDisplayName => null;
        public string OutputAnchorDisplayName => string.Empty;

        public Guid BindedInputId { get; set; } = IEffectBundle.InputAnchorGUID;
        public Guid BindedOutputId { get; set; } = IEffectBundle.OutputAnchorGUID;
        public bool IsMultiInput => false;
        public List<Guid>? BindedInputIds { get; set; }

        public int StartPoint { get; set; }
        public int EndPoint { get; set; }

        public Dictionary<string, EffectBundleSettableFields> SettableFields => s_settableFields;

        public Dictionary<string, object> Parameters { get; set; } = new Dictionary<string, object>
        {
            { "Ratio", 1f }
        };

        public List<string> ParametersNeeded => s_ParametersNeeded;
        public Dictionary<string, string> ParametersType => s_ParametersType;

        public IEffectFactory[] Create()
        {
            var factory = new ClassicSpeedVarianceProviderFactory();
            this.ConfigureFactory(factory);
            return [factory];
        }

        public PropertyPanelBuilder CreateUI()
        {
            float ratio = EffectBundleHelper.GetFloat(Parameters, "Ratio", 1f);
            if (ratio <= 0f)
            {
                ratio = 1f;
            }

            var panel = new PropertyPanelBuilder();
            panel.AddSlider(
                "Ratio",
                EffectBundleHelper.L("Property_Ratio", "Ratio"),
                0.05,
                8,
                ratio,
                null,
                SliderUpdateEventCallMode.OnMouseUp);

            return panel;
        }

        public Dictionary<string, object> HandlePropertyPanelChange(PropertyPanelPropertyChangedEventArgs args)
        {
            if (args.Id == "Ratio" && EffectBundleHelper.TrySetFloat(Parameters, "Ratio", args.Value))
            {
                float ratio = EffectBundleHelper.GetFloat(Parameters, "Ratio", 1f);
                if (ratio <= 0f)
                {
                    Parameters["Ratio"] = 1f;
                }
                LogDiagnostic(Convert.ToString(Parameters["Ratio"]));
            }

            return Parameters;
        }

        public bool HandleSettableFieldsChange(EffectBundleSettableFields field, object value, out string feedback)
        {
            return EffectBundleHelper.HandleSettableFieldChange(Parameters, field, value, out feedback);
        }

        public EffectBundleDisplayItem GetEffectBundleItem(string? locate = null)
        {
            return new EffectBundleDisplayItem
            {
                Name = EffectBundleHelper.L("DisplayName_Effect_ClassicSpeedVarianceProvider", "Classic Speed"),
                Description = EffectBundleHelper.L("Description_Effect_ClassicSpeedVarianceProvider", "Apply a constant speed ratio for the whole clip."),
                Thumbnail = ImageHelper.LoadFromAsset("icon_add")
            };
        }
    }
}
