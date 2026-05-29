using Microsoft.Maui.Graphics;
using projectFrameCut.ApplicationAPIBase.Text;
using projectFrameCut.ApplicationAPIBase.Views.PropertyPanelBuilders;
using projectFrameCut.ApplicationPluginBase.DynamicPreviewProvider;
using projectFrameCut.Render.Plugin;
using projectFrameCut.Services;
using TinyPinyin;
using projectFrameCut.Shared;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using projectFrameCut.ApplicationAPIBase.Helpers;
using SixLabors.Fonts;
using Font = SixLabors.Fonts.Font;

namespace projectFrameCut.ApplicationPluginBase.Text
{
    public class PinyinTextStyleProvider : ITextClipStyleProvider
    {
        protected const string TextKey = "Text";
        protected const string FontKey = "FontFamily";
        protected const string SizeKey = "FontSize";
        protected const string ColorKey = "Color";
        protected const string PinyinFontSizeRatioKey = "PinyinFontSizeRatio";
        protected const string PinyinColorKey = "PinyinColor";
        protected const string SpacingKey = "CharSpacing";
        public const string ManualSizeKey = "PinyinManualSize";

        private Dictionary<string, string> _parameters = new();

        public PinyinTextStyleProvider()
        {
            EnsureDefaults();
        }

        public string FromPlugin => InternalPluginBase.InternalPluginBaseID;

        public virtual string TypeName => "Pinyin";

        protected virtual string DefaultText => "拼音";

        protected virtual float DefaultFontSize => 64f;

        protected virtual float DefaultPinyinFontSizeRatio => 0.4f;

        protected virtual int DefaultSpacing => 8;

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
            var text = BasicText;
            if (string.IsNullOrWhiteSpace(text))
                return Array.Empty<TextClipEntry>();

            var fontFamily = GetOrDefault(FontKey, "HarmonyOS Sans SC");
            var fontSize = ParseFloat(GetOrDefault(SizeKey, DefaultFontSize.ToString(CultureInfo.InvariantCulture)), DefaultFontSize);
            var pinyinRatio = ParseFloat(GetOrDefault(PinyinFontSizeRatioKey, DefaultPinyinFontSizeRatio.ToString(CultureInfo.InvariantCulture)), DefaultPinyinFontSizeRatio);
            var pinyinFontSize = fontSize * pinyinRatio;
            var charSpacing = ParseInt(GetOrDefault(SpacingKey, DefaultSpacing.ToString()), DefaultSpacing);
            var colorText = GetOrDefault(ColorKey, "#FFFFFF");
            var pinyinColorText = GetOrDefault(PinyinColorKey, colorText);
            var color = ParseColorOrFallback(colorText, Colors.White);
            var pinyinColor = ParseColorOrFallback(pinyinColorText, color);

