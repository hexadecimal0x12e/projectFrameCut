using Microsoft.Maui.Controls;
using projectFrameCut.Setting.SettingPages;
using System.Collections.Generic;
using System.Diagnostics;
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
            VersionLabel.Text = $"{Localized.AppBrand} v{AppInfo.VersionString}";
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
                string path = "projectFrameCut_Protocol:";
                if (!MauiProgram.IsPackaged())
                {
                    var exePath = Process.GetCurrentProcess().MainModule?.FileName;
                    if (exePath != null)
                    {
                        path = exePath;
                    }
                }
                var script =
$$"""

Clear-Host;Write-Output "projectFrameCut is now rebooting, please wait for a while...";Start-Process "{{path}}";exit

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
#endif
                Environment.Exit(0);

            }
        }
        int count = 0;
        private async void TapGestureRecognizer_Tapped(object sender, TappedEventArgs e)
        {
            count++;
            if(count == 20)
            {
                await NavigateAsync(new AdvancedSettingPage());

            }
        }


    }
}
