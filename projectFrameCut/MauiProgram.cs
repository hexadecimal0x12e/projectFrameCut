#pragma warning disable CS8974 //log a exception will cause this
using System;
using System.Diagnostics;
using FFmpeg.AutoGen;
using System.Reflection;
using System.Runtime.InteropServices;
using Exception = System.Exception;
using System.Text;
using System.Text.Json;
using System.Globalization;
using CommunityToolkit.Maui;
using projectFrameCut.Render.RenderAPIBase.Plugins;
using projectFrameCut.Services;
using Thread = System.Threading.Thread;
using projectFrameCut.Render.Plugin;
using Microsoft.Extensions.Logging;
using projectFrameCut.Shared;
using projectFrameCut.Asset;
using projectFrameCut.Render.RenderAPIBase.EffectAndMixture;
using System.Collections.Concurrent;

using projectFrameCut.Render.RenderAPIBase.EffectAndMixture;
using projectFrameCut.Render.VideoMakeEngine;





#if ANDROID
using projectFrameCut.Render.AndroidOpenGL.Platforms.Android;
using projectFrameCut.Platforms.Android;
using Java.Lang;

#endif

#if WINDOWS
using projectFrameCut.Platforms.Windows;
using projectFrameCut.WinUI;
using projectFrameCut.Render.WindowsRender;
#endif


namespace projectFrameCut
{
    public static class MauiProgram
    {
        public static StreamWriter LogWriter;

        public static string DataPath { get; private set; }

        public static string BasicDataPath { get; private set; }

        public static string FFmpegRoot { get; private set; }

        private static readonly string[] FoldersNeedInUserdata =
            [
            "My Drafts",
            "My Assets",
#if WINDOWS
            "My Assets\\.database",
            "My Assets\\.thumbnails"
#else
            "My Assets/.database",
            "My Assets/.thumbnails"
#endif
        ];

        public static string[] CmdlineArgs = Array.Empty<string>();