            var font = ResolveFont(fontFamily, fontSize);
            var pinyinFont = ResolveFont(fontFamily, pinyinFontSize);
            var pinyinBlockHeight = MeasureTextHeight(pinyinFont, "Ag");
            var hasHanCharacters = text.Any(IsHanCharacter);
            var baseLineY = hasHanCharacters
                ? (int)Math.Ceiling(pinyinBlockHeight + Math.Max(8f, pinyinFontSize * 0.18f))
                : 0;
            var entries = new List<TextClipEntry>();
            var currentX = 0;
            var sentenceLanguage = TextHelper.DetectTextLanguage(text);
            foreach (var c in text)
            {
                var isCJK = IsCJKCharacter(c);
                if (isCJK)
                {
                    var pronunciationLanguage = IsHanCharacter(c) && sentenceLanguage != TextLanguage.Chinese && sentenceLanguage != TextLanguage.Japanese
                        ? TextLanguage.Chinese
                        : sentenceLanguage;
                    var rawPinyin = TaskHelper.SyncWait(() => TextServices.GetHowToPronuce(c.ToString(), pronunciationLanguage), cancellationToken: CancellationToken.None);
                    var isKnown = rawPinyin.Length > 0 && rawPinyin != c.ToString();
                    if (isKnown)
                    {
                        var estCharWidth = MeasureTextWidth(font, c.ToString());
                        var estPinyinWidth = MeasureTextWidth(pinyinFont, rawPinyin);
                        var columnWidth = (int)Math.Ceiling(Math.Max(estCharWidth, estPinyinWidth)) + charSpacing;
                        var centerX = currentX + columnWidth / 2;

                        entries.Add(new TextClipEntry
                        {
                            text = rawPinyin,
                            x = centerX,
                            y = 0,
                            fontFamily = fontFamily,
                            fontSize = pinyinFontSize,
                            horizontalAlignment = SixLabors.Fonts.HorizontalAlignment.Center,
                            r = (ushort)Math.Round(pinyinColor.Red * 65535),
                            g = (ushort)Math.Round(pinyinColor.Green * 65535),
                            b = (ushort)Math.Round(pinyinColor.Blue * 65535),
                            a = (float)pinyinColor.Alpha
                        });

                        entries.Add(new TextClipEntry
                        {
                            text = c.ToString(),
                            x = centerX,
                            y = baseLineY,
                            fontFamily = fontFamily,
                            fontSize = fontSize,
                            horizontalAlignment = SixLabors.Fonts.HorizontalAlignment.Center,
                            r = (ushort)Math.Round(color.Red * 65535),
                            g = (ushort)Math.Round(color.Green * 65535),
                            b = (ushort)Math.Round(color.Blue * 65535),
                            a = (float)color.Alpha
                        });

                        currentX += columnWidth;
                    }
                    else
                    {
                        var estWidth = (int)Math.Ceiling(MeasureTextWidth(font, c.ToString())) + charSpacing;
                        entries.Add(new TextClipEntry
                        {
                            text = c.ToString(),
                            x = currentX,
                            y = baseLineY,
                            fontFamily = fontFamily,
                            fontSize = fontSize,
                            r = (ushort)Math.Round(color.Red * 65535),
                            g = (ushort)Math.Round(color.Green * 65535),
                            b = (ushort)Math.Round(color.Blue * 65535),
                            a = (float)color.Alpha
                        });
                        currentX += estWidth;
                    }
                }
                else
                {
                    var estWidth = c == ' '
                        ? (int)Math.Ceiling(fontSize * 0.35f) + charSpacing
                        : (int)Math.Ceiling(MeasureTextWidth(font, c.ToString())) + charSpacing;

                    entries.Add(new TextClipEntry
                    {
                        text = c.ToString(),
                        x = currentX,
                        y = baseLineY,
                        fontFamily = fontFamily,
                        fontSize = fontSize,
                        r = (ushort)Math.Round(color.Red * 65535),
                        g = (ushort)Math.Round(color.Green * 65535),
                        b = (ushort)Math.Round(color.Blue * 65535),
                        a = (float)color.Alpha
                    });
                    currentX += estWidth;
                }
            }

            return entries.ToArray();
        }

