#pragma warning disable CS8974 //log a exception will cause this
using CommunityToolkit.Maui;
using CommunityToolkit.Maui.Core;
using FFmpeg.AutoGen;
using FFmpeg.AutoGen.Native;
using LocalizedResources;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Maui.LifecycleEvents;
using projectFrameCut.AIAssistance;
using projectFrameCut.ApplicationAPIBase.Effect;
using projectFrameCut.ApplicationAPIBase.Helpers;
using projectFrameCut.ApplicationAPIBase.Plugins;
using projectFrameCut.ApplicationAPIBase.Views.Pickers;
using projectFrameCut.ApplicationPluginBase;
using projectFrameCut.Asset;
using projectFrameCut.Drawing.Text.FontHelper;
using projectFrameCut.Render.ClipsAndTracks.Text;
using projectFrameCut.Render.Effect;
using projectFrameCut.Render.EncodeAndDecode;
using projectFrameCut.Render.Plugin;
using projectFrameCut.Render.RenderAPIBase.EffectAndMixture;
using projectFrameCut.Render.RenderAPIBase.Plugins;
using projectFrameCut.Render.RenderAPIBase.Project;
using projectFrameCut.Render.TemplateSystem;
using projectFrameCut.Services;
using projectFrameCut.Shared;
using projectFrameCut.Template;
using System;
using System.Diagnostics;
using System.Globalization;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using Exception = System.Exception;
using Thread = System.Threading.Thread;

#if ANDROID
using projectFrameCut.Render.HwAccelEngine.Platforms.Android;
using Java.Lang;

#endif

#if WINDOWS
using projectFrameCut.Platforms.Windows;
using projectFrameCut.WinUI;
using projectFrameCut.Render.WindowsRender;
#endif

#if LINUX
using projectFrameCut.Platforms.Linux;
using Microsoft.Maui.Platforms.Linux.Gtk4.Essentials.Hosting;
using Microsoft.Maui.Platforms.Linux.Gtk4.Hosting;
#endif

#if IOS
using projectFrameCut.Platforms.iOS;
#endif

#if MACOS
using projectFrameCut.Platforms.MacOS;
#endif

namespace projectFrameCut
{
    public static class MauiProgram
    {
        internal static IReadOnlyList<IPluginBase> IntegratedPlugins { get; private set; } = [];
        public static StreamWriter LogWriter;

        public static string LogPath { get; private set; }

        public static string DataPath { get; private set; }

        public static string BasicDataPath { get; private set; }

        public static string FFmpegRoot { get; private set; }

        public static string ProgramConfig = "?", ProgramCommit = "?", AssemblyName = "projectFrameCut";

        private static readonly string[] FoldersNeedInUserdata =
        [
            "My Drafts",
            "My Assets",
            "My Templates",
            "My Skills",
            "RenderCache",
#if WINDOWS
            "My Assets\\.database",
            "My Assets\\.thumbnails",
            "My Assets\\.perAssetThumb",
#else
            "My Assets/.database",
            "My Assets/.thumbnails",
            "My Assets/.perAssetThumb",
            "Logs",
            "AppData",
#endif
        ];

        public static string[] CmdlineArgs = Array.Empty<string>();

        public static bool IsStoreMode { get; private set; } = true;

        /// <summary>
        /// Initializes the localization state needed by the Android render-worker
        /// process. The worker has a deliberately minimal MAUI bootstrap and does
        /// not execute the normal application initialization path, so it must
        /// receive the already-selected locale through its start Intent.
        /// </summary>
        public static void InitializeWorkerLocalization(string? locate)
        {
            locate = string.IsNullOrWhiteSpace(locate) ? "en-US" : locate;
            try
            {
                Localized = SimpleLocalizer.Init(locate);
            }
            catch
            {
                SimpleLocalizer.IsFallbackMatched = true;
                Localized = ISimpleLocalizerBase.GetMapping().First().Value;
            }

            SettingsManager.SettingLocalizedResources = ISimpleLocalizerBase_Settings.GetMapping()
                .TryGetValue(Localized._LocaleId_, out var settingsLocalized)
                ? settingsLocalized
                : ISimpleLocalizerBase_Settings.GetMapping().First().Value;
            SimpleLocalizerBaseGeneratedHelper_PropertyPanel.PPLocalizedResources = ISimpleLocalizerBase_PropertyPanel.GetMapping()
                .TryGetValue(Localized._LocaleId_, out var propertyPanelLocalized)
                ? propertyPanelLocalized
                : ISimpleLocalizerBase_PropertyPanel.GetMapping().First().Value;
            projectFrameCut.ApplicationAPIBase.Localize.APIBaseLocalizedResources.Localized = ApplicationAPIBaseLocalizerBase.GetMapping()
                .TryGetValue(Localized._LocaleId_, out var apiLocalized)
                ? apiLocalized
                : ApplicationAPIBaseLocalizerBase.GetMapping().First().Value;
            PluginManager.CurrentLocale = Localized._LocaleId_;
            PluginManager.ExtenedLocalizationGetter = key =>
                Localized.IsItemExist(key) ? Localized.DynamicLookup(key, key) : null;

            try
            {
                var cultureName = locate == "文言文" ? "zh-HK" : Localized._LocaleId_;
                var culture = CultureInfo.CreateSpecificCulture(cultureName);
                Thread.CurrentThread.CurrentCulture = culture;
                Thread.CurrentThread.CurrentUICulture = culture;
                CultureInfo.DefaultThreadCurrentCulture = culture;
                CultureInfo.DefaultThreadCurrentUICulture = culture;
            }
            catch
            {
                // Localization must not prevent the worker service from starting.
            }
        }

