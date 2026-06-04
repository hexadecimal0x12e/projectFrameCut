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
    public class FlipEffectBundle : IEffectBundle
    {
        public Guid Id { get; set; } = Guid.NewGuid();
        public string Name { get; set; } = "Flip";
        public string FromPlugin => InternalPluginBase.InternalPluginBaseID;

        public Dictionary<string, object> Parameters { get; set; } = new Dictionary<string, object>
        {
            { "Horizontal", false },
            { "Vertical", false }
        };

        public List<string> ParametersNeeded => FlipEffect_IPicture.ParametersNeeded;
        public Dictionary<string, string> ParametersType => FlipEffect_IPicture.ParametersType;

        public string TypeName => "Flip";
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
            var factory = new FlipEffectFactory();
            this.ConfigureFactory(factory);
            return [factory];
        }

        public PropertyPanelBuilder CreateUI()
        {
            bool horizontal = EffectBundleUiHelper.GetBool(Parameters, "Horizontal", false);
            bool vertical = EffectBundleUiHelper.GetBool(Parameters, "Vertical", false);

            var panel = new PropertyPanelBuilder();
            panel.AddCheckbox("Horizontal", EffectBundleUiHelper.L("Effect_Flip_Horizontal", "Flip Horizontal"), horizontal);
            panel.AddCheckbox("Vertical", EffectBundleUiHelper.L("Effect_Flip_Vertical", "Flip Vertical"), vertical);
            return panel;
        }

        public Dictionary<string, object> HandlePropertyPanelChange(PropertyPanelPropertyChangedEventArgs args)
        {
            if (args.Id == "Horizontal")
            {
                EffectBundleUiHelper.TrySetBool(Parameters, "Horizontal", args.Value);
            }
            else if (args.Id == "Vertical")
            {
                EffectBundleUiHelper.TrySetBool(Parameters, "Vertical", args.Value);
            }

            return Parameters;
        }

        public EffectBundleDisplayItem GetEffectBundleItem(string? locate = null)
        {
            return new EffectBundleDisplayItem
            {
                Name = EffectBundleUiHelper.L("DisplayName_Effect_Flip", "Flip"),
                Description = EffectBundleUiHelper.L("Description_Effect_Flip", "Flip the frame horizontally and/or vertically."),
                Thumbnail = ImageHelper.LoadFromAsset("icon_add")
            };
        }
    }
}
