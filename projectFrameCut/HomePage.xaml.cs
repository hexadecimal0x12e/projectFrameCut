using LocalizedResources;
using Microsoft.Maui.Dispatching;
using Microsoft.Maui.Graphics;
using Microsoft.Maui.Platform;
using projectFrameCut.ApplicationAPIBase.Helpers;
using projectFrameCut.ApplicationAPIBase.Plugins;
using projectFrameCut.ApplicationAPIBase.Views.PropertyPanelBuilders;
using projectFrameCut.Asset;
using projectFrameCut.DraftStuff;
using projectFrameCut.Render.Plugin;
using projectFrameCut.Render.RenderAPIBase.Plugins;
using projectFrameCut.Render.RenderAPIBase.Project;
using projectFrameCut.Services;
using projectFrameCut.Setting.SettingManager;
using projectFrameCut.Shared;
using projectFrameCut.Template;
using projectFrameCut.ViewModels;
using System.Collections.Concurrent;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Globalization;
using System.IO.Compression;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Threading.Tasks;
using System.Windows.Input;
using static System.Net.Mime.MediaTypeNames;
using IPicture = projectFrameCut.Shared.IPicture;
using Application = Microsoft.Maui.Controls.Application;
using projectFrameCut.Setting.SettingPages;




#if WINDOWS
using projectFrameCut.Platforms.Windows;
using Windows.ApplicationModel.UserActivities;
using Microsoft.UI.Xaml.Media;
using ILGPU;

#endif

namespace projectFrameCut;

public partial class HomePage : ContentPage
{
    private readonly ProjectsListViewModel _viewModel;

    private const string CreateButtonName = "!!CreateButton!!";

    private string _lastSelectedItemName = string.Empty;

    public static bool HasAlreadyLaunchedFromFile = false;
    public static bool IsFontLoaded = false;
    public static bool IsWelcomePageShown = false;


    public HomePage()
    {
        InitializeComponent();
        WelcomeLabel.Text = Localized.HomePage_Welcome();
        if (DateTime.Now.Month == 4 && DateTime.Now.Day == 1 && int.TryParse(Preferences.Get("LastAprilFoolsDayEasterEggTriggerYear", "0000"), out var y) && y != DateTime.Now.Year) EasterEgg();
        _viewModel = new ProjectsListViewModel();
        BindingContext = _viewModel;
        Loaded += async (s, e) =>
        {
#if WINDOWS
            if (projectFrameCut.Helper.HelperProgram.SplashShowing)
            {
                projectFrameCut.Helper.HelperProgram.CloseSplash();
            }
            await projectFrameCut.WinUI.App.BringToForeground();

#endif
            if (VersionTracking.Default.IsFirstLaunchEver)
            {
                SettingsManager.WriteSetting("ui_ShowWelcomePage", "True");
            }
            if (SettingsManager.IsBoolSettingTrue("ui_ShowWelcomePage") && !IsWelcomePageShown)
            {
                IsWelcomePageShown = true;
                var p = new SetupPage();
                await Navigation.PushAsync(p);
                return;
            }
            await ShowManyAlertsAsync();
            if (!HasAlreadyLaunchedFromFile) await LaunchFromFile();
            HasAlreadyLaunchedFromFile = true;

            try
            {
                var defaultWidthOfCont = SettingsManager.GetSetting("ui_defaultWidthOfContent", "-1");
                if (!double.TryParse(defaultWidthOfCont, out var widthOfCont)) widthOfCont = -1d;
                if (widthOfCont <= 0)
                {
                    PropertyPanelBuilder.DefaultWidthOfContent = DeviceInfo.Idiom switch
                    {
                        var d when d == DeviceIdiom.Phone => 1,
                        var d when d == DeviceIdiom.Tablet => 3,
                        _ => 3

                    };
                }
                else
                {
                    PropertyPanelBuilder.DefaultWidthOfContent = widthOfCont;
                }

            }
            catch { }
            if (!IsFontLoaded)
            {
                var t = new Thread(TextServices.LoadFonts)
                {
                    Priority = ThreadPriority.BelowNormal,
                    IsBackground = true
                };
                t.Start();
                IsFontLoaded = true;
            }
#if WINDOWS
            try
            {
                if (SettingsManager.GetSetting("ui_defaultTheme", "default") != "default")
                {
                    MainThread.InvokeOnMainThreadAsync(() =>
                    {
                        WinUI.App.Instance?.RequestedTheme = SettingsManager.GetSetting("ui_defaultTheme", "default") switch
                        {
                            "dark" => Microsoft.UI.Xaml.ApplicationTheme.Dark,
                            _ => Microsoft.UI.Xaml.ApplicationTheme.Light,
                        };
                    });
                }

            }
            catch { }



#endif
        };


    }

    public async Task LaunchFromFile()
    {
        HasAlreadyLaunchedFromFile = true;
        var origCont = Content;
        Dispatcher.Dispatch(() =>
        {
            Content = new ActivityIndicator
            {
                IsRunning = true,
                WidthRequest = 200,
                HeightRequest = 200
            };
        });
        try
        {
            string path = "";

            var args = MauiProgram.CmdlineArgs.ToArray();
            if (args.ArrayAny())
            {
                var maybePath = args.OrderByDescending(s => s.Length).First();
                if (maybePath.Contains(':') && maybePath.Count(c => c == ':') >= 2)
                {
                    path = maybePath.Split(":", 2, StringSplitOptions.RemoveEmptyEntries)[1] ?? maybePath;
                }
                else
                {
                    path = maybePath;
                }
            }

            if (Preferences.ContainsKey("LaunchedPJFCUri"))
            {
                path = Preferences.Get("LaunchedPJFCUri", "");
            }
            LogDiagnostic($"Launch target from cli args:{path}");
            if (string.IsNullOrWhiteSpace(path)) return;
            switch (Path.GetExtension(path))
            {
                case ".pjfc":
                    {
                        if (File.Exists(path) || Directory.Exists(path))
                        {
                            if (Directory.Exists(path))
                            {
                                if (File.Exists(Path.Combine(path, "project.pjfc")))
                                {
                                    path = Path.Combine(path, "project.pjfc");
                                }
                                else if (File.Exists(Path.Combine(path, "project.json")))
                                {
                                    path = Path.Combine(path, "project.json");
                                }
                                else
                                {
                                    await DisplayAlertAsync(Localized._Error, $"Cannot find a valid project file in the directory '{path}'.", Localized._OK);
                                    return;
                                }
                            }

                            if (new FileInfo(path).OpenRead().ReadByte() == '{')
                            {
                                try
                                {
                                    var draft = JsonSerializer.Deserialize<ProjectJSONStructure>(File.ReadAllText(path), DraftPage.DraftJSONOption);
                                    if (draft is ProjectJSONStructure && Path.GetDirectoryName(path) is string p)
                                    {
                                        await GoDraft(p, draft.ProjectName ?? "Project", skipAskForRecover: args.Any(c => c.StartsWith("--fromCrashHandler")));
                                        return;

                                    }
                                }
                                catch
                                {

                                }

                            }
                            else
                            {
                                await ImportDraft(path);
                            }
                        }
                        break;
                    }
                case ".pjfcPlugin":
                    {
                        if (File.Exists(path))
                        {
                            try
                            {
                                await DisplayAlertAsync(Localized._Warn, SettingsManager.SettingLocalizedResources.Plugin_LoadWarn, Localized._OK);
                                await PluginService.AddAPlugin(path, this);
                            }
                            catch (Exception ex)
                            {
                                await DisplayAlertAsync(Localized._Error, $"{Localized.HomePage_Import_CannotAddPlugin}\r\n({Localized._ExceptionTemplate(ex)})", Localized._OK);
                            }
                        }
                        break;
                    }

                default:
                    {
                        if (Directory.Exists(path))
                        {
                            if (File.Exists(Path.Combine(path, "project.json")) || File.Exists(Path.Combine(path, "project.pjfc")))
                            {
                                await GoDraft(path, (Path.GetDirectoryName(path) ?? "Project").Split('.')?.FirstOrDefault("Project")!, false, false);

                            }
                        }
                        else
                        {
                            //await DisplayAlertAsync(Localized._Error, $"Cannot launch from the file '{path}' because of it's invalid.", Localized._OK);
                        }
                        break;
                    }


            }

        }
        catch (Exception ex)
        {
            Log(ex, "Launch from file", this);
            await DisplayAlertAsync(Localized._Error, "Cannot launch from file. Try again later.", Localized._OK);
        }
        finally
        {
            Dispatcher.Dispatch(() =>
            {
                Content = origCont;
            });
        }
    }

