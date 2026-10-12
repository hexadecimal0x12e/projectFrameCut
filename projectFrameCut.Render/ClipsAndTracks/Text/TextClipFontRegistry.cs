using projectFrameCut.Drawing.Text.FontHelper;
using projectFrameCut.Drawing.Text.ImportExport;
using projectFrameCut.Drawing.Vector.ImportExport;
using System.Collections.Concurrent;
using System.Text;

namespace projectFrameCut.Render.ClipsAndTracks.Text;

/// <summary>
/// 字体注册表 —— 启动时仅扫描字体文件并缓存其名称元数据，
/// 直至字体被 <see cref="GetFont"/> / <see cref="TryGetFont"/> / <see cref="GetAllFonts"/>
/// 等 API 真正访问时才执行完整的 <see cref="FontFace.Load(string)"/>。
/// </summary>
public static class TextClipFontRegistry
{
    // ── 已完整加载的 FontFace ──────────────────────────────────────
    private static readonly ConcurrentDictionary<string, FontFace> LoadedFonts = new(StringComparer.OrdinalIgnoreCase);

    // ── 已扫描但尚未加载的字体（key → 文件路径 + 家族名） ──────────
    private static readonly ConcurrentDictionary<string, PendingFontInfo> PendingFonts = new(StringComparer.OrdinalIgnoreCase);

    // ── 已扫描过的路径（防重复） ─────────────────────────────────────
    private static readonly ConcurrentDictionary<string, byte> ScannedPaths = new(StringComparer.OrdinalIgnoreCase);

    private static bool _scanned;
    private static readonly object ScanLock = new();
    private static string? _fallbackFamilyName;

    /// <summary>
    /// 后备字体列表（仅从已加载字体的条目中填充，保持与旧版一致的行为）。
    /// </summary>
    public static List<FontFace> FallbackFonts { get; private set; } = [];

    /// <summary>
    /// 待加载字体的轻量元信息 —— 仅包含能将字体完整加载所需的文件路径
    /// 和用于模糊查找的家族名，不持有 <see cref="FontFace"/> 对象。
    /// </summary>
    private readonly struct PendingFontInfo(string filePath, string familyName, Dictionary<string, string> localizedNames, string subfamilyName = "")
    {
        public readonly string FilePath = filePath;
        public readonly string FamilyName = familyName;
        public readonly string SubfamilyName = subfamilyName;
        public readonly Dictionary<string, string> LocalizedNames = localizedNames;
    }

    // ════════════════════════════════════════════════════════════════
    //  公开 API
    // ════════════════════════════════════════════════════════════════

    /// <summary>
    /// 初始化字体注册表。
    /// - 已由外部加载好的 <paramref name="sysFonts"/> 直接加入缓存。
    /// - 目录扫描仅读取字体名称表（极轻量），不执行完整加载。
    /// 可重复调用；目录扫描只执行一次。
    /// </summary>
    public static void Initialize(IEnumerable<FontFace>? sysFonts = null)
    {
        // 外部已加载的字体始终接纳（可在初次扫描之后追加）
        if (sysFonts?.Any() ?? false)
        {
            foreach (var font in sysFonts.Where(f => f is not null))
            {
                var fontKey = font.UniqueName ?? $"{font.FamilyName} {font.SubfamilyName}";
                LoadedFonts.TryAdd(fontKey, font);
            }
        }

        // 目录扫描仅一次
        if (_scanned)
        {
            InitializeFallbackFonts();
            return;
        }
        lock (ScanLock)
        {
            if (_scanned) return;

            var baseDir = AppContext.BaseDirectory;
            if (Directory.Exists(baseDir))
            {
                foreach (var ttf in Directory.GetFiles(baseDir, "*.ttf"))
                {
                    RegisterPendingFont(ttf);
                }
            }

            _scanned = true;
        }
        InitializeFallbackFonts();
        SVGToVectorElement.TextImportHandler = SvgTextImport.CreateHandler(name =>
        {
            if (TryGetFont(name, out var f)) return f;
            if (LoadedFonts.FirstOrDefault(c => c.Value.LocalizedNames.Any(n => n.Value == name), new KeyValuePair<string, FontFace>(null!, null!)).Value is FontFace font) return font;
            if (PendingFonts.FirstOrDefault(c => c.Value.LocalizedNames.Any(n => n.Value == name), new KeyValuePair<string, PendingFontInfo>(null!, new(null!, null!, null!))).Value is PendingFontInfo info && !string.IsNullOrWhiteSpace(info.FilePath))
            {
                return LoadPendingFont(info.FilePath);
            }
            return FallbackFonts.FirstOrDefault() ?? null;
        });
    }

