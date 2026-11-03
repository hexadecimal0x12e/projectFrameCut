using projectFrameCut.ApplicationAPIBase.Effect;
using projectFrameCut.ApplicationAPIBase.Views.PropertyPanelBuilders;
using projectFrameCut.ApplicationAPIBase.Views.TabbedView;
using projectFrameCut.Render.RenderAPIBase.EffectAndMixture;
using System.Collections.Generic;
using System.Collections.ObjectModel;

namespace projectFrameCut.ApplicationPluginBase.Effect
{
    /// <summary>
    /// Custom property UI of the ColorAdjustment effect. Restores the legacy tabbed layout:
    /// Tone / Color / Advanced / Effects tabs, each owning a subset of the adjustment sliders.
    /// </summary>
    public class ColorAdjustmentUI : EffectProviderUI
    {
        public ColorAdjustmentUI(IEffectProvider inner) : base(inner)
        {
        }

        public override PropertyPanelBuilder CreateUI(IEffectProvider _)
        {
            var ppb = new PropertyPanelBuilder();

            // Forward changes inside each tab to the top-level panel so the node editor
            // (which subscribes to ppb.PropertyChanged) receives every slider/checkbox change.
            TabbedViewItem BuildTab(string headerKey, string headerFallback, PropertyPanelBuilder content)
            {
                content.ListenToChanges((s, e) => PropertyPanelPropertyChangedEventArgs.CreateAndInvoke(ppb, e));
                return new TabbedViewItem
                {
                    Header = EffectProviderHelper.L(headerKey, headerFallback),
                    Content = content.Build()
                };
            }

            ppb.AddCustomChild(new CompactTabView
            {
                TabItems = new ObservableCollection<TabbedViewItem>
                {
                    BuildTab("ColorAdjust_TabTone", "Tone", BuildToneTab()),
                    BuildTab("ColorAdjust_TabColor", "Color", BuildColorTab()),
                    BuildTab("ColorAdjust_TabAdvanced", "Advanced", BuildAdvancedTab()),
                    BuildTab("ColorAdjust_TabEffects", "Effects", BuildEffectsTab())
                }
            });
            return ppb;
        }

        private PropertyPanelBuilder BuildToneTab()
        {
            float brightness = EffectProviderHelper.GetFieldFloat(Inner.Fields, "Brightness", 1f);
            float contrast = EffectProviderHelper.GetFieldFloat(Inner.Fields, "Contrast", 1f);

            var panel = new PropertyPanelBuilder();
            panel.AddSlider(
                "Brightness",
                EffectProviderUIHelper.FieldLabel(Inner, "Brightness", "ColorAdjust_TabTone_Brightness"),
                0,
                2,
                brightness,
                null,
                SliderUpdateEventCallMode.OnMouseUp);
            panel.AddSlider(
                "Contrast",
                EffectProviderUIHelper.FieldLabel(Inner, "Contrast", "ColorAdjust_TabTone_Contrast"),
                0,
                3,
                contrast,
                null,
                SliderUpdateEventCallMode.OnMouseUp);
            return panel;
        }

        private PropertyPanelBuilder BuildColorTab()
        {
            float saturation = EffectProviderHelper.GetFieldFloat(Inner.Fields, "Saturation", 1f);
            float hue = EffectProviderHelper.GetFieldFloat(Inner.Fields, "Hue", 0f);
            float vibrance = EffectProviderHelper.GetFieldFloat(Inner.Fields, "Vibrance", 0f);

            var panel = new PropertyPanelBuilder();
            panel.AddSlider(
                "Saturation",
                EffectProviderUIHelper.FieldLabel(Inner, "Saturation", "ColorAdjust_TabColor_Saturation"),
                0,
                3,
                saturation,
                null,
                SliderUpdateEventCallMode.OnMouseUp);
            panel.AddSlider(
                "Hue",
                EffectProviderUIHelper.FieldLabel(Inner, "Hue", "ColorAdjust_TabColor_Hue"),
                0,
                360,
                hue,
                null,
                SliderUpdateEventCallMode.OnMouseUp);
            panel.AddSlider(
                "Vibrance",
                EffectProviderUIHelper.FieldLabel(Inner, "Vibrance", "ColorAdjust_TabColor_Vibrance"),
                -1,
                1,
                vibrance,
                null,
                SliderUpdateEventCallMode.OnMouseUp);
            return panel;
        }