        public PropertyPanelBuilder BuildPropertyPanel()
        {
            var panel = new PropertyPanelBuilder();
            var currentText = BasicText;
            var currentFont = GetOrDefault(FontKey, "HarmonyOS Sans SC");
            var fontSize = ParseFloat(GetOrDefault(SizeKey, DefaultFontSize.ToString(CultureInfo.InvariantCulture)), DefaultFontSize);
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

            panel.AddEntry(TextKey, "Text", BasicText, "Text", entry =>
            {
                entry.TextChanged += (s, e) =>
                {
                    currentText = e.NewTextValue ?? string.Empty;
                    UpdateGlyphWarning();
                };
            }, EntryUpdateEventCallMode.OnAnyTextChange);

            var fontOptions = TextServices.LoadedFonts.Keys.OrderBy(c => c).ToArray();
            if (fontOptions.Length > 0)
            {
                currentFont = fontOptions.Contains(currentFont) ? currentFont : fontOptions.First();
                panel.AddPicker(FontKey, "Font", fontOptions, currentFont, picker =>
                {
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
                panel.AddEntry(FontKey, "Font", currentFont, "Arial", entry =>
                {
                    entry.TextChanged += (s, e) =>
                    {
                        currentFont = e.NewTextValue ?? string.Empty;
                        UpdateGlyphWarning();
                    };
                }, EntryUpdateEventCallMode.OnAnyTextChange);
            }

            UpdateGlyphWarning();

            panel.AddCustomChild(glyphWarning);

            panel.AddSlider(SizeKey, "Font Size", 20, 400, fontSize, eventCallMode: SliderUpdateEventCallMode.OnMouseUp);

            var pinyinRatio = ParseFloat(GetOrDefault(PinyinFontSizeRatioKey, DefaultPinyinFontSizeRatio.ToString(CultureInfo.InvariantCulture)), DefaultPinyinFontSizeRatio);
            panel.AddSlider(PinyinFontSizeRatioKey, "Pinyin Size Ratio", 0.15f, 0.8f, pinyinRatio, eventCallMode: SliderUpdateEventCallMode.OnMouseUp);

            var spacing = ParseInt(GetOrDefault(SpacingKey, DefaultSpacing.ToString()), DefaultSpacing);
            panel.AddSlider(SpacingKey, "Char Spacing", 0, 40, spacing, eventCallMode: SliderUpdateEventCallMode.OnMouseUp);

            panel.AddEntry(ColorKey, "Char Color", GetOrDefault(ColorKey, "#FFFFFF"), "#FFFFFF");
            panel.AddEntry(PinyinColorKey, "Pinyin Color", GetOrDefault(PinyinColorKey, ""), "");
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
                case PinyinFontSizeRatioKey:
                    if (TryParseFloat(args.Value, out var ratio))
                    {
                        _parameters[PinyinFontSizeRatioKey] = ratio.ToString(CultureInfo.InvariantCulture);
                    }
                    break;
                case SpacingKey:
                    if (int.TryParse(args.Value?.ToString(), NumberStyles.Any, CultureInfo.InvariantCulture, out var spacing))
                    {
                        _parameters[SpacingKey] = spacing.ToString(CultureInfo.InvariantCulture);
                    }
                    break;
                case ColorKey:
                    _parameters[ColorKey] = args.Value?.ToString() ?? "#FFFFFF";
                    break;
                case PinyinColorKey:
                    _parameters[PinyinColorKey] = args.Value?.ToString() ?? string.Empty;
                    break;
            }

            var rect = MeasureEntries(BuildEntries());
            return (_parameters, Math.Max(1, (int)Math.Ceiling(rect.Width)), Math.Max(1, (int)Math.Ceiling(rect.Height)));
        }

        public Dictionary<string, string> HandleClipResize(bool isInRatio, int TargetX, int TargetY, int TargetWidth, int TargetHeight)
        {
            EnsureDefaults();

            if (TargetWidth <= 0 || TargetHeight <= 0)
                return new Dictionary<string, string>(_parameters);

            var currentRect = GetViewRect(TargetWidth, TargetHeight);
            if (currentRect.TargetWidth <= 0 || currentRect.TargetHeight <= 0)
                return new Dictionary<string, string>(_parameters);

            var currentFontSize = ParseFloat(
                GetOrDefault(SizeKey, DefaultFontSize.ToString(CultureInfo.InvariantCulture)),
                DefaultFontSize);

            var scaleX = (double)TargetWidth / currentRect.TargetWidth;
            var scaleY = (double)TargetHeight / currentRect.TargetHeight;
            if (double.IsNaN(scaleX) || double.IsInfinity(scaleX) || scaleX <= 0)
                return new Dictionary<string, string>(_parameters);

            double scale = scaleX;
            if (!isInRatio && !double.IsNaN(scaleY) && !double.IsInfinity(scaleY) && scaleY > 0)
            {
                if (Math.Abs(scaleX - scaleY) > 0.0001)
                {
                    scale = (scaleX + scaleY) / 2d;
                }
            }

            var updatedSize = (float)(currentFontSize * scale);
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
                return new ClipPositionTuple(0, 0, Math.Max(1, canvasWidth), Math.Max(1, canvasHeight), false);

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
            if (!_parameters.ContainsKey(PinyinFontSizeRatioKey)) _parameters[PinyinFontSizeRatioKey] = DefaultPinyinFontSizeRatio.ToString(CultureInfo.InvariantCulture);
            if (!_parameters.ContainsKey(PinyinColorKey)) _parameters[PinyinColorKey] = "";
            if (!_parameters.ContainsKey(SpacingKey)) _parameters[SpacingKey] = DefaultSpacing.ToString(CultureInfo.InvariantCulture);
        }

        private static (float left, float top, float right, float bottom) GetEntryBounds(TextClipEntry entry)
        {
            var (width, height) = EstimateEntrySize(entry);
            var left = entry.horizontalAlignment switch
            {
                SixLabors.Fonts.HorizontalAlignment.Center => entry.x - width / 2f,
                SixLabors.Fonts.HorizontalAlignment.Right => entry.x - width,
                _ => entry.x
            };

            var top = entry.verticalAlignment switch
            {
                SixLabors.Fonts.VerticalAlignment.Center => entry.y - height / 2f,
                SixLabors.Fonts.VerticalAlignment.Bottom => entry.y - height,
                _ => entry.y
            };

            return (left, top, left + width, top + height);
        }

        private static (int width, int height) EstimateEntrySize(TextClipEntry entry)
        {
            var text = entry.text ?? string.Empty;
            var fontSize = entry.fontSize > 0 ? entry.fontSize : 24f;
            var font = ResolveFont(entry.fontFamily ?? "Arial", fontSize);
            var rect = TextMeasurer.MeasureBounds(text, new TextOptions(font));
            var width = Math.Max(1, (int)Math.Ceiling(rect.Width));
            var height = Math.Max(1, (int)Math.Ceiling(rect.Height));
            return (width, height);
        }

        private static (float Width, float Height) MeasureEntries(TextClipEntry[] entries)
        {
            if (entries.Length == 0)
            {
                return (1f, 1f);
            }

            try
            {
                var rect = TextMeasureHelper.MeasureBounds(entries);
                return (Math.Max(1f, (float)Math.Ceiling(rect.Width)) + 15f, Math.Max(1f, (float)Math.Ceiling(rect.Height)) + 15f);
            }
            catch
            {
                return (1f, 1f);
            }
        }

        private static bool IsCJKCharacter(char c)
        {
            return c >= 0x4E00 && c <= 0x9FFF || c >= 0x3400 && c <= 0x4DBF
                || c >= 0x2F800 && c <= 0x2FA1F || c >= 0x3000 && c <= 0x303F
                || c >= 0xFF00 && c <= 0xFFEF;
        }

        private static bool IsHanCharacter(char c)
        {
            return c >= 0x4E00 && c <= 0x9FFF
                || c >= 0x3400 && c <= 0x4DBF
                || c >= 0x2F800 && c <= 0x2FA1F;
        }

        private string GetOrDefault(string key, string fallback)
        {
            return _parameters.TryGetValue(key, out var value) && !string.IsNullOrWhiteSpace(value)
                ? value
                : fallback;
        }

        private static float ParseFloat(string? value, float fallback)
        {
            return float.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out var parsed) ? parsed : fallback;
        }

