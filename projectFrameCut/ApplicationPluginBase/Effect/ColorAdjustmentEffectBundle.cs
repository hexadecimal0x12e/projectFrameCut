using projectFrameCut.ApplicationAPIBase.Effect;
using projectFrameCut.ApplicationAPIBase.Helpers;
using projectFrameCut.ApplicationAPIBase.Views.PropertyPanelBuilders;
using projectFrameCut.ApplicationAPIBase.Views.TabbedView;
using projectFrameCut.Render.Effect;
using projectFrameCut.Render.Plugin;
using projectFrameCut.Render.RenderAPIBase.EffectAndMixture;
using projectFrameCut.Shared;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using static LocalizedResources.SimpleLocalizerBaseGeneratedHelper_PropertyPanel;

namespace projectFrameCut.ApplicationPluginBase.Effect
{
    public class ColorAdjustmentEffectBundle : IEffectBundle
    {
        public Guid Id { get; set; } = Guid.NewGuid();

        public string Name { get; set; } = "ColorAdjustment";

        public string FromPlugin => InternalPluginBase.InternalPluginBaseID;

        public Dictionary<string, object> Parameters { get; set; } = new Dictionary<string, object>
        {
            {"Brightness", 1f},
            {"Contrast", 1f},
            {"Saturation", 1f},
            {"Hue", 0f},
            {"Gamma", 1f},
            {"Vibrance", 0f},
            {"Temperature", 0f},
            {"Invert", false},
            {"Grayscale", 0f},
            {"Opacity", 1f}
        };

        private static readonly Dictionary<string, EffectBundleSettableFields> s_settableFields = new()
        {
            { "Brightness", EffectBundleHelper.FloatField("Brightness", "Brightness", "Brightness adjustment", 1f, 0f, 2f) },
            { "Contrast", EffectBundleHelper.FloatField("Contrast", "Contrast", "Contrast adjustment", 1f, 0f, 3f) },
            { "Saturation", EffectBundleHelper.FloatField("Saturation", "Saturation", "Saturation adjustment", 1f, 0f, 3f) },
            { "Hue", EffectBundleHelper.FloatField("Hue", "Hue", "Hue rotation in degrees", 0f, 0f, 360f) },
            { "Gamma", EffectBundleHelper.FloatField("Gamma", "Gamma", "Gamma correction", 1f, 0.5f, 2f) },
            { "Vibrance", EffectBundleHelper.FloatField("Vibrance", "Vibrance", "Vibrance adjustment", 0f, -1f, 1f) },
            { "Temperature", EffectBundleHelper.FloatField("Temperature", "Temperature", "Color temperature adjustment", 0f, -100f, 100f) },
            { "Invert", EffectBundleHelper.BoolField("Invert", "Invert Colors", "Invert all colors", false) },
            { "Grayscale", EffectBundleHelper.FloatField("Grayscale", "Grayscale", "Grayscale conversion amount", 0f, 0f, 1f) },
            { "Opacity", EffectBundleHelper.FloatField("Opacity", "Opacity", "Overall opacity", 1f, 0f, 1f) }
        };

        public List<string> ParametersNeeded => new List<string>
        {
            "Brightness",
            "Contrast",
            "Saturation",
            "Hue",
            "Gamma",
            "Vibrance",
            "Temperature",
            "Invert",
            "Grayscale",
            "Opacity"
        };

        public Dictionary<string, string> ParametersType => new Dictionary<string, string>
        {
            {"Brightness", "float"},
            {"Contrast", "float"},
            {"Saturation", "float"},
            {"Hue", "float"},
            {"Gamma", "float"},
            {"Vibrance", "float"},
            {"Temperature", "float"},
            {"Invert", "bool"},
            {"Grayscale", "float"},
            {"Opacity", "float"}
        };
        public Dictionary<string, EffectBundleSettableFields> SettableFields => s_settableFields;

        public string TypeName => "ColorAdjustment";

        public Guid BindedInputId { get; set; } = IEffectBundle.InputAnchorGUID;

        public Guid BindedOutputId { get; set; } = IEffectBundle.OutputAnchorGUID;

