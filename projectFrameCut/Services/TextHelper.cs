using projectFrameCut.Render.ClipsAndTracks;
using projectFrameCut.Render.Plugin;
using projectFrameCut.Shared;
using SixLabors.Fonts;
using SixLabors.Fonts.Unicode;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.Drawing.Processing;
using SixLabors.ImageSharp.PixelFormats;
using SixLabors.ImageSharp.Processing;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Unicode;
using TinyPinyin;
using Kawazu;
using static projectFrameCut.Services.TextHelper;
using Color = SixLabors.ImageSharp.Color;
using Font = SixLabors.Fonts.Font;
using HorizontalAlignment = SixLabors.Fonts.HorizontalAlignment;
using PointF = SixLabors.ImageSharp.PointF;
using VerticalAlignment = SixLabors.Fonts.VerticalAlignment;
using System.Diagnostics;
using System.IO.Compression;

namespace projectFrameCut.Services
{
    public static class TextHelper
    {
        public enum TextLanguage
        {
            Unknown,
            English,
            Chinese,
            Japanese,
            Korean,
            Russian,
            Thai,
            Arabic
        }

        /// <summary>
        /// 从语言代码转换为 TextLanguage 枚举
        /// </summary>
        /// <param name="languageCode">语言代码，如 "zh-CN", "en", "ja-JP" 等</param>
        /// <returns>对应的 TextLanguage 枚举值</returns>
        public static TextLanguage FromLanguageCode(string languageCode)
        {
            if (string.IsNullOrEmpty(languageCode))
            {
                return TextLanguage.Unknown;
            }

            // 标准化为小写并提取主语言代码
            var code = languageCode.ToLowerInvariant().Split('-', '_')[0];

            return code switch
            {
                "zh" => TextLanguage.Chinese,
                "ja" => TextLanguage.Japanese,
                "ko" => TextLanguage.Korean,
                "ru" => TextLanguage.Russian,
                "th" => TextLanguage.Thai,
                "ar" => TextLanguage.Arabic,
                "en" => TextLanguage.English,
                _ => TextLanguage.Unknown
            };
        }

        /// <summary>
        /// 从 TextLanguage 枚举转换为语言代码
        /// </summary>
        /// <param name="language">TextLanguage 枚举值</param>
        /// <param name="includeRegion">是否包含区域代码，默认为 false</param>
        /// <returns>语言代码字符串，如 "zh", "en", "ja" 等</returns>
        public static string ToLanguageCode(TextLanguage language, bool includeRegion = false)
        {
            if (includeRegion)
            {
                return language switch
                {
                    TextLanguage.Chinese => "zh-CN",
                    TextLanguage.Japanese => "ja-JP",
                    TextLanguage.Korean => "ko-KR",
                    TextLanguage.Russian => "ru-RU",
                    TextLanguage.Thai => "th-TH",
                    TextLanguage.Arabic => "ar-SA",
                    TextLanguage.English => "en-US",
                    _ => string.Empty
                };
            }
            else
            {
                return language switch
                {
                    TextLanguage.Chinese => "zh",
                    TextLanguage.Japanese => "ja",
                    TextLanguage.Korean => "ko",
                    TextLanguage.Russian => "ru",
                    TextLanguage.Thai => "th",
                    TextLanguage.Arabic => "ar",
                    TextLanguage.English => "en",
                    _ => string.Empty
                };
            }
        }

        public static string GetSampleText(TextLanguage lang)
        {
            return lang switch
            {
                TextLanguage.Chinese => "你好，世界！",
                TextLanguage.Japanese => "こんにちは、世界！",
                TextLanguage.Korean => "안녕하세요, 세계!",
                TextLanguage.Russian => "Привет, мир!",
                TextLanguage.Thai => "สวัสดี ชาวโลก!",
                TextLanguage.Arabic => "مرحبا بالعالم!",
                TextLanguage.English => "Hello, world!",
                _ => "Hello, world!",
            };
        }


        public static double MeasureTextLength(string text, float fontSize = 14f)
        {
            try
            {
                Font font = SystemFonts.CreateFont(SystemFonts.Families.First().Name, fontSize);
                FontRectangle rect = TextMeasurer.MeasureSize(text, new TextOptions(font));
                return rect.Width > 0 ? rect.Width : 100;
            }
            catch
            {
                return text.Length * fontSize * 0.6 + 50;
            }
        }


