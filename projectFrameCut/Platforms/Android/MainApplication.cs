using Android.App;
using Android.Content;
using Android.Runtime;
using System.Diagnostics.CodeAnalysis;

namespace projectFrameCut.Platforms.Android
{
    [Application]
    public class MainApplication : MauiApplication
    {
        [NotNull]
        public static Context? MainContext;

        public MainApplication(IntPtr handle, JniHandleOwnership ownership)
            : base(handle, ownership)
        {
            System.Threading.Thread.CurrentThread.Name = "App Main thread";
            MainContext = this;
            string? loggingDir = null;
            var extFilesDir = GetExternalFilesDir(null);
            if (extFilesDir != null)
            {
                loggingDir = System.IO.Path.Combine(extFilesDir.AbsolutePath, "..", "Logs");
                var appDataDir = System.IO.Path.Combine(extFilesDir.AbsolutePath, "..", "AppData");
                Directory.CreateDirectory(appDataDir);
                Directory.CreateDirectory(loggingDir);
                if (File.Exists(Path.Combine(extFilesDir.AbsolutePath, "..", "settings.json")))
                {
                    foreach (var f in Directory.GetFiles(Path.Combine(extFilesDir.AbsolutePath, ".."), "*.json", SearchOption.TopDirectoryOnly))
                    {
                        try
                        {
                            File.Move(f, Path.Combine(appDataDir, Path.GetFileName(f)), true);
                        }
                        catch
                        {
                            // ignore
                        }
                    }
                }
            }
            // use https://github.com/Kyant0/Fishnet to capture Android crashes
            Com.Kyant.Fishnet.Fishnet.Init(this, loggingDir ?? FileSystem.AppDataDirectory);

        }

        protected override MauiApp CreateMauiApp() => MauiProgram.CreateMauiApp();


    }
}
