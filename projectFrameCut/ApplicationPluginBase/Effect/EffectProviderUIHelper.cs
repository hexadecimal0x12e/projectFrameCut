using projectFrameCut.ApplicationAPIBase.Effect;
using projectFrameCut.ApplicationAPIBase.Views.PropertyPanelBuilders;
using projectFrameCut.Render.RenderAPIBase.EffectAndMixture;
using System;
using System.Collections.Generic;
using System.Globalization;

namespace projectFrameCut.ApplicationPluginBase.Effect
{
    /// <summary>
    /// Metadata driven property UI helper. It walks the fields described by an <see cref="IEffectProvider"/>'s
    /// <see cref="IEffectProvider.Fields"/> (FieldType / Min / Max / PresetOptions) and generates the
    /// <see cref="PropertyPanelBuilder"/> controls automatically.
    /// </summary>
    internal static class EffectProviderUIHelper
    {
        public static void BuildUI(IEffectProvider provider, PropertyPanelBuilder panel, IEffectBindingHost? bindingHost)
        {
            foreach (var kvp in provider.Fields)
            {
                var field = kvp.Value;
                if (field is null) continue;
                if (field.FieldType.HasFlag(EffectArgumentFieldType.IPicture)) continue;
                bool isBound = field.IsDynamic;
                var componentId = ComponentId(provider, field.Id);

                // Enum-like string fields are rendered as a picker.
                if (field.PresetOptions is { Length: > 0 })
                {
                    var current = GetString(provider, field.Id);
                    var defaultValue = Array.IndexOf(field.PresetOptions, current) >= 0 ? current : field.PresetOptions[0];
                    panel.AddPicker(componentId, Label(field.Id), field.PresetOptions, defaultValue);
                    MaybeWrapWithBind(panel, provider, field, componentId, bindingHost, isBound);
                    continue;
                }

                var baseType = field.FieldType & (EffectArgumentFieldType)0x3FF;
                switch (baseType)
                {
                    case EffectArgumentFieldType.Boolean:
                        panel.AddCheckbox(componentId, Label(field.Id), GetBool(provider, field.Id));
                        MaybeWrapWithBind(panel, provider, field, componentId, bindingHost, isBound);
                        break;
                    case EffectArgumentFieldType.UnsignedInteger:
                        AddNumericEntry(panel, provider, field, componentId);
                        MaybeWrapWithBind(panel, provider, field, componentId, bindingHost, isBound);
                        break;
                    case EffectArgumentFieldType.Integer:
                    case EffectArgumentFieldType.Numeric:
                        AddNumericOrSlider(panel, provider, field, componentId);
                        MaybeWrapWithBind(panel, provider, field, componentId, bindingHost, isBound);
                        break;
                    case EffectArgumentFieldType.String:
                        panel.AddEntry(componentId, Label(field.Id), GetString(provider, field.Id), field.DefaultValue);
                        MaybeWrapWithBind(panel, provider, field, componentId, bindingHost, isBound);
                        break;
                    default:
                        panel.AddText(EffectProviderHelper.L("_UnsupportedField", $"Field '{field.Id}' requires custom handling."));
                        break;
                }
            }
        }

        /// <summary>
        /// When a binding host is present (the node editor), wraps the just-added static editor control of a
        /// field with a bind button. While the field is bound, the static editor is disabled and the button is
        /// highlighted, showing the bound source in its tooltip.
        /// </summary>
        private static void MaybeWrapWithBind(PropertyPanelBuilder panel, IEffectProvider provider, IEffectArgumentField field, string componentId, IEffectBindingHost? bindingHost, bool isBound)
        {
            if (bindingHost is null) return;
            if (!panel.Components.TryGetValue(componentId, out var control) || control is null) return;

            if (isBound && control is Microsoft.Maui.Controls.View view)
            {
                view.IsEnabled = false; // static editor is read-only while bound
            }

            var button = new Microsoft.Maui.Controls.Button
            {
                Text = "🔗",
                WidthRequest = 34,
                HeightRequest = 34,
                FontSize = 12,
                Padding = new Thickness(2),
                HorizontalOptions = Microsoft.Maui.Controls.LayoutOptions.End,
                VerticalOptions = Microsoft.Maui.Controls.LayoutOptions.Center,
                BackgroundColor = isBound ? Microsoft.Maui.Graphics.Color.FromArgb("#f2c94c") : Microsoft.Maui.Graphics.Color.FromArgb("#3a3a3a"),
                TextColor = Microsoft.Maui.Graphics.Colors.White,
            };
            if (isBound && field is DynamicEffectParamField df && df.BoundProviderId is { } boundSource
                && bindingHost.GetSourceDisplayName(boundSource) is { } sourceName)
            {
                Microsoft.Maui.Controls.ToolTipProperties.SetText(button, $"Bound to: {sourceName}");
            }
            button.Clicked += async (s, e) => await bindingHost.EditBinding(field.Id);

            var row = new Microsoft.Maui.Controls.Grid
            {
                ColumnDefinitions =
                {
                    new Microsoft.Maui.Controls.ColumnDefinition(Microsoft.Maui.GridLength.Star),
                    new Microsoft.Maui.Controls.ColumnDefinition(Microsoft.Maui.GridLength.Auto),
                },
                ColumnSpacing = 4,
                VerticalOptions = Microsoft.Maui.Controls.LayoutOptions.Center,
            };
            row.Add(button, 1);
            if (panel.ReplaceComponent(componentId, row))
            {
                // Replace first so the original control's outer Grid.Column is preserved for the
                // wrapper. Only then assign the control to the wrapper's first column.
                row.Add(control, 0);
            }
        }

