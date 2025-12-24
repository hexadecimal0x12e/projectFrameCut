using Microsoft.Maui.Storage;
using projectFrameCut.PropertyPanel;
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
    private Dictionary<string, string> locateDisplayNameMapping = new();
    public GeneralSettingPage()
    {
        Title = Localized.MainSettingsPage_Tab_General;
        locates = new List<string>([$"{Localized._Default} / Default"]).Concat(ISimpleLocalizerBase.GetMapping().Select(l => l.Value._LocateDisplayName)).ToArray();
        locateDisplayNameMapping = new(ISimpleLocalizerBase.GetMapping().ToDictionary(k => k.Value._LocateDisplayName, v => v.Key).Append(new KeyValuePair<string, string>("OS Default", "default")));
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
        var currentLocate = GetSetting("locate", "default");
        rootPPB = new();
        rootPPB
            .AddPicker("locate", SettingLocalizedResources.General_Language, locates, currentLocate != "default" ? Localized._LocateDisplayName : $"{Localized._Default} / Default", null)
#if WINDOWS
            .AddPicker("OverrideCulture", SettingLocalizedResources.General_Language_OverrideCulture, overrideOpts.Values.ToArray(), overrideOpts[GetSetting("OverrideCulture", "default")], null)
#endif
            .AddSeparator()
            .AddText(new TitleAndDescriptionLineLabel(SettingLocalizedResources.GeneralUI_Title, SettingLocalizedResources.GeneralUI_Subtitle))
            .AddPicker("ui_defaultTheme", SettingLocalizedResources.GeneralUI_DefaultTheme, themeOpts.Values.ToArray(), themeOpts[GetSetting("ui_defaultTheme", "default")])
            .AddSlider("ui_defaultWidthOfContent", SettingLocalizedResources.GeneralUI_DefaultWidthOfContent, 1, 10, PropertyPanelBuilder.DefaultWidthOfContent)
            .AddButton("setUISafeZone",SettingLocalizedResources.GeneralUI_SetupSafeZone)
            .AddSeparator()
            .AddText(new PropertyPanel.TitleAndDescriptionLineLabel(SettingLocalizedResources.General_UserData, SettingLocalizedResources.General_UserData_Subtitle, 20, 12))
#if WINDOWS
            .AddButton("userDataSelectButton", SettingLocalizedResources.General_UserData_SelectPath)
            .AddButton("openUserDataButton", SettingLocalizedResources.General_UserData_Open(MauiProgram.DataPath))
#endif
            .AddButton("manageUsedDataButton", SettingLocalizedResources.General_UserData_ManagePageOpen, null)

            .ListenToChanges(SettingInvoker);
        Content = new ScrollView { Content = rootPPB.Build() };
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

                                }
                                File.WriteAllText(Path.Combine(MauiProgram.BasicDataPath, "OverrideUserDataPath.txt"), fullPath);

                                needReboot = true;
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
#if WINDOWS
                    Process.Start(new ProcessStartInfo { FileName = MauiProgram.DataPath, UseShellExecute = true });
#elif ANDROID

#elif iDevices

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
                    var key = themeOpts.FirstOrDefault(c => c.Value == args.Value as string,new KeyValuePair<string, string>("default", "default")).Key;
                    WriteSetting("ui_defaultTheme", key);
                    Application.Current?.UserAppTheme = key switch
                    {
                        "dark" => AppTheme.Dark,
                        "light" => AppTheme.Light,
                        _ => AppTheme.Unspecified
                    };
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