    private async void CollectionView_SelectionChanged(object? sender, Microsoft.Maui.Controls.SelectionChangedEventArgs e)
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
#if WINDOWS || MACCATALYST
                if (_lastSelectedItemName == selected.Name)
                {
                    ProjectsCollection.SelectedItem = null;
                    if (selected._name == CreateButtonName)
                    {
                        await Dispatcher.DispatchAsync(CreateDraft);
                    }
                    else
                    {
                        await Dispatcher.DispatchAsync(async () => await GoDraft(_viewModel.Projects.First(s => s.Name == _lastSelectedItemName)));
                    }
                    _lastSelectedItemName = string.Empty;

                }
                else
                {
                    _lastSelectedItemName = selected.Name;
                    ProjectsCollection.SelectedItem = null;
                }

#else
                if (selected._name == CreateButtonName)
                {
                    await CreateDraft();
                }
                else
                {
                    await GoDraft(_viewModel.Projects.First(s => s.Name == selected._name));
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

        var projName = await DisplayPromptAsync(Localized._Info, Localized.HomePage_CreateAProject_InputName, Localized._OK, Localized._Cancel, "Untitled Project", 1024, null, $"Untitled Project {DateTime.Now:yyyy\\-M\\-dd}");
        if (projName is null || string.IsNullOrWhiteSpace(projName))
        {
            return;
        }
        if (Path.GetInvalidPathChars().Any(projName.Contains) || Path.GetInvalidFileNameChars().Any(projName.Contains))
        {
            await DisplayAlertAsync(Localized._Error, Localized.HomePage_CreateAProject_InvalidName, Localized._OK);
            return;
        }

        draftSourcePath = Path.Combine(draftSourcePath, projName + ".pjfc");
        if (Path.GetInvalidPathChars().Any(draftSourcePath.Contains) || draftSourcePath.Length > 65535)
        {
            await DisplayAlertAsync(Localized._Error, Localized.HomePage_CreateAProject_InvalidName, Localized._OK);
            return;
        }

        if (Directory.Exists(draftSourcePath))
        {
            await DisplayAlertAsync(Localized._Info, Localized.HomePage_CreateAProject_Exists, Localized._OK);
            return;
        }

        try
        {
            Directory.CreateDirectory(draftSourcePath);
        }
        catch (Exception ex)
        {
            await DisplayAlertAsync(Localized._Error, Localized.HomePage_CreateAProject_InvalidName, Localized._OK);
            return;
        }

        var ProjectInfo = new ProjectJSONStructure
        {
            ProjectName = projName,
            NormallyExited = true,
            LastChanged = DateTime.Now,
            LastOpenAPIBaseVersion = IPluginBase.CurrentPluginAPIVersion,
            LastOpenAppVersion = Assembly.GetExecutingAssembly().GetName().Version?.ToString() ?? "Unknown",
            PluginUsed = []
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
            Path.Combine(draftSourcePath, "project.pjfc"),
            JsonSerializer.Serialize(ProjectInfo));
        await Task.Delay(1500);
        await _viewModel.LoadDrafts(Path.Combine(MauiProgram.DataPath, "My Drafts"));


    }

