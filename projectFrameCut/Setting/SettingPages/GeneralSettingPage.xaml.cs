using Microsoft.Maui.Storage;
using projectFrameCut.PropertyPanel;
using projectFrameCut.Render.Plugin;
using projectFrameCut.Services;
using System;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading.Tasks;

namespace projectFrameCut.Setting.SettingPages;

using static SettingManager.SettingsManager;

public partial class GeneralSettingPage : ContentPage
{
    public PropertyPanel.PropertyPanelBuilder rootPPB;
    private string[] locates;
    private Dictionary<string, string> overrideOpts, themeOpts;
    private Dictionary<string, string> locateDisplayNameMapping = new(), FFmpegProviderDisplayNameMapping = new();
    public GeneralSettingPage()
    {
        Title = Localized.MainSettingsPage_Tab_General;
        locates = new List<string>([$"{Localized._Default} / Default"]).Concat(ISimpleLocalizerBase.GetMapping().Select(l => l.Value._LocateDisplayName)).ToArray();
        locateDisplayNameMapping = new(ISimpleLocalizerBase.GetMapping().ToDictionary(k => k.Value._LocateDisplayName, v => v.Key).Append(new KeyValuePair<string, string>("OS Default", "default")));
        FFmpegProviderDisplayNameMapping =
            new Dictionary<string, string>
            { {SettingLocalizedResources.GeneralCodec_SelectProvider_Internal, "disable" } }
            .Concat(
                PluginManager.LoadedPlugins
                .Where(c => c.Value.LocalizationProvider.TryGetValue("option", out var optsKVP) && optsKVP.TryGetValue("_IsFFmpegLibraryProvider", out var value) && bool.TryParse(value, out var result) && result)
                .Select(p => new KeyValuePair<string, string>(p.Value.Name, p.Key))
            )
            .ToDictionary(c => c.Key, c => c.Value);
        BuildPPB();
    }

    public void BuildPPB()
    {
        Content = new VerticalStackLayout();
        Title = Localized.MainSettingsPage_Tab_General;
        overrideOpts = new Dictionary<string, string>
        {
            {"default", SettingLocalizedResources.General_Language_OverrideCulture_DontOverride},
            {"zh-CN", SettingLocalizedResources.General_Language_OverrideCulture_OverrideTo
                    (ISimpleLocalizerBase.GetMapping()["zh-CN"]._LocateDisplayName) },
            {"ja-JP", SettingLocalizedResources.General_Language_OverrideCulture_OverrideTo
                    (ISimpleLocalizerBase.GetMapping()["ja-JP"]._LocateDisplayName) },
            {"ko-KR", SettingLocalizedResources.General_Language_OverrideCulture_OverrideTo
                    (ISimpleLocalizerBase.GetMapping()["ko-KR"]._LocateDisplayName) },
        };
        themeOpts = new Dictionary<string, string>
        {
            { "default", SettingLocalizedResources.GeneralUI_DefaultTheme_OSDefault },
            { "dark", SettingLocalizedResources.GeneralUI_DefaultTheme_Dark },
            { "light",SettingLocalizedResources.GeneralUI_DefaultTheme_Bright }
        };
        if (!SettingsManager.IsSettingExists("render_EnableScreenSaver"))
        {
#if ANDROID || IOS //oled screen, avoid burn-in
            WriteSetting("render_EnableScreenSaver", "true");
#else
            WriteSetting("render_EnableScreenSaver", "false");
#endif
        }
        var currentLocate = GetSetting("locate", "default");
        rootPPB = new();
        rootPPB
            .AddPicker("locate", SettingLocalizedResources.General_Language, locates, currentLocate != "default" ? Localized._LocateDisplayName : $"{Localized._Default} / Default", null)
#if WINDOWS
            //.AddPicker("OverrideCulture", SettingLocalizedResources.General_Language_OverrideCulture, overrideOpts.Values.ToArray(), overrideOpts[GetSetting("OverrideCulture", "default")], null)
#endif
            .AddSeparator()
            .AddText(new TitleAndDescriptionLineLabel(SettingLocalizedResources.GeneralUI_Title, SettingLocalizedResources.GeneralUI_Subtitle))
#if !WINDOWS
            .AddPicker("ui_defaultTheme", SettingLocalizedResources.GeneralUI_DefaultTheme, themeOpts.Values.ToArray(), themeOpts[GetSetting("ui_defaultTheme", "default")])
#endif
            .AddSlider("ui_defaultWidthOfContent", SettingLocalizedResources.GeneralUI_DefaultWidthOfContent, 1, 10, PropertyPanelBuilder.DefaultWidthOfContent)
            .AddSwitch("render_EnableScreenSaver", SettingLocalizedResources.Render_EnableScreenSaver, IsBoolSettingTrue("render_EnableScreenSaver"), null)
            .AddButton("setUISafeZone", SettingLocalizedResources.GeneralUI_SetupSafeZone)
            .AddSeparator()
            .AddText(new PropertyPanel.TitleAndDescriptionLineLabel(SettingLocalizedResources.GeneralCodec_Title, SettingLocalizedResources.GeneralCodec_SubTitle, 20, 12))
            .AddPicker("codec_FFmpegProvider", SettingLocalizedResources.GeneralCodec_SelectProvider, FFmpegProviderDisplayNameMapping.Keys.ToArray(), FFmpegProviderDisplayNameMapping.FirstOrDefault(c => c.Value == GetSetting("PluginProvidedFFmpeg_PluginID", "disable"), new(SettingLocalizedResources.GeneralCodec_SelectProvider_Internal, "disable")).Key)
            .AddSwitch("codec_PreferredHWAccel", SettingLocalizedResources.GeneralCodec_PreferredHWAccel, IsBoolSettingTrue("codec_PreferredHWAccel"))
            .AddSeparator()
            .AddText(new PropertyPanel.TitleAndDescriptionLineLabel(SettingLocalizedResources.General_UserData, SettingLocalizedResources.General_UserData_Subtitle, 20, 12))
#if WINDOWS
            .AddButton("userDataSelectButton", SettingLocalizedResources.General_UserData_SelectPath)
#endif
            .AddButton("openUserDataButton", SettingLocalizedResources.General_UserData_Open(MauiProgram.DataPath))
            .AddButton("manageUsedDataButton", SettingLocalizedResources.General_UserData_ManagePageOpen, null)

            .ListenToChanges(SettingInvoker);
        Content = rootPPB.BuildWithScrollView();
    }

