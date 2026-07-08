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
    public class SharpenEffectBundle : IEffectBundle
    {
        public Guid Id { get; set; } = Guid.NewGuid();
        public string Name { get; set; } = "Sharpen";
        public string FromPlugin => InternalPluginBase.InternalPluginBaseID;

        public Dictionary<string, object> Parameters { get; set; } = new Dictionary<string, object>
        {
            { "Amount", 1f }
        };

        private static readonly Dictionary<string, EffectBundleSettableFields> s_settableFields = new()
        {
            { "Amount", EffectBundleHelper.FloatField("Amount", "Amount", "Sharpening intensity", 1f, 0f, 5f) }
        };

        public List<string> ParametersNeeded => SharpenEffect_IPicture.ParametersNeeded;
        public Dictionary<string, string> ParametersType => SharpenEffect_IPicture.ParametersType;

        public string TypeName => "Sharpen";
        public bool IsNormalEffect => true;
        public bool IsContinuousEffect => false;
        public bool IsBindableEffect => false;
        public EffectType TypeOfEffect => EffectType.NormalEffect;
        public EffectTarget Target => EffectTarget.Video;

        public Dictionary<string, EffectBundleSettableFields> SettableFields => s_settableFields;

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
            var factory = new SharpenEffectFactory();
            this.ConfigureFactory(factory);
            return [factory];
        }

        public PropertyPanelBuilder CreateUI()
        {
            float amount = EffectBundleHelper.GetFloat(Parameters, "Amount", 1f);
            if (amount < 0f)
            {
                amount = 0f;
            }

            var panel = new PropertyPanelBuilder();
            panel.AddSlider(
                "Amount",
                EffectBundleHelper.L("Effect_Sharpen_Amount", "Amount"),
                0,
                5,
                amount,
                null,
                SliderUpdateEventCallMode.OnMouseUp);
            return panel;
        }

        public Dictionary<string, object> HandlePropertyPanelChange(PropertyPanelPropertyChangedEventArgs args)
        {
            if (args.Id == "Amount" && EffectBundleHelper.TrySetFloat(Parameters, "Amount", args.Value))
            {
                float amount = EffectBundleHelper.GetFloat(Parameters, "Amount", 1f);
                if (amount < 0f)
                {
                    Parameters["Amount"] = 0f;
                }
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
                Name = EffectBundleHelper.L("DisplayName_Effect_Sharpen", "Sharpen"),
                Description = EffectBundleHelper.L("Description_Effect_Sharpen", "Increase local contrast to make frame details more crisp."),
                Thumbnail = ImageHelper.LoadFromAsset("icon_add")
            };
        }
    }
}