        public static MauiApp CreateMauiApp()
        {
#if ANDROID
            if (projectFrameCut.Platforms.Android.MainApplication.IsRenderWorkerProcess
                || global::Android.App.Application.ProcessName?.EndsWith(":renderworker", StringComparison.Ordinal) == true)
            {
                DataPath = FileSystem.AppDataDirectory;
                BasicDataPath = Path.Combine(FileSystem.AppDataDirectory, "AppData");
                Directory.CreateDirectory(BasicDataPath);
                return MauiApp.CreateBuilder()
                    .UseMauiApp<projectFrameCut.Platforms.Android.RenderWorkerMauiApplication>()
                    .Build();
            }
#endif
            if (CmdlineArgs is null || CmdlineArgs.Length == 0)
            {
                try
                {
                    CmdlineArgs = Environment.GetCommandLineArgs().Skip(1).ToArray();
                }
                catch { } //safe to ignore it
            }
            string loggingDir = "";
            try
            {
#if !LINUX
                loggingDir = System.IO.Path.Combine(FileSystem.AppDataDirectory, "logging");
                DataPath = FileSystem.AppDataDirectory;
                BasicDataPath = FileSystem.AppDataDirectory;
#endif

#if ANDROID
                try
                {
                    var pfn = Android.App.Application.Context.PackageName;
                    IsStoreMode = pfn?.EndsWith("store", StringComparison.InvariantCultureIgnoreCase) ?? false;
                    var userAccessblePath = $"/sdcard/Android/data/{pfn}/";
                    if (Path.Exists(userAccessblePath))
                    {
                        DataPath = userAccessblePath;
                        BasicDataPath = Path.Combine(userAccessblePath, "AppData");
                        loggingDir = Path.Combine(userAccessblePath, "Logs");
                    }
                }
                catch //use the default path (/data/data/...)           
                { }
#elif WINDOWS
                if (projectFrameCut.Helper.HelperProgram.AppChannel.Equals("MS Store", StringComparison.InvariantCultureIgnoreCase)) // <AppContainer Data>\LocalState
                {
                    Directory.CreateDirectory(Path.Combine(FileSystem.AppDataDirectory, "AppData"));
                    Directory.CreateDirectory(Path.Combine(FileSystem.AppDataDirectory, "UserData"));
                    DataPath = Path.Combine(FileSystem.AppDataDirectory, "UserData");
                    BasicDataPath = Path.Combine(FileSystem.AppDataDirectory, "AppData");
                }
                else
                {
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

                IsStoreMode = WinUI.Program.IsStoreModeEnabled;
#elif IOS
                DataPath = Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);
                // iOS's Environment.GetFolderPath can return empty under hardened
                // sandbox. Fall back to MAUI's FileSystem (Foundation NSSearchPath).
                if (string.IsNullOrEmpty(DataPath))
                {
                    BasicDataPath = Path.Combine(DataPath, "AppData");
                }
                else
                {
                    DataPath = FileSystem.AppDataDirectory;
                }
                   
                loggingDir = Path.Combine(DataPath, "Logs");
                // iOS sandbox: POSIX stat through container root returns EPERM.
                // .NET's Path.GetFullPath → realpath() resolves each component —
                // including the protected root. Fall back to NSFileManager.
                try
                {
                    Directory.CreateDirectory(BasicDataPath);
                    Directory.CreateDirectory(loggingDir);
                }
                catch (UnauthorizedAccessException)
                {
                    Foundation.NSFileManager.DefaultManager.CreateDirectory(
                        Foundation.NSUrl.FromFilename(loggingDir), true, null, out _);
                    Foundation.NSFileManager.DefaultManager.CreateDirectory(
                        Foundation.NSUrl.FromFilename(BasicDataPath), true, null, out _);
                }

#elif MACCATALYST
                DataPath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "projectFrameCut");
#elif LINUX
                var homePath = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
                Directory.CreateDirectory(Path.Combine(homePath, ".projectFrameCut"));
                BasicDataPath = Path.Combine(homePath, ".projectFrameCut", "AppData");
                DataPath = Path.Combine(homePath, ".projectFrameCut");
                loggingDir = Path.Combine(homePath, ".projectFrameCut", "Logs");
                Directory.CreateDirectory(BasicDataPath);

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

#if !iDevices
                Directory.CreateDirectory(loggingDir);
#endif
                try
                {
                    Directory.CreateDirectory(DataPath);
                }
                catch (Exception ex)
                {
                    Log(ex, "create userdata dir", CreateMauiApp);
                    DataPath = FileSystem.AppDataDirectory;
                }
                LogPath = System.IO.Path.Combine(loggingDir, $"log-{DateTime.Now:yyyy-MM-dd-HH-mm-ss}.log");
                LogWriter = new StreamWriter(LogPath, append: true)
                {
                    AutoFlush = true
                };

                MyLoggerExtensions.OnLog += MyLoggerExtensions_OnLog;

                IApplicationPluginBase.AppDataRoot = BasicDataPath;
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"Failed to set up log file: {ex.Message}");
                Crash(new InvalidOperationException($"projectFrameCut can't initialize BasicData. Try uninstall program, cleanup BasicData and reinstall program.", ex));
            }
            try
            {
                ProgramConfig = Assembly.GetExecutingAssembly().GetCustomAttribute<AssemblyConfigurationAttribute>()?.Configuration ?? "unknown config";
                ProgramCommit = (Assembly.GetExecutingAssembly().GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion ?? "0.1.2+unknown commit").Split('+').Last();
                AssemblyName = Assembly.GetExecutingAssembly().GetCustomAttribute<AssemblyTitleAttribute>()?.Title ?? "projectFrameCut";
            }
            catch { }
#if MAUISDK
            Log($"""
                projectFrameCut - v{Assembly.GetExecutingAssembly().GetName().Version} 
                                  {ProgramConfig}@{ProgramCommit},
                                  on {DeviceInfo.Platform} in cpu arch {RuntimeInformation.ProcessArchitecture},
                                  os version {Environment.OSVersion}/{DeviceInfo.Version},
                                  clr version {Environment.Version},
                                  cmdline: {Environment.CommandLine}
                """);
#else
            Log($"""
                projectFrameCut - v{Assembly.GetExecutingAssembly().GetName().Version} 
                                  {ProgramConfig}@{ProgramCommit},
                                  in cpu arch {RuntimeInformation.ProcessArchitecture},
                                  os version {Environment.OSVersion},
                                  clr version {Environment.Version},
                                  cmdline: {Environment.CommandLine}
                """);
#endif
            Log("Copyright (c) hexadecimal0x12e 2025-2026, and thanks to other open-source code's authors.");
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
                    if (SettingsManager.IsBoolSettingTrue("reset_Settings"))
                    {
                        Log("Settings reset as requested by user.");
                        SettingsManager.Settings = null!;
                        SettingsManager.ToggleSaveSignal();
                        Preferences.Clear();
                    }

