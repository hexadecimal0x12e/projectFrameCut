using projectFrameCut.ApplicationAPIBase.Helpers;
using projectFrameCut.Shared;

namespace projectFrameCut;

public partial class SetupPage : ContentPage
{
    public SetupPage()
    {
#if WINDOWS
        App.HideNavBar();
#endif
        SettingsManager.WriteSetting("ui_ShowWelcomePage", "false");
        InitializeComponent();
        AppLogoIcon.Source = ImageHelper.LoadFromAsset("projectframecut");
    }

    private async void Button_Clicked(object sender, EventArgs e)
    {
#if WINDOWS
        App.ShowNavBar();
        if (CreateDesktopShortcutCheckbox.IsChecked)
        {
            try
            {
                string desktopPath = Environment.GetFolderPath(Environment.SpecialFolder.Desktop);
                string shortcutPath = Path.Combine(desktopPath, $"{Localized.AppBrand}.lnk");
                if (projectFrameCut.Helper.HelperProgram.AppChannel.Equals("MS Store", StringComparison.InvariantCultureIgnoreCase))
                {
                    File.Copy(Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "projectFrameCut.Store.lnk"), shortcutPath);
                }
                else
                {
                    File.Copy(Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "projectFrameCut.lnk"), shortcutPath);
                }
            }
            catch (Exception ex)
            {
                Log(ex, "Create desktop shortcut.", this);
            }
        }
#endif
#if WINDOWS || LINUX
        var devices = projectFrameCut.Render.HwAccelEngine.AcceleratorsManager.DiscoverDevices();
        if (!devices.Any(c => c.Type != "CPU"))
        {
            await DisplayAlertAsync(Localized._Warn, Localized.WelocmePage_NoAccel, Localized._OK);
        }

        // Auto-select best non-CPU accelerator and persist to accels.json.
        // AcceleratorsManager will be re-initialized on next plugin load.
        var best = devices
            .Select(c => (c, c.Type switch { "Cuda" => 10, "OpenCL" => 5, "CPU" => -10, _ => 1 }))
            .OrderByDescending(t => t.Item2).ThenByDescending(t => t.c.Name)
            .FirstOrDefault().c;
        if (best is not null)
        {
            var nonCpuDevices = devices.Where(c => c.Type != "CPU").ToList();
            projectFrameCut.Render.HwAccelEngine.AcceleratorsManager.SetDefaultAccelerator(best.Name);
            Log($"Auto-selected accelerator: {best.Name} ({best.Type}).");
        }

#endif
        SettingsManager.WriteSetting("ui_ShowWelcomePage", "false");
        MainSettingsPage.SyncSettingToModules();
        await Navigation.PushAsync(new HomePage());
    }

}