        public static Shared.IPicture GenerateFontThumbnail(string fontPath)
        {
            if (string.IsNullOrEmpty(fontPath) || !File.Exists(fontPath))
            {
                return Picture8bpp.GenerateSolidColor(640, 480, 255, 255, 255, null);
            }

            try
            {
                FontCollection collection = new FontCollection();
                FontFamily family = collection.Add(fontPath);
                Image<Rgba64> canvas = new(640, 480);
                canvas.Mutate((ctx) =>
                {
                    ctx.Fill(Color.White);
                    TextLanguage lang = DetectPrimaryLanguage(family);
                    string sampleText = GetSampleText(lang);
                    Font font = family.CreateFont(72, FontStyle.Regular);

                    ctx.DrawText(sampleText, font, Color.Black, new PointF(10, 240));
                });
                return new Picture8bpp(canvas);

            }
            catch
            {
                return Picture8bpp.GenerateSolidColor(640, 480, 255, 255, 255, null);
            }
        }

        [DebuggerNonUserCode()]
        public static TextLanguage DetectTextLanguage(string input)
        {
            if (string.IsNullOrEmpty(input))
            {
                return TextLanguage.Unknown;
            }

            var languageScores = new Dictionary<TextLanguage, int>
            {
                { TextLanguage.Chinese, 0 },
                { TextLanguage.Japanese, 0 },
                { TextLanguage.Korean, 0 },
                { TextLanguage.Russian, 0 },
                { TextLanguage.Thai, 0 },
                { TextLanguage.Arabic, 0 },
                { TextLanguage.English, 0 }
            };

            foreach (char c in input)
            {
                if (c >= 0x4E00 && c <= 0x9FFF)
                {
                    languageScores[TextLanguage.Chinese]++;
                }
                else if (c >= 0x3040 && c <= 0x309F)
                {
                    languageScores[TextLanguage.Japanese]++;
                }
                else if (c >= 0x30A0 && c <= 0x30FF)
                {
                    languageScores[TextLanguage.Japanese]++;
                }
                else if (c >= 0xAC00 && c <= 0xD7AF)
                {
                    languageScores[TextLanguage.Korean]++;
                }
                else if ((c >= 0x0400 && c <= 0x04FF) || (c >= 0x0500 && c <= 0x052F))
                {
                    languageScores[TextLanguage.Russian]++;
                }
                else if (c >= 0x0E00 && c <= 0x0E7F)
                {
                    languageScores[TextLanguage.Thai]++;
                }
                else if ((c >= 0x0600 && c <= 0x06FF) || (c >= 0x0750 && c <= 0x077F))
                {
                    languageScores[TextLanguage.Arabic]++;
                }
                else if ((c >= 'A' && c <= 'Z') || (c >= 'a' && c <= 'z'))
                {
                    languageScores[TextLanguage.English]++;
                }
            }

            var maxScore = languageScores.Values.Max();
            if (maxScore == 0)
            {
                return TextLanguage.Unknown;
            }

            var detectedLanguage = languageScores.FirstOrDefault(x => x.Value == maxScore).Key;

            if (languageScores[TextLanguage.Chinese] > 0 &&
                (languageScores[TextLanguage.Japanese] > 0))
            {
                detectedLanguage = TextLanguage.Japanese;
            }

            return detectedLanguage;
        }

        public static async Task<string> GetPronounceForOrdering(string input, string? locateID = null)
        {
            var loc = DetectTextLanguage(input);
            locateID ??= Localized._LocaleId_;
            return loc switch
            {
                TextLanguage.Japanese => await GetJapaneseHiragana(input),
                TextLanguage.Chinese when locateID != "ja-JP" => await GetChinesePinyin(input),
                TextLanguage.Chinese when locateID == "ja-JP" => await GetJapaneseHiragana(input),
                TextLanguage.Korean => input,
                TextLanguage.Russian => GetRussianTransliteration(input),
                TextLanguage.Thai => input,
                TextLanguage.Arabic => GetArabicTransliteration(input),
                TextLanguage.English => input,
                _ => input
            };
        }