        private PropertyPanelBuilder BuildAdvancedTab()
        {
            float gamma = EffectProviderHelper.GetFieldFloat(Inner.Fields, "Gamma", 1f);
            float temperature = EffectProviderHelper.GetFieldFloat(Inner.Fields, "Temperature", 0f);

            var panel = new PropertyPanelBuilder();
            panel.AddSlider(
                "Gamma",
                EffectProviderUIHelper.FieldLabel(Inner, "Gamma", "ColorAdjust_TabAdvanced_Gamma"),
                0.5f,
                2f,
                gamma,
                null,
                SliderUpdateEventCallMode.OnMouseUp);
            panel.AddSlider(
                "Temperature",
                EffectProviderUIHelper.FieldLabel(Inner, "Temperature", "ColorAdjust_TabAdvanced_Temperature"),
                -100,
                100,
                temperature,
                null,
                SliderUpdateEventCallMode.OnMouseUp);
            return panel;
        }

        private PropertyPanelBuilder BuildEffectsTab()
        {
            bool invert = EffectProviderHelper.GetFieldBool(Inner.Fields, "Invert", false);
            float grayscale = EffectProviderHelper.GetFieldFloat(Inner.Fields, "Grayscale", 0f);
            float opacity = EffectProviderHelper.GetFieldFloat(Inner.Fields, "Opacity", 1f);

            var panel = new PropertyPanelBuilder();
            panel.AddCheckbox(
                "Invert",
                EffectProviderUIHelper.FieldLabel(Inner, "Invert", "ColorAdjust_TabEffects_Invert"),
                invert);
            panel.AddSlider(
                "Grayscale",
                EffectProviderUIHelper.FieldLabel(Inner, "Grayscale", "ColorAdjust_TabEffects_Grayscale"),
                0,
                1,
                grayscale,
                null,
                SliderUpdateEventCallMode.OnMouseUp);
            panel.AddSlider(
                "Opacity",
                EffectProviderUIHelper.FieldLabel(Inner, "Opacity", "ColorAdjust_TabEffects_Opacity"),
                0,
                1,
                opacity,
                null,
                SliderUpdateEventCallMode.OnMouseUp);
            return panel;
        }

        public override (Dictionary<string, object>? newParams, Dictionary<string, IEffectArgumentField>? newFields) HandlePropertyPanelChange(IEffectProvider source, PropertyPanelPropertyChangedEventArgs args)
        {
            var fields = Inner.Fields;
            object? value = null;
            switch (args.Id)
            {
                case "Brightness":
                    value = DynamicParam.ToFloat(args.Value);
                    EffectProviderHelper.SetFieldValue(fields, "Brightness", value, EffectArgumentFieldType.Numeric);
                    break;
                case "Contrast":
                    value = DynamicParam.ToFloat(args.Value);
                    EffectProviderHelper.SetFieldValue(fields, "Contrast", value, EffectArgumentFieldType.Numeric);
                    break;
                case "Saturation":
                    value = DynamicParam.ToFloat(args.Value);
                    EffectProviderHelper.SetFieldValue(fields, "Saturation", value, EffectArgumentFieldType.Numeric);
                    break;
                case "Hue":
                    value = DynamicParam.ToFloat(args.Value);
                    EffectProviderHelper.SetFieldValue(fields, "Hue", value, EffectArgumentFieldType.Numeric);
                    break;
                case "Gamma":
                    value = DynamicParam.ToFloat(args.Value);
                    EffectProviderHelper.SetFieldValue(fields, "Gamma", value, EffectArgumentFieldType.Numeric);
                    break;
                case "Vibrance":
                    value = DynamicParam.ToFloat(args.Value);
                    EffectProviderHelper.SetFieldValue(fields, "Vibrance", value, EffectArgumentFieldType.Numeric);
                    break;
                case "Temperature":
                    value = DynamicParam.ToFloat(args.Value);
                    EffectProviderHelper.SetFieldValue(fields, "Temperature", value, EffectArgumentFieldType.Numeric);
                    break;
                case "Invert":
                    value = DynamicParam.ToBool(args.Value);
                    EffectProviderHelper.SetFieldValue(fields, "Invert", value, EffectArgumentFieldType.Boolean);
                    break;
                case "Grayscale":
                    value = DynamicParam.ToFloat(args.Value);
                    EffectProviderHelper.SetFieldValue(fields, "Grayscale", value, EffectArgumentFieldType.Numeric);
                    break;
                case "Opacity":
                    value = DynamicParam.ToFloat(args.Value);
                    EffectProviderHelper.SetFieldValue(fields, "Opacity", value, EffectArgumentFieldType.Numeric);
                    break;
                default:
                    return base.HandlePropertyPanelChange(source, args);
            }

            Inner.Fields = fields;
            return (null, fields);
        }
    }
}
