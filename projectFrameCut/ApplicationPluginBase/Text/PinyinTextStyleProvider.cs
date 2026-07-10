using Microsoft.Maui.Graphics;
using projectFrameCut.ApplicationAPIBase.Effect;
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
using projectFrameCut.Drawing.Text.Entry;
using projectFrameCut.Drawing.Text.FontHelper;
using projectFrameCut.Drawing.Text.Typology;
using projectFrameCut.Render.ClipsAndTracks.Text;
using TextAlignment = projectFrameCut.Drawing.Text.Entry.TextAlignment;

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
        public const string LayoutModeKey = "LayoutMode";
        protected const string WrappingWidthKey = "WrappingWidth";

        private Dictionary<string, string> _parameters = new();

        public PinyinTextStyleProvider()
        {
            BasicText = DefaultText;
            EnsureDefaults();
        }

        public string FromPlugin => InternalPluginBase.InternalPluginBaseID;

        public virtual string TypeName => "Pinyin";

        protected virtual string DefaultText => "拼音";

        protected virtual float DefaultFontSize => 120f;

        protected virtual float DefaultPinyinFontSizeRatio => 0.4f;

        protected virtual int DefaultSpacing => 8;

        public string BasicText { get; set; }

        public Dictionary<string, string> Parameters
        {
            get => _parameters;
            set
            {
                _parameters = value ?? new Dictionary<string, string>();
                EnsureDefaults();
                if (_parameters.TryGetValue(TextKey, out var t) && !string.IsNullOrWhiteSpace(t))
                    BasicText = t;
            }
        }

        public bool AllowFreeRatioResize => LayoutMode switch
        {
            TextClipLayoutMode.FixedSize => false,
            _ => true,
        };

        public bool IsHorizontalResizable => LayoutMode switch
        {
            TextClipLayoutMode.FixedSize or TextClipLayoutMode.FixedHeight => false,
            _ => true,
        };

        public bool IsVerticalResizable => LayoutMode switch
        {
            TextClipLayoutMode.FixedSize or TextClipLayoutMode.FixedWidth => false,
            _ => true,
        };

        public bool CanSnapWhileResizing => LayoutMode switch
        {
            TextClipLayoutMode.FixedSize or TextClipLayoutMode.FixedWidth or TextClipLayoutMode.FixedHeight => false,
            _ => true,
        };

        public TextClipLayoutMode LayoutMode
        {
            get => ParseLayoutMode(GetOrDefault(LayoutModeKey, "FillClip"), TextClipLayoutMode.FillClip);
            set
            {
                var oldMode = LayoutMode;
                _parameters[LayoutModeKey] = value.ToString();

                if (oldMode != value && value == TextClipLayoutMode.FixedWidth)
                {
                    if (!_parameters.ContainsKey(WrappingWidthKey) || string.IsNullOrWhiteSpace(_parameters[WrappingWidthKey]))
                    {
                        var measured = MeasureEntries(BuildEntries());
                        _parameters[WrappingWidthKey] = Math.Max(100, (int)Math.Ceiling(measured.Width)).ToString(CultureInfo.InvariantCulture);
                    }
                }
            }
        }

        public Dictionary<string, EffectBundleSettableFields> SettableFields
        {
            get
            {
                var fontNames = TextStyleProviderSettableFieldHelper.GetAvailableFontNames();
                return new Dictionary<string, EffectBundleSettableFields>
                {
                    [TextKey] = TextStyleProviderSettableFieldHelper.StringField(TextKey, "Text", "Text content to annotate with pronunciation", DefaultText),
                    [FontKey] = TextStyleProviderSettableFieldHelper.EnumField(FontKey, "Font Family", "Font used for the text and pronunciation", "HarmonyOS Sans SC Medium", fontNames),
                    [SizeKey] = TextStyleProviderSettableFieldHelper.NumericField(SizeKey, "Font Size", "Base character font size in canvas pixels", DefaultFontSize, 20f, 400f),
                    [ColorKey] = TextStyleProviderSettableFieldHelper.ColorField(ColorKey, "Character Color", "Base character color", "#FFFFFF"),
                    [PinyinFontSizeRatioKey] = TextStyleProviderSettableFieldHelper.NumericField(PinyinFontSizeRatioKey, "Pinyin Size Ratio", "Pronunciation size relative to the base characters", DefaultPinyinFontSizeRatio, 0.15f, 0.8f),
                    [PinyinColorKey] = TextStyleProviderSettableFieldHelper.ColorField(PinyinColorKey, "Pinyin Color", "Pronunciation text color", "#FFFFFF"),
                    [SpacingKey] = TextStyleProviderSettableFieldHelper.IntegerField(SpacingKey, "Character Spacing", "Spacing between character columns in canvas pixels", DefaultSpacing, 0, 40),
                    [LayoutModeKey] = TextStyleProviderSettableFieldHelper.EnumField(LayoutModeKey, "Layout Mode", "How text is sized relative to the clip", TextClipLayoutMode.FillClip.ToString(),
                    [
                        TextClipLayoutMode.FillClip.ToString(),
                        TextClipLayoutMode.FixedWidth.ToString(),
                        TextClipLayoutMode.FixedSize.ToString()
                    ])
                };
            }
        }

        public bool HandleSettableFieldsChange(EffectBundleSettableFields field, object value, out string feedback)
        {
            if (field is null || !SettableFields.TryGetValue(field.Id, out var canonicalField))
            {
                feedback = field is null
                    ? "Field definition is null."
                    : $"Unknown settable field '{field.Id}' for text style '{TypeName}'.";
                return false;
            }

            if (!TextStyleProviderSettableFieldHelper.TryNormalizeValue(
                canonicalField, value, out var normalizedValue, out feedback))
            {
                return false;
            }

            if (canonicalField.Id == LayoutModeKey)
            {
                LayoutMode = Enum.Parse<TextClipLayoutMode>((string)normalizedValue);
            }
            else
            {
                HandlePropertyPanelChange(new PropertyPanelPropertyChangedEventArgs(
                    canonicalField.Id, normalizedValue, null));
            }

            feedback = "";
            return true;
        }

        public TextEntry[] BuildEntries()
        {
            var text = BasicText;
            if (string.IsNullOrWhiteSpace(text))
                return Array.Empty<TextEntry>();

            var fontFamily = GetOrDefault(FontKey, "HarmonyOS Sans SC");
            var fontSize = ParseFloat(GetOrDefault(SizeKey, DefaultFontSize.ToString(CultureInfo.InvariantCulture)), DefaultFontSize);
            var pinyinRatio = ParseFloat(GetOrDefault(PinyinFontSizeRatioKey, DefaultPinyinFontSizeRatio.ToString(CultureInfo.InvariantCulture)), DefaultPinyinFontSizeRatio);
            var pinyinFontSize = fontSize * pinyinRatio;
            var charSpacing = ParseInt(GetOrDefault(SpacingKey, DefaultSpacing.ToString()), DefaultSpacing);
            var colorText = GetOrDefault(ColorKey, "#FFFFFF");
            var pinyinColorText = GetOrDefault(PinyinColorKey, colorText);
            var color = ParseColorOrFallback(colorText, Colors.White);
            var pinyinColor = ParseColorOrFallback(pinyinColorText, color);

            var font = ResolveFontFace(fontFamily);
            var pinyinFont = ResolveFontFace(fontFamily);
            var pinyinBlockHeight = pinyinFont is not null ? MeasureTextHeight(pinyinFont, "Ag", pinyinFontSize) : pinyinFontSize;
            var hasHanCharacters = text.Any(IsHanCharacter);
            var baseLineY = hasHanCharacters
                ? (int)Math.Ceiling(pinyinBlockHeight + Math.Max(8f, pinyinFontSize * 0.18f))
                : 0;

            if (LayoutMode == TextClipLayoutMode.FixedWidth)
            {
                var ww = ParseNullableFloat(GetOrDefault(WrappingWidthKey, string.Empty));
                if (ww.HasValue && ww.Value > 0 && font is not null)
                {
                    var breakEntry = new TextEntry
                    {
                        Text = text,
                        FontName = fontFamily,
                        FontSize = fontSize,
                    };
                    // Use any square canvas — for square canvases UnitLength
                    // equals the canvas dim, so divider cancels naturally and
                    // BreakLine receives the wrap width in the engine's
                    // height-normalised units.
                    var ctx = TextLayoutContext.FromCanvas(1000f, 1000f);
                    var normalized = TextLayoutPipeline.ToEngineSpace(breakEntry, ctx);
                    text = LineBreakHandler.BreakLine(normalized, font, ww.Value / 1000f);
                }
            }

            var lineHeight = baseLineY > 0
                ? baseLineY + (int)Math.Ceiling(fontSize * 1.2f)
                : (int)Math.Ceiling(fontSize * 1.2f);

            var entries = new List<TextEntry>();
            var sentenceLanguage = TextHelper.DetectTextLanguage(text);
            var lines = text.Split('\n');
            var currentY = 0;

            ushort fillR = (ushort)Math.Round(color.Red * 65535);
            ushort fillG = (ushort)Math.Round(color.Green * 65535);
            ushort fillB = (ushort)Math.Round(color.Blue * 65535);
            float fillA = (float)color.Alpha;
            ushort pinyinR = (ushort)Math.Round(pinyinColor.Red * 65535);
            ushort pinyinG = (ushort)Math.Round(pinyinColor.Green * 65535);
            ushort pinyinB = (ushort)Math.Round(pinyinColor.Blue * 65535);
            float pinyinA = (float)pinyinColor.Alpha;

            foreach (var line in lines)
            {
                var currentX = 0;
                foreach (var c in line)
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
                            var estCharWidth = MeasureTextWidth(font!, c.ToString(), fontSize);
                            var estPinyinWidth = MeasureTextWidth(pinyinFont!, rawPinyin, pinyinFontSize);
                            var columnWidth = (int)Math.Ceiling(Math.Max(estCharWidth, estPinyinWidth)) + charSpacing;
                            var centerX = currentX + columnWidth / 2;

                            entries.Add(new TextEntry
                            {
                                Text = rawPinyin,
                                X = centerX,
                                Y = currentY,
                                FontName = fontFamily,
                                FontSize = pinyinFontSize,
                                Alignment = TextAlignment.Center,
                                FillR = pinyinR,
                                FillG = pinyinG,
                                FillB = pinyinB,
                                FillA = pinyinA,
                            });

                            entries.Add(new TextEntry
                            {
                                Text = c.ToString(),
                                X = centerX,
                                Y = currentY + baseLineY,
                                FontName = fontFamily,
                                FontSize = fontSize,
                                Alignment = TextAlignment.Center,
                                FillR = fillR,
                                FillG = fillG,
                                FillB = fillB,
                                FillA = fillA,
                            });

                            currentX += columnWidth;
                        }
                        else
                        {
                            var estWidth = (int)Math.Ceiling(MeasureTextWidth(font!, c.ToString(), fontSize)) + charSpacing;
                            entries.Add(new TextEntry
                            {
                                Text = c.ToString(),
                                X = currentX,
                                Y = currentY + baseLineY,
                                FontName = fontFamily,
                                FontSize = fontSize,
                                FillR = fillR,
                                FillG = fillG,
                                FillB = fillB,
                                FillA = fillA,
                            });
                            currentX += estWidth;
                        }
                    }
                    else
                    {
                        var estWidth = c == ' '
                            ? (int)Math.Ceiling(fontSize * 0.35f) + charSpacing
                            : (int)Math.Ceiling(MeasureTextWidth(font!, c.ToString(), fontSize)) + charSpacing;

                        entries.Add(new TextEntry
                        {
                            Text = c.ToString(),
                            X = currentX,
                            Y = currentY + baseLineY,
                            FontName = fontFamily,
                            FontSize = fontSize,
                            FillR = fillR,
                            FillG = fillG,
                            FillB = fillB,
                            FillA = fillA,
                        });
                        currentX += estWidth;
                    }
                }
                currentY += lineHeight;
            }

            return entries.ToArray();
        }

        Label glyphWarning = new Label
        {
            TextColor = Colors.OrangeRed,
            FontSize = 12,
            IsVisible = false,
            LineBreakMode = LineBreakMode.WordWrap
        };

        void UpdateGlyphWarning()
        {
            var warning = TextServices.GetMissingGlyphWarning(GetOrDefault(FontKey, "HarmonyOS Sans SC Medium"), BasicText, ParseFloat(GetOrDefault(SizeKey, DefaultFontSize.ToString(CultureInfo.InvariantCulture)), DefaultFontSize));
            glyphWarning.Text = warning;
            glyphWarning.IsVisible = !string.IsNullOrWhiteSpace(warning);
        }

        public PropertyPanelBuilder BuildPropertyPanel()
        {
            var panel = new PropertyPanelBuilder();
            var fontSize = ParseFloat(GetOrDefault(SizeKey, DefaultFontSize.ToString(CultureInfo.InvariantCulture)), DefaultFontSize);

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
                    _parameters[TextKey] = BasicText;
                    UpdateGlyphWarning();
                    break;
                case FontKey:
                    _parameters[FontKey] = args.Value?.ToString() ?? "HarmonyOS Sans SC Medium";
                    UpdateGlyphWarning();
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
                        _parameters[PinyinFontSizeRatioKey] = ratio.ToString(CultureInfo.InvariantCulture);
                    break;
                case SpacingKey:
                    if (int.TryParse(args.Value?.ToString(), NumberStyles.Any, CultureInfo.InvariantCulture, out var spacing))
                        _parameters[SpacingKey] = spacing.ToString(CultureInfo.InvariantCulture);
                    break;
                case ColorKey:
                    _parameters[ColorKey] = args.Value?.ToString() ?? "#FFFFFF";
                    break;
                case PinyinColorKey:
                    _parameters[PinyinColorKey] = args.Value?.ToString() ?? string.Empty;
                    break;
            }
            var rect = MeasureEntries(BuildEntries());
            var measuredW = Math.Max(1, (int)Math.Ceiling(rect.Width));
            var measuredH = Math.Max(1, (int)Math.Ceiling(rect.Height));

            if (LayoutMode == TextClipLayoutMode.FixedWidth)
            {
                var ww = ParseNullableFloat(GetOrDefault(WrappingWidthKey, string.Empty));
                if (ww.HasValue && ww.Value > 0)
                    return (_parameters, (int)Math.Ceiling(ww.Value), measuredH);
                return (_parameters, measuredW, measuredH);
            }

            return (_parameters, measuredW, measuredH);
        }

        public Dictionary<string, string> HandleClipResize(bool isInRatio, int TargetX, int TargetY, int TargetWidth, int TargetHeight)
        {
            EnsureDefaults();

            if (TargetWidth <= 0 || TargetHeight <= 0)
                return new Dictionary<string, string>(_parameters);

            if (LayoutMode == TextClipLayoutMode.FixedSize)
                return new Dictionary<string, string>(_parameters);

            if (LayoutMode == TextClipLayoutMode.FixedWidth)
            {
                _parameters[WrappingWidthKey] = TargetWidth.ToString(CultureInfo.InvariantCulture);
                return new Dictionary<string, string>(_parameters);
            }

            var currentRect = GetViewRect(TargetWidth, TargetHeight);
            if (currentRect.TargetWidth <= 0 || currentRect.TargetHeight <= 0)
                return new Dictionary<string, string>(_parameters);

            var currentFontSize = ParseFloat(
                GetOrDefault(SizeKey, DefaultFontSize.ToString(CultureInfo.InvariantCulture)),
                DefaultFontSize);

            var scale = (double)TargetHeight / currentRect.TargetHeight;
            if (double.IsNaN(scale) || double.IsInfinity(scale) || scale <= 0)
                return new Dictionary<string, string>(_parameters);

            var updatedSize = (float)(currentFontSize * scale);
            if (updatedSize > 0)
                _parameters[SizeKey] = updatedSize.ToString(CultureInfo.InvariantCulture);

            return new Dictionary<string, string>(_parameters);
        }

        public ClipPositionTuple GetViewRect(int canvasWidth, int canvasHeight)
        {
            var entries = BuildEntries();
            if (entries.Length == 0)
                return new ClipPositionTuple(0, 0, Math.Max(1, canvasWidth), Math.Max(1, canvasHeight), false);

            try
            {
                var rect = TextMeasureHelper.MeasureBounds(entries, canvasWidth, canvasHeight);
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
            if (!_parameters.ContainsKey(FontKey)) _parameters[FontKey] = "HarmonyOS Sans SC Medium";
            if (!_parameters.ContainsKey(SizeKey)) _parameters[SizeKey] = DefaultFontSize.ToString(CultureInfo.InvariantCulture);
            if (!_parameters.ContainsKey(ColorKey)) _parameters[ColorKey] = "#FFFFFF";
            if (!_parameters.ContainsKey(PinyinFontSizeRatioKey)) _parameters[PinyinFontSizeRatioKey] = DefaultPinyinFontSizeRatio.ToString(CultureInfo.InvariantCulture);
            if (!_parameters.ContainsKey(PinyinColorKey)) _parameters[PinyinColorKey] = "";
            if (!_parameters.ContainsKey(SpacingKey)) _parameters[SpacingKey] = DefaultSpacing.ToString(CultureInfo.InvariantCulture);
            if (!_parameters.ContainsKey(LayoutModeKey)) _parameters[LayoutModeKey] = "FillClip";
            if (!_parameters.ContainsKey(WrappingWidthKey)) _parameters[WrappingWidthKey] = string.Empty;
        }

        private static (float Width, float Height) MeasureEntries(TextEntry[] entries)
        {
            if (entries.Length == 0)
                return (1f, 1f);

            try
            {
                var rect = TextMeasureHelper.MeasureBounds(entries, 1920f, 1080f);
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

        private static TextClipLayoutMode ParseLayoutMode(string? value, TextClipLayoutMode fallback)
        {
            return Enum.TryParse<TextClipLayoutMode>(value, true, out var parsed) ? parsed : fallback;
        }

        private static float ParseFloat(string? value, float fallback)
        {
            return float.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out var parsed) ? parsed : fallback;
        }

        private static int ParseInt(string? value, int fallback)
        {
            return int.TryParse(value, NumberStyles.Any, CultureInfo.InvariantCulture, out var parsed) ? parsed : fallback;
        }

        private static float? ParseNullableFloat(string? value)
        {
            if (string.IsNullOrWhiteSpace(value)) return null;
            return float.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out var parsed) ? parsed : null;
        }

        private static bool TryParseFloat(object? value, out float result)
        {
            if (value is float f) { result = f; return true; }
            if (value is double d) { result = (float)d; return true; }
            if (float.TryParse(value?.ToString(), NumberStyles.Float, CultureInfo.InvariantCulture, out var parsed))
            { result = parsed; return true; }
            result = 0f;
            return false;
        }

        private static FontFace? ResolveFontFace(string fontFamily)
        {
            if (!string.IsNullOrWhiteSpace(fontFamily))
            {
                if (TextServices.LoadedFonts.TryGetValue(fontFamily, out var fontItem) && TextServices.TryResolveFontFamily(fontItem, out var face))
                    return face;
                TextClipFontRegistry.TryGetFont(fontFamily, out var registryFont);
                if (registryFont is not null) return registryFont;
            }
            var fallbackKey = TextClipFontRegistry.FallbackFamilyName;
            if (fallbackKey is not null)
            {
                if (TextServices.LoadedFonts.TryGetValue(fallbackKey, out var fallbackItem) && TextServices.TryResolveFontFamily(fallbackItem, out var fallbackFace))
                    return fallbackFace;
                TextClipFontRegistry.TryGetFont(fallbackKey, out var fallbackRegistryFont);
                if (fallbackRegistryFont is not null) return fallbackRegistryFont;
            }
            return null;
        }

        private static float MeasureTextWidth(FontFace font, string text, float fontSize)
        {
            if (string.IsNullOrEmpty(text))
                return Math.Max(1f, fontSize);
            var entry = new TextEntry
            {
                Text = text,
                FontName = font.FamilyName,
                FontSize = fontSize,
                LineSpacing = 0f,
            };
            var ctx = TextLayoutContext.FromCanvas(100f, 100f);
            var normalized = TextLayoutPipeline.ToEngineSpace(entry, ctx);
            var engine = new NormalTypesettingEngine();
            var (width, _) = engine.Measure(normalized, font);
            return Math.Max(1f, width * 100f);
        }

        private static float MeasureTextHeight(FontFace font, string text, float fontSize)
        {
            if (string.IsNullOrEmpty(text))
                return Math.Max(1f, fontSize);
            var entry = new TextEntry
            {
                Text = text,
                FontName = font.FamilyName,
                FontSize = fontSize,
                LineSpacing = 0f,
            };
            var ctx = TextLayoutContext.FromCanvas(100f, 100f);
            var normalized = TextLayoutPipeline.ToEngineSpace(entry, ctx);
            var engine = new NormalTypesettingEngine();
            var (_, height) = engine.Measure(normalized, font);
            return Math.Max(1f, height * 100f);
        }

        private static Color ParseColorOrFallback(string? value, Color fallback)
        {
            if (string.IsNullOrWhiteSpace(value)) return fallback;
            try { return Color.FromArgb(value); }
            catch { return fallback; }
        }
    }
}
