
namespace projectFrameCut.SplashScreen
{
    public static class SplashProgram
    {
        /// <summary>
        ///  The main entry point for the application.
        /// </summary>
        /// 
        static SplashForm splash;
        [STAThread]
        public static void Main()
        {
            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);

            splash = new();
            splash.ShowInTaskbar = false;
            splash.Show();
            SplashShowing = true;
            Application.Run();
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
    }
}