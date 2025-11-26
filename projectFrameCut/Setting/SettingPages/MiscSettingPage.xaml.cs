using projectFrameCut.PropertyPanel;
using System.Diagnostics;

namespace projectFrameCut.Setting.SettingPages;

using static SettingManager.SettingsManager;

public partial class MiscSettingPage : ContentPage
{
    public MiscSettingPage()
    {
        Title = Localized.MainSettingsPage_Tab_Misc;
        BuildPPB();

    }
    public PropertyPanel.PropertyPanelBuilder rootPPB;

    public void BuildPPB()
    {
        Content = new VerticalStackLayout();
        rootPPB = new();
        rootPPB
            .AddText(new PropertyPanel.TitleAndDescriptionLineLabel(SettingLocalizedResources.Misc_UserInfo, SettingLocalizedResources.Misc_UserInfo_Subtitle, 20, 12))
            .AddEntry("UserName", SettingLocalizedResources.Misc_UserDisplayName, GetSetting("UserName", Environment.UserName), Environment.UserName)
            .AddCustomChild(SettingLocalizedResources.Misc_UserID, new Label { Text = GetSetting("UserID") })
            .AddSeparator()
            .AddText(new PropertyPanel.TitleAndDescriptionLineLabel(SettingLocalizedResources.Misc_DiagOptions, SettingLocalizedResources.Misc_DiagOptions_Subtitle, 20, 12))
            .AddButton("makeDiagReport", SettingLocalizedResources.Misc_MakeDiagReport, null)
            .AddButton("openSettingsButton", SettingLocalizedResources.Misc_OpenSettingsJson, null!)
#if !iDevices || (iDevices && !DEBUG) //appstore not allow developers put developer settings in release version of apps
            .AddSwitch("DeveloperMode", SettingLocalizedResources.Misc_DebugMode, bool.TryParse(GetSetting("DeveloperMode", "false"), out var devMode) ? devMode : false, null)
#endif
            .AddSeparator()
            .AddText(new PropertyPanel.SingleLineLabel(SettingLocalizedResources.Misc_Reset, 20, default))
            .AddButton("reset_Settings", SettingLocalizedResources.Misc_ResetSettings,
            (b) =>
            {
                b.BackgroundColor = Color.FromRgba("CC0000FF");
                b.TextColor = Colors.Black;
            })
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
                case var t when t.StartsWith("reset_"):
                    {
                        var tag = t switch
                        {
                            "reset_Settings" => SettingLocalizedResources.Misc_ResetSettings,
                            _ => "Unknown"
                        };
                        var conf = await MainSettingsPage.instance.DisplayAlertAsync(Localized._Warn,
                                    SettingLocalizedResources.CommonStr_Sure(tag),
                                    Localized._Confirm,
                                    Localized._Cancel);
                        if (conf)
                        {
                            needReboot = true;
                        }
                        else
                        {
                            goto done;
                        }
                        break;
                    }
                case "makeDiagReport":
                    //todo
                    goto done;
                case "openSettingsButton":
                    var jsonPath = Path.Combine(MauiProgram.BasicDataPath, "settings.json");
#if WINDOWS
                    Process.Start(new ProcessStartInfo { FileName = jsonPath, UseShellExecute = true });
#elif ANDROID

#elif iDevices

#endif
                    goto done;
                case "DeveloperMode":
                    needReboot = true;
                    break;

            }

            if (args.Value != null)
            {
                WriteSetting(args.Id, args.Value?.ToString() ?? "");
            }


            if (needReboot)
                RebootApp();

            done:
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
            if (MauiProgram.IsPackaged() == false)
            {
                var exePath = Process.GetCurrentProcess().MainModule?.FileName;
                if (exePath != null)
                {
                    path = exePath;
                }
            }
            var script =
    $$"""

Clear-Host;Write-Output "projectFrameCut is now rebooting, please wait for a while...";Start-Sleep 2;Start-Process "{{path}}";exit

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
            Environment.Exit(0);
#endif
        }
    }
}