    private async Task CloneDraft(ProjectsViewModel viewModel)
    {
        string draftSourcePath = Path.Combine(MauiProgram.DataPath, "My Drafts");

        var projName = await DisplayPromptAsync(Localized._Info, Localized.HomePage_CreateAProject_InputName, Localized._OK, Localized._Cancel, viewModel.Name + " (2)", 1024, null, viewModel.Name + " (2)");
        if (projName is null) return;
        if (projName is null || string.IsNullOrWhiteSpace(projName))
        {
            return;
        }
        if (Path.GetInvalidPathChars().Any(projName.Contains) || Path.GetInvalidFileNameChars().Any(projName.Contains))
        {
            await DisplayAlertAsync(Localized._Error, Localized.HomePage_CreateAProject_InvalidName, Localized._OK);
            return;
        }

        draftSourcePath = Path.Combine(draftSourcePath, projName + ".pjfc");
        if (Path.GetInvalidPathChars().Any(draftSourcePath.Contains) || Path.GetInvalidFileNameChars().Any(draftSourcePath.Contains) || draftSourcePath.Length > 65535)
        {
            await DisplayAlertAsync(Localized._Error, Localized.HomePage_CreateAProject_InvalidName, Localized._OK);
            return;
        }
        if (Directory.Exists(draftSourcePath))
        {
            await DisplayAlertAsync(Localized._Info, Localized.HomePage_CreateAProject_Exists, Localized._OK);
            return;
        }

        try
        {
            Directory.CreateDirectory(draftSourcePath);
            CopyDirectory(viewModel._projectPath, draftSourcePath);

        }
        catch (Exception ex)
        {
            Log(ex, "clone draft", this);
            await DisplayAlertAsync(Localized._Error, $"{Localized.HomePage_CreateAProject_InvalidName}{Environment.NewLine}{Localized._ExceptionTemplate(ex)}", Localized._OK);
            return;
        }

        var ProjectInfo = new ProjectJSONStructure
        {
            ProjectName = projName,
            NormallyExited = true,
            LastChanged = DateTime.Now,
            LastOpenAPIBaseVersion = IPluginBase.CurrentPluginAPIVersion,
            LastOpenAppVersion = Assembly.GetExecutingAssembly().GetName().Version?.ToString() ?? "Unknown",
            PluginUsed = []
        };

        File.WriteAllText(
            Path.Combine(draftSourcePath, "project.json"),
            JsonSerializer.Serialize(ProjectInfo));

        await _viewModel.LoadDrafts(Path.Combine(MauiProgram.DataPath, "My Drafts"));


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

    private async Task GoDraft(ProjectsViewModel pvm, bool isReadonly = false, bool throwOnException = false)
        => await GoDraft(pvm._projectPath, pvm.Name, isReadonly, throwOnException);

    private async Task GoDraft(string draftSourcePath, string title, bool isReadonly = false, bool throwOnException = false, bool? skipAskForRecover = null, bool? overrideLayoutOption = null)
    {
        LogDiagnostic($"Loading draft {draftSourcePath}, {title}, \r\n{Environment.StackTrace}");
        Stopwatch initTimer = Stopwatch.StartNew();
        bool cancelled = false;
        if (!Directory.Exists(draftSourcePath))
        {
            await Dispatcher.DispatchAsync(async () => await DisplayAlertAsync(Localized._Warn, "Draft not found.", Localized._OK));
            return;
        }
        DraftPage? page = null;
        var origContent = Content;
        var cancelButton = new Button
        {
            Text = Localized._Cancel
        };
        cancelButton.Clicked += async (s, e) =>
        {
            cancelled = true;
            await Dispatcher.DispatchAsync(() =>
            {
                Content = origContent;
            });
        };
        //TODO: Do you know
        /*
        string doyouknowText = "";
        try
        {
            if (File.Exists(Path.Combine(MauiProgram.DataPath, "DoYouKnow.txt")))
            {
                using var lines = new StreamReader(Path.Combine(MauiProgram.DataPath, "DoYouKnow.txt"));
                doyouknowText = (await lines.ReadToEndAsync())?.Split("\r\n")?.PickRandom(1)?.FirstOrDefault()?.Trim() ?? "";
            }
            else
            {
                using var lines = new StreamReader(await FileSystem.OpenAppPackageFileAsync($"DoYouKnow/{Localized._LocaleId_}.txt"));
                doyouknowText = (await lines.ReadToEndAsync())?.Split("\r\n")?.PickRandom(1)?.FirstOrDefault()?.Trim() ?? "";
            }
        }
        catch { }
        */
        await Dispatcher.DispatchAsync(
            async () =>
            Content = new VerticalStackLayout
            {
                HorizontalOptions = LayoutOptions.Center,
                VerticalOptions = LayoutOptions.Center,
                Children =
                {
                    new ActivityIndicator
                    {
                        IsRunning = true
                    },
                    new Label
                    {
                        Text = Localized.LandingPage_TakingToDraft(title),
                        HorizontalTextAlignment = Microsoft.Maui.TextAlignment.Center,
                        Margin = new Microsoft.Maui.Thickness(0.0, 8.0, 0.0, 8.0)
                    },
                    cancelButton,
                    /*
                    new Label
                    {
                        Text = doyouknowText,
                        TextColor = Colors.Gray,
                        HorizontalTextAlignment = Microsoft.Maui.TextAlignment.Center,
                        Margin = new Microsoft.Maui.Thickness(0.0, 12.0, 0.0, 0.0),
                        FontSize = 24
                    },
                    */
                }
            });
        ProjectJSONStructure project = new();
        try
        {
            if (File.Exists(Path.Combine(draftSourcePath, "project.pjfc")))
            {
                project = JsonSerializer.Deserialize<ProjectJSONStructure>(File.ReadAllText(Path.Combine(draftSourcePath, "project.pjfc")), DraftPage.DraftJSONOption) ?? new();
            }
            else if (File.Exists(Path.Combine(draftSourcePath, "project.json")))
            {
                project = JsonSerializer.Deserialize<ProjectJSONStructure>(File.ReadAllText(Path.Combine(draftSourcePath, "project.json")), DraftPage.DraftJSONOption) ?? new();
            }
            else
            {
                await Dispatcher.DispatchAsync(async () => await DisplayAlertAsync(Localized._Error, $"{Localized.HomePage_GoDraft_DraftBroken_InvaildInfo}", Localized._OK));
                await Dispatcher.DispatchAsync(async () => Content = origContent);
                return;
            }
        }
        catch (Exception ex)
        {
            Log(ex, "get project info", this);
            await Dispatcher.DispatchAsync(async () => await DisplayAlertAsync(Localized._Error, $"{Localized.HomePage_GoDraft_DraftBroken_InvaildInfo}\r\n({ex.Message})", Localized._OK));
            await Dispatcher.DispatchAsync(async () => Content = origContent);
            return;
        }

        project.SnapshotIDMapping = ProjectJSONStructure.LoadSnapshotMapping(draftSourcePath, DraftPage.DraftJSONOption);
        if (project.SnapshotIDMapping.Count == 0)
        {
            project.SnapshotIDMapping = ProjectJSONStructure.RebuildSnapshotMappingFromSlots(draftSourcePath, DraftPage.DraftJSONOption);
        }

        if (!await CheckProjectVersionCompatibility(project))
        {
            await Dispatcher.DispatchAsync(async () => Content = origContent);
            return;
        }

        await Task.Run(async () =>
        {
            try
            {
                while (!MauiProgram.IsAppReady)
                {
                    await Task.Delay(500);
                }
                if (!Directory.Exists(draftSourcePath))
                {
                    throw new DirectoryNotFoundException("Working path not found: " + draftSourcePath);
                }
                string[] filesShouldExist = [(File.Exists(Path.Combine(draftSourcePath, "project.pjfc")) ? "project.pjfc" : "project.json"), "timeline.json", "assets.json"];
                if (filesShouldExist.Any((f) => !File.Exists(Path.Combine(draftSourcePath, f))))
                {
                    await Dispatcher.DispatchAsync(async () =>
                    {
                        await DisplayAlertAsync(Localized._Warn, $"{Localized.HomePage_GoDraft_DraftBroken_InvaildInfo}\r\n(These files are missing:{string.Join(", ", filesShouldExist.Where(f => !File.Exists(Path.Combine(draftSourcePath, f))))})", Localized._OK);
                    });
                    return;
                }
                if (!File.Exists(Path.Combine(draftSourcePath, "project.pjfc")) && File.Exists(Path.Combine(draftSourcePath, "project.json")))
                {
                    File.Move(Path.Combine(draftSourcePath, "project.json"), Path.Combine(draftSourcePath, "project.pjfc"));
                }
                if (!project.NormallyExited) goto recover;
                List<AssetItem> assets;
                DraftStructureJSON timeline;
                try
                {
                    assets = JsonSerializer.Deserialize<List<AssetItem>>(File.ReadAllText(Path.Combine(draftSourcePath, "assets.json")), DraftPage.DraftJSONOption) ?? new();
                    timeline = JsonSerializer.Deserialize<DraftStructureJSON>(File.ReadAllText(Path.Combine(draftSourcePath, "timeline.json")), DraftPage.DraftJSONOption) ?? new();
                    goto ok;
                }
                catch (Exception ex)
                {
                    Log(ex, "read draft", this);
                    goto recover;
                }

            recover:
                try
                {
                    bool skipAsk = skipAskForRecover ?? SettingsManager.IsBoolSettingTrue("AutoRecoverDraft");
                    bool conf = false;
                    await Dispatcher.DispatchAsync(async delegate
                    {
                        conf = skipAsk || await DisplayAlertAsync(Localized._Warn, Localized.HomePage_GoDraft_DraftBroken, Localized._Confirm, Localized._Cancel);
                    });
                    if (!conf) return;
                    Dictionary<string, DraftStructureJSON?> tmls = new Dictionary<string, DraftStructureJSON?>();
                    string[] directories = Directory.GetDirectories(Path.Combine(draftSourcePath, "saveSlots"));
                    foreach (string item in directories)
                    {
                        if (File.Exists(Path.Combine(item, "timeline.json")))
                        {
                            try
                            {
                                DraftStructureJSON? tml = JsonSerializer.Deserialize<DraftStructureJSON>(File.ReadAllText(Path.Combine(item, "timeline.json")));
                                if (tml != null)
                                {
                                    tmls.Add(item, tml);
                                }
                            }
                            catch (Exception ex2)
                            {
                                Exception exInner = ex2;
                                Logger.Log(exInner, "read draft from save slot", this);
                            }
                        }
                    }
                    KeyValuePair<string, DraftStructureJSON?> newest = tmls.OrderByDescending(t => t.Value?.SavedAt).FirstOrDefault(new KeyValuePair<string, DraftStructureJSON?>("", null));
                    bool result = false;
                    await Dispatcher.DispatchAsync(async () =>
                    {
                        if (newest.Value is null)
                        {
                            await DisplayAlertAsync(Localized._Warn, Localized.HomePage_GoDraft_DraftBroken_Fail, Localized._OK);
                        }
                        else
                        {
                            result = skipAsk || await DisplayAlertAsync(Localized._Warn, Localized.HomePage_GoDraft_DraftBroken_Confirm(newest.Value.SavedAt), Localized._Confirm, Localized._Cancel);
                        }
                    });
                    if (!result)
                    {
                        return;
                    }
                    assets = JsonSerializer.Deserialize<List<AssetItem>>(File.ReadAllText(Path.Combine(newest.Key, "assets.json"))) ?? new List<AssetItem>();
                    timeline = JsonSerializer.Deserialize<DraftStructureJSON>(File.ReadAllText(Path.Combine(newest.Key, "timeline.json"))) ?? new DraftStructureJSON();
                    if (!skipAsk)
                    {
                        await Dispatcher.DispatchAsync(async () =>
                        {
                            await DisplayAlertAsync(Localized._Info, Localized.HomePage_GoDraft_DraftBroken_Success, Localized._OK);
                        });
                    }
                }
                catch (Exception ex3)
                {
                    Log(ex3, "read draft from save slot confirm", this);
                    await Dispatcher.DispatchAsync(async () =>
                    {
                        await DisplayAlertAsync(Localized._Warn, $"{Localized.HomePage_GoDraft_DraftBroken_Fail}\r\n({ex3.Message})", Localized._OK);
                    });
                    return;
                }
            ok:
                (var dict, var trackCount) = DraftImportAndExportHelper.ImportFromJSON(timeline, project);
                ConcurrentDictionary<string, AssetItem> assetDict = new ConcurrentDictionary<string, AssetItem>(assets.ToDictionary((AssetItem a) => a.AssetId ?? $"unknown+{Random.Shared.Next()}", (AssetItem a) => a));
                Dictionary<string, AssetItem> notfounds = new();
                foreach (var item in dict)
                {
                    if (!string.IsNullOrWhiteSpace(item.Value.SourcePath) && !item.Value.SourcePath.StartsWith('#'))
                    {
                        if (item.Value.SourcePath.StartsWith('$') && AssetDatabase.Assets.TryGetValue(item.Value.SourcePath.Substring(1), out var a))
                        {
                            AssetsLibraryPage.StartPerAssetThumbGeneration(a);
                            continue;
                        }
                        else if (Path.IsPathRooted(item.Value.SourcePath) ? File.Exists(item.Value.SourcePath) : File.Exists(Path.Combine(draftSourcePath, item.Value.SourcePath)))
                        {
                            continue; // draft will handle thumb generation
                        }
                        else
                        {
                            notfounds.Add(item.Value.Id, new AssetItem { AssetId = item.Value.Id, Name = item.Value.SourcePath.StartsWith('$') ? $"Asset@{item.Value.SourcePath.Substring(1)}" : item.Value.SourcePath, Path = item.Value.SourcePath });
                        }
                    }
                }
                foreach (var item in assetDict)
                {
                    if (!string.IsNullOrWhiteSpace(item.Value.Path) && !item.Value.Path.StartsWith('#') && !File.Exists(item.Value.Path))
                    {
                        notfounds.Add(item.Value?.AssetId ?? Guid.NewGuid().ToString(), item.Value);
                    }
                }
                if (notfounds.Any())
                {
                    var notFoundStr = notfounds.Select(kv => $"- {kv.Value.Name} ({kv.Value.Path})").Aggregate((a, b) => $"{a}{Environment.NewLine}{b}");
                    await Dispatcher.DispatchAsync(async () =>
                    {
                        int result = 0;
#if WINDOWS
                        Microsoft.UI.Xaml.Controls.ContentDialog diag = new Microsoft.UI.Xaml.Controls.ContentDialog
                        {
                            Title = Localized.HomePage_SourceNotFound_Title,
                            Content = $"{Localized.HomePage_SourceNotFound}\r\n{notFoundStr}",
                            CloseButtonText = Localized._Cancel,
                            PrimaryButtonText = Localized.HomePage_SourceNotFound_Continue,
                            SecondaryButtonText = Localized.HomePage_SourceNotFound_RemoveThem
                        };

                        var services = Application.Current?.Handler?.MauiContext?.Services;
                        var dialogueHelper = services?.GetService(typeof(projectFrameCut.Platforms.Windows.IDialogueHelper)) as projectFrameCut.Platforms.Windows.IDialogueHelper;
                        if (dialogueHelper != null)
                        {
                            var r = await dialogueHelper.ShowContentDialogue(diag);
                            result = (int)r;
                        }
#else
                        string[] opts = [Localized.HomePage_SourceNotFound_RemoveThem, Localized.HomePage_SourceNotFound_Continue];

                        var select = await DisplayActionSheetAsync($"{Localized.HomePage_SourceNotFound}\r\n{notFoundStr}", null, Localized._Cancel, opts);
                        if (select == Localized.HomePage_SourceNotFound_RemoveThem) result = 2;
                        else if (select == Localized.HomePage_SourceNotFound_Continue) result = 1;
                        else result = 0;
#endif


                        switch (result)
                        {
                            case 0:
                                {
                                    page = null;
                                    return;
                                }
                            case 1:
                                {
                                    break;
                                }
                            case 2:
                                {
                                    var input = await DisplayPromptAsync(Localized._Warn, Localized.HomePage_SourceNotFound_RemoveThem_Conf, Localized._OK, Localized._Cancel, "no", -1, null, null);
                                    if (input != "yes") return;
                                    foreach (var item in notfounds)
                                    {
                                        dict = new(dict.RemoveRange(dict.Where(c => c.Value.SourcePath == item.Value.Path)));
                                        assetDict = new(assetDict.RemoveRange(assetDict.Where(c => c.Key == item.Key)));
                                    }

                                    break;
                                }
                        }
                    });
                }

                if (!SettingsManager.IsSettingExists("Edit_PreferredPopupMode"))
                {
                    SettingsManager.WriteSetting("Edit_PreferredPopupMode", "bottom");
                }
                if (!(SettingsManager.IsSettingExists("Edit_UseDynamicPreview") || SettingsManager.IsSettingExists("Edit_LiveVideoPreviewDefaultResolution"))) SettingsManager.WriteSetting("Edit_UseDynamicPreview", true.ToString());
                {
                    int maxRetries = 5;
                    int attempt = 0;
                    while (true)
                    {
                        try
                        {
                            page = new DraftPage(project ?? new ProjectJSONStructure(), dict, assetDict, trackCount, draftSourcePath, project?.ProjectName ?? "?", isReadonly)
                            {
                                ProjectName = project?.ProjectName ?? "?",
                                IsReadonly = isReadonly,
                                Denoise = SettingsManager.IsBoolSettingTrue("Edit_Denoise"),
                                PreferredPopupMode = SettingsManager.GetSetting("Edit_PreferredPopupMode", "bottom"),
                                MaximumSaveSlot = SettingsManager.GetSettingAs("Edit_MaximumSaveSlot", 50, 50),
                                AlwaysShowToolbarBtns = SettingsManager.IsBoolSettingTrue("Edit_AlwaysShowToolbarButtons"),
                                ShowBackendConsole = SettingsManager.IsBoolSettingTrue("render_ShowBackendConsole"),
                                LiveVideoPreviewBufferLength = int.TryParse(SettingsManager.GetSetting("Edit_LiveVideoPreviewBufferLength", "240"), out var bufferLen) ? bufferLen : 240,
                                LivePreviewResolutionFactor = int.TryParse(SettingsManager.GetSetting("Edit_LiveVideoPreviewZoomFactor", "8"), out var resolutionFactor) ? resolutionFactor : 8,
                                UseDynamicPreview = SettingsManager.IsBoolSettingTrue("Edit_UseDynamicPreview"),
                                ProxyOption = SettingsManager.GetSetting("Edit_ProxyOption", "none"),
                                AutoSavePreviewAreaHeight = SettingsManager.IsBoolSettingTrue("Edit_UpperContentHeight_AutoSave"),
                                LockScrollViewAfterSelection = SettingsManager.IsBoolSettingTrueOrDefault("Edit_LockScrollViewAfterSelection", true),
                                UseCommunityToolkitPopupInsteadOfOverlayLayer = SettingsManager.IsBoolSettingTrue("Edit_UseCommunityToolkitPopupInsteadOfOverlayLayer"),
                                PreviewAreaHeight = double.TryParse(SettingsManager.GetSetting("Edit_UpperContentHeight", "250"), out var upperHeight) ? upperHeight : 250d,
                                UseCompactLayout = overrideLayoutOption ?? DeviceInfo.Idiom == DeviceIdiom.Phone,
                                EnableClipInfoPopup = SettingsManager.IsBoolSettingTrue("Edit_EnableClipInfoPopup")
                            };
                            if (!SettingsManager.IsBoolSettingTrue("Edit_UseDynamicPreview"))
                            {
                                var resolution = SettingsManager.GetSetting("Edit_LiveVideoPreviewDefaultResolution", "1280x720");
                                if (resolution.Split('x', 2).Length >= 2)
                                {
                                    page.DefaultPreviewWidth = int.TryParse(resolution.Split('x', 2)[0], out var w) ? w : 1280;
                                    page.DefaultPreviewHeight = int.TryParse(resolution.Split('x', 2)[1], out var h) ? h : 720;
                                }
                                else
                                {
                                    page.DefaultPreviewWidth = 1280;
                                    page.DefaultPreviewHeight = 720;
                                }
                            }
#if WINDOWS
                            ILGPU.Context context = ILGPU.Context.CreateDefault();
                            var devices = context.Devices.ToList();
                            List<AcceleratorInfo> listAccels = new();
                            for (uint i = 0; i < devices.Count; i++)
                            {
                                var item = devices[(int)i];
                                listAccels.Add(new AcceleratorInfo(i, item.Name, item.AcceleratorType.ToString()));
                            }
                            if (!int.TryParse(SettingsManager.GetSetting("accel_DeviceId", "-1"), out var result) || result < 0 || !(listAccels?.Any(c => c.index == result) ?? false))
                            {
                                var bestAccel = listAccels?.Select(c => (c, c.Type switch { "Cuda" => 10, "OpenCL" => 5, "CPU" => -10, _ => 1 })).OrderByDescending(c => c.Item2).ThenByDescending(c => c.c.name).FirstOrDefault();
                                SettingsManager.WriteSetting("accel_DeviceId", (bestAccel?.c.index ?? 0).ToString());
                                Log($"No accelerator defined yet; set to best one {bestAccel?.c.name} ({bestAccel?.c.Type}) by default.");
                            }
                            var accelDevice = devices.Index().Select(t => new KeyValuePair<int, ILGPU.Runtime.Device>(t.Index, t.Item))
                                                    .FirstOrDefault((t) => t.Key == (int.TryParse(SettingsManager.GetSetting("accel_DeviceId", "-1"), out var accelIdx) ? accelIdx : -1),
                                                    new KeyValuePair<int, ILGPU.Runtime.Device>(-1, devices.FirstOrDefault(c => c.AcceleratorType != ILGPU.Runtime.AcceleratorType.CPU, devices.First()))).Value;
                            page.AcceleratorToUse = accelDevice.CreateAccelerator(context);
#endif
                            await page.PostInit();
                            foreach (var plugin in PluginManager.LoadedPlugins.Values.OfType<IApplicationPluginBase>())
                            {
                                try
                                {
                                    plugin.InjectUI(page);
                                    var name = plugin.ReadLocalizationItem("_PluginBase_Name_", Localized._LocaleId_) ?? plugin.Name;
                                    var items = plugin.GetMenuItems(page);
                                    var sub = new MenuFlyoutSubItem { Text = name, IsEnabled = items.Any() };
                                    items.ForEach(c => sub.Add(c));
                                    page.ExtensionsMenuBar.Add(sub);

                                }
                                catch (Exception ex)
                                {
                                    Log(ex, $"plugin {plugin.Name} InjectUI", this);
                                    if (!await DisplayAlertAsync(Localized._Warn, Localized.HomePage_InitPlugin_Fail(plugin.Name, ex), Localized.HomePage_SourceNotFound_Continue, Localized._Cancel))
                                    {
                                        page = null;
                                        return;
                                    }
                                }
                            }
                            break;
                        }
                        catch (COMException comEx)
                        {
                            attempt++;
                            Log(comEx, $"COMException while loading DraftPage attempt {attempt}", this);
                            if (attempt >= maxRetries)
                            {
                                throw;
                            }
                            await Task.Delay(500);
                            continue;
                        }
                    }
                }

                foreach (var item in PluginManager.LoadedPlugins)
                {
                    try
                    {
                        project = item.Value.OnProjectLoad(project) ?? project;
                    }
                    catch (Exception ex)
                    {
                        Log(ex, $"plugin {item.Value.Name} OnProjectLoad", this);
                        if (!await DisplayAlertAsync(Localized._Warn, Localized.HomePage_InitPlugin_Fail(item.Value.Name, ex), Localized.HomePage_SourceNotFound_Continue, Localized._Cancel))
                        {
                            page = null;
                            return;
                        }

                    }
                }

#if WINDOWS //for recall/timeline

                await Dispatcher.DispatchAsync(async () =>
                {

                    try
                    {
                        await MainThread.InvokeOnMainThreadAsync(async () =>
                        {
                            try
                            {
                                _previousSession?.Dispose();
                                var activity = await UserActivityChannel.GetDefault().GetOrCreateUserActivityAsync($"projectFrameCut_draft_{project?.ProjectName ?? "Project"}");
                                activity.ActivationUri = new Uri($"pjfc:{Path.Combine(draftSourcePath, "project.pjfc")}");
                                activity.VisualElements.DisplayText = project?.ProjectName ?? "Project";
                                activity.VisualElements.Description = $"Continue work on {project?.ProjectName ?? "Project"}";
                                await activity.SaveAsync();
                                _previousSession = activity.CreateSession();
                            }
                            catch { }
                        });

                    }
                    catch { }

                    try
                    {
                        if (WinUI.App.IsPackaged())
                        {
                            var jumpList = await Windows.UI.StartScreen.JumpList.LoadCurrentAsync();
                            var task = Windows.UI.StartScreen.JumpListItem.CreateWithArguments($"\"{Path.Combine(draftSourcePath, "project.pjfc")}\"", project?.ProjectName ?? "Project");
                            task.GroupName = Localized.AppShell_ProjectsTab;
                            task.Logo = new Uri("ms-appx:///Images/Logo.png");
                            task.Description = $"Continue work on {project?.ProjectName ?? "Project"}";

                            jumpList.Items.Add(task);
                            await jumpList.SaveAsync();
                        }
                    }
                    catch { }

                });
#endif
            }
            catch (Exception ex4)
            {
                Log(ex4, $"Load project {project?.ProjectName}", this);
                if (throwOnException)
                {
                    throw;
                }
                await Dispatcher.DispatchAsync(async () =>
                {
                    await DisplayAlertAsync(Localized._Warn, Localized.HomePage_GoDraft_FailByException(ex4), Localized._OK);
                });
            }
        });
        await Dispatcher.DispatchAsync(() =>
        {
            try
            {
                cancelButton.IsVisible = false;
            }
            catch { }
        });
        initTimer.Stop();
        LogDiagnostic($"Initialize project {project?.ProjectName} cost {initTimer.ElapsedMilliseconds} ms.");
        if (initTimer.Elapsed.TotalSeconds < 2) await Task.Delay(2000 - (int)initTimer.Elapsed.TotalMilliseconds);
        Content = origContent;

        if (!cancelled && page != null && project != null)
        {
#if WINDOWS
            TryEnableCrashAutoRestart(draftSourcePath);
#endif
            await Dispatcher.DispatchAsync(async () =>
            {

                try
                {
                    try
                    {
                        App.Current?.Windows?[0]?.Title = $"{Localized.AppBrand} - {project.ProjectName}";
                        AppShell.instance.HideNavView();
                        Shell.SetTabBarIsVisible(page, false);
                        Shell.SetNavBarIsVisible(page, true);
                        lastPage = page;
                        await Navigation.PushAsync(page);
                    }
                    catch (Exception ex)
                    {
                        await DisplayAlertAsync(Localized._Warn, Localized.HomePage_GoDraft_FailByException(ex), "OK");
                    }

                }
                catch (Exception ex)
                {
                    if (throwOnException)
                    {
                        throw;
                    }

                    await DisplayAlertAsync(Localized._Warn, Localized.HomePage_GoDraft_FailByException(ex), "OK");
                }
            });
        }
    }

#if WINDOWS
    private void TryEnableCrashAutoRestart(string draftSourcePath)
    {
        try
        {
            if (SettingsManager.IsBoolSettingTrue("General_NoRebootAfterCrash") || Debugger.IsAttached) return;
            if (string.IsNullOrWhiteSpace(draftSourcePath) || !Directory.Exists(draftSourcePath)) return;

            projectFrameCut.Helper.CrashHandler.BootHandler(Path.GetFullPath(draftSourcePath));
        }
        catch (Exception ex)
        {
            Log(ex, "Enable crash auto restart", this);
        }
    }
#endif

#if WINDOWS
    UserActivitySession _previousSession;
#endif

    DraftPage? lastPage = null;

    protected override async void OnAppearing()
    {
        base.OnAppearing();
        try
        {
            if (lastPage is not null && Window is not null)
            {
                Window?.SizeChanged -= lastPage.Window_SizeChanged;
            }
        }
        catch { }
#if WINDOWS
        await projectFrameCut.WinUI.App.BringToForeground();
        AppShell.instance.ShowNavView();
        try
        {
            if (!Helper.CrashHandler.Handler?.HasExited ?? false)
            {
                Helper.CrashHandler.Handler?.Kill();
            }
        }
        catch { }
#elif iDevices
        if (OperatingSystem.IsMacCatalyst())
        {
            AppShell_MacCatalyst.instance.ShowNavView();
        }
#endif


        await _viewModel.LoadDrafts(Path.Combine(MauiProgram.DataPath, "My Drafts"));
        if (_viewModel.LoadFailed)
        {
            await DisplayAlertAsync(Localized._Info, Localized.HomePage_DraftLoadFailed(), Localized._OK);

        }
    }

    private async Task ShowManyAlertsAsync()
    {
        if (SimpleLocalizer.IsFallbackMatched && !OperatingSystem.IsAndroid())
        {
            List<string> localeDispName = new();
            foreach (var item in ISimpleLocalizerBase.GetMapping().Select(k => k.Value._LocateDisplayName))
            {
                localeDispName.Add(item.Split('/').Last().Trim(' '));
            }
            if (localeDispName.Count > 1)
            {
                localeDispName[localeDispName.Count - 1] = $"and {localeDispName.Last()}";
            }

            await DisplayAlertAsync("Info", $"it seems like projectFrameCut doesn't support your system language ({CultureInfo.CurrentCulture.NativeName}) yet.\r\nwe support {localeDispName.Aggregate((a, b) => $"{a}, {b}")} yet.\r\nIf you'd like to contribute the localization, do it and make a pull request.", "OK");
            SimpleLocalizer.IsFallbackMatched = false;
        }

        //if (!SettingsManager.IsBoolSettingTrue("EULAagreed"))
        //{
        //    var agree = await DisplayAlertAsync(Localized._Info, Localized.HomePage_AgreeEULA(), Localized._OK, Localized.HomePage_AgreeEULA_Disagree);
        //    if (!agree) Environment.Exit(0);
        //    SettingsManager.WriteSetting("EULAagreed", true.ToString());
        //}

        if (SettingsManager.IsBoolSettingTrue("_SettingFailLoad"))
        {
            await DisplayAlertAsync(Localized._Warn, $"{Localized.HomePage_SettingInitFailWarn}\r\n({SettingsManager.GetSetting("_SettingFailLoadMsg", "unknown")})", Localized._OK);

        }

        if (!SettingsManager.IsBoolSettingTrue("AIGeneratedTranslatePromptReaded") && Localized._LocaleId_ != "zh-CN")
        {
            await DisplayAlertAsync(Localized._Info, Localized.HomePage_AIGeneratdTranslationPrompt, Localized._OK);
            SettingsManager.WriteSetting("AIGeneratedTranslatePromptReaded", "true");
        }

        if (AdminServices.IsRunningAsAdministrator())
        {
            await DisplayAlertAsync(Localized._Warn, Localized.HomePage_AdminWarn(), Localized._OK);
        }

        if (!string.IsNullOrWhiteSpace(MauiProgram.ffmpegFailMessage))
        {
            await DisplayAlertAsync(Localized._Warn, Localized.HomePage_FFmpegFailedLoadWarn(MauiProgram.ffmpegFailMessage), Localized._OK);
        }

        try
        {
            if (File.Exists(Path.Combine(FileSystem.AppDataDirectory, "OverrideUserDataPath.txt")) && !Directory.Exists(File.ReadAllText(Path.Combine(FileSystem.AppDataDirectory, "OverrideUserDataPath.txt"))))
            {
                await DisplayAlertAsync(Localized._Warn, Localized.HomePage_UserdataPathNotFoundWarn(File.ReadAllText(Path.Combine(FileSystem.AppDataDirectory, "OverrideUserDataPath.txt"))), Localized._OK);

            }
        }
        catch { }

        if (!SettingsManager.IsSettingExists("UserName") || string.IsNullOrWhiteSpace(SettingsManager.GetSetting("UserName", "")))
        {
            try
            {
                var rnd = new RandomNameGenerator(Localized.RandomNameGenerator_Adjectives.Replace("��", ",").Split(',').Select(c => c.TrimStart(' ').TrimEnd(' ').Trim()), Localized.RandomNameGenerator_Nouns.Replace("��", ",").Split(',').Select(c => c.TrimStart(' ').TrimEnd(' ').Trim()), (a, b) => Localized.RandomNameGenerator_Contacter(a, b));
                SettingsManager.WriteSetting("UserName", rnd.Generate());
            }
            catch
            {
                SettingsManager.WriteSetting("UserName", OperatingSystem.IsWindows() ? Environment.UserName : "default user");

            }
        }


        if (SettingsManager.IsBoolSettingTrue("render_SaveCheckpoint"))
        {
            Directory.CreateDirectory(Path.Combine(MauiProgram.DataPath, "RenderCheckpoint"));
            IPicture.DiagImagePath = Path.Combine(MauiProgram.DataPath, "RenderCheckpoint");
            PictureProcesser.SaveDiagResult = true;
            PictureProcesser.DiagResultPath = Path.Combine(MauiProgram.DataPath, "RenderCheckpoint");

        }
        else
        {
            IPicture.DiagImagePath = null;
            PictureProcesser.SaveDiagResult = false;
        }
        IPicture.AllowPixelModeDowngrade = !SettingsManager.IsBoolSettingTrue("render_DisallowPictureModeDowngrade");
        PictureProcesser.EnableLogProcessStack = !SettingsManager.IsSettingExists("diag_EnableProcessStack") || SettingsManager.IsBoolSettingTrue("diag_EnableProcessStack");
        PictureLifecycleTracker.Enabled = SettingsManager.IsBoolSettingTrue("diag_TraceIPictureObject");
        PictureLifecycleTracker.TrackCollection = SettingsManager.IsBoolSettingTrue("diag_TraceIPictureObject");
#if WINDOWS
        if (IContextMenuBuilder.Default is null) IContextMenuBuilder.Default = new WindowsContextMenuBuilder();
#endif
    }

    private async void MenuOpen_Clicked(object? sender, EventArgs e)
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
            Log(ex, "open from menu", this);
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
            var confirm2 = await DisplayPromptAsync(Localized._Warn, Localized.HomePage_ProjectContextMenu_Delete_Confirm2Input(pvm.Name), Localized.HomePage_ProjectContextMenu_Delete_Confirm3(pvm.Name), Localized._Cancel, "no");

            if (confirm2 != "yes") return;

            if (Directory.Exists(pvm._projectPath))
            {
                Directory.Delete(pvm._projectPath, true);
            }
            await _viewModel.LoadDrafts(Path.Combine(MauiProgram.DataPath, "My Drafts"));
            await DisplayAlertAsync(Localized._Info, Localized.HomePage_ProjectContextMenu_Delete_Deleted(pvm.Name), Localized._OK);

        }
        catch (Exception ex)
        {
            Log(ex, "delete project", this);
            await DisplayAlertAsync(Localized._Error, Localized.HomePage_ProjectContextMenu_Delete_Fail(pvm.Name, ex), Localized._OK);
        }
    }


    private async Task ExportToTemplate(ProjectsViewModel vmItem)
    {
        try
        {
            var page = new TemplateExtractPage(vmItem);
            await Navigation.PushAsync(page);
        }
        catch (Exception ex)
        {
            Log(ex, "export to template", this);
            await DisplayAlertAsync(Localized._Error, $"???????{ex.Message}", Localized._OK);
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

    private async Task ImportDraft(string path)
    {
        var origCont = Content;
        Content = new ActivityIndicator
        {
            IsRunning = true,
            WidthRequest = 200,
            HeightRequest = 200
        };
        try
        {
            var workingDir = Path.Combine(MauiProgram.DataPath, "My Drafts", Path.GetFileNameWithoutExtension(path));
            if (Directory.Exists(workingDir))
            {
                workingDir = Path.Combine(MauiProgram.DataPath, "My Drafts", $"Imported - {Path.GetFileNameWithoutExtension(path)}{Random.Shared.Next(1000, 9999)}");
            }
            Directory.CreateDirectory(workingDir);
            await Task.Run(() =>
            {
                ZipFile.ExtractToDirectory(path, workingDir, true);
            });
        }
        catch (Exception ex)
        {
            await DisplayAlertAsync(Localized._Error, $"{Localized.HomePage_Import_CannotImportFraft}\r\n({Localized._ExceptionTemplate(ex)})", Localized._OK);
        }
        Content = origCont;
        await _viewModel.LoadDrafts(Path.Combine(MauiProgram.DataPath, "My Drafts"));

    }


    private async Task GoRender(ProjectsViewModel vmItem)
    {
        try
        {
            var project = JsonSerializer.Deserialize<ProjectJSONStructure>(File.ReadAllText(Path.Combine(vmItem._projectPath, "project.pjfc")), DraftPage.DraftJSONOption);
            var tml = JsonSerializer.Deserialize<DraftStructureJSON>(File.ReadAllText(Path.Combine(vmItem._projectPath, "timeline.json")), DraftPage.DraftJSONOption);
            if (tml is null || project is null)
            {
                await DisplayAlertAsync(Localized._Warn, $"{Localized.HomePage_GoDraft_DraftBroken_InvaildInfo}", Localized._OK);
                return;
            }
            (var dict, var trackCount) = DraftImportAndExportHelper.ImportFromJSON(tml, project);
            var draftPage = new DraftPage(project ?? new ProjectJSONStructure(), dict, new(), trackCount, vmItem._projectPath, project?.ProjectName ?? "?", false);
            var draft = DraftImportAndExportHelper.ExportFromDraftPage(draftPage, true, false);
            var renderPage = new RenderPage(vmItem._projectPath, tml.Duration, project, draft);
            await Dispatcher.DispatchAsync(async () =>
            {
                Shell.SetTabBarIsVisible(renderPage, false);
                Shell.SetNavBarIsVisible(renderPage, true);
#if WINDOWS
                AppShell.instance.HideNavView();
#endif
                await Navigation.PushAsync(renderPage);
            });

        }
        catch (Exception ex)
        {
            Log(ex, "get project info", this);
            await DisplayAlertAsync(Localized._Warn, $"{Localized.HomePage_GoDraft_DraftBroken_InvaildInfo}\r\n({ex.Message})", Localized._OK);
            return;
        }

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
        if (Path.GetInvalidPathChars().Any(projName.Contains) || Path.GetInvalidFileNameChars().Any(projName.Contains))
        {
            await DisplayAlertAsync(Localized._Error, Localized.HomePage_CreateAProject_InvalidName, Localized._OK);
            return;
        }
        Directory.Move(vmItem._projectPath, newPath);
        var projInfoPath = Path.Combine(newPath, "project.pjfc");
        if (!File.Exists(projInfoPath)) projInfoPath = Path.Combine(newPath, "project.json");
        var info = JsonSerializer.Deserialize<ProjectJSONStructure>(File.ReadAllText(projInfoPath), DraftPage.DraftJSONOption);
        if (info is not null)
        {
            info.ProjectName = projName;
            File.WriteAllText(
                Path.Combine(newPath, "project.pjfc"),
                JsonSerializer.Serialize(info));
        }

        await _viewModel.LoadDrafts(Path.Combine(MauiProgram.DataPath, "My Drafts"));
    }

    public async Task ManageProject(ProjectsViewModel vmItem)
    {
        var draftSourcePath = vmItem._projectPath;
        var settingPage = new DraftSettingPage(vmItem._projectPath);
        var page = new ContentPage { Content = settingPage.tabView };
        await Navigation.PushAsync(page);
    }


    private async void ItemBorder_Loaded(object? sender, EventArgs e)
    {
        if (sender is Microsoft.Maui.Controls.Border border && border.BindingContext is ProjectsViewModel vmItem)
        {
            foreach (var gr in border.GestureRecognizers.ToList())
            {
                if (gr is TapGestureRecognizer || gr is PointerGestureRecognizer)
                {
                    border.GestureRecognizers.Remove(gr);
                }
            }

            UIServices.RegisterSelectOrContextMenu(
                border,
                OnSelected: () =>
                {
                    _lastSelectedItemName = vmItem.Name;
                    ProjectsCollection.SelectedItem = vmItem;
                },
                OnClicked: async () =>
                {
                    if (vmItem._name == CreateButtonName)
                    {
                        await CreateDraft();
                    }
                    else
                    {
                        await GoDraft(vmItem);
                    }

                    ProjectsCollection.SelectedItem = null;
                    _lastSelectedItemName = string.Empty;
                },
                OnContextMenuClick: async () =>
                {
                    if (OperatingSystem.IsAndroid() || OperatingSystem.IsIOS())
                    {
                        Vibration.Vibrate(120);
                    }
                    await ShowContextMenu(vmItem);
                }
            );

#if WINDOWS
            if (border.Handler?.PlatformView is Microsoft.UI.Xaml.FrameworkElement fe)
            {
                fe.IsTabStop = true;
                fe.GotFocus -= ItemBorder_GotFocus;
                fe.GotFocus += ItemBorder_GotFocus;
                fe.KeyDown -= ItemBorder_KeyDown;
                fe.KeyDown += ItemBorder_KeyDown;
            }
#endif
            if (vmItem._name != CreateButtonName)
            {
                SemanticProperties.SetHint(border, DeviceInfo.Idiom == DeviceIdiom.Desktop ? Localized.HomePage_OpenHint_DoubleClick : Localized.HomePage_OpenHint_Tap);
            }

            SemanticProperties.SetDescription(border, $"{vmItem.Name}, {vmItem.LastChangedDisplay} {(DeviceInfo.Idiom == DeviceIdiom.Desktop ? Localized.HomePage_OpenHint_DoubleClick : Localized.HomePage_OpenHint_Tap)}");

        }
    }

#if WINDOWS
    private void ItemBorder_GotFocus(object sender, Microsoft.UI.Xaml.RoutedEventArgs e)
    {
        if (sender is Microsoft.UI.Xaml.FrameworkElement fe && fe.DataContext is ProjectsViewModel vmItem)
        {
            _lastSelectedItemName = vmItem.Name;
            ProjectsCollection.SelectedItem = vmItem;
        }
    }

    private async void ItemBorder_KeyDown(object sender, Microsoft.UI.Xaml.Input.KeyRoutedEventArgs e)
    {
        if (sender is not Microsoft.UI.Xaml.FrameworkElement fe || fe.DataContext is not ProjectsViewModel vmItem)
        {
            return;
        }

        if (e.Key == Windows.System.VirtualKey.Enter || e.Key == Windows.System.VirtualKey.Space)
        {
            e.Handled = true;
            if (vmItem._name == CreateButtonName)
            {
                await CreateDraft();
            }
            else
            {
                await GoDraft(vmItem);
            }

            ProjectsCollection.SelectedItem = null;
            _lastSelectedItemName = string.Empty;
        }
    }
#endif

    private async Task ShowContextMenu(ProjectsViewModel vmItem)
    {
        if (vmItem._name == CreateButtonName)
        {
            if (DateTime.Now.Month == 4 && DateTime.Now.Day == 1) EasterEgg();

            return;
        }

        var verbs = new List<string>
        {
            Localized.HomePage_ProjectContextMenu_Open,
            Localized.HomePage_ProjectContextMenu_OpenReadonly,
            Localized.DraftPage_GoRender,
            Localized.HomePage_ProjectContextMenu_ToTemplate,
            Localized.HomePage_ProjectContextMenu_Export,
            Localized.HomePage_ProjectContextMenu_OpenInFileManager,
            Localized.DraftPage_MenuBar_Project_Option,
            Localized.HomePage_ProjectContextMenu_Clone,
            Localized.HomePage_ProjectContextMenu_Rename,
            Localized.HomePage_ProjectContextMenu_Delete
        };


        if (SettingsManager.IsBoolSettingTrue("DeveloperMode"))
        {
            verbs.Add("Debug: throw the exceptions while opening");
            verbs.Add("Debug: use mobile layout");
        }

        var action = await DisplayActionSheetAsync(vmItem.Name, Localized._Cancel, null, verbs.ToArray());

        if (SettingsManager.IsBoolSettingTrue("DeveloperMode"))
        {
            switch (action)
            {
                case "Debug: throw the exceptions while opening":
                    {
                        await GoDraft(vmItem, throwOnException: true);
                        return;
                    }
                case "Debug: use mobile layout":
                    {
                        await GoDraft(vmItem._projectPath, vmItem.Name, false, false, true, true);
                        return;
                    }

                default:
                    {
                        break;
                    }
            }
        }
        await Dispatcher.DispatchAsync(async () =>
        {
            switch (verbs.IndexOf(action))
            {
                case 0: //Open
                    await GoDraft(vmItem);
                    break;
                case 1: //OpenReadonly 
                    await GoDraft(vmItem, isReadonly: true);
                    break;
                case 2: //Render
                    await GoRender(vmItem);
                    break;
                case 3: //ToTemplate
                    await ExportToTemplate(vmItem);
                    break;
                case 4: //Export
                    await ExportProject(vmItem);
                    break;
                case 5: //OpenInFileManager
                    await FileSystemService.OpenFolderAsync(vmItem._projectPath);
                    break;
                case 6: //ManageProject
                    await ManageProject(vmItem);
                    break;
                case 7: //Clone
                    await CloneDraft(vmItem);
                    break;
                case 8: //Rename
                    await RenameProject(vmItem);
                    break;
                case 9: //Delete
                    await DeleteProject(vmItem);
                    break;
                default: //unknown/cancel
                    if (!string.IsNullOrWhiteSpace(action))
                    {
                        Log($"Action {action} doesn't matched on any case.", "warn");
                    }
                    break;
            }
        });
    }


    private async void DropGestureRecognizer_Drop(object sender, DropEventArgs e)
    {
        foreach (var item in await FileDropHelper.GetFilePathsFromDrop(e))
        {
            if (Path.GetExtension(item) == ".pjfc")
                await ImportDraft(item);
        }
    }

    private async void ImportButton_Clicked(object sender, EventArgs e)
    {
        var path = await FileSystemService.PickFileAsync();
        if (!string.IsNullOrWhiteSpace(path)) await ImportDraft(path);
    }

    private async Task<bool> CheckProjectVersionCompatibility(ProjectJSONStructure project)
    {
        try
        {
            var currentAppVersion = Assembly.GetExecutingAssembly()?.GetName()?.Version;
            var currentAPIVersion = IPluginBase.CurrentPluginAPIVersion;
            var plugins = PluginManager.LoadedPlugins.Select(p => p.Key);
            if (project.LastOpenAPIBaseVersion != 0)
            {
                if (project.LastOpenAPIBaseVersion > currentAPIVersion)
                {
                    await DisplayAlertAsync(
                            Localized._Error,
                            Localized.HomePage_IncompatibleVersionError($"API v{project.LastOpenAPIBaseVersion}", $"API v{currentAPIVersion}", project?.ProjectName ?? "Project"),
                            Localized._OK
                            );
                    return false;
                }
                else if (project.LastOpenAPIBaseVersion < currentAPIVersion)
                {
                    return await DisplayAlertAsync(
                            Localized._Error,
                            Localized.HomePage_VersionTooOld($"API v{project.LastOpenAPIBaseVersion}", $"API v{currentAPIVersion}", project?.ProjectName ?? "Project"),
                            Localized._OK,
                            Localized._Cancel
                            );
                }
            }

            if (!string.IsNullOrEmpty(project?.LastOpenAppVersion) &&
                currentAppVersion != null &&
                Version.TryParse(project.LastOpenAppVersion, out var projectVersion))
            {
                var projVerStr = projectVersion.ToString();
                if (projVerStr == "0.0.0.0") projVerStr = "Unknown";
                if (projectVersion > currentAppVersion)
                {
                    return await DisplayAlertAsync(
                        Localized._Warn,
                        Localized.HomePage_VersionTooNew(projVerStr, currentAppVersion.ToString(), project?.ProjectName ?? "Project"),
                        Localized._OK,
                        Localized._Cancel
                    );
                }
                else if (projectVersion < currentAppVersion)
                {
                    return await DisplayAlertAsync(
                         Localized._Info,
                         Localized.HomePage_VersionTooOld(projVerStr, currentAppVersion.ToString(), project?.ProjectName ?? "Project"),
                         Localized._OK,
                         Localized._Cancel);
                }
            }

            if (project?.PluginUsed != null && project.PluginUsed.Any((c) => !string.IsNullOrWhiteSpace(c)))
            {

                var unsupportedPlugins = project.PluginUsed.Where(p => !plugins.Contains(p)).ToList();
                if (unsupportedPlugins.Any())
                {
                    return await DisplayAlertAsync(
                        Localized._Warn,
                        Localized.HomePage_MissingPlugin(project?.ProjectName ?? "Project", string.Join(", ", unsupportedPlugins)),
                        Localized._OK,
                        Localized._Cancel);
                }

            }

            return true;
        }
        catch (Exception ex)
        {
            Log(ex, "version compatibility check", this);
            return true;
        }
    }

    private void ContentPage_Appearing(object sender, EventArgs e)
    {
        //Dispatcher.DispatchAsync(async () =>
        //{
        //    await Task.Delay(5000);
        //    Window?.Width = Window.Width - 8; //avoid the contents go inside navigation bar
        //    Thread.Sleep(50);
        //    Window?.Width = Window.Width + 8;
        //});

    }

    public void EasterEgg()
    {
        try
        {
            Preferences.Set("LastAprilFoolsDayEasterEggTriggerYear", DateTime.Now.Year.ToString());

            var projName = "Untitled";

            var draftSourcePath = Path.Combine(MauiProgram.DataPath, "My Drafts", $"apfd-{DateTime.Now.Year}.pjfc");


            Directory.CreateDirectory(draftSourcePath);

            var ProjectInfo = new ProjectJSONStructure
            {
                ProjectName = projName,
                NormallyExited = true,
                LastChanged = DateTime.Now,
                LastOpenAPIBaseVersion = IPluginBase.CurrentPluginAPIVersion,
                LastOpenAppVersion = Assembly.GetExecutingAssembly().GetName().Version?.ToString() ?? "Unknown",
                PluginUsed = []
            };

            Dictionary<string, TextClipEntry> template = new();
            EditSettingPage.LoadTextTemplates(ref template);

            DraftPage _draftPage = new();
            var element = _draftPage.CreateAndAddClip(
                startX: 0,
                width: _draftPage.FrameToPixel(300),
                trackIndex: 0,
                id: null,
                labelText: "???",
                background: new Microsoft.Maui.Controls.SolidColorBrush(Colors.MediumPurple),
                resolveOverlap: true,
                relativeStart: 0,
                maxFrames: 0
            );

            element.ClipType = ClipMode.TextClip;
            element.FromPlugin = "projectFrameCut.Render.Plugins.InternalPluginBase";
            element.isInfiniteLength = true;
            element.maxFrameCount = 0;
            element.ExtraData = new();
            element.ExtraData["TextEntries"] = new List<TextClipEntry>
            {
                template.First().Value with { text = $"Happy April fools day!" }
            };

            File.WriteAllText(
                Path.Combine(draftSourcePath, "timeline.json"),
                JsonSerializer.Serialize(new DraftStructureJSON
                {
                    Clips = new List<ClipDraftDTO>
                    {
                        DraftImportAndExportHelper.ExportClipElementFromDraftPage(_draftPage, element, false)
                    }.Cast<object>().ToArray(),
                }));
            File.WriteAllText(
                Path.Combine(draftSourcePath, "assets.json"),
                JsonSerializer.Serialize(Array.Empty<AssetItem>()));
            File.WriteAllText(
                Path.Combine(draftSourcePath, "project.pjfc"),
                JsonSerializer.Serialize(ProjectInfo));

        }
        catch (Exception ex)
        {
            return;
        }


    }
}

public class ProjectsListViewModel
{
    public ObservableCollection<ProjectsViewModel> Projects { get; } = new();

    public bool LoadFailed = false;

    public ProjectsListViewModel()
    {
        //LoadSample();

    }

    public async Task LoadDrafts(string sourcePath)
    {
        List<ProjectsViewModel> projects = new();
        List<ProjectsViewModel> failedProjects = new();
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
                ProjectJSONStructure? proj = null;
                var projFile = Path.Combine(item, (File.Exists(Path.Combine(item, "project.pjfc")) ? "project.pjfc" : "project.json"));
                if (!File.Exists(projFile)) goto fail;
                try
                {
                    proj = JsonSerializer.Deserialize<ProjectJSONStructure>(File.ReadAllText(projFile));
                    if (proj is not null)
                    {
                        var thumb = Path.Combine(item, "thumbs", "_project.png");
                        projects.Add(new ProjectsViewModel(proj.ProjectName, proj.LastChanged ?? DateTime.MinValue, thumb)
                        {
                            _projectPath = item
                        });
                    }
                    else goto fail;
                    continue;
                }
                catch (Exception exInner)
                {
                    if (MyLoggerExtensions.LoggingDiagnosticInfo) Log(exInner, "load draft", this);
                    goto fail;
                }

            fail:
                failedProjects.Add(new ProjectsViewModel(proj?.ProjectName ?? "Unknown project", null, "")
                {
                    _projectPath = item
                });
                continue;



            }
        }
        catch (Exception ex)
        {
            if (MyLoggerExtensions.LoggingDiagnosticInfo) Log(ex, "load draft", this);
        }
        finally
        {
            try
            {
                foreach (var item in projects.OrderByDescending(x => x._lastChanged))
                {
                    Projects.Insert(Projects.Count - 1, item);
                }
                // Insert failed (invalid) projects after valid ones, so they appear closer to the bottom
                foreach (var f in failedProjects)
                {
                    Projects.Insert(Projects.Count - 1, f);
                }
            }
            catch (Exception ex)
            {
                Log(ex, "render draft list", this);
                LoadFailed = true;
            }
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