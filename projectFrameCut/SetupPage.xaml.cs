using projectFrameCut.ApplicationAPIBase.Helpers;

namespace projectFrameCut;

public partial class SetupPage : ContentPage
{
    public SetupPage()
    {
#if WINDOWS && !Avalonia
        App.HideNavBar();
#endif
        SettingsManager.WriteSetting("ui_ShowWelcomePage", "false");
        InitializeComponent();
        AppLogoIcon.Source = ImageHelper.LoadFromAsset("projectframecut");
    }

    private async void Button_Clicked(object sender, EventArgs e)
    {
#if WINDOWS && !Avalonia
        await App.ShowNavBar();
#endif
#if WINDOWS
        if (CreateDesktopShortcutCheckbox.IsChecked)
        {
            try
            {
                string exePath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "pjfc.exe");
                string desktopPath = Environment.GetFolderPath(Environment.SpecialFolder.Desktop);
                string shortcutPath = Path.Combine(desktopPath, "projectFrameCut.lnk");

                dynamic shell = Activator.CreateInstance(Type.GetTypeFromProgID("WScript.Shell"));
                dynamic shortcut = shell.CreateShortcut(shortcutPath);

                shortcut.TargetPath = exePath;
                shortcut.WorkingDirectory = AppDomain.CurrentDomain.BaseDirectory;
                shortcut.Description = "projectFrameCut";
                shortcut.Save();

                System.Runtime.InteropServices.Marshal.FinalReleaseComObject(shortcut);
                System.Runtime.InteropServices.Marshal.FinalReleaseComObject(shell);
            }
            catch (Exception ex)
            {
                Log(ex, "Create desktop shortcut.", this);
            }
        }
#endif
        SettingsManager.WriteSetting("ui_ShowWelcomePage", "false");
        await Navigation.PushAsync(new HomePage());
    }

}