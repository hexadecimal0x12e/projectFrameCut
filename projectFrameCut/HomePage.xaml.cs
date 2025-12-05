using Microsoft.Maui.Graphics;
using projectFrameCut.DraftStuff;
using projectFrameCut.Shared;
using projectFrameCut.ViewModels;
using System.Collections.Concurrent;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO.Compression;
using System.Text.Json;
using System.Threading.Tasks;
using System.Windows.Input;
using projectFrameCut.Setting.SettingManager;

namespace projectFrameCut;

public partial class HomePage : ContentPage
{
    private readonly ProjectsListViewModel vm;

    private string lastItem;

    public HomePage()
    {
        InitializeComponent();
        WelcomeLabel.Text = Localized.HomePage_Welcome();
        vm = new ProjectsListViewModel();
        vm.LoadDrafts(Path.Combine(MauiProgram.DataPath, "My Drafts"));
        BindingContext = vm;
#if !ANDROID
        ProjectsCollection.SelectionChanged += CollectionView_SelectionChanged;
#endif

    }

    private async void CollectionView_SelectionChanged(object sender, Microsoft.Maui.Controls.SelectionChangedEventArgs e)
    {
        try
        {
            var selected = e.CurrentSelection?.FirstOrDefault() as ProjectsViewModel;
            if (selected is null)
            {
                return;
            }
            else
            {
#if WINDOWS
                if (lastItem == selected.Name)
                {
                    ProjectsCollection.SelectedItem = null;
                    if (selected._name == "!!CreateButton!!")
                    {
                        await CreateDraft();
                    }
                    else
                    {
                        await GoDraft(vm.Projects.First(s => s.Name == lastItem));
                    }
                    lastItem = "";

                }
                else
                {
                    lastItem = selected.Name;
                    ProjectsCollection.SelectedItem = null;
                }

#else
                if (selected._name == "!!CreateButton!!")
                {
                    await CreateDraft();
                }
                else
                {
                    await GoDraft(vm.Projects.First(s => s.Name == selected._name));
                }

                ProjectsCollection.SelectedItem = null;

#endif
            }
        }
        catch (Exception ex)
        {
            Log(ex, "open draft", this);
        }
        finally
        {
        }
    }

    private async Task CreateDraft()
    {
        string draftSourcePath = Path.Combine(MauiProgram.DataPath, "My Drafts");

        var projName = await DisplayPromptAsync(Localized._Info, Localized.HomePage_CreateAProject_InputName, Localized._OK, Localized._Cancel, "Untitled Project 1", 1024, null, "Untitled Project 1");
        if (projName is null) return;
        draftSourcePath = Path.Combine(draftSourcePath, projName + ".pjfc");
        if (Directory.Exists(draftSourcePath))
        {
            await DisplayAlertAsync(Localized._Info, Localized.HomePage_CreateAProject_Exists, Localized._OK);
            return;
        }
        Directory.CreateDirectory(draftSourcePath);
        var ProjectInfo = new ProjectJSONStructure
        {
            projectName = projName,
            NormallyExited = true,
            LastChanged = DateTime.Now
        };

        File.WriteAllText(
            Path.Combine(draftSourcePath, "timeline.json"),
            JsonSerializer.Serialize(new DraftStructureJSON
            {
                Clips = new List<ClipDraftDTO>().Cast<object>().ToArray(),
            }));
        File.WriteAllText(
            Path.Combine(draftSourcePath, "assets.json"),
            JsonSerializer.Serialize(Array.Empty<AssetItem>()));
        File.WriteAllText(
            Path.Combine(draftSourcePath, "project.json"),
            JsonSerializer.Serialize(ProjectInfo));

        vm.LoadDrafts(Path.Combine(MauiProgram.DataPath, "My Drafts"));


    }

    private async Task CloneDraft(ProjectsViewModel viewModel)
    {
        string draftSourcePath = Path.Combine(MauiProgram.DataPath, "My Drafts");

        var projName = await DisplayPromptAsync(Localized._Info, Localized.HomePage_CreateAProject_InputName, Localized._OK, Localized._Cancel, viewModel.Name + " (2)", 1024, null, viewModel.Name + " (2)");
        if (projName is null) return;
        draftSourcePath = Path.Combine(draftSourcePath, projName + ".pjfc");
        if (Directory.Exists(draftSourcePath))
        {
            await DisplayAlertAsync(Localized._Info, Localized.HomePage_CreateAProject_Exists, Localized._OK);
            return;
        }
        Directory.CreateDirectory(draftSourcePath);

        CopyDirectory(viewModel._projectPath, draftSourcePath);

        var ProjectInfo = new ProjectJSONStructure
        {
            projectName = projName,
            NormallyExited = true,
            LastChanged = DateTime.Now
        };

        File.WriteAllText(
            Path.Combine(draftSourcePath, "project.json"),
            JsonSerializer.Serialize(ProjectInfo));

        vm.LoadDrafts(Path.Combine(MauiProgram.DataPath, "My Drafts"));


    }



