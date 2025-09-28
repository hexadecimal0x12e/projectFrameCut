using projectFrameCut.Strings;

namespace projectFrameCut;

public partial class WelcomePage : ContentPage
{
	public WelcomePage()
	{
		InitializeComponent();

	}

    private async void ToDraftPage_Clicked(object sender, EventArgs e)
    {
        await Navigation.PushAsync(string.IsNullOrWhiteSpace(draftSourcePath) || !Path.Exists(draftSourcePath) ? new DraftPage() : new DraftPage(draftSourcePath));

    }

    private async void TestPageButton_Clicked(object sender, EventArgs e)
    {
        await Navigation.PushAsync(new TestPage());
    }
    string draftSourcePath

#if WINDOWS

     = @"D:\code\playground\projectFrameCut\project\Untitled Project 2";

#elif ANDROID
        = "/storage/emulated/0/Android/data/com.hexadecimal0x12e.projectframecut/drafts/Untitled Project 1";
#else
 = String.Empty;
#endif
    private async void SelectDraft_Clicked(object sender, EventArgs e)
    {
#if ANDROID || IOS || MACCATALYST || WINDOWS
        var result = await FilePicker.PickAsync(new PickOptions {});
        if (result == null) return;
        //using var stream = await result.OpenReadAsync();
        //using var sr = new StreamReader(stream);
        //draftSource = await sr.ReadToEndAsync();
        SelectedDraftPath.Text = result.FullPath;
        draftSourcePath = Path.Combine(Path.GetDirectoryName(result.FullPath) ?? throw new NullReferenceException(), Path.GetFileNameWithoutExtension(result.FullPath)) ;

#else
                // For platforms without picker, prompt paste
                draftSource  = await DisplayPromptAsync("Import", "Paste Draft JSON:");
                if (string.IsNullOrWhiteSpace(json)) return;
                SelectedDraftPath.Text = "selected.";
#endif


    }

    private void CreateNewDraft_Clicked(object sender, EventArgs e)
    {
        draftSourcePath = "";
    }

    private async void RpcTestPage_Clicked(object sender, EventArgs e)
    {
        await Navigation.PushAsync(new RPCTestPage());

    }
}