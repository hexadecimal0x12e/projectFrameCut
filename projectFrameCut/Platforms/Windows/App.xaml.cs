using Microsoft.UI.Xaml;
using System.Diagnostics;

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

        private void App_UnhandledException(object sender, Microsoft.UI.Xaml.UnhandledExceptionEventArgs e)
        {
            throw new NotImplementedException();
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
Sorry, the application has encountered an unhandled exception and needs to close.
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
OS version: {Environment.OSVersion}
CLR Version:{Environment.Version}
Command line: {Environment.CommandLine}
Current directory: {Environment.CurrentDirectory}

(report ended here)
""";
                Directory.CreateDirectory(Path.Combine(MauiProgram.DataPath, "crashlog"));
                string logPath;
                try
                {
                    logPath = Path.Combine(MauiProgram.DataPath, "crashlog\\", $"Crashlog-{DateTime.Now:yyyy-MM-dd-hh-mm-ss}.log");
                    File.WriteAllText(logPath, logMessage);
                }
                catch (Exception) //避免最坏的情况（整个UWP运行时都不可用）
                {
                    logPath = Path.Combine(Directory.CreateTempSubdirectory("audiocopy_").FullName, "crash.log");
                    File.WriteAllText(logPath, logMessage);
                }
                Thread.Sleep(100);
                Process.Start(new ProcessStartInfo { FileName = logPath, UseShellExecute = true });
                Environment.FailFast(ex.Message, ex);
                Environment.Exit(1);
            }
        }

        protected override MauiApp CreateMauiApp() => MauiProgram.CreateMauiApp();
    }

}
