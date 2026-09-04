using Foundation;
using ObjCRuntime;
using projectFrameCut.AIAssistance;
using projectFrameCut.ApplicationAPIBase.Plugins;
using projectFrameCut.Render.RenderAPIBase.Plugins;
using projectFrameCut.Shared;
using System.Diagnostics;
using System.Reflection;
using System.Reflection.Metadata;
using UIKit;

namespace projectFrameCut.Platforms.iOS
{
    public class Program
    {
        private static readonly string? loggingDir;
        private static readonly string? crashLogDir;

        static Program()
        {
            // On iOS, System.IO internally calls Path.GetFullPath → realpath
            // which stats the app container root — and Apple's hardened sandbox now
            // denies that with EPERM.  We must use Foundation APIs (NSFileManager,
            // NSSearchPath) for ALL file operations at the crash-handler level.
            //
            // Strategy: Documents → Library/Caches → the shared application cache path.
            string? root = null;
            try
            {
                var docDirs = NSSearchPath.GetDirectories(
                    NSSearchPathDirectory.DocumentDirectory,
                    NSSearchPathDomain.User,
                    true);
                if (docDirs.Length > 0)
                    root = docDirs[0];
            }
            catch { }

            if (root is null)
            {
                try
                {
                    var cacheDirs = NSSearchPath.GetDirectories(
                        NSSearchPathDirectory.CachesDirectory,
                        NSSearchPathDomain.User,
                        true);
                    if (cacheDirs.Length > 0)
                        root = cacheDirs[0];
                }
                catch { }
            }

            if (root is null)
            {
                root = MauiProgram.CachePath;
            }

            if (root is not null)
            {
                loggingDir = System.IO.Path.Combine(root, "logging");
                crashLogDir = System.IO.Path.Combine(loggingDir, "crashlog");
                // NSFileManager.CreateDirectory is sandbox-aware and works correctly.
                NSFileManager.DefaultManager.CreateDirectory(
                    NSUrl.FromFilename(crashLogDir!), true, null, out _);
            }
            else
            {
                // Extreme fallback — Crash() will skip file-based logging.
                loggingDir = crashLogDir = null;
            }
        }

        // This is the main entry point of the application.
        static void Main(string[] args)
        {
            System.Threading.Thread.CurrentThread.Name = "App Main thread";

            // Global unhandled exception handler for any CLR-level crashes.
            AppDomain.CurrentDomain.UnhandledException += (s, e) =>
            {
                if (e.ExceptionObject is Exception ex)
                {
                    try { Log("FATAL: AppDomain unhandled exception", "Fatal"); }
                    catch { }
                    Crash(ex);
                }
            };

            // Catch exceptions that escape the UIKit main loop.
            // On iOS, the hardened sandbox can cause POSIX syscalls
            // (chdir, stat, mkdir, etc.) to throw UnauthorizedAccessException
            // when they traverse the app container root — even internally via
            // Path.GetFullPath / realpath. We intercept here so the crash
            // handler can save a complete log before we exit.
            try
            {
                UIApplication.Main(args, null, typeof(AppDelegate));
            }
            catch (Exception ex)
            {
                Log("FATAL: Unhandled exception in Main", "Fatal");
                Crash(ex);
                throw;
            }
        }

        /// <summary>
        /// Save a crash report using ONLY Foundation/UIKit APIs, never System.IO,
        /// because iOS sandbox blocks the POSIX syscalls that System.IO relies on.
        /// </summary>
        internal static void Crash(Exception ex)
        {
            MauiProgram.LogWriter?.Flush();

            try
            {
                MauiProgram.LogWriter?.WriteLine("*** FATAL: CRASH");
                Log(ex, "", "Application");
                Log($"Crash() is invoked by:{Environment.StackTrace}.", "fatal");
                MauiProgram.LogWriter?.Flush();
#if DEBUG
                if (Debugger.IsAttached)
                {
                    Debugger.BreakForUserUnhandledException(ex);
                    Environment.Exit(ex.HResult);
                }
#endif

            }
            catch (Exception) { }
            finally
            {
                string innerExceptionInfo = "None";
                if (ex.InnerException != null)
                {
                    innerExceptionInfo =
$"""
ClipType: {ex.InnerException.GetType().Name}
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
                string appInfo = $"Application: {Assembly.GetExecutingAssembly().GetName().FullName}";
                try
                {
                    appInfo = Setting.SettingPages.DiagnosticSettingPage.GetAppInfo(false, false);
                }
                catch
                {
                    appInfo =
$"""
{Assembly.GetExecutingAssembly().GetCustomAttribute<AssemblyTitleAttribute>()?.Title ?? "unknown"}: {Assembly.GetExecutingAssembly().GetCustomAttribute<AssemblyConfigurationAttribute>()?.Configuration ?? "unknown config"}@{new string((Assembly.GetExecutingAssembly().GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion ?? "0.1.2+unknown commit").Skip(6).ToArray())}
Assembly: {AppInfo.PackageName},{AppInfo.VersionString} on {AppContext.TargetFrameworkName}
IPluginBase API: v{IPluginBase.CurrentPluginAPIVersion} | IApplicationPluginBase API: v{IApplicationPluginBase.CurrentAppLevelPluginAPIVersion}

OS version: {Environment.OSVersion}
CLR Version:{Environment.Version}
Command line: {Environment.CommandLine}
Current directory: {Environment.CurrentDirectory}
""";
                }
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
{appInfo}

(report ended here)
""";

                var logMessage = $"{header}\r\n{content}";
                WriteCrashLogFile(logMessage);
                Thread.Sleep(100);
                MauiProgram.LogWriter?.Flush();
            }
        }

        /// <summary>
        /// Write the crash report to disk using ONLY Foundation APIs.
        /// System.IO (File.WriteAllText, Directory.CreateDirectory, etc.) is
        /// avoided because it uses POSIX syscalls blocked by iOS sandbox.
        /// </summary>
        private static void WriteCrashLogFile(string logMessage)
        {
            // Determine target directory — fallback chain: crashLogDir → shared cache
            string? targetDir = crashLogDir;
            if (targetDir is null || !EnsureDirectoryExists(targetDir))
            {
                targetDir = MauiProgram.CachePath;
            }

            if (!EnsureDirectoryExists(targetDir))
                return;

            var fileName = $"Crashlog-{DateTime.Now:yyyy-MM-dd-HH-mm-ss}.log";
            var filePath = System.IO.Path.Combine(targetDir, fileName);

            try
            {
                // NSData.Save uses Foundation APIs that handle the iOS sandbox correctly.
                var data = NSData.FromString(logMessage, NSStringEncoding.UTF8);
                data.Save(filePath, true);
            }
            catch
            {
                // Ultimate fallback: try System.IO one last time in case the
                // target directory is under Documents/Library (not container root).
                try
                {
                    System.IO.File.WriteAllText(filePath, logMessage);
                }
                catch { }
            }
        }

        /// <summary>Sandbox-safe directory creation using NSFileManager.</summary>
        private static bool EnsureDirectoryExists(string path)
        {
            try
            {
                bool isDir = false;
                bool exists = NSFileManager.DefaultManager.FileExists(path, ref isDir);
                if (exists && isDir) return true;
            }
            catch { }

            try
            {
                return NSFileManager.DefaultManager.CreateDirectory(
                    NSUrl.FromFilename(path), true, null, out _);
            }
            catch
            {
                return false;
            }
        }
    }
}
