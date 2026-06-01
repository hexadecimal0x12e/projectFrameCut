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
    public class ResizeEffectBundle : IEffectBundle
    {
        public Guid Id { get; set; } = Guid.NewGuid();
        public string Name { get; set; } = "Resize";
        public string FromPlugin => InternalPluginBase.InternalPluginBaseID;

        public Dictionary<string, object> Parameters { get; set; } = new Dictionary<string, object>
        {
            { "Width", 1920 },
            { "Height", 1080 },
            { "PreserveAspectRatio", true },
        };

        public List<string> ParametersNeeded => ResizeEffect_ImageSharp.ParametersNeeded;
        public Dictionary<string, string> ParametersType => ResizeEffect_ImageSharp.ParametersType;

        public string TypeName => "Resize";
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
            var factory = new ResizeEffectFactory();
            this.ConfigureFactory(factory);
            return [factory];
        }

        public PropertyPanelBuilder CreateUI()
        {
            int width = Math.Max(1, EffectBundleUiHelper.GetInt(Parameters, "Width", 1920));
            int height = Math.Max(1, EffectBundleUiHelper.GetInt(Parameters, "Height", 1080));
            bool preserveAspectRatio = EffectBundleUiHelper.GetBool(Parameters, "PreserveAspectRatio", true);

            var panel = new PropertyPanelBuilder();
            EffectBundleUiHelper.AddNumericEntry(panel, "Width", EffectBundleUiHelper.ParamLabel("Width"), width.ToString(), "1920");
            EffectBundleUiHelper.AddNumericEntry(panel, "Height", EffectBundleUiHelper.ParamLabel("Height"), height.ToString(), "1080");
            panel.AddCheckbox(
                "PreserveAspectRatio",
                EffectBundleUiHelper.L("Effect_Resize_PreserveAspectRatio", "Preserve Aspect Ratio"),
                preserveAspectRatio);

            return panel;
        }

        public Dictionary<string, object> HandlePropertyPanelChange(PropertyPanelPropertyChangedEventArgs args)
        {
            if (args.Id == "Width" && EffectBundleUiHelper.TrySetInt(Parameters, "Width", args.Value))
            {
                Parameters["Width"] = Math.Max(1, EffectBundleUiHelper.GetInt(Parameters, "Width", 1920));
            }
            else if (args.Id == "Height" && EffectBundleUiHelper.TrySetInt(Parameters, "Height", args.Value))
            {
                Parameters["Height"] = Math.Max(1, EffectBundleUiHelper.GetInt(Parameters, "Height", 1080));
            }
            else if (args.Id == "PreserveAspectRatio")
            {
                EffectBundleUiHelper.TrySetBool(Parameters, "PreserveAspectRatio", args.Value);
            }

            return Parameters;
        }

        public EffectBundleDisplayItem GetEffectBundleItem(string? locate = null)
        {
            return new EffectBundleDisplayItem
            {
                Name = EffectBundleUiHelper.L("DisplayName_Effect_Resize", "Resize"),
                Description = EffectBundleUiHelper.L("Description_Effect_Resize", "Resize the frame output width and height."),
                Thumbnail = ImageHelper.LoadFromAsset("icon_add")
            };
        }
    }
}