    private static void InitializeFallbackFonts()
    {
        if (FallbackFonts.Count > 0) return;
        lock (ScanLock)
        {
            if (FallbackFonts.Count > 0) return;
            foreach (string family in new[] { "HarmonyOS Sans SC", "HarmonyOS Sans TC", "Arial" })
            {
                var font = LoadedFonts.Values.FirstOrDefault(f => string.Equals(f.FamilyName, family, StringComparison.OrdinalIgnoreCase)
                    && string.Equals(f.SubfamilyName, "Regular", StringComparison.OrdinalIgnoreCase));
                if (font is null)
                {
                    var pending = PendingFonts.FirstOrDefault(p => string.Equals(p.Value.FamilyName, family, StringComparison.OrdinalIgnoreCase)
                        && string.Equals(p.Value.SubfamilyName, "Regular", StringComparison.OrdinalIgnoreCase));
                    if (pending.Key is not null) font = LoadPendingFont(pending.Key);
                }
                if (font is not null) FallbackFonts.Add(font);
            }
        }
    }

    /// <summary>
    /// 将指定路径的字体文件加入注册表（轻量登记，不立即加载）。
    /// </summary>
    public static void AddFont(string path)
    {
        if (string.IsNullOrWhiteSpace(path)) return;
        if (!ScannedPaths.TryAdd(path, 0)) return;
        RegisterPendingFont(path);
    }

    /// <summary>
    /// 注册一个已经完整加载的 <see cref="FontFace"/> 实例。
    /// </summary>
    public static void RegisterFontFace(FontFace fontFace)
    {
        if (fontFace is null) return;
        var fontKey = fontFace.UniqueName ?? $"{fontFace.FamilyName} {fontFace.SubfamilyName}";
        if (string.IsNullOrWhiteSpace(fontKey)) return;

        // 如果待加载队列里恰好有同名条目，移除它（已经直接拿到完整实例了）
        PendingFonts.TryRemove(fontKey, out _);

        LoadedFonts.AddOrUpdate(fontKey,
            _ => fontFace,
            (_, existing) =>
            {
                if (!ReferenceEquals(existing, fontFace))
                    existing.Dispose();
                return fontFace;
            });
    }

    /// <summary>
    /// 按字体名查找字体。若该字体尚未加载则触发懒加载。
    /// </summary>
    public static FontFace? GetFont(string familyName)
    {
        Initialize();
        if (LoadedFonts.TryGetValue(familyName, out var font))
            return font;
        return LoadPendingFont(familyName);
    }

    /// <summary>
    /// 按字体名查找字体，返回是否找到。若待加载队列中有匹配项则触发懒加载。
    /// 最终兜底：仅按 FamilyName 模糊匹配（不要求 SubfamilyName）。
    /// </summary>
    public static bool TryGetFont(string familyName, out FontFace? font)
    {
        Initialize();
        if (LoadedFonts.TryGetValue(familyName, out font))
            return true;

        // 精确 key 匹配 → 懒加载
        if (PendingFonts.ContainsKey(familyName))
        {
            font = LoadPendingFont(familyName);
            if (font is not null) return true;
        }

        // 已在加载字体中按 FamilyName 模糊查找
        font = LoadedFonts.Values.FirstOrDefault(f =>
            string.Equals(f.FamilyName, familyName, StringComparison.OrdinalIgnoreCase));
        if (font is not null) return true;

        // 待加载字体中按 FamilyName 模糊查找 → 加载后返回
        foreach (var kvp in PendingFonts)
        {
            if (string.Equals(kvp.Value.FamilyName, familyName, StringComparison.OrdinalIgnoreCase))
            {
                font = LoadPendingFont(kvp.Key);
                return font is not null;
            }
        }

        return false;
    }

    public static FontFace? ResolveFont(string name, string style)
    {
        if (!TryGetFont(name, out var font) || font is null) return null;
        string Normalize(string value) => value.Replace(" ", "").Replace("-", "").ToLowerInvariant();
        string requested = Normalize(style);
        if (requested == "regular" && LoadedFonts.ContainsKey(name)) return font;
        var variant = LoadedFonts.Values.FirstOrDefault(f =>
            string.Equals(f.FamilyName, font.FamilyName, StringComparison.OrdinalIgnoreCase) && Normalize(f.SubfamilyName) == requested);
        if (variant is not null) return variant;
        foreach (var kv in PendingFonts)
            if (string.Equals(kv.Value.FamilyName, font.FamilyName, StringComparison.OrdinalIgnoreCase) && Normalize(kv.Value.SubfamilyName) == requested)
                return LoadPendingFont(kv.Key) ?? font;
        return font;
    }

    /// <summary>
    /// 后备字体家族名 —— 在扫描时已确定，无需触发加载。
    /// </summary>
    public static string? FallbackFamilyName
    {
        get
        {
            Initialize();
            return _fallbackFamilyName;
        }
    }

