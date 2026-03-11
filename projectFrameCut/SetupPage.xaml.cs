using projectFrameCut.ApplicationAPIBase.Helpers;

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
#endif
        SettingsManager.WriteSetting("ui_ShowWelcomePage", "false");
        await Navigation.PushAsync(new HomePage());
    }
}