    public static void CopyDirectory(string sourceDir, string destDir)
    {
        Directory.CreateDirectory(destDir);

        foreach (string file in Directory.GetFiles(sourceDir))
        {
            string targetFile = Path.Combine(destDir, Path.GetFileName(file));
            File.Copy(file, targetFile, overwrite: true);
        }

        foreach (string directory in Directory.GetDirectories(sourceDir))
        {
            string targetSubDir = Path.Combine(destDir, Path.GetFileName(directory));
            CopyDirectory(directory, targetSubDir);
        }
    }

    private async Task GoDraft(ProjectsViewModel projectsViewModel, bool isReadonly = false, bool throwOnException = false)
    {
        var draftSourcePath = projectsViewModel._projectPath;
        if (Directory.Exists(draftSourcePath))
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

                List<AssetItem> assets = new();
                DraftStructureJSON timeline = new();

                try
                {
                    if (!(project?.NormallyExited ?? true)) throw new Exception("draft not saved correctly.");
                    assets = JsonSerializer.Deserialize<List<AssetItem>>(File.ReadAllText(Path.Combine(draftSourcePath, "assets.json"))) ?? new();
                    timeline = JsonSerializer.Deserialize<DraftStructureJSON>(File.ReadAllText(Path.Combine(draftSourcePath, "timeline.json"))) ?? new();
                }
                catch (Exception ex)
                {
                    try
                    {
                        bool skipAsk = SettingsManager.IsBoolSettingTrue("AutoRecoverDraft");
                        Log(ex, "read draft", this);
                        var conf = !skipAsk ? await DisplayAlertAsync(Localized._Warn, Localized.HomePage_GoDraft_DraftBroken, Localized._Confirm, Localized._Cancel) : true;
                        if (!conf) return;

                        Dictionary<string, DraftStructureJSON?> tmls = new();
                        foreach (var item in Directory.GetDirectories(Path.Combine(draftSourcePath, "saveSlots")))
                        {
                            if (File.Exists(Path.Combine(item, "timeline.json")))
                            {
                                try
                                {
                                    var tml = JsonSerializer.Deserialize<DraftStructureJSON?>(File.ReadAllText(Path.Combine(item, "timeline.json")));
                                    if (tml is not null)
                                    {
                                        tmls.Add(item, tml);
                                    }
                                }
                                catch (Exception exInner)
                                {
                                    Log(exInner, "read draft from save slot", this);
                                }
                            }
                        }

                        var newest = tmls.OrderByDescending((t) => t.Value.SavedAt).FirstOrDefault(new KeyValuePair<string, DraftStructureJSON?>("", null));
                        if (newest.Value is null)
                        {
                            await DisplayAlertAsync(Localized._Warn, Localized.HomePage_GoDraft_DraftBroken_Fail, Localized._OK);
                            return;
                        }
                        else
                        {

                            var result = !skipAsk ? await DisplayAlertAsync(Localized._Warn, Localized.HomePage_GoDraft_DraftBroken_Confirm(newest.Value.SavedAt), Localized._Confirm, Localized._Cancel) : true;

                            if (result)
                            {
                                assets = JsonSerializer.Deserialize<List<AssetItem>>(File.ReadAllText(Path.Combine(newest.Key, "assets.json"))) ?? new();
                                timeline = JsonSerializer.Deserialize<DraftStructureJSON>(File.ReadAllText(Path.Combine(newest.Key, "timeline.json"))) ?? new();
                                if (skipAsk) goto open;
                                await DisplayAlertAsync(Localized._Info, Localized.HomePage_GoDraft_DraftBroken_Success, Localized._OK);
                            }
                            else return;


                        }
                    }
                    catch (Exception exInner)
                    {
                        Log(exInner, "read draft from save slot confirm", this);
                        await DisplayAlertAsync(Localized._Warn, Localized.HomePage_GoDraft_DraftBroken_Fail, Localized._OK);
                        return;
                    }
                }
            open:
                (var dict, var trackCount) = DraftImportAndExportHelper.ImportFromJSON(timeline);
                var assetDict = new ConcurrentDictionary<string, AssetItem>(assets.ToDictionary((a) => a.AssetId ?? $"unknown+{Random.Shared.Next()}", (a) => a));

                page = new DraftPage(project ?? new(), dict, assetDict, trackCount, draftSourcePath, project?.projectName ?? "?", isReadonly);

#if WINDOWS || ANDROID
                AppShell.instance.HideNavView();
#elif iDevices
                if (OperatingSystem.IsMacCatalyst())
                {
                    AppShell_MacCatalyst.instance.HideNavView();
                }
                else
                {
                    AppShell.instance.HideNavView();
                }
#endif
                await Navigation.PushAsync(page);
            }
            catch (Exception ex)
            {
                if (throwOnException) throw;
                await DisplayAlert(Localized._Warn, Localized._ExceptionTemplate(ex), "ok");
            }
        }
    }

    protected override void OnAppearing()
    {
        base.OnAppearing();
#if WINDOWS || ANDROID
        AppShell.instance.ShowNavView();
#elif iDevices
        if (OperatingSystem.IsMacCatalyst())
        {
            AppShell_MacCatalyst.instance.ShowNavView();
        }
        else
        {
            AppShell.instance.ShowNavView();
        }
#endif
    }

    private async void MenuOpen_Clicked(object sender, EventArgs e)
    {
        ProjectsViewModel? pvm = null;
        if (sender is Microsoft.Maui.Controls.VisualElement ve && ve.BindingContext is ProjectsViewModel pv3) pvm = pv3;

        if (pvm is null) return;

        try
        {
            await GoDraft(pvm);
        }
        catch (Exception ex)
        {
            Log(ex, "open from context menu", this);
        }
    }

    private async Task DeleteProject(ProjectsViewModel pvm)
    {

        try
        {
            var confirm0 = await DisplayAlertAsync(Localized._Warn, Localized.HomePage_ProjectContextMenu_Delete_Confirm0(pvm.Name), Localized._Confirm, Localized._Cancel);
            if (!confirm0) return;
            var confirm1 = await DisplayAlertAsync(Localized._Warn, Localized.HomePage_ProjectContextMenu_Delete_Confirm1(pvm.Name), Localized._Confirm, Localized._Cancel);
            if (!confirm1) return;
#if WINDOWS
            bool confirm2 = false;
            Microsoft.UI.Xaml.Controls.ContentDialog lastDiag = new Microsoft.UI.Xaml.Controls.ContentDialog
            {
                Title = Localized._Warn,
                Content = Localized.HomePage_ProjectContextMenu_Delete_Confirm2(pvm.Name),
                PrimaryButtonText = Localized.HomePage_ProjectContextMenu_Delete_Confirm3(pvm.Name),
                CloseButtonText = Localized._Cancel,
                PrimaryButtonStyle = new Microsoft.UI.Xaml.Style(typeof(Microsoft.UI.Xaml.Controls.Button))
                {
                    Setters =
                    {
                        new Microsoft.UI.Xaml.Setter(
                            Microsoft.UI.Xaml.Controls.Control.BackgroundProperty,
                            Microsoft.UI.Xaml.Application.Current.Resources["SystemFillColorCriticalBackgroundBrush"]
                        )
                    }
                }
            };
            var services = Application.Current?.Handler?.MauiContext?.Services;
            var dialogueHelper = services?.GetService(typeof(projectFrameCut.Platforms.Windows.IDialogueHelper)) as projectFrameCut.Platforms.Windows.IDialogueHelper;
            if (dialogueHelper != null)
            {
                var result = await dialogueHelper.ShowContentDialogue(lastDiag);
                confirm2 = result == Microsoft.UI.Xaml.Controls.ContentDialogResult.Primary;
            }
#else
            var confirm2 = await DisplayAlertAsync(Localized._Warn, Localized.HomePage_ProjectContextMenu_Delete_Confirm2(pvm.Name), Localized.HomePage_ProjectContextMenu_Delete_Confirm3(pvm.Name), Localized._Cancel);
#endif
            if (!confirm2) return;

            if (Directory.Exists(pvm._projectPath))
            {
                Directory.Delete(pvm._projectPath, true);
            }
            vm.LoadDrafts(Path.Combine(MauiProgram.DataPath, "My Drafts"));
            await DisplayAlertAsync(Localized._Info, Localized.HomePage_ProjectContextMenu_Delete_Deleted(pvm.Name), Localized._OK);

        }
        catch (Exception ex)
        {
            Log(ex, "delete project", this);
        }
    }


    private async Task ExportProject(ProjectsViewModel vmItem)
    {
        var origCont = Content;
        Content = new ActivityIndicator
        {
            IsRunning = true,
            WidthRequest = 200,
            HeightRequest = 200
        };
        var fileName = $"{new string(vmItem.Name.Select(s => char.IsAsciiLetterOrDigit(s) ? s : '_').ToArray())}_{Guid.NewGuid()}.pjfc";
        var tmpPath = Path.Combine(FileSystem.CacheDirectory, fileName);
        await Task.Run(() =>
        {
            ZipFile.CreateFromDirectory(vmItem._projectPath, tmpPath, CompressionLevel.SmallestSize, includeBaseDirectory: false);
        });
        Content = origCont;
        await Share.RequestAsync(new ShareFileRequest()
        {
            File = new ShareFile(tmpPath),
            Title = fileName
        });
    }

    private async Task RenameProject(ProjectsViewModel vmItem)
    {
        var projName = await DisplayPromptAsync(Localized._Info, Localized.HomePage_CreateAProject_InputName, Localized._OK, Localized._Cancel, vmItem.Name, 1024, null, vmItem.Name);
        if (projName is null) return;
        var newPath = Path.Combine(Path.GetDirectoryName(vmItem._projectPath) ?? "", projName + ".pjfc");
        if (Directory.Exists(newPath))
        {
            await DisplayAlertAsync(Localized._Info, Localized.HomePage_CreateAProject_Exists, Localized._OK);
            return;
        }
        Directory.Move(vmItem._projectPath, newPath);

        var info = JsonSerializer.Deserialize<ProjectJSONStructure>(File.ReadAllText(Path.Combine(newPath, "project.json")));
        if (info is not null)
        {
            info.projectName = projName;
            File.WriteAllText(
                Path.Combine(newPath, "project.json"),
                JsonSerializer.Serialize(info));
        }

        vm.LoadDrafts(Path.Combine(MauiProgram.DataPath, "My Drafts"));
    }

    private async void ItemBorder_Loaded(object sender, EventArgs e)
    {
        if (sender is Microsoft.Maui.Controls.Border border && border.BindingContext is ProjectsViewModel vmItem)
        {
#if WINDOWS || MACCATALYST
            // Windows: Right-click to show context menu
            var tap = new TapGestureRecognizer { NumberOfTapsRequired = 1, Buttons = ButtonsMask.Secondary };
            tap.Tapped += async (_, _) =>
            {
                await ShowContextMenu(vmItem);
            };

            // remove existing tap to avoid duplicates
            var existing = border.GestureRecognizers.OfType<TapGestureRecognizer>().FirstOrDefault();
            if (existing is not null) border.GestureRecognizers.Remove(existing);
            border.GestureRecognizers.Add(tap);
#elif ANDROID || IOS
            // Android/iOS: Single tap to open, long press (>500ms) to show context menu
            var pointerGesture = new PointerGestureRecognizer();
            DateTime pointerDownTime = DateTime.MinValue;

            pointerGesture.PointerPressed += (s, e) =>
            {
                pointerDownTime = DateTime.Now;
            };

            pointerGesture.PointerReleased += async (s, e) =>
            {
                var duration = (DateTime.Now - pointerDownTime).TotalMilliseconds;
                // If held for more than 500ms, show context menu
                if (duration >= 500)
                {
                    await ShowContextMenu(vmItem);
                }
                else if (duration > 0)
                {
                    // Short tap to open
                    if (vmItem._name == "!!CreateButton!!")
                    {
                        await CreateDraft();
                    }
                    else
                    {
                        await GoDraft(vmItem);
                    }
                }
            };

            // Remove any existing pointer gesture recognizer to avoid duplicates
            var existingPointer = border.GestureRecognizers.OfType<PointerGestureRecognizer>().FirstOrDefault();
            if (existingPointer is not null) border.GestureRecognizers.Remove(existingPointer);

            border.GestureRecognizers.Add(pointerGesture);
#endif
        }
    }

    private async Task ShowContextMenu(ProjectsViewModel vmItem)
    {
        if (vmItem._name == "!!CreateButton!!")
        {
            return;
        }

        string[] verbs = [
                    Localized.HomePage_ProjectContextMenu_Open,
                    Localized.HomePage_ProjectContextMenu_OpenReadonly,
                    Localized.HomePage_ProjectContextMenu_Export,
                    Localized.HomePage_ProjectContextMenu_OpenInFileManager,
                    Localized.HomePage_ProjectContextMenu_Clone,
                    Localized.HomePage_ProjectContextMenu_Rename,
                    Localized.HomePage_ProjectContextMenu_Delete
                    ];


        if (SettingsManager.IsBoolSettingTrue("DeveloperMode"))
        {
            verbs = verbs.Append("Debug: throw the exceptions while opening").ToArray();
        }

        var action = await DisplayActionSheetAsync(vmItem.Name, Localized._Cancel, Localized.HomePage_ProjectContextMenu_Delete, verbs);

        if (SettingsManager.IsBoolSettingTrue("DeveloperMode"))
        {
            switch (action)
            {
                case "Debug: throw the exceptions while opening":
                    {
                        await GoDraft(vmItem, throwOnException: true);
                        return;
                    }
                default:
                    {
                        break;
                    }
            }
        }

        switch (Array.IndexOf(verbs, action))
        {
            case 0: //Open
                await GoDraft(vmItem);
                break;
            case 1: //OpenReadonly 
                await GoDraft(vmItem, isReadonly: true);
                break;
            case 2: //Export
                await ExportProject(vmItem);
                break;
            case 3: //OpenInFileManager
#if WINDOWS
                Process.Start(new ProcessStartInfo { FileName = vmItem._projectPath, UseShellExecute = true });
#elif ANDROID

#elif iDevices

#endif
                break;
            case 4: //Clone
                await CloneDraft(vmItem);
                break;
            case 5: //Rename
                await RenameProject(vmItem);
                break;
            case 6: //Delete
                await DeleteProject(vmItem);
                break;
            default: //unknown/cancel
                break;
        }
    }
}

