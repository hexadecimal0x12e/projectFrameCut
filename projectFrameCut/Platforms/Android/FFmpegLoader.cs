#if ANDROID
using Java.Lang;

namespace projectFrameCut.Platforms.Android;

internal static class FFmpegLoader
{
    static bool _loaded;
    static readonly string[] Libs = new[]
    {
        "avutil",
        "swresample",
        "swscale",
        "avcodec",
        "avformat",
        "avfilter",
        "avdevice"
    };

    // 如需 x264/openh264，请放在最前或最后加载都可
    static readonly string[] OptionalCodecLibs = new[]
    {
        "openh264",
        "dav1d",
        
    };

    public static void EnsureLoaded()
    {
        if (_loaded) return;

        // 可选先加载编码器依赖
        foreach (var l in OptionalCodecLibs)
        {
            try { JavaSystem.LoadLibrary(l); } catch { /* ignore if not present */ }
        }

        foreach (var l in Libs)
        {
            try
            {
                JavaSystem.LoadLibrary(l);
                Log($"succeed to Load Library {l} ");

            }
            catch (UnsatisfiedLinkError ex)
            {
                Log($"LoadLibrary {l} failed: {ex.Message}","error");
                //throw;
            }
        }

        _loaded = true;
    }
}
#endif