        public bool IsMultiInput => false;
        public string InputAnchorDisplayName => string.Empty;
        public string[]? InputAnchorsDisplayName => null;
        public string OutputAnchorDisplayName => string.Empty;
        public List<Guid>? BindedInputIds { get; set; }

        public bool Enabled { get; set; } = true;

        public int StartPoint { get; set; }
        public int EndPoint { get; set; }

        public EffectType TypeOfEffect => EffectType.NormalEffect;

        public EffectTarget Target => EffectTarget.ColorAdjustment | EffectTarget.IsNotVisibleInEffectEditor;

        public IEffectFactory[] Create()
        {
            var factory = new ColorAdjustmentEffectFactory();
            this.ConfigureFactory(factory);
            return new IEffectFactory[] { factory };
        }

        public PropertyPanelBuilder CreateUI()
        {
            PropertyPanelBuilder ppb = new();

            ppb.AddCustomChild(new CompactTabView
            {
                TabItems = new ObservableCollection<TabbedViewItem>
                {
                    new TabbedViewItem
                    {
                        Header = EffectBundleHelper.L("ColorAdjust_TabTone", "Tone"),
                        Content = BuildToneTab().ListenToChanges((s, e) => PropertyPanelPropertyChangedEventArgs.CreateAndInvoke(ppb, e)).Build()
                    },
                    new TabbedViewItem
                    {
                        Header = EffectBundleHelper.L("ColorAdjust_TabColor", "Color"),
                        Content = BuildColorTab().ListenToChanges((s, e) => PropertyPanelPropertyChangedEventArgs.CreateAndInvoke(ppb, e)).Build()
                    },
                    new TabbedViewItem
                    {
                        Header = EffectBundleHelper.L("ColorAdjust_TabAdvanced", "Advanced"),
                        Content = BuildAdvancedTab().ListenToChanges((s, e) => PropertyPanelPropertyChangedEventArgs.CreateAndInvoke(ppb, e)).Build()
                    },
                    new TabbedViewItem
                    {
                        Header = EffectBundleHelper.L("ColorAdjust_TabEffects", "Effects"),
                        Content = BuildEffectsTab().ListenToChanges((s, e) => PropertyPanelPropertyChangedEventArgs.CreateAndInvoke(ppb, e)).Build()
                    }
                }
            });
            return ppb;
        }

        private PropertyPanelBuilder BuildToneTab()
        {
            float brightness = EffectBundleHelper.GetFloat(Parameters, "Brightness", 1f);
            float contrast = EffectBundleHelper.GetFloat(Parameters, "Contrast", 1f);

            var panel = new PropertyPanelBuilder();
            panel.AddSlider(
                "Brightness",
                EffectBundleHelper.L("ColorAdjust_TabTone_Brightness", "Brightness"),
                0,
                2,
                brightness,
                null,
                SliderUpdateEventCallMode.OnMouseUp);
            panel.AddSlider(
                "Contrast",
                EffectBundleHelper.L("ColorAdjust_TabTone_Contrast", "Contrast"),
                0,
                3,
                contrast,
                null,
                SliderUpdateEventCallMode.OnMouseUp);

            return panel;
        }

        private PropertyPanelBuilder BuildColorTab()
        {
            float saturation = EffectBundleHelper.GetFloat(Parameters, "Saturation", 1f);
            float hue = EffectBundleHelper.GetFloat(Parameters, "Hue", 0f);
            float vibrance = EffectBundleHelper.GetFloat(Parameters, "Vibrance", 0f);

            var panel = new PropertyPanelBuilder();
            panel.AddSlider(
                "Saturation",
                EffectBundleHelper.L("ColorAdjust_TabColor_Saturation", "Saturation"),
                0,
                3,
                saturation,
                null,
                SliderUpdateEventCallMode.OnMouseUp);
            panel.AddSlider(
                "Hue",
                EffectBundleHelper.L("ColorAdjust_TabColor_Hue", "Hue"),
                0,
                360,
                hue,
                null,
                SliderUpdateEventCallMode.OnMouseUp);
            panel.AddSlider(
                "Vibrance",
                EffectBundleHelper.L("ColorAdjust_TabColor_Vibrance", "Vibrance"),
                -1,
                1,
                vibrance,
                null,
                SliderUpdateEventCallMode.OnMouseUp);
            return panel;
        }

