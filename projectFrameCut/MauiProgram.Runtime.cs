using projectFrameCut.Render.RenderAPIBase.Plugins;
using System.Reflection;
using System.Runtime.CompilerServices;

namespace projectFrameCut;

public static partial class MauiProgram
{
    internal static IReadOnlyList<IPluginBase> IntegratedPlugins { get; private set; } = [];

    public static StreamWriter LogWriter;

    public static string LogPath { get; private set; }

    public static string DataPath { get; private set; }

    public static string BasicDataPath { get; private set; }

    public static string CachePath { get; private set; }

    public static string FFmpegRoot { get; private set; }

    public static string ProgramConfig = "?", ProgramCommit = "?", AssemblyName = "projectFrameCut";

    public static string AppIdentifier =>
#if MAUISDK
        AppInfo.PackageName;
#else
        "hexadecimal0x12e.projectFrameCut";
#endif

    private static readonly string[] FoldersNeedInUserdata =
    [
        "My Drafts",
        "My Assets",
        "My Templates",
        "My Skills",
        "RenderCache",
#if WINDOWS
        "My Assets\\.database",
        "My Assets\\.thumbnails",
        "My Assets\\.perAssetThumb",
#else
        "My Assets/.database",
        "My Assets/.thumbnails",
        "My Assets/.perAssetThumb",
        "Logs",
        "AppData",
#endif
    ];

    public static string[] CmdlineArgs = Array.Empty<string>();

    public static bool IsStoreMode { get; private set; } = true;

    [ModuleInitializer]
    public static void LoadModuleConfig()
    {
        try
        {
            ProgramConfig = Assembly.GetExecutingAssembly().GetCustomAttribute<AssemblyConfigurationAttribute>()?.Configuration ?? "unknown config";
            ProgramCommit = (Assembly.GetExecutingAssembly().GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion ?? "0.1.2+unknown commit").Split('+').Last();
            AssemblyName = Assembly.GetExecutingAssembly().GetCustomAttribute<AssemblyTitleAttribute>()?.Title ?? "projectFrameCut";
            CachePath = GetInitialCachePath();
        }
        catch { }
    }

    private static string GetInitialCachePath()
    {
#if WINDOWS
        try
        {
            if (WinUI.App.IsPackaged())
                return Windows.Storage.ApplicationData.Current.LocalCacheFolder.Path;
        }
        catch { }
#endif
        return Path.Combine(Path.GetTempPath(), new string(AssemblyName.Select(t => char.IsAsciiLetterOrDigit(t) ? t : '_').ToArray()));
    }

}