        private static TextLanguage DetectPrimaryLanguage(FontFamily family)
        {
            TextLanguage result = TextLanguage.Unknown;
            if (family.Culture.ThreeLetterISOLanguageName == "ivl")
            {
                result = family.Name.ToLowerInvariant() switch
                {
                    string name when name.Contains("ja") || name.Contains("jp") => TextLanguage.Japanese,
                    string name when name.Contains("kr") || name.Contains("ko") => TextLanguage.Korean,
                    string name when name.Contains("ru") => TextLanguage.Russian,
                    string name when name.Contains("th") => TextLanguage.Thai,
                    string name when name.Contains("ar") => TextLanguage.Arabic,
                    string name when name.Contains("zh") || name.Contains("sc") || name.Contains("tc") => TextLanguage.Chinese,
                    _ => TextLanguage.English,
                };
            }
            else
            {
                result = family.Culture.Name.StartsWith("ja") ? TextLanguage.Japanese :
                                      family.Culture.Name.StartsWith("ko") ? TextLanguage.Korean :
                                      family.Culture.Name.StartsWith("ru") ? TextLanguage.Russian :
                                      family.Culture.Name.StartsWith("th") ? TextLanguage.Thai :
                                      family.Culture.Name.StartsWith("ar") ? TextLanguage.Arabic :
                                      family.Culture.Name.StartsWith("zh") ? TextLanguage.Chinese :
                                      TextLanguage.English;
            }


            Log($"Font {family.Name}: consider as {result}.");

            return result;
        }

        public static async Task<string> GetHowToPronuce(string input, TextLanguage? language = null)
        {
            if (string.IsNullOrEmpty(input))
            {
                return string.Empty;
            }

            try
            {
                var pron = (language ?? DetectTextLanguage(input)) switch
                {
                    TextLanguage.Chinese => await GetChinesePinyin(input),
                    TextLanguage.Japanese => await GetJapaneseRomaji(input),
                    TextLanguage.Korean => input,
                    TextLanguage.Russian => GetRussianTransliteration(input),
                    TextLanguage.Thai => input,
                    TextLanguage.Arabic => GetArabicTransliteration(input),
                    TextLanguage.English => input,
                    _ => input,
                };
                LogDiagnostic($"Sentence '{input}' has pronunciation '{pron}' in {(language ?? DetectTextLanguage(input))}.");
                return pron;
            }
            catch (Exception ex)
            {
                Log($"Error getting pronunciation for '{input}': {ex.Message}");
                return input;
            }
        }

        private static async Task<string> GetChinesePinyin(string input)
        {
            try
            {
                return PinyinHelper.GetPinyin(input, "");
            }
            catch (Exception ex)
            {
                Log(ex, $"converting Chinese to Pinyin via PinyinHelper");
                return input;
            }
        }


        static async Task<KawazuConverter> GetKawazuConvenerAsync()
        {
            if (!Directory.Exists(Path.Combine(MauiProgram.BasicDataPath, "JapaneseDictionary")))
            {
                try
                {
                    var zip = await FileSystem.OpenAppPackageFileAsync("JapaneseDictionary/JapaneseDictionary.zip");
                    using var archive = new System.IO.Compression.ZipArchive(zip, System.IO.Compression.ZipArchiveMode.Read);
                    var extractPath = Path.Combine(MauiProgram.BasicDataPath, "JapaneseDictionary");
                    if (!Directory.Exists(extractPath))
                    {
                        Directory.CreateDirectory(extractPath);
                    }
                    archive.ExtractToDirectory(extractPath);
                }
                catch (Exception ex)
                {
                    Log(ex, "extracting JapaneseDictionary.zip");
                }
            }
            return new KawazuConverter(Path.Combine(MauiProgram.BasicDataPath, "JapaneseDictionary"));
        }

        private static async Task<string> GetJapaneseRomaji(string input)
        {
            try
            {

                var result = await (await GetKawazuConvenerAsync()).Convert(input, To.Romaji, Mode.Normal, RomajiSystem.Hepburn, "", "");
                return result;
            }
            catch (Exception ex)
            {
                Log(ex, $"Error converting Japanese to Romaji via KawazuConverter");

                var result = new StringBuilder();
                foreach (char c in input)
                {
                    if (JapaneseKatakanaOrHiraganaMapping.TryGetValue(c, out string? transliteration))
                    {
                        result.Append(transliteration);
                    }
                    else if (c >= 0x4E00 && c <= 0x9FFF)
                    {
                        result.Append(c); 
                    }
                    else
                    {
                        result.Append(c);
                    }
                }
                return result.ToString();
            }


        }