        public static MauiApp CreateMauiApp()
        {
            if (CmdlineArgs is null || CmdlineArgs.Length == 0)
            {
                try
                {
                    CmdlineArgs = Environment.GetCommandLineArgs();
                }
                catch { } //safe to ignore it
            }
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
                if (Program.UserDataPathOverride != null || Program.BasicDataPathOverride != null)
                {
                    if (!string.IsNullOrWhiteSpace(Program.BasicDataPathOverride))
                    {
                        BasicDataPath = Program.BasicDataPathOverride;
                    }
                    if (!string.IsNullOrWhiteSpace(Program.UserDataPathOverride))
                    {
                        DataPath = Program.UserDataPathOverride;
                    }
                    loggingDir = System.IO.Path.Combine(BasicDataPath, "logging");
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
                $"                  os version {Environment.OSVersion}/{DeviceInfo.Version},\r\n" +
                $"                  clr version {Environment.Version},\r\n" +
                $"                  cmdline: {Environment.CommandLine}");
            Log("Copyright (c) hexadecimal0x12e 2025, and thanks to other open-source code's authors.");
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

                try
                {
                    if (File.Exists(Path.Combine(MauiProgram.BasicDataPath, "EffectImplement.json")))
                    {
                        string json = File.ReadAllText(Path.Combine(MauiProgram.BasicDataPath, "EffectImplement.json"));
                        try
                        {
                            var dict = JsonSerializer.Deserialize<Dictionary<string, EffectImplementType>>(json);
                            if (dict != null)
                            {
                                EffectHelper.DefaultImplementsType = dict;
                            }
                        }
                        catch (Exception ex)
                        {
                            Log(ex, "read effectImplement", CreateMauiApp);
                            EffectHelper.DefaultImplementsType = new();
                        }
                    }
                    else
                    {
                        EffectHelper.DefaultImplementsType = new();
                    }
                }
                catch { }
            }
            catch (Exception ex)
            {
                Log(ex, "load settings", CreateMauiApp);
#if ANDROID
                Android.Util.Log.Wtf("projectFrameCut", $"Failed to init the settings because of a {ex.GetType().Name} exception:{ex.Message}");
#elif WINDOWS
                //_ = MessageBox(new nint(0), $"CRITICAL error: projectFrameCut cannot init the settings because of a {ex.GetType().Name} exception:{ex.Message}\r\nYour settings will be reset temporarily.\r\nTry fix the setting.json manually, or submit a issue with a screenshot of this dialogue.", "projectFrameCut", 0U);
#endif
                SettingsManager.Settings = new();
                SettingsManager.WriteSetting("_SettingFailLoad", "True");
                SettingsManager.WriteSetting("_SettingFailLoadMsg", $"An unhandled {ex.GetType().Name} exception: {ex.Message}");


            }
#if WINDOWS
            try
            {
                if (SettingsManager.IsBoolSettingTrue("DedicatedLogWindow") && !projectFrameCut.WinUI.Program.LogWindowShowing)
                {
                    Thread logThread = new Thread(Helper.HelperProgram.LogMain);
                    logThread.Name = "LogWindow thread";
                    logThread.Priority = ThreadPriority.Highest;
                    logThread.IsBackground = false;
                    logThread.Start();
                    projectFrameCut.WinUI.Program.LogWindowShowing = true;
                    Log($"Logger window started.");
                }
            }
            catch { }
#endif
            try
            {
                if (File.Exists(Path.Combine(FileSystem.AppDataDirectory, "OverrideUserDataPath.txt")))
                {
                    var newPath = File.ReadAllText(Path.Combine(FileSystem.AppDataDirectory, "OverrideUserDataPath.txt"));
                    if (!Directory.Exists(newPath))
                    {
                        Log($"User defined UserData path '{newPath}' is not exist, ignore the override.");
#if WINDOWS
                        //_ = MessageBox(new nint(0), $"CRITICAL error: projectFrameCut cannot setup the UserData because of the path your defined is not exist now.\r\nYou may found your drafts disappeared.\r\nTry reset the data directory path later.", "projectFrameCut", 0U);
#endif
                    }
                    else
                    {
                        DataPath = newPath;
                        Log($"User override Data path to:{DataPath}");
                    }

                }

                foreach (var item in FoldersNeedInUserdata)
                {
                    Directory.CreateDirectory(Path.Combine(DataPath, item));
                }

                if (!File.Exists(Path.Combine(DataPath, "My Assets",  "@WARNING.txt")))
                {
                    File.WriteAllText(Path.Combine(DataPath, "My Assets", "@WARNING.txt"),
                        """
                        WARNING: Do not modify or delete any files in this folder manually, or your assets may be corrupted!
                        """);
                }
                if (!File.Exists(Path.Combine(DataPath, "My Assets", ".database", "database.json")))
                {
                    AssetDatabase.Initialize("{}");
                }
                else
                {
                    AssetDatabase.Initialize(File.ReadAllText(Path.Combine(DataPath, "My Assets", ".database", "database.json")));
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
#pragma warning disable CA1416  //let VS shut up here
                builder.UseMauiApp<App>()
                       .UseMauiCommunityToolkit(options =>
                       {
                           options.SetShouldEnableSnackbarOnWindows(true);
                       })
#if ANDROID26_0_OR_GREATER || WINDOWS10_0_17763_0_OR_GREATER
                       .UseMauiCommunityToolkitMediaElement();
#pragma warning restore CA1416

#endif
                LogLevel logLevel = LogLevel.Information;
                if (Debugger.IsAttached || SettingsManager.IsBoolSettingTrue("LogDiagnostics"))
                {
                    if (File.Exists(Path.Combine(BasicDataPath, "trace.logging")))
                    {
                        logLevel = LogLevel.Trace;
                    }
                    else
                    {
                        logLevel = LogLevel.Debug;
                    }
                }
                builder.Logging.SetMinimumLevel(logLevel);
                builder.Logging.AddProvider(new MyLoggerProvider(logLevel));
                builder.Services.AddSingleton<IScreenReaderService, ScreenReaderService>();
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

#elif ANDROID
                builder.ConfigureMauiHandlers(handlers =>
                {
                    handlers.AddHandler<NativeGLSurfaceView, NativeGLSurfaceViewHandler>();
                });

                // Initialize Android compute helpers
                projectFrameCut.Render.AndroidOpenGL.ComputerHelper.Init();

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
                    var locate = SettingsManager.GetSetting("locate", "default");
#if !ANDROID
                    CultureInfo culture = CultureInfo.CurrentCulture;
#else
                    CultureInfo culture = projectFrameCut.Platforms.Android.DeviceLocaleHelper.GetDeviceCultureInfo();
#endif

                    Log($"OS default current culture: {culture.Name}, locate defined in settings:{locate} ");
                    if (locate == "default")
                    {
                        if (culture.Name.StartsWith("en"))
                        {
                            locate = "en-US";
                        }
                        else
                        {
                            locate = culture.Name;
                        }
                    }
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
                    SimpleLocalizerBaseGeneratedHelper_PropertyPanel.PPLocalizedResuorces = ISimpleLocalizerBase_PropertyPanel.GetMapping().TryGetValue(Localized._LocaleId_, out var pploc) ? pploc : ISimpleLocalizerBase_PropertyPanel.GetMapping().First().Value;
                    PluginManager.CurrentLocale = Localized._LocaleId_;
                    PluginManager.ExtenedLocalizationGetter = new((k) =>
                    {
                        var r = Localized.DynamicLookup(k, "!!!NULL!!!");
                        return r == "!!!NULL!!!" ? null : r;
                    });

                    try
                    {
                        if (!SettingsManager.IsSettingExists("UseSystemFont")) ConfigFontFromCulture(builder, culture);
                    }
                    catch
                    {
                        builder.ConfigureFonts(fonts =>
                        {
                            fonts.AddFont("HarmonyOS_Sans_Regular.ttf", "Font_Regular");
                            fonts.AddFont("HarmonyOS_Sans_Bold.ttf", "Font_Semibold");
                        });
                    }

                    if (SettingsManager.IsSettingExists("OverrideCulture") && SettingsManager.GetSetting("OverrideCulture", "default") != "default") //resolve IME not work when locate isn't them
                    {
                        culture = CultureInfo.CreateSpecificCulture(SettingsManager.GetSetting("OverrideCulture"));
                    }
                    if (locate != "default")
                    {
                        Thread.CurrentThread.CurrentCulture = culture;
                        Thread.CurrentThread.CurrentUICulture = culture;
                        CultureInfo.DefaultThreadCurrentCulture = culture;
                        CultureInfo.DefaultThreadCurrentUICulture = culture;
                    }


                    Log($"Culture:{Thread.CurrentThread.CurrentCulture}, locate:{Localized._LocaleId_}, {Localized.WelcomeMessage}");
                }
                catch (Exception ex)
                {
                    Log(ex, "init localization", CreateMauiApp);
                    SimpleLocalizer.IsFallbackMatched = true;
                    Localized = ISimpleLocalizerBase.GetMapping().First().Value;
                    SettingsManager.SettingLocalizedResources = ISimpleLocalizerBase_Settings.GetMapping().First().Value;
                    SimpleLocalizerBaseGeneratedHelper_PropertyPanel.PPLocalizedResuorces = ISimpleLocalizerBase_PropertyPanel.GetMapping().First().Value;
                    PluginManager.CurrentLocale = "en-US";
                    PluginManager.ExtenedLocalizationGetter = new((k) => ISimpleLocalizerBase.GetMapping().First().Value.DynamicLookup(k));
                    builder.ConfigureFonts(fonts =>
                    {
                        fonts.AddFont("HarmonyOS_Sans_Regular.ttf", "Font_Regular");
                        fonts.AddFont("HarmonyOS_Sans_Bold.ttf", "Font_Semibold");
                    });
                }
                try
                {
                    MessagingServices.Init();
                }
                catch (Exception ex)
                {
                    Log(ex, "init messaging service", CreateMauiApp);
                }

                try
                {
                    var internalBase = new InternalApplicationPluginBase();
                    internalBase.MessagingQueue = MessagingServices.MessagingService;
                    List<IPluginBase> plugins = new() { internalBase };
#if ANDROID
                    plugins.Add(new OpenGLPlugin());
#elif WINDOWS
                    plugins.Add(new ILGPUPlugin());
#elif iDevices

#endif
                    try
                    {
                        if (Environment.GetCommandLineArgs().Contains("--forceLoadPlugins") || (!AdminHelper.IsRunningAsAdministrator() && !Environment.GetCommandLineArgs().Contains("--disablePlugins") && !SettingsManager.IsBoolSettingTrue("DisablePluginEngine")))
                        {
                            plugins.AddRange(PluginService.LoadUserPlugins());
                        }
                        else
                        {
                            if (AdminHelper.IsRunningAsAdministrator()) Log("Running as administrator, skip load user plugins for security reason.", "warn");
                            else Log("User disabled the plugin engine.");
                            PluginService.FailedLoadPlugin.Add("<No plugin ID available>", AdminHelper.IsRunningAsAdministrator() ? Localized.PluginEngine_DisabledBecauseAdmin : Localized.PluginEngine_DisabledBecauseUserDisabled);
                        }
                    }
                    catch (Exception ex)
                    {
                        Log(ex, "load user plugins", CreateMauiApp);
                    }

                    PluginManager.Init(plugins);
                }
                catch (Exception ex)
                {
                    Log(ex, "Load plugins", CreateMauiApp);
                    try
                    {
                        if (!PluginManager.Inited)
                        {
                            PluginManager.Init([new InternalPluginBase()]);
                            PluginService.FailedLoadPlugin.Add("<No plugin ID available>", $"Plugin engine fail to init. ({ex})");

                        }
                    }
                    catch (Exception ex1)
                    {
                        Log(ex1, "try load internal plugin", CreateMauiApp);
#if ANDROID
                        Android.Util.Log.Wtf("projectFrameCut", $"FATAL: The pluginBase cannot be loaded. projectFrameCut may not work at all.\r\n(a {ex.GetType().Name} exception happends, {ex.Message})");
#elif WINDOWS
                        _ = MessageBox(new nint(0), $"FATAL: The pluginBase cannot be loaded. projectFrameCut may not work at all.\r\n(a {ex.GetType().Name} exception happens, {ex.Message})", "projectFrameCut", 0U);
#endif
                    }
                }

                try
                {
                    string? nativeLibDirOverride = null;
                    try
                    {
                        if (SettingsManager.IsBoolSettingTrue("PluginProvidedFFmpeg_Enable"))
                        {
#if WINDOWS
                            var pluginId = SettingsManager.GetSetting("PluginProvidedFFmpeg_PluginID", "");
                            if (!PluginManager.LoadedPlugins.TryGetValue(pluginId, out var value))
                            {
                                Log($"PluginProvidedFFmpeg_Enable is true, but plugin {pluginId} is not loaded.");
                            }
                            else
                            {
                                var ffmpegPath = Path.Combine(BasicDataPath, "Plugins", value.PluginID, "FFmpeg", "windows");
                                if (!string.IsNullOrWhiteSpace(ffmpegPath) && Directory.Exists(ffmpegPath))
                                {
                                    Log($"Using FFmpeg libraries provided by plugin {pluginId}, path:{ffmpegPath}");
                                    nativeLibDirOverride = ffmpegPath;
                                }
                                else
                                {
                                    Log($"PluginProvidedFFmpeg_Enable is true, but plugin {pluginId} provided invalid path:{ffmpegPath}");
                                }
                            }
#elif ANDROID
                            nativeLibDirOverride = Path.Combine(FileSystem.AppDataDirectory, "ffmpeg_plugin_libs");
#endif
                        }
                    }
                    catch { }
#if ANDROID
                    FFmpegRoot = nativeLibDirOverride ?? Android.App.Application.Context.ApplicationInfo?.NativeLibraryDir;
                    JavaSystem.LoadLibrary("c");
#elif WINDOWS
                    FFmpegRoot = nativeLibDirOverride ?? Path.Combine(AppContext.BaseDirectory, "FFmpeg", "8.x_internal");
#endif
                    ffmpeg.RootPath = FFmpegRoot;
                    Log($"FFmpeg library root path: {ffmpeg.RootPath}");
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
                    //_ = MessageBox(new nint(0), $"WARN: projectFrameCut cannot init the ffmpeg to make sure it work because of a {ex.GetType().Name} exception:{ex.Message}\r\nYou can't do any render so far.", "projectFrameCut", 0U);
#endif
                    ffmpegFailMessage = ex.Message;
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

        public static string? ffmpegFailMessage = null;

        private static object locker = new();

        [DebuggerNonUserCode()]
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
#if !ANDROID
                    builder.ConfigureFonts(fonts =>
                    {
                        fonts.AddFont("HarmonyOS_Sans_SC_Regular.ttf", "Font_Regular");
                        fonts.AddFont("HarmonyOS_Sans_SC_Bold.ttf", "Font_Semibold");
                    });
#else
                    //use system ones
#endif
                    break;
                case 949: //Korean
#if !ANDROID
                    builder.ConfigureFonts(fonts =>
                    {
                        fonts.AddFont("NotoSansKR-Regular.ttf", "Font_Regular");
                        fonts.AddFont("NotoSansKR-Bold.ttf", "Font_Semibold");
                    });
#else
                    //use system ones
#endif
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



