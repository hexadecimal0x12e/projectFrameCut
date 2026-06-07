using Microsoft.Maui.Graphics;
using CommunityToolkit.Maui.Extensions;
using CommunityToolkit.Maui.Views;
using projectFrameCut.ApplicationAPIBase.Text;
using projectFrameCut.ApplicationAPIBase.Views.PropertyPanelBuilders;
using projectFrameCut.ApplicationAPIBase.Views.Pickers;
using projectFrameCut.ApplicationPluginBase.DynamicPreviewProvider;
using projectFrameCut.Render.Plugin;
using projectFrameCut.Services;
using projectFrameCut.Shared;
using Microsoft.Maui.Controls.Shapes;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Threading.Tasks;
using static LocalizedResources.SimpleLocalizerBaseGeneratedHelper_PropertyPanel;
using System.Diagnostics;

namespace projectFrameCut.ApplicationPluginBase.Text
{
    public class BasicTextStyleProvider : ITextClipStyleProvider
    {
        protected const string TextKey = "Text";
        protected const string FontKey = "FontFamily";
        protected const string SizeKey = "FontSize";
        protected const string ColorKey = "Color";
        protected const string FontStyleKey = "FontStyle";
        protected const string HorizontalAlignmentKey = "HorizontalAlignment";
        protected const string VerticalAlignmentKey = "VerticalAlignment";
        protected const string WrappingWidthKey = "WrappingWidth";
        protected const string ApplyKerningKey = "ApplyKerning";
        protected const string LineSpacingKey = "LineSpacing";
        protected const string RotationKey = "Rotation";
        protected const string StrokeWidthKey = "StrokeWidth";
        protected const string StrokeColorKey = "StrokeColor";
        protected const string DpiKey = "Dpi";
        protected const string UseVerticalLayoutKey = "UseVerticalLayout";
        protected const string KeepNonCJKTextAsHorizontalKey = "KeepNonCJKTextAsHorizontal";
        public const string ManualSizeKey = "TextStyleManualSize";

        private Dictionary<string, string> _parameters = new();

        public BasicTextStyleProvider()
        {
            EnsureDefaults();
        }

        public string FromPlugin => InternalPluginBase.InternalPluginBaseID;

        public virtual string TypeName => "Basic";

        protected virtual string DefaultText => "Text";

        protected virtual float DefaultFontSize => 120f;

        public string BasicText
        {
            get => GetOrDefault(TextKey, DefaultText);
            set => _parameters[TextKey] = value ?? string.Empty;
        }

        public Dictionary<string, string> Parameters
        {
            get => _parameters;
            set
            {
                _parameters = value ?? new Dictionary<string, string>();
                EnsureDefaults();
            }
        }

        public bool AllowFreeRatioResize => true;

