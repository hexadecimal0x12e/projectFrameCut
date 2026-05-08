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
    public class CropEffectBundle : IEffectBundle
    {
        public Guid Id { get; set; } = Guid.NewGuid();
        public string Name { get; set; } = "Crop";
        public string FromPlugin => InternalPluginBase.InternalPluginBaseID;

        public Dictionary<string, object> Parameters { get; set; } = new Dictionary<string, object>
        {
            { "StartX", 0 },
            { "StartY", 0 },
            { "Width", 1280 },
            { "Height", 720 },
            { "Angle", 0f },
        };

        public List<string> ParametersNeeded => CropEffect_ImageSharp.ParametersNeeded;
        public Dictionary<string, string> ParametersType => CropEffect_ImageSharp.ParametersType;

        public string TypeName => "Crop";
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
            var factory = new CropEffectFactory();
            this.ConfigureFactory(factory);
            return [factory];
        }

        public PropertyPanelBuilder CreateUI()
        {
            int startX = EffectBundleUiHelper.GetInt(Parameters, "StartX", 0);
            int startY = EffectBundleUiHelper.GetInt(Parameters, "StartY", 0);
            int width = Math.Max(1, EffectBundleUiHelper.GetInt(Parameters, "Width", 1280));
            int height = Math.Max(1, EffectBundleUiHelper.GetInt(Parameters, "Height", 720));
            float angle = EffectBundleUiHelper.GetFloat(Parameters, "Angle", 0f);

            var panel = new PropertyPanelBuilder();
            EffectBundleUiHelper.AddNumericEntry(panel, "StartX", EffectBundleUiHelper.ParamLabel("StartX"), startX.ToString(), "0");
            EffectBundleUiHelper.AddNumericEntry(panel, "StartY", EffectBundleUiHelper.ParamLabel("StartY"), startY.ToString(), "0");
            EffectBundleUiHelper.AddNumericEntry(panel, "Width", EffectBundleUiHelper.ParamLabel("Width"), width.ToString(), "1280");
            EffectBundleUiHelper.AddNumericEntry(panel, "Height", EffectBundleUiHelper.ParamLabel("Height"), height.ToString(), "720");
            panel.AddSlider(
                "Angle",
                EffectBundleUiHelper.L("General_Rotation", "Rotation"),
                -180,
                180,
                angle,
                null,
                SliderUpdateEventCallMode.OnMouseUp);

            return panel;
        }

        public Dictionary<string, object> HandlePropertyPanelChange(PropertyPanelPropertyChangedEventArgs args)
        {
            if (args.Id == "StartX")
            {
                EffectBundleUiHelper.TrySetInt(Parameters, "StartX", args.Value);
            }
            else if (args.Id == "StartY")
            {
                EffectBundleUiHelper.TrySetInt(Parameters, "StartY", args.Value);
            }
            else if (args.Id == "Width" && EffectBundleUiHelper.TrySetInt(Parameters, "Width", args.Value))
            {
                Parameters["Width"] = Math.Max(1, EffectBundleUiHelper.GetInt(Parameters, "Width", 1280));
            }
            else if (args.Id == "Height" && EffectBundleUiHelper.TrySetInt(Parameters, "Height", args.Value))
            {
                Parameters["Height"] = Math.Max(1, EffectBundleUiHelper.GetInt(Parameters, "Height", 720));
            }
            else if (args.Id == "Angle")
            {
                EffectBundleUiHelper.TrySetFloat(Parameters, "Angle", args.Value);
            }

            return Parameters;
        }

        public EffectBundleDisplayItem GetEffectBundleItem(string? locate = null)
        {
            return new EffectBundleDisplayItem
            {
                Name = EffectBundleUiHelper.L("DisplayName_Effect_Crop", "Crop"),
                Description = EffectBundleUiHelper.L("Description_Effect_Crop", "Crop frame area and optionally rotate the crop region."),
                Thumbnail = ImageHelper.LoadFromAsset("icon_add")
            };
        }
    }
}