using projectFrameCut.Shared;
using projectFrameCut.SplashScreen;
using System.Diagnostics;
using System.Reflection;

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
        public static void Main(string[] args)
        {
            if(args.Contains("--wait"))
            {
                while (!Debugger.IsAttached)
                {
                    Thread.Sleep(500);
                }
                return;
            }
            SimpleLocalizerBaseGeneratedHelper.Localized = SimpleLocalizer.Init();

            if (args.Length > 1)
            {
                var mode = args[0];
                switch (mode)
                {
                    case "crashForm":
                        ApplicationConfiguration.Initialize();
                        Application.Run(new CrashForm());
                        return;
                    case "uriCallback":
                        //todo
                        return;
                }
            }
            var proc = new ProcessStartInfo
            {
                FileName = "projectFrameCut.exe",
                Arguments = args.Length > 0 ? string.Join(" ", args.Select(a => $"\"{a}\"")) : "",
            };  
            Process.Start(proc);


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
    }
}