    private async void SettingInvoker(PropertyPanelPropertyChangedEventArgs args)
    {
        bool needReboot = false;
        try
        {
            switch (args.Id)
            {
#if WINDOWS
                case "userDataSelectButton":
                    {
                        var picker = new Windows.Storage.Pickers.FolderPicker();
                        picker.FileTypeFilter.Add("*");

                        var mauiWin = Application.Current?.Windows?.FirstOrDefault();
                        if (mauiWin?.Handler?.PlatformView is Microsoft.UI.Xaml.Window window)
                        {
                            var hwnd = WinRT.Interop.WindowNative.GetWindowHandle(window);
                            WinRT.Interop.InitializeWithWindow.Initialize(picker, hwnd);
                        }
                        picker.CommitButtonText = SettingLocalizedResources.General_UserData_SelectFolder_ConfirmButton;
                        var folder = await picker.PickSingleFolderAsync();
                        if (folder == null)
                        {
                            var conf = await DisplayAlertAsync(Localized._Warn,
                                SettingLocalizedResources.General_UserData_SelectFolder_ConfirmReset,
                                Localized._Confirm,
                                Localized._Cancel);
                            if (conf)
                            {
                                var overridePath = Path.Combine(MauiProgram.BasicDataPath, "OverrideUserDataPath.txt");
                                if (File.Exists(overridePath))
                                {
                                    File.Delete(overridePath);
                                }
                                needReboot = true;
                                break;
                            }
                            else
                            {
                                goto done;
                            }
                        }
                        var fullPath = folder.Path;


                        if (fullPath != null)
                        {
                            var conf1 = await DisplayAlertAsync(Localized._Info,
                                SettingLocalizedResources.General_UserData_SelectFolder_Confirm(fullPath),
                                Localized._Confirm,
                                 Localized._Cancel);
                            if (conf1)
                            {
                                var conf2 = await DisplayAlertAsync(Localized._Info,
                                    SettingLocalizedResources.General_UserData_SelectFolder_ConfirmMigrateData,
                                    Localized._Confirm,
                                    Localized._Cancel);
                                if (conf2)
                                {
                                    var cont = Content;
                                    var files = Directory.GetFiles(MauiProgram.DataPath, "*", SearchOption.AllDirectories);
                                    uint finished = 0;
                                    int duplicated = 0;
                                    Stopwatch cd = Stopwatch.StartNew();
                                    var procLabel = new Label
                                    {
                                        Text = Localized._Processing,
                                        FontSize = 20,
                                        HorizontalOptions = LayoutOptions.Center,
                                        VerticalOptions = LayoutOptions.Center,
                                    };
                                    var mover = Task.Run(async () =>
                                    {
                                        try
                                        {
                                            foreach (var f in files)
                                            {
                                                var destFile = Path.Combine(fullPath, Path.GetRelativePath(MauiProgram.DataPath, f));
                                                LogDiagnostic($"Clone {f} to {destFile}...");
                                                try
                                                {
                                                    Directory.CreateDirectory(Path.GetDirectoryName(destFile));
                                                    if (!File.Exists(destFile))
                                                    {
                                                        File.Copy(f, destFile);
                                                        await Task.Delay(1);
                                                        File.Delete(f);
                                                    }
                                                    else duplicated++;
                                                    finished++;
                                                    if (cd.Elapsed.Seconds > 2)
                                                    {
                                                        await Dispatcher.DispatchAsync(() =>
                                                        {
                                                            procLabel.Text = Localized._ProcessingWithProg(finished / files.Length);
                                                        });
                                                        cd.Restart();
                                                    }
                                                }
                                                catch (Exception ex)
                                                {
                                                    var skip = await Dispatcher.DispatchAsync(async () =>
                                                    {
                                                        var cont = await DisplayAlertAsync(Localized._Warn,
                                                            SettingLocalizedResources.General_UserData_SelectFolder_MigrateError(Path.GetFileName(f), ex),
                                                            SettingLocalizedResources.General_UserData_SelectFolder_MigrateError_Skip,
                                                            Localized._Cancel);
                                                        return cont;
                                                    });
                                                    if (!skip)
                                                    {
                                                        await Dispatcher.DispatchAsync(async () =>
                                                        {
                                                            Content = cont;
                                                        });
                                                        return;
                                                    }
                                                }
                                            }
                                        }
                                        catch { }
                                    });
                                    await Dispatcher.DispatchAsync(async () =>
                                    {

                                        Content = new VerticalStackLayout
                                        {
                                            Children =
                                            {
                                                new ActivityIndicator
                                                {
                                                    IsRunning = true,
                                                    VerticalOptions = LayoutOptions.Center,
                                                    HorizontalOptions = LayoutOptions.Center
                                                },
                                                procLabel,
                                                new Label
                                                {
                                                    Text = SettingLocalizedResources.Diag_MakingReport_Sub,
                                                    FontSize = 28,
                                                    TextColor = Colors.OrangeRed,
                                                    HorizontalOptions = LayoutOptions.Center,
                                                    VerticalOptions = LayoutOptions.Center
                                                }

                                            },
                                            HorizontalOptions = LayoutOptions.Center,
                                            VerticalOptions = LayoutOptions.Center
                                        };

                                    });
                                    await mover;

                                    Dispatcher.Dispatch(() => Content = cont);
                                    if (duplicated > 0)
                                    {
                                        await DisplayAlertAsync(Localized._Info,
                                            SettingLocalizedResources.General_UserData_SelectFolder_FinishedWithConflict(duplicated),
                                            Localized._OK);
                                    }
                                    else
                                    {
                                        var del = await DisplayAlertAsync(Localized._Info,
                                            SettingLocalizedResources.General_UserData_SelectFolder_FinishedNoConflict(),
                                            Localized._OK,
                                            Localized._Cancel);
                                        if (del)
                                        {
                                            await Task.Run(() =>
                                            {
                                                try
                                                {
                                                    Directory.Delete(MauiProgram.DataPath, true);
                                                }
                                                catch { }
                                            });
                                        }
                                    }
                                }
                                needReboot = true;
                                File.WriteAllText(Path.Combine(MauiProgram.BasicDataPath, "OverrideUserDataPath.txt"), fullPath);

                            }
                            else
                            {
                                return;
                            }
                        }





                        break;
                    }
#endif
                case "manageUsedDataButton":
                    //todo: manage userdata
                    goto done;
                case "openUserDataButton":
                    await FileSystemService.OpenFolderAsync(MauiProgram.DataPath);
#if ANDROID
                    await DisplayAlertAsync(Localized._Info, SettingLocalizedResources.General_UserData_Open_Android(projectFrameCut.Platforms.Android.MainApplication.MainContext?.PackageName ?? "com.hexadecimal0x12e.projectframecut"), Localized._OK);
#elif iDevices
                    await DisplayAlertAsync(Localized._Info, SettingLocalizedResources.General_UserData_Open_iDevices(DeviceInfo.Idiom switch { var t when t == DeviceIdiom.Phone => "iPhone", var t when t == DeviceIdiom.Tablet => "iPad",  _ => "Devices"}), Localized._OK);
#endif
                    goto done;
                case "setUISafeZone":
                    var page = new SafeZoneSettingPage();
                    await Dispatcher.DispatchAsync(async () =>
                    {
                        Shell.SetTabBarIsVisible(page, false); //ensure tab bar is hidden
                        Shell.SetNavBarIsVisible(page, false);
                        await Navigation.PushAsync(page);
                    });
                    break;
                case "locate":
                    {
                        var locateDispName = args.Value?.ToString() ?? "default";
                        var mapping = ISimpleLocalizerBase.GetMapping();
                        var locate = mapping.FirstOrDefault(l => l.Value._LocateDisplayName == locateDispName).Key;
                        if (string.IsNullOrEmpty(locate)) locate = "default";
                        WriteSetting("locate", locate);
                        Localized = SimpleLocalizer.Init(locate);
                        SettingLocalizedResources = ISimpleLocalizerBase_Settings.GetMapping().TryGetValue(Localized._LocaleId_, out var loc) ? loc : ISimpleLocalizerBase_Settings.GetMapping().First().Value;
                        App.Current?.Windows?.First()?.Title = Localized.AppBrand;
                        Log($"Localization is set to {Localized._LocaleId_}, {Localized.WelcomeMessage}");
                        await Task.Delay(150);
                        needReboot = true;
                        goto done;
                    }
                case "OverrideCulture":
                    {
                        var DispName = args.Value?.ToString() ?? "default";
                        var overrideLocate = overrideOpts.First(p => p.Value == DispName).Key;
                        WriteSetting("OverrideCulture", overrideLocate);
                        needReboot = true;
                        goto done;
                    }
                case "ui_defaultWidthOfContent":
                    if (args.Value is double d)
                        PropertyPanelBuilder.DefaultWidthOfContent = d;
                    break;
                case "ui_defaultTheme":
                    var key = themeOpts.FirstOrDefault(c => c.Value == args.Value as string, new KeyValuePair<string, string>("default", "default")).Key;
                    WriteSetting("ui_defaultTheme", key);
                    Application.Current?.UserAppTheme = key switch
                    {
                        "dark" => AppTheme.Dark,
                        "light" => AppTheme.Light,
                        _ => AppTheme.Unspecified
                    };
                    goto done;
                case "codec_FFmpegProvider":
                    var id = FFmpegProviderDisplayNameMapping.TryGetValue(args.Value as string, out var pvdid) ? pvdid : "disable";
                    if (id == "disable")
                    {
                        WriteSetting("PluginProvidedFFmpeg_Enable", false.ToString());
#if ANDROID
                        try
                        {
                            var internalLibPath = Path.Combine(FileSystem.AppDataDirectory, "ffmpeg_plugin_libs");
                            Directory.Delete(internalLibPath, true);
                        }
                        catch { }
#endif
                        WriteSetting("PluginProvidedFFmpeg_PluginID", "disable");

                    }
                    else
                    {
                        WriteSetting("PluginProvidedFFmpeg_Enable", true.ToString());
#if ANDROID
                        try
                        {
                            var internalLibPath = Path.Combine(FileSystem.AppDataDirectory, "ffmpeg_plugin_libs");
                            Log($"Copying plugin FFmpeg libs to internal storage: {internalLibPath}");
                            if (Directory.Exists(internalLibPath)) Directory.Delete(internalLibPath, true);
                            Directory.CreateDirectory(internalLibPath);
                            var ffmpegPath = Path.Combine(MauiProgram.BasicDataPath, "Plugins", id, "FFmpeg", "android");
                            foreach (var file in Directory.GetFiles(ffmpegPath, "*.so*"))
                            {
                                File.Copy(file, Path.Combine(internalLibPath, Path.GetFileName(file)), true);
                            }
                        }
                        catch (Exception ex)
                        {
                            Log(ex, "copy ffmpeg libs to internal", this);
                        }
#endif
                        WriteSetting("PluginProvidedFFmpeg_PluginID", id);

                    }
                    needReboot = true;
                    goto done;
            }

            if (args.Value != null)
            {
                WriteSetting(args.Id, args.Value?.ToString() ?? "");
            }

        done:
            if (needReboot)
                await MainSettingsPage.RebootApp(this);

            BuildPPB();
        }
        catch (Exception ex)
        {
            // 处理异常并通知用户
            await DisplayAlert(Localized._Warn, Localized._ExceptionTemplate(ex), Localized._OK);
        }
    }
}