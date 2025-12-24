using LocalizedResources;
using Microsoft.Maui.Graphics;
using projectFrameCut.DraftStuff;
using projectFrameCut.PropertyPanel;
using projectFrameCut.Render.Plugin;
using projectFrameCut.Render.RenderAPIBase.Project;
using projectFrameCut.Services;
using projectFrameCut.Setting.SettingManager;
using projectFrameCut.Shared;
using projectFrameCut.ViewModels;
using System.Collections.Concurrent;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO.Compression;
using System.Text.Json;
using System.Threading.Tasks;
using System.Windows.Input;






#if WINDOWS
using projectFrameCut.Platforms.Windows;
using Windows.ApplicationModel.UserActivities;
using ILGPU;

#endif

namespace projectFrameCut;

public partial class HomePage : ContentPage
{
    private readonly ProjectsListViewModel _viewModel;

    private const string CreateButtonName = "!!CreateButton!!";

    private string _lastSelectedItemName = string.Empty;

    private static bool HasAlreadyLaunchedFromFile = false;


    public HomePage()
    {
        InitializeComponent();
        WelcomeLabel.Text = Localized.HomePage_Welcome();
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
            await ShowManyAlertsAsync();

#endif
#if !ANDROID
            ProjectsCollection.SelectionChanged += CollectionView_SelectionChanged;
#endif
            if (HasAlreadyLaunchedFromFile) return;
            HasAlreadyLaunchedFromFile = true;
            try
            {
                if (MauiProgram.CmdlineArgs.Length > 0)
                {
                    var args = MauiProgram.CmdlineArgs.Skip(1).ToArray();
                    if (args.Length >= 2)
                    {
                        switch (args[0])
                        {
#if WINDOWS && DEBUG
                            case "go_draft_dbgBackend":
                                {
                                    var draft = args[1];
                                    if (Directory.Exists(draft))
                                    {
                                        RpcClient c = new();
                                        if (Process.GetProcessesByName("projectFrameCut.Render.WindowsRender").Any())
                                        {
                                            await Task.Delay(1200);
                                            await PluginPipeTransport.SendEnabledPluginsAsync("pjfc_plugin_debug123");
                                            await c.StartAsync("pjfc_rpc_debug123", default);
                                            await GoDraft(draft, "Project", false, false, c, true);
                                        }

                                    }
                                    break;
                                }
#endif
                            case "goDraft":
                                {
                                    var draft = args[1];
                                    if (Directory.Exists(draft))
                                    {
                                        await GoDraft(draft, (Path.GetDirectoryName(draft) ?? "Project").Split('.')?.FirstOrDefault("Project")!, false, false);
                                    }
                                    break;
                                }
                            case "installPlugin":
                                {
                                    var path = args[1];
                                    if (File.Exists(path))
                                    {
                                        try
                                        {
                                            await PluginService.AddAPlugin(path, this);
                                        }
                                        catch (Exception ex)
                                        {
                                            await DisplayAlertAsync(Localized._Error, $"{Localized.HomePage_Import_CannotAddPlugin}\r\n({Localized._ExceptionTemplate(ex)})", Localized._OK);
                                        }
                                    }
                                    break;
                                }
                            case "importDraft":
                                {
                                    var path = args[1];
                                    if (File.Exists(path))
                                    {
                                        await ImportDraft(path);
                                    }
                                    break;
                                }
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                Log(ex, "Launch from file", this);
                await DisplayAlertAsync(Localized._Error, "Cannot launch from file. Try again later.", Localized._OK);
            }

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

#if WINDOWS
            try
            {
                if (SettingsManager.GetSetting("ui_defaultTheme", "default") != "default")
                {
                    MainThread.InvokeOnMainThreadAsync(() =>
                    {
                        WinUI.App.Instance?.RequestedTheme = SettingsManager.GetSetting("ui_defaultTheme", "default") switch
                        {
                            "dark" => Microsoft.UI.Xaml.ApplicationTheme.Light,
                            _ => Microsoft.UI.Xaml.ApplicationTheme.Light,
                        };
                    }).GetAwaiter().GetResult();
                }

            }
            catch { }
#endif
        };


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

        await _viewModel.LoadDrafts(Path.Combine(MauiProgram.DataPath, "My Drafts"));


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

    private async Task GoDraft(string draftSourcePath, string title, bool isReadonly = false, bool throwOnException = false, object? dbgBackend = null, bool? skipAskForRecover = null)
    {
        LogDiagnostic($"Loading draft {draftSourcePath}, {title}, \r\n{Environment.StackTrace}");
        bool cancelled = false;
        if (!Directory.Exists(draftSourcePath))
        {
            await DisplayAlertAsync(Localized._Warn, "Draft not found.", Localized._OK);
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
                    Margin = new Microsoft.Maui.Thickness(0.0, 10.0, 0.0, 0.0)
                },
                cancelButton
            }
        };
        ProjectJSONStructure project = new();
        try
        {
            project = JsonSerializer.Deserialize<ProjectJSONStructure>(File.ReadAllText(Path.Combine(draftSourcePath, "project.json")));

        }
        catch (Exception ex)
        {
            Log(ex, "get project info", this);
            await DisplayAlertAsync(Localized._Warn, $"{Localized.HomePage_GoDraft_DraftBroken_InvaildInfo}\r\n({ex.Message})", Localized._OK);
            Content = origContent;
            return;
        }

        await Task.Run(async () =>
        {
            try
            {
                if (!Directory.Exists(draftSourcePath))
                {
                    throw new DirectoryNotFoundException("Working path not found: " + draftSourcePath);
                }
                string[] filesShouldExist = ["project.json", "timeline.json", "assets.json"];
                if (filesShouldExist.Any((f) => !File.Exists(Path.Combine(draftSourcePath, f))))
                {
                    await Dispatcher.DispatchAsync(async () =>
                    {
                        await DisplayAlertAsync(Localized._Warn, $"{Localized.HomePage_GoDraft_DraftBroken_InvaildInfo}\r\n(These files are missing:{string.Join(", ", filesShouldExist.Where(f => !File.Exists(Path.Combine(draftSourcePath, f))))})", Localized._OK);
                    });
                    return;
                }
                List<AssetItem> assets;
                DraftStructureJSON timeline;
                try
                {
                    assets = JsonSerializer.Deserialize<List<AssetItem>>(File.ReadAllText(Path.Combine(draftSourcePath, "assets.json"))) ?? new();
                    timeline = JsonSerializer.Deserialize<DraftStructureJSON>(File.ReadAllText(Path.Combine(draftSourcePath, "timeline.json"))) ?? new();
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
                if (!SettingsManager.IsSettingExists("Edit_PreferredPopupMode"))
                {
#if WINDOWS || MACCATALYST
                    SettingsManager.WriteSetting("Edit_PreferredPopupMode", "right");
#else
                    SettingsManager.WriteSetting("Edit_PreferredPopupMode", "bottom");
#endif
                }
                page = new DraftPage(project ?? new ProjectJSONStructure(), dict, assetDict, trackCount, draftSourcePath, project?.projectName ?? "?", isReadonly, dbgBackend);
                page.ProjectName = project?.projectName ?? "?";
                page.UseLivePreviewInsteadOfBackend = SettingsManager.IsBoolSettingTrue("edit_UseLivePreviewInsteadOfBackend");
                page.IsReadonly = isReadonly;
                page.PreferredPopupMode = SettingsManager.GetSetting("Edit_PreferredPopupMode", "right");
                page.MaximumSaveSlot = int.TryParse(SettingsManager.GetSetting("Edit_MaximumSaveSlot"), out var slotCount) ? slotCount : 10;
                page.AlwaysShowToolbarBtns = SettingsManager.IsBoolSettingTrue("Edit_AlwaysShowToolbarButtons");
                page.ShowBackendConsole = SettingsManager.IsBoolSettingTrue("render_ShowBackendConsole");
                page.LiveVideoPreviewBufferLength = int.TryParse(SettingsManager.GetSetting("edit_LiveVideoPreviewBufferLength", "240"), out var bufferLen) ? bufferLen : 240;
#if WINDOWS
                if (!page.UseLivePreviewInsteadOfBackend)
                {
                    await page.BootRPC();
                }
                else
                {
                    Context context = Context.CreateDefault();
                    var devices = context.Devices.ToList();
                    var accelDevice = devices.Index().Select(t => new KeyValuePair<int, ILGPU.Runtime.Device>(t.Index,t.Item))
                                            .FirstOrDefault((t) => t.Key == (int.TryParse(SettingsManager.GetSetting("accel_DeviceId", "-1"), out var accelIdx) ? accelIdx : -1),
                                            new KeyValuePair<int, ILGPU.Runtime.Device>(-1, devices.FirstOrDefault(c => c.AcceleratorType != ILGPU.Runtime.AcceleratorType.CPU, devices.First()))).Value;
                    page.AcceleratorToUse = accelDevice.CreateAccelerator(context);
                }
#endif
                await page.PostInit();

            }
            catch (Exception ex4)
            {
                if (throwOnException)
                {
                    throw;
                }
                await Dispatcher.DispatchAsync(async () =>
                {
                    await DisplayAlertAsync(Localized._Warn, Localized.HomePage_GoDraft_FailByException(ex4), "OK");
                });
            }
        });
        try
        {
            Content = origContent;

            if (!cancelled && page != null && project != null)
            {
#if WINDOWS
            AppShell.instance.HideNavView();
#endif
                foreach (var item in PluginManager.LoadedPlugins)
                {
                    try
                    {
                        project = item.Value.OnProjectLoad(project) ?? project;
                    }
                    catch (Exception ex)
                    {
                        Log(ex, $"plugin {item.Value.Name} OnProjectLoad", this);
                    }
                }

#if WINDOWS //for recall/timeline
            try
            {
                await Dispatcher.DispatchAsync(async () =>
                {
                    try
                    {
                        var platformPage = this.Handler?.PlatformView as Microsoft.UI.Xaml.Controls.Page;
                        await platformPage?.Dispatcher.RunAsync(default, async () =>
                        {
                            _previousSession?.Dispose();
                            var activity = await UserActivityChannel.GetDefault().GetOrCreateUserActivityAsync($"projectFrameCut_draft_{project?.projectName ?? "Project"}");
                            activity.ActivationUri = new Uri($"projectFrameCut://draft/{draftSourcePath.Replace('\\', '/')}");
                            activity.VisualElements.DisplayText = $"projectFrameCut draft-'{project?.projectName ?? "Project"}'";
                            await activity.SaveAsync();
                            _previousSession = activity.CreateSession();
                        });

                    }
                    catch (Exception ex)
                    {

                    }
                });

            }
            catch (Exception ex)
            {

            }

#endif
                await Dispatcher.DispatchAsync(async () =>
                {
                    Shell.SetTabBarIsVisible(page, false);
                    Shell.SetNavBarIsVisible(page, true);
                    await Navigation.PushAsync(page);
                });
            }
        }
        catch (Exception ex)
        {
            if (throwOnException)
            {
                throw;
            }
            await Dispatcher.DispatchAsync(async () =>
            {
                await DisplayAlertAsync(Localized._Warn, Localized.HomePage_GoDraft_FailByException(ex), "OK");
            });
        }


    }

#if WINDOWS
    UserActivitySession _previousSession;
#endif

    protected override async void OnAppearing()
    {
        base.OnAppearing();
#if WINDOWS
        await projectFrameCut.WinUI.App.BringToForeground();
#endif

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

        await _viewModel.LoadDrafts(Path.Combine(MauiProgram.DataPath, "My Drafts"));
        if (_viewModel.LoadFailed)
        {
            await DisplayAlertAsync(Localized._Info, Localized.HomePage_DraftLoadFailed(), Localized._OK);

        }
    }

    private async Task ShowManyAlertsAsync()
    {
        if (SimpleLocalizer.IsFallbackMatched)
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

            await DisplayAlertAsync("Info", $"it seems like projectFrameCut doesn't support your system language yet.\r\nwe support {localeDispName.Aggregate((a, b) => $"{a}, {b}")} yet.\r\nIf you'd like to contribute the localization, do it and make a pull request.", "OK");
            SimpleLocalizer.IsFallbackMatched = false;
        }

        if (!SettingsManager.IsBoolSettingTrue("AIGeneratedTranslatePromptReaded") && Localized._LocaleId_ != "zh-CN")
        {
            await DisplayAlertAsync(Localized._Info, Localized.HomePage_AIGeneratdTranslationPrompt, Localized._OK);
            SettingsManager.WriteSetting("AIGeneratedTranslatePromptReaded", "true");
        }

        if (AdminHelper.IsRunningAsAdministrator())
        {
            await DisplayAlertAsync(Localized._Warn, Localized.HomePage_AdminWarn(), Localized._OK);
        }
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
            await _viewModel.LoadDrafts(Path.Combine(MauiProgram.DataPath, "My Drafts"));
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

        await _viewModel.LoadDrafts(Path.Combine(MauiProgram.DataPath, "My Drafts"));
    }

    private async void ItemBorder_Loaded(object? sender, EventArgs e)
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
                if (duration >= 500)
                {
                    Vibration.Vibrate(120);
                    await ShowContextMenu(vmItem);
                }
                else if (duration > 0)
                {
                    // Short tap to open
                    if (vmItem._name == CreateButtonName)
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
        if (vmItem._name == CreateButtonName)
        {
            return;
        }

        var verbs = new List<string>
        {
            Localized.HomePage_ProjectContextMenu_Open,
            Localized.HomePage_ProjectContextMenu_OpenReadonly,
            Localized.HomePage_ProjectContextMenu_Export,
            Localized.HomePage_ProjectContextMenu_OpenInFileManager,
            Localized.HomePage_ProjectContextMenu_Clone,
            Localized.HomePage_ProjectContextMenu_Rename,
            Localized.HomePage_ProjectContextMenu_Delete
        };


        if (SettingsManager.IsBoolSettingTrue("DeveloperMode"))
        {
            verbs.Add("Debug: throw the exceptions while opening");
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
                    if (!string.IsNullOrWhiteSpace(action))
                    {
                        Log($"Action {action} doesn't matched on any case.", "warn");
                    }
                    break;
            }
        });
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
                var projFile = Path.Combine(item, "project.json");
                if (!File.Exists(projFile)) goto fail;
                try
                {
                    proj = JsonSerializer.Deserialize<ProjectJSONStructure>(File.ReadAllText(projFile));
                    if (proj is not null)
                    {
                        var thumb = Path.Combine(item, "thumbs", "_project.png");
                        projects.Add(new ProjectsViewModel(proj.projectName, proj.LastChanged ?? DateTime.MinValue, thumb)
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
                failedProjects.Add(new ProjectsViewModel(proj?.projectName ?? "Unknown project", null, "")
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