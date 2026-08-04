using Microsoft.Extensions.AI;
using Microsoft.Maui.Graphics;
using projectFrameCut.AIAssistance;
using projectFrameCut.ApplicationAPIBase.Text;
using projectFrameCut.ApplicationAPIBase.Views.PropertyPanelBuilders;
using projectFrameCut.ApplicationPluginBase.DynamicPreviewProvider;
using projectFrameCut.Render.Plugin;
using projectFrameCut.Services;
using projectFrameCut.Shared;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using AIChatMessage = Microsoft.Extensions.AI.ChatMessage;
using projectFrameCut.Drawing.Text.Entry;
using projectFrameCut.Drawing.Text.FontHelper;
using projectFrameCut.Drawing.Text.Typology;
using projectFrameCut.Render.ClipsAndTracks.Text;
using projectFrameCut.Render.RenderAPIBase.EffectAndMixture;

namespace projectFrameCut.ApplicationPluginBase.Text
{
    public class LlmTranslateTextStyleProvider : ITextClipStyleProvider
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

        private Dictionary<string, string> _parameters = new();

        public LlmTranslateTextStyleProvider()
        {
            BasicText = DefaultText;
            EnsureDefaults();
        }

        public string FromPlugin => InternalPluginBase.InternalPluginBaseID;

        public string TypeName => "LlmTranslate";

        protected virtual string DefaultText => "Hello world";

        protected virtual float DefaultFontSize => 110f;

        protected virtual float DefaultTranslationSizeRatio => 0.62f;

        protected virtual int DefaultLineSpacing => 12;

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

        public Dictionary<string, EffectArgumentFieldDescriptor> SettableFields
        {
            get
            {
                var fontNames = TextStyleProviderSettableFieldHelper.GetAvailableFontNames();
                return new Dictionary<string, EffectArgumentFieldDescriptor>
                {
                    [TextKey] = TextStyleProviderSettableFieldHelper.StringField(TextKey, "Source Text", "Source text to display and translate", DefaultText),
                    [FontKey] = TextStyleProviderSettableFieldHelper.EnumField(FontKey, "Font Family", "Font used for source and translated text", "HarmonyOS Sans SC Medium", fontNames),
                    [SizeKey] = TextStyleProviderSettableFieldHelper.NumericField(SizeKey, "Font Size", "Source text font size in canvas pixels", DefaultFontSize, 20f, 400f),
                    [ColorKey] = TextStyleProviderSettableFieldHelper.ColorField(ColorKey, "Source Color", "Source text color", "#FFFFFF"),
                    [TranslationTextKey] = TextStyleProviderSettableFieldHelper.StringField(TranslationTextKey, "Translation", "Translated text", ""),
                    [TranslationTargetLanguageKey] = TextStyleProviderSettableFieldHelper.StringField(TranslationTargetLanguageKey, "Target Language", "Target language name or locale", "zh-CN"),
                    [TranslationFontSizeRatioKey] = TextStyleProviderSettableFieldHelper.NumericField(TranslationFontSizeRatioKey, "Translation Size Ratio", "Translated text size relative to source text", DefaultTranslationSizeRatio, 0.3f, 1.2f),
                    [TranslationColorKey] = TextStyleProviderSettableFieldHelper.ColorField(TranslationColorKey, "Translation Color", "Translated text color", "#CFCFCF"),
                    [TranslationLineSpacingKey] = TextStyleProviderSettableFieldHelper.IntegerField(TranslationLineSpacingKey, "Translation Line Spacing", "Space between source and translated text in canvas pixels", DefaultLineSpacing, 0, 80),
                    [TranslationAutoGenerateKey] = TextStyleProviderSettableFieldHelper.BooleanField(TranslationAutoGenerateKey, "Auto Generate Translation", "Automatically regenerate translation when source text changes", true),
                    [LayoutModeKey] = TextStyleProviderSettableFieldHelper.EnumField(LayoutModeKey, "Layout Mode", "How text is sized relative to the clip", TextClipLayoutMode.FillClip.ToString(),
                    [
                        TextClipLayoutMode.FillClip.ToString(),
                        TextClipLayoutMode.FixedWidth.ToString(),
                        TextClipLayoutMode.FixedSize.ToString()
                    ])
                };
            }
        }

