using projectFrameCut.SplashScreen;
using System.Diagnostics;

namespace projectFrameCut.Helper
{
    public static class HelperProgram
    {
        /// <summary>
        ///  The main entry point for the application.
        /// </summary>
        /// 
        static SplashForm splash;
        [STAThread]
        public static void SplashMain()
        {
            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);

            splash = new();
            splash.ShowInTaskbar = false;
            splash.Show();
            SplashShowing = true;
            Application.Run();
        }
        [STAThread]
        public static void CrashMain(string[] args)
        {
            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);

            var form = new CrashForm(args);
            form.ShowInTaskbar = false;
            form.Show();
            Application.Run();
        }

        [STAThread]
        public static void Main(string[] args)
        {
            if(args.Length > 1)
            {
                var mode = args[0];
                switch (mode)
                {
                    case "crashHandler":
                        CrashMain(args);
                        return;
                }
            }
            Process.Start("projectFrameCut.exe", string.Join(' ', args));
            return;
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
                Application.Exit();
            }));
            SplashShowing = false;
        }
    }
}