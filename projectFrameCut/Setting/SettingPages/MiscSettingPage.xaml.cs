
using projectFrameCut.ApplicationAPIBase.Views.PropertyPanelBuilders;
using projectFrameCut.Services;
using projectFrameCut.Shared;
using System.Diagnostics;

namespace projectFrameCut.Setting.SettingPages;

using static SettingManager.SettingsManager;
using IPicture = Shared.IPicture;

public partial class MiscSettingPage : ContentPage
{
    public MiscSettingPage()
    {
        Title = Localized.MainSettingsPage_Tab_Misc;
        BuildPPB();

    }
    public PropertyPanelBuilder rootPPB;

    public void BuildPPB()
    {
        Content = new VerticalStackLayout();
        rootPPB = new();
        rootPPB
            .AddText(new TitleAndDescriptionLineLabel(SettingLocalizedResources.Misc_UserInfo, SettingLocalizedResources.Misc_UserInfo_Subtitle))
            .AddEntry("UserName", SettingLocalizedResources.Misc_UserDisplayName, GetSetting("UserName", Environment.UserName), Environment.UserName)
            .AddCustomChild(SettingLocalizedResources.Misc_UserID, new Label { Text = GetSetting("UserID") })
            .AddSeparator()
            .AddText(new TitleAndDescriptionLineLabel(SettingLocalizedResources.Misc_DiagOptions, SettingLocalizedResources.Misc_DiagOptions_Desc()))
            .AddButton("makeDiagReport", SettingLocalizedResources.Misc_MakeDiagReport, null)
            .AddButton(Localized.MainSettingsPage_Tab_Advanced, async (s, e) => await Navigation.PushAsync(new SettingPages.AdvancedSettingPage()))
            .AddSeparator()
            .AddText(new SingleLineLabel(SettingLocalizedResources.Misc_Reset, 25, default))
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
        Content = rootPPB.BuildWithScrollView();
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
                            "nobody.ExamplePlugin",
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
                            "no",
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


                            await DisplayPromptAsync(Localized._Warn,
                                SettingLocalizedResources.Misc_ResetSettings_Warn,
                                Localized._Confirm,
                                Localized._Cancel,
                                "no",
                                -1,
                                null,
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
                    await FileSystemService.OpenFileAsync(jsonPath);
                    goto done;
                case "render_SaveCheckpoint":
                    if (args.Value is bool b && b)
                    {
                        WriteSetting("render_SaveCheckpoint", "true");
                        Directory.CreateDirectory(Path.Combine(MauiProgram.DataPath, "RenderCheckpoint"));
                        IPicture.DiagImagePath = Path.Combine(MauiProgram.DataPath, "RenderCheckpoint");
                    }
                    else
                    {
                        WriteSetting("render_SaveCheckpoint", "false");
                        IPicture.DiagImagePath = null;
                    }
                    break;
                case "DeveloperMode":
                case "DisablePluginEngine":
                    needReboot = true;
                    break;
                case "LogDiagnostics":
                    MyLoggerExtensions.LoggingDiagnosticInfo = args.Value is bool ? (bool)args.Value : false;
                    LogDiagnostic($"User toggled LogDiagnostics to {args.Value}");
                    break;

            }

            if (args.Value != null)
            {
                WriteSetting(args.Id, args.Value?.ToString() ?? "");
            }


            if (needReboot)
                await MainSettingsPage.RebootApp(this);

        done:
            { }
            //BuildPPB();
        }
        catch (Exception ex)
        {
            // 处理异常并通知用户
            await DisplayAlertAsync(Localized._Warn, Localized._ExceptionTemplate(ex), Localized._OK);
        }
    }

}