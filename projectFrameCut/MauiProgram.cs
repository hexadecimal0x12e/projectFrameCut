#pragma warning disable CS8974 //log a exception will cause this
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

#if WINDOWS
using projectFrameCut.Platforms.Windows;

#endif
using System;
using System.Diagnostics;
using FFmpeg.AutoGen;
using System.Reflection;
using System.Runtime.InteropServices;
using FFmpeg.AutoGen.Native;
using Exception = System.Exception;
using System.Text;
using System.Text.Json;
using projectFrameCut.Setting.SettingManager;
using System.Globalization;

namespace projectFrameCut
{
    public static class MauiProgram
    {
        static StreamWriter LogWriter;

        public static string DataPath { get; private set; }

        public static string BasicDataPath { get; private set; }

        private static readonly string[] FoldersNeedInUserdata =
            [
            "My Drafts",
            "My Assets"
            ];

#if WINDOWS
        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        static extern int MessageBox(IntPtr hWnd, String text, String caption, uint type);
#endif

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
                { }
#elif IOS
                //files->my [iDevices]->projectFrameCut
                loggingDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "logging"); 
                DataPath = Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments); 
                
#elif MACCATALYST
                loggingDir = Path.Combine(FileSystem.AppDataDirectory, "logging"); // ~/Library/Containers/<bundle>/Data/Library/Application Support/<bundle>）

                DataPath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments),"projectFrameCut");
#elif WINDOWS
                if (IsPackaged()) //%localappdata%\Packages\<pfn>\LocalState
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
                BasicDataPath = new string(DataPath.ToArray()); //avoid any reference change
                Directory.CreateDirectory(loggingDir);
                try
                {
                    Directory.CreateDirectory(DataPath);
                }
                catch (Exception ex)
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
#if ANDROID
                Android.Util.Log.Wtf("projectFrameCut", $"Failed to init the basic user data because of a {ex.GetType().Name} exception:{ex.Message}");           
#elif WINDOWS
                _ = MessageBox(new nint(0), $"CRITICAL error: projectFrameCut cannot init the basic user data because of a {ex.GetType().Name} exception:\r\n{ex.Message}\r\n\r\nApplication may work abnormally.\r\nTo help us fix this problem, please submit a issue with a screenshot of this dialogue.", "projectFrameCut", 0U);
#endif
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
                if (File.Exists(Path.Combine(DataPath, "settings.json")))
                {
                    var json = File.ReadAllText(Path.Combine(DataPath, "settings.json"));
                    SettingsManager.Settings = new(JsonSerializer.Deserialize<Dictionary<string, string>>(json) ?? []);
                    Log($"Settings inited. Count: {SettingsManager.Settings.Count}");
                }
                else
                {
                    SettingsManager.Settings = new();
                    Log("Settings inited with empty.");
                }
            }
            catch (Exception ex)
            {
                Log(ex, "load settings", CreateMauiApp);
#if ANDROID
                Android.Util.Log.Wtf("projectFrameCut", $"Failed to init the settings because of a {ex.GetType().Name} exception:{ex.Message}");       
#elif WINDOWS
                _ = MessageBox(new nint(0), $"CRITICAL error: projectFrameCut cannot init the settings because of a {ex.GetType().Name} exception:{ex.Message}\r\nYour settings will be reset temporarily.\r\nTry fix the setting.json manually, or submit a issue with a screenshot of this dialogue.", "projectFrameCut", 0U);
#endif
                SettingsManager.Settings = new();

            }

            try
            {
                if (File.Exists(Path.Combine(FileSystem.AppDataDirectory, "OverrideUserDataPath.txt")))
                {
                    var newPath = File.ReadAllText(Path.Combine(FileSystem.AppDataDirectory, "OverrideUserDataPath.txt"));
                    if (!Directory.Exists(newPath))
                    {
#if WINDOWS
                        _ = MessageBox(new nint(0), $"CRITICAL error: projectFrameCut cannot setup the UserData because of the path your defined is not exist now.\r\nYou may found your drafts disappeared.\r\nTry reset the data directory path later.", "projectFrameCut", 0U);
#endif
                    }
                        DataPath = newPath;
                        Log($"User override Data path to:{DataPath}");
                    }

                    foreach (var item in FoldersNeedInUserdata)
                    {
                        Directory.CreateDirectory(Path.Combine(DataPath, item));
                    }
                }
            catch (Exception ex)
            {
                Log(ex, "setup user data dir", CreateMauiApp);
#if ANDROID
                Android.Util.Log.Wtf("projectFrameCut", $"Failed to init the settings because of a {ex.GetType().Name} exception:{ex.Message}");         
#elif WINDOWS
                _ = MessageBox(new nint(0), $"CRITICAL error: projectFrameCut cannot init the UserData directory because of a {ex.GetType().Name} exception:{ex.Message}\r\nYou may found your drafts disappeared.\r\nTry reset the data directory path.", "projectFrameCut", 0U);
#endif
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
#if WINDOWS
                builder.Services.AddSingleton<IDialogueHelper, DialogueHelper>();
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
                catch (Exception ex)
                {
                    Log(ex, "query ffmpeg version", CreateMauiApp);
                    Log("Unknown internal FFmpeg version");
                }

                var locate = SettingsManager.GetSetting("locale", "default"); 
                Log($"Your culture: {CultureInfo.CurrentCulture.Name}, UI culture: {CultureInfo.CurrentUICulture.Name}, locate defined in settings:{locate} ");
                if (locate == "default") locate = CultureInfo.CurrentCulture.Name;
                System.Threading.Thread.CurrentThread.CurrentCulture = CultureInfo.CreateSpecificCulture(locate);
                System.Threading.Thread.CurrentThread.CurrentUICulture = CultureInfo.CreateSpecificCulture(locate);
                Localized = SimpleLocalizer.Init(locate);
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



