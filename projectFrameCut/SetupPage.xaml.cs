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
        await App.ShowNavBar();
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
        ILGPU.Context context = ILGPU.Context.CreateDefault();
        var devices = context.Devices.ToList();
        List<AcceleratorInfo> listAccels = new();
        for (uint i = 0; i < devices.Count; i++)
        {
            var item = devices[(int)i];
            listAccels.Add(new AcceleratorInfo(i, item.Name, item.AcceleratorType.ToString()));
        }
        if (!devices.Any(c => c.AcceleratorType != ILGPU.Runtime.AcceleratorType.CPU))
        {
            await DisplayAlertAsync(Localized._Warn, Localized.WelocmePage_NoAccel, Localized._OK);
        }
        if (!int.TryParse(SettingsManager.GetSetting("accel_DeviceId", "-1"), out var result) || result < 0 || !(listAccels?.Any(c => c.index == result) ?? false))
        {
            var bestAccel = listAccels?.Select(c => (c, c.Type switch { "Cuda" => 10, "OpenCL" => 5, "CPU" => -10, _ => 1 })).OrderByDescending(c => c.Item2).ThenByDescending(c => c.c.name).FirstOrDefault();
            SettingsManager.WriteSetting("accel_DeviceId", (bestAccel?.c.index ?? 0).ToString());
            SettingsManager.WriteSetting("accel_enableMultiAccel", true.ToString());
            Log($"No accelerator defined yet; set to best one {bestAccel?.c.name} ({bestAccel?.c.Type}) by default.");
        }

#endif
        SettingsManager.WriteSetting("ui_ShowWelcomePage", "false");
        await Navigation.PushAsync(new HomePage());
    }

}