        private static int ParseInt(string? value, int fallback)
        {
            return int.TryParse(value, NumberStyles.Any, CultureInfo.InvariantCulture, out var parsed) ? parsed : fallback;
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

        private static Font ResolveFont(string fontFamily, float fontSize)
        {
            if (TextServices.LoadedFonts.TryGetValue(fontFamily, out var fontItem) && TextServices.TryResolveFontFamily(fontItem, out var family))
            {
                return family.CreateFont(fontSize);
            }

            if (TextServices.LoadedFonts.TryGetValue("HarmonyOS_Sans_SC_Regular", out var fallbackItem) && TextServices.TryResolveFontFamily(fallbackItem, out var fallbackFamily))
            {
                return fallbackFamily.CreateFont(fontSize);
            }

            var systemFamily = SystemFonts.Families.FirstOrDefault();
            if (systemFamily != default)
            {
                return systemFamily.CreateFont(fontSize);
            }

            return SystemFonts.CreateFont("Arial", fontSize);
        }

        private static float MeasureTextWidth(Font font, string text)
        {
            if (string.IsNullOrEmpty(text))
            {
                return Math.Max(1f, font.Size);
            }

            var rect = TextMeasurer.MeasureBounds(text, new TextOptions(font));
            return Math.Max(1f, rect.Width);
        }

        private static float MeasureTextHeight(Font font, string text)
        {
            if (string.IsNullOrEmpty(text))
            {
                return Math.Max(1f, font.Size);
            }

            var rect = TextMeasurer.MeasureBounds(text, new TextOptions(font));
            return Math.Max(1f, rect.Height);
        }

        private static Color ParseColorOrFallback(string? value, Color fallback)
        {
            if (string.IsNullOrWhiteSpace(value))
                return fallback;

            try
            {
                return Color.FromArgb(value);
            }
            catch
            {
                return fallback;
            }
        }

    }
}