        public bool HandleSettableFieldsChange(EffectArgumentFieldDescriptor field, object value, out string feedback)
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
            var translatedText = GetOrBuildTranslation(sourceText);

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
            panel.AddCustomChild(glyphWarning);
            // BasicText editor is managed centrally by ClipInfoBuilder.
            UpdateGlyphWarning();

            panel.AddEntry(TranslationTargetLanguageKey, "Target Language", GetOrDefault(TranslationTargetLanguageKey, "zh-CN"), "zh-CN");
            panel.AddButton(GenerateTranslationButtonKey, "Generate Translation");
            panel.AddCustomChild(
                "Translation",
                (c) =>
                {
                    var e = new Editor
                    {
                        MinimumHeightRequest = 110,
                        Background = Colors.DarkGray,
                        Text = GetOrDefault(TranslationTextKey, string.Empty),
                        IsSpellCheckEnabled = false,
                        IsTextPredictionEnabled = false,
                        Placeholder = "Translated text"
                    };
                    e.TextChanged += (s, e) => c(e.NewTextValue);
                    return e;
                },
                TranslationTextKey, GetOrDefault(TranslationTextKey, string.Empty));


            panel.AddSlider(SizeKey, "Font Size", 20, 400, fontSize, eventCallMode: SliderUpdateEventCallMode.OnMouseUp);
            var translationRatio = ParseFloat(GetOrDefault(TranslationFontSizeRatioKey, DefaultTranslationSizeRatio.ToString(CultureInfo.InvariantCulture)), DefaultTranslationSizeRatio);
            panel.AddSlider(TranslationFontSizeRatioKey, "Translation Size Ratio", 0.3f, 1.2f, translationRatio, eventCallMode: SliderUpdateEventCallMode.OnMouseUp);
            var spacing = ParseInt(GetOrDefault(TranslationLineSpacingKey, DefaultLineSpacing.ToString(CultureInfo.InvariantCulture)), DefaultLineSpacing);
            panel.AddSlider(TranslationLineSpacingKey, "Line Spacing", 0, 80, spacing, eventCallMode: SliderUpdateEventCallMode.OnMouseUp);

