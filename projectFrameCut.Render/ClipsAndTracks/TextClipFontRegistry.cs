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

    public static FontFace? GetFont(string familyName)
    {
        Initialize();
        return Fonts.TryGetValue(familyName, out var font) ? font : null;
    }

    public static bool TryGetFont(string familyName, out FontFace? font)
    {
        Initialize();
        return Fonts.TryGetValue(familyName, out font);
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