        private static void AddNumericOrSlider(PropertyPanelBuilder panel, IEffectProvider provider, IEffectArgumentField field, string componentId)
        {
            if (TryGetMinMax(field, out var min, out var max))
            {
                panel.AddSlider(componentId, Label(field.Id), min, max, GetDouble(provider, field.Id), eventCallMode: SliderUpdateEventCallMode.OnMouseUp);
            }
            else
            {
                AddNumericEntry(panel, provider, field, componentId);
            }
        }

        private static void AddNumericEntry(PropertyPanelBuilder panel, IEffectProvider provider, IEffectArgumentField field, string componentId)
        {
            panel.AddEntry(componentId, Label(field.Id), GetString(provider, field.Id), field.DefaultValue, entry => entry.Keyboard = Microsoft.Maui.Keyboard.Numeric);
        }

        private static bool TryGetMinMax(IEffectArgumentField field, out double min, out double max)
        {
            min = 0;
            max = 0;
            if (string.IsNullOrWhiteSpace(field.MinValue) || string.IsNullOrWhiteSpace(field.MaxValue))
            {
                return false;
            }

            return double.TryParse(field.MinValue, NumberStyles.Float, CultureInfo.InvariantCulture, out min)
                && double.TryParse(field.MaxValue, NumberStyles.Float, CultureInfo.InvariantCulture, out max);
        }

        /// <summary>
        /// Writes the changed value back into the provider's fields.
        /// </summary>
        public static (Dictionary<string, object>? newParams, Dictionary<string, IEffectArgumentField>? newFields) HandleChange(IEffectProvider provider, PropertyPanelPropertyChangedEventArgs args)
        {
            var fields = provider.Fields;
            var fieldId = FieldId(provider, args.Id);
            if (fields.TryGetValue(fieldId, out var field) && field is not null)
            {
                WriteBack(fields, field, fieldId, args.Value);
            }
            else
            {
                // Compound Position / Size coordinates use the {id}_X / {id}_Y / {id}_W / {id}_H convention.
                // No built-in effect uses a single Position/Size field currently, so a generic fallback is enough here.
                int sep = fieldId.LastIndexOf('_');
                if (sep > 0 && fields.TryGetValue(fieldId[..sep], out _))
                {
                    fields[fieldId] = new StaticEffectArgumentField
                    {
                        Id = fieldId,
                        FieldType = EffectArgumentFieldType.Integer,
                        Value = args.Value ?? string.Empty,
                    };
                }
            }

            provider.Fields = fields;
            return (null, fields);
        }

        private static void WriteBack(Dictionary<string, IEffectArgumentField> fields, IEffectArgumentField field, string id, object? value)
        {
            object? convertedValue = null;
            bool converted = false;
            var baseType = field.FieldType & (EffectArgumentFieldType)0x3FF;
            switch (baseType)
            {
                case EffectArgumentFieldType.Boolean:
                    if (EffectParamConvert.TryConvertToBool(value, out var b))
                    {
                        convertedValue = b;
                        converted = true;
                    }
                    break;
                case EffectArgumentFieldType.UnsignedInteger:
                    if (EffectParamConvert.TryConvertToUShort(value, out var us))
                    {
                        convertedValue = us;
                        converted = true;
                    }
                    break;
                case EffectArgumentFieldType.Integer:
                    if (EffectParamConvert.TryConvertToInt(value, out var i))
                    {
                        convertedValue = i;
                        converted = true;
                    }
                    break;
                case EffectArgumentFieldType.Numeric:
                    if (EffectParamConvert.TryConvertToFloat(value, out var f))
                    {
                        convertedValue = f;
                        converted = true;
                    }
                    break;
                default:
                    convertedValue = value?.ToString() ?? string.Empty;
                    converted = true;
                    break;
            }

            if (!converted) return;

            fields[id] = new StaticEffectArgumentField
            {
                Id = id,
                FieldType = field.FieldType,
                Value = convertedValue!,
                DefaultValue = field.DefaultValue,
                MinValue = field.MinValue,
                MaxValue = field.MaxValue,
                PresetOptions = field.PresetOptions,
                Remarks = field.Remarks,
            };
        }

        private static bool GetBool(IEffectProvider provider, string id)
        {
            if (provider.Fields.TryGetValue(id, out var field) && field is StaticEffectArgumentField sf && sf.Value is bool b)
                return b;
            return false;
        }

        private static string GetString(IEffectProvider provider, string id)
        {
            if (provider.Fields.TryGetValue(id, out var field) && field is StaticEffectArgumentField sf)
                return sf.Value?.ToString() ?? string.Empty;
            return string.Empty;
        }

        private static double GetDouble(IEffectProvider provider, string id)
        {
            if (provider.Fields.TryGetValue(id, out var field) && field is StaticEffectArgumentField sf)
            {
                if (sf.Value is float f) return f;
                if (sf.Value is double d) return d;
                if (sf.Value is int i) return i;
            }
            return 0f;
        }

        private static PropertyPanelItemLabel Label(string id)
        {
            return EffectProviderHelper.ParamLabel(id);
        }

        private static string ComponentId(IEffectProvider provider, string fieldId)
        {
            return $"EffectProviderField|{provider.Id:N}|{fieldId}";
        }

        private static string FieldId(IEffectProvider provider, string componentId)
        {
            var prefix = $"EffectProviderField|{provider.Id:N}|";
            return componentId.StartsWith(prefix, StringComparison.Ordinal)
                ? componentId[prefix.Length..]
                : componentId;
        }
    }
}
