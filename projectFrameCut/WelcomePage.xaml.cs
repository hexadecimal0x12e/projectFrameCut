namespace projectFrameCut;

public partial class WelcomePage : ContentPage
{
	public WelcomePage()
	{
		InitializeComponent();
	}

    private async void ToDraftPage_Clicked(object sender, EventArgs e)
    {
        await Navigation.PushAsync(string.IsNullOrWhiteSpace(draftSource) ? new DraftPage() : new DraftPage(draftSource));

    }

    private async void TestPageButton_Clicked(object sender, EventArgs e)
    {
        await Navigation.PushAsync(new TestPage());
    }

    string draftSource = string.Empty;

    private async void SelectDraft_Clicked(object sender, EventArgs e)
    {
#if ANDROID || IOS || MACCATALYST || WINDOWS
        var result = await FilePicker.PickAsync(new PickOptions { PickerTitle = "Ñ¡Ôñ²Ý¸å JSON ÎÄ¼þ" });
        if (result == null) return;
        using var stream = await result.OpenReadAsync();
        using var sr = new StreamReader(stream);
        draftSource = await sr.ReadToEndAsync();
        SelectedDraftPath.Text = result.FullPath;
#else
                // For platforms without picker, prompt paste
                draftSource  = await DisplayPromptAsync("Import", "Paste Draft JSON:");
                if (string.IsNullOrWhiteSpace(json)) return;
                SelectedDraftPath.Text = "selected.";
#endif


    }

    private void CreateNewDraft_Clicked(object sender, EventArgs e)
    {

    }

    private async void RpcTestPage_Clicked(object sender, EventArgs e)
    {
        await Navigation.PushAsync(new RPCTestPage());

    }
}