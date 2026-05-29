using Kawazu;
using projectFrameCut.Render.ClipsAndTracks;
using projectFrameCut.Render.Plugin;
using Microsoft.Maui.Controls;
using projectFrameCut.Render.RenderAPIBase.Project;
using projectFrameCut.Shared;
using SixLabors.Fonts;
using SixLabors.Fonts.Unicode;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.Drawing.Processing;
using SixLabors.ImageSharp.PixelFormats;
using SixLabors.ImageSharp.Processing;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text;
using System.Text.Unicode;
using TinyPinyin;
using static projectFrameCut.ApplicationAPIBase.Helpers.TextHelper;
using Color = SixLabors.ImageSharp.Color;
using Font = SixLabors.Fonts.Font;
using HorizontalAlignment = SixLabors.Fonts.HorizontalAlignment;
using PointF = SixLabors.ImageSharp.PointF;
using VerticalAlignment = SixLabors.Fonts.VerticalAlignment;
using projectFrameCut.ApplicationAPIBase.Views.Pickers;
using projectFrameCut.ApplicationAPIBase.Helpers;
using projectFrameCut.Drawing.Base.Picture;

namespace projectFrameCut.Services
{
    public static class TextServices
    {
        #region thumb
        public static Drawing.Base.IPicture GenerateFontThumbnail(string fontPath)
        {
            if (string.IsNullOrEmpty(fontPath) || !File.Exists(fontPath))
            {
                return Picture8bpp.GenerateSolidColor(640, 480, 255, 255, 255, null);
            }

            try
            {
                // 直接从字体文件 name 表 + OS/2 Unicode Range 读取语言，不再依赖命名猜测
                var info = ReadFontFileInfo(fontPath);
                FontCollection collection = new FontCollection();
                FontFamily family = collection.Add(fontPath);
                Image<Rgba64> canvas = new(640, 480);
                canvas.Mutate((ctx) =>
                {
                    ctx.Fill(Color.White);
                    string sampleText = GetSampleText(info.PrimaryLanguage);
                    Font font = family.CreateFont(72, FontStyle.Regular);
                    ctx.DrawText(sampleText, font, Color.Black, new PointF(10, 240));
                });
                return Shared.PictureExtensions.ToPJFCPicture(canvas, 8);
            }
            catch
            {
                return Picture8bpp.GenerateSolidColor(640, 480, 255, 255, 255, null);
            }
        }


        public static Task<ImageSource> RenderFontPreviewAsync(projectFrameCut.ApplicationAPIBase.Views.Pickers.FontItem item) => RenderFontPreviewAsync(item, 1000, 64);

        /// <summary>
        /// 渲染字体预览图。<paramref name="sample"/> 为 null 时，自动根据字体文件 name 表检测主语言并使用对应语言的样本文字。
        /// </summary>
        public static async Task<ImageSource> RenderFontPreviewAsync(projectFrameCut.ApplicationAPIBase.Views.Pickers.FontItem item, int width = 420, int height = 64, string? sample = null)
        {
            if (item == null)
                return null;

#pragma warning disable CS8603 // shut up pls
            return await Task<ImageSource>.Run(() =>
            {
                var isDark = Application.Current?.RequestedTheme == AppTheme.Dark;

                var cachePath = Path.Combine(FileSystem.CacheDirectory, "FontCache", isDark ? "dark" : "light", $"{item.FontName}.png");
                if (File.Exists(cachePath))
                {
                    return ImageSource.FromFile(cachePath);
                }
                float fontSize = Math.Clamp(height * 0.6f, 12f, 64f);

                try
                {
                    Font font = null!;
                    try
                    {
                        if (!TryResolveFontFamily(item, out var family))
                        {
                            throw new InvalidOperationException("Font not available.");
                        }
                        font = family.CreateFont(fontSize);
                    }
                    catch
                    {
                        font = TextClip.FontsCache.TryGet(item.FontName, out var family) ? family.CreateFont(fontSize) : throw new InvalidOperationException("Font not available.");
                    }


                    // 当未指定 sample 时，优先使用 FontItem.PrimaryLanguageTag 判断语言，
                    // 再尝试从字体文件直接读取（如果能找到路径的话）。
                    string effectiveSample = sample ?? ResolveSampleText(item);

                    var options = new DrawingOptions();
                    using var img = new Image<Rgba32>(width, height);
                    img.Mutate(ctx =>
                    {
                        ctx.Fill(Color.Transparent);
                        var location = new PointF(10, height / 2f - fontSize / 2f);
                        ctx.DrawText(options, effectiveSample, font, isDark ? Color.White : Color.Black, location);
                    });

                    img.SaveAsPng(cachePath);
                    img.Dispose();

                    return ImageSource.FromFile(cachePath);
                }
                catch (Exception ex)
                {
                    Log(ex, $"Init preview image for font {item.FontName}");
                }
                return null;
            });
#pragma warning restore CS8603 
        }
        /// <summary>
        /// 根据 FontItem.PrimaryLanguageTag 选出合适的样本文字。
        /// </summary>
        private static string ResolveSampleText(projectFrameCut.ApplicationAPIBase.Views.Pickers.FontItem item)
        {
            if (!string.IsNullOrEmpty(item.PrimaryLanguageTag))
            {
                var lang = FromLanguageCode(item.PrimaryLanguageTag);
                if (lang != TextLanguage.Unknown)
                    return GetSampleText(lang);
            }
            // 最后回退到通用英文
            return GetSampleText(TextLanguage.English);
        }
        #endregion

        #region font

        public static Dictionary<string, FontItem> LoadedFonts = new();

