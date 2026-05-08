using projectFrameCut.ApplicationAPIBase.Effect;
using projectFrameCut.ApplicationAPIBase.Helpers;
using projectFrameCut.ApplicationAPIBase.Views.PropertyPanelBuilders;
using projectFrameCut.Render.Compose;
using projectFrameCut.Render.Plugin;
using projectFrameCut.Render.RenderAPIBase.EffectAndMixture;
using projectFrameCut.Shared;
using System;
using System.Collections.Generic;

namespace projectFrameCut.ApplicationPluginBase.Effect
{
    public class ClassicOverlayMixtureEffectBundle : IEffectBundle
    {
        public Guid Id { get; set; } = Guid.NewGuid();
        public string Name { get; set; } = "Classic Overlay";
        public string FromPlugin => InternalPluginBase.InternalPluginBaseID;
        public string TypeName => "ClassicOverlayMixture";

        public EffectType TypeOfEffect => EffectType.MixtureProvider;
        public EffectTarget Target => EffectTarget.Mixture;

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

        public Dictionary<string, object> Parameters { get; set; } = new();

        bool IEffectBundle.IsUserVisibleEffect => false;

        public List<string> ParametersNeeded => [];
        public Dictionary<string, string> ParametersType => new();

        public IEffectFactory[] Create()
        {
            var factory = new ClassicOverlayMixtureFactory();
            this.ConfigureFactory(factory);
            return [factory];
        }

        public PropertyPanelBuilder CreateUI()
        {
            var panel = new PropertyPanelBuilder();
            panel.AddText(new SingleLineLabel(
                "Classic Overlay Mixture\nblends each frame onto the layer below using alpha compositing.", 14));
            return panel;
        }

        public Dictionary<string, object> HandlePropertyPanelChange(PropertyPanelPropertyChangedEventArgs args)
        {
            return Parameters;
        }

        public EffectBundleDisplayItem GetEffectBundleItem(string? locate = null)
        {
            return new EffectBundleDisplayItem
            {
                Name = EffectBundleUiHelper.L("DisplayName_Mixture_ClassicOverlay", "Classic Overlay"),
                Description = EffectBundleUiHelper.L("Description_Mixture_ClassicOverlay",
                    "Classic alpha-blend overlay. Blends each frame onto the layer below using standard alpha compositing."),
                Thumbnail = ImageHelper.LoadFromAsset("icon_add")
            };
        }
    }
}
