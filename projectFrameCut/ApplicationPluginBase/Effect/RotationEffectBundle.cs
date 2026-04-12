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
    public class RotationEffectBundle : IEffectBundle
    {
        public Guid Id { get; set; } = Guid.NewGuid();
        public string Name { get; set; } = "Rotation";
        public string FromPlugin => InternalPluginBase.InternalPluginBaseID;

        public Dictionary<string, object> Parameters { get; set; } = new Dictionary<string, object>
        {
            { "Angle", 0f },
            { "ExpandCanvas", false },
        };

        public List<string> ParametersNeeded => RotationEffect_ImageSharp.ParametersNeeded;
        public Dictionary<string, string> ParametersType => RotationEffect_ImageSharp.ParametersType;

        public string TypeName => "Rotation";
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
            var factory = new RotationEffectFactory();
            this.ConfigureFactory(factory);
            return [factory];
        }

        public PropertyPanelBuilder CreateUI()
        {
            float angle = EffectBundleUiHelper.GetFloat(Parameters, "Angle", 0f);
            bool expandCanvas = EffectBundleUiHelper.GetBool(Parameters, "ExpandCanvas", false);

            var panel = new PropertyPanelBuilder();
            panel.AddSlider(
                "Angle",
                EffectBundleUiHelper.ParamLabel("Angle"),
                -180,
                180,
                angle,
                null,
                SliderUpdateEventCallMode.OnValueChanged);
            panel.AddCheckbox(
                "ExpandCanvas",
                EffectBundleUiHelper.L("Effect_Rotation_ExpandCanvas", "Expand Canvas"),
                expandCanvas);

            return panel;
        }

        public Dictionary<string, object> HandlePropertyPanelChange(PropertyPanelPropertyChangedEventArgs args)
        {
            if (args.Id == "Angle")
            {
                EffectBundleUiHelper.TrySetFloat(Parameters, "Angle", args.Value);
            }
            else if (args.Id == "ExpandCanvas")
            {
                EffectBundleUiHelper.TrySetBool(Parameters, "ExpandCanvas", args.Value);
            }

            return Parameters;
        }

        public EffectBundleDisplayItem GetEffectBundleItem(string? locate = null)
        {
            return new EffectBundleDisplayItem
            {
                Name = EffectBundleUiHelper.L("DisplayName_Effect_Rotation", "Rotation"),
                Description = EffectBundleUiHelper.L("Description_Effect_Rotation", "Rotate the frame by angle and optionally expand canvas."),
                Thumbnail = ImageHelper.LoadFromAsset("icon_add")
            };
        }
    }
}