        public static void LoadFonts()
        {
            Directory.CreateDirectory(Path.Combine(FileSystem.CacheDirectory, "FontCache"));
            Directory.CreateDirectory(Path.Combine(FileSystem.CacheDirectory, "FontCache", "dark"));
            Directory.CreateDirectory(Path.Combine(FileSystem.CacheDirectory, "FontCache", "light"));
            LoadedFonts.Clear();
            foreach (var f in (new[] { "*.ttf", "*.otf", "*.ttc" }).SelectMany(ext => Directory.GetFiles(Path.Combine(MauiProgram.DataPath, "My Assets"), ext)))
            {
                var info = TextHelper.ReadFontFileInfo(f);
                var fontCollection = new FontCollection();
                FontFamily family;
                try
                {
                    family = fontCollection.Add(f);
                }
                catch (Exception ex)
                {
                    Log(ex, $"Failed to add font to collection: {f}");
                    continue;
                }
                if (string.IsNullOrWhiteSpace(family.Name))
                {
                    continue;
                }
                LoadedFonts.TryAdd(info.EnglishName, new FontItem
                {
                    InnerItem = info,
                    InnerFont = fontCollection,
                    InnerFamily = family,
                    Category = Localized.TextServices_FontCatagory_YourAsset,
                    DisplayName = info.DisplayName,
                    PrimaryLanguageTag = TextHelper.ToLanguageCode(info.PrimaryLanguage, true),
                    FontName = info.EnglishName,
                    Path = f

                });

            }
            foreach (var item in TextHelper.BuildSystemFontItems(category: Localized.TextServices_FontCatagory_System))
            {
                LoadedFonts.TryAdd(item.FontName, item);
            }

        }

        public static string GetMissingGlyphWarning(string fontName, string text, float fontSize = 14f)
        {
            if (string.IsNullOrWhiteSpace(fontName) || string.IsNullOrEmpty(text))
            {
                return string.Empty;
            }

            if (!TryResolveFont(fontName, fontSize, out var font))
            {
                return string.Empty;
            }

            var missing = TextHelper.GetMissingGlyphs(font, text).Distinct().ToArray();
            if (missing.Length == 0)
            {
                return string.Empty;
            }

            var preview = string.Join(", ", missing.Take(6).Select(codePoint => $"U+{codePoint:X4}"));
            var suffix = missing.Length > 6 ? "..." : string.Empty;
            return $"当前字体 \"{fontName}\" 可能不支持部分字符：{preview}{suffix}";
        }

        private static bool TryResolveFont(string fontName, float fontSize, out Font font)
        {
            font = default!;

            if (LoadedFonts.TryGetValue(fontName, out var item))
            {
                if (TryCreateFontFromItem(item, fontSize, out font))
                {
                    return true;
                }
            }

            if (TextClip.GetFont().TryGet(fontName, out var family))
            {
                font = family.CreateFont(fontSize);
                return true;
            }

            return false;
        }

        public static bool TryResolveFontFamily(projectFrameCut.ApplicationAPIBase.Views.Pickers.FontItem item, out SixLabors.Fonts.FontFamily family)
        {
            family = default;

            if (!string.IsNullOrWhiteSpace(item.InnerFamily.Name))
            {
                family = item.InnerFamily;
                return true;
            }

            var collectionFamily = item.InnerFont?.Families.FirstOrDefault();
            if (collectionFamily is { } fromCollection && !string.IsNullOrWhiteSpace(fromCollection.Name))
            {
                family = fromCollection;
                return true;
            }

            if (!string.IsNullOrWhiteSpace(item.Path) && File.Exists(item.Path))
            {
                try
                {
                    var collection = new FontCollection();
                    var pathFamily = collection.Add(item.Path);
                    if (string.IsNullOrWhiteSpace(pathFamily.Name))
                    {
                        return false;
                    }

                    family = pathFamily;
                    return true;
                }
                catch (Exception ex)
                {
                    Log(ex, $"TryResolveFontFamily('{item.FontName}')");
                }
            }

            return false;
        }

        private static bool TryCreateFontFromItem(FontItem item, float fontSize, out Font font)
        {
            font = default!;

            if (TryResolveFontFamily(item, out var family))
            {
                font = family.CreateFont(fontSize);
                return true;
            }

            return false;
        }

        #endregion

        #region pron and order
        public static async Task<IEnumerable<TResult>> OrderByPronounceAsync<TResult>(this IEnumerable<TResult> source, Func<TResult, string> keySelector, string? locateID = null)
        {
            var kvpList = await GetPronounceKVP(source, keySelector, locateID).ToListAsync();
            return kvpList.OrderBy(kvp => kvp.Value).Select(kvp => kvp.Key);
        }
        public static async Task<IEnumerable<TResult>> OrderByPronounceDescendingAsync<TResult>(this IEnumerable<TResult> source, Func<TResult, string> keySelector, string? locateID = null)
        {
            var kvpList = await GetPronounceKVP(source, keySelector, locateID).ToListAsync();
            return kvpList.OrderByDescending(kvp => kvp.Value).Select(kvp => kvp.Key);
        }

        private static async IAsyncEnumerable<KeyValuePair<TResult, string>> GetPronounceKVP<TResult>(IEnumerable<TResult> source, Func<TResult, string> keySelector, string? locateID = null)
        {
            foreach (var item in source)
            {
                yield return new(item, await GetPronounceForOrdering(keySelector(item), locateID));
            }
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
            catch
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

        public static string[] DummyStrings => JapaneseKatakanaOrHiraganaMapping.Keys.Select(c => c.ToString()).ToArray();

        #endregion

    }


}