    /// <summary>
    /// 获取全部已注册字体。此调用会强制加载所有尚未加载的 Pending 字体，
    /// 以确保调用方获得完整的 <see cref="FontFace"/> 列表。
    /// </summary>
    public static IReadOnlyList<FontFace> GetAllFonts()
    {
        Initialize();
        // 强制加载所有待加载字体
        foreach (var key in PendingFonts.Keys.ToArray())
        {
            LoadPendingFont(key);
        }
        return LoadedFonts.Values.ToList().AsReadOnly();
    }

    /// <summary>
    /// 清空所有字体缓存，重置注册表到未初始化状态。
    /// </summary>
    public static void Clear()
    {
        lock (ScanLock)
        {
            foreach (var font in LoadedFonts.Values)
            {
                try { font.Dispose(); } catch { }
            }
            LoadedFonts.Clear();
            PendingFonts.Clear();
            ScannedPaths.Clear();
            _fallbackFamilyName = null;
            _scanned = false;
            FallbackFonts.Clear();
        }
    }

    // ════════════════════════════════════════════════════════════════
    //  内部实现
    // ════════════════════════════════════════════════════════════════

    /// <summary>
    /// 扫描单个字体文件，解析其名称表中的元数据并登记为 Pending（不加载完整字体）。
    /// </summary>
    private static void RegisterPendingFont(string path)
    {
        try
        {
            ReadFontNameTable(path, out var uniqueName, out var familyName, out var subfamilyName);

            var fontKey = uniqueName ?? $"{familyName} {subfamilyName}";
            if (string.IsNullOrWhiteSpace(fontKey)) return;

            // 如果已通过其他途径加载过同名字体，跳过
            if (LoadedFonts.ContainsKey(fontKey)) return;

            PendingFonts.TryAdd(fontKey, new PendingFontInfo(path, familyName ?? string.Empty, GetLocalizedFontNames(path), subfamilyName ?? ""));

            // 记录后备字体家族名（与旧版 RegisterFont 逻辑一致但无需加载）
            if (_fallbackFamilyName is null)
                _fallbackFamilyName = fontKey;
            else if (string.Equals(familyName, "HarmonyOS Sans SC", StringComparison.OrdinalIgnoreCase)
                && string.Equals(subfamilyName, "Regular", StringComparison.OrdinalIgnoreCase))
                _fallbackFamilyName = fontKey;
        }
        catch (Exception ex)
        {
            projectFrameCut.Shared.Logger.Log(ex, $"Register text font {path}");
        }
    }

    /// <summary>
    /// 将 key 对应的待加载字体完整加载为 <see cref="FontFace"/>，
    /// 移出 Pending 表并移入 Loaded 表。
    /// </summary>
    private static FontFace? LoadPendingFont(string fontKey)
    {
        if (!PendingFonts.TryRemove(fontKey, out var info))
            return null;

        try
        {
            var fontFace = FontFace.Load(info.FilePath);
            return LoadedFonts.AddOrUpdate(fontKey,
                _ => fontFace,
                (_, existing) =>
                {
                    existing.Dispose();
                    return fontFace;
                });
        }
        catch (Exception ex)
        {
            projectFrameCut.Shared.Logger.Log(ex, $"Load text font {info.FilePath}");
            return null;
        }
    }

    // ════════════════════════════════════════════════════════════════
    //  TTF / OTF 名称表轻量解析器
    // ════════════════════════════════════════════════════════════════

    /// <summary>
    /// 从 TTF / OTF 文件中仅读取 'name' 表，提取字体标识信息。
    /// 不调用 <see cref="FontFace.Load(string)"/>，因此不加载完整的
    /// 字形数据、度量信息等，仅为构建查找索引的最小开销。
    /// </summary>
    private static void ReadFontNameTable(
        string path,
        out string? uniqueName,
        out string? familyName,
        out string? subfamilyName)
    {
        var meta = FontMetadataReader.ReadFromFile(path);
        uniqueName = meta.UniqueName;
        familyName = meta.FamilyName;
        subfamilyName = meta.SubfamilyName;
    }


    public static Dictionary<string, string> GetLocalizedFontNames(string fontPath)
    {
        if (string.IsNullOrEmpty(fontPath) || !File.Exists(fontPath))
        {
            string fallbackName = Path.GetFileNameWithoutExtension(fontPath ?? "");
            return new Dictionary<string, string> { { "en-US", fallbackName } };
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

            return localizedNames;
        }
        catch (Exception ex)
        {
            Log(ex, $"parsing font '{fontPath}'");
            string fallbackName = Path.GetFileNameWithoutExtension(fontPath ?? "");
            return new Dictionary<string, string> { { "en-US", fallbackName } };
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
        catch
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


}
