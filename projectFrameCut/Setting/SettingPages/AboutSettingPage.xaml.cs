using Microsoft.Maui.Controls;
using projectFrameCut.DraftStuff;
using System;
using System.IO;
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
        AppVersionLabel.Text = $"Version {AppInfo.VersionString} ({AppInfo.BuildString})";
        AppLogoIcon.Source = ImageHelper.LoadFromAsset("projectframecut");
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
                else await DisplayAlertAsync("???", "Have a nice day :)",Localized._OK);
            }

        };
        AppLogoIcon.GestureRecognizers.Clear();
#if WINDOWS
        AppLogoIcon.GestureRecognizers.Add(tap);

#else
        AppLogoIcon.GestureRecognizers.Add(pinch);

#endif
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