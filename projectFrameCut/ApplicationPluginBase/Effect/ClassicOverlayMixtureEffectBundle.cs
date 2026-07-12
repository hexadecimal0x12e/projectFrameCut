using projectFrameCut.ApplicationAPIBase.Effect;
using projectFrameCut.ApplicationAPIBase.Helpers;
using projectFrameCut.ApplicationAPIBase.Views.PropertyPanelBuilders;
using projectFrameCut.Render.Compose;
using projectFrameCut.Render.Plugin;
using projectFrameCut.Render.RenderAPIBase.EffectAndMixture;
using projectFrameCut.Services;
using projectFrameCut.Shared;
using System;
using System.Collections.Generic;

namespace projectFrameCut.ApplicationPluginBase.Effect
{
    public class ClassicOverlayMixtureEffectBundle : IEffectBundle
    {
        private static readonly string[] AccuracyModeOptions = ["Accurate", "Approximate"];

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

        public Dictionary<string, object> Parameters { get; set; } = new()
        {
            { "AccuracyMode", "Accurate" }
        };

        private static readonly Dictionary<string, EffectBundleSettableFields> s_settableFields = new()
        {
            { "AccuracyMode", EffectBundleHelper.EnumField("AccuracyMode", "Accuracy Mode", "Overlay accuracy mode", "Accurate", ["Accurate", "Approximate"]) },
        };

        public Dictionary<string, EffectBundleSettableFields> SettableFields => s_settableFields;

        public bool HandleSettableFieldsChange(EffectBundleSettableFields field, object value, out string feedback)
        {
            return EffectBundleHelper.HandleSettableFieldChange(Parameters, field, value, out feedback);
        }

        public List<string> ParametersNeeded => ["AccuracyMode"];
        public Dictionary<string, string> ParametersType => new()
        {
            { "AccuracyMode", "string" }
        };

        public IEffectFactory[] Create()
        {
            var factory = new ClassicOverlayMixtureFactory();
            this.ConfigureFactory(factory);
            return [factory];
        }

        public PropertyPanelBuilder CreateUI()
        {
            var mode = EffectBundleHelper.GetString(Parameters, "AccuracyMode", "Accurate");
            var panel = new PropertyPanelBuilder();
            panel.AddText(new SingleLineLabel(
                "Classic Overlay Mixture\nblends each frame onto the layer below using alpha compositing.", 14));
            panel.AddPicker(
                "AccuracyMode",
                EffectBundleHelper.L("ClassicOverlay_AccuracyMode", "Accuracy Mode"),
                AccuracyModeOptions,
                Array.IndexOf(AccuracyModeOptions, mode) >= 0 ? mode : "Accurate");
            return panel;
        }

        public Dictionary<string, object> HandlePropertyPanelChange(PropertyPanelPropertyChangedEventArgs args)
        {
            if (args.Id == "AccuracyMode")
            {
                var value = args.Value?.ToString();
                if (value != null && Array.IndexOf(AccuracyModeOptions, value) >= 0)
                {
                    Parameters["AccuracyMode"] = value;
                }
            }
            return Parameters;
        }

        public EffectBundleDisplayItem GetEffectBundleItem(string? locate = null)
        {
            return new EffectBundleDisplayItem
            {
                Name = EffectBundleHelper.L("DisplayName_Mixture_ClassicOverlay", "Classic Overlay"),
                Description = EffectBundleHelper.L("Description_Mixture_ClassicOverlay",
                    "Classic alpha-blend overlay. Blends each frame onto the layer below using standard alpha compositing."),
                Thumbnail = ImageSource.FromFile(FileSystemService.GetAppPackageFileSync("EffectSample", "classicOverlayMixture.png"))
            };
        }
    }
}
