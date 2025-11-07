using Microsoft.Maui.Graphics;
using projectFrameCut.DraftStuff;
using projectFrameCut.Shared;
using projectFrameCut.ViewModels;
using System.Collections.Concurrent;
using System.Collections.ObjectModel;
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

     = @"D:\code\playground\projectFrameCut\project\test";

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
        string draftSourcePath;


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
        draftSourcePath = folder.Path;



#elif MACCATALYST || IOS
draftSourcePath = "";
#elif ANDROID
        draftSourcePath = "";
#endif

        var projName = await DisplayPromptAsync("info", "input a name for this project", "ok", "", "project 1", -1, null, "Untitled Project 1");

#if MACCATALYST || IOS
                draftSourcePath = Directory.CreateDirectory(Path.Combine(draftSourcePath, projName + ".pjfc")).FullName;
#else
        File.WriteAllText(Path.Combine(draftSourcePath, projName + ".pjfc"), "@projectFrameCut v1");
        draftSourcePath = Directory.CreateDirectory(Path.Combine(draftSourcePath, projName)).FullName;

#endif
        var ProjectInfo = new ProjectJSONStructure
        {
            projectName = projName,
            ResourcePath = draftSourcePath,

        };



        File.WriteAllText(Path.Combine(draftSourcePath, "timeline.json"),
        JsonSerializer.Serialize(new DraftStructureJSON
        {
            Clips = new List<ClipDraftDTO>().Cast<object>().ToArray(),
        }));
        File.WriteAllText(Path.Combine(draftSourcePath, "assets.json"), JsonSerializer.Serialize(Array.Empty<AssetItem>()));
        File.WriteAllText(Path.Combine(draftSourcePath, "project.json"), JsonSerializer.Serialize(ProjectInfo));

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

    private void ToV2DraftPageWithNoDraft_Clicked(object sender, EventArgs e)
    {
        var page = new DraftPage();
        Navigation.PushAsync(page);
    }

    private async void ToV2DraftPageWithDiagBackend_Clicked(object sender, EventArgs e)
    {
        try
        {


            DraftPage page;

            if (!Directory.Exists(draftSourcePath))
                throw new DirectoryNotFoundException($"Working path not found: {draftSourcePath}");

            var filesShouldExist = new[] { "project.json", "timeline.json", "assets.json" };

            if (filesShouldExist.Any((f) => !File.Exists(Path.Combine(draftSourcePath, f))))
            {
                throw new FileNotFoundException("One or more required files are missing.", draftSourcePath);
            }

            var project = JsonSerializer.Deserialize<ProjectJSONStructure>(File.ReadAllText(Path.Combine(draftSourcePath, "project.json")));

            var assets = JsonSerializer.Deserialize<List<AssetItem>>(File.ReadAllText(Path.Combine(draftSourcePath, "assets.json"))) ?? new();
            var timeline = JsonSerializer.Deserialize<DraftStructureJSON>(File.ReadAllText(Path.Combine(draftSourcePath, "timeline.json"))) ?? new();



            var assetDict = new ConcurrentDictionary<string, AssetItem>(assets.ToDictionary((a) => a.AssetId ?? $"unknown+{Random.Shared.Next()}", (a) => a));
            (var dict, var trackCount) = DraftImportAndExportHelper.ImportFromJSON(timeline);

            //var ovlp = DraftImportAndExportHelper.FindOverlaps(dict.Values, 5);

            //if (ovlp.Count > 0)
            //{
            //    await DisplayAlert("Overlap", ovlp.Aggregate("", (s, i) => $"{i.ClipAId} and {i.ClipBId} overlap by {i.OverlapFrames} frames on layer {i.LayerIndex};\r\n"), "ok");
            //}

            page = new DraftPage(dict, assetDict, trackCount, draftSourcePath, project?.projectName ?? "?")
            {
                ProjectInfo = project ?? new(),
            };

            await Navigation.PushAsync(page);
        }catch(Exception ex)
        {
            await DisplayAlert(Localized._Warn, Localized._ExceptionTemplate(ex), "ok");
        }
    }

    private async void ToV2DraftPageWithSomeClips_Clicked(object sender, EventArgs e)
    {
        DraftPage page;
        var d = new DraftStructureJSON
        {
            targetFrameRate = 60,
            Clips =
                [
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
                    ,
                    new ClipDraftDTO
                    {
                        Id = "clip4",
                        Name = "clip 4",
                        LayerIndex = 1,
                        StartFrame = 1200,
                        Duration = 250,
                        RelativeStartFrame = 0,
                    }
                ]
        };

        var ovlp = DraftImportAndExportHelper.FindOverlaps(d.Clips.Cast<ClipDraftDTO>(), 5);

        if (ovlp.Count > 0)
        {
            await DisplayAlert("Overlap", ovlp.Aggregate("", (s, i) => $"{i.ClipAId} and {i.ClipBId} overlap by {i.OverlapFrames} frames on layer {i.LayerIndex};\r\n"), "ok");
        }

        (var dict, var trackCount) = DraftImportAndExportHelper.ImportFromJSON(d);
        var assetJson = File.ReadAllText(@"D:\code\playground\projectFrameCut\project\Untitled Project 2\assets.json");
        page = new DraftPage(dict, DraftImportAndExportHelper.ImportAssetsFromJSON(assetJson), trackCount, "1");

        await Navigation.PushAsync(page);

    }
}