        public TextClipEntry[] BuildEntries()
        {
            var fontFamily = GetOrDefault(FontKey, "Arial");
            var fontSize = ParseFloat(GetOrDefault(SizeKey, DefaultFontSize.ToString(CultureInfo.InvariantCulture)), DefaultFontSize);
            var colorText = GetOrDefault(ColorKey, "#FFFFFF");
            var color = ParseColorOrFallback(colorText, Colors.White);
            var fontStyle = ParseFontStyle(GetOrDefault(FontStyleKey, ClipFontStyle.Regular.ToString()), ClipFontStyle.Regular);
            var horizontalAlignment = ParseHorizontalAlignment(GetOrDefault(HorizontalAlignmentKey, ClipHorizontalAlignment.Left.ToString()), ClipHorizontalAlignment.Left);
            var verticalAlignment = ParseVerticalAlignment(GetOrDefault(VerticalAlignmentKey, ClipVerticalAlignment.Top.ToString()), ClipVerticalAlignment.Top);
            var wrappingWidth = ParseNullableFloat(GetOrDefault(WrappingWidthKey, string.Empty));
            var applyKerning = ParseBool(GetOrDefault(ApplyKerningKey, bool.TrueString), true);
            var lineSpacing = ParseFloat(GetOrDefault(LineSpacingKey, 1f.ToString(CultureInfo.InvariantCulture)), 1f);
            var rotation = ParseFloat(GetOrDefault(RotationKey, 0f.ToString(CultureInfo.InvariantCulture)), 0f);
            var strokeWidth = ParseNullableFloat(GetOrDefault(StrokeWidthKey, string.Empty));
            var strokeColor = ParseColorOrFallback(GetOrDefault(StrokeColorKey, "#000000"), Colors.Black);
            var dpi = ParseNullableFloat(GetOrDefault(DpiKey, string.Empty));
            var useVerticalLayout = ParseBool(GetOrDefault(UseVerticalLayoutKey, bool.FalseString), false);
            var keepNonCJKTextAsHorizontal = ParseBool(GetOrDefault(KeepNonCJKTextAsHorizontalKey, bool.FalseString), false);

            return new[]
            {
                new TextClipEntry
                {
                    text = BasicText,
                    x = 0,
                    y = 0,
                    fontFamily = fontFamily,
                    fontSize = fontSize,
                    fontStyle = fontStyle,
                    horizontalAlignment = horizontalAlignment,
                    verticalAlignment = verticalAlignment,
                    wrappingWidth = wrappingWidth,
                    applyKerning = applyKerning,
                    lineSpacing = lineSpacing,
                    rotation = rotation,
                    strokeWidth = strokeWidth,
                    strokeR = (ushort)Math.Round(strokeColor.Red * 65535),
                    strokeG = (ushort)Math.Round(strokeColor.Green * 65535),
                    strokeB = (ushort)Math.Round(strokeColor.Blue * 65535),
                    dpi = dpi,
                    UseVerticalLayout = useVerticalLayout,
                    KeepNonCJKTextAsHorizontal = keepNonCJKTextAsHorizontal,
                    r = (ushort)Math.Round(color.Red * 65535),
                    g = (ushort)Math.Round(color.Green * 65535),
                    b = (ushort)Math.Round(color.Blue * 65535),
                    a = (float)color.Alpha
                }
            };
        }

