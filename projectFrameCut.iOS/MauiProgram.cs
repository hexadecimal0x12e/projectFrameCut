using Microsoft.Extensions.Logging;
using projectFrameCut.iDevicesAPI;
using projectFrameCut.Shared;
using System.Reflection;
using System.Reflection.Metadata;
using System.Runtime.InteropServices;
using Microsoft.Maui.LifecycleEvents;
using projectFrameCut.Setting.SettingManager;
using System.Text.Json;
using System.Globalization;
using System.Diagnostics;
using CommunityToolkit.Maui;
using projectFrameCut.Render.Plugin;
using projectFrameCut.MetalAccelerater;
using projectFrameCut.Render.RenderAPIBase.Plugins;

using projectFrameCut.Services;








#if iDevices
using UIKit;
using projectFrameCut.Platforms;

#endif


namespace projectFrameCut
{
    public static class MauiProgram
    {
        public static string DataPath { get; private set; }

        public static string BasicDataPath { get; private set; }

        private static readonly string[] FoldersNeedInUserdata =
            [
            "My Drafts",
            "My Assets"
            ];
        private static object locker;

        public static StreamWriter LogWriter { get; internal set; }

        public static string[] CmdlineArgs = Array.Empty<string>();


        public static MauiApp CreateMauiApp()
        {
            System.Threading.Thread.CurrentThread.Name = "App Main thread";
            if (CmdlineArgs is null || CmdlineArgs.Length == 0)
            {
                try
                {
                    CmdlineArgs = Environment.GetCommandLineArgs();
                }
                catch { } //safe to ignore it
            }
            System.Threading.Thread.CurrentThread.Name = "App Main thread";
            string loggingDir = "";
            try
            {
                loggingDir = System.IO.Path.Combine(FileSystem.AppDataDirectory, "logging");
                DataPath = FileSystem.AppDataDirectory;
#if WINDOWS
#elif IOS
                //files->my [iDevices]->projectFrameCut
                DataPath = Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);

#elif MACCATALYST
                DataPath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "projectFrameCut");
                loggingDir = System.IO.Path.Combine(DataPath, "logging");
#endif
                Directory.CreateDirectory(loggingDir);
                LogWriter = new StreamWriter(System.IO.Path.Combine(loggingDir, $"log-{DateTime.Now:yyyy-MM-dd-HH-mm-ss}.log"), append: true)
                {
                    AutoFlush = true
                };

                MyLoggerExtensions.OnLog += MyLoggerExtensions_OnLog;

