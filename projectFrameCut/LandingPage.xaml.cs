using System.Threading.Tasks;

namespace projectFrameCut;

public partial class LandingPage : ContentPage
{
	public LandingPage()
	{
		InitializeComponent();
	}

    private async void ContentPage_Loaded(object sender, EventArgs e)
    {
		await Navigation.PushAsync(new DebuggingMainPage()); //todo: go to different page based on condition
    }
}