        public PropertyPanelBuilder BuildPropertyPanel()
        {
            var panel = new PropertyPanelBuilder();
            var currentText = GetOrDefault(TextKey, DefaultText);
            var currentFont = GetOrDefault(FontKey, "Arial");
            var fontSize = ParseFloat(GetOrDefault(SizeKey, DefaultFontSize.ToString(CultureInfo.InvariantCulture)), DefaultFontSize);
            var currentFontStyle = ParseFontStyle(GetOrDefault(FontStyleKey, ClipFontStyle.Regular.ToString()), ClipFontStyle.Regular);
            var glyphWarning = new Label
            {
                TextColor = Colors.OrangeRed,
                FontSize = 12,
                IsVisible = false,
                LineBreakMode = LineBreakMode.WordWrap
            };

            void UpdateGlyphWarning()
            {
                var warning = TextServices.GetMissingGlyphWarning(currentFont, currentText, fontSize);
                glyphWarning.Text = warning;
                glyphWarning.IsVisible = !string.IsNullOrWhiteSpace(warning);
            }

            panel.AddCustomChild(
                (c) =>
                {
                    var e = new Editor
                    {
                        MinimumHeightRequest = 150,
                        Background = Colors.Gray,
                        Text = GetOrDefault(TextKey, DefaultText),
                        IsSpellCheckEnabled = true,
                        IsTextPredictionEnabled = true,
                        Placeholder = PPLocalizedResources.TextOption_Content_Placeholder
                    };
                    e.Unfocused += (_, _) =>
                    {
                        currentText = e.Text;
                        c(e.Text);
                        UpdateGlyphWarning();
                    };

                    var stack = new VerticalStackLayout
                    {
                        Spacing = 6,
                        Children = { e, glyphWarning }
                    };
                    UpdateGlyphWarning();
                    return stack;
                },
                TextKey, GetOrDefault(TextKey, DefaultText))
                .AddButton(Localized._Apply, (s, e) => { }); //give user a place to unfocus entry

            var fontOptions = TextServices.LoadedFonts.Keys.OrderBy(c => c).ToArray();
            if (fontOptions.Length > 0)
            {
                currentFont = fontOptions.Contains(currentFont) ? currentFont : fontOptions.First();
                panel.AddPicker(FontKey, PPLocalizedResources.TextOption_Font, fontOptions, currentFont, picker =>
                {
                    picker.TextColor = Colors.Black;
#if iDevices
                    picker.Closed += (s, e) =>
                    {
                        if (picker.SelectedItem is string selectedFont && !string.IsNullOrWhiteSpace(selectedFont))
                        {
                            currentFont = selectedFont;
                            UpdateGlyphWarning();
                        }
                    };
#else
                    picker.SelectedIndexChanged += (s, e) =>
                    {
                        if (picker.SelectedItem is string selectedFont && !string.IsNullOrWhiteSpace(selectedFont))
                        {
                            currentFont = selectedFont;
                            UpdateGlyphWarning();
                        }
                    };
#endif
                });
            }
            else
            {
                panel.AddEntry(FontKey, PPLocalizedResources.TextOption_Font, currentFont, "Arial", entry =>
                {
                    entry.TextChanged += (s, e) =>
                    {
                        currentFont = e.NewTextValue ?? string.Empty;
                        UpdateGlyphWarning();
                    };
                }, EntryUpdateEventCallMode.OnAnyTextChange);
            }

            UpdateGlyphWarning();

            panel.AddSlider(SizeKey, PPLocalizedResources.TextOption_Size, 50, 500, fontSize, eventCallMode: SliderUpdateEventCallMode.OnMouseUp);
            panel.AddPicker(FontStyleKey, PPLocalizedResources.TextOption_Style, new[] { ClipFontStyle.Regular.ToString(), ClipFontStyle.Bold.ToString(), ClipFontStyle.Italic.ToString(), ClipFontStyle.BoldItalic.ToString() }, currentFontStyle.ToString());
            panel.AddCustomChild(PPLocalizedResources.TextOption_Color, invoker => BuildColorPickerField(
                ColorKey,
                GetOrDefault(ColorKey, "#FFFFFF"),
                invoker), ColorKey, GetOrDefault(ColorKey, "#FFFFFF"));
            panel.AddPicker(HorizontalAlignmentKey, PPLocalizedResources.TextOption_HorizonOption, new[] { ClipHorizontalAlignment.Left.ToString(), ClipHorizontalAlignment.Center.ToString(), ClipHorizontalAlignment.Right.ToString() }, GetOrDefault(HorizontalAlignmentKey, ClipHorizontalAlignment.Left.ToString()));
            panel.AddPicker(VerticalAlignmentKey, PPLocalizedResources.TextOption_VerticalOption, new[] { ClipVerticalAlignment.Top.ToString(), ClipVerticalAlignment.Center.ToString(), ClipVerticalAlignment.Bottom.ToString() }, GetOrDefault(VerticalAlignmentKey, ClipVerticalAlignment.Top.ToString()));
            panel.AddEntry(WrappingWidthKey, PPLocalizedResources.TextOption_WrapW, GetOrDefault(WrappingWidthKey, string.Empty), PPLocalizedResources.TextOption_WrapW_Hint);
            panel.AddSwitch(ApplyKerningKey, PPLocalizedResources.TextOption_Kerning, ParseBool(GetOrDefault(ApplyKerningKey, bool.TrueString), true));
            panel.AddEntry(LineSpacingKey, PPLocalizedResources.TextOption_LineSpacing, GetOrDefault(LineSpacingKey, 1f.ToString(CultureInfo.InvariantCulture)), "1.0");
            panel.AddEntry(RotationKey, PPLocalizedResources.TextOption_Rotation, GetOrDefault(RotationKey, 0f.ToString(CultureInfo.InvariantCulture)), "0");
            panel.AddEntry(StrokeWidthKey, PPLocalizedResources.TextOption_Stroke, GetOrDefault(StrokeWidthKey, string.Empty), PPLocalizedResources.TextOption_Stroke_Hint);
            panel.AddCustomChild(PPLocalizedResources.TextOption_Stroke, invoker => BuildColorPickerField(
                StrokeColorKey,
                GetOrDefault(StrokeColorKey, "#000000"),
                invoker), StrokeColorKey, GetOrDefault(StrokeColorKey, "#000000"));
            panel.AddEntry(DpiKey, "DPI", GetOrDefault(DpiKey, string.Empty), string.Empty);
            panel.AddSwitch(UseVerticalLayoutKey, PPLocalizedResources.TextOption_UseVerticalLayout, ParseBool(GetOrDefault(UseVerticalLayoutKey, bool.FalseString), false));
            panel.AddSwitch(KeepNonCJKTextAsHorizontalKey, PPLocalizedResources.TextOption_KeepNonCJKHorizontal, ParseBool(GetOrDefault(KeepNonCJKTextAsHorizontalKey, bool.FalseString), false));
            return panel;
        }

