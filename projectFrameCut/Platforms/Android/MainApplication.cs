using Android.App;
using Android.Content;
using Android.Runtime;
using System.Diagnostics.CodeAnalysis;
using System.Reflection;
using System.Runtime.InteropServices;

namespace projectFrameCut.Platforms.Android
{
    [Application]
    public class MainApplication : MauiApplication
    {
        [NotNull]
        public static Context? MainContext;
        public static bool IsRenderWorkerProcess { get; private set; }

        public MainApplication(IntPtr handle, JniHandleOwnership ownership)
            : base(handle, ownership)
        {
            System.Threading.Thread.CurrentThread.Name = "App Main thread";
            MainContext = this;
            IsRenderWorkerProcess = global::Android.App.Application.ProcessName
                ?.EndsWith(":renderworker", StringComparison.Ordinal) == true;
            NativeLoader.Init();
            if (IsRenderWorkerProcess) return;

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

        private static class NativeLoader
        {
            static NativeLoader()
            {
                NativeLibrary.SetDllImportResolver(
                    Assembly.GetExecutingAssembly(),
                    Resolve);
            }

            private static IntPtr Resolve(string libraryName, Assembly assembly, DllImportSearchPath? searchPath)
            {
                List<string> paths = Enumerable.Empty<string>()
                .Append(libraryName)
                .Append(Path.Combine(Context.ApplicationInfo?.NativeLibraryDir, libraryName))
                .Append(Path.Combine(Context.ApplicationInfo?.NativeLibraryDir, libraryName + ".so"))
                .Append(Path.Combine("/system/lib", libraryName))
                .Append(Path.Combine("/system/lib64", libraryName))
                .Append(Path.Combine("/vendor/lib", libraryName))
                .Append(Path.Combine("/vendor/lib64", libraryName))
                .Append(Path.Combine("/system/lib", libraryName + ".so"))
                .Append(Path.Combine("/system/lib64", libraryName + ".so"))
                .Append(Path.Combine("/vendor/lib", libraryName + ".so"))
                .Append(Path.Combine("/vendor/lib64", libraryName + ".so"))
                .ToList();
                foreach (var item in paths)
                {
                    LogDiagnostic($"Trying to load native library {libraryName} in {item}");
                    if (File.Exists(item) && NativeLibrary.TryLoad(item, out var handle))
                    {
                        LogDiagnostic($"Successfully loaded native library {libraryName} from {item}");
                        return handle;
                    }
                }
                LogDiagnostic($"Failed to load native library {libraryName} from any known path.");
                return IntPtr.Zero;
            }

            public static void Init() { }
        }
    }

    internal sealed class RenderWorkerMauiApplication : Microsoft.Maui.Controls.Application
    {
        protected override Microsoft.Maui.Controls.Window CreateWindow(IActivationState? activationState)
            => new(new Microsoft.Maui.Controls.ContentPage());
    }
}
