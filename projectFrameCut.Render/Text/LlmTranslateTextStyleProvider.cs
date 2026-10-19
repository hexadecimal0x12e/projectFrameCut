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
    public class LlmTranslateTextStyleProvider : TextClipStyleProviderBase
    {
        protected const string TextKey = "Text";
        protected const string FontKey = "FontFamily";
        protected const string SizeKey = "FontSize";
        protected const string ColorKey = "Color";
        protected const string TranslationTextKey = "TranslationText";
        protected const string TranslationTargetLanguageKey = "TranslationTargetLanguage";
        protected const string TranslationFontSizeRatioKey = "TranslationFontSizeRatio";
        protected const string TranslationColorKey = "TranslationColor";
        protected const string TranslationLineSpacingKey = "TranslationLineSpacing";
        protected const string TranslationAutoGenerateKey = "TranslationAutoGenerate";
        protected const string TranslationSourceCacheKey = "TranslationSourceCache";
        protected const string GenerateTranslationButtonKey = "GenerateTranslation";
        public const string ManualSizeKey = "LlmTranslateManualSize";
        public const string LayoutModeKey = "LayoutMode";
        protected const string WrappingWidthKey = "WrappingWidth";

        private TextStyleParameters? parameterView;
        private TextStyleParameters _parameters => parameterView ??= this.GetParameters();

        public LlmTranslateTextStyleProvider()
        {
            BasicText = DefaultText;
            EnsureDefaults();
        }

        public override string FromPlugin => InternalPluginBase.InternalPluginBaseID;

        public override string TypeName => "LlmTranslate";

        protected virtual string DefaultText => "Hello world";

        protected virtual float DefaultFontSize => 110f;

        protected virtual float DefaultTranslationSizeRatio => 0.62f;

        protected virtual int DefaultLineSpacing => 12;

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
                [TextKey] = TextStyleFieldHelper.StringField(TextKey, "Source Text", "Source text to display and translate", DefaultText),
                [FontKey] = TextStyleFieldHelper.EnumField(FontKey, "Font Family", "Font used for source and translated text", "HarmonyOS Sans SC Medium", Array.Empty<string>()),
                [SizeKey] = TextStyleFieldHelper.NumericField(SizeKey, "Font Size", "Source text font size in canvas pixels", DefaultFontSize, 1f),
                [ColorKey] = TextStyleFieldHelper.ColorField(ColorKey, "Source Color", "Source text color", "#FFFFFF"),
                [TranslationTextKey] = TextStyleFieldHelper.StringField(TranslationTextKey, "Translation", "Translated text", ""),
                [TranslationFontSizeRatioKey] = TextStyleFieldHelper.NumericField(TranslationFontSizeRatioKey, "Translation Size Ratio", "Translated text size relative to source text", DefaultTranslationSizeRatio, 0.3f, 1.2f),
                [TranslationColorKey] = TextStyleFieldHelper.ColorField(TranslationColorKey, "Translation Color", "Translated text color", "#CFCFCF"),
                [TranslationLineSpacingKey] = TextStyleFieldHelper.IntegerField(TranslationLineSpacingKey, "Translation Line Spacing", "Space between source and translated text in canvas pixels", DefaultLineSpacing, 0, 80),
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
            var sourceText = BasicText;
            if (string.IsNullOrWhiteSpace(sourceText))
                return Array.Empty<TextEntry>();

            var fontFamily = GetOrDefault(FontKey, "HarmonyOS Sans SC Medium");
            var fontSize = ParseFloat(GetOrDefault(SizeKey, DefaultFontSize.ToString(CultureInfo.InvariantCulture)), DefaultFontSize);
            var color = ParseColorOrFallback(GetOrDefault(ColorKey, "#FFFFFF"), Colors.White);
            var translationRatio = ParseFloat(GetOrDefault(TranslationFontSizeRatioKey, DefaultTranslationSizeRatio.ToString(CultureInfo.InvariantCulture)), DefaultTranslationSizeRatio);
            var translationFontSize = Math.Max(12f, fontSize * translationRatio);
            var translationColor = ParseColorOrFallback(GetOrDefault(TranslationColorKey, "#CFCFCF"), Colors.LightGray);
            var lineSpacing = ParseInt(GetOrDefault(TranslationLineSpacingKey, DefaultLineSpacing.ToString(CultureInfo.InvariantCulture)), DefaultLineSpacing);
            var translatedText = GetOrDefault(TranslationTextKey, string.Empty);

            float? wrappingWidth = null;

            if (LayoutMode == TextClipLayoutMode.FixedWidth)
            {
                var ww = ParseNullableFloat(GetOrDefault(WrappingWidthKey, string.Empty));
                if (ww.HasValue && ww.Value > 0)
                {
                    var breakFont = ResolveFontFace(fontFamily);
                    if (breakFont is not null)
                    {
                        var ctx = TextLayoutContext.FromCanvas(1000f, 1000f);
                        var sourceBreakEntry = new TextEntry
                        {
                            Text = sourceText,
                            FontName = fontFamily,
                            FontSize = fontSize,
                        };
                        var normalized = TextLayoutPipeline.ToEngineSpace(sourceBreakEntry, ctx);
                        sourceText = LineBreakHandler.BreakLine(normalized, breakFont, ww.Value / 1000f);

                        if (!string.IsNullOrWhiteSpace(translatedText))
                        {
                            var transBreakEntry = new TextEntry
                            {
                                Text = translatedText,
                                FontName = fontFamily,
                                FontSize = translationFontSize,
                            };
                            var transNormalized = TextLayoutPipeline.ToEngineSpace(transBreakEntry, ctx);
                            translatedText = LineBreakHandler.BreakLine(transNormalized, breakFont, ww.Value / 1000f);
                        }
                    }
                    else
                    {
                        wrappingWidth = ww;
                    }
                }
            }

            ushort fillR = (ushort)Math.Round(color.Red * 65535);
            ushort fillG = (ushort)Math.Round(color.Green * 65535);
            ushort fillB = (ushort)Math.Round(color.Blue * 65535);
            float fillA = (float)color.Alpha;
            ushort transR = (ushort)Math.Round(translationColor.Red * 65535);
            ushort transG = (ushort)Math.Round(translationColor.Green * 65535);
            ushort transB = (ushort)Math.Round(translationColor.Blue * 65535);
            float transA = (float)translationColor.Alpha;

            var sourceEntry = new TextEntry
            {
                Text = sourceText,
                X = 0,
                Y = 0,
                FontName = fontFamily,
                FontSize = fontSize,
                FillR = fillR,
                FillG = fillG,
                FillB = fillB,
                FillA = fillA,
            };
            sourceEntry.SetWrappingWidth(wrappingWidth);

            if (string.IsNullOrWhiteSpace(translatedText))
                return [sourceEntry];

            var sourceHeight = MeasureTextHeight(ResolveFontFace(fontFamily)!, sourceText, fontSize);
            var translationY = (int)Math.Ceiling(sourceHeight + Math.Max(0, lineSpacing));
            var translationEntry = new TextEntry
            {
                Text = translatedText,
                X = 0,
                Y = translationY,
                FontName = fontFamily,
                FontSize = translationFontSize,
                FillR = transR,
                FillG = transG,
                FillB = transB,
                FillA = transA,
            };
            translationEntry.SetWrappingWidth(wrappingWidth);

            return [sourceEntry, translationEntry];
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

            var currentFontSize = ParseFloat(GetOrDefault(SizeKey, DefaultFontSize.ToString(CultureInfo.InvariantCulture)), DefaultFontSize);

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
            if (!_parameters.ContainsKey(TranslationTextKey)) _parameters[TranslationTextKey] = string.Empty;
            if (!_parameters.ContainsKey(TranslationTargetLanguageKey)) _parameters[TranslationTargetLanguageKey] = "zh-CN";
            if (!_parameters.ContainsKey(TranslationFontSizeRatioKey)) _parameters[TranslationFontSizeRatioKey] = DefaultTranslationSizeRatio.ToString(CultureInfo.InvariantCulture);
            if (!_parameters.ContainsKey(TranslationColorKey)) _parameters[TranslationColorKey] = "#CFCFCF";
            if (!_parameters.ContainsKey(TranslationLineSpacingKey)) _parameters[TranslationLineSpacingKey] = DefaultLineSpacing.ToString(CultureInfo.InvariantCulture);
            if (!_parameters.ContainsKey(TranslationAutoGenerateKey)) _parameters[TranslationAutoGenerateKey] = bool.TrueString;
            if (!_parameters.ContainsKey(TranslationSourceCacheKey)) _parameters[TranslationSourceCacheKey] = string.Empty;
            if (!_parameters.ContainsKey(LayoutModeKey)) _parameters[LayoutModeKey] = "FillClip";
            if (!_parameters.ContainsKey(WrappingWidthKey)) _parameters[WrappingWidthKey] = string.Empty;
        }

        private string GetOrDefault(string key, string fallback)
            => _parameters.TryGetValue(key, out var value) && !string.IsNullOrWhiteSpace(value) ? value : fallback;

        private static TextClipLayoutMode ParseLayoutMode(string? value, TextClipLayoutMode fallback)
            => Enum.TryParse<TextClipLayoutMode>(value, true, out var parsed) ? parsed : fallback;

        private static float ParseFloat(string? value, float fallback)
            => float.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out var parsed) ? parsed : fallback;

        private static int ParseInt(string? value, int fallback)
            => int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed) ? parsed : fallback;

        private static bool ParseBool(string? value, bool fallback)
            => bool.TryParse(value, out var parsed) ? parsed : fallback;

        private static float? ParseNullableFloat(string? value)
        {
            if (string.IsNullOrWhiteSpace(value)) return null;
            return float.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out var parsed) ? parsed : null;
        }

        private static Color ParseColorOrFallback(string? value, Color fallback)
        {
            if (string.IsNullOrWhiteSpace(value)) return fallback;
            try { return Color.FromArgb(value); }
            catch { return fallback; }
        }

        private static (float Width, float Height) MeasureEntries(TextEntry[] entries)
        {
            if (entries.Length == 0) return (1f, 1f);
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

        private static FontFace? ResolveFontFace(string fontFamily) => TextStyleRenderHelper.ResolveFont(fontFamily);

        private static float MeasureTextHeight(FontFace? font, string text, float fontSize)
        {
            if (string.IsNullOrEmpty(text)) return Math.Max(1f, fontSize);
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
    }
}
