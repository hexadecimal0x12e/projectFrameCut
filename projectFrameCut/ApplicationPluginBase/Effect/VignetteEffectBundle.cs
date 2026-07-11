using projectFrameCut.ApplicationAPIBase.Effect;
using projectFrameCut.ApplicationAPIBase.Helpers;
using projectFrameCut.ApplicationAPIBase.Views.PropertyPanelBuilders;
using projectFrameCut.Render.Effect;
using projectFrameCut.Render.Plugin;
using projectFrameCut.Render.RenderAPIBase.EffectAndMixture;
using projectFrameCut.Services;
using projectFrameCut.Shared;
using System;
using System.Collections.Generic;

namespace projectFrameCut.ApplicationPluginBase.Effect
{
    public class VignetteEffectBundle : IEffectBundle
    {
        public Guid Id { get; set; } = Guid.NewGuid();
        public string Name { get; set; } = "Vignette";
        public string FromPlugin => InternalPluginBase.InternalPluginBaseID;

        public Dictionary<string, object> Parameters { get; set; } = new Dictionary<string, object>
        {
            { "Strength", 0.5f },
            { "Radius", 0.65f }
        };

        private static readonly Dictionary<string, EffectBundleSettableFields> s_settableFields = new()
        {
            { "Strength", EffectBundleHelper.FloatField("Strength", "Strength", "Vignette strength", 0.5f, 0f, 1f) },
            { "Radius", EffectBundleHelper.FloatField("Radius", "Radius", "Vignette radius", 0.65f, 0f, 1f) },
        };

        public Dictionary<string, EffectBundleSettableFields> SettableFields => s_settableFields;

        public bool HandleSettableFieldsChange(EffectBundleSettableFields field, object value, out string feedback)
        {
            return EffectBundleHelper.HandleSettableFieldChange(Parameters, field, value, out feedback);
        }

        public List<string> ParametersNeeded => VignetteEffect_IPicture.ParametersNeeded;
        public Dictionary<string, string> ParametersType => VignetteEffect_IPicture.ParametersType;

        public string TypeName => "Vignette";
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
            var factory = new VignetteEffectFactory();
            this.ConfigureFactory(factory);
            return [factory];
        }

        public PropertyPanelBuilder CreateUI()
        {
            float strength = EffectBundleHelper.GetFloat(Parameters, "Strength", 0.5f);
            float radius = EffectBundleHelper.GetFloat(Parameters, "Radius", 0.65f);

            strength = Math.Clamp(strength, 0f, 1f);
            radius = Math.Clamp(radius, 0.05f, 0.99f);

            var panel = new PropertyPanelBuilder();
            panel.AddSlider(
                "Strength",
                EffectBundleHelper.L("Effect_Vignette_Strength", "Strength"),
                0,
                1,
                strength,
                null,
                SliderUpdateEventCallMode.OnMouseUp);
            panel.AddSlider(
                "Radius",
                EffectBundleHelper.L("Effect_Vignette_Radius", "Radius"),
                0.05f,
                0.99f,
                radius,
                null,
                SliderUpdateEventCallMode.OnMouseUp);
            return panel;
        }

        public Dictionary<string, object> HandlePropertyPanelChange(PropertyPanelPropertyChangedEventArgs args)
        {
            if (args.Id == "Strength" && EffectBundleHelper.TrySetFloat(Parameters, "Strength", args.Value))
            {
                Parameters["Strength"] = Math.Clamp(EffectBundleHelper.GetFloat(Parameters, "Strength", 0.5f), 0f, 1f);
            }
            else if (args.Id == "Radius" && EffectBundleHelper.TrySetFloat(Parameters, "Radius", args.Value))
            {
                Parameters["Radius"] = Math.Clamp(EffectBundleHelper.GetFloat(Parameters, "Radius", 0.65f), 0.05f, 0.99f);
            }

            return Parameters;
        }

        public EffectBundleDisplayItem GetEffectBundleItem(string? locate = null)
        {
            return new EffectBundleDisplayItem
            {
                Name = EffectBundleHelper.L("DisplayName_Effect_Vignette", "Vignette"),
                Description = EffectBundleHelper.L("Description_Effect_Vignette", "Darken image edges to emphasize the center area."),
                Thumbnail = ImageSource.FromFile(FileSystemService.GetAppPackageFileSync("EffectSample/vignette.png"))
            };
        }
    }
}
