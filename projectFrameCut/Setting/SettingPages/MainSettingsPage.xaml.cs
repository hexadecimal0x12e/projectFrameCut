using System.Collections.Generic;
using Microsoft.Maui.Controls;
using projectFrameCut.Setting.SettingPages;
using static projectFrameCut.Setting.SettingManager.SettingsManager;

namespace projectFrameCut
{
    public partial class MainSettingsPage : ContentPage
    {
        public static Page instance;

        public MainSettingsPage()
        {
            InitializeComponent();

            instance = this;
            HintLabel.Text = SettingLocalizedResources.General_SelectAPageToGo;
            VersionLabel.Text = $"{Localized.AppBrand} v{AppInfo.VersionString}";
#if iDevices && !DEBUG // no reflection in momo on ios, plugin can't work at all.
            PluginSettingButton.IsVisible = false; 
#endif
        }

        private async void OnGeneralSettingClicked(object sender, EventArgs e)
        {
            await NavigateAsync(new GeneralSettingPage());
        }
        private async void OnEditSettingClicked(object sender, EventArgs e)
        {
            await NavigateAsync(new EditSettingPage());
        }
        private async void OnRenderSettingClicked(object sender, EventArgs e)
        {
            await NavigateAsync(new RenderSettingPage());
        }
        private async void OnMiscSettingClicked(object sender, EventArgs e)
        {
            await NavigateAsync(new MiscSettingPage());
        }
        private async void OnPluginSettingClicked(object sender, EventArgs e)
        {
            await NavigateAsync(new PluginSettingPage());
        }
        private async void OnAboutSettingClicked(object sender, EventArgs e)
        {
            await NavigateAsync(new AboutSettingPage());
        }

        private Task NavigateAsync(Page page)
        {
            // Ensure we use Navigation stack from Shell or current NavigationPage
            return Navigation.PushAsync(page);
        }
    }
}
