using Microsoft.Extensions.Logging;
using projectFrameCut.Shared;

#if ANDROID
using projectFrameCut.Render.AndroidOpenGL.Platforms.Android;
using projectFrameCut.Platforms.Android;
using Java.Lang;

#endif

#if IOS || MACCATALYST
using Foundation;
#endif
using System;
using System.Diagnostics;
using FFmpeg.AutoGen;
using System.Reflection;
using System.Runtime.InteropServices;
using FFmpeg.AutoGen.Native;
using Exception = System.Exception;
using System.Text;

namespace projectFrameCut
{
    public static class MauiProgram
    {
        static StreamWriter LogWriter;

        public static string DataPath { get; private set; }

        public static MauiApp CreateMauiApp()
        {
            var loggingDir = System.IO.Path.Combine(FileSystem.AppDataDirectory, "logging");
            DataPath = FileSystem.AppDataDirectory;
            try
            {
#if ANDROID
                try
                {
                    var pfn = Android.App.Application.Context.PackageName;
                    var userAccessblePath = $"/sdcard/Android/data/{pfn}/";
                    if (Path.Exists(userAccessblePath))
                    {
                        DataPath = userAccessblePath;
                        loggingDir = Path.Combine(userAccessblePath, "logging");
                    }

                }
                catch //use the default path (/data/data/...)           
                {
                    
                }
#elif IOS
                //files->my [iDevices]->projectFrameCut
                loggingDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "logging"); 
                DataPath = Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments); 
                
#elif MACCATALYST
                loggingDir = Path.Combine(FileSystem.AppDataDirectory, "logging"); // ~/Library/Containers/<bundle>/Data/Library/Application Support/<bundle>）

                DataPath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments),"projectFrameCut");
#elif WINDOWS
                if (IsPackaged()) //%localappdata%\Packages\<package>\LocalState
                {
                    loggingDir = System.IO.Path.Combine(FileSystem.AppDataDirectory, "logging");
                    DataPath = Path.Combine(FileSystem.AppDataDirectory, "Data"); //Respect the vision of store applications (no residuals after uninstallation)
                }
                else
                {
                    loggingDir = System.IO.Path.Combine(FileSystem.AppDataDirectory, "logging"); //%localappdata%\hexadecimal0x12e\hexadecimal0x12e.projectFrameCut\Data
                    DataPath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "projectFrameCut");
                }
