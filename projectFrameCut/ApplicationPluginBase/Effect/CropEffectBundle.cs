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
        public EffectTarget Target => EffectTarget.Video | EffectTarget.IsNotVisibleInEffectEditor | EffectTarget.IsNotVisibleInNewEffectSelector;

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
            panel.AddPositionTupleInputBox("crop", new SingleLineLabel(EffectBundleUiHelper.L("_CropRegion", "Crop Region")), PositionTupleMode.XYWH, (startX, startY, width, height));
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
            switch (args.Id)
            {
                case "crop_X":
                    EffectBundleUiHelper.TrySetInt(Parameters, "StartX", args.Value);
                    break;
                case "crop_Y":
                    EffectBundleUiHelper.TrySetInt(Parameters, "StartY", args.Value);
                    break;
                case "crop_W":
                    if (EffectBundleUiHelper.TrySetInt(Parameters, "Width", args.Value))
                        Parameters["Width"] = Math.Max(1, EffectBundleUiHelper.GetInt(Parameters, "Width", 1280));
                    break;
                case "crop_H":
                    if (EffectBundleUiHelper.TrySetInt(Parameters, "Height", args.Value))
                        Parameters["Height"] = Math.Max(1, EffectBundleUiHelper.GetInt(Parameters, "Height", 720));
                    break;
                case "Angle":
                    EffectBundleUiHelper.TrySetFloat(Parameters, "Angle", args.Value);
                    break;
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