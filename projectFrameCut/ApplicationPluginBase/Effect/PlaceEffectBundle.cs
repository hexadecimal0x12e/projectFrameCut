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
    public class PlaceEffectBundle : IEffectBundle
    {
        public Guid Id { get; set; } = Guid.NewGuid();
        public string Name { get; set; } = "Place";
        public string FromPlugin => InternalPluginBase.InternalPluginBaseID;

        public Dictionary<string, object> Parameters { get; set; } = new Dictionary<string, object>
        {
            { "StartX", 0 },
            { "StartY", 0 },
        };

        public List<string> ParametersNeeded => PlaceEffect_IPicture.ParametersNeeded;
        public Dictionary<string, string> ParametersType => PlaceEffect_IPicture.ParametersType;

        public string TypeName => "Place";
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
            var factory = new PlaceEffectFactory();
            this.ConfigureFactory(factory);
            return [factory];
        }

        public PropertyPanelBuilder CreateUI()
        {
            int startX = EffectBundleUiHelper.GetInt(Parameters, "StartX", 0);
            int startY = EffectBundleUiHelper.GetInt(Parameters, "StartY", 0);

            var panel = new PropertyPanelBuilder();
            EffectBundleUiHelper.AddNumericEntry(panel, "StartX", EffectBundleUiHelper.ParamLabel("StartX"), startX.ToString(), "0");
            EffectBundleUiHelper.AddNumericEntry(panel, "StartY", EffectBundleUiHelper.ParamLabel("StartY"), startY.ToString(), "0");
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

            return Parameters;
        }

        public EffectBundleDisplayItem GetEffectBundleItem(string? locate = null)
        {
            return new EffectBundleDisplayItem
            {
                Name = EffectBundleUiHelper.L("DisplayName_Effect_Place", "Place"),
                Description = EffectBundleUiHelper.L("Description_Effect_Place", "Move the frame to a target position on the canvas."),
                Thumbnail = ImageHelper.LoadFromAsset("icon_add")
            };
        }
    }
}