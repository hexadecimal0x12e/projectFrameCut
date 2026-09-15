using System.Diagnostics;
using System.Reflection;

namespace projectFrameCut.Services;

internal static class CliProcessLauncher
{
    public static IReadOnlyList<string> GetExecutableCandidates()
    {
#if WINDOWS
        return
        [
            Path.Combine(AppContext.BaseDirectory, "pjfc-cli.exe"),
            $"projectFrameCutCompatible_{AppInfo.PackageName}_{Assembly.GetExecutingAssembly().GetName().Version}.exe",
        ];
#elif MACOS
        return [Path.Combine(Foundation.NSBundle.MainBundle.BundlePath, "Contents", "MacOS", "projectFrameCut_cli")];
#elif LINUX
        return [Path.Combine(AppContext.BaseDirectory, "projectFrameCut")];
#else
        return [];
#endif
    }

    public static ProcessStartInfo CreateStartInfo(string executablePath, bool noConsole) => new()
    {
        FileName = executablePath,
        UseShellExecute = false,
        CreateNoWindow = noConsole,
        RedirectStandardError = noConsole,
        RedirectStandardOutput = noConsole,
    };
}
