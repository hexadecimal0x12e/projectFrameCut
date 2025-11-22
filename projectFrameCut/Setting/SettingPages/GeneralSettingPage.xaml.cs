using Microsoft.Maui.Storage;
using projectFrameCut.PropertyPanel;
using System;
using System.Diagnostics;
using System.IO;
using System.Linq;

namespace projectFrameCut.Setting.SettingPages;

using static SettingManager.SettingsManager;

public partial class GeneralSettingPage : ContentPage
{
    public PropertyPanel.PropertyPanelBuilder rootPPB;

    public GeneralSettingPage()
    {
        Title = Localized.MainSettingsPage_Tab_General;
        BuildPPB();
    }

    public void BuildPPB()
    {
        Content = new VerticalStackLayout();
        rootPPB = new();
        rootPPB
            .AddPicker("locate", SettingLocalizedResources.General_Language, new List<string>(["default"]).Concat(ISimpleLocalizerBase.GetMapping().Select(l => l.Key)).ToArray(), GetSetting("locate", "default"), null)
            .AddSeparator()
            .AddText(SettingLocalizedResources.General_UserData)
#if WINDOWS
            .AddButton("userDataSelectButton", SettingLocalizedResources.General_UserData_Path, MauiProgram.DataPath)
#endif
            .AddButton("manageUsedDataButton", SettingLocalizedResources.General_UserData_ManagePageOpen, SettingLocalizedResources.CommonStr_Go, null)
            
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
                    break;

                case "locate":
                    needReboot = true;
                    break;
            }

            if (args.Value != null)
            {
                WriteSetting(args.Id, args.Value?.ToString() ?? "");
            }


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