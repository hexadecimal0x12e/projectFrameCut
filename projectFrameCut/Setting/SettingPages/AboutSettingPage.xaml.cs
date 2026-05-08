using Microsoft.Maui.Controls;
using projectFrameCut.ApplicationAPIBase.Helpers;
using projectFrameCut.ApplicationAPIBase.Plugins;
using projectFrameCut.Render.RenderAPIBase.Plugins;
using projectFrameCut.Render.Rendering;
using projectFrameCut.Shared;
using System;
using System.IO;
using System.Reflection;
using System.Security.AccessControl;
using System.Text;
using System.Threading.Tasks;

namespace projectFrameCut.Setting.SettingPages;

public partial class AboutSettingPage : ContentPage
{
#if WINDOWS
    TapGestureRecognizer tap = new TapGestureRecognizer();
#else
    PinchGestureRecognizer pinch = new PinchGestureRecognizer();
#endif
    private int count = 0;
    public AboutSettingPage()
    {
        InitializeComponent();
        Loaded += AboutSettingPage_Loaded;
        AppLogoIcon.Source = ImageHelper.LoadFromAsset("projectframecut");
        AppLogoIcon_Narrow.Source = ImageHelper.LoadFromAsset("projectframecut");
#if WINDOWS
        tap.Tapped
#else
        pinch.PinchUpdated
#endif
         += async (s, e) =>
        {
            count++;
            if (count >= 20)
            {
                count = 0;
                var result = await DisplayAlertAsync("???", "Let's play a game!", Localized._Cancel, Localized._OK);
                if (result) await Launcher.OpenAsync("https://oig.mihoyo.com/ys"); //____
                else await DisplayAlertAsync("???", "Have a nice day :)", Localized._OK);
            }

        };
        AppLogoIcon.GestureRecognizers.Clear();
        AppLogoIcon_Narrow.GestureRecognizers.Clear();
#if WINDOWS
        AppLogoIcon.GestureRecognizers.Add(tap);
        AppLogoIcon_Narrow.GestureRecognizers.Add(tap);
        try
        {
            var pfn = WinUI.App.GetPackageFamilyName();
            var channel = pfn switch
            {
                "hexadecimal0x12e.projectFrameCutCommunity_f91nmrsqwpk6y" => "Community",
                "0xeeeeeeeeeeee.projectFrameCutCommunity_f91nmrsqwpk6y" => "Community MS Store",
                "hexadecimal0x12e.projectFrameCut_f91nmrsqwpk6y" => "Standard",
                "0xeeeeeeeeeeee.projectFrameCut_f91nmrsqwpk6y" => "MS Store",
                _ => "Non-official build"
            };
            AppVersionLabel.Text = $"Version {Assembly.GetExecutingAssembly()?.GetName()?.Version?.ToString() ?? "Unknown"} ({channel} channel)";
            AppVersionLabel_Narrow.Text = AppVersionLabel.Text;

        }
        catch
        {

        }

#else
        AppLogoIcon.GestureRecognizers.Add(pinch);
        AppLogoIcon_Narrow.GestureRecognizers.Add(pinch);
        AppVersionLabel.Text = $"Version {Assembly.GetExecutingAssembly()?.GetName()?.Version?.ToString() ?? "Unknown"}";
        AppVersionLabel_Narrow.Text = AppVersionLabel.Text;
#endif
        try
        {
            var renderType = typeof(Renderer).Assembly;
            string renderHash = "";
            try
            {
                renderHash = !renderType.IsDynamic && Path.Exists(renderType.Location) ? HashServices.ComputeFileHash(renderType.Location) : "unknown";
            }
            catch { renderHash = "unknown"; }

            AppDetailVersionLabel.Text =
                $"""
                IPluginBase API: v{IPluginBase.CurrentPluginAPIVersion} | IApplicationPluginBase API: v{IApplicationPluginBase.CurrentAppLevelPluginAPIVersion}
                CoreRender library: v{renderType.GetName().Version} hash:{renderHash}
                {MauiProgram.AssemblyName}: {MauiProgram.ProgramConfig}@{MauiProgram.ProgramCommit}
                Store: {(MauiProgram.IsStoreMode ? "Yes" : "No")}
                """;
            AppDetailVersionLabel_Narrow.Text = AppDetailVersionLabel.Text;
        }
        catch { }
    }

    private async void AboutSettingPage_Loaded(object? sender, EventArgs e)
    {
        await LoadAboutAsync();
    }

    private async Task LoadAboutAsync()
    {
        try
        {
            var filePath = $"AboutApplication/{Localized._LocaleId_}/About.html";
            using var stream = await FileSystem.OpenAppPackageFileAsync(filePath);
            using var reader = new StreamReader(stream);
            var text = await reader.ReadToEndAsync();
            Dispatcher.Dispatch(() =>
            {
                AboutWebview.Source = new HtmlWebViewSource
                {
                    Html = text
                };
            });

        }
        catch (Exception ex)
        {
            try
            {
                var filePath = $"AboutApplication/en-US/About.html";
                using var stream = await FileSystem.OpenAppPackageFileAsync(filePath);
                using var reader = new StreamReader(stream);
                var text = await reader.ReadToEndAsync();
                Dispatcher.Dispatch(() =>
                {
                    AboutWebview.Source = new HtmlWebViewSource
                    {
                        Html = text
                    };
                });
            }
            catch
            {
                Dispatcher.Dispatch(() =>
                {
                    AboutWebview.Source = new HtmlWebViewSource
                    {
                        Html = $"<html><body><h2>Error loading about content</h2><p>{ex.Message}</p></body></html>"
                    };
                });
            }
        }
    }



}