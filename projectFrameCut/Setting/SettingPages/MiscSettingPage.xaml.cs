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
            .AddButton("makeDiagReport", SettingLocalizedResources.Misc_MakeDiagReport, SettingLocalizedResources.CommonStr_Make, null)
            .AddSwitch("DeveloperMode", SettingLocalizedResources.Misc_DebugMode, bool.TryParse(GetSetting("DeveloperMode","false"), out var devMode) ? devMode : false, null)
            .AddSeparator((b) =>
            {
                b.HeightRequest = 200;
                b.BackgroundColor = Colors.Transparent;
            })
            .AddButton("reset_Settings", SettingLocalizedResources.Misc_ResetSettings, SettingLocalizedResources.CommonStr_Reset, 
            (b) =>
            {
                b.BackgroundColor = Color.FromRgba("CC0000FF");
                b.TextColor = Colors.Black;
            })
            .ListenToChanges(SettingInvoker);
        Content = rootPPB.Build();
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
                        var conf = await MainSettingsPage.instance.DisplayAlertAsync(Localized._Warn,
                                    SettingLocalizedResources.CommonStr_Sure(t),
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