using Microsoft.Maui.Controls;
using projectFrameCut.ApplicationAPIBase.Plugins;
using projectFrameCut.Render.RenderAPIBase.Plugins;
using projectFrameCut.Render.Rendering;
using projectFrameCut.Setting.SettingPages;
using projectFrameCut.Shared;
using System.Collections.Generic;
using System.Diagnostics;
using System.Reflection;
using System.Threading.Tasks;
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
            string channelStr = "";
#if WINDOWS
            channelStr = $" ({Helper.HelperProgram.AppChannel} channel)";
#endif
            VersionLabel.Text = $"{Localized.AppBrand} v{Assembly.GetExecutingAssembly()?.GetName()?.Version?.ToString() ?? "Unknown"}{channelStr}";
            CopyrightText.Text += DateTime.Now.Year.ToString();
#if iDevices && !DEBUG // no reflection in momo on ios, plugin can't work at all.
            PluginSettingButton.IsVisible = false; 
#endif
            if (IsBoolSettingTrue("DeveloperMode"))
            {
                TestPageButton.IsVisible = true;
                AdvancedSettingButton.IsVisible = true;
            }
        }

        private async void OnGeneralSettingClicked(object sender, EventArgs e)
        {
            await NavigateAsync(new GeneralSettingPage());
        }
        private async void OnEditSettingClicked(object sender, EventArgs e)
        {
            await NavigateAsync(new EditSettingPage());
        }
        private async void OnRemoteFeedSettingClicked(object sender, EventArgs e)
        {
            await NavigateAsync(new RemoteFeedSettingPage());
        }
        private async void OnAISettingClicked(object sender, EventArgs e)
        {
            await NavigateAsync(new AISettingPage());
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
#if Avalonia
            try
            {
                var renderType = typeof(Renderer).Assembly;
                string renderHash = "";
                try
                {
                    renderHash = !renderType.IsDynamic && Path.Exists(renderType.Location) ? HashServices.ComputeFileHash(renderType.Location) : "unknown";
                }
                catch { renderHash = "unknown"; }

                var info =
                    $"""
                This edition of {Localized.AppBrand} is powered by Avalonia.Controls.Maui.

                IPluginBase API: v{IPluginBase.CurrentPluginAPIVersion} | IApplicationPluginBase API: v{IApplicationPluginBase.CurrentAppLevelPluginAPIVersion}

                CoreRender library: v{renderType.GetName().Version} hash:{renderHash}

                {Assembly.GetExecutingAssembly().GetCustomAttribute<AssemblyTitleAttribute>()?.Title ?? "unknown"}: {Assembly.GetExecutingAssembly().GetCustomAttribute<AssemblyConfigurationAttribute>()?.Configuration ?? "unknown config"}@{new string((Assembly.GetExecutingAssembly().GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion ?? "0.1.2+unknown commit").Skip(6).ToArray())}  
                """;
                await DisplayAlertAsync(Localized.MainSettingsPage_Tab_About, info, Localized._OK);

            }
            catch { }
#else
            await NavigateAsync(new AboutSettingPage());
#endif
        }

        private async void AdvancedSettingButton_Clicked(object sender, EventArgs e)
        {
            await NavigateAsync(new AdvancedSettingPage());
        }

        private async void TestPageButton_Clicked(object sender, EventArgs e)
        {
            await NavigateAsync(new TestPage());
        }

        private Task NavigateAsync(Page page)
        {
            try
            {
                return Navigation.PushAsync(page);
            }
            catch (Exception ex)
            {
                Log(ex, $"Navigate to {page.GetType().Name}", this);
                var errpage = new ContentPage
                {
                    Content = new Label
                    {
                        Text = Localized.AppShell_NavFailed(ex, page.GetType().Name),
                        HorizontalOptions = LayoutOptions.Center,
                        VerticalOptions = LayoutOptions.Center
                    }
                };
                return Navigation.PushAsync(errpage);
            }
        }

        public static async Task RebootApp(Page currentPage)
        {
            var conf = await currentPage.DisplayAlertAsync(Localized._Info,
                                        SettingLocalizedResources.CommonStr_RebootRequired(),
                                        Localized._Confirm,
                                        Localized._Cancel);
            if (conf)
            {
                await FlushAndStopAsync();
                if (Debugger.IsAttached) //let user to reboot in debugger
                {
                    Debugger.Break();
                    Environment.Exit(0);
                }
#if WINDOWS
                WinUI.Program.RebootApp();
#endif
                Environment.Exit(0);

            }
        }
        int count = 0;
        private async void TapGestureRecognizer_Tapped(object sender, TappedEventArgs e)
        {
            count++;
            if (count >= 20)
            {
                count = 0;
                if (!IsBoolSettingTrue("DeveloperMode"))
                {
                    WriteSetting("DeveloperMode", "True");
                    await DisplayAlertAsync(Localized._Info, "🛠️✅", Localized._OK);
                }

            }
        }


    }
}
