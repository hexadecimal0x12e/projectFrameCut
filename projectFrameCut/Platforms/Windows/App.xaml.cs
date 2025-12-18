using Microsoft.UI;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media;
using projectFrameCut.Platforms.Windows;
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
            Instance = this;
            UnhandledException += App_UnhandledException;
        }

        public static App? Instance;

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

        public static void Crash(Exception ex) => Program.Crash(ex);

        protected override MauiApp CreateMauiApp() => MauiProgram.CreateMauiApp();

        [DllImport("user32.dll")]
        private static extern bool SetForegroundWindow(IntPtr hWnd);

        [DllImport("user32.dll")]
        private static extern IntPtr SetFocus(IntPtr hWnd);

        public static async Task BringToForeground()
        {
            await MainThread.InvokeOnMainThreadAsync(() =>
            {
                var window = Microsoft.Maui.Controls.Application.Current?.Windows[0];
                if (window?.Handler?.PlatformView is Microsoft.UI.Xaml.Window nativeWindow)
                {
                    var hwnd = WinRT.Interop.WindowNative.GetWindowHandle(nativeWindow);
                    SetForegroundWindow(hwnd);
                    SetFocus(hwnd);
                }
            });

        }
    }


}
