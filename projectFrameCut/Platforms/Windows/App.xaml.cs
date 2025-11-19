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


                var logMessage =
$"""
Sorry, the application has encountered an unhandled exception and needs to close now.
Your works have been saved automatically when you make any change on the UI, so you won't lose your work.
If you want to help the development of this application, please consider to submit an issue or send this report to me:

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
Application: {AppInfo.PackageName},{AppInfo.VersionString} ({AppInfo.BuildString})
OS version: {Environment.OSVersion}
CLR Version:{Environment.Version}
Command line: {Environment.CommandLine}
Current directory: {Environment.CurrentDirectory}

(report ended here)
""";
                string logPath;

                try
                {
                    Directory.CreateDirectory(Path.Combine(MauiProgram.DataPath, "Crashlogs"));
                    logPath = Path.Combine(MauiProgram.DataPath, "Crashlogs", $"Crashlog-{DateTime.Now:yyyy-MM-dd-hh-mm-ss}.log");
                    File.WriteAllText(logPath, logMessage);
                }
                catch (Exception)  
                {
                    logPath = Path.Combine(Directory.CreateTempSubdirectory("projectFrameCut_").FullName, "crash.log");
                    File.WriteAllText(logPath, logMessage);
                }
                Thread.Sleep(100);
                Process.Start(new ProcessStartInfo { FileName = logPath , UseShellExecute = true });
                Environment.FailFast(logMessage, ex);
                Environment.Exit(-1);
            }
        }

        

        protected override MauiApp CreateMauiApp() => MauiProgram.CreateMauiApp();
    }
    public static class Program
    {
        static async Task Main(string[] args)
        {
            try
            {
                WinRT.ComWrappersSupport.InitializeComWrappers();
                Microsoft.UI.Xaml.Application.Start((p) =>
                {
                    var context = new Microsoft.UI.Dispatching.DispatcherQueueSynchronizationContext(
                        Microsoft.UI.Dispatching.DispatcherQueue.GetForCurrentThread());
                    SynchronizationContext.SetSynchronizationContext(context);
                    new App();
                });

                await SettingsManager.FlushAndStopAsync();
            }
            catch (Exception ex)
            {
                [DllImport("user32.dll", CharSet = CharSet.Unicode)]
                static extern int MessageBox(IntPtr hWnd, String text, String caption, uint type);

                _ = MessageBox(IntPtr.Zero,
                    $"Application suffered from a unrecoverable early-boot {ex.GetType().Name} exception:\n{ex}\r\n\r\nTry reinstall application.",
                    "Fatal error",
                    0);
                App.Crash(ex);
            }
            
        }
    }
}
