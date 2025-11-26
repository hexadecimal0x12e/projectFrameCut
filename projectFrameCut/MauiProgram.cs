#pragma warning disable CS8974 //log a exception will cause this
using Microsoft.Extensions.Logging;
using projectFrameCut.Shared;

#if ANDROID
using projectFrameCut.Render.AndroidOpenGL.Platforms.Android;
using projectFrameCut.Platforms.Android;
using Java.Lang;

#endif

#if WINDOWS
using projectFrameCut.Platforms.Windows;
using projectFrameCut.WinUI;

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
using System.Globalization;
using Microsoft.Maui.Controls.PlatformConfiguration;
using System.Runtime.Versioning;
using CommunityToolkit.Maui;

namespace projectFrameCut
{
    public static class MauiProgram
    {
        public static StreamWriter LogWriter;

        public static string DataPath { get; private set; }

        public static string BasicDataPath { get; private set; }

        private static readonly string[] FoldersNeedInUserdata =
            [
            "My Drafts",
            "My Assets"
            ];


        public static MauiApp CreateMauiApp()
        {
            string loggingDir = "";
            try
            {
                loggingDir = System.IO.Path.Combine(FileSystem.AppDataDirectory, "logging");
                DataPath = FileSystem.AppDataDirectory;
                BasicDataPath = FileSystem.AppDataDirectory;
#if ANDROID
                try
                {
                    var pfn = Android.App.Application.Context.PackageName;
                    var userAccessblePath = $"/sdcard/Android/data/{pfn}/";
                    if (Path.Exists(userAccessblePath))
                    {
                        DataPath = userAccessblePath;
                        BasicDataPath = userAccessblePath;
                        loggingDir = Path.Combine(userAccessblePath, "logging");
                    }
                }
                catch //use the default path (/data/data/...)           
                { }
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
            Log($"BasicDataPath:{BasicDataPath}, DataPath:{DataPath}");
            try
            {
                if (File.Exists(Path.Combine(BasicDataPath, "settings.json")))
                {
                    var json = File.ReadAllText(Path.Combine(BasicDataPath, "settings.json"));
                    SettingsManager.Settings = new(JsonSerializer.Deserialize<Dictionary<string, string>>(json) ?? []);
                    Log($"Settings inited. Count: {SettingsManager.Settings.Count}");
                    if (SettingsManager.IsSettingExists("reset_Settings") && bool.TryParse(SettingsManager.GetSetting("reset_Settings", "true"), out var resetAll) ? resetAll : false)
                    {
                        Log("Settings reset as requested by user.");
                        SettingsManager.Settings = null!;
                        SettingsManager.ToggleSaveSignal();
                    }

                    MyLoggerExtensions.LoggingDiagnosticInfo = bool.TryParse(SettingsManager.GetSetting("LogDiagnostics", "False"), out var diagLog) ? diagLog : false;
                }
                else
                {
                    SettingsManager.Settings = new();
                    SettingsManager.ToggleSaveSignal();
                    Log("Settings inited with empty.");
                }

                if (!SettingsManager.IsSettingExists("UserID")) SettingsManager.Settings.TryAdd("UserID", Guid.NewGuid().ToString());
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
                _ = MessageBox(new nint(0), $"CRITICAL error: projectFrameCut cannot init the UserData directory because of a {ex.GetType().Name} exception:{ex.Message}\r\nYou may found your options disappeared.\r\nTry reset the data directory path.", "projectFrameCut", 0U);
#endif
            }

            try
            {
                var builder = MauiApp.CreateBuilder();
                builder.UseMauiApp<App>().UseMauiCommunityToolkit();
#if DEBUG
                builder.Logging.SetMinimumLevel(LogLevel.Trace);
#else
                builder.Logging.SetMinimumLevel(LogLevel.Information);
#endif
                builder.Logging.AddProvider(new MyLoggerProvider());
#if WINDOWS
                builder.Services.AddSingleton<IDialogueHelper, DialogueHelper>();
                try
                {
                    var userDataFolder = Path.Combine(BasicDataPath, "WebView2Data");
                    Environment.SetEnvironmentVariable("WEBVIEW2_USER_DATA_FOLDER", userDataFolder);
                }
                catch (Exception ex)
                {
                    Log(ex, "init webview2", CreateMauiApp);
                }
                builder.ConfigureMauiHandlers(handlers =>
                {
                    Microsoft.Maui.Handlers.WebViewHandler.Mapper.AppendToMapping("FixWebViewInit", (handler, view) =>
                    {
                        handler.PlatformView.CoreWebView2Initialized += (s, e) =>
                        {
                            if (handler.PlatformView.CoreWebView2 == null)
                            {
                                handler.PlatformView.EnsureCoreWebView2Async().AsTask().Wait();
                            }
                        };
                    });
                });

                
#endif
#if ANDROID
                builder.ConfigureMauiHandlers(handlers =>
                {
                    handlers.AddHandler<NativeGLSurfaceView, NativeGLSurfaceViewHandler>();
                });

                try
                {
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
                }
                catch { } //this is not very important so just let it go



#endif
                try
                {
#if ANDROID
                    var nativeLibDir = Android.App.Application.Context.ApplicationInfo.NativeLibraryDir;
                    Log($"Native library dir: {nativeLibDir}");
                    ffmpeg.RootPath = nativeLibDir;
                    JavaSystem.LoadLibrary("c");
#elif WINDOWS
                    ffmpeg.RootPath = Path.Combine(AppContext.BaseDirectory, "FFmpeg", "8.x_internal");
#endif
                    FFmpeg.AutoGen.DynamicallyLoadedBindings.ThrowErrorIfFunctionNotFound = true;
                    FFmpeg.AutoGen.DynamicallyLoadedBindings.Initialize();
                    Log($"internal FFmpeg library: version {ffmpeg.av_version_info()}, {ffmpeg.avcodec_license()}\r\nconfiguration:{ffmpeg.avcodec_configuration()}");
                }
                catch (Exception ex)
                {
                    Log(ex, "query ffmpeg version", CreateMauiApp);
#if ANDROID
                    Android.Util.Log.Wtf("projectFrameCut", $"ffmpeg may not work because of a {ex.GetType().Name} exception:{ex.Message}");
#elif WINDOWS
                    _ = MessageBox(new nint(0), $"WARN: projectFrameCut cannot init the ffmpeg to make sure it work because of a {ex.GetType().Name} exception:{ex.Message}\r\nYou can't do any render so far.", "projectFrameCut", 0U);
#endif
                }

                try
                {
                    var locate = SettingsManager.GetSetting("locate", "default");
                    CultureInfo culture = CultureInfo.CurrentCulture;
                    Log($"Your current culture: {culture.Name}, locate defined in settings:{locate} ");
                    if (locate == "default") locate = CultureInfo.CurrentCulture.Name;
                    try
                    {
                        var cul = CultureInfo.GetCultures(CultureTypes.NeutralCultures);
                        switch (locate)
                        {
                            case "zh-TW":
                                {
                                    if (!cul.Any((c) => CultureInfo.CreateSpecificCulture(c.Name).Name == "zh-TW"))
                                    {
                                        Log("zh-TW culture not found, fallback to zh-HK");
                                        culture = CultureInfo.CreateSpecificCulture("zh-HK");
                                    }
                                    else
                                    {
                                        culture = CultureInfo.CreateSpecificCulture(locate);
                                    }
                                    break;
                                }
                            case "文言文":
                                {
                                    culture = CultureInfo.CreateSpecificCulture("zh-HK");
                                    break;
                                }
                            default:
                                {
                                    if (!cul.Any((c) => CultureInfo.CreateSpecificCulture(c.Name).Name == locate))
                                    {
                                        Log($"{locate} culture not found, fallback to en-US");
                                        culture = CultureInfo.CreateSpecificCulture("en-US");
                                    }
                                    else
                                    {
                                        culture = CultureInfo.CreateSpecificCulture(locate);
                                    }
                                    break;  
                                }

                        }
                        
                    }
                    catch (Exception ex)
                    {
                        Log(ex, "init culture");
                    }

                    Localized = SimpleLocalizer.Init(locate);    
                    SettingsManager.SettingLocalizedResources = ISimpleLocalizerBase_Settings.GetMapping().TryGetValue(Localized._LocaleId_, out var loc) ? loc : ISimpleLocalizerBase_Settings.GetMapping().First().Value;

                    try
                    {
                        ConfigFontFromCulture(builder, culture);
                    }
                    catch
                    {
                        builder.ConfigureFonts(fonts =>
                        {
                            fonts.AddFont("HarmonyOS_Sans_Regular.ttf", "Font_Regular");
                            fonts.AddFont("HarmonyOS_Sans_Bold.ttf", "Font_Semibold");
                        });
                    }

                    if (!SettingsManager.IsSettingExists("OverrideCulture") || SettingsManager.GetSetting("OverrideCulture", "default") == "default") //resolve IME not work when locate isn't them
                    {
                        System.Threading.Thread.CurrentThread.CurrentCulture = culture;
                        System.Threading.Thread.CurrentThread.CurrentUICulture = culture;
                    }
                    else
                    {
                        culture = CultureInfo.CreateSpecificCulture(SettingsManager.GetSetting("OverrideCulture"));
                        System.Threading.Thread.CurrentThread.CurrentCulture = culture;
                        System.Threading.Thread.CurrentThread.CurrentUICulture = culture;
                    }

                    Log($"Culture:{System.Threading.Thread.CurrentThread.CurrentCulture}, locate:{Localized._LocaleId_}, {Localized.WelcomeMessage}");
                }
                catch (Exception ex)
                {
                    Log(ex, "init localization", CreateMauiApp);
                    SimpleLocalizer.IsFallbackMatched = true;
                    Localized = ISimpleLocalizerBase.GetMapping().First().Value;
                    SettingsManager.SettingLocalizedResources = ISimpleLocalizerBase_Settings.GetMapping().First().Value;
                    builder.ConfigureFonts(fonts =>
                    {
                        fonts.AddFont("HarmonyOS_Sans_Regular.ttf", "Font_Regular");
                        fonts.AddFont("HarmonyOS_Sans_Bold.ttf", "Font_Semibold");
                    });
                }

                Log("Everything ready!");
                var app = builder.Build();
                Log("App is ready!");
                return app;
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"FATAL Error creating MAUI App: {ex.Message}");
#if ANDROID
                Android.Util.Log.Wtf("projectFrameCut", $"Oh no! application can't be launched because of a {ex.GetType().Name} exception:{ex.Message}.");
#elif WINDOWS
                _ = MessageBox(new nint(0), $"Oh no! projectFrameCut cannot start because of a {ex.GetType().Name} exception:\r\n{ex.Message}\r\n\r\nApplication will exit now, and you'll see the detailed info later in the crash report.", "projectFrameCut", 0U);
                projectFrameCut.WinUI.App.Crash(ex);
#endif
                throw;
            }
        }

