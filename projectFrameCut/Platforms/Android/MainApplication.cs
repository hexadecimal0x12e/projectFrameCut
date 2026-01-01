using Android.App;
using Android.Content;
using Android.Runtime;

namespace projectFrameCut.Platforms.Android
{
    [Application]
    public class MainApplication : MauiApplication
    {
        public static Context MainContext;

        public MainApplication(IntPtr handle, JniHandleOwnership ownership)
            : base(handle, ownership)
        {
            System.Threading.Thread.CurrentThread.Name = "App Main thread";
            MainContext = this;
            string? loggingDir = null;
            var extFilesDir = GetExternalFilesDir(null);
            if (extFilesDir != null)
            {
                loggingDir = System.IO.Path.Combine(extFilesDir.AbsolutePath, "..", "logging");
            }
            if (loggingDir is not null) Directory.CreateDirectory(loggingDir);
            // use https://github.com/Kyant0/Fishnet to capture Android crashes
            Com.Kyant.Fishnet.Fishnet.Init(this, loggingDir ?? FileSystem.AppDataDirectory);
        }

        protected override MauiApp CreateMauiApp() => MauiProgram.CreateMauiApp();

        
    }
}
