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
    public class JitterEffectBundle : IEffectBundle
    {
        public string TypeName => "Jitter";

        public string FromPlugin => InternalPluginBase.InternalPluginBaseID;

        public bool IsNormalEffect => false;

        public bool IsContinuousEffect => true;

        public bool IsBindableEffect => false;

        public EffectType TypeOfEffect => EffectType.ContinuousEffect;

        public EffectTarget Target => EffectTarget.Video;

        public Guid Id { get; set; } = Guid.NewGuid();
        public string Name { get; set; } = "Jitter";
        
        public Guid BindedInputId { get; set; } = IEffectBundle.InputAnchorGUID;
        public Guid BindedOutputId { get; set; } = IEffectBundle.OutputAnchorGUID;
        public List<Guid>? BindedInputIds { get; set; }
        public bool IsMultiInput => false;

        public string InputAnchorDisplayName => string.Empty;
        public string[]? InputAnchorsDisplayName => null;
        public string OutputAnchorDisplayName => string.Empty;

        public int StartPoint { get; set; }
        public int EndPoint { get; set; }

        public Dictionary<string, object> Parameters { get; set; } = new Dictionary<string, object>
        {
            { "MaxOffsetX", 10 },
            { "MaxOffsetY", 10 },
            { "Direction", JitterEffect.Direction_Both },
            { "Seed", 0 },
        };

        public List<string> ParametersNeeded => JitterEffect.s_ParametersNeeded;

        public Dictionary<string, string> ParametersType => JitterEffect.s_ParametersType;

        public bool Enabled { get; set; } = true;

        public IEffectFactory[] Create()
        {
            var factory = new JitterContinuousEffectFactory();
            this.ConfigureFactory(factory);
            return [factory];
        }

        public PropertyPanelBuilder CreateUI()
        {
            int maxOffsetX = Math.Max(0, EffectBundleUiHelper.GetInt(Parameters, "MaxOffsetX", 10));
            int maxOffsetY = Math.Max(0, EffectBundleUiHelper.GetInt(Parameters, "MaxOffsetY", 10));
            int seed = EffectBundleUiHelper.GetInt(Parameters, "Seed", 0);
            string direction = EffectBundleUiHelper.GetString(Parameters, "Direction", JitterEffect.Direction_Both);

            var options = GetDirectionOptions();
            string selectedDirection = ToDirectionLabel(direction, options);

            var panel = new PropertyPanelBuilder();
            panel.AddSlider(
                "MaxOffsetX",
                EffectBundleUiHelper.L("Effect_Jitter_MaxOffsetX", "Max X Offset"),
                0,
                Math.Max(200, maxOffsetX),
                maxOffsetX,
                null,
                SliderUpdateEventCallMode.OnValueChanged);
            panel.AddSlider(
                "MaxOffsetY",
                EffectBundleUiHelper.L("Effect_Jitter_MaxOffsetY", "Max Y Offset"),
                0,
                Math.Max(200, maxOffsetY),
                maxOffsetY,
                null,
                SliderUpdateEventCallMode.OnValueChanged);
            panel.AddPicker("Direction", EffectBundleUiHelper.L("Direction", "Direction"), options, selectedDirection);
            EffectBundleUiHelper.AddNumericEntry(panel, "Seed", EffectBundleUiHelper.L("Effect_Jitter_Seed", "Random Seed"), seed.ToString(), "0");
            return panel;
        }

        public Dictionary<string, object> HandlePropertyPanelChange(PropertyPanelPropertyChangedEventArgs args)
        {
            if (args.Id == "Direction")
            {
                Parameters["Direction"] = ToDirectionValue(args.Value?.ToString(), GetDirectionOptions());
            }
            else if (args.Id == "MaxOffsetX" && EffectBundleUiHelper.TrySetInt(Parameters, "MaxOffsetX", args.Value))
            {
                Parameters["MaxOffsetX"] = Math.Max(0, EffectBundleUiHelper.GetInt(Parameters, "MaxOffsetX", 10));
            }
            else if (args.Id == "MaxOffsetY" && EffectBundleUiHelper.TrySetInt(Parameters, "MaxOffsetY", args.Value))
            {
                Parameters["MaxOffsetY"] = Math.Max(0, EffectBundleUiHelper.GetInt(Parameters, "MaxOffsetY", 10));
            }
            else if (args.Id == "Seed")
            {
                EffectBundleUiHelper.TrySetInt(Parameters, "Seed", args.Value);
            }

            return Parameters;
        }

        public EffectBundleDisplayItem GetEffectBundleItem(string? locate = null)
        {
            return new EffectBundleDisplayItem
            {
                Name = EffectBundleUiHelper.L("DisplayName_Effect_Jitter", "Jitter"),
                Description = EffectBundleUiHelper.L("Description_Effect_Jitter", "Apply random frame-to-frame positional jitter."),
                Thumbnail = ImageHelper.LoadFromAsset("icon_add")
            };
        }

        private static string[] GetDirectionOptions()
        {
            return
            [
                EffectBundleUiHelper.L("_Effect_Jitter_Both", "Both directions"),
                EffectBundleUiHelper.L("_Effect_Jitter_XOnly", "X direction"),
                EffectBundleUiHelper.L("_Effect_Jitter_YOnly", "Y direction")
            ];
        }

        private static string ToDirectionLabel(string direction, string[] options)
        {
            return direction switch
            {
                var x when x == JitterEffect.Direction_XOnly => options[1],
                var y when y == JitterEffect.Direction_YOnly => options[2],
                _ => options[0]
            };
        }

        private static string ToDirectionValue(string? selectedLabel, string[] options)
        {
            if (selectedLabel == options[1])
            {
                return JitterEffect.Direction_XOnly;
            }

            if (selectedLabel == options[2])
            {
                return JitterEffect.Direction_YOnly;
            }

            return JitterEffect.Direction_Both;
        }

    }
}