        public static async Task<string> GetJapaneseHiragana(string input)
        {
            try
            {
                var result = await (await GetKawazuConvenerAsync()).Convert(input, To.Hiragana);
                return result;
            }
            catch (Exception ex)
            {
                return input;
            }

        }

        private static string GetRussianTransliteration(string input)
        {
            var result = new StringBuilder();
            foreach (char c in input)
            {
                if (RussianMapping.TryGetValue(c, out string? transliteration))
                {
                    result.Append(transliteration);
                }
                else
                {
                    result.Append(c);
                }
            }
            return result.ToString();
        }


        private static string GetArabicTransliteration(string input)
        {

            var result = new StringBuilder();
            foreach (char c in input)
            {
                if (ArabicMapping.TryGetValue(c, out string? transliteration))
                {
                    result.Append(transliteration);
                }
                else
                {
                    result.Append(c);
                }
            }
            return result.ToString();
        }


        static Dictionary<char, string> JapaneseKatakanaOrHiraganaMapping = new Dictionary<char, string>
        {
            {'あ',"a"},{'い',"i"},{'う',"u"},{'え',"e"},{'お',"o"},
            {'か',"ka"},{'き',"ki"},{'く',"ku"},{'け',"ke"},{'こ',"ko"},
            {'さ',"sa"},{'し',"shi"},{'す',"su"},{'せ',"se"},{'そ',"so"},
            {'た',"ta"},{'ち',"chi"},{'つ',"tsu"},{'て',"te"},{'と',"to"},
            {'な',"na"},{'に',"ni"},{'ぬ',"nu"},{'ね',"ne"},{'の',"no"},
            {'は',"ha"},{'ひ',"hi"},{'ふ',"fu"},{'へ',"he"},{'ほ',"ho"},
            {'ま',"ma"},{'み',"mi"},{'む',"mu"},{'め',"me"},{'も',"mo"},
            {'や',"ya"},{'ゆ',"yu"},{'よ',"yo"},
            {'ら',"ra"},{'り',"ri"},{'る',"ru"},{'れ',"re"},{'ろ',"ro"},
            {'わ',"wa"},{'を',"wo"},{'ん',"n"},

            {'が',"ga"},{'ぎ',"gi"},{'ぐ',"gu"},{'げ',"ge"},{'ご',"go"},
            {'ざ',"za"},{'じ',"ji"},{'ず',"zu"},{'ぜ',"ze"},{'ぞ',"zo"},
            {'だ',"da"},{'ぢ',"ji"},{'づ',"zu"},{'で',"de"},{'ど',"do"},
            {'ば',"ba"},{'び',"bi"},{'ぶ',"bu"},{'べ',"be"},{'ぼ',"bo"},
            {'ぱ',"pa"},{'ぴ',"pi"},{'ぷ',"pu"},{'ぺ',"pe"},{'ぽ',"po"},

            {'ぁ',"a"},{'ぃ',"i"},{'ぅ',"u"},{'ぇ',"e"},{'ぉ',"o"},
            {'ゃ',"ya"},{'ゅ',"yu"},{'ょ',"yo"},{'っ',"tsu"},

            {'ア',"a"},{'イ',"i"},{'ウ',"u"},{'エ',"e"},{'オ',"o"},
            {'カ',"ka"},{'キ',"ki"},{'ク',"ku"},{'ケ',"ke"},{'コ',"ko"},
            {'サ',"sa"},{'シ',"shi"},{'ス',"su"},{'セ',"se"},{'ソ',"so"},
            {'タ',"ta"},{'チ',"chi"},{'ツ',"tsu"},{'テ',"te"},{'ト',"to"},
            {'ナ',"na"},{'ニ',"ni"},{'ヌ',"nu"},{'ネ',"ne"},{'ノ',"no"},
            {'ハ',"ha"},{'ヒ',"hi"},{'フ',"fu"},{'ヘ',"he"},{'ホ',"ho"},
            {'マ',"ma"},{'ミ',"mi"},{'ム',"mu"},{'メ',"me"},{'モ',"mo"},
            {'ヤ',"ya"},{'ユ',"yu"},{'ヨ',"yo"},
            {'ラ',"ra"},{'リ',"ri"},{'ル',"ru"},{'レ',"re"},{'ロ',"ro"},
            {'ワ',"wa"},{'ヲ',"wo"},{'ン',"n"},

            {'ガ',"ga"},{'ギ',"gi"},{'グ',"gu"},{'ゲ',"ge"},{'ゴ',"go"},
            {'ザ',"za"},{'ジ',"ji"},{'ズ',"zu"},{'ゼ',"ze"},{'ゾ',"zo"},
            {'ダ',"da"},{'ヂ',"ji"},{'ヅ',"zu"},{'デ',"de"},{'ド',"do"},
            {'バ',"ba"},{'ビ',"bi"},{'ブ',"bu"},{'ベ',"be"},{'ボ',"bo"},
            {'パ',"pa"},{'ピ',"pi"},{'プ',"pu"},{'ペ',"pe"},{'ポ',"po"},

            {'ァ',"a"},{'ィ',"i"},{'ゥ',"u"},{'ェ',"e"},{'ォ',"o"},
            {'ャ',"ya"},{'ュ',"yu"},{'ョ',"yo"},{'ッ',"tsu"}

        };

