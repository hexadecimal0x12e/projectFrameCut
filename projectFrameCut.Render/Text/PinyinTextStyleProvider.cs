using projectFrameCut.Drawing.Text.Entry;
using projectFrameCut.Drawing.Text.FontHelper;
using projectFrameCut.Drawing.Text.Typology;
using projectFrameCut.Render.ClipsAndTracks.Text;
using projectFrameCut.Render.Plugin;
using projectFrameCut.Render.RenderAPIBase.EffectAndMixture;
using projectFrameCut.Render.RenderAPIBase.Text;
using projectFrameCut.Shared;
using System.Globalization;
using System.Diagnostics;
using Color = projectFrameCut.Render.Text.TextStyleColor;
using Colors = projectFrameCut.Render.Text.TextStyleColors;
using TextAlignment = projectFrameCut.Drawing.Text.Entry.TextAlignment;

namespace projectFrameCut.Render.Text
{
    public class PinyinTextStyleProvider : TextClipStyleProviderBase
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

        private TextStyleParameters? parameterView;
        private TextStyleParameters _parameters => parameterView ??= this.GetParameters();

        public PinyinTextStyleProvider()
        {
            BasicText = DefaultText;
            EnsureDefaults();
        }

        public override string FromPlugin => InternalPluginBase.InternalPluginBaseID;

        public override string TypeName => "Pinyin";

        protected virtual string DefaultText => PluginManager.CurrentLocale switch
        {
            "zh-CN" or "zh-TW" or "文言文" => "拼音",
            "ja-JP" => "漢字",
            _ => "Pronunciation",
        };

        protected virtual float DefaultFontSize => 120f;

        protected virtual float DefaultPinyinFontSizeRatio => 0.4f;

        protected virtual int DefaultSpacing => 8;

        public override string BasicText
        {
            get => _parameters.TryGetValue(TextKey, out var text) ? text : DefaultText;
            set => _parameters[TextKey] = value;
        }

        public override bool AllowFreeRatioResize => LayoutMode switch
        {
            TextClipLayoutMode.FixedSize => false,
            _ => true,
        };

        public override bool IsHorizontalResizable => LayoutMode switch
        {
            TextClipLayoutMode.FixedSize or TextClipLayoutMode.FixedHeight => false,
            _ => true,
        };

        public override bool IsVerticalResizable => LayoutMode switch
        {
            TextClipLayoutMode.FixedSize or TextClipLayoutMode.FixedWidth => false,
            _ => true,
        };

        public override bool CanSnapWhileResizing => LayoutMode switch
        {
            TextClipLayoutMode.FixedSize or TextClipLayoutMode.FixedWidth or TextClipLayoutMode.FixedHeight => false,
            _ => true,
        };

        public override TextClipLayoutMode LayoutMode
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

        private Dictionary<string, EffectArgumentFieldDescriptor>? settableFields;
        public override Dictionary<string, EffectArgumentFieldDescriptor> SettableFields => settableFields ??= CreateFields();

        private Dictionary<string, EffectArgumentFieldDescriptor> CreateFields()
        {
            return new Dictionary<string, EffectArgumentFieldDescriptor>
            {
                [TextKey] = TextStyleFieldHelper.StringField(TextKey, "Text", "Text content to annotate with pronunciation", DefaultText),
                [FontKey] = TextStyleFieldHelper.EnumField(FontKey, "Font Family", "Font used for the text and pronunciation", "HarmonyOS Sans SC Medium", Array.Empty<string>()),
                [SizeKey] = TextStyleFieldHelper.NumericField(SizeKey, "Font Size", "Base character font size in canvas pixels", DefaultFontSize, 1f),
                [ColorKey] = TextStyleFieldHelper.ColorField(ColorKey, "Character Color", "Base character color", "#FFFFFF"),
                [PinyinFontSizeRatioKey] = TextStyleFieldHelper.NumericField(PinyinFontSizeRatioKey, "Pinyin Size Ratio", "Pronunciation size relative to the base characters", DefaultPinyinFontSizeRatio, 0.15f, 0.8f),
                [PinyinColorKey] = TextStyleFieldHelper.ColorField(PinyinColorKey, "Pinyin Color", "Pronunciation text color", "#FFFFFF"),
                [SpacingKey] = TextStyleFieldHelper.IntegerField(SpacingKey, "Character Spacing", "Spacing between character columns in canvas pixels", DefaultSpacing, 0, 40),
                [LayoutModeKey] = TextStyleFieldHelper.EnumField(LayoutModeKey, "Layout Mode", "How text is sized relative to the clip", TextClipLayoutMode.FillClip.ToString(),
                [
                    TextClipLayoutMode.FillClip.ToString(),
                    TextClipLayoutMode.FixedWidth.ToString(),
                    TextClipLayoutMode.FixedHeight.ToString(),
                    TextClipLayoutMode.FixedSize.ToString()
                ])
            };
        }