        public (Dictionary<string, string> newParams, int newWidth, int newHeight) HandlePropertyPanelChange(PropertyPanelPropertyChangedEventArgs args)
        {
            switch (args.Id)
            {
                case TextKey:
                    BasicText = args.Value?.ToString() ?? string.Empty;
                    break;
                case FontKey:
                    _parameters[FontKey] = args.Value?.ToString() ?? "Arial";
                    break;
                case SizeKey:
                    if (TryParseFloat(args.Value, out var size))
                    {
                        _parameters[SizeKey] = size.ToString(CultureInfo.InvariantCulture);
                        _parameters[ManualSizeKey] = "true";
                    }
                    break;
                case ColorKey:
                    _parameters[ColorKey] = args.Value?.ToString() ?? "#FFFFFF";
                    break;
                case FontStyleKey:
                    _parameters[FontStyleKey] = ParseFontStyle(args.Value?.ToString(), ClipFontStyle.Regular).ToString();
                    break;
                case HorizontalAlignmentKey:
                    _parameters[HorizontalAlignmentKey] = ParseHorizontalAlignment(args.Value?.ToString(), ClipHorizontalAlignment.Left).ToString();
                    break;
                case VerticalAlignmentKey:
                    _parameters[VerticalAlignmentKey] = ParseVerticalAlignment(args.Value?.ToString(), ClipVerticalAlignment.Top).ToString();
                    break;
                case WrappingWidthKey:
                    _parameters[WrappingWidthKey] = args.Value?.ToString() ?? string.Empty;
                    break;
                case ApplyKerningKey:
                    _parameters[ApplyKerningKey] = ParseBool(args.Value, true).ToString();
                    break;
                case LineSpacingKey:
                    if (TryParseFloat(args.Value, out var lineSpacing))
                    {
                        _parameters[LineSpacingKey] = lineSpacing.ToString(CultureInfo.InvariantCulture);
                    }
                    break;
                case RotationKey:
                    if (TryParseFloat(args.Value, out var rotation))
                    {
                        _parameters[RotationKey] = rotation.ToString(CultureInfo.InvariantCulture);
                    }
                    break;
                case StrokeWidthKey:
                    if (TryParseFloat(args.Value, out var strokeWidth))
                    {
                        _parameters[StrokeWidthKey] = strokeWidth.ToString(CultureInfo.InvariantCulture);
                    }
                    else
                    {
                        _parameters[StrokeWidthKey] = string.Empty;
                    }
                    break;
                case StrokeColorKey:
                    _parameters[StrokeColorKey] = args.Value?.ToString() ?? "#000000";
                    break;
                case DpiKey:
                    if (TryParseFloat(args.Value, out var dpi))
                    {
                        _parameters[DpiKey] = dpi.ToString(CultureInfo.InvariantCulture);
                    }
                    else
                    {
                        _parameters[DpiKey] = string.Empty;
                    }
                    break;
                case UseVerticalLayoutKey:
                    _parameters[UseVerticalLayoutKey] = ParseBool(args.Value, false).ToString();
                    break;
                case KeepNonCJKTextAsHorizontalKey:
                    _parameters[KeepNonCJKTextAsHorizontalKey] = ParseBool(args.Value, false).ToString();
                    break;
            }
            var rect = TextMeasureHelper.MeasureBounds(BuildEntries());
            return (_parameters, Math.Max(1, (int)Math.Ceiling(rect.Width)), Math.Max(1, (int)Math.Ceiling(rect.Height)));
        }

        public Dictionary<string, string> HandleClipResize(bool isInRatio, int TargetX, int TargetY, int TargetWidth, int TargetHeight)
        {
            EnsureDefaults();

            if (TargetWidth <= 0 || TargetHeight <= 0)
            {
                return new Dictionary<string, string>(_parameters);
            }

            var currentRect = GetViewRect(TargetWidth, TargetHeight);
            if (currentRect.TargetWidth <= 0 || currentRect.TargetHeight <= 0)
            {
                return new Dictionary<string, string>(_parameters);
            }

            var currentFontSize = ParseFloat(
                GetOrDefault(SizeKey, DefaultFontSize.ToString(CultureInfo.InvariantCulture)),
                DefaultFontSize);

            var scaleY = (double)TargetHeight / currentRect.TargetHeight;
            if (double.IsNaN(scaleY) || double.IsInfinity(scaleY) || scaleY <= 0)
            {
                return new Dictionary<string, string>(_parameters);
            }

            var updatedSize = (float)(currentFontSize * scaleY);
            if (updatedSize > 0)
            {
                _parameters[SizeKey] = updatedSize.ToString(CultureInfo.InvariantCulture);
            }

            return new Dictionary<string, string>(_parameters);
        }

