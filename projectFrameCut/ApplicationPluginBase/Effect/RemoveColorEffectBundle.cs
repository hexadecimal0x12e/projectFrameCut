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
    public class RemoveColorEffectBundle : IEffectBundle
    {
        public Guid Id { get; set; } = Guid.NewGuid();

        public string Name { get; set; } = string.Empty;

        public string FromPlugin => InternalPluginBase.InternalPluginBaseID;

        public Dictionary<string, object> Parameters { get; set; } = new Dictionary<string, object>
        {
            { "R", (ushort)0 },
            { "G", (ushort)0 },
            { "B", (ushort)0 },
            { "A", ushort.MaxValue },
            { "Tolerance", (ushort)1200 },
        };

        public List<string> ParametersNeeded => new List<string>
        {
            "R",
            "G",
            "B",
            "A",
            "Tolerance",
        };

        public Dictionary<string, string> ParametersType => new Dictionary<string, string>
        {
            {"R", "ushort" },
            {"G", "ushort" },
            {"B", "ushort" },
            {"A", "ushort" },
            {"Tolerance", "ushort" },
        };

        public string TypeName => "RemoveColor";

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
            var factory = new RemoveColorEffectFactory();
            this.ConfigureFactory(factory);
            return new IEffectFactory[] { factory };
        }

        public PropertyPanelBuilder CreateUI()
        {
            ushort r = EffectBundleUiHelper.GetUShort(Parameters, "R", 0);
            ushort g = EffectBundleUiHelper.GetUShort(Parameters, "G", 0);
            ushort b = EffectBundleUiHelper.GetUShort(Parameters, "B", 0);
            ushort a = EffectBundleUiHelper.GetUShort(Parameters, "A", ushort.MaxValue);
            ushort tolerance = EffectBundleUiHelper.GetUShort(Parameters, "Tolerance", 1200);

            var panel = new PropertyPanelBuilder();
            EffectBundleUiHelper.AddNumericEntry(panel, "R", EffectBundleUiHelper.ParamLabel("R"), r.ToString(), "0");
            EffectBundleUiHelper.AddNumericEntry(panel, "G", EffectBundleUiHelper.ParamLabel("G"), g.ToString(), "0");
            EffectBundleUiHelper.AddNumericEntry(panel, "B", EffectBundleUiHelper.ParamLabel("B"), b.ToString(), "0");
            EffectBundleUiHelper.AddNumericEntry(panel, "A", EffectBundleUiHelper.ParamLabel("A"), a.ToString(), ushort.MaxValue.ToString());
            panel.AddSlider(
                "Tolerance",
                EffectBundleUiHelper.ParamLabel("Tolerance"),
                0,
                ushort.MaxValue,
                tolerance,
                null,
                SliderUpdateEventCallMode.OnValueChanged);
            return panel;
        }

        public Dictionary<string, object> HandlePropertyPanelChange(PropertyPanelPropertyChangedEventArgs args)
        {
            if ((args.Id == "R" || args.Id == "G" || args.Id == "B" || args.Id == "A" || args.Id == "Tolerance")
                && EffectBundleUiHelper.TrySetUShort(Parameters, args.Id, args.Value))
            {
                return Parameters;
            }

            return Parameters;
        }

        public EffectBundleDisplayItem GetEffectBundleItem(string? locate = null)
        {
            return new EffectBundleDisplayItem
            {
                Name = EffectBundleUiHelper.L("DisplayName_Effect_RemoveColor", "Remove Color"),
                Description = EffectBundleUiHelper.L("Description_Effect_RemoveColor", "Remove a specific color from the image based on tolerance."),
                Thumbnail = ImageHelper.LoadFromAsset("icon_effect_remove_color")
            };
        }
    }
}
