using Microsoft.Maui.Graphics;
using projectFrameCut.Shared;
using projectFrameCut.ViewModels;
using System.Diagnostics;
using System.IO.Pipes;
using System.Reflection.Metadata;
using System.Text.Json;

namespace projectFrameCut;

public partial class DebuggingMainPage : ContentPage
{
    public DebuggingMainPage()
    {
        InitializeComponent();
        Title = Localized.AppBrand;
        Debug.WriteLine(Localized.WelcomeMessage);
    }

    protected override bool OnBackButtonPressed() => true;

    private async void ToDraftPage_Clicked(object sender, EventArgs e)
    {
        await Navigation.PushAsync(string.IsNullOrWhiteSpace(draftSourcePath) || !Path.Exists(draftSourcePath) ? new DraftPageOld() : new DraftPageOld(draftSourcePath));

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
        var result = await FilePicker.PickAsync(new PickOptions { });
        if (result == null) return;
        //using var stream = await result.OpenReadAsync();
        //using var sr = new StreamReader(stream);
        //draftSource = await sr.ReadToEndAsync();
        SelectedDraftPath.Text = result.FullPath;
        draftSourcePath = Path.Combine(Path.GetDirectoryName(result.FullPath) ?? throw new NullReferenceException(), Path.GetFileNameWithoutExtension(result.FullPath));

#else
                // For platforms without picker, prompt paste
                draftSource  = await DisplayPromptAsync("Import", "Paste Draft JSON:");
                if (string.IsNullOrWhiteSpace(json)) return;
                SelectedDraftPath.Text = "selected.";
#endif


    }

    private async void CreateNewDraft_Clicked(object sender, EventArgs e)
    {
        string workingDirectory;


#if WINDOWS
        var picker = new Windows.Storage.Pickers.FolderPicker();
        picker.FileTypeFilter.Add("*");

        var mauiWin = Application.Current?.Windows?.FirstOrDefault();
        if (mauiWin?.Handler?.PlatformView is Microsoft.UI.Xaml.Window window)
        {
            var hwnd = WinRT.Interop.WindowNative.GetWindowHandle(window);
            WinRT.Interop.InitializeWithWindow.Initialize(picker, hwnd);
        }

        var folder = await picker.PickSingleFolderAsync();
        if (folder == null) return;
        workingDirectory = folder.Path;



#elif MACCATALYST || IOS
workingDirectory = "";
#elif ANDROID
workingDirectory = "";
#endif

        var projName = await DisplayPromptAsync("info", "input a name for this project", "ok", "", "project 1", -1, null, "Untitled Project 1");

#if MACCATALYST || IOS
                workingDirectory = Directory.CreateDirectory(Path.Combine(workingDirectory, projName + ".pjfc")).FullName;
#else
        File.WriteAllText(Path.Combine(workingDirectory, projName + ".pjfc"), "@projectFrameCut v1");
        workingDirectory = Directory.CreateDirectory(Path.Combine(workingDirectory, projName)).FullName;

#endif
        var ProjectInfo = new ProjectJSONStructure
        {
            projectName = projName,
            ResourcePath = workingDirectory,

        };



        File.WriteAllText(Path.Combine(workingDirectory, "timeline.json"),
        JsonSerializer.Serialize(new DraftStructureJSON
        {
            Name = projName,
            Clips = new List<ClipDraftDTO>().Cast<object>().ToArray(),
        }));
        File.WriteAllText(Path.Combine(workingDirectory, "assets.json"), JsonSerializer.Serialize(Array.Empty<AssetItem>()));
        File.WriteAllText(Path.Combine(workingDirectory, "project.json"), JsonSerializer.Serialize(ProjectInfo));

    }

    private async void RpcTestPage_Clicked(object sender, EventArgs e)
    {
        await Navigation.PushAsync(new RPCTestPage());

    }

    private async void ToDraftPageWithDiagBackend_Clicked(object sender, EventArgs e)
    {
#if DEBUG && WINDOWS
        await Navigation.PushAsync(string.IsNullOrWhiteSpace(draftSourcePath) || !Path.Exists(draftSourcePath) ? new DraftPageOld() : new DraftPageOld(draftSourcePath, new NamedPipeClientStream(".", "pjfcTestPipe1", PipeDirection.InOut, PipeOptions.Asynchronous)));
#else

#endif
    }

    private void MoreOptions_Clicked(object sender, EventArgs e)
    {

    }

    private async void ToV2DraftPageWithDiagBackend_Clicked(object sender, EventArgs e)
    {
        await Navigation.PushAsync(new DraftPage());

    }
}