        public ClipPositionTuple GetViewRect(int canvasWidth, int canvasHeight)
        {
            var entries = BuildEntries();
            if (entries.Length == 0)
            {
                return new ClipPositionTuple(0, 0, Math.Max(1, canvasWidth), Math.Max(1, canvasHeight), false);
            }

            try
            {
                var rect = TextMeasureHelper.MeasureBounds(entries);
                return new ClipPositionTuple(
                    (int)Math.Round(rect.X),
                    (int)Math.Round(rect.Y),
                    Math.Max(1, (int)Math.Ceiling(rect.Width)),
                    Math.Max(1, (int)Math.Ceiling(rect.Height)),
                    false);
            }
            catch
            {
                return new ClipPositionTuple(0, 0, Math.Max(1, canvasWidth), Math.Max(1, canvasHeight), false);
            }
        }

        private void EnsureDefaults()
        {
            if (!_parameters.ContainsKey(TextKey)) _parameters[TextKey] = DefaultText;
            if (!_parameters.ContainsKey(FontKey)) _parameters[FontKey] = "Arial";
            if (!_parameters.ContainsKey(SizeKey)) _parameters[SizeKey] = DefaultFontSize.ToString(CultureInfo.InvariantCulture);
            if (!_parameters.ContainsKey(ColorKey)) _parameters[ColorKey] = "#FFFFFF";
        }

        private static (int width, int height) EstimateEntrySize(TextClipEntry entry)
        {
            try
            {
                var rect = TextMeasureHelper.MeasureBounds([entry]);
                return (Math.Max(1, (int)Math.Round(rect.Width)), Math.Max(1, (int)Math.Round(rect.Height)));
            }
            catch
            {
                var text = entry.text ?? string.Empty;
                var fontSize = entry.fontSize > 0 ? entry.fontSize : 24f;
                var width = Math.Max(1, (int)Math.Round(text.Length * fontSize * 0.6f));
                var height = Math.Max(1, (int)Math.Round(fontSize * 1.2f));
                return (width, height);
            }
        }

        [DebuggerStepThrough()]
        private string GetOrDefault(string key, string fallback)
        {
            return _parameters.TryGetValue(key, out var value) && !string.IsNullOrWhiteSpace(value)
                ? value
                : fallback;
        }

        [DebuggerStepThrough()]
        private static float ParseFloat(string? value, float fallback)
        {
            return float.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out var parsed) ? parsed : fallback;
        }

        private static bool TryParseFloat(object? value, out float result)
        {
            if (value is float f)
            {
                result = f;
                return true;
            }
            if (value is double d)
            {
                result = (float)d;
                return true;
            }
            if (float.TryParse(value?.ToString(), NumberStyles.Float, CultureInfo.InvariantCulture, out var parsed))
            {
                result = parsed;
                return true;
            }

            result = 0f;
            return false;
        }

        private static bool ParseBool(object? value, bool fallback)
        {
            if (value is bool b)
            {
                return b;
            }

            if (bool.TryParse(value?.ToString(), out var parsed))
            {
                return parsed;
            }

            return fallback;
        }