                    if (SettingsManager.IsBoolSettingTrue("LogDiagnostics") || ProgramConfig == "Debug")
                        MyLoggerExtensions.LoggingDiagnosticInfo = true;

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
                Log(ex, "load settings", CreateMauiApp);
                try
                {
                    File.Copy(Path.Combine(BasicDataPath, "settings.json"), Path.Combine(BasicDataPath, "settings.json.bak"));
                    File.Copy(Path.Combine(BasicDataPath, "settings_a.json"), Path.Combine(BasicDataPath, "settings_a.json.bak"));
                    File.Copy(Path.Combine(BasicDataPath, "settings_b.json"), Path.Combine(BasicDataPath, "settings_b.json.bak"));
                }
                catch { }
#if ANDROID
                Android.Util.Log.Wtf("projectFrameCut", $"Failed to init the settings because of a {ex.GetType().Name} exception:{ex.Message}");
#elif WINDOWS
                //_ = MessageBox(new nint(0), $"CRITICAL error: projectFrameCut cannot init the settings because of a {ex.GetType().Name} exception:{ex.Message}\r\nYour settings will be reset temporarily.\r\nTry fix the setting.json manually, or submit a issue with a screenshot of this dialogue.", "projectFrameCut", 0U);
#endif
                SettingsManager.Settings = new();
                SettingsManager.WriteSetting("_SettingFailLoad", "True");
                SettingsManager.WriteSetting("_SettingFailLoadMsg", $"An unhandled {ex.GetType().Name} exception: {ex.Message}");


            }
            var locate = SettingsManager.GetSetting("locate", "default");
#if !ANDROID
            CultureInfo culture = CultureInfo.CurrentCulture;
#else
            CultureInfo culture = projectFrameCut.Platforms.Android.DeviceLocaleHelper.GetDeviceCultureInfo();
#endif
            InitLocate(ref locate, ref culture);


            var backgroundInitThread = new Thread(BackgroundInit)
            {
                Name = "BackgroundInit",
                IsBackground = true,
                Priority = ThreadPriority.BelowNormal
            };

#if WINDOWS
            try
            {
                if (SettingsManager.IsBoolSettingTrue("DedicatedLogWindow") && !Program.LogWindowShowing)
                {
                    Thread logThread = new Thread(Helper.HelperProgram.LogMain)
                    {
                        Name = "LogWindow thread",
                        Priority = ThreadPriority.Highest,
                        IsBackground = false
                    };
                    logThread.Start();
                    Program.LogWindowShowing = true;
                    Log($"Logger window started.");
                }
            }
            catch { }