        public override TextEntry[] BuildEntries()
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
            var isJapanese = text.Any(c => c is >= '\u3040' and <= '\u30ff');
            var lines = text.Split('\n');
            var currentY = 0;

            ushort fillR = (ushort)Math.Round(color.Red * 65535);
            ushort fillG = (ushort)Math.Round(color.Green * 65535);
            ushort fillB = (ushort)Math.Round(color.Blue * 65535);
            Half fillA = color.Alpha;
            ushort pinyinR = (ushort)Math.Round(pinyinColor.Red * 65535);
            ushort pinyinG = (ushort)Math.Round(pinyinColor.Green * 65535);
            ushort pinyinB = (ushort)Math.Round(pinyinColor.Blue * 65535);
            Half pinyinA = pinyinColor.Alpha;

            foreach (var line in lines)
            {
                var currentX = 0;
                foreach (var c in line)
                {
                    var isCJK = IsCJKCharacter(c);
                    if (isCJK)
                    {
                        var rawPinyin = TextStylePronunciation.Get(c.ToString(), isJapanese);
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

        public override void HandleClipResize(bool isInRatio, int TargetX, int TargetY, int TargetWidth, int TargetHeight)
        {
            EnsureDefaults();

            if (TargetWidth <= 0 || TargetHeight <= 0)
                return;

            if (LayoutMode == TextClipLayoutMode.FixedSize)
                return;

            if (LayoutMode == TextClipLayoutMode.FixedWidth)
            {
                _parameters[WrappingWidthKey] = TargetWidth.ToString(CultureInfo.InvariantCulture);
                return;
            }

            var currentRect = GetViewRect(TargetWidth, TargetHeight);
            if (currentRect.TargetWidth <= 0 || currentRect.TargetHeight <= 0)
                return;

            var currentFontSize = ParseFloat(
                GetOrDefault(SizeKey, DefaultFontSize.ToString(CultureInfo.InvariantCulture)),
                DefaultFontSize);

            var scale = (double)TargetHeight / currentRect.TargetHeight;
            if (double.IsNaN(scale) || double.IsInfinity(scale) || scale <= 0)
                return;

            var updatedSize = (float)(currentFontSize * scale);
            if (updatedSize > 0)
                _parameters[SizeKey] = updatedSize.ToString(CultureInfo.InvariantCulture);

        }

        public override ClipPositionTuple GetViewRect(int canvasWidth, int canvasHeight)
        {
            var entries = BuildEntries();
            if (entries.Length == 0)
                return new ClipPositionTuple(0, 0, Math.Max(1, canvasWidth), Math.Max(1, canvasHeight), false);

            try
            {
                var rect = TextStyleRenderHelper.MeasureBounds(entries, canvasWidth, canvasHeight);
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
                var rect = TextStyleRenderHelper.MeasureBounds(entries, 1920f, 1080f);
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

        private static FontFace? ResolveFontFace(string fontFamily) => TextStyleRenderHelper.ResolveFont(fontFamily);

        private static float MeasureTextWidth(FontFace? font, string text, float fontSize)
        {
            if (font is null || string.IsNullOrEmpty(text))
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

        private static float MeasureTextHeight(FontFace? font, string text, float fontSize)
        {
            if (font is null || string.IsNullOrEmpty(text))
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
