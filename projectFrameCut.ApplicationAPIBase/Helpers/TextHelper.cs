using projectFrameCut.ApplicationAPIBase.Views.Pickers;
using static projectFrameCut.ApplicationAPIBase.Localize.APIBaseLocalizedResources;
using projectFrameCut.Shared;
using projectFrameCut.Drawing.Text.FontHelper;
using projectFrameCut.Drawing.Text.Entry;
using projectFrameCut.Drawing.Text.Typology;
using System.Diagnostics;

namespace projectFrameCut.ApplicationAPIBase.Helpers
{
    public static class TextHelper
    {
        #region language


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
        #endregion

        #region font 

        public static double MeasureTextLength(string text, float fontSize = 14f)
        {
            try
            {
                // Use a rough estimate; for precise measurement FontFace + Engine is overkill for this helper.
                return text.Length * fontSize * 0.6 + 50;
            }
            catch
            {
                return text.Length * fontSize * 0.6 + 50;
            }
        }

        // ──────────────────────────────────────────────────────────────────────
        // 字形（Glyph）存在性检测
        // ──────────────────────────────────────────────────────────────────────

        /// <summary>
        /// 将字体 SubfamilyName（如 "Regular"、"Bold"）本地化为当前语言的显示名称。
        /// </summary>
        private static string LocalizeFontStyleName(string subfamilyName)
        {
            if (string.IsNullOrWhiteSpace(subfamilyName))
                return subfamilyName ?? string.Empty;

            var loc = Localized;

            return subfamilyName.Trim() switch
            {
                string s when s.Equals("Regular", StringComparison.OrdinalIgnoreCase) => loc.FontPicker_FontStyle_Regular,
                string s when s.Equals("Bold", StringComparison.OrdinalIgnoreCase) => loc.FontPicker_FontStyle_Bold,
                string s when s.Equals("Italic", StringComparison.OrdinalIgnoreCase) => loc.FontPicker_FontStyle_Italic,
                string s when s.Equals("Light", StringComparison.OrdinalIgnoreCase) => loc.FontPicker_FontStyle_Light,
                string s when s.Equals("Medium", StringComparison.OrdinalIgnoreCase) => loc.FontPicker_FontStyle_Medium,
                string s when s.Equals("SemiBold", StringComparison.OrdinalIgnoreCase) || s.Equals("DemiBold", StringComparison.OrdinalIgnoreCase) || s.Equals("Semibold", StringComparison.OrdinalIgnoreCase) => loc.FontPicker_FontStyle_SemiBold,
                string s when s.Equals("ExtraBold", StringComparison.OrdinalIgnoreCase) || s.Equals("Extra Bold", StringComparison.OrdinalIgnoreCase) || s.Equals("Black", StringComparison.OrdinalIgnoreCase) => loc.FontPicker_FontStyle_ExtraBold,
                string s when s.Equals("Thin", StringComparison.OrdinalIgnoreCase) || s.Equals("Hairline", StringComparison.OrdinalIgnoreCase) => loc.FontPicker_FontStyle_Thin,
                string s when s.Equals("ExtraLight", StringComparison.OrdinalIgnoreCase) || s.Equals("Extra Light", StringComparison.OrdinalIgnoreCase) => loc.FontPicker_FontStyle_ExtraLight,
                string s when s.Equals("Heavy", StringComparison.OrdinalIgnoreCase) => loc.FontPicker_FontStyle_Heavy,
                string s when s.Equals("Book", StringComparison.OrdinalIgnoreCase) => loc.FontPicker_FontStyle_Book,
                string s when s.Equals("Normal", StringComparison.OrdinalIgnoreCase) => loc.FontPicker_FontStyle_Normal,
                _ => subfamilyName.Trim(),
            };
        }

        /// <summary>
        /// 检测 <see cref="FontFace"/> 中是否存在指定字符的字形（Glyph）。
        /// </summary>
        public static bool FontContainsGlyph(FontFace font, char character)
            => font.GetGlyphIndex(character) != 0;

        /// <summary>
        /// 检测 <see cref="FontFace"/> 中是否存在指定 Unicode 码点的字形。
        /// </summary>
        public static bool FontContainsGlyph(FontFace font, int codePoint)
            => codePoint <= char.MaxValue && font.GetGlyphIndex((char)codePoint) != 0;

        /// <summary>
        /// 检测 <see cref="FontFace"/> 中是否包含 <paramref name="text"/> 内所有字符的字形。
        /// </summary>
        public static bool FontContainsAllGlyphs(FontFace font, string text)
        {
            if (string.IsNullOrEmpty(text)) return true;
            foreach (System.Text.Rune rune in text.EnumerateRunes())
            {
                if (rune.Value is '\r' or '\n' or '\t')
                    continue;
                if (!FontContainsGlyph(font, rune.Value))
                    return false;
            }
            return true;
        }

        /// <summary>
        /// 获取 <paramref name="text"/> 中在 <paramref name="font"/> 内缺失字形的 Unicode 码点列表。
        /// </summary>
        public static IReadOnlyList<int> GetMissingGlyphs(FontFace font, string text)
        {
            if (string.IsNullOrEmpty(text)) return Array.Empty<int>();
            var missing = new List<int>();
            foreach (System.Text.Rune rune in text.EnumerateRunes())
            {
                if (rune.Value is '\r' or '\n' or '\t')
                    continue;
                if (!FontContainsGlyph(font, rune.Value))
                    missing.Add(rune.Value);
            }
            return missing;
        }