        private PropertyPanelBuilder BuildAdvancedTab()
        {
            float gamma = EffectBundleHelper.GetFloat(Parameters, "Gamma", 1f);
            float temperature = EffectBundleHelper.GetFloat(Parameters, "Temperature", 0f);

            var panel = new PropertyPanelBuilder();
            panel.AddSlider(
                "Gamma",
                EffectBundleHelper.L("ColorAdjust_TabAdvanced_Gamma", "Gamma"),
                0.5f,
                2f,
                gamma,
                null,
                SliderUpdateEventCallMode.OnMouseUp);
            panel.AddSlider(
                "Temperature",
                EffectBundleHelper.L("ColorAdjust_TabAdvanced_Temperature", "Temperature"),
                -100,
                100,
                temperature,
                null,
                SliderUpdateEventCallMode.OnMouseUp);
            return panel;
        }

        private PropertyPanelBuilder BuildEffectsTab()
        {
            bool invert = EffectBundleHelper.GetBool(Parameters, "Invert", false);
            float grayscale = EffectBundleHelper.GetFloat(Parameters, "Grayscale", 0f);
            float opacity = EffectBundleHelper.GetFloat(Parameters, "Opacity", 1f);

            var panel = new PropertyPanelBuilder();
            panel.AddCheckbox(
                "Invert",
                EffectBundleHelper.L("ColorAdjust_TabEffects_Invert", "Invert Colors"),
                invert);
            panel.AddSlider(
                "Grayscale",
                EffectBundleHelper.L("ColorAdjust_TabEffects_Grayscale", "Grayscale"),
                0,
                1,
                grayscale,
                null,
                SliderUpdateEventCallMode.OnMouseUp);
            panel.AddSlider(
                "Opacity",
                EffectBundleHelper.L("ColorAdjust_TabEffects_Opacity", "Opacity"),
                0,
                1,
                opacity,
                null,
                SliderUpdateEventCallMode.OnMouseUp);
            return panel;
        }

        public Dictionary<string, object> HandlePropertyPanelChange(PropertyPanelPropertyChangedEventArgs args)
        {
            if (args.Id == "Brightness" && EffectBundleHelper.TrySetFloat(Parameters, "Brightness", args.Value))
            {
            }

            if (args.Id == "Contrast" && EffectBundleHelper.TrySetFloat(Parameters, "Contrast", args.Value))
            {
            }

            if (args.Id == "Saturation" && EffectBundleHelper.TrySetFloat(Parameters, "Saturation", args.Value))
            {
            }

            if (args.Id == "Hue" && EffectBundleHelper.TrySetFloat(Parameters, "Hue", args.Value))
            {
            }

            if (args.Id == "Gamma" && EffectBundleHelper.TrySetFloat(Parameters, "Gamma", args.Value))
            {
            }

            if (args.Id == "Vibrance" && EffectBundleHelper.TrySetFloat(Parameters, "Vibrance", args.Value))
            {
            }

            if (args.Id == "Temperature" && EffectBundleHelper.TrySetFloat(Parameters, "Temperature", args.Value))
            {
            }

            if (args.Id == "Invert" && EffectBundleHelper.TrySetBool(Parameters, "Invert", args.Value))
            {
            }

            if (args.Id == "Grayscale" && EffectBundleHelper.TrySetFloat(Parameters, "Grayscale", args.Value))
            {
            }

            if (args.Id == "Opacity" && EffectBundleHelper.TrySetFloat(Parameters, "Opacity", args.Value))
            {
            }

            return Parameters;
        }

        public bool HandleSettableFieldsChange(EffectBundleSettableFields field, object value, out string feedback)
        {
            return EffectBundleHelper.HandleSettableFieldChange(Parameters, field, value, out feedback);
        }

        public EffectBundleDisplayItem GetEffectBundleItem(string? locate = null)
        {
            return new EffectBundleDisplayItem
            {
                Name = EffectBundleHelper.L("DisplayName_Effect_ColorAdjustment", "Color Adjustment"),
                Description = EffectBundleHelper.L("Description_Effect_ColorAdjustment", "Comprehensive color adjustment with brightness, contrast, saturation, hue, gamma, vibrance, temperature, invert, grayscale, and opacity controls.")
            };
        }
    }
}
