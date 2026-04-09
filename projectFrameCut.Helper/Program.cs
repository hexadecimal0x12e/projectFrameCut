using projectFrameCut.Shared;
using projectFrameCut.SplashScreen;
using System;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace projectFrameCut.Helper
{
    public static class HelperProgram
    {
        static SplashForm splash;
        static LogForm log;
        static FrozenForm froze;
        [STAThread]
        public static void SplashMain()
        {
            SimpleLocalizerBaseGeneratedHelper.Localized ??= SimpleLocalizer_Helper.Init();
            Application.SetHighDpiMode(HighDpiMode.PerMonitorV2);
            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);

            splash = new();
            splash.ShowInTaskbar = false;
            splash.Show();
            SplashShowing = true;
            Application.Run();
        }
        [STAThread]
        public static void CrashMain()
        {
            SimpleLocalizerBaseGeneratedHelper.Localized ??= SimpleLocalizer_Helper.Init();

            Application.SetHighDpiMode(HighDpiMode.PerMonitorV2);
            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);

            var form = new CrashForm();
            form.ShowInTaskbar = true;
            form.Show();
            Application.Run();
        }
        [STAThread]
        public static void LogMain()
        {
            SimpleLocalizerBaseGeneratedHelper.Localized ??= SimpleLocalizer_Helper.Init();

            Application.SetHighDpiMode(HighDpiMode.PerMonitorV2);

            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);

            log = new LogForm();
            log.ShowInTaskbar = false;
            log.Show();
            Application.Run();
        }

        [STAThread]
        public static void FrozenMain()
        {
            SimpleLocalizerBaseGeneratedHelper.Localized ??= SimpleLocalizer_Helper.Init();

            Application.SetHighDpiMode(HighDpiMode.PerMonitorV2);
            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);

            froze = new FrozenForm();
            froze.ShowInTaskbar = true;
            froze.Show();
            Application.Run();
        }

        [STAThread]
        public static async Task Main(string[] args)
        {
            if (!OperatingSystem.IsWindowsVersionAtLeast(10, 0, 19041, 0))
            {
                var opt = MessageBox(IntPtr.Zero,
                    SimpleLocalizerBaseGeneratedHelper.Localized.UnsupportedOSPrompt(),
                    AppTitle,
                    0x10 | 0x4);
                if (opt == 1)
                {
                    Process.Start(new ProcessStartInfo
                    {
                        FileName = "https://www.microsoft.com/en-us/software-download/windows11",
                        UseShellExecute = true
                    });
                }
                return;
            }
#if DEBUG
            if (args.Contains("--wait"))
            {
                while (!Debugger.IsAttached)
                {
                    Thread.Sleep(500);
                }
            }
#endif
            SimpleLocalizerBaseGeneratedHelper.Localized ??= SimpleLocalizer_Helper.Init();

            if (args.Length > 1)
            {
                var mode = args[0];
                switch (mode)
                {
                    case "crashForm":
                        Application.SetHighDpiMode(HighDpiMode.PerMonitorV2);
                        Application.EnableVisualStyles();
                        Application.SetCompatibleTextRenderingDefault(false);
                        Application.Run(new CrashForm(false));
                        return;
                    case "crashHandler":
                        Application.SetHighDpiMode(HighDpiMode.PerMonitorV2);
                        Application.EnableVisualStyles();
                        Application.SetCompatibleTextRenderingDefault(false);
                        Application.Run(new CrashForm(true, args.Skip(1).ToArray()));
                        return;
                }
            }
            Process.Start(new ProcessStartInfo
            {
                FileName = "pjfc:",
                UseShellExecute = true
            });
            //if (File.Exists(Path.Combine(AppContext.BaseDirectory, "projectFrameCut.exe")))
            //{
            //    var proc = new ProcessStartInfo
            //    {
            //        FileName = "pjfc:",
            //        Arguments = args.Length > 0 ? string.Join(" ", args.Select(a => $"\"{a}\"")) : "",
            //    };
            //    Process.Start(proc);
            //}
            //else
            //{
            //    _ = MessageBox(IntPtr.Zero,
            //         SimpleLocalizerBaseGeneratedHelper.Localized.CorruptedInstallPrompt(),
            //         AppTitle,
            //         0x10);
            //    return;
            //}



        }

        public static bool SplashShowing { get; set; }

        public static string AppVersion { get; set; } = "";
        public static string AppTitle { get; set; } = "projectFrameCut";
        public static string AppChannel { get; set; } = "Unknown";

        public static void CloseSplash()
        {
            Thread.Sleep(1500);
            try
            {
                splash?.Invoke(new Action(() =>
                {
                    splash?.Close();
                }));
            }
            catch { }
            finally
            {
                SplashShowing = false;
                splash = null;
            }
        }

        public static void CloseLog()
        {
            Thread.Sleep(1500);
            try
            {
                log?.Invoke(() => log?.Close());
            }
            catch { }
        }
        public static void CloseFrozenDiag()
        {
            Thread.Sleep(1500);
            try
            {
                froze?.Invoke(() => froze?.Close());
            }
            catch { }
        }

        public static void Cleanup()
        {
            CloseSplash();
            CloseLog();
            CloseFrozenDiag();
            Application.Exit();
        }

        public static void UpdatePluginLoadingStat(string id)
        {
            splash?.Invoke(() => splash?.pluginStatLabel.Text = SimpleLocalizerBaseGeneratedHelper.Localized.SplashForm_PluginLoading(id));
        }
        public static void ResetPluginLoadingStat()
        {
            splash?.Invoke(() => splash?.pluginStatLabel.Text = SimpleLocalizerBaseGeneratedHelper.Localized.SplashForm_PostInit);
        }

        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        public static extern int MessageBox(IntPtr hWnd, String text, String caption, uint type);
    }
}