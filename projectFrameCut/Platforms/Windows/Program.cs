using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;

namespace projectFrameCut.WinUI
{
    public static class Program
    {
        public static bool LogWindowShowing = false;

        [STAThread] //avoid failed to initialize COM library error, cause a lot of issue like IME not work at all...
        static void Main(string[] args)
        {
            System.Threading.Thread.CurrentThread.Name = "App Main thread";
            try
            {
                projectFrameCut.Helper.HelperProgram.AppVersion = AppInfo.Version.ToString();
                var splash = new Thread(projectFrameCut.Helper.HelperProgram.SplashMain);
                splash.Priority = ThreadPriority.Highest;
                splash.IsBackground = false;
                splash.Start();
                try
                {
                    if (args.Any(c => c.StartsWith("--overrideCulture")))
                    {
                        var overrideCulture = args.First(c => c.StartsWith("--overrideCulture")).Split('=')[1];
                        var culture = new System.Globalization.CultureInfo(overrideCulture);
                        System.Globalization.CultureInfo.DefaultThreadCurrentCulture = culture;
                        System.Globalization.CultureInfo.DefaultThreadCurrentUICulture = culture;
                        System.Threading.Thread.CurrentThread.CurrentCulture = culture;
                        System.Threading.Thread.CurrentThread.CurrentUICulture = culture;
                    }
                    if (args.Any(c => c == "--log"))
                    {
                        Thread logThread = new Thread(Helper.HelperProgram.LogMain);
                        logThread.Priority = ThreadPriority.Highest;
                        logThread.Name = "LogWindow thread";
                        logThread.IsBackground = false;
                        logThread.Start();
                        LogWindowShowing = true;
                        Log($"Logger window started.");
                    }
                }
                catch
                {

                }
                WinRT.ComWrappersSupport.InitializeComWrappers();
                Microsoft.UI.Xaml.Application.Start((p) =>
                {
                    var context = new Microsoft.UI.Dispatching.DispatcherQueueSynchronizationContext(
                        Microsoft.UI.Dispatching.DispatcherQueue.GetForCurrentThread());
                    SynchronizationContext.SetSynchronizationContext(context);
                    new App();
                });
                Log("Application exited.");
                SettingsManager.FlushAndStopAsync().GetAwaiter().GetResult();
                Helper.HelperProgram.Cleanup();
                MauiProgram.LogWriter.Flush();
                return;
            }
            catch (Exception ex)
            {
                [DllImport("user32.dll", CharSet = CharSet.Unicode)]
                static extern int MessageBox(IntPtr hWnd, String text, String caption, uint type);

                _ = MessageBox(IntPtr.Zero,
                    $"Oh no! projectFrameCut have to stop now because a unrecoverable {ex.GetType().Name} exception happens.\r\nFor more information, please see the crash report popped up later.\r\n\r\n({ex})",
                    "Fatal error",
                    0);
                Crash(ex);
            }


        }

        public static void Crash(Exception ex)
        {
            try
            {
                Log(ex, "Application crashed", "Application");
                MauiProgram.LogWriter?.Flush();
                Helper.HelperProgram.Cleanup();

            }
            catch (Exception) { }
            finally
            {
                string innerExceptionInfo = "None";
                if (ex.InnerException != null)
                {
                    innerExceptionInfo =
$"""
Type: {ex.InnerException.GetType().Name}                        
Message: {ex.InnerException.Message}
StackTrace:
{ex.InnerException.StackTrace}

""";
                }

                string header =
"""
Sorry, the application has encountered an unhandled exception and needs to close now.
Your works have been saved automatically when you make any change on the UI, so you won't lose your work.
If you want to help the development of this application, please consider to submit an issue or send this report to me.
""";
                try
                {
                    if (Localized is not null) header = Localized.AppCrashed;
                }
                catch { }

                var content =
$"""
Exception type: {ex.GetType().Name}
Message: {ex.Message}
StackTrace:
{ex.StackTrace}

From:{(ex.TargetSite is not null ? ex.TargetSite.ToString() : "unknown")}
InnerException:
{innerExceptionInfo}

Exception data:
{string.Join("\r\n", ex.Data.Cast<System.Collections.DictionaryEntry>().Select(k => $"{k.Key} : {k.Value}"))}

Environment:
Application: {AppInfo.PackageName},{AppInfo.VersionString} on {AppContext.TargetFrameworkName} ({AppInfo.BuildString})
OS version: {Environment.OSVersion}
CLR Version:{Environment.Version}
Command line: {Environment.CommandLine}
Current directory: {Environment.CurrentDirectory}

(report ended here)
""";
                string logPath, logMessage;

                try
                {
                    Directory.CreateDirectory(Path.Combine(MauiProgram.DataPath, "Crashlogs"));
                    logPath = Path.Combine(MauiProgram.DataPath, "Crashlogs", $"Crashlog-{DateTime.Now:yyyy-MM-dd-hh-mm-ss}.log");
                    logMessage = $"{header}\r\nthis log is in: {logPath}\r\n\r\n{content}";
                    File.WriteAllText(logPath, logMessage);
                }
                catch (Exception)
                {
                    logPath = Path.Combine(Directory.CreateTempSubdirectory("projectFrameCut_").FullName, "crash.log");
                    logMessage = $"{header}\r\nthis log is in: {logPath}\r\n\r\n{content}";
                    File.WriteAllText(logPath, logMessage);
                }
                Thread.Sleep(100);
                if (File.Exists(Path.Combine(AppContext.BaseDirectory, "projectFrameCut.Helper.exe")))
                {
                    Process.Start(new ProcessStartInfo { FileName = Path.Combine(AppContext.BaseDirectory, "projectFrameCut.Helper.exe"), Arguments = $"crashForm \"{logPath}\"", UseShellExecute = true });
                }
                else
                {
                    Process.Start(new ProcessStartInfo { FileName = logPath, UseShellExecute = true });
                }

                Environment.FailFast(logMessage, ex);
                Environment.Exit(ex.HResult);
            }
        }

    }
}
