using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media;
using projectFrameCut.Setting.SettingManager;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using WinRT.Interop;

// To learn more about WinUI, the WinUI project structure,
// and more about our project templates, see: http://aka.ms/winui-project-info.

namespace projectFrameCut.WinUI
{
    /// <summary>
    /// Provides application-specific behavior to supplement the default Application class.
    /// </summary>
    public partial class App : MauiWinUIApplication
    {
        /// <summary>
        /// Initializes the singleton application object.  This is the first line of authored code
        /// executed, and as such is the logical equivalent of main() or WinMain().
        /// </summary>
        public App()
        {
            this.InitializeComponent();
            UnhandledException += App_UnhandledException;
        }

        //protected override void OnLaunched(LaunchActivatedEventArgs args)
        //{
        //    base.OnLaunched(args);
        //    var window = Application.Windows[0].Handler?.PlatformView as MauiWinUIWindow;



        //}

        private void App_UnhandledException(object sender, Microsoft.UI.Xaml.UnhandledExceptionEventArgs e)
        {
            try
            {
                if (SettingsManager.Settings is not null)
                {
                    if (SettingsManager.IsSettingExists("DontPanicOnUnhandledException"))
                    {
                        if (bool.TryParse(SettingsManager.GetSetting("DontPanicOnUnhandledException", "False"), out var p) ? p : false)
                        {
                            e.Handled = true;
                            Log(e.Exception, "Global unhandled exception", this);
                            AppShell.instance.CurrentPage?.DisplayAlertAsync("Global unhandled exception", Localized._ExceptionTemplate(e.Exception), "ok");
                            return;
                        }
                    }
                }
            }
            catch { }
            Crash(e.Exception);
        }

        internal static void Crash(Exception ex)
        {
            try
            {
                Log(ex,"Application crashed",null);

            }
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
                Process.Start(new ProcessStartInfo { FileName = logPath , UseShellExecute = true });
                Environment.FailFast(logMessage, ex);
                Environment.Exit(ex.HResult);
            }
        }


        protected override MauiApp CreateMauiApp() => MauiProgram.CreateMauiApp();
    }

    public static class Program
    {
        [STAThread] //avoid failed to initialize COM library error, cause a lot of issue like IME not work at all...
        static void Main(string[] args)
        {
            try
            {
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

                SettingsManager.FlushAndStopAsync().GetAwaiter().GetResult();
            }
            catch (Exception ex)
            {
                [DllImport("user32.dll", CharSet = CharSet.Unicode)]
                static extern int MessageBox(IntPtr hWnd, String text, String caption, uint type);

                _ = MessageBox(IntPtr.Zero,
                    $"Oh no! projectFrameCut have to stop now because a unrecoverable {ex.GetType().Name} exception happens.\r\nFor more information, please see the crash report popped up later.\r\n\r\n({ex})",
                    "Fatal error",
                    0);
                App.Crash(ex);
            }
            
        }
    }
}