        /// <summary>
        /// 从字体文件路径加载字体，检测其是否包含 <paramref name="text"/> 内所有字符的字形。
        /// </summary>
        public static bool FontFileContainsAllGlyphs(string fontPath, string text, float fontSize = 14f)
        {
            if (!File.Exists(fontPath)) return false;
            try
            {
                using var fontFace = FontFace.Load(fontPath);
                return FontContainsAllGlyphs(fontFace, text);
            }
            catch
            {
                return false;
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

        private static TextLanguage DetectPrimaryLanguage(FontFace face)
        {
            var name = face.FamilyName?.ToLowerInvariant() ?? string.Empty;
            var result = name switch
            {
                string n when n.Contains("ja") || n.Contains("jp") => TextLanguage.Japanese,
                string n when n.Contains("kr") || n.Contains("ko") => TextLanguage.Korean,
                string n when n.Contains("ru") => TextLanguage.Russian,
                string n when n.Contains("th") => TextLanguage.Thai,
                string n when n.Contains("ar") => TextLanguage.Arabic,
                string n when n.Contains("zh") || n.Contains("sc") || n.Contains("tc") => TextLanguage.Chinese,
                _ => TextLanguage.English,
            };

            Log($"Font {face.FamilyName}: consider as {result}.");
            return result;
        }

        #endregion

        #region FontInfo

        public static IReadOnlyList<FontItem> BuildSystemFontItems(
            string? preferredLocale = null, string category = "system")
        {
            HashSet<string> fontFiles = ScanSystemFont();

            var tmpResult = new System.Collections.Concurrent.ConcurrentBag<FontItem>();

            // 性能优化：并行处理字体文件扫描，避免串行 I/O 阻塞
            // 1. 使用 ConcurrentBag 存储结果，支持多线程安全
            // 2. 限制并发数为逻辑处理器数的 50%，避免过度竞争
            // 3. 每个字体的全部操作在单个任务中完成，减少同步开销
            var parallelOptions = new ParallelOptions
            {
                MaxDegreeOfParallelism = Math.Max(1, System.Environment.ProcessorCount / 2)
            };

            Parallel.ForEach(fontFiles, parallelOptions, path =>
            {
                foreach (var item in CreateFontInfo(category, path))
                {
                    tmpResult.Add(item);
                }
            });

            // 合并结果并按显示名称排序
            var result = tmpResult.DistinctBy(C => C.DisplayName).ToList();
            return result;
        }

        public static IEnumerable<FontItem> CreateFontInfo(string category, string path)
        {
            try
            {
                bool ttcTried = false, ttfTried = false;
                if (Path.GetExtension(path).ToLower() == "ttc") goto ttc;
            ttf:
                try
                {
                    ttfTried = true;
                    var face = Drawing.Text.FontHelper.FontFace.Load(path);
                    return [new FontItem
                    {
                        FontName = face.UniqueName ?? $"{face.FamilyName} {face.SubfamilyName}",
                        DisplayName = $"{face.DisplayName} {LocalizeFontStyleName(face.SubfamilyName)}",
                        Path = path,
                        Category = category,
                        InnerFont = face,
                    }];
                }
                catch
                {
                    if (!ttcTried) goto ttc;
                }

            ttc:
                try
                {
                    ttcTried = true;
                    var faces = Drawing.Text.FontHelper.FontCollection.Load(path);
                    return faces.Select(c => c.Load()).Select(face => new FontItem
                    {
                        FontName = face.UniqueName ?? $"{face.FamilyName} {face.SubfamilyName}",
                        DisplayName = $"{face.DisplayName} {LocalizeFontStyleName(face.SubfamilyName)}",
                        Path = path,
                        Category = category,
                        InnerFont = face
                    });
                }
                catch
                {
                    if (!ttfTried) goto ttf;
                }
                return Enumerable.Empty<FontItem>();
            }
            catch
            {
                // 整个字体项加载失败，跳过此字体
            }

            return Enumerable.Empty<FontItem>();
        }

        public static HashSet<string> ScanSystemFont()
        {
            var fontFiles = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            if (OperatingSystem.IsWindows())
            {
                var sysDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "Fonts");
                try
                {
                    if (Directory.Exists(sysDir))
                    {
                        foreach (var f in (new[] { "*.ttf", "*.otf", "*.ttc" }).SelectMany(ext => Directory.GetFiles(sysDir, ext)))
                        {
                            fontFiles.Add(f);
                        }
                    }
                }
                catch { }

                if (OperatingSystem.IsWindowsVersionAtLeast(10, 0, 17763))
                {
                    var userDir = Path.Combine(
                        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                        "Microsoft", "Windows", "Fonts");
                    if (Directory.Exists(userDir))
                    {
                        try
                        {
                            foreach (var f in (new[] { "*.ttf", "*.otf", "*.ttc" }).SelectMany(ext => Directory.GetFiles(userDir, ext)))
                            {
                                fontFiles.Add(f);
                            }
                        }
                        catch { }
                    }
                }
            }
            else if (OperatingSystem.IsAndroid() || OperatingSystem.IsLinux())
            {
                foreach (var sysDir in new[] { "/system/fonts", "/system/product/fonts", "/data/fonts" })
                {
                    try
                    {
                        if (Directory.Exists(sysDir))
                        {
                            foreach (var f in (new[] { "*.ttf", "*.otf", "*.ttc" }).SelectMany(ext => Directory.GetFiles(sysDir, ext)))
                            {
                                fontFiles.Add(f);
                            }
                        }
                    }
                    catch { }
                }
            }

            return fontFiles;
        }

        public static string DummyString = "Lorem ipsum dolor sit amet, consectetur adipiscing elit. Curabitur est tortor, imperdiet et dui id, egestas hendrerit quam. Suspendisse ac felis a felis ultrices cursus a sit amet ligula. Praesent volutpat vitae dolor luctus rutrum. Vestibulum eu nibh magna. Maecenas vel tempus nunc. Donec vitae convallis odio. Donec nec mattis sapien.";

        #endregion
    }
}
