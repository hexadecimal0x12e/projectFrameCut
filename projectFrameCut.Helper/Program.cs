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
        /// <summary>
        ///  The main entry point for the application.
        /// </summary>
        /// 
        static SplashForm splash;
        static LogForm log;
        [STAThread]
        public static void SplashMain()
        {
            SimpleLocalizerBaseGeneratedHelper.Localized = SimpleLocalizer.Init();
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
            SimpleLocalizerBaseGeneratedHelper.Localized = SimpleLocalizer.Init();

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
            SimpleLocalizerBaseGeneratedHelper.Localized = SimpleLocalizer.Init();

            Application.SetHighDpiMode(HighDpiMode.PerMonitorV2);

            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);

            log = new LogForm();
            log.ShowInTaskbar = false;
            log.Show();
            Application.Run();
        }

        [STAThread]
        public static async Task Main(string[] args)
        {
            if (!OperatingSystem.IsWindowsVersionAtLeast(10, 0, 19041, 0))
            {
                var opt = MessageBox(IntPtr.Zero,
                    "Sorry, projectFrameCut requires Windows 10 2004 / LTSC 2021 (build 19041) or higher to run. Consider upgrade your Windows system.",
                    "projectFrameCut",
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
            if (args.Contains("--wait"))
            {
                while (!Debugger.IsAttached)
                {
                    Thread.Sleep(500);
                }
            }
            SimpleLocalizerBaseGeneratedHelper.Localized = SimpleLocalizer.Init();

            if (args.Length > 1)
            {
                var mode = args[0];
                switch (mode)
                {
                    case "crashForm":
                        SimpleLocalizerBaseGeneratedHelper.Localized = SimpleLocalizer.Init();
                        Application.SetHighDpiMode(HighDpiMode.PerMonitorV2);
                        Application.EnableVisualStyles();
                        Application.SetCompatibleTextRenderingDefault(false); 
                        Application.Run(new CrashForm());
                        return;
                    case "uriCallback":
                        //todo
                        return;
                }
            }
            if(File.Exists(Path.Combine(AppContext.BaseDirectory, "projectFrameCut.exe")))
            {
                var proc = new ProcessStartInfo
                {
                    FileName = Path.Combine(AppContext.BaseDirectory, "projectFrameCut.exe"),
                    Arguments = args.Length > 0 ? string.Join(" ", args.Select(a => $"\"{a}\"")) : "",
                };
                Process.Start(proc);
            }
            else
            {
                MessageBox(IntPtr.Zero,
                    "projectFrameCut installation is corrupted or incomplete. Please reinstall the application.",
                    "projectFrameCut",
                    0x10);
            }



        }

        public static bool SplashShowing { get; set; }

        public static string AppVersion { get; set; } = "";
        public static string AppTitle { get; set; } = "projectFrameCut";

        public static void CloseSplash()
        {
            Thread.Sleep(1500);
            splash.Invoke(new Action(() =>
            {
                splash.Close();
            }));
            SplashShowing = false;
        }
        public static void CloseLog()
        {
            Thread.Sleep(1500);
            log.Invoke(log.Close);
        }

        public static void Cleanup()
        {
            Application.Exit();
        }

        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        static extern int MessageBox(IntPtr hWnd, String text, String caption, uint type);
    }
}