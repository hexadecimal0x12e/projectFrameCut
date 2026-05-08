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
    public class BlendModeMixtureEffectBundle : IEffectBundle
    {
        private static readonly string[] MixtureTypeOptions = ["Add", "Subtract", "Multiply", "Screen", "OverlayBlend", "Darken", "Lighten", "Difference"];

        private static readonly Dictionary<string, string> MixtureTypeDisplayNames = new()
        {
            { "Add", "Add (Linear Dodge)" },
            { "Subtract", "Subtract" },
            { "Multiply", "Multiply" },
            { "Screen", "Screen" },
            { "OverlayBlend", "Overlay" },
            { "Darken", "Darken" },
            { "Lighten", "Lighten" },
            { "Difference", "Difference" },
        };

        public Guid Id { get; set; } = Guid.NewGuid();
        public string Name { get; set; } = "Blend Mode";

        public string FromPlugin => InternalPluginBase.InternalPluginBaseID;
        public string TypeName => "BlendModeMixture";

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

        public Dictionary<string, object> Parameters { get; set; } = new()
        {
            { "MixtureType", "Add" }
        };

        public List<string> ParametersNeeded => ["MixtureType"];
        public Dictionary<string, string> ParametersType => new() { { "MixtureType", "string" } };

        public IEffectFactory[] Create()
        {
            var mixtureType = EffectBundleUiHelper.GetString(Parameters, "MixtureType", "Add");
            var factory = new BlendModeMixtureFactory { MixtureType = mixtureType };
            this.ConfigureFactory(factory);
            return [factory];
        }

        public PropertyPanelBuilder CreateUI()
        {
            var mixtureType = EffectBundleUiHelper.GetString(Parameters, "MixtureType", "Add");
            var panel = new PropertyPanelBuilder();
            panel.AddPicker(
                "MixtureType",
                EffectBundleUiHelper.L("BlendMode_MixtureType", "Blend Mode"),
                MixtureTypeOptions,
                mixtureType);
            return panel;
        }

        public Dictionary<string, object> HandlePropertyPanelChange(PropertyPanelPropertyChangedEventArgs args)
        {
            if (args.Id == "MixtureType")
            {
                var value = args.Value?.ToString();
                if (value != null && Array.IndexOf(MixtureTypeOptions, value) >= 0)
                    Parameters["MixtureType"] = value;
            }
            return Parameters;
        }

        public EffectBundleDisplayItem GetEffectBundleItem(string? locate = null)
        {
            return new EffectBundleDisplayItem
            {
                Name = EffectBundleUiHelper.L("DisplayName_Mixture_BlendMode", "Blend Mode"),
                Description = EffectBundleUiHelper.L("Description_Mixture_BlendMode",
                    "Composites the clip using a blend mode (Add, Subtract, Multiply, Screen, Overlay, Darken, Lighten, Difference)."),
                Thumbnail = ImageHelper.LoadFromAsset("icon_add")
            };
        }
    }
}