        private static object locker = new();

        private static void MyLoggerExtensions_OnLog(string msg, string level)
        {
            lock (locker) LogWriter.WriteLine($"[{DateTime.Now:T} @ {level}] {msg}");
        }
#if ANDROID
        [Obsolete("we have Fishnet to handle crashes on android, so don't use it")] //we have Fishnet to handle crashes on android, so don't use it
#endif
        public static void Crash(Exception ex)
        {
#if WINDOWS
            projectFrameCut.WinUI.App.Crash(ex);
#elif ANDROID
            Log("FATAL: unhandled exception happened.", "fatal");
            Log(ex, "Global crash");
            throw ex; //let Fishnet handle it
#endif
        }


#if WINDOWS
        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        static extern int MessageBox(IntPtr hWnd, String text, String caption, uint type);

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

        public static void ConfigFontFromCulture(MauiAppBuilder builder, CultureInfo culture)
        {
            int codePage = culture.TextInfo.ANSICodePage;

            switch (codePage)
            {
                case 936:
                    //Simplified Chinese
                    builder.ConfigureFonts(fonts =>
                    {
                        fonts.AddFont("HarmonyOS_Sans_SC_Regular.ttf", "Font_Regular");
                        fonts.AddFont("HarmonyOS_Sans_SC_Bold.ttf", "Font_Semibold");
                    });
                    break;
                case 950: //Traditional Chinese
                    builder.ConfigureFonts(fonts =>
                    {
                        fonts.AddFont("HarmonyOS_Sans_TC_Regular.ttf", "Font_Regular");
                        fonts.AddFont("HarmonyOS_Sans_TC_Bold.ttf", "Font_Semibold");
                    });
                    break;
                case 932: //Japanese
                    builder.ConfigureFonts(fonts =>
                    {
                        fonts.AddFont("NotoSansJP-VariableFont_wght.ttf", "Font_Regular");
                        fonts.AddFont("NotoSansJP-VariableFont_wght.ttf", "Font_Semibold");
                    });
                    break;
                case 949: //Korean
                    builder.ConfigureFonts(fonts =>
                    {
                        fonts.AddFont("NotoSansKR-VariableFont_wght.ttf", "Font_Regular");
                        fonts.AddFont("NotoSansKR-VariableFont_wght.ttf", "Font_Semibold");
                    });
                    break;
                case 1256: //Arabic
                    builder.ConfigureFonts(fonts =>
                    {
                        fonts.AddFont("HarmonyOS_Sans_Naskh_Arabic_Regular.ttf", "Font_Regular");
                        fonts.AddFont("HarmonyOS_Sans_Naskh_Arabic_Bold.ttf", "Font_Semibold");
                    });
                    break;
                default: //Latin and others
                    builder.ConfigureFonts(fonts =>
                    {
                        fonts.AddFont("HarmonyOS_Sans_Regular.ttf", "Font_Regular");
                        fonts.AddFont("HarmonyOS_Sans_Bold.ttf", "Font_Semibold");
                    });
                    break;
            }


        }

    }


}



