using projectFrameCut.DraftStuff;
using projectFrameCut.Shared;
using projectFrameCut.ViewModels;
using System.Collections.Concurrent;
using System.Collections.ObjectModel;
using System.Text.Json;
using System.Threading.Tasks;
using System.Windows.Input;

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

    }

    private async void CollectionView_SelectionChanged(object sender, SelectionChangedEventArgs e)
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
                if(lastItem == selected.Name)
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
        catch(Exception ex)
        {
            Log(ex, "open draft", this);
        }
    }

    private async Task CreateDraft()
    {
        string draftSourcePath = Path.Combine(MauiProgram.DataPath, "My Drafts");

        var projName = await DisplayPromptAsync(Localized._Info, Localized.HomePage_CreateAProject_InputName, Localized._OK, Localized._Cancel, "Untitled Project 1", 1024, null, "Untitled Project 1");
        if (projName is null) return;
        draftSourcePath = Directory.CreateDirectory(Path.Combine(draftSourcePath, projName + ".pjfc")).FullName;

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

    private async Task GoDraft(ProjectsViewModel projectsViewModel)
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

                if (!(project?.NormallyExited ?? true))
                {
                    var resume = await DisplayAlert(Localized._Warn, Localized.HomePage_GoDraft_ResumeLastTimeWarning(), Localized._OK, Localized._Cancel);
                    if (!resume) return;
                    var lastSlotPath = Path.Combine(draftSourcePath, "saveSlots", $"slot_{project.SaveSlotIndicator}");
                    File.Copy(Path.Combine(lastSlotPath, "timeline.json"), Path.Combine(draftSourcePath, "timeline.json"), true);
                    File.Copy(Path.Combine(lastSlotPath, "assets.json"), Path.Combine(draftSourcePath, "assets.json"), true);
                }

                var assets = JsonSerializer.Deserialize<List<AssetItem>>(File.ReadAllText(Path.Combine(draftSourcePath, "assets.json"))) ?? new();
                var assetDict = new ConcurrentDictionary<string, AssetItem>(assets.ToDictionary((a) => a.AssetId ?? $"unknown+{Random.Shared.Next()}", (a) => a));

                var timeline = JsonSerializer.Deserialize<DraftStructureJSON>(File.ReadAllText(Path.Combine(draftSourcePath, "timeline.json"))) ?? new();

                (var dict, var trackCount) = DraftImportAndExportHelper.ImportFromJSON(timeline);

                page = new DraftPage(project ?? new(), dict, assetDict, trackCount, draftSourcePath, project?.projectName ?? "?");

#if WINDOWS || ANDROID
                AppShell.instance.HideNavView();
#endif
                await Navigation.PushAsync(page);
            }
            catch (Exception ex)
            {
                await DisplayAlert(Localized._Warn, Localized._ExceptionTemplate(ex), "ok");
            }
        }
    }

    protected override void OnAppearing()
    {
        base.OnAppearing();
#if WINDOWS || ANDROID
        AppShell.instance.ShowNavView();
#endif
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