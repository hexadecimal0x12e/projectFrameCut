using SixLabors.Fonts;
using Font = SixLabors.Fonts.Font;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using projectFrameCut.ApplicationAPIBase.Views.Pickers;
using static projectFrameCut.ApplicationAPIBase.LocalizedResources.APIBaseLocalizedResources;
using projectFrameCut.Shared;

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
                Font font = SystemFonts.CreateFont(SystemFonts.Families.First().Name, fontSize);
                FontRectangle rect = TextMeasurer.MeasureSize(text, new TextOptions(font));
                return rect.Width > 0 ? rect.Width : 100;
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
        /// 检测 <see cref="Font"/> 中是否存在指定字符的字形（Glyph）。
        /// </summary>
        /// <param name="font">要检测的 SixLabors 字体对象</param>
        /// <param name="character">要检测的字符（Unicode 码点 ≤ U+FFFF）</param>
        /// <returns>字体包含该字符的字形时返回 <c>true</c>，否则返回 <c>false</c></returns>
        public static bool FontContainsGlyph(Font font, char character)
            => HasRenderableGlyph(font, new SixLabors.Fonts.Unicode.CodePoint(character));

        /// <summary>
        /// 检测 <see cref="Font"/> 中是否存在指定 Unicode 码点的字形（支持辅助平面字符，如 Emoji）。
        /// </summary>
        /// <param name="font">要检测的 SixLabors 字体对象</param>
        /// <param name="codePoint">Unicode 码点（如 0x1F600 表示 😀）</param>
        /// <returns>字体包含该码点的字形时返回 <c>true</c>，否则返回 <c>false</c></returns>
        public static bool FontContainsGlyph(Font font, int codePoint)
            => HasRenderableGlyph(font, new SixLabors.Fonts.Unicode.CodePoint(codePoint));

        private static bool HasRenderableGlyph(Font font, SixLabors.Fonts.Unicode.CodePoint codePoint)
        {
            if (!font.TryGetGlyphs(codePoint, out var glyphs) || glyphs is null || glyphs.Count == 0)
            {
                return false;
            }

            // TryGetGlyphs 在缺字时仍可能返回 true，但 glyphId=0 表示 .notdef（缺失字形占位）
            return glyphs.All(g => g.GlyphMetrics.GlyphId != 0);
        }

        /// <summary>
        /// 检测 <see cref="Font"/> 中是否包含 <paramref name="text"/> 内所有字符的字形。
        /// </summary>
        /// <param name="font">要检测的 SixLabors 字体对象</param>
        /// <param name="text">要检测的文本；为 null 或空时返回 <c>true</c></param>
        /// <returns>字体包含文本内所有字符的字形时返回 <c>true</c>；任意字符缺失时返回 <c>false</c></returns>
        public static bool FontContainsAllGlyphs(Font font, string text)
        {
            if (string.IsNullOrEmpty(text)) return true;
            foreach (System.Text.Rune rune in text.EnumerateRunes())
            {
                if (rune.Value is '\r' or '\n' or '\t')
                {
                    continue;
                }

                if (!HasRenderableGlyph(font, new SixLabors.Fonts.Unicode.CodePoint(rune.Value)))
                    return false;
            }
            return true;
        }

        /// <summary>
        /// 获取 <paramref name="text"/> 中在 <paramref name="font"/> 内缺失字形的 Unicode 码点列表。
        /// </summary>
        /// <param name="font">要检测的 SixLabors 字体对象</param>
        /// <param name="text">要检测的文本</param>
        /// <returns>缺失字形的码点列表；若字体完整支持则返回空列表</returns>
        public static IReadOnlyList<int> GetMissingGlyphs(Font font, string text)
        {
            if (string.IsNullOrEmpty(text)) return Array.Empty<int>();
            var missing = new List<int>();
            foreach (System.Text.Rune rune in text.EnumerateRunes())
            {
                if (rune.Value is '\r' or '\n' or '\t')
                {
                    continue;
                }

                if (!HasRenderableGlyph(font, new SixLabors.Fonts.Unicode.CodePoint(rune.Value)))
                    missing.Add(rune.Value);
            }
            return missing;
        }

        /// <summary>
        /// 从字体文件路径加载字体，检测其是否包含 <paramref name="text"/> 内所有字符的字形。
        /// </summary>
        /// <param name="fontPath">字体文件路径（.ttf / .otf / .ttc）</param>
        /// <param name="text">要检测的文本</param>
        /// <param name="fontSize">加载字体时使用的磅值，默认 14</param>
        /// <returns>字体包含全部字形时返回 <c>true</c>；文件不存在或字符缺失时返回 <c>false</c></returns>
        public static bool FontFileContainsAllGlyphs(string fontPath, string text, float fontSize = 14f)
        {
            if (!File.Exists(fontPath)) return false;
            try
            {
                var collection = new FontCollection();
                var family = collection.Add(fontPath);
                var font = family.CreateFont(fontSize);
                return FontContainsAllGlyphs(font, text);
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

        #endregion

        #region FontInfo
        public sealed record FontFileInfo
        {
            public string EnglishName { get; init; } = string.Empty;

            public string DisplayName { get; init; } = string.Empty;

            public IReadOnlyDictionary<string, string> LocalizedNames { get; init; }
                = new Dictionary<string, string>();

            public TextLanguage PrimaryLanguage { get; init; } = TextLanguage.Unknown;

            public IReadOnlyList<TextLanguage> SupportedLanguages { get; init; }
                = Array.Empty<TextLanguage>();
        }

        /// <summary>
        /// 直接解析 OpenType/TrueType 字体文件，无需系统 API 或命名猜测，即可获得：<br/>
        /// • 多语言显示名称（<c>name</c> 表，nameID = 16/1/4，Platform 3 Windows UTF-16 BE 或 Platform 1 Mac Latin）<br/>
        /// • 主要服务语言（<c>name</c> 表 languageID 集合 → <see cref="TextLanguage"/>）<br/>
        /// • 完整 Unicode 区段支持列表（<c>OS/2</c> <c>ulUnicodeRange</c> 位图，OpenType 规范 §OS/2）
        /// </summary>
        /// <param name="fontPath">字体文件完整路径（.ttf / .otf / .ttc 均支持）</param>
        /// <param name="preferredLocale">
        ///   偏好语言标签（如 "zh-CN"、"ja-JP"）；为 null 时使用当前 UI 区域，再回退至 "en-US"。
        /// </param>
        public static FontFileInfo ReadFontFileInfo(string fontPath, string? preferredLocale = null)
        {
            if (string.IsNullOrEmpty(fontPath) || !File.Exists(fontPath))
            {
                string fallbackName = Path.GetFileNameWithoutExtension(fontPath ?? "");
                return new FontFileInfo { EnglishName = fallbackName, DisplayName = fallbackName };
            }

            try
            {
                using var fs = File.OpenRead(fontPath);
                using var reader = new BinaryReader(fs, Encoding.UTF8, leaveOpen: false);

                // ── 0. 解析文件头，处理 TTC 集合（取第一个字体）──────────────
                uint sfVersion = ReadUInt32BE(reader);
                if (sfVersion == 0x74746366u) // 'ttcf' = TrueType Collection
                {
                    // TTC 头：TTCTag(4) + Version(4) + numFonts(4) + OffsetTable[0](4...)
                    // 已读走 TTCTag(4 字节)，还需跳过 Version(4) + numFonts(4) = 8 字节，
                    // 然后读 OffsetTable[0] 得到第一个字体的 sfnt 头偏移。
                    reader.BaseStream.Seek(12, SeekOrigin.Begin);  // 跳过 TTCTag+Version+numFonts
                    uint firstOffset = ReadUInt32BE(reader);
                    reader.BaseStream.Seek(firstOffset, SeekOrigin.Begin);
                    sfVersion = ReadUInt32BE(reader);
                }

                ushort numTables = ReadUInt16BE(reader);
                reader.BaseStream.Seek(6, SeekOrigin.Current);     // searchRange / entrySelector / rangeShift

                // ── 1. 读取表目录 ─────────────────────────────────────────────
                var tables = new Dictionary<string, (uint offset, uint length)>(StringComparer.Ordinal);
                for (int i = 0; i < numTables; i++)
                {
                    string tag = new string(reader.ReadChars(4));
                    reader.BaseStream.Seek(4, SeekOrigin.Current); // checkSum
                    uint tblOffset = ReadUInt32BE(reader);
                    uint tblLength = ReadUInt32BE(reader);
                    tables[tag] = (tblOffset, tblLength);
                }

                // ── 2. 解析 name 表（多语言显示名称）───────────────────────────
                var localizedNames = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                var nameLangIds = new HashSet<ushort>();
                if (tables.TryGetValue("name", out var nameTable))
                {
                    reader.BaseStream.Seek(nameTable.offset, SeekOrigin.Begin);
                    ReadUInt16BE(reader);                          // format（0 或 1）
                    ushort nameCount = ReadUInt16BE(reader);
                    ushort stringOffset = ReadUInt16BE(reader);
                    long strBase = nameTable.offset + stringOffset;

                    var nameRecords = new List<(ushort plat, ushort enc, ushort lang, ushort nid, ushort len, ushort off)>();
                    for (int i = 0; i < nameCount; i++)
                    {
                        ushort plat = ReadUInt16BE(reader);
                        ushort enc = ReadUInt16BE(reader);
                        ushort lang = ReadUInt16BE(reader);
                        ushort nid = ReadUInt16BE(reader);
                        ushort len = ReadUInt16BE(reader);
                        ushort off = ReadUInt16BE(reader);
                        nameRecords.Add((plat, enc, lang, nid, len, off));
                    }

                    // nameID 优先级：16（Preferred Family）> 1（Family）> 4（Full Name）
                    bool gotPreferred = false;
                    foreach (ushort targetId in new ushort[] { 16, 1, 4 })
                    {
                        if (targetId == 1 && gotPreferred) break;
                        bool anyThisRound = false;
                        foreach (var rec in nameRecords)
                        {
                            if (rec.nid != targetId) continue;
                            if (rec.plat != 3 && rec.plat != 1) continue; // Platform 3=Windows, 1=Mac

                            string? name = ReadNameString(reader, strBase, rec.off, rec.len, rec.plat, rec.enc);
                            if (string.IsNullOrWhiteSpace(name)) continue;

                            string localeTag = WindowsLangIdToBcp47(rec.lang, rec.plat);
                            if (!localizedNames.ContainsKey(localeTag))
                            {
                                localizedNames[localeTag] = name;
                                anyThisRound = true;
                            }
                            if (rec.plat == 3)
                                nameLangIds.Add(rec.lang);
                        }
                        if (anyThisRound && targetId == 16) gotPreferred = true;
                    }
                }

                // ── 3. 解析 OS/2 表（Unicode Range 位图）─────────────────────
                var supportedLanguages = new List<TextLanguage>();
                TextLanguage os2Primary = TextLanguage.Unknown;
                if (tables.TryGetValue("OS/2", out var os2Table))
                {
                    reader.BaseStream.Seek(os2Table.offset + 42, SeekOrigin.Begin); // ulUnicodeRange1 在 offset 42
                    uint ur1 = ReadUInt32BE(reader); // bits 0–31
                    uint ur2 = ReadUInt32BE(reader); // bits 32–63
                    uint ur3 = ReadUInt32BE(reader); // bits 64–95

                    // OpenType 规范 §OS/2 ulUnicodeRange 位定义（截取常用位）：
                    bool hasBasicLatin = (ur1 & (1u << 0)) != 0; // U+0020-007E
                    bool hasCyrillic = (ur1 & (1u << 9)) != 0; // U+0400-04FF → Russian
                    bool hasArabic = (ur1 & (1u << 13)) != 0; // U+0600-06FF → Arabic
                    bool hasThai = (ur1 & (1u << 24)) != 0; // U+0E00-0E7F → Thai
                    bool hasHangulJamo = (ur1 & (1u << 28)) != 0; // U+1100-11FF → Korean

                    // ur2 存储总体 bit 32-63，bit 52-54、59 等为 CJK 区段：
                    bool hasHiragana = (ur2 & (1u << 20)) != 0; // bit 52：U+3040-309F → Japanese
                    bool hasKatakana = (ur2 & (1u << 21)) != 0; // bit 53：U+30A0-30FF → Japanese
                    bool hasBopomofo = (ur2 & (1u << 22)) != 0; // bit 54：注音→ Chinese
                    bool hasCJK = (ur2 & (1u << 27)) != 0; // bit 59：U+4E00-9FFF CJK Unified Ideographs

                    bool hasHangulSyll = (ur3 & (1u << 6)) != 0; // bit 70：U+AC00-D7AF Hangul Syllables → Korean

                    bool isJapanese = hasHiragana || hasKatakana;
                    bool isKorean = hasHangulJamo || hasHangulSyll;
                    bool isChinese = hasCJK || hasBopomofo;

                    if (isJapanese) supportedLanguages.Add(TextLanguage.Japanese);
                    if (isKorean) supportedLanguages.Add(TextLanguage.Korean);
                    if (isChinese && !isJapanese) supportedLanguages.Add(TextLanguage.Chinese);  // 纯中文字体
                    if (isChinese && isJapanese) supportedLanguages.Add(TextLanguage.Chinese);  // 日文字体也带中文
                    if (hasCyrillic) supportedLanguages.Add(TextLanguage.Russian);
                    if (hasArabic) supportedLanguages.Add(TextLanguage.Arabic);
                    if (hasThai) supportedLanguages.Add(TextLanguage.Thai);
                    if (hasBasicLatin && supportedLanguages.Count == 0)
                        supportedLanguages.Add(TextLanguage.English);
                    if (supportedLanguages.Count == 0)
                        supportedLanguages.Add(TextLanguage.English);

                    os2Primary = supportedLanguages[0]; // 按添加顺序第一个即为主要语言
                }

                // ── 4. 综合判断主要语言（name 表 languageID 更权威）────────────
                TextLanguage primaryLang = InferLangFromNameLangIds(nameLangIds, os2Primary);
                if (primaryLang != TextLanguage.Unknown && !supportedLanguages.Contains(primaryLang))
                    supportedLanguages.Insert(0, primaryLang);

                // ── 5. 选出最佳显示名称 ───────────────────────────────────────
                string locale = preferredLocale ?? Localized?._LocaleId_ ?? "en-US";
                string displayName = PickBestFontName(localizedNames, preferredLocale ?? ToLanguageCode(primaryLang, true));
                string englishName = PickBestFontName(localizedNames, "en-US");
                if (string.IsNullOrEmpty(englishName))
                    englishName = Path.GetFileNameWithoutExtension(fontPath);
                if (string.IsNullOrEmpty(displayName))
                    displayName = englishName;

                return new FontFileInfo
                {
                    EnglishName = englishName,
                    DisplayName = displayName,
                    LocalizedNames = localizedNames,
                    PrimaryLanguage = primaryLang,
                    SupportedLanguages = supportedLanguages.Distinct().ToList(),
                };
            }
            catch (Exception ex)
            {
                Log(ex, $"ReadFontFileInfo: parsing '{fontPath}'");
                string fallback = Path.GetFileNameWithoutExtension(fontPath);
                return new FontFileInfo { EnglishName = fallback, DisplayName = fallback };
            }
        }

        // ── 辅助：按 platformId/encodingId 解码 name 表字符串 ──────────────
        private static string? ReadNameString(BinaryReader reader, long strBase, ushort strOff, ushort strLen, ushort platformId, ushort encodingId)
        {
            long pos = strBase + strOff;
            if (pos < 0 || pos + strLen > reader.BaseStream.Length) return null;
            reader.BaseStream.Seek(pos, SeekOrigin.Begin);
            byte[] bytes = reader.ReadBytes(strLen);
            try
            {
                return (platformId, encodingId) switch
                {
                    (3, 1) => Encoding.BigEndianUnicode.GetString(bytes), // Windows Unicode BMP (UTF-16 BE)
                    (3, _) => Encoding.BigEndianUnicode.GetString(bytes),
                    (0, _) => Encoding.BigEndianUnicode.GetString(bytes), // Unicode platform
                    (1, 0) => Encoding.Latin1.GetString(bytes), // actually Mac Roman doesn't exist
                    _ => Encoding.BigEndianUnicode.GetString(bytes),
                };
            }
            catch (Exception ex ) 
            {
                return Encoding.Latin1.GetString(bytes);
            }
        }

        // ── 辅助：OpenType Platform 3 languageID → BCP-47 语言标签 ──────
        private static string WindowsLangIdToBcp47(ushort langId, ushort platformId)
        {
            if (platformId == 1) return "en"; // Mac platform 简单归 en
            return langId switch
            {
                0x0404 => "zh-TW",
                0x0804 => "zh-CN",
                0x0C04 => "zh-HK",
                0x1404 => "zh-MO",
                0x1004 => "zh-SG",
                0x0411 => "ja",
                0x0412 => "ko",
                0x0419 => "ru",
                0x041E => "th",
                0x0401 => "ar-SA",
                0x0801 => "ar-IQ",
                0x0C01 => "ar-EG",
                0x0409 => "en-US",
                0x0809 => "en-GB",
                0x0C09 => "en-AU",
                0x0407 => "de",
                0x040C => "fr",
                0x0C0A => "es",
                0x0410 => "it",
                0x0416 => "pt-BR",
                0x0816 => "pt-PT",
                _ => $"x-lcid-{langId:X4}",
            };
        }

        // ── 辅助：从 name 表出现的 Windows languageID 集合推断字体主语言 ─
        private static TextLanguage InferLangFromNameLangIds(IEnumerable<ushort> langIds, TextLanguage os2Fallback)
        {
            bool hasJa = false, hasKo = false, hasZh = false;
            bool hasRu = false, hasTh = false, hasAr = false, hasEn = false;
            foreach (ushort id in langIds)
            {
                switch (id)
                {
                    case 0x0411: hasJa = true; break;
                    case 0x0412: hasKo = true; break;
                    case 0x0404:
                    case 0x0804:
                    case 0x0C04:
                    case 0x1004:
                    case 0x1404: hasZh = true; break;
                    case 0x0419: hasRu = true; break;
                    case 0x041E: hasTh = true; break;
                }
                // Arabic LCID 系列：低字节 0x01
                if (!hasAr && (id & 0xFF) == 0x01 && id >= 0x0401 && id <= 0x1C01) hasAr = true;
                // 英文 LCID 系列：0x0409(en-US) 0x0809(en-GB) 0x0C09(en-AU) 等，低字节 0x09
                if (!hasEn && (id & 0xFF) == 0x09) hasEn = true;
            }
            if (hasJa) return TextLanguage.Japanese;
            if (hasKo) return TextLanguage.Korean;
            if (hasZh) return TextLanguage.Chinese;
            if (hasRu) return TextLanguage.Russian;
            if (hasTh) return TextLanguage.Thai;
            if (hasAr) return TextLanguage.Arabic;
            // name 表中只有英文条目时，应返回 English，
            // 而不是因 OS/2 附带 Cyrillic 支持就误判为 Russian
            if (hasEn) return TextLanguage.English;
            return os2Fallback;
        }

        // ── 辅助：从 localizedNames 按偏好语言选名称 ─────────────────────
        private static string PickBestFontName(Dictionary<string, string> names, string preferredLocale)
        {
            if (names.Count == 0) return string.Empty;
            if (names.TryGetValue(preferredLocale, out var exact)) return exact;
            string primary = preferredLocale.Split('-')[0];
            var partial = names.FirstOrDefault(kv => kv.Key.StartsWith(primary, StringComparison.OrdinalIgnoreCase));
            if (partial.Value is not null) return partial.Value;
            if (names.TryGetValue("en-US", out var enUs)) return enUs;
            var enAny = names.FirstOrDefault(kv => kv.Key.StartsWith("en", StringComparison.OrdinalIgnoreCase));
            if (enAny.Value is not null) return enAny.Value;
            return names.Values.First();
        }

        // ── 辅助：大端序读取 ──────────────────────────────────────────────
        private static uint ReadUInt32BE(BinaryReader r) { var b = r.ReadBytes(4); return (uint)(b[0] << 24 | b[1] << 16 | b[2] << 8 | b[3]); }
        private static ushort ReadUInt16BE(BinaryReader r) { var b = r.ReadBytes(2); return (ushort)(b[0] << 8 | b[1]); }

        // ─────────────────────────────────────────────────────────────────────

        /// <summary>
        /// 从字体文件路径构造 <see cref="FontItem"/>，
        /// 自动填充本地化显示名称（<c>DisplayName</c>）和主语言标签（<c>PrimaryLanguageTag</c>）。<br/>
        /// 内部调用 <see cref="ReadFontFileInfo"/>，无需额外 API 或命名猜测。
        /// </summary>
        /// <param name="fontPath">字体文件路径</param>
        /// <param name="fontFamilyName">已知的 SixLabors 家族名（用于 <c>FontName</c>）；为 null 时用文件名</param>
        /// <param name="category">分类标签</param>
        /// <param name="preferredLocale">偏好语言；为 null 时用当前 UI 区域</param>
        public static FontItem CreateFontItem(
            string fontPath,
            string? fontFamilyName = null,
            string? category = null,
            string? preferredLocale = null)
        {
            var info = ReadFontFileInfo(fontPath, preferredLocale);
            return new FontItem
            {
                FontName = fontFamilyName ?? info.EnglishName,
                DisplayName = info.DisplayName,
                PrimaryLanguageTag = ToLanguageCode(info.PrimaryLanguage),
                Category = category ?? string.Empty,
            };
        }

        public static IReadOnlyList<FontItem> BuildSystemFontItems(
            string? preferredLocale = null, string category = "system")
        {
            HashSet<string> fontFiles = ScanSystemFont();

            var seenNames = new System.Collections.Concurrent.ConcurrentDictionary<string, bool>(StringComparer.OrdinalIgnoreCase);
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
                FontFileInfo? info = null;

                try
                {
                    // 第一步：读取字体文件元数据（主要耗时操作）
                    info = ReadFontFileInfo(path, preferredLocale);
                    if (info == null || !seenNames.TryAdd(info.EnglishName, true))
                        return;

                    // 第二步：创建 FontCollection（内存密集，失败时继续）
                    FontCollection? fontCollection = null;
                    try
                    {
                        fontCollection = new FontCollection();
                        fontCollection.Add(path);
                    }
                    catch
                    {
                        // FontCollection 创建失败不影响继续，保留为 null
                    }

                    // 第三步：创建结果对象
                    var fontItem = new FontItem
                    {
                        FontName = info.EnglishName,
                        DisplayName = info.DisplayName,
                        PrimaryLanguageTag = ToLanguageCode(info.PrimaryLanguage),
                        Category = category,
                        InnerItem = info,
                        InnerFont = fontCollection,
                        Path = path,
                    };

                    tmpResult.Add(fontItem);
                }
                catch
                {
                    // 整个字体项加载失败，跳过此字体
                }
            });

            // 合并结果并按显示名称排序
            var result = tmpResult.ToList();
            return result;
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