#endif

                Directory.CreateDirectory(loggingDir);
                try
                {
                    Directory.CreateDirectory(DataPath);
                }
                catch(Exception ex)
                {
                    Log(ex, "create userdata dir", CreateMauiApp);
                    DataPath = FileSystem.AppDataDirectory;
                }
                LogWriter = new StreamWriter(System.IO.Path.Combine(loggingDir, $"log-{DateTime.Now:yyyy-MM-dd-HH-mm-ss}.log"), append: true)
                {
                    AutoFlush = true
                };

                MyLoggerExtensions.OnLog += MyLoggerExtensions_OnLog;


            }
            catch (Exception ex)
            {
                Debug.WriteLine($"Failed to set up log file: {ex.Message}");
            }

            Log($"projectFrameCut - v{Assembly.GetExecutingAssembly().GetName().Version} \r\n" +
                $"                  on {DeviceInfo.Platform} in cpu arch {RuntimeInformation.ProcessArchitecture},\r\n" +
                $"                  os version {Environment.OSVersion},\r\n" +
                $"                  clr version {Environment.Version},\r\n" +
                $"                  cmdline: {Environment.CommandLine}");
            Log("Copyright (c) hexadecimal0x12e 2025, and thanks to other open-source code's authors. This project is licensed under GNU GPL V2.");
            Log($"DataPath:{DataPath}, loggingDir:{loggingDir}");
            try
            {
                if (File.Exists(Path.Combine(DataPath, "OverrideUserData.txt")))
                {
                    var newPath = File.ReadAllText(Path.Combine(DataPath, "OverrideUserData.txt"));
                    DataPath = newPath;
                    Log($"User override Data path to:{DataPath}");
                }
            }
            catch(Exception ex)
            {
                Log(ex, "set user data dir", CreateMauiApp);
            }

            try
            {
                var builder = MauiApp.CreateBuilder();
                builder.UseMauiApp<App>();
#if DEBUG
                builder.Logging.SetMinimumLevel(LogLevel.Trace);
#else
                builder.Logging.SetMinimumLevel(LogLevel.Information);
#endif
                builder.Logging.AddProvider(new MyLoggerProvider());
                builder.ConfigureFonts(fonts =>
                    {
                        fonts.AddFont("HarmonyOS_Sans_SC_Regular.ttf", "Font_Regular");
                        fonts.AddFont("HarmonyOS_Sans_SC_Bold.ttf", "Font_Semibold");
                    })
#if ANDROID
                    .ConfigureMauiHandlers(handlers =>
                    {
                        handlers.AddHandler<NativeGLSurfaceView, NativeGLSurfaceViewHandler>();
                    });
#else
;
#endif


#if ANDROID
                MyLoggerExtensions.OnLog += (msg, level) =>
                {
                    switch (level.ToLower())
                    {
                        case "info":
                            Android.Util.Log.Info("projectFrameCut", msg);
                            break;
                        case "warning":
                        case "warn":
                            Android.Util.Log.Warn("projectFrameCut", msg);
                            break;
                        case "error":
                            Android.Util.Log.Error("projectFrameCut", msg);
                            break;
                        case "critical":
                            Android.Util.Log.Wtf("projectFrameCut", msg);
                            break;
                        default:
                            Android.Util.Log.Info($"projectFrameCut/{level}", msg);
                            break;
                    }
                };

                try
                {
                    var nativeLibDir = Android.App.Application.Context.ApplicationInfo.NativeLibraryDir;
                    Log($"Native library dir: {nativeLibDir}");
                    ffmpeg.RootPath = nativeLibDir;
                    JavaSystem.LoadLibrary("c"); //准备加载库
                    FFmpeg.AutoGen.DynamicallyLoadedBindings.ThrowErrorIfFunctionNotFound = true;
                    FFmpeg.AutoGen.DynamicallyLoadedBindings.Initialize();
                }
                catch (Exception ex)
                {
                    Log(ex);
                    //throw; 
                }

#endif

#if WINDOWS
                if (Directory.GetFiles(AppContext.BaseDirectory, "av*.dll").Length == 0)
                {
                    Log("ERROR: ffmpeg binaries not found. Please reinstall projectFrameCut.");
                    Environment.FailFast("ERROR: ffmpeg binaries not found. Please reinstall projectFrameCut.", new DllNotFoundException("ERROR: ffmpeg binaries not found. Please reinstall projectFrameCut."));
                }


#endif
                try
                {
                    Log($"internal FFmpeg library: version {ffmpeg.av_version_info()}, {ffmpeg.avcodec_license()}\r\nconfiguration:{ffmpeg.avcodec_configuration()}");
                }
                catch(Exception ex)
                {
                    Log(ex,"query ffmpeg version", CreateMauiApp);
                    Log("Unknown internal FFmpeg version");
                }

                Localized = SimpleLocalizer.Init();
                Log($"Localization initialized to {Localized._LocaleId_}, {Localized.WelcomeMessage}");
                Log("Everything ready!");
                var app = builder.Build();
                Log("App is ready!");
                return app;
            }
            catch (Exception ex)
            {
                Log($"Error creating MAUI App: {ex.Message}");
                Debug.WriteLine($"Error creating MAUI App: {ex.Message}");
                throw;
            }
        }

        private static object locker = new();

        private static void MyLoggerExtensions_OnLog(string msg, string level)
        {
            lock (locker) LogWriter.WriteLine($"[{DateTime.Now:T} @ {level}] {msg}");
        }


#if WINDOWS
        private const int APPMODEL_ERROR_NO_PACKAGE = 15700;

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, ExactSpelling = true)]
        private static extern int GetPackageFullName(IntPtr hProcess, ref int packageFullNameLength, StringBuilder packageFullName);

        [DllImport("kernel32.dll")]
        private static extern IntPtr GetCurrentProcess();

        public static bool IsPackaged()
        {
            try
            {
                IntPtr h = GetCurrentProcess();
                int length = 0;
                int rc = GetPackageFullName(h, ref length, null);
                if (rc == APPMODEL_ERROR_NO_PACKAGE)
                    return false;
                if (length <= 0)
                    return false;

                var sb = new StringBuilder(length);
                rc = GetPackageFullName(h, ref length, sb);
                Log($"Running inside a MSIX container, pfn:{sb}");
                return rc == 0 && sb.Length > 0;
            }
            catch
            {
                Log($"Running outside a MSIX container.");
                return false;
            }

        }
#endif

    }


}



