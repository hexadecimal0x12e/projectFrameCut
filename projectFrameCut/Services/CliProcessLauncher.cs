using System.Diagnostics;
using System.Reflection;

namespace projectFrameCut.Services;

internal static class CliProcessLauncher
{
    public static IReadOnlyList<string> GetExecutableCandidates()
    {
#if HEADLESS
        if (string.IsNullOrWhiteSpace(Environment.ProcessPath)) return [];
        return Path.GetFileNameWithoutExtension(Environment.ProcessPath).Equals("dotnet", StringComparison.OrdinalIgnoreCase)
            ? [Assembly.GetExecutingAssembly().Location]
            : [Environment.ProcessPath];
#elif WINDOWS
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

    public static ProcessStartInfo CreateStartInfo(string executablePath, bool noConsole)
    {
        var info = new ProcessStartInfo(executablePath)
        {
            UseShellExecute = false,
            CreateNoWindow = noConsole,
            RedirectStandardError = noConsole,
            RedirectStandardOutput = noConsole,
        };
#if HEADLESS
        if (Path.GetExtension(executablePath).Equals(".dll", StringComparison.OrdinalIgnoreCase))
        {
            info.FileName = Environment.ProcessPath ?? "dotnet";
            info.ArgumentList.Add(executablePath);
        }
#endif
        return info;
    }
}