#endif
            try
            {
                if (File.Exists(Path.Combine(BasicDataPath, "OverrideUserDataPath.txt")))
                {
                    var newPath = File.ReadAllText(Path.Combine(BasicDataPath, "OverrideUserDataPath.txt"));
                    if (!Directory.Exists(newPath))
                    {
                        Log($"User defined UserData path '{newPath}' is not exist, ignore the override.");
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

            }
            catch (Exception ex)
            {
                Log(ex, "setup user data dir", CreateMauiApp);
#if ANDROID
                Android.Util.Log.Wtf("projectFrameCut", $"Failed to init the userdata because of a {ex.GetType().Name} exception:{ex.Message}");
#elif WINDOWS
                _ = WinUI.App.MessageBox(new nint(0), $"CRITICAL error: projectFrameCut cannot init the UserData directory because of a {ex.GetType().Name} exception:{ex.Message}\r\nYou may found your options disappeared.\r\nTry reset the data directory.", "projectFrameCut", 0U);
#endif
            }

            try
            {
                var builder = MauiApp.CreateBuilder();
#pragma warning disable CA1416  //let VS shut up here
                builder
#if MAUISDK
                       .UseMauiApp<App>()
#elif LINUX
                       .UseMauiAppLinuxGtk4<App>()
                       .AddLinuxGtk4Essentials()
#endif
                       .UseMauiCommunityToolkit(options =>
                       {
                           options.SetShouldEnableSnackbarOnWindows(false);
                       });
#if MAUISDK && (ANDROID26_0_OR_GREATER || WINDOWS10_0_17763_0_OR_GREATER || IOS15_0_OR_GREATER)
                builder.UseMauiCommunityToolkitMediaElement(isAndroidForegroundServiceEnabled: false, static options =>
                {
                    options.SetDefaultAndroidViewType(AndroidViewType.TextureView);
                });
#endif
#if MAUISDK
                builder.ConfigureEssentials(essentials =>
                {
                    essentials.UseVersionTracking();
                });
#endif

#pragma warning restore CA1416
                var lastPath = SettingsManager.GetSetting("General_LastOpenedProject", "");
                if (!string.IsNullOrWhiteSpace(lastPath) && Directory.Exists(lastPath))
                {
#if MAUISDK
                    try
                    {
                        var dirName = Path.GetFileName(Path.GetDirectoryName(lastPath).TrimEnd(Path.DirectorySeparatorChar));
                        builder = builder.ConfigureEssentials(essentials =>
                        {
                            essentials.AddAppAction("--continue", Localized.HomePage_Continue(dirName?.Split('\\')?.Last() ?? "Project"), icon: "icon_project")
                                      .OnAppAction(HomePage.HandleAppActionLaunch);
                        });
                    }
                    catch { }
#endif
                }
                try
                {
                    Log($"StoreMode: {IsStoreMode}, StoreModeOverride: {SettingsManager.GetSetting("StoreModeOverride", "disable")}");

                    if (SettingsManager.GetSetting("StoreModeOverride", "disable") != "disable")
                    {
                        IsStoreMode = SettingsManager.IsBoolSettingTrue("StoreModeOverride");
                    }
                }
                catch { }
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
                builder.Services.AddSingleton<UIThreadWatchdogService>();
#if WINDOWS
                builder.Services.AddSingleton<IDialogueHelper, DialogueHelper>();
                builder.Services.AddSingleton<projectFrameCut.Services.AIComponent.IAIComponentClient, projectFrameCut.Services.AIComponent.WindowsAIComponentClient>();
                builder.Services.AddSingleton<projectFrameCut.Services.AIComponent.WindowsVideoSuperResolutionProcessor>();
                builder.Services.AddSingleton<projectFrameCut.Services.AIComponent.IIntegratedAIComponent>(services =>
                    services.GetRequiredService<projectFrameCut.Services.AIComponent.WindowsVideoSuperResolutionProcessor>());
#elif ANDROID
                builder.ConfigureMauiHandlers(handlers =>
                {
                    handlers.AddHandler<NativeGLSurfaceView, NativeGLSurfaceViewHandler>();
                    handlers.AddHandler<NativeVulkanSurfaceView, NativeVulkanSurfaceViewHandler>();
                });



                try
                {
                    MyLoggerExtensions.OnLog += [DebuggerNonUserCode()] (msg, level) =>
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
                Preferences.Remove("LaunchedPJFCUri");

                builder.ConfigureLifecycleEvents(lifecycle =>
                {
                    lifecycle.AddAndroid(android =>
                    {
                        android.OnCreate(async (activity, bundle) =>
                        {
                            var action = activity.Intent?.Action;
                            var data = activity.Intent?.Data?.ToString();

                            if (action == Android.Content.Intent.ActionView && data is not null)
                            {
                                Preferences.Set("LaunchedPJFCUri", data);
                                HomePage.HasAlreadyLaunchedFromFile = false;
                                Window? w = App.Current?.Windows?[0];
                                if (w is not null)
                                {
                                    w?.Page?.Navigation?.PopToRootAsync();
                                    if (w?.Page is HomePage h)
                                    {
                                        await h.LaunchFromFile();
                                    }

                                }
                            }
                        });
                        android.OnNewIntent(async (activity, intent) =>
                        {
                            var action = intent?.Action;
                            var data = intent?.Data?.ToString();

                            if (action == Android.Content.Intent.ActionView && data is not null)
                            {
                                Preferences.Set("LaunchedPJFCUri", data);
                                HomePage.HasAlreadyLaunchedFromFile = false;
                                Window? w = App.Current?.Windows?[0];
                                if (w is not null)
                                {
                                    w?.Page?.Navigation?.PopToRootAsync();
                                    if (w?.Page is HomePage h)
                                    {
                                        await h.LaunchFromFile();
                                    }

                                }

                            }
                        });
                    });
                });
#endif
                builder.Services.AddSingleton<projectFrameCut.Services.AIComponent.IntegratedAIPlugin>();
#if !WINDOWS
                builder.Services.AddSingleton<projectFrameCut.Services.AIComponent.IAIComponentClient, projectFrameCut.Services.AIComponent.AIComponentUnavailableClient>();
#endif

                try
                {
                    if (!SettingsManager.IsBoolSettingTrue("UseSystemFont")) ConfigFontFromCulture(builder, ReadCultureFromSetting(locate, culture));
                    if (!SettingsManager.IsBoolSettingTrue("RegisterUserFonts"))
                    {

                        TextServices.LoadFonts();
                        // 将已发现字体作为懒加载条目注册到 FontRegistry（不加载 FontFace）
                        foreach (var item in TextServices.LoadedFonts.Values)
                        {
                            if (!string.IsNullOrEmpty(item.Path) && File.Exists(item.Path))
                                TextClipFontRegistry.AddFont(item.Path);
                        }
                        TextClipFontRegistry.Initialize();
                    }

                    try
                    {
                        var emojiPath = Path.Combine(DataPath, "emoji.ttf");
                        if (!File.Exists(emojiPath))
                        {
                            emojiPath = FileSystemService.GetAppPackageFileSync("NotoColorEmoji-Regular.ttf");
                        }
                        if (File.Exists(emojiPath))
                        {
                            var emojiFont = FontFace.AutoLoad(emojiPath).FirstOrDefault();
                            if (emojiFont is not null)
                            {
                                projectFrameCut.Drawing.Text.FontHelper.FontFace.EmojiFont = emojiFont;
                                LogDiagnostic($"Using font {emojiFont.DisplayName} for emoji rendering");
                            }
                        }
                    }
                    catch { }

                    Microsoft.Maui.Handlers.LabelHandler.Mapper.AppendToMapping("EnableTextSelection", (handler, view) =>
                    {
                        if (view is Label l && (l.FontFamily == "MarkdownCodeBlock" || l.StyleId == "SelectableLabel"))
                        {
#if WINDOWS
                            if (handler.PlatformView is Microsoft.UI.Xaml.Controls.TextBlock tb)
                            {
                                tb.IsTextSelectionEnabled = true;
                            }
#elif ANDROID
                            if (handler.PlatformView is Android.Widget.TextView tv)
                            {
                                tv.SetTextIsSelectable(true);
                            }
#endif
                        }
                    });

#if LINUX
                    builder.ConfigureMauiHandlers(handlers =>
                    {
                        handlers.AddHandler<
                            Border,
                            projectFrameCut.Platforms.Linux.LinuxBorderHandler>();
                    });
#endif

                }
                catch
                {
                    builder.ConfigureFonts(fonts =>
                    {
                        fonts.AddFont("HarmonyOS_Sans_Regular.ttf", "Font_Regular");
                        fonts.AddFont("HarmonyOS_Sans_Bold.ttf", "Font_Semibold");
                    });
                }
                finally
                {
                    builder.ConfigureFonts(fonts =>
                    {
                        fonts.AddFont("MaterialSymbolsRounded.ttf", "Icons");
                        fonts.AddFont("SourceCodePro-Regular.ttf", "MarkdownCodeBlock");
                    });
                }


                try
                {
                    if (!File.Exists(Path.Combine(DataPath, $"{Localized.AboutAppData}.txt")))
                    {
                        File.WriteAllText(Path.Combine(DataPath, $"{Localized.AboutAppData}.txt"), OperatingSystem.IsWindows() || OperatingSystem.IsMacCatalyst() ? Localized.AboutAppData_Windows : Localized.AboutAppData_NotWindows);
                    }

                    if (!File.Exists(Path.Combine(BasicDataPath, $"{Localized.AboutAppData}.txt")))
                    {
                        File.WriteAllText(Path.Combine(BasicDataPath, $"{Localized.AboutAppData}.txt"), Localized.AboutAppData_BasicData);
                    }
                }
                catch { }



                Log("Everything ready!");
                var app = builder.Build();
                IntegratedPlugins = [app.Services.GetRequiredService<projectFrameCut.Services.AIComponent.IntegratedAIPlugin>()];
                Task.Run(async () =>
                {
                    await Task.Delay(1000);
                    backgroundInitThread.Start();
                });
                Log("App is ready!");
                return app;
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"FATAL Error creating MAUI App: {ex.Message}");
#if ANDROID
                Android.Util.Log.Wtf("projectFrameCut", $"Oh no! application can't be launched because of a {ex.GetType().Name} exception:{ex.Message}.");
#elif WINDOWS
                _ = WinUI.App.MessageBox(new nint(0), $"Oh no! projectFrameCut cannot start because of a {ex.GetType().Name} exception:\r\n{ex.Message}\r\n\r\nApplication will exit now, and you'll see the detailed info later in the crash report.", "projectFrameCut", 0U);
                projectFrameCut.WinUI.App.Crash(ex);
#endif
                throw;
            }
        }

        public static void InitLocate(ref string locate, ref CultureInfo culture)
        {
            try
            {
                if (locate == "default")
                {
                    if (Thread.CurrentThread.CurrentCulture.Name.StartsWith("en"))
                    {
                        locate = "en-US";
                    }
                    else
                    {
                        locate = Thread.CurrentThread.CurrentCulture.Name;
                    }
                }

                Localized = SimpleLocalizer.Init(locate);
                SettingsManager.SettingLocalizedResources = ISimpleLocalizerBase_Settings.GetMapping().TryGetValue(Localized._LocaleId_, out var loc) ? loc : ISimpleLocalizerBase_Settings.GetMapping().First().Value;
                SimpleLocalizerBaseGeneratedHelper_PropertyPanel.PPLocalizedResources = ISimpleLocalizerBase_PropertyPanel.GetMapping().TryGetValue(Localized._LocaleId_, out var pploc) ? pploc : ISimpleLocalizerBase_PropertyPanel.GetMapping().First().Value;
                projectFrameCut.ApplicationAPIBase.Localize.APIBaseLocalizedResources.Localized = ApplicationAPIBaseLocalizerBase.GetMapping().TryGetValue(Localized._LocaleId_, out var apiloc) ? apiloc : ApplicationAPIBaseLocalizerBase.GetMapping().First().Value;
#if WINDOWS
                SimpleLocalizerBaseGeneratedHelper.Localized = ISimpleLocalizerBase_Helper.GetMapping().TryGetValue(Localized._LocaleId_, out var hloc) ? hloc : ISimpleLocalizerBase_Helper.GetMapping().First().Value;
#endif
                PluginManager.CurrentLocale = Localized._LocaleId_;
                PluginManager.ExtenedLocalizationGetter = new((k) =>
                {
                    return Localized.IsItemExist(k) ? Localized.DynamicLookup(k, k) : null;
                });

                EffectProviderDisplayDefaults.AppPackageFileResolver = paths => FileSystemService.GetAppPackageFileSync(paths);
                EffectProviderDisplayDefaults.TaggedLocalizedStringResolver = (key, fallback, locate) =>
                {
                    var map = ISimpleLocalizerBase_PropertyPanel.GetMapping();
                    var loc = map.TryGetValue(locate, out var l) ? l
                            : map.TryGetValue(PluginManager.CurrentLocale, out var c) ? c
                            : SimpleLocalizerBaseGeneratedHelper_PropertyPanel.PPLocalizedResources;
                    return loc is not null && loc.IsItemExist(key) ? loc.DynamicLookup(key, key) : fallback;
                };



                Log($"OS default current culture: {culture.Name}, locate defined in settings:{locate} ");

                if (!NoOverrideCulture)
                {
                    culture = ReadCultureFromSetting(locate, culture);
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
                LocalizedResources.SimpleLocalizerBaseGeneratedHelper_PropertyPanel.PPLocalizedResources = ISimpleLocalizerBase_PropertyPanel.GetMapping().First().Value;
                PluginManager.CurrentLocale = "en-US";
                PluginManager.ExtenedLocalizationGetter = new((k) => ISimpleLocalizerBase.GetMapping().First().Value.DynamicLookup(k));
                EffectProviderDisplayDefaults.AppPackageFileResolver = paths => FileSystemService.GetAppPackageFileSync(paths);
                EffectProviderDisplayDefaults.TaggedLocalizedStringResolver = (key, fallback, locate) =>
                {
                    var map = ISimpleLocalizerBase_PropertyPanel.GetMapping();
                    var loc = map.TryGetValue(locate, out var l) ? l
                            : map.TryGetValue(PluginManager.CurrentLocale, out var c) ? c
                            : SimpleLocalizerBaseGeneratedHelper_PropertyPanel.PPLocalizedResources;
                    return loc is not null && loc.IsItemExist(key) ? loc.DynamicLookup(key, key) : fallback;
                };
            }
        }

        public static CultureInfo ReadCultureFromSetting(string locate, CultureInfo culture)
        {
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
            if (SettingsManager.IsSettingExists("OverrideCulture") && SettingsManager.GetSetting("OverrideCulture", "default") != "default") //resolve IME not work when locate isn't them
            {
                culture = CultureInfo.CreateSpecificCulture(SettingsManager.GetSetting("OverrideCulture"));
            }

            return culture;
        }

        private static void BackgroundInit()
        {
            Log("Start background init...");


            try
            {
                PluginManager.InitGlobalGetter();
                GlobalPluginHelper.PluginsDataRootPath = Path.Combine(BasicDataPath, "Plugins");
                var internalBase = new InternalApplicationPluginBase();
                internalBase.locateId = SettingsManager.GetSetting("locate", "default");
                (internalBase as IApplicationPluginBase).OnApplicationPluginLoaded();
                List<IPluginBase> plugins =
                    [
                        internalBase,
                        new Render.HwAccelEngine.HwAccelEnginePlugin()
                        {
#if ANDROID
                            DefaultComputeBackend = SettingsManager.GetSetting("render_AndroidHWAccelType", "vulkan")
#endif
                        },
                        .. IntegratedPlugins,
                    ];
                try
                {
                    if (!AdminServices.IsRunningAsAdministrator() && !Environment.GetCommandLineArgs().Contains("--disablePlugins") && !SettingsManager.IsBoolSettingTrue("DisablePluginEngine") && !File.Exists(Path.Combine(BasicDataPath, "noplugin.flag")))
                    {
                        plugins.AddRange(PluginService.LoadUserPlugins());
#if WINDOWS
                        Helper.HelperProgram.ResetPluginLoadingStat();
#endif
                    }
                    else
                    {
                        if (AdminServices.IsRunningAsAdministrator()) Log("Running as administrator, skip load user plugins for security reason.", "warn");
                        else Log("User disabled the plugin engine.");
                        PluginService.FailedLoadPlugin.Add("<No plugin ID available>", AdminServices.IsRunningAsAdministrator() ? Localized.PluginEngine_DisabledBecauseAdmin : Localized.PluginEngine_DisabledBecauseUserDisabled);
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
#pragma warning disable CS0618
                    Crash(new InvalidOperationException($"FATAL: The pluginBase cannot be loaded. projectFrameCut can't work without PluginEngine. \r\n{ex} \r\n{ex1}", new AggregateException(ex, ex1)));
#pragma warning restore CS0618
                }
            }

            try
            {
                FFmpeg.AutoGen.DynamicallyLoadedBindings.EnableAutoInitialization = false;

                try
                {
                    if (Environment.GetCommandLineArgs().FirstOrDefault(c => c.StartsWith("--ffmpegRoot=")) is string ffPath)
                    {
                        var ffmpegRoot = ffPath.Substring("--ffmpegRoot=".Length);
                        if (!string.IsNullOrWhiteSpace(ffmpegRoot) && Directory.Exists(ffmpegRoot))
                        {
                            ffmpeg.RootPath = ffmpegRoot;
                            Log($"Using FFmpeg libraries from command line argument, path:{ffmpegRoot}");
                        }
                    }
                    else if (SettingsManager.IsBoolSettingTrue("PluginProvidedFFmpeg_Enable"))
                    {
#if WINDOWS
                        string? nativeLibDirOverride = null;
                        var pluginId = SettingsManager.GetSetting("PluginProvidedFFmpeg_PluginID", "");
                        if (pluginId == "external")
                        {
                            var ffmpegPath = SettingsManager.GetSetting("PluginProvidedFFmpeg_LibPath", "");
                            if (!string.IsNullOrWhiteSpace(ffmpegPath) && Directory.Exists(ffmpegPath))
                            {
                                Log($"Using external FFmpeg libraries, path:{ffmpegPath}");
                                nativeLibDirOverride = ffmpegPath;
                            }
                            else
                            {
                                Log($"PluginProvidedFFmpeg_Enable is true, but invalid path provided:{ffmpegPath}");
                            }
                        }
                        else if (!PluginManager.LoadedPlugins.TryGetValue(pluginId, out var value))
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
                        if (!string.IsNullOrWhiteSpace(nativeLibDirOverride) && Directory.Exists(nativeLibDirOverride))
                        {
                            ffmpeg.RootPath = nativeLibDirOverride;
                        }
                        else
                        {
                            ffmpeg.RootPath = Path.Combine(AppContext.BaseDirectory, "FFmpeg", "8.x_internal");
                        }
#elif ANDROID
                        ffmpeg.RootPath = Path.Combine(FileSystem.AppDataDirectory, "ffmpeg_plugin_libs");
#endif
                    }
                    else
                    {
                        if (OperatingSystem.IsWindows())
                        {
                            ffmpeg.RootPath = Path.Combine(AppContext.BaseDirectory, "FFmpeg", "8.x_internal");
                        }
                    }
                }
                catch { }
                FFmpegRoot = ffmpeg.RootPath;
                Log($"FFmpeg library root path: {FFmpegRoot}");
                FFmpeg.AutoGen.DynamicallyLoadedBindings.EnableAutoInitialization = false;
                FFmpeg.AutoGen.DynamicallyLoadedBindings.ThrowErrorIfFunctionNotFound = true;

                try
                {
                    FFmpeg.AutoGen.DynamicallyLoadedBindings.Initialize(OperatingSystem.IsWindows() || OperatingSystem.IsLinux(), true);
                    FFmpegHelper.SetupFFmpegLogging();
                    Log($"internal FFmpeg library: version {ffmpeg.av_version_info()}");
                }
                catch (Exception ex)
                {
                    ffmpegFailMessage = $"FFmpeg fail to load. {ex}";
                }

            }
            catch (Exception ex)
            {
                Log(ex, "init ffmpeg", CreateMauiApp);
                ffmpegFailMessage = $"FFmpeg fail to init. {ex}";
            }

            try
            {

                if (!File.Exists(Path.Combine(DataPath, "My Assets", "@WARNING.txt")))
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
                Log(ex, "Init Asset library", CreateMauiApp);

            }


            try
            {
                var templateDir = Path.Combine(DataPath, "My Templates");
                if (Directory.Exists(templateDir))
                {
                    // 加载轻量元数据 .json（不含 Project/Draft，仅供列表展示）
                    foreach (var item in Directory.GetFiles(templateDir, "*.json", SearchOption.AllDirectories))
                    {
                        try
                        {
                            var templateJson = File.ReadAllText(item);

                            // 检测是否为新的轻量元数据格式（含 $schema 标记）
                            if (templateJson.Contains("\"$schema\"") && templateJson.Contains("\"template-meta-v2\""))
                            {
                                var listingTemplate = TemplatePackageIO.LoadListingTemplate(templateJson);
                                if (listingTemplate.TemplateID != Guid.Empty)
                                    TemplateStore.Templates[listingTemplate.TemplateID] = listingTemplate;
                            }
                            else
                            {
                                // 兼容旧格式：通过 JSON 中的 ScriptContent 字段检测旧版脚本模板
                                if (templateJson.Contains("\"ScriptContent\""))
                                {
                                    var scriptCandidate = JsonSerializer.Deserialize<ScriptBasedTemplateStructure>(templateJson);
                                    if (scriptCandidate?.TemplateID != Guid.Empty)
                                    {
                                        TemplateStore.Templates[scriptCandidate.TemplateID] = scriptCandidate;
                                        continue;
                                    }
                                }

                                var template = JSONBasedTemplateHelper.DeserializeTemplate(templateJson);
                                TemplateStore.Templates[template.TemplateID] = template;
                            }
                        }
                        catch (Exception exInner)
                        {
                            Log(exInner, $"load template meta: {item}", CreateMauiApp);
                        }
                    }

                    // 不再在启动时解压 .pjfcTemplate，改为使用时按需解压
                }
            }
            catch (Exception ex)
            {
                Log(ex, "load templates", CreateMauiApp);
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

            try
            {

                if (File.Exists(Path.Combine(MauiProgram.BasicDataPath, "ai_settings_text.json")))
                {
                    AIHelper.CurrentOption = JsonSerializer.Deserialize<AIOption>(File.ReadAllText(Path.Combine(MauiProgram.BasicDataPath, "ai_settings_text.json"))) ?? new AIOption { Provider = "OpenAI" };
                }
                if (File.Exists(Path.Combine(MauiProgram.BasicDataPath, "ai_settings_image.json")))
                {
                    AIHelper.CurrentImageOption = JsonSerializer.Deserialize<AIOption>(File.ReadAllText(Path.Combine(MauiProgram.BasicDataPath, "ai_settings_image.json"))) ?? new AIOption { Provider = "OpenAI" };
                }
                if (File.Exists(Path.Combine(MauiProgram.BasicDataPath, "ai_settings_video.json")))
                {
                    AIHelper.CurrentVideoOption = JsonSerializer.Deserialize<VideoGenAIOption>(File.ReadAllText(Path.Combine(MauiProgram.BasicDataPath, "ai_settings_video.json"))) ?? new VideoGenAIOption { Provider = "OpenAI" };
                }
            }
            catch (Exception ex)
            {
                Log(ex, "Init AI Config", CreateMauiApp);
            }


            try
            {
                MessagingServices.Init();

            }
            catch (Exception ex)
            {
                Log(ex, "init services", CreateMauiApp);
            }


            Log("Background init completed.");



            IsAppReady = true;
        }

        public static string? ffmpegFailMessage = null;

        public static bool IsAppReady { get; private set; } = false;
        public static bool NoOverrideCulture { get; set; } = false;

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
#elif IOS || MACOS || LINUX
            Program.Crash(ex);
#endif
        }

        public static ITemplateStructure? LoadPjfcTemplateSync(string packagePath)
        {
            var extractDir = Path.Combine(FileSystem.CacheDirectory, $"startup_extract_{Guid.NewGuid():N}");
            try
            {
                System.IO.Compression.ZipFile.ExtractToDirectory(packagePath, extractDir, overwriteFiles: true);

                var templateJsonPath = Path.Combine(extractDir, "template.json");
                if (!File.Exists(templateJsonPath))
                    return null;

                var text = File.ReadAllText(templateJsonPath);

                // 检测是否包含脚本
                var scriptPath = Path.Combine(extractDir, "script.ps1");
                if (File.Exists(scriptPath))
                {
                    return JsonSerializer.Deserialize<ScriptBasedTemplateStructure>(text);
                }

                return JsonSerializer.Deserialize<JSONBasedTemplateStructure>(text);
            }
            catch
            {
                return null;
            }
            finally
            {
                try { Directory.Delete(extractDir, true); } catch { }
            }
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
                        fonts.AddFont("MaterialSymbolsRounded.ttf", "Icons");
                    });
                    break;
                case 950: //Traditional Chinese
                    builder.ConfigureFonts(fonts =>
                    {
                        fonts.AddFont("HarmonyOS_Sans_TC_Regular.ttf", "Font_Regular");
                        fonts.AddFont("HarmonyOS_Sans_TC_Bold.ttf", "Font_Semibold");
                        fonts.AddFont("MaterialSymbolsRounded.ttf", "Icons");
                    });
                    break;
                case 932: //Japanese
#if !ANDROID
                    builder.ConfigureFonts(fonts =>
                    {
                        fonts.AddFont("HarmonyOS_Sans_SC_Regular.ttf", "Font_Regular");
                        fonts.AddFont("HarmonyOS_Sans_SC_Bold.ttf", "Font_Semibold");
                        fonts.AddFont("MaterialSymbolsRounded.ttf", "Icons");
                    });
#else
                    builder.ConfigureFonts(fonts =>
                    {
                        fonts.AddFont("MaterialSymbolsRounded.ttf", "Icons");
                    });
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
                    builder.ConfigureFonts(fonts =>
                    {
                        fonts.AddFont("MaterialSymbolsRounded.ttf", "Icons");
                    });
                    //use system ones
#endif
                    break;
                case 1256: //Arabic
                    builder.ConfigureFonts(fonts =>
                    {
                        fonts.AddFont("HarmonyOS_Sans_Naskh_Arabic_Regular.ttf", "Font_Regular");
                        fonts.AddFont("HarmonyOS_Sans_Naskh_Arabic_Bold.ttf", "Font_Semibold");
                        fonts.AddFont("MaterialSymbolsRounded.ttf", "Icons");
                    });
                    break;
                default: //Latin and others
                    builder.ConfigureFonts(fonts =>
                    {
                        fonts.AddFont("HarmonyOS_Sans_Regular.ttf", "Font_Regular");
                        fonts.AddFont("HarmonyOS_Sans_Bold.ttf", "Font_Semibold");
                        fonts.AddFont("MaterialSymbolsRounded.ttf", "Icons");
                    });
                    break;
            }


        }

    }


}