public class ProjectsListViewModel
{
    public ObservableCollection<ProjectsViewModel> Projects { get; } = new();


    public ProjectsListViewModel()
    {
        //LoadSample();

    }

    public void LoadDrafts(string sourcePath)
    {
        try
        {
            if (!Directory.Exists(sourcePath))
                return;
            Projects.Clear();
            Projects.Add(new ProjectsViewModel
            {
                _name = "!!CreateButton!!",
                _thumbPath = "!!CreateButton!!"
            });
            foreach (var item in Directory.GetDirectories(sourcePath, "*"))
            {
                var projFile = Path.Combine(item, "project.json");
                if (!File.Exists(projFile)) continue;

                try
                {
                    var proj = JsonSerializer.Deserialize<ProjectJSONStructure>(File.ReadAllText(projFile));
                    if (proj is not null)
                    {
                        var thumb = Path.Combine(item, "thumbs", "_project.png");
                        Projects.Insert(Projects.Count - 1, new ProjectsViewModel(proj.projectName, proj.LastChanged ?? DateTime.MinValue, thumb)
                        {
                            _projectPath = item
                        });
                    }
                }
                catch (Exception exInner)
                {
                    Log(exInner, "load draft", this);
                }
            }
        }
        catch (Exception ex)
        {
            Log(ex, "load draft", this);

        }
    }

    public void LoadSample()
    {
        Projects.Insert(Projects.Count, new ProjectsViewModel("project 1", DateTime.Now.AddMinutes(-30), @"D:\code\playground\projectFrameCut\@Original_track_a.png"));
        Projects.Insert(Projects.Count, new ProjectsViewModel("a loooooooooooooooooooong name", DateTime.Now.AddHours(-5), @"D:\code\playground\projectFrameCut\@Original_track_b.png"));
        Projects.Insert(Projects.Count, new ProjectsViewModel("wtf?\r\n111", DateTime.Now.AddDays(-2), @"D:\code\playground\projectFrameCut\@Original_track_c.png"));
        Projects.Insert(Projects.Count, new ProjectsViewModel("1", DateTime.Now.AddDays(1), "nope.png"));
        Projects.Insert(Projects.Count, new ProjectsViewModel("1", DateTime.Now.AddDays(-100), "nope.png"));
    }

}