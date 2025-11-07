namespace projectFrameCut;

public partial class LandingPage_iDevices : ContentPage
{
	public LandingPage_iDevices()
	{
		InitializeComponent();
	}

    private async void ContentPage_Loaded(object sender, EventArgs e)
    {
        await Navigation.PushAsync(new DebuggingMainPage()); //todo: go to different page based on condition
    }
}