        private static float? ParseNullableFloat(string? value)
        {
            if (string.IsNullOrWhiteSpace(value))
            {
                return null;
            }

            return float.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out var parsed) ? parsed : null;
        }

        private static ClipFontStyle ParseFontStyle(string? value, ClipFontStyle fallback)
        {
            return Enum.TryParse<ClipFontStyle>(value, true, out var parsed) ? parsed : fallback;
        }

        private static ClipHorizontalAlignment ParseHorizontalAlignment(string? value, ClipHorizontalAlignment fallback)
        {
            return Enum.TryParse<ClipHorizontalAlignment>(value, true, out var parsed) ? parsed : fallback;
        }

        private static ClipVerticalAlignment ParseVerticalAlignment(string? value, ClipVerticalAlignment fallback)
        {
            return Enum.TryParse<ClipVerticalAlignment>(value, true, out var parsed) ? parsed : fallback;
        }

        private static Color ParseColorOrFallback(string? value, Color fallback)
        {
            if (string.IsNullOrWhiteSpace(value))
            {
                return fallback;
            }

            try
            {
                return Color.FromArgb(value);
            }
            catch
            {
                return fallback;
            }
        }

        private View BuildColorPickerField(string parameterKey, string defaultHex, Action<object> invoker)
        {
            var currentHex = GetOrDefault(parameterKey, defaultHex);
            var currentColor = ParseColorOrFallback(currentHex, Colors.White);
            var swatch = new Border
            {
                WidthRequest = 24,
                HeightRequest = 24,
                StrokeShape = new RoundRectangle { CornerRadius = 5 },
                StrokeThickness = 1,
                Stroke = Colors.White.WithAlpha(0.2f),
                Background = new SolidColorBrush(currentColor),
                VerticalOptions = LayoutOptions.Center
            };
            var valueLabel = new Label
            {
                Text = currentHex,
                TextColor = Colors.White,
                VerticalOptions = LayoutOptions.Center,
                LineBreakMode = LineBreakMode.TailTruncation
            };

            async Task OpenPickerAsync()
            {
                var selectedColor = currentColor;
                var picker = new ColorPicker
                {
                    SelectedColor = selectedColor
                };

                var pickerPopup = new Popup
                {
                    CanBeDismissedByTappingOutsideOfPopup = true,
                    Content = new Border
                    {
                        StrokeShape = new RoundRectangle { CornerRadius = 12 },
                        Stroke = Colors.White.WithAlpha(0.12f),
                        Background = new SolidColorBrush(Color.FromArgb("#1F2023")),
                        Padding = new Thickness(12),
                        Content = new VerticalStackLayout
                        {
                            Spacing = 10,
                            Children =
                            {
                                picker,
                                new HorizontalStackLayout
                                {
                                    Spacing = 8,
                                    Children =
                                    {
                                        new Button
                                        {
                                            Text = Localized._Cancel,
                                        },
                                        new Button
                                        {
                                            Text = Localized._OK,
                                        }
                                    }
                                }
                            }
                        }
                    }
                };

                if (pickerPopup.Content is Border popupBorder &&
                    popupBorder.Content is VerticalStackLayout popupStack &&
                    popupStack.Children.Count >= 2 &&
                    popupStack.Children[1] is HorizontalStackLayout buttonRow &&
                    buttonRow.Children.Count >= 2 &&
                    buttonRow.Children[0] is Button cancelButton &&
                    buttonRow.Children[1] is Button okButton)
                {
                    void ApplyColor(Color color)
                    {
                        selectedColor = color;
                        currentColor = color;
                        var hex = ToArgbHex(color);
                        _parameters[parameterKey] = hex;
                        swatch.Background = new SolidColorBrush(color);
                        valueLabel.Text = hex;
                        invoker(hex);
                    }

                    picker.SelectedColorChanged += (_, color) => ApplyColor(color);
                    okButton.Clicked += async (_, _) =>
                    {
                        ApplyColor(picker.SelectedColor);
                        await pickerPopup.CloseAsync();
                    };
                    cancelButton.Clicked += async (_, _) => await pickerPopup.CloseAsync();

                    if(Shell.Current.CurrentPage is DraftPage d)
                    {
                        await d.ShowAPopup(picker, null, null, "dialog");
                    }
                }
            }

            var tapSurface = new Border
            {
                StrokeShape = new RoundRectangle { CornerRadius = 8 },
                Stroke = Colors.White.WithAlpha(0.12f),
                Background = new SolidColorBrush(Color.FromArgb("#1AFFFFFF")),
                Padding = new Thickness(10, 8),
                Content = new HorizontalStackLayout
                {
                    Spacing = 10,
                    Children =
                    {
                        swatch,
                        valueLabel
                    }
                }
            };

            tapSurface.GestureRecognizers.Add(new TapGestureRecognizer
            {
                Command = new Command(async () => await OpenPickerAsync())
            });

            return tapSurface;
        }

        private static string ToArgbHex(Color color)
        {
            var a = (int)Math.Round(color.Alpha * 255);
            var r = (int)Math.Round(color.Red * 255);
            var g = (int)Math.Round(color.Green * 255);
            var b = (int)Math.Round(color.Blue * 255);
            return $"#{a:X2}{r:X2}{g:X2}{b:X2}";
        }

    }

    public sealed class TitleTextStyleProvider : BasicTextStyleProvider
    {
        public override string TypeName => "Title";

        protected override string DefaultText => "Title";

        protected override float DefaultFontSize => 140f;
    }
}