                try
                {
                    Directory.CreateDirectory(DataPath);
                    BasicDataPath = DataPath;
                }
                catch (Exception ex)
                {
                    Log(ex, "create userdata dir", typeof(MauiProgram));
                    DataPath = FileSystem.AppDataDirectory;
                    BasicDataPath = DataPath;
                }

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
            Log($"BasicDataPath:{BasicDataPath}, DataPath:{DataPath}");
            try
            {
                if (File.Exists(Path.Combine(BasicDataPath, "settings.json")))
                {
                    var json = File.ReadAllText(Path.Combine(BasicDataPath, "settings.json"));
                    try
                    {
                        SettingsManager.Settings = new(JsonSerializer.Deserialize<Dictionary<string, string>>(json) ?? []);
                    }
                    catch (Exception ex2)
                    {
                        try
                        {
                            json = File.ReadAllText(Path.Combine(BasicDataPath, "settings_a.json"));
                            SettingsManager.Settings = new(JsonSerializer.Deserialize<Dictionary<string, string>>(json) ?? []);
                        }
                        catch (Exception ex3)
                        {
                            try
                            {
                                json = File.ReadAllText(Path.Combine(BasicDataPath, "settings_b.json"));
                                SettingsManager.Settings = new(JsonSerializer.Deserialize<Dictionary<string, string>>(json) ?? []);
                            }
                            catch (Exception ex4)
                            {
                                throw new AggregateException($"Failed to load settings from all slots.\r\n\r\n{ex2.GetType().Name} ({ex2.Message})", [ex2, ex3, ex4]);
                            }
                        }
                    }
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

                if (!SettingsManager.IsSettingExists("UserID") || string.IsNullOrWhiteSpace(SettingsManager.GetSetting("UserID")))
                {
                    SettingsManager.Settings.AddOrUpdate("UserID", Guid.NewGuid().ToString(), (_, v) => string.IsNullOrWhiteSpace(v) ? Guid.NewGuid().ToString() : v);
                    SettingsManager.ToggleSaveSignal();
                }
            }
            catch (Exception ex)
            {
                Log(ex, "load settings", typeof(MauiProgram));
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
                Log(ex, "setup user data dir", typeof(MauiProgram));
#if ANDROID
                Android.Util.Log.Wtf("projectFrameCut", $"Failed to init the settings because of a {ex.GetType().Name} exception:{ex.Message}");
#elif WINDOWS
                _ = MessageBox(new nint(0), $"CRITICAL error: projectFrameCut cannot init the UserData directory because of a {ex.GetType().Name} exception:{ex.Message}\r\nYou may found your options disappeared.\r\nTry reset the data directory path.", "projectFrameCut", 0U);
#endif
            }

            try
            {
                var builder = MauiApp.CreateBuilder();
                builder.UseMauiApp<App>()
                       .UseMauiCommunityToolkit(options =>
                       {
                           options.SetShouldEnableSnackbarOnWindows(true);
                       })
#if IOS15_0_OR_GREATER 
                       .UseMauiCommunityToolkitMediaElement();
#else
;
#endif
#if DEBUG
                builder.Logging.SetMinimumLevel(LogLevel.Trace);
#else
                builder.Logging.SetMinimumLevel(LogLevel.Information);
#endif
                builder.Logging.AddProvider(new MyLoggerProvider());

                //                 try
                //                 {
                // #if ANDROID
                //                     var nativeLibDir = Android.App.Application.Context.ApplicationInfo.NativeLibraryDir;
                //                     Log($"Native library dir: {nativeLibDir}");
                //                     ffmpeg.RootPath = nativeLibDir;
                //                     JavaSystem.LoadLibrary("c");
                // #elif WINDOWS
                //                     ffmpeg.RootPath = Path.Combine(AppContext.BaseDirectory, "FFmpeg", "8.x_internal");
                // #endif
                //                     FFmpeg.AutoGen.DynamicallyLoadedBindings.ThrowErrorIfFunctionNotFound = true;
                //                     FFmpeg.AutoGen.DynamicallyLoadedBindings.Initialize();
                //                     Log($"internal FFmpeg library: version {ffmpeg.av_version_info()}, {ffmpeg.avcodec_license()}\r\nconfiguration:{ffmpeg.avcodec_configuration()}");
                //                 }
                //                 catch (Exception ex)
                //                 {
                //                     Log(ex, "query ffmpeg version", CreateMauiApp);
                // #if ANDROID
                //                     Android.Util.Log.Wtf("projectFrameCut", $"ffmpeg may not work because of a {ex.GetType().Name} exception:{ex.Message}");
                // #elif WINDOWS
                //                     _ = MessageBox(new nint(0), $"WARN: projectFrameCut cannot init the ffmpeg to make sure it work because of a {ex.GetType().Name} exception:{ex.Message}\r\nYou can't do any render so far.", "projectFrameCut", 0U);
                // #endif
                //                 }

                try
                {
                    var locate = SettingsManager.GetSetting("locate", "default");
#if !ANDROID
                    CultureInfo culture = CultureInfo.CurrentCulture;
#else
                    CultureInfo culture = projectFrameCut.Platforms.Android.DeviceLocaleHelper.GetDeviceCultureInfo();
#endif
                    Log($"OS default current culture: {culture.Name}, locate defined in settings:{locate} ");
                    if (locate == "default") locate = culture.Name;
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
                    Log(ex, "init localization", typeof(MauiProgram));
                    SimpleLocalizer.IsFallbackMatched = true;
                    Localized = ISimpleLocalizerBase.GetMapping().First().Value;
                    SettingsManager.SettingLocalizedResources = ISimpleLocalizerBase_Settings.GetMapping().First().Value;
                    builder.ConfigureFonts(fonts =>
                    {
                        fonts.AddFont("HarmonyOS_Sans_Regular.ttf", "Font_Regular");
                        fonts.AddFont("HarmonyOS_Sans_Bold.ttf", "Font_Semibold");
                    });
                }

                try
                {
                    MetalComputerHelper.RegisterComputerBridge();
                }
                catch { }

                try
                {
                    List<IPluginBase> plugins = [new InternalPluginBase()];
#if DEBUG
                    plugins.AddRange(PluginService.LoadUserPlugins());
#endif

                    PluginManager.Init(plugins);
                }
                catch (Exception ex)
                {
                    Log(ex, "Load plugins", CreateMauiApp);
                }
                Log("Everything ready!");
                var app = builder.Build();
                Log("App is ready!");
                return app;
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"FATAL Error creating MAUI App: {ex.Message}");

                throw;
            }
        }
        private static void MyLoggerExtensions_OnLog(string msg, string level)
        {
            lock (locker) LogWriter.WriteLine($"[{DateTime.Now:T} @ {level}] {msg}");
        }
        public static void Crash(Exception ex)
        {
#if IOS
            projectFrameCut.Platforms.iOS.Program.Crash(ex);
#endif
        }
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
                        fonts.AddFont("NotoSansJP-Regular.ttf", "Font_Regular");
                        fonts.AddFont("NotoSansJP-Bold.ttf", "Font_Semibold");
                    });
                    break;
                case 949: //Korean
                    builder.ConfigureFonts(fonts =>
                    {
                        fonts.AddFont("NotoSansKR-Regular.ttf", "Font_Regular");
                        fonts.AddFont("NotoSansKR-Bold.ttf", "Font_Semibold");
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
