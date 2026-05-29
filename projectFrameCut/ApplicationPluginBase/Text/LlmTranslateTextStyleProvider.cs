using Microsoft.Extensions.AI;
using Microsoft.Maui.Graphics;
using projectFrameCut.AIAssistance;
using projectFrameCut.ApplicationAPIBase.Text;
using projectFrameCut.ApplicationAPIBase.Views.PropertyPanelBuilders;
using projectFrameCut.ApplicationPluginBase.DynamicPreviewProvider;
using projectFrameCut.Render.Plugin;
using projectFrameCut.Services;
using projectFrameCut.Shared;
using SixLabors.Fonts;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using AIChatMessage = Microsoft.Extensions.AI.ChatMessage;
using Font = SixLabors.Fonts.Font;

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

        private Dictionary<string, string> _parameters = new();

        public LlmTranslateTextStyleProvider()
        {
            EnsureDefaults();
        }

        public string FromPlugin => InternalPluginBase.InternalPluginBaseID;

        public string TypeName => "LlmTranslate";

        protected virtual string DefaultText => "Hello world";

        protected virtual float DefaultFontSize => 56f;

        protected virtual float DefaultTranslationSizeRatio => 0.62f;

        protected virtual int DefaultLineSpacing => 12;

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
            var sourceText = BasicText;
            if (string.IsNullOrWhiteSpace(sourceText))
            {
                return Array.Empty<TextClipEntry>();
            }

            var fontFamily = GetOrDefault(FontKey, "Arial");
            var fontSize = ParseFloat(GetOrDefault(SizeKey, DefaultFontSize.ToString(CultureInfo.InvariantCulture)), DefaultFontSize);
            var color = ParseColorOrFallback(GetOrDefault(ColorKey, "#FFFFFF"), Colors.White);
            var translationRatio = ParseFloat(GetOrDefault(TranslationFontSizeRatioKey, DefaultTranslationSizeRatio.ToString(CultureInfo.InvariantCulture)), DefaultTranslationSizeRatio);
            var translationFontSize = Math.Max(12f, fontSize * translationRatio);
            var translationColor = ParseColorOrFallback(GetOrDefault(TranslationColorKey, "#CFCFCF"), Colors.LightGray);
            var lineSpacing = ParseInt(GetOrDefault(TranslationLineSpacingKey, DefaultLineSpacing.ToString(CultureInfo.InvariantCulture)), DefaultLineSpacing);
            var translatedText = GetOrBuildTranslation(sourceText);

            var sourceEntry = new TextClipEntry
            {
                text = sourceText,
                x = 0,
                y = 0,
                fontFamily = fontFamily,
                fontSize = fontSize,
                r = (ushort)Math.Round(color.Red * 65535),
                g = (ushort)Math.Round(color.Green * 65535),
                b = (ushort)Math.Round(color.Blue * 65535),
                a = (float)color.Alpha
            };

            if (string.IsNullOrWhiteSpace(translatedText))
            {
                return [sourceEntry];
            }

            var sourceHeight = MeasureTextHeight(ResolveFont(fontFamily, fontSize), sourceText);
            var translationY = (int)Math.Ceiling(sourceHeight + Math.Max(0, lineSpacing));
            var translationEntry = new TextClipEntry
            {
                text = translatedText,
                x = 0,
                y = translationY,
                fontFamily = fontFamily,
                fontSize = translationFontSize,
                r = (ushort)Math.Round(translationColor.Red * 65535),
                g = (ushort)Math.Round(translationColor.Green * 65535),
                b = (ushort)Math.Round(translationColor.Blue * 65535),
                a = (float)translationColor.Alpha
            };
            return [sourceEntry, translationEntry];
        }

        public PropertyPanelBuilder BuildPropertyPanel()
        {
            var panel = new PropertyPanelBuilder();
            var currentText = BasicText;
            var currentFont = GetOrDefault(FontKey, "Arial");
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
                        Placeholder = "Source text"
                    };
                    e.TextChanged += (s, e) =>
                    {
                        currentText = e.NewTextValue ?? string.Empty;
                        c(e.NewTextValue);
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
                TextKey, GetOrDefault(TextKey, DefaultText));

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
                    if (ParseBool(GetOrDefault(TranslationAutoGenerateKey, bool.TrueString), true))
                    {
                        _parameters[TranslationTextKey] = GenerateTranslation(BasicText);
                        _parameters[TranslationSourceCacheKey] = BasicText;
                    }
                    break;
                case TranslationTargetLanguageKey:
                    _parameters[TranslationTargetLanguageKey] = args.Value?.ToString()?.Trim() ?? "zh-CN";
                    if (ParseBool(GetOrDefault(TranslationAutoGenerateKey, bool.TrueString), true))
                    {
                        _parameters[TranslationTextKey] = GenerateTranslation(BasicText);
                        _parameters[TranslationSourceCacheKey] = BasicText;
                    }
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
                    _parameters[FontKey] = args.Value?.ToString() ?? "Arial";
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
                    {
                        _parameters[TranslationFontSizeRatioKey] = ratio.ToString(CultureInfo.InvariantCulture);
                    }
                    break;
                case TranslationLineSpacingKey:
                    if (int.TryParse(args.Value?.ToString(), NumberStyles.Any, CultureInfo.InvariantCulture, out var lineSpacing))
                    {
                        _parameters[TranslationLineSpacingKey] = lineSpacing.ToString(CultureInfo.InvariantCulture);
                    }
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

            var currentFontSize = ParseFloat(GetOrDefault(SizeKey, DefaultFontSize.ToString(CultureInfo.InvariantCulture)), DefaultFontSize);

            var scaleX = (double)TargetWidth / currentRect.TargetWidth;
            var scaleY = (double)TargetHeight / currentRect.TargetHeight;
            if (double.IsNaN(scaleX) || double.IsInfinity(scaleX) || scaleX <= 0)
            {
                return new Dictionary<string, string>(_parameters);
            }

            var scale = scaleX;
            if (!isInRatio && !double.IsNaN(scaleY) && !double.IsInfinity(scaleY) && scaleY > 0 && Math.Abs(scaleX - scaleY) > 0.0001)
            {
                scale = (scaleX + scaleY) / 2d;
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

        private string GetOrBuildTranslation(string sourceText)
        {
            var cachedSource = GetOrDefault(TranslationSourceCacheKey, string.Empty);
            var cachedTranslation = GetOrDefault(TranslationTextKey, string.Empty);
            if (cachedSource == sourceText && !string.IsNullOrWhiteSpace(cachedTranslation))
            {
                return cachedTranslation;
            }

            if (!ParseBool(GetOrDefault(TranslationAutoGenerateKey, bool.TrueString), true))
            {
                return cachedTranslation;
            }

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
            {
                return string.Empty;
            }

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
            if (client is null)
            {
                return sourceText;
            }

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
            if (!_parameters.ContainsKey(FontKey)) _parameters[FontKey] = "Arial";
            if (!_parameters.ContainsKey(SizeKey)) _parameters[SizeKey] = DefaultFontSize.ToString(CultureInfo.InvariantCulture);
            if (!_parameters.ContainsKey(ColorKey)) _parameters[ColorKey] = "#FFFFFF";
            if (!_parameters.ContainsKey(TranslationTextKey)) _parameters[TranslationTextKey] = string.Empty;
            if (!_parameters.ContainsKey(TranslationTargetLanguageKey)) _parameters[TranslationTargetLanguageKey] = "zh-CN";
            if (!_parameters.ContainsKey(TranslationFontSizeRatioKey)) _parameters[TranslationFontSizeRatioKey] = DefaultTranslationSizeRatio.ToString(CultureInfo.InvariantCulture);
            if (!_parameters.ContainsKey(TranslationColorKey)) _parameters[TranslationColorKey] = "#CFCFCF";
            if (!_parameters.ContainsKey(TranslationLineSpacingKey)) _parameters[TranslationLineSpacingKey] = DefaultLineSpacing.ToString(CultureInfo.InvariantCulture);
            if (!_parameters.ContainsKey(TranslationAutoGenerateKey)) _parameters[TranslationAutoGenerateKey] = bool.TrueString;
            if (!_parameters.ContainsKey(TranslationSourceCacheKey)) _parameters[TranslationSourceCacheKey] = string.Empty;
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
            return int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed) ? parsed : fallback;
        }

        private static bool ParseBool(string? value, bool fallback)
        {
            return bool.TryParse(value, out var parsed) ? parsed : fallback;
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

        private static float MeasureTextHeight(Font font, string text)
        {
            if (string.IsNullOrEmpty(text))
            {
                return Math.Max(1f, font.Size);
            }

            var rect = TextMeasurer.MeasureBounds(text, new TextOptions(font));
            return Math.Max(1f, rect.Height);
        }
    }
}
