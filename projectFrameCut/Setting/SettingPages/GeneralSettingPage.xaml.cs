using Microsoft.Maui.Storage;
using projectFrameCut.PropertyPanel;
using System;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;

namespace projectFrameCut.Setting.SettingPages;

using static SettingManager.SettingsManager;

public partial class GeneralSettingPage : ContentPage
{
    public PropertyPanel.PropertyPanelBuilder rootPPB;
    private string[] locates;
    private Dictionary<string, string> overrideOpts;
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
            {"default",  SettingLocalizedResources.General_Language_OverrideCulture_DontOverride},
            {"zh-CN", SettingLocalizedResources.General_Language_OverrideCulture_OverrideTo
                    (ISimpleLocalizerBase.GetMapping()["zh-CN"]._LocateDisplayName) },
            {"ja-JP", SettingLocalizedResources.General_Language_OverrideCulture_OverrideTo
                    (ISimpleLocalizerBase.GetMapping()["ja-JP"]._LocateDisplayName) },
            {"ko-KR", SettingLocalizedResources.General_Language_OverrideCulture_OverrideTo
                    (ISimpleLocalizerBase.GetMapping()["ko-KR"]._LocateDisplayName) },
        };
        var currentLocate = GetSetting("locate", "default");
        rootPPB = new()
        {
            WidthOfContent = 3
        };
        rootPPB
            .AddPicker("locate", SettingLocalizedResources.General_Language, locates, currentLocate != "default" ? Localized._LocateDisplayName : $"{Localized._Default} / Default", null)
            .AddPicker("OverrideCulture", SettingLocalizedResources.General_Language_OverrideCulture, overrideOpts.Values.ToArray(), overrideOpts[GetSetting("OverrideCulture", "default")], null)
            .AddSeparator()
            .AddText(SettingLocalizedResources.General_UserData)
#if WINDOWS
            .AddButton("userDataSelectButton", SettingLocalizedResources.General_UserData_Path)
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

                case "locate":
                    {
                        var locateDispName = args.Value?.ToString() ?? "default";
                        var mapping = ISimpleLocalizerBase.GetMapping();
                        var locate = mapping.FirstOrDefault(l => l.Value._LocateDisplayName == locateDispName).Key;
                        if (string.IsNullOrEmpty(locate)) locate = "default";
                        WriteSetting("locate", locate);
                        CultureInfo culture = CultureInfo.CurrentCulture;
                        Log($"Your culture: {culture.Name}, locate defined in settings:{locate} ");
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
                                case "default":
                                    {
                                        culture = CultureInfo.InstalledUICulture;
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

                            System.Threading.Thread.CurrentThread.CurrentCulture = culture;
                            System.Threading.Thread.CurrentThread.CurrentUICulture = culture;
                        }
                        catch (Exception ex)
                        {
                            Log(ex, "init culture");
                        }

                        Localized = SimpleLocalizer.Init(locate);
                        SettingLocalizedResources = ISimpleLocalizerBase_Settings.GetMapping().TryGetValue(Localized._LocaleId_, out var loc) ? loc : ISimpleLocalizerBase_Settings.GetMapping().First().Value;
                        App.Current?.Windows?.First()?.Title = Localized.AppBrand;
                        Log($"Localization initialized to {Localized._LocaleId_}, {Localized.WelcomeMessage}");
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

            }

            if (args.Value != null)
            {
                WriteSetting(args.Id, args.Value?.ToString() ?? "");
            }

        done:
            if (needReboot)
                RebootApp();

            BuildPPB();
        }
        catch (Exception ex)
        {
            // 处理异常并通知用户
            await DisplayAlert(Localized._Warn, Localized._ExceptionTemplate(ex), Localized._OK);
        }
    }

    private async void RebootApp()
    {
        var conf = await MainSettingsPage.instance.DisplayAlertAsync(Localized._Info,
                                    SettingLocalizedResources.CommonStr_RebootRequired(),
                                    Localized._Confirm,
                                    Localized._Cancel);
        if (conf)
        {
            await FlushAndStopAsync();
#if WINDOWS
            string path = "projectFrameCut_Protocol:";
            if (!MauiProgram.IsPackaged())
            {
                var exePath = Process.GetCurrentProcess().MainModule?.FileName;
                if (exePath != null)
                {
                    path = exePath;
                }
            }
            var script =
    $$"""

Clear-Host;Write-Output "projectFrameCut is now rebooting, please wait for a while...";Start-Process "{{path}}";exit

""";
            var proc = new Process();
            proc.StartInfo.FileName = "powershell.exe";
            proc.StartInfo.UseShellExecute = false;
            proc.StartInfo.RedirectStandardInput = true;
            proc.StartInfo.CreateNoWindow = false;
            proc.Start();
            var procWriter = proc.StandardInput;
            if (procWriter != null)
            {
                procWriter.AutoFlush = true;
                procWriter.WriteLine(script);
            }
#endif
            Environment.Exit(0);

        }
    }
}