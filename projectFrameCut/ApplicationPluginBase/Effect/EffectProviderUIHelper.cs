using projectFrameCut.ApplicationAPIBase.Effect;
using projectFrameCut.ApplicationAPIBase.Views.PropertyPanelBuilders;
using projectFrameCut.Render.Plugin;
using projectFrameCut.Render.Text;
using projectFrameCut.Services;
using System.Text.Json;
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
        public static void BuildUI(IEffectProvider provider, PropertyPanelBuilder panel, IEffectBindingHost? bindingHost, IEnumerable<string>? fieldIds = null,
            IReadOnlyDictionary<string, IEffectArgumentField>? fields = null)
        {
            foreach (var kvp in fields ?? provider.Fields)
            {
                if (fieldIds is not null && !fieldIds.Contains(kvp.Key)) continue;
                var field = kvp.Value;
                if (field is null || field.FieldType.HasFlag(EffectArgumentFieldType.NotVisibleInEffectPanel)
                    || field.FieldType.HasFlag(EffectArgumentFieldType.InternalUseOnly)) continue;
                bool isBound = field.IsDynamic;
                var componentId = ComponentId(provider, field.Id);
                if (field.FieldType.IsContent() || field.FieldType.HasFlag(EffectArgumentFieldType.Any)
                    || field.TypeContract is not null
                    || field.FieldType.BaseType() is EffectArgumentFieldType.AudioSample or EffectArgumentFieldType.SpeedVariance or EffectArgumentFieldType.SizeAndPosition)
                {
                    if (field.TypeContract is not null && bindingHost is ICustomEffectPortEditorHost editorHost
                        && editorHost.CanOpenExternalEditor(field.Id))
                    {
                        var button = new Button { Text = Localized.Effect_OpenExternalEditor, IsEnabled = !isBound };
                        var busy = new ActivityIndicator { IsVisible = false, IsRunning = false, WidthRequest = 24, HeightRequest = 24, VerticalOptions = LayoutOptions.Center };
                        var row = new Grid
                        {
                            ColumnDefinitions = { new ColumnDefinition(GridLength.Star), new ColumnDefinition(GridLength.Auto) },
                            ColumnSpacing = 8,
                        };
                        row.Add(button, 0);
                        row.Add(busy, 1);
                        bool opening = false;
                        void UpdateState()
                        {
                            button.IsEnabled = !isBound && !opening && !CustomEffectPortEditorService.IsOpen;
                            busy.IsRunning = busy.IsVisible = CustomEffectPortEditorService.IsConnecting(provider, field.Id)
                                || (opening && !CustomEffectPortEditorService.IsOpen);
                        }
                        row.Loaded += (_, _) =>
                        {
                            CustomEffectPortEditorService.StateChanged -= UpdateState;
                            CustomEffectPortEditorService.StateChanged += UpdateState;
                            UpdateState();
                        };
                        row.Unloaded += (_, _) => CustomEffectPortEditorService.StateChanged -= UpdateState;
                        UpdateState();
                        button.Clicked += async (_, _) =>
                        {
                            if (opening || CustomEffectPortEditorService.IsOpen) return;
                            opening = true;
                            UpdateState();
                            try { await editorHost.OpenExternalEditorAsync(field.Id); }
                            finally
                            {
                                opening = false;
                                UpdateState();
                            }
                        };
                        panel.AddCustomChild(Label(provider, field), row, componentId);
                    }
                    else panel.AddText(Label(provider, field), componentId);
                    MaybeWrapWithBind(panel, provider, field, componentId, bindingHost, isBound);
                    continue;
                }

                // Enum-like string fields are rendered as a picker.
                if (field.PresetOptions is { Length: > 0 })
                {
                    var current = GetString(field);
                    var defaultValue = Array.IndexOf(field.PresetOptions, current) >= 0 ? current : field.PresetOptions[0];
                    panel.AddPicker(componentId, Label(provider, field), field.PresetOptions, defaultValue,
                        p => p.ItemDisplayBinding = new Binding(".", BindingMode.OneWay,
                            new PresetOptionConverter($"Effect_{provider.TypeName}_{field.Id.Replace('.', '_')}_")));
                    MaybeWrapWithBind(panel, provider, field, componentId, bindingHost, isBound);
                    continue;
                }

                var baseType = field.FieldType & (EffectArgumentFieldType)0x3FF;
                switch (baseType)
                {
                    case EffectArgumentFieldType.Boolean:
                        panel.AddCheckbox(componentId, Label(provider, field), GetBool(field));
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
                        panel.AddEntry(componentId, Label(provider, field), GetString(field), field.DefaultValue);
                        MaybeWrapWithBind(panel, provider, field, componentId, bindingHost, isBound);
                        break;
                    case EffectArgumentFieldType.Color:
                        panel.AddCustomChild(Label(provider, field), invoker =>
                        {
                            var color = Microsoft.Maui.Graphics.Colors.Transparent;
                            var value = GetString(field);
                            if (string.IsNullOrWhiteSpace(value)) value = field.DefaultValue;
                            try
                            {
                                if (value?.StartsWith('{') == true)
                                {
                                    using var json = JsonDocument.Parse(value);
                                    color = Microsoft.Maui.Graphics.Color.FromRgba(
                                        json.RootElement.GetProperty("r").GetUInt16() / 65535d,
                                        json.RootElement.GetProperty("g").GetUInt16() / 65535d,
                                        json.RootElement.GetProperty("b").GetUInt16() / 65535d,
                                        json.RootElement.GetProperty("a").GetSingle());
                                }
                                else if (!string.IsNullOrWhiteSpace(value))
                                    color = Microsoft.Maui.Graphics.Color.FromArgb(value);
                            }
                            catch (Exception ex) when (ex is ArgumentException or FormatException or JsonException or InvalidOperationException or KeyNotFoundException)
                            {
                                projectFrameCut.Shared.Logger.Log(ex, $"Read effect color {field.Id}", provider);
                            }
                            var swatch = new BoxView
                            {
                                Color = color, WidthRequest = 30, HeightRequest = 30,
                                CornerRadius = 5, VerticalOptions = LayoutOptions.Center
                            };
                            var label = new Microsoft.Maui.Controls.Label
                            {
                                Text = ColorHex(color), VerticalOptions = LayoutOptions.Center
                            };
                            var row = new HorizontalStackLayout { Spacing = 8, Children = { swatch, label } };
                            bool opening = false;
                            var tap = new TapGestureRecognizer();
                            tap.Tapped += async (_, _) =>
                            {
                                if (opening || App.GetCurrentPage() is not DraftPage page) return;
                                opening = true;
                                try
                                {
                                    var picker = new projectFrameCut.ApplicationAPIBase.Views.Pickers.ColorPicker { SelectedColor = swatch.Color };
                                    picker.SelectedColorChanged += (_, selected) =>
                                    {
                                        swatch.Color = selected;
                                        label.Text = ColorHex(selected);
                                        invoker(label.Text);
                                    };
                                    await page.ShowAPopup(new ScrollView
                                    {
                                        Content = new VerticalStackLayout
                                        {
                                            Spacing = 10,
                                            Padding = new Thickness(10, 0),
                                            Children =
                                            {
                                                new Button { Text = Localized._Hide, Command = new Command(async () => await page.HidePopup(true)) },
                                                picker
                                            }
                                        }
                                    }, mode: "dialog");
                                }
                                catch (Exception ex) { projectFrameCut.Shared.Logger.Log(ex, $"Open effect color picker {field.Id}", provider); }
                                finally { opening = false; }
                            };
                            row.GestureRecognizers.Add(tap);
                            return row;
                        }, componentId, GetString(field));
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
            if (bindingHost is null || field.FieldType.HasFlag(EffectArgumentFieldType.CannotBeDynamic)) return;
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
                Microsoft.Maui.Controls.ToolTipProperties.SetText(button, $"{FieldName(provider, field.Id, true)} ← {sourceName}");
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
                panel.AddSlider(componentId, Label(provider, field), min, max, GetDouble(field), eventCallMode: SliderUpdateEventCallMode.OnMouseUp);
            }
            else
            {
                AddNumericEntry(panel, provider, field, componentId);
            }
        }

        private static void AddNumericEntry(PropertyPanelBuilder panel, IEffectProvider provider, IEffectArgumentField field, string componentId)
        {
            panel.AddEntry(componentId, Label(provider, field), GetString(field), field.DefaultValue, entry => entry.Keyboard = Microsoft.Maui.Keyboard.Numeric);
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
            var baseType = field.FieldType.BaseType();
            switch (baseType)
            {
                case EffectArgumentFieldType.Any:
                case EffectArgumentFieldType.CustomType when field.TypeContract is not null:
                    convertedValue = value;
                    converted = true;
                    break;
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
                TypeContract = field.TypeContract,
                DataFormat = field.DataFormat,
                Value = convertedValue!,
                DefaultValue = field.DefaultValue,
                MinValue = field.MinValue,
                MaxValue = field.MaxValue,
                PresetOptions = field.PresetOptions,
                Remarks = field.Remarks,
            };
        }

        private static string ColorHex(Microsoft.Maui.Graphics.Color color) =>
            color.Alpha == 1f ? "#" + color.ToArgbHex()[3..] : color.ToArgbHex();

        private static bool GetBool(IEffectArgumentField field) => field is StaticEffectArgumentField { Value: true };

        private static string GetString(IEffectArgumentField field) =>
            field is StaticEffectArgumentField sf ? sf.Value?.ToString() ?? string.Empty : string.Empty;

        private static double GetDouble(IEffectArgumentField field)
        {
            if (field is StaticEffectArgumentField sf)
            {
                if (sf.Value is float f) return f;
                if (sf.Value is double d) return d;
                if (sf.Value is int i) return i;
            }
            return 0f;
        }

        private sealed class PresetOptionConverter(string prefix) : IValueConverter
        {
            public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
                PresetOptionName(prefix, value);

            public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
                throw new NotSupportedException();
        }

        internal static string PresetOptionName(string prefix, object? value) => PluginManager.GetLocalizationItem(
            (prefix == "Effect_Jitter_Direction_" ? "_Effect_Jitter_" : prefix) + value, value?.ToString() ?? string.Empty);

        private static PropertyPanelItemLabel Label(IEffectProvider provider, IEffectArgumentField field) => FieldLabel(provider, field.Id);

        internal static (string name, string description) FieldInfo(IEffectProvider provider, string fieldId)
        {
            string id = fieldId.StartsWith(TextClipStyles.ArgumentPrefix, StringComparison.Ordinal)
                ? fieldId[TextClipStyles.ArgumentPrefix.Length..] : fieldId;
            if (provider.MetaData.TryGetValue(TextClipStyles.FromKey, out var from)
                && provider.MetaData.TryGetValue(TextClipStyles.TypeKey, out var type)
                && TextClipStyles.Create(from.ToString()!, type.ToString()!) is { } style
                && style.SettableFields.ContainsKey(id))
                return TextStyleServices.GetUIProvider(style).GetLocalizedFieldInfo(style, id, PluginManager.CurrentLocale);
            var info = EffectServices.GetUIProvider(provider).GetLocalizedFieldInfo(provider, fieldId, PluginManager.CurrentLocale);
            return fieldId switch
            {
                MixtureProviderBase.UpperInputKey => (Localized.Effect_MixtureUpper, info.description),
                MixtureProviderBase.LowerInputKey => (Localized.Effect_MixtureLower, info.description),
                _ => info,
            };
        }

        internal static PropertyPanelItemLabel FieldLabel(IEffectProvider provider, string id, string? nameKey = null)
        {
            var (name, description) = FieldInfo(provider, id);
            if (nameKey is not null) name = PluginManager.GetLocalizationItem(nameKey, name);
            return string.IsNullOrWhiteSpace(description) ? new SingleLineLabel(name) : new InfoSingleLineLabel(name, description);
        }

        internal static string FieldName(IEffectProvider provider, string id, bool isInput, string? fallback = null)
        {
            var name = FieldInfo(provider, id).name;
            return name == id ? fallback ?? name : name;
        }

        private static string ComponentId(IEffectProvider provider, string fieldId)
        {
            return $"EffectProviderField|{provider.Id:N}|{fieldId}";
        }

        internal static string FieldId(IEffectProvider provider, string componentId)
        {
            var prefix = $"EffectProviderField|{provider.Id:N}|";
            return componentId.StartsWith(prefix, StringComparison.Ordinal)
                ? componentId[prefix.Length..]
                : componentId;
        }
    }
}