        static Dictionary<char, string> ArabicMapping = new Dictionary<char, string>
        {
            {'ا', "a"}, {'ب', "b"}, {'ت', "t"}, {'ث', "th"}, {'ج', "j"},
            {'ح', "h"}, {'خ', "kh"}, {'د', "d"}, {'ذ', "dh"}, {'ر', "r"},
            {'ز', "z"}, {'س', "s"}, {'ش', "sh"}, {'ص', "s"}, {'ض', "d"},
            {'ط', "t"}, {'ظ', "z"}, {'ع', "'"}, {'غ', "gh"}, {'ف', "f"},
            {'ق', "q"}, {'ك', "k"}, {'ل', "l"}, {'م', "m"}, {'ن', "n"},
            {'ه', "h"}, {'و', "w"}, {'ي', "y"}
        };

        static Dictionary<char, string> RussianMapping = new Dictionary<char, string>
        {
            {'а', "a"}, {'б', "b"}, {'в', "v"}, {'г', "g"}, {'д', "d"},
            {'е', "e"}, {'ё', "yo"}, {'ж', "zh"}, {'з', "z"}, {'и', "i"},
            {'й', "y"}, {'к', "k"}, {'л', "l"}, {'м', "m"}, {'н', "n"},
            {'о', "o"}, {'п', "p"}, {'р', "r"}, {'с', "s"}, {'т', "t"},
            {'у', "u"}, {'ф', "f"}, {'х', "kh"}, {'ц', "ts"}, {'ч', "ch"},
            {'ш', "sh"}, {'щ', "shch"}, {'ъ', ""}, {'ы', "y"}, {'ь', ""},
            {'э', "e"}, {'ю', "yu"}, {'я', "ya"},
            {'А', "A"}, {'Б', "B"}, {'В', "V"}, {'Г', "G"}, {'Д', "D"},
            {'Е', "E"}, {'Ё', "Yo"}, {'Ж', "Zh"}, {'З', "Z"}, {'И', "I"},
            {'Й', "Y"}, {'К', "K"}, {'Л', "L"}, {'М', "M"}, {'Н', "N"},
            {'О', "O"}, {'П', "P"}, {'Р', "R"}, {'С', "S"}, {'Т', "T"},
            {'У', "U"}, {'Ф', "F"}, {'Х', "Kh"}, {'Ц', "Ts"}, {'Ч', "Ch"},
            {'Ш', "Sh"}, {'Щ', "Shch"}, {'Ъ', ""}, {'Ы', "Y"}, {'Ь', ""},
            {'Э', "E"}, {'Ю', "Yu"}, {'Я', "Ya"}
        };

        public static string DummyString = "Lorem ipsum dolor sit amet, consectetur adipiscing elit. Curabitur est tortor, imperdiet et dui id, egestas hendrerit quam. Suspendisse ac felis a felis ultrices cursus a sit amet ligula. Praesent volutpat vitae dolor luctus rutrum. Vestibulum eu nibh magna. Maecenas vel tempus nunc. Donec vitae convallis odio. Donec nec mattis sapien.";

        public static string[] DummyStrings => JapaneseKatakanaOrHiraganaMapping.Keys.Select(c => c.ToString()).ToArray();

    }


}
