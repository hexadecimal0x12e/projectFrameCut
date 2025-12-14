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
            .AddSwitch("LogDiagnostics", SettingLocalizedResources.Misc_LogDiagnostics, bool.TryParse(GetSetting("LogDiagnostics", "false"), out var logDiagnostics) ? logDiagnostics : false, null)
#if !iDevices || (iDevices && !DEBUG) //appstore not allow developers put developer settings in release version of apps
            .AddSwitch("DeveloperMode", SettingLocalizedResources.Misc_DebugMode, bool.TryParse(GetSetting("DeveloperMode", "false"), out var devMode) ? devMode : false, null)
#endif
            .AddSeparator()
            .AddText(new PropertyPanel.SingleLineLabel(SettingLocalizedResources.Misc_Reset, 20, default))
            .AddButton("reset_ClearPluginSign", SettingLocalizedResources.Misc_ForgetPluginSign,
            (b) =>
            {
                b.BackgroundColor = Color.FromRgba("FF9999FF");
                b.TextColor = Colors.Black;
            })
            .AddButton("reset_SaveStors", SettingLocalizedResources.Misc_ClearSafeStor,
            (b) =>
            {
                b.BackgroundColor = Color.FromRgba("FF9999FF");
                b.TextColor = Colors.Black;
            })
            .AddButton("reset_Settings", SettingLocalizedResources.Misc_ResetSettings,
            (b) =>
            {
                b.BackgroundColor = Color.FromRgba("FF9999FF");
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
                case "reset_ClearPluginSign":
                    {
                        await DisplayPromptAsync(Localized._Info,
                            SettingLocalizedResources.Misc_ForgetPluginSign_Hint,
                            Localized._Confirm,
                            Localized._Cancel,
                            "projectFrameCut.ExamplePlugin",
                            -1,
                            Keyboard.Default,
                            "").ContinueWith(async (t) =>
                            {
                                if (string.IsNullOrWhiteSpace(t.Result))
                                    return;
                                try
                                {
                                    SecureStorage.Remove($"plugin_pem_{t.Result}");
                                    await MainSettingsPage.instance.DisplayAlertAsync(Localized._Info,
                                        SettingLocalizedResources.Misc_ForgetPluginSign_Success(t.Result),
                                        Localized._OK);
                                    needReboot = true;
                                }
                                catch (Exception ex)
                                {
                                    await MainSettingsPage.instance.DisplayAlertAsync(Localized._Error,
                                        Localized._ExceptionTemplate(ex),
                                        Localized._OK);
                                }
                            });
                        break;
                    }

                case "reset_SaveStors":
                    {
                        await DisplayPromptAsync(Localized._Info,
                            SettingLocalizedResources.Misc_ClearSafeStor_Warn,
                            Localized._Confirm,
                            Localized._Cancel,
                            "yes",
                            -1,
                            Keyboard.Default,
                            "").ContinueWith(async (t) =>
                            {
                                if (string.IsNullOrWhiteSpace(t.Result))
                                    return;
                                if (t.Result.Trim() == "yes")
                                {
                                    SecureStorage.RemoveAll();
                                    needReboot = true;
                                    
                                }
                            });
                        break;
                    }
                case "reset_Settings":
                    {
                       var warn1 = await DisplayAlertAsync(Localized._Warn, SettingLocalizedResources.Misc_ResetSettings_Warn2, Localized._Confirm, Localized._Cancel);
                        if (warn1)
                        {


                            await DisplayPromptAsync(Localized._Info,
                                SettingLocalizedResources.Misc_ResetSettings_Warn,
                                Localized._Confirm,
                                Localized._Cancel,
                                "yes",
                                -1,
                                Keyboard.Email,
                                "").ContinueWith(async (t) =>
                                {
                                    if (string.IsNullOrWhiteSpace(t.Result))
                                        return;
                                    if (t.Result.Trim() == "yes")
                                    {
                                        SecureStorage.RemoveAll();
                                        try
                                        {
                                            Directory.Delete(MauiProgram.BasicDataPath, true);
                                        }
                                        catch
                                        {

                                        }
                                        Directory.CreateDirectory(MauiProgram.BasicDataPath);
                                        WriteSetting("reset_Settings", "true");

                                        Environment.Exit(0);
                                    }
                                });
                        }
                        break;
                    }
                case "makeDiagReport":
                    await Navigation.PushAsync(new DiagnosticSettingPage());
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