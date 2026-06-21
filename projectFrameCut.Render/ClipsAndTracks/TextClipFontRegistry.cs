using projectFrameCut.Drawing.Text.FontHelper;
using System.Collections.Concurrent;
using System.Text;

namespace projectFrameCut.Render.ClipsAndTracks;

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
    private readonly struct PendingFontInfo(string filePath, string familyName)
    {
        public readonly string FilePath = filePath;
        public readonly string FamilyName = familyName;
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

        // 从已加载字体中尝试填充后备列表（原有语义，仅填充一次）
        if (FallbackFonts.Count == 0)
        {
            if (LoadedFonts.TryGetValue("HarmonyOS Sans SC Medium", out var f1))
                FallbackFonts.Add(f1);
            if (LoadedFonts.TryGetValue("Arial Regular", out var f2))
                FallbackFonts.Add(f2);
        }

        // 目录扫描仅一次
        if (_scanned) return;
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

            PendingFonts.TryAdd(fontKey, new PendingFontInfo(path, familyName ?? string.Empty));

            // 记录后备字体家族名（与旧版 RegisterFont 逻辑一致但无需加载）
            if (_fallbackFamilyName is null)
                _fallbackFamilyName = fontKey;
            else if (familyName?.Contains("HarmonyOS_Sans_SC", StringComparison.OrdinalIgnoreCase) == true)
                _fallbackFamilyName = fontKey;
        }
        catch
        {
            // 无法读取的字体文件直接跳过
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
        catch
        {
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
        uniqueName = null;
        familyName = null;
        subfamilyName = null;

        var bytes = File.ReadAllBytes(path);
        if (bytes.Length < 12) return;

        // ── 解析 Offset Table ──────────────────────────────────
        // 偏移  大小  字段
        //   0    4   sfVersion (uint32)
        //   4    2   numTables (uint16)
        //   6    2   searchRange
        //   8    2   entrySelector
        //  10    2   rangeShift
        //  12      → Table Directory 开始

        var numTables = (ushort)((bytes[4] << 8) | bytes[5]);
        var dirPos = 12;

        uint nameOffset = 0;
        for (var i = 0; i < numTables && dirPos + 16 <= bytes.Length; i++)
        {
            // tag (4) | checksum (4) | offset (4) | length (4)
            if (bytes[dirPos] == 0x6E &&  // 'n'
                bytes[dirPos + 1] == 0x61 &&  // 'a'
                bytes[dirPos + 2] == 0x6D &&  // 'm'
                bytes[dirPos + 3] == 0x65)    // 'e'
            {
                nameOffset = ReadU32BE(bytes, dirPos + 8);
                break;
            }
            dirPos += 16;
        }

        if (nameOffset == 0 || nameOffset >= (uint)bytes.Length) return;

        // ── 解析 Name Table ────────────────────────────────────
        // uint16 format | uint16 count | uint16 stringOffset
        var nameBase = (int)nameOffset;
        if (nameBase + 6 > bytes.Length) return;

        var count = (ushort)((bytes[nameBase + 2] << 8) | bytes[nameBase + 3]);
        var stringOffset = (ushort)((bytes[nameBase + 4] << 8) | bytes[nameBase + 5]);
        var stringsBase = nameBase + stringOffset;
        var recordPos = nameBase + 6;

        for (var i = 0; i < count && recordPos + 12 <= bytes.Length; i++)
        {
            var platformId = ReadU16BE(bytes, recordPos);
            var /*encodingId*/ _ = ReadU16BE(bytes, recordPos + 2);
            var /*languageId*/ __ = ReadU16BE(bytes, recordPos + 4);
            var nameId = ReadU16BE(bytes, recordPos + 6);
            var len = ReadU16BE(bytes, recordPos + 8);
            var off = ReadU16BE(bytes, recordPos + 10);

            var strStart = stringsBase + off;
            if (strStart < 0 || strStart + len > bytes.Length)
            {
                recordPos += 12;
                continue;
            }

            string? value = null;
            if (platformId == 3) // Windows: UTF-16BE
            {
                value = Encoding.BigEndianUnicode.GetString(bytes, strStart, len);
            }
            else if (platformId == 1 && value is null) // Mac: ASCII
            {
                value = Encoding.ASCII.GetString(bytes, strStart, len);
            }

            if (string.IsNullOrWhiteSpace(value))
            {
                recordPos += 12;
                continue;
            }

            switch (nameId)
            {
                case 1: familyName ??= value; break;
                case 2: subfamilyName ??= value; break;
                case 4: uniqueName ??= value; break;
                case 6: uniqueName ??= value; break; // PostScript name
            }

            recordPos += 12;
        }

        familyName ??= string.Empty;
        subfamilyName ??= string.Empty;
    }

    // ── 大端读取辅助 ───────────────────────────────────────────────

    private static ushort ReadU16BE(byte[] buffer, int offset)
        => (ushort)((buffer[offset] << 8) | buffer[offset + 1]);

    private static uint ReadU32BE(byte[] buffer, int offset)
        => (uint)((buffer[offset] << 24) | (buffer[offset + 1] << 16) | (buffer[offset + 2] << 8) | buffer[offset + 3]);
}
