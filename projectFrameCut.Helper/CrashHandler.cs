using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Text;
using static projectFrameCut.Shared.Logger;
namespace projectFrameCut.Helper
{
    public static class CrashHandler
    {
        public static Process? Handler { get; private set; }
        private static readonly object _handlerLock = new();
        private static string? _handlerProjectPath;

        public static void CrashHandlerMain(string[] args)
        {
            if (!OperatingSystem.IsWindows()) return;
            if (args.Length != 2) return;
            if(!int.TryParse(args[0], out var parentPID)) return;
            var launchTarget = args[1];
            if (string.IsNullOrWhiteSpace(launchTarget)) return;
            Log($"CrashHandler: parent {parentPID}, launch target {launchTarget}");
            Process parent;
            try
            {
                parent = Process.GetProcessById(parentPID);
            }
            catch (Exception ex)
            {
                Log(ex, "CrashHandler: Cannot resolve parent process");
                return;
            }
            Log($"CrashHandler: Start wait for parent crashing...");
            try
            {
                parent.WaitForExit();
                Log($"CrashHandler: Parent crashed. Try rebooting...");
                new Thread(() => _ = HelperProgram.MessageBox(0, SimpleLocalizerBaseGeneratedHelper.Localized?.CrashForm_AutoRebootSoon() ?? $"projectFrameCut has crashed. Program will be automatically reboot soon. To disable this feature, go Settings-General-No reboot after crash.", "projectFrameCut", 0x00000040)).Start();
                Process.Start(CreateRebootStartInfo(launchTarget));
            }
            catch (Exception ex)
            {
                Log(ex, "CrashHandler: Wait parent process exit failed");
                return;
            }

        }

        public static void BootHandler(string workingProject, bool diag = false)
        {
            if (!OperatingSystem.IsWindows()) return;
            if (string.IsNullOrWhiteSpace(workingProject)) return;
            var projectPath = Path.GetFullPath(workingProject);
            var helperPath = Path.Combine(AppContext.BaseDirectory, "projectFrameCut.Helper.dll");
            if (!File.Exists(helperPath))
            {
                Log($"CrashHandler: Helper not found at {helperPath}", "fatal");
                return;
            }

            lock (_handlerLock)
            {
                try
                {
                    if (Handler is not null && !Handler.HasExited)
                    {
                        if (string.Equals(_handlerProjectPath, projectPath, StringComparison.OrdinalIgnoreCase))
                        {
                            return;
                        }

                        try
                        {
                            Handler.Kill(entireProcessTree: true);
                            Handler.WaitForExit(1000);
                        }
                        catch (Exception ex)
                        {
                            Log(ex, "CrashHandler: Failed to stop old crash handler process");
                        }
                    }
                }
                catch (Exception ex)
                {
                    Log(ex, "CrashHandler: Failed to inspect previous crash handler process");
                }

                var launchTarget = BuildCrashRebootLaunchTarget(projectPath);
                var bootArgs = $"\"{helperPath}\" crashHandler {Process.GetCurrentProcess().Id} \"{launchTarget}\" ";
                LogDiagnostic($"CrashHandler boot args: dotnet.exe {bootArgs}");
                var p = Process.Start(new ProcessStartInfo
                {
                    FileName = "dotnet.exe",
                    Arguments = bootArgs,
                    UseShellExecute = false,
                    CreateNoWindow = !diag
                });
                if (p is null)
                {
                    Log($"CrashHandler: Failed to start crash handler process!", "fatal");
                    return;
                }

                Handler = p;
                _handlerProjectPath = projectPath;

                if (!p.HasExited)
                {
                    LogDiagnostic($"CrashHandler: Crash handler started with PID {p.Id}");
                }
            }
        }

        private static string BuildCrashRebootLaunchTarget(string projectPath)
        {
            try
            {
                var fileUri = new Uri(Path.Combine(projectPath,"project.pjfc"));
                var query = $"{Uri.EscapeDataString("--fromCrashHandler")}&{Uri.EscapeDataString("--noSplash")}";
                return $"pjfc:{fileUri.AbsoluteUri}?{query}";
            }
            catch (Exception ex)
            {
                Log(ex, "CrashHandler: Build reboot URI failed, fallback to legacy argument mode");
                return projectPath;
            }
        }

        private static ProcessStartInfo CreateRebootStartInfo(string launchTarget)
        {
            if (launchTarget.StartsWith("pjfc:", StringComparison.OrdinalIgnoreCase))
            {
                return new ProcessStartInfo
                {
                    FileName = launchTarget,
                    UseShellExecute = true,
                };
            }

            return new ProcessStartInfo
            {
                FileName = "pjfc:",
                Arguments = $"\"{launchTarget}\" --fromCrashHandler --noSplash",
                UseShellExecute = true,
            };
        }
    }
}
