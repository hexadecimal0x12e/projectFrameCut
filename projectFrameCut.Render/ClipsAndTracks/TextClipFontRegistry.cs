using projectFrameCut.Drawing.Text.FontHelper;
using System.Collections.Concurrent;
using System.Linq;

namespace projectFrameCut.Render.ClipsAndTracks;

public static class TextClipFontRegistry
{
    private static readonly ConcurrentDictionary<string, FontFace> Fonts = new(StringComparer.OrdinalIgnoreCase);
    private static bool _initialized;
    private static readonly object InitLock = new();
    private static string? _fallbackFamilyName;

    private static readonly HashSet<string> FontPaths = new(StringComparer.OrdinalIgnoreCase);

    public static List<FontFace> FallbackFonts { get; private set; } = new List<FontFace>();

    public static void Initialize(IEnumerable<FontFace>? sysFonts = null)
    {
        if (sysFonts?.Any() ?? false)
        {
            foreach (var font in sysFonts.Where(font => font is not null))
            {
                var fontKey = font.UniqueName ?? $"{font.FamilyName} {font.SubfamilyName}";
                Fonts.TryAdd(fontKey, font);
            }
        }
        if (FallbackFonts?.Any() ?? false)
        {
            if (Fonts.TryGetValue("HarmonyOS Sans SC Medium", out var f1)) FallbackFonts.Add(f1);
            if (Fonts.TryGetValue("Arial Regular", out var f2)) FallbackFonts.Add(f2);
        }
        if (_initialized) return;

        lock (InitLock)
        {
            if (_initialized)
                return;

            var baseDir = AppContext.BaseDirectory;
            if (Directory.Exists(baseDir))
            {
                foreach (var ttf in Directory.GetFiles(baseDir, "*.ttf"))
                {
                    RegisterFont(ttf);
                }
            }

            _initialized = true;
        }
    }

    public static void AddFont(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
            return;

        if (!FontPaths.Add(path))
            return;

        RegisterFont(path);
    }

    public static void RegisterFontFace(FontFace fontFace)
    {
        if (fontFace is null) return;
        var fontKey = fontFace.UniqueName ?? $"{fontFace.FamilyName} {fontFace.SubfamilyName}";
        if (string.IsNullOrWhiteSpace(fontKey))
            return;

        Fonts.AddOrUpdate(fontKey,
            _ => fontFace,
            (_, existing) =>
            {
                if (!ReferenceEquals(existing, fontFace))
                    existing.Dispose();
                return fontFace;
            });
    }

    public static FontFace? GetFont(string familyName)
    {
        Initialize();
        return Fonts.TryGetValue(familyName, out var font) ? font : null;
    }
    public static bool TryGetFont(string familyName, out FontFace? font)
    {
        Initialize();
        if (Fonts.TryGetValue(familyName, out font))
            return true;

        //final fallback: try to find by family name only
        font = Fonts.Values.FirstOrDefault(f =>
            string.Equals(f.FamilyName, familyName, StringComparison.OrdinalIgnoreCase));
        return font is not null;
    }

    public static string? FallbackFamilyName
    {
        get
        {
            Initialize();
            return _fallbackFamilyName;
        }
    }

    public static IReadOnlyList<FontFace> GetAllFonts()
    {
        Initialize();
        return Fonts.Values.ToList().AsReadOnly();
    }

    public static void Clear()
    {
        lock (InitLock)
        {
            foreach (var font in Fonts.Values)
            {
                try { font.Dispose(); } catch { }
            }
            Fonts.Clear();
            FontPaths.Clear();
            _fallbackFamilyName = null;
            _initialized = false;
        }
    }

    private static void RegisterFont(string path)
    {
        try
        {
            var fontFace = FontFace.Load(path);
            var fontKey = fontFace.UniqueName ?? $"{fontFace.FamilyName} {fontFace.SubfamilyName}";

            if (string.IsNullOrWhiteSpace(fontKey))
            {
                fontFace.Dispose();
                return;
            }

            Fonts.AddOrUpdate(fontKey,
                _ => fontFace,
                (_, existing) =>
                {
                    existing.Dispose();
                    return fontFace;
                });

            if (_fallbackFamilyName is null)
            {
                _fallbackFamilyName = fontKey;
            }
            else if (fontFace.FamilyName.Contains("HarmonyOS_Sans_SC", StringComparison.OrdinalIgnoreCase))
            {
                _fallbackFamilyName = fontKey;
            }
        }
        catch
        {
            // skip unloadable font files
        }
    }
}