            panel.AddEntry(ColorKey, "Source Color", GetOrDefault(ColorKey, "#FFFFFF"), "#FFFFFF");
            panel.AddEntry(TranslationColorKey, "Translation Color", GetOrDefault(TranslationColorKey, "#CFCFCF"), "#CFCFCF");
            panel.AddSwitch(TranslationAutoGenerateKey, "Auto Generate", ParseBool(GetOrDefault(TranslationAutoGenerateKey, bool.TrueString), true));
            return panel;
        }

        public (Dictionary<string, string> newParams, int newWidth, int newHeight) HandlePropertyPanelChange(PropertyPanelPropertyChangedEventArgs args)
        {
            switch (args.Id)
            {
                case TextKey:
                    BasicText = args.Value?.ToString() ?? string.Empty;
                    _parameters[TextKey] = BasicText;
                    if (ParseBool(GetOrDefault(TranslationAutoGenerateKey, bool.TrueString), true))
                    {
                        _parameters[TranslationTextKey] = GenerateTranslation(BasicText);
                        _parameters[TranslationSourceCacheKey] = BasicText;
                    }
                    UpdateGlyphWarning();
                    break;
                case TranslationTargetLanguageKey:
                    _parameters[TranslationTargetLanguageKey] = args.Value?.ToString()?.Trim() ?? "zh-CN";
                    if (ParseBool(GetOrDefault(TranslationAutoGenerateKey, bool.TrueString), true))
                    {
                        _parameters[TranslationTextKey] = GenerateTranslation(BasicText);
                        _parameters[TranslationSourceCacheKey] = BasicText;
                    }
                    UpdateGlyphWarning();
                    break;
                case GenerateTranslationButtonKey:
                    _parameters[TranslationTextKey] = GenerateTranslation(BasicText);
                    _parameters[TranslationSourceCacheKey] = BasicText;
                    break;
                case TranslationTextKey:
                    _parameters[TranslationTextKey] = args.Value?.ToString() ?? string.Empty;
                    _parameters[TranslationSourceCacheKey] = BasicText;
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
                case TranslationFontSizeRatioKey:
                    if (TryParseFloat(args.Value, out var ratio))
                        _parameters[TranslationFontSizeRatioKey] = ratio.ToString(CultureInfo.InvariantCulture);
                    break;
                case TranslationLineSpacingKey:
                    if (int.TryParse(args.Value?.ToString(), NumberStyles.Any, CultureInfo.InvariantCulture, out var lineSpacing))
                        _parameters[TranslationLineSpacingKey] = lineSpacing.ToString(CultureInfo.InvariantCulture);
                    break;
                case ColorKey:
                    _parameters[ColorKey] = args.Value?.ToString() ?? "#FFFFFF";
                    break;
                case TranslationColorKey:
                    _parameters[TranslationColorKey] = args.Value?.ToString() ?? "#CFCFCF";
                    break;
                case TranslationAutoGenerateKey:
                    _parameters[TranslationAutoGenerateKey] = (args.Value is bool b ? b : ParseBool(args.Value?.ToString(), true)).ToString();
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

            var currentFontSize = ParseFloat(GetOrDefault(SizeKey, DefaultFontSize.ToString(CultureInfo.InvariantCulture)), DefaultFontSize);

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

        private string GetOrBuildTranslation(string sourceText)
        {
            if (sourceText == "Hello world") return "你好，世界";
            var cachedSource = GetOrDefault(TranslationSourceCacheKey, string.Empty);
            var cachedTranslation = GetOrDefault(TranslationTextKey, string.Empty);
            if (cachedSource == sourceText && !string.IsNullOrWhiteSpace(cachedTranslation))
                return cachedTranslation;

            if (!ParseBool(GetOrDefault(TranslationAutoGenerateKey, bool.TrueString), true))
                return cachedTranslation;

            var translation = GenerateTranslation(sourceText);
            if (!string.IsNullOrWhiteSpace(translation))
            {
                _parameters[TranslationTextKey] = translation;
                _parameters[TranslationSourceCacheKey] = sourceText;
                return translation;
            }

            return cachedTranslation;
        }

        private string GenerateTranslation(string sourceText)
        {
            if (string.IsNullOrWhiteSpace(sourceText))
                return string.Empty;

            var targetLanguage = GetOrDefault(TranslationTargetLanguageKey, "zh-CN");
            try
            {
                return TaskHelper.SyncWait(
                    () => GenerateTranslationAsync(sourceText, targetLanguage),
                    timeoutMs: 30000,
                    DefaultValue: GetOrDefault(TranslationTextKey, string.Empty));
            }
            catch (Exception ex)
            {
                Logger.Log(ex, $"LLM translation failed for source text '{sourceText}'", typeof(LlmTranslateTextStyleProvider));
                return GetOrDefault(TranslationTextKey, string.Empty);
            }
        }

        private static async Task<string> GenerateTranslationAsync(string sourceText, string targetLanguage)
        {
            var client = AssistanceChatView.CreateChatClient();
            if (client is null) return sourceText;

            var promptLanguage = string.IsNullOrWhiteSpace(targetLanguage) ? "zh-CN" : targetLanguage.Trim();
            var messages = new List<AIChatMessage>
            {
                new(ChatRole.System,
                    $"You are a professional translator. Translate user text to {promptLanguage}. Return only translated text without explanations or quotes."),
                new(ChatRole.User, sourceText)
            };

            ChatResponse response = await client.GetResponseAsync(messages);
            return response.Text?.Trim() ?? string.Empty;
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

        private static bool TryParseFloat(object? value, out float result)
        {
            if (value is float f) { result = f; return true; }
            if (value is double d) { result = (float)d; return true; }
            if (float.TryParse(value?.ToString(), NumberStyles.Float, CultureInfo.InvariantCulture, out var parsed))
            { result = parsed; return true; }
            result = 0f;
            return false;
        }

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
                var rect = TextMeasureHelper.MeasureBounds(entries, 1920f, 1080f);
                return (Math.Max(1f, (float)Math.Ceiling(rect.Width)) + 15f, Math.Max(1f, (float)Math.Ceiling(rect.Height)) + 15f);
            }
            catch
            {
                return (1f, 1f);
            }
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

        private static float MeasureTextHeight(FontFace font, string text, float fontSize)
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
