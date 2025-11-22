using Microsoft.Maui.Controls;
using System;
using System.IO;
using System.Text;
using System.Threading.Tasks;

namespace projectFrameCut.Setting.SettingPages;

public partial class AboutSettingPage : ContentPage
{
    public AboutSettingPage()
    {
        InitializeComponent();
        Loaded += AboutSettingPage_Loaded;
        AppVersionLabel.Text = $"Version {AppInfo.VersionString} ({AppInfo.BuildString})";
    }

    private async void AboutSettingPage_Loaded(object? sender, EventArgs e)
    {
        await LoadAboutAsync();
    }

    private async Task LoadAboutAsync()
    {
        try
        {
            var filePath = $"AboutApplication/{Localized._LocaleId_}/About.md";
            using var stream = await FileSystem.OpenAppPackageFileAsync(filePath);
            using var reader = new StreamReader(stream, Encoding.UTF8);
            var text = await reader.ReadToEndAsync();
            Dispatcher.Dispatch(() =>
            {
                AboutTextEntry.Text = text ?? string.Empty;
            });

        }
        catch (Exception ex)
        {
            Dispatcher.Dispatch(() =>
            {
                AboutTextEntry.Text = "Sorry, about content is unavailable.";
            });
        }
    }

    
    
}