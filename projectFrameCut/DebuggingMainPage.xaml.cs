using Microsoft.Maui.Graphics;
using projectFrameCut.DraftStuff;
using projectFrameCut.Shared;
using projectFrameCut.ViewModels;
using System.Collections.Concurrent;
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

    private async void ToV2DraftPageWithSomeClips_Clicked(object sender, EventArgs e)
    {
        DraftPage page;
//        #if !DEBUG
//        try
//#endif
//        {
//            var c1 = DraftPage.CreateClip(0, 500, 0, "clip1", "clip 1", null, null, 0U, 500U);
//            var c2 = DraftPage.CreateClip(500, 500, 0, "clip2", "clip 2", null, null, 1U, 500U);
//            var c3 = DraftPage.CreateClip(1000, 500, 0, "clip3", "clip 3", null, null, 0U, 250U);

//            var clips = new ConcurrentDictionary<string, ClipElementUI>();
//            clips.TryAdd(c1.Id, c1);
//            clips.TryAdd(c2.Id, c2);
//            clips.TryAdd(c3.Id, c3);

//            page = new DraftPage(clips, 2);
//        }
//#if !DEBUG
//        catch (Exception ex)
//        {
//            Log(ex, "init draft page with clips", this);
//            await DisplayAlert("Error", ex.Message, "OK");
//            throw;
//        }
//#endif

       page = DraftImportAndExportHelper.ImportToDraftPage(
            new DraftStructureJSON
            {
                Name = "Test Draft",
                targetFrameRate = 60,
                Clips = new object[]
                {
                    new ClipDraftDTO
                    {
                        Id = "clip1",
                        Name = "clip 1",
                        LayerIndex = 0,
                        StartFrame = 0,
                        Duration = 500,
                        RelativeStartFrame = 0,
                    },
                    new ClipDraftDTO
                    {
                        Id = "clip2",
                        Name = "clip 2",
                        LayerIndex = 0,
                        StartFrame = 500,
                        Duration = 500,
                        RelativeStartFrame = 1,
                    },
                    new ClipDraftDTO
                    {
                        Id = "clip3",
                        Name = "clip 3",
                        LayerIndex = 1,
                        StartFrame = 1000,
                        Duration = 250,
                        RelativeStartFrame = 0,
                    }
                }
            }
        );

        await Navigation.PushAsync(page);

    }
}