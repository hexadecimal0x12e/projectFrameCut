using LocalizedResources;
using Microsoft.Maui.Dispatching;
using Microsoft.Maui.Graphics;
using Microsoft.Maui.Platform;
using projectFrameCut.ApplicationAPIBase.Helpers;
using projectFrameCut.ApplicationAPIBase.Plugins;
using projectFrameCut.ApplicationAPIBase.Views.PropertyPanelBuilders;
using projectFrameCut.Asset;
using projectFrameCut.DraftStuff;
using projectFrameCut.InteractableEditor;
using projectFrameCut.IntegratedAPIServer;
using projectFrameCut.Render.EncodeAndDecode;
using projectFrameCut.Render.Plugin;
using projectFrameCut.Render.RenderAPIBase.Plugins;
using projectFrameCut.Render.RenderAPIBase.Project;
using projectFrameCut.Services;
using projectFrameCut.Setting.SettingManager;
using projectFrameCut.Setting.SettingPages;
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
using Application = Microsoft.Maui.Controls.Application;
using IPicture = projectFrameCut.Drawing.Base.IPicture;
using projectFrameCut.Render.RenderAPIBase.Sources;
using projectFrameCut.Render.Compose;
using projectFrameCut.Render.Contracts;
using projectFrameCut.Render.RPCProtocol;
using projectFrameCut.Drawing.Base;
using projectFrameCut.Render.RenderAPIBase.ClipAndTrack;
using projectFrameCut.Drawing.Vector.ImportExport;
using projectFrameCut.Drawing.Text.Typology;
using projectFrameCut.Render.ClipsAndTracks;
using projectFrameCut.Render.Effect;
using Microsoft.Win32;
using System.Runtime;

#if WINDOWS
using projectFrameCut.Platforms.Windows;
using Windows.ApplicationModel.UserActivities;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml.Media;
using winui = Microsoft.UI.Xaml.Controls;
using WinRT.Interop;
#endif

namespace projectFrameCut;

public partial class HomePage : ContentPage
{
    private readonly ProjectsListViewModel _viewModel;
    private string? _pendingIntegratedMcpAddress;
    private IntegratedApiServer? _integratedApiServer;
    private IntegratedApiBackend? _integratedApiBackend;
    private CancellationTokenSource? _mcpClientModeCancellation;
    private IRenderClient? _mcpClientModeClient;
    private Task? _mcpClientModeMonitor;
    private DraftPage? _mcpClientModePage;
    private Guid _mcpClientModeSessionId;
    private long _mcpClientModeRevision;
    private string _mcpClientModeSnapshotHash = string.Empty;
    private long _mcpClientModeDeclinedRevision = -1;
    private string _mcpClientModeDeclinedSnapshotHash = string.Empty;
#if WINDOWS
    private Microsoft.UI.Xaml.Window? _mcpClientNativeWindow;
    private AppWindow? _mcpClientAppWindow;
    private bool _mcpClientCloseConfirmed;
    private bool _mcpClientClosePromptOpen;
#endif

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
            if (string.Equals(
                GetCommandLineOption(MauiProgram.CmdlineArgs, "--mcpMode"),
                "client",
                StringComparison.OrdinalIgnoreCase))
            {
                if (!HasAlreadyLaunchedFromFile) await LaunchFromFile();
                HasAlreadyLaunchedFromFile = true;
                return;
            }

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
            try
            {
                if (!HasAlreadyLaunchedFromFile) await LaunchFromFile();
                HasAlreadyLaunchedFromFile = true;
            }
            catch (Exception ex)
            {
                Log(ex, "post-dialogue init", this);
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

    public HomePage(string path, bool skipAskForRecover = false)
    {
        InitializeComponent();
        WelcomeLabel.Text = Localized.HomePage_Welcome();
        _viewModel = new ProjectsListViewModel();
        BindingContext = _viewModel;
        Loaded += async (s, e) =>
        {
            if (new FileInfo(path).OpenRead().ReadByte() == '{')
            {
                try
                {
                    var draft = JsonSerializer.Deserialize<ProjectJSONStructure>(File.ReadAllText(path), DraftPage.DraftJSONOption);
                    if (draft is ProjectJSONStructure && Path.GetDirectoryName(path) is string p)
                    {
                        await GoDraft(p, draft.ProjectName ?? "Project", isReadonly: false, throwOnException: false, skipAskForRecover: skipAskForRecover);
                        return;

                    }
                }
                catch
                {

                }

            }
        };


    }

    public static void HandleAppActionLaunch(AppAction appAction)
    {
        App.Current?.Dispatcher?.Dispatch(async () =>
        {
            if (Application.Current.Windows?[0]?.Page is HomePage p)
            {
                await p.LaunchFromFile([appAction.Id]);
            }
        });
    }

    public async Task LaunchFromFile(string[]? argsOverride = null)
    {
        HasAlreadyLaunchedFromFile = true;
        var origCont = Content;
        bool restoreContent = true;
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

            var args = argsOverride ?? MauiProgram.CmdlineArgs.ToArray();
            bool continueRequested = args.Any(c => c.Equals("--continue", StringComparison.OrdinalIgnoreCase));
            bool openRenderPage = args.Any(c => c.Equals("--render", StringComparison.OrdinalIgnoreCase));
            string? mcpMode = GetCommandLineOption(args, "--mcpMode");
            if (string.Equals(mcpMode, "client", StringComparison.OrdinalIgnoreCase))
            {
                string? pipeName = GetCommandLineOption(args, "--mcpPipe");
                string? pipeToken = GetCommandLineOption(args, "--mcpToken");
                if (string.IsNullOrWhiteSpace(pipeName) || string.IsNullOrWhiteSpace(pipeToken))
                    throw new ArgumentException("MCP client mode requires --mcpPipe and --mcpToken.");

                await StartMcpClientModeAsync(pipeName, pipeToken);
                restoreContent = false;
                return;
            }
#if WINDOWS
            var integratedMcpArg = args.FirstOrDefault(c => c.StartsWith("--mcp=", StringComparison.OrdinalIgnoreCase));
            if (!string.IsNullOrWhiteSpace(integratedMcpArg))
            {
                _pendingIntegratedMcpAddress = integratedMcpArg.Split('=', 2)[1];
            }
#endif

            var remoteArg = args.FirstOrDefault(c => c.StartsWith("--remote=", StringComparison.OrdinalIgnoreCase));
            if (!string.IsNullOrWhiteSpace(remoteArg))
            {
                string remoteValue = remoteArg[(remoteArg.IndexOf('=') + 1)..];
                if (!Uri.TryCreate(remoteValue, UriKind.Absolute, out var remoteUri))
                {
                    await Dispatcher.DispatchAsync(async () =>
                    {
                        await DisplayAlertAsync(Localized._Info, "--remote must contain an absolute HTTP or HTTPS RPC server URL.", Localized._OK);
                    });
                    return;
                }

                string token = GetCommandLineOption(args, "--remoteToken")
                    ?? GetRemoteUriValue(remoteUri, "token")
                    ?? SettingsManager.GetSetting("RemoteRpcToken", string.Empty);
                remoteUri = RemoveRemoteCredentials(remoteUri);
                if (string.IsNullOrWhiteSpace(token))
                {
                    await Dispatcher.DispatchAsync(async () =>
                    {
                        token = await DisplayPromptAsync(Localized._Info, "Input the RPC token below:", Localized._OK, Localized._Cancel);
                    });
                    if (string.IsNullOrWhiteSpace(token)) return;
                }
                await Dispatcher.DispatchAsync(async () =>
                {
                    App.Current?.Windows?[0]?.Title = $"{Localized.AppBrand} - Remoting @ {remoteUri.Host}";
                    var page = await DraftPage.OpenRemoteAsync(remoteUri, token);
                    await page.PostInit();
                    AppShell.instance?.HideNavView();
                    Shell.SetTabBarIsVisible(page, false);
                    Shell.SetNavBarIsVisible(page, true);
                    lastPage = page;
                    await Navigation.PushAsync(page);
                });
                return;
            }

            if (string.IsNullOrWhiteSpace(path) && args.ArrayAny())
            {
                //Only treat non-option arguments as path candidates, so switches like --log won't be mistaken as a path.
                var maybePath = args.Where(c => !c.StartsWith("--", StringComparison.OrdinalIgnoreCase)).OrderByDescending(s => s.Length).FirstOrDefault();
                if (!string.IsNullOrWhiteSpace(maybePath))
                {
                    if (maybePath.Contains(':') && maybePath.Count(c => c == ':') >= 2)
                    {
                        path = maybePath.Split(":", 2, StringSplitOptions.RemoveEmptyEntries)[1] ?? maybePath;
                    }
                    else
                    {
                        path = maybePath;
                    }
                }
            }

            if (string.IsNullOrWhiteSpace(path) && Preferences.ContainsKey("LaunchedPJFCUri"))
            {
                path = Preferences.Get("LaunchedPJFCUri", "");
            }

            //--continue: directly resume to the last opened project when no explicit path is given.
            if (string.IsNullOrWhiteSpace(path) && continueRequested)
            {
                if (SettingsManager.IsSettingExists("General_LastOpenedProject"))
                {
                    path = SettingsManager.GetSetting("General_LastOpenedProject", "");
                }
                LogDiagnostic($"--continue requested, last opened project: {path}");
            }
            if (string.IsNullOrWhiteSpace(path)) return;
            switch (Path.GetExtension(path))
            {
                case ".pjfc":
                    {
                        if (Path.Exists(path))
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
                                    var dirName = Path.GetFileName(path.TrimEnd(Path.DirectorySeparatorChar));
                                    if (File.Exists(Path.Combine(path, $"{dirName}.pjfc")))
                                    {
                                        path = Path.Combine(path, $"{dirName}.pjfc");
                                    }
                                    else
                                    {
                                        await DisplayAlertAsync(Localized._Error, $"Cannot find a valid project file in the directory '{path}'.", Localized._OK);
                                        return;
                                    }
                                }
                            }

                            if (new FileInfo(path).OpenRead().ReadByte() == '{')
                            {
                                try
                                {
                                    var draft = JsonSerializer.Deserialize<ProjectJSONStructure>(File.ReadAllText(path), DraftPage.DraftJSONOption);
                                    if (draft is ProjectJSONStructure && Path.GetDirectoryName(path) is string p)
                                    {
                                        LogDiagnostic($"Launch target from cli args:{path}");
                                        if (openRenderPage)
                                        {
                                            await GoRender(p);
                                        }
                                        else
                                        {
                                            await GoDraft(p, draft.ProjectName ?? "Project", skipAskForRecover: args.Any(c => c.StartsWith("--fromCrashHandler")));
                                        }
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
                                if (openRenderPage)
                                {
                                    await GoRender(path);
                                }
                                else
                                {
                                    await GoDraft(path, (Path.GetDirectoryName(path) ?? "Project").Split('.')?.FirstOrDefault("Project")!, false, false);
                                }

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
            if (restoreContent) Dispatcher.Dispatch(() =>
            {
                Content = origCont;
            });
        }
    }

    private async Task StartMcpClientModeAsync(string pipeName, string pipeToken)
    {
        if (_mcpClientModeMonitor is not null) return;

#if WINDOWS
        EnableMcpClientCloseConfirmation();
#endif

        MenuBarItems.Clear();
        ToolbarItems.Clear();
        Title = "  ";
        Application.Current?.Windows?[0]?.Title = $"{Localized.AppBrand} (MCP)";

        var cancellation = new CancellationTokenSource();
        string clientId = $"projectFrameCut-mcp-editor-{Guid.NewGuid():N}";
        var transport = new NamedPipeRenderClientTransport(
            pipeName,
            pipeToken,
            clientId);
        var client = new RenderClient(transport, clientId);
        try
        {
            Exception? connectionError = null;
            for (int attempt = 0; attempt < 10; attempt++)
            {
                try
                {
                    _ = await client.GetCapabilitiesAsync(cancellation.Token);
                    connectionError = null;
                    break;
                }
                catch (Exception ex) when (ex is IOException or TimeoutException or OperationCanceledException)
                {
                    connectionError = ex;
                    await Task.Delay(200, cancellation.Token);
                }
            }
            if (connectionError is not null)
                throw new IOException("Could not connect to the MCP named pipe.", connectionError);

            _mcpClientModeCancellation = cancellation;
            _mcpClientModeClient = client;
            ShowMcpWaitingState();
            _mcpClientModeMonitor = MonitorMcpClientModeAsync(client, cancellation.Token);
        }
        catch
        {
            cancellation.Dispose();
            await client.DisposeAsync();
            throw;
        }
    }

#if WINDOWS
    private void EnableMcpClientCloseConfirmation()
    {
        if (_mcpClientAppWindow is not null || Window?.Handler?.PlatformView is not Microsoft.UI.Xaml.Window nativeWindow)
            return;

        nint windowHandle = WindowNative.GetWindowHandle(nativeWindow);
        Microsoft.UI.WindowId windowId = Microsoft.UI.Win32Interop.GetWindowIdFromWindow(windowHandle);
        _mcpClientNativeWindow = nativeWindow;
        _mcpClientAppWindow = AppWindow.GetFromWindowId(windowId);
        _mcpClientAppWindow.Closing += OnMcpClientWindowClosing;
    }

    private async void OnMcpClientWindowClosing(AppWindow sender, AppWindowClosingEventArgs args)
    {
        if (_mcpClientCloseConfirmed) return;

        args.Cancel = true;
        if (_mcpClientClosePromptOpen) return;

        _mcpClientClosePromptOpen = true;
        try
        {
            if (!await DisplayAlertAsync(
                Localized._Warn,
                Localized.McpClient_CloseWarning,
                Localized._Confirm,
                Localized._Cancel))
                return;

            _mcpClientCloseConfirmed = true;
            _mcpClientNativeWindow?.Close();
        }
        finally
        {
            _mcpClientClosePromptOpen = false;
        }
    }
#endif

    private async Task MonitorMcpClientModeAsync(IRenderClient client, CancellationToken cancellationToken)
    {
        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                HeadlessProjectSnapshot? snapshot = null;
                try
                {
                    snapshot = await client.GetHeadlessProjectSnapshotAsync(Guid.Empty, cancellationToken);
                }
                catch (Exception ex) when (HasRenderErrorCode(ex, RenderErrorCode.SessionNotFound))
                {
                }

                bool transitioned = await ApplyMcpProjectStateAsync(client, snapshot);
                await Task.Delay(transitioned ? 500 : 5000, cancellationToken);
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
        catch (Exception ex)
        {
            Log(ex, "Monitor MCP client mode", this);
            await Dispatcher.DispatchAsync(async () =>
            {
                ShowMcpWaitingState(Localized._ExceptionTemplate(ex));
                await DisplayAlertAsync(Localized._Error, Localized._ExceptionTemplate(ex), Localized._OK);
            });
        }
    }

    private async Task<bool> ApplyMcpProjectStateAsync(
        IRenderClient client,
        HeadlessProjectSnapshot? snapshot)
    {
        bool transitioned = true;
        await Dispatcher.DispatchAsync(async () =>
        {
            DraftPage? activePage = _mcpClientModePage;
            if (activePage is not null && !Navigation.NavigationStack.Contains(activePage))
            {
                _mcpClientModePage = null;
                activePage = null;
            }

            Guid targetSessionId = snapshot?.SessionId ?? Guid.Empty;
            bool sameSession = _mcpClientModeSessionId == targetSessionId;
            bool sameSnapshot = sameSession && snapshot is not null &&
                _mcpClientModeRevision == snapshot.Revision &&
                string.Equals(_mcpClientModeSnapshotHash, snapshot.SnapshotHash, StringComparison.Ordinal);
            if (activePage is not null && sameSnapshot)
            {
                transitioned = false;
                return;
            }
            if (activePage is null && targetSessionId != Guid.Empty && sameSnapshot)
            {
                transitioned = false;
                return;
            }
            if (activePage is null && snapshot is null && _mcpClientModeSessionId == Guid.Empty)
            {
                transitioned = false;
                return;
            }

            bool refreshingCurrentSession = activePage is not null && sameSession && snapshot is not null;
            if (refreshingCurrentSession && activePage!.HasUnsavedRemoteChanges)
            {
                bool alreadyDeclined = _mcpClientModeDeclinedRevision == snapshot!.Revision &&
                    string.Equals(_mcpClientModeDeclinedSnapshotHash, snapshot.SnapshotHash, StringComparison.Ordinal);
                if (alreadyDeclined)
                {
                    transitioned = false;
                    return;
                }

                bool sync = await DisplayAlertAsync(
                    Localized._Info,
                    Localized.DraftPage_RemoteProject_ModifiedOnServer,
                    Localized.DraftPage_RemoteProject_SyncFromServer,
                    Localized._Cancel);
                if (!sync)
                {
                    _mcpClientModeDeclinedRevision = snapshot.Revision;
                    _mcpClientModeDeclinedSnapshotHash = snapshot.SnapshotHash;
                    transitioned = false;
                    return;
                }
            }

            if (activePage is not null && !refreshingCurrentSession)
            {
                try
                {
                    if (!await activePage.PrepareForMcpProjectReplacementAsync())
                    {
                        transitioned = false;
                        return;
                    }
                }
                catch (Exception ex)
                {
                    transitioned = false;
                    Log(ex, "Save MCP project before replacement", this);
                    await DisplayAlertAsync(Localized._Error, Localized._ExceptionTemplate(ex), Localized._OK);
                    return;
                }
            }

            if (snapshot is null)
            {
                try
                {
                    if (activePage is not null)
                        await activePage.CompleteMcpProjectReplacementAsync();
                    _mcpClientModePage = null;
                    _mcpClientModeSessionId = Guid.Empty;
                    _mcpClientModeRevision = 0;
                    _mcpClientModeSnapshotHash = string.Empty;
                    _mcpClientModeDeclinedRevision = -1;
                    _mcpClientModeDeclinedSnapshotHash = string.Empty;
                    if (activePage is not null) await Navigation.PopToRootAsync(false);
                    ShowMcpWaitingState();
                }
                catch (Exception ex)
                {
                    transitioned = false;
                    Log(ex, "Close MCP project page", this);
                    await DisplayAlertAsync(Localized._Error, Localized._ExceptionTemplate(ex), Localized._OK);
                }
                return;
            }

            try
            {
                RemoteProjectSession session = RemoteProjectSession.CreateNamedPipeSession(client, snapshot);
                DraftPage page = DraftPage.CreateFromRemoteSession(session);
                page.AllowExit = false;
                await page.PostInit();
                if (activePage is not null)
                {
                    if (refreshingCurrentSession)
                        activePage.DetachRemoteSessionForReplacement();
                    else
                        await activePage.CompleteMcpProjectReplacementAsync();
                }
                App.Current?.Windows?[0]?.Title = $"{Localized.AppBrand} - {page.ProjectName} (MCP)";
                AppShell.instance?.HideNavView();
                Shell.SetTabBarIsVisible(page, false);
                Shell.SetNavBarIsVisible(page, true);

                if (activePage is null)
                {
                    await Navigation.PushAsync(page);
                }
                else
                {
                    Navigation.InsertPageBefore(page, activePage);
                    Navigation.RemovePage(activePage);
                }

                _mcpClientModePage = page;
                _mcpClientModeSessionId = snapshot.SessionId;
                _mcpClientModeRevision = snapshot.Revision;
                _mcpClientModeSnapshotHash = snapshot.SnapshotHash;
                _mcpClientModeDeclinedRevision = -1;
                _mcpClientModeDeclinedSnapshotHash = string.Empty;
                lastPage = page;
            }
            catch (Exception ex)
            {
                transitioned = false;
                Log(ex, "Open MCP project page", this);
                await DisplayAlertAsync(Localized._Error, Localized._ExceptionTemplate(ex), Localized._OK);
            }
        });
        return transitioned;
    }

    private void ShowMcpWaitingState(string? detail = null)
    {
        string text = detail ?? Localized.HomePage_WaitingForMCPProject;
        AppShell.instance?.HideNavView();
        Content = new VerticalStackLayout
        {
            HorizontalOptions = LayoutOptions.Center,
            VerticalOptions = LayoutOptions.Center,
            Spacing = 12,
            Children =
            {
                new ActivityIndicator { IsRunning = true, WidthRequest = 48, HeightRequest = 48 },
                new Label { Text = text, HorizontalTextAlignment = TextAlignment.Center },
            },
        };
    }

    private static bool HasRenderErrorCode(Exception exception, RenderErrorCode code)
        => exception is RemoteRenderException remote && remote.ErrorCode == code
            || exception.Data[nameof(RemoteError.Code)] is RenderErrorCode dataCode && dataCode == code
            || exception.Data["RenderErrorCode"] is RenderErrorCode renderCode && renderCode == code;

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
        if (!SettingsManager.IsBoolSettingTrue("UseLegacyCreateExperience"))
        {
            await Navigation.PushAsync(new CreatePage());
            return;
        }

        string draftSourcePath = Path.Combine(MauiProgram.DataPath, "My Drafts");

        var projName = await DisplayPromptAsync(Localized._Info, Localized.HomePage_CreateAProject_InputName, Localized._OK, Localized._Cancel, "Untitled Project", 1024, null, $"Untitled Project {DateTime.Now:yyyy\\-M\\-dd}");
        if (projName is null || string.IsNullOrWhiteSpace(projName))
        {
            return;
        }
        if (Path.GetInvalidPathChars().Any(projName.Contains) || Path.GetInvalidFileNameChars().Any(projName.Contains))
        {
            await DisplayAlertAsync(Localized._Error, GetInvalidFileNameWarn(), Localized._OK);
            return;
        }

        draftSourcePath = Path.Combine(draftSourcePath, projName + ".pjfc");
        if (Path.GetInvalidPathChars().Any(draftSourcePath.Contains) || draftSourcePath.Length > GetMaxPathLength())
        {
            await DisplayAlertAsync(Localized._Error, GetInvalidFileNameWarn(), Localized._OK);
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
            await DisplayAlertAsync(Localized._Error, GetInvalidFileNameWarn(), Localized._OK);
            return;
        }

        var ProjectInfo = new ProjectJSONStructure
        {
            ProjectName = projName,
            NormallyExited = true,
            LastChanged = DateTime.Now,
            LastOpenAPIBaseVersion = IPluginBase.CurrentPluginAPIVersion,
            LastOpenAppVersion = Assembly.GetExecutingAssembly().GetName().Version?.ToString() ?? "Unknown",
            LastOpenAppName = MauiProgram.AssemblyName,
            LastOpenAppIdentifier = MauiProgram.AppIdentifier,
            PluginUsed = []
        };

        File.WriteAllText(
            Path.Combine(draftSourcePath, "timeline.json"),
            JsonSerializer.Serialize(new DraftStructureJSON
            {
                Clips = Array.Empty<ClipDraftDTO>(),
            }));
        File.WriteAllText(
            Path.Combine(draftSourcePath, "assets.json"),
            JsonSerializer.Serialize(Array.Empty<AssetItem>()));
        File.WriteAllText(
            Path.Combine(draftSourcePath, "project.pjfc"),
            JsonSerializer.Serialize(ProjectInfo));
        DraftImportAndExportHelper.EnsureProjectDirectoryShellIntegration(draftSourcePath);
        await Task.Delay(1500);
        await _viewModel.LoadDrafts(Path.Combine(MauiProgram.DataPath, "My Drafts"));


    }

    public static string GetInvalidFileNameWarn()
    {
        if (OperatingSystem.IsWindows())
        {
            return Localized.HomePage_CreateAProject_InvalidName_Windows;
        }
        else
        {
            return Localized.HomePage_CreateAProject_InvalidName_Linux;
        }
    }

    public static int GetMaxPathLength()
    {
#if WINDOWS
        try
        {
            var k = Registry.LocalMachine.OpenSubKey("SYSTEM\\CurrentControlSet\\Control\\FileSystem");
            if (Convert.ToBoolean(k.GetValue("LongPathsEnabled"))) return 65535;
            return 260;
        }
        catch { }
        return 65535;
#else
        return 65535;
#endif
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
            await DisplayAlertAsync(Localized._Error, GetInvalidFileNameWarn(), Localized._OK);
            return;
        }

        draftSourcePath = Path.Combine(draftSourcePath, projName + ".pjfc");
        if (Path.GetInvalidPathChars().Any(draftSourcePath.Contains) || Path.GetInvalidFileNameChars().Any(draftSourcePath.Contains) || draftSourcePath.Length > 65535)
        {
            await DisplayAlertAsync(Localized._Error, GetInvalidFileNameWarn(), Localized._OK);
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
            await DisplayAlertAsync(Localized._Error, $"{GetInvalidFileNameWarn()}{Environment.NewLine}{Localized._ExceptionTemplate(ex)}", Localized._OK);
            return;
        }

        var ProjectInfo = new ProjectJSONStructure
        {
            ProjectName = projName,
            NormallyExited = true,
            LastChanged = DateTime.Now,
            LastOpenAPIBaseVersion = IPluginBase.CurrentPluginAPIVersion,
            LastOpenAppVersion = Assembly.GetExecutingAssembly().GetName().Version?.ToString() ?? "Unknown",
            LastOpenAppName = MauiProgram.AssemblyName,
            LastOpenAppIdentifier = MauiProgram.AppIdentifier,
            PluginUsed = []
        };

        File.WriteAllText(
            Path.Combine(draftSourcePath, "project.json"),
            JsonSerializer.Serialize(ProjectInfo));
        DraftImportAndExportHelper.EnsureProjectDirectoryShellIntegration(draftSourcePath);

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
                    KeyValuePair<string, DraftStructureJSON?> newest = tmls.Where(c => c.Value is not null && c.Value.SavedAt <= DateTime.Now).OrderByDescending(t => t.Value?.SavedAt).FirstOrDefault(new KeyValuePair<string, DraftStructureJSON?>("", null));
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
                await Dispatcher.DispatchAsync(async () =>
                {
                    if (timeline.Clips.Any(c => c.EffectBundles?.Any() ?? false))
                    {
                        if (!await DisplayAlertAsync(Localized._Info, Localized.HomePage_GoDraft_OneWayUpdateWarn("- EffectBundle"), Localized._Confirm, Localized._Cancel))
                        {
                            return;
                        }
                    }
                    if (!(skipAskForRecover ?? false) && timeline.Clips.Any(c => c.Effects is { Length: > 0 } && (c.Effects?.Any(d => d.IsVariableArgumentEffect) ?? false)))
                    {
                        await DisplayAlertAsync(Localized._Info, Localized.HomePage_GoDraft_DeprecatedFeatureWarn(IPluginBase.CurrentPluginAPIVersion + 1, "- BindableEffect"), Localized._Confirm);
                    }
                });
                (var dict, var trackCount) = DraftImportAndExportHelper.ImportFromJSON(timeline, project);
                ConcurrentDictionary<string, AssetItem> assetDict = new(assets.ToDictionary((a) => a.AssetId ?? $"unknown+{Random.Shared.Next()}"));
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
                            notfounds.Add(item.Value.Id.ToString(), new AssetItem { AssetId = item.Value.Id.ToString(), Name = item.Value.SourcePath.StartsWith('$') ? $"Asset@{item.Value.SourcePath.Substring(1)}" : item.Value.SourcePath, Path = item.Value.SourcePath });
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
                //                if (notfounds.Any())
                //                {
                //                    var notFoundStr = notfounds.Select(kv => $"- {kv.Value.Name} ({kv.Value.Path})").Aggregate((a, b) => $"{a}{Environment.NewLine}{b}");
                //                    await Dispatcher.DispatchAsync(async () =>
                //                    {
                //                        int result = 0;
                //#if WINDOWS
                //                        Microsoft.UI.Xaml.Controls.ContentDialog diag = new Microsoft.UI.Xaml.Controls.ContentDialog
                //                        {
                //                            Title = Localized.HomePage_SourceNotFound_Title,
                //                            Content = $"{Localized.HomePage_SourceNotFound}\r\n{notFoundStr}",
                //                            CloseButtonText = Localized._Cancel,
                //                            PrimaryButtonText = Localized.HomePage_SourceNotFound_Continue,
                //                            SecondaryButtonText = Localized.HomePage_SourceNotFound_RemoveThem
                //                        };

                //                        var services = Application.Current?.Handler?.MauiContext?.Services;
                //                        var dialogueHelper = services?.GetService(typeof(projectFrameCut.Platforms.Windows.IDialogueHelper)) as projectFrameCut.Platforms.Windows.IDialogueHelper;
                //                        if (dialogueHelper != null)
                //                        {
                //                            var r = await dialogueHelper.ShowContentDialogue(diag);
                //                            result = (int)r;
                //                        }
                //#else
                //                        string[] opts = [Localized.HomePage_SourceNotFound_RemoveThem, Localized.HomePage_SourceNotFound_Continue];

                //                        var select = await DisplayActionSheetAsync($"{Localized.HomePage_SourceNotFound}\r\n{notFoundStr}", null, Localized._Cancel, opts);
                //                        if (select == Localized.HomePage_SourceNotFound_RemoveThem) result = 2;
                //                        else if (select == Localized.HomePage_SourceNotFound_Continue) result = 1;
                //                        else result = 0;
                //#endif


                //                        switch (result)
                //                        {
                //                            case 0:
                //                                {
                //                                    page = null;
                //                                    return;
                //                                }
                //                            case 1:
                //                                {
                //                                    break;
                //                                }
                //                            case 2:
                //                                {
                //                                    var input = await DisplayPromptAsync(Localized._Warn, Localized.HomePage_SourceNotFound_RemoveThem_Conf, Localized._OK, Localized._Cancel, "no", -1, null, null);
                //                                    if (input != "yes") return;
                //                                    foreach (var item in notfounds)
                //                                    {
                //                                        dict = new(dict.RemoveRange(dict.Where(c => c.Value.SourcePath == item.Value.Path)));
                //                                        assetDict = new(assetDict.RemoveRange(assetDict.Where(c => c.Key == item.Key)));
                //                                    }

                //                                    break;
                //                                }
                //                        }
                //                    });
                //                }
                if (!SettingsManager.IsSettingExists("Edit_PreferredPopupMode"))
                {
                    SettingsManager.WriteSetting("Edit_PreferredPopupMode", "bottom");
                }
                if (!(SettingsManager.IsSettingExists("Edit_UseDynamicPreview") || SettingsManager.IsSettingExists("Edit_LiveVideoPreviewDefaultResolution"))) SettingsManager.WriteSetting("Edit_UseDynamicPreview", true.ToString());
                DraftPage? createdPage = null;
                bool pageCreationCancelled = false;
                await Dispatcher.DispatchAsync(async () =>
                {
                    int maxRetries = 5;
                    int attempt = 0;
                    while (true)
                    {
                        try
                        {
                            var p = new DraftPage(project ?? new ProjectJSONStructure(), dict, assetDict, trackCount, draftSourcePath, project?.ProjectName ?? "?", isReadonly)
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
                                DynamicPreviewResolutionDivisor = SettingsManager.GetSettingAs<int>("Edit_DynamicPreviewResolutionDivisor", 1, 1),
                                DynamicPreviewTimeout = SettingsManager.GetSettingAs<int>("Edit_DynamicPreviewTimeout", 5000, 5000),
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
                                    p.DefaultPreviewWidth = int.TryParse(resolution.Split('x', 2)[0], out var w) ? w : 1280;
                                    p.DefaultPreviewHeight = int.TryParse(resolution.Split('x', 2)[1], out var h) ? h : 720;
                                }
                                else
                                {
                                    p.DefaultPreviewWidth = 1280;
                                    p.DefaultPreviewHeight = 720;
                                }
                            }
#if WINDOWS || LINUX
                            // AcceleratorsManager was initialized during plugin load.
                            // No need to re-enumerate devices here — the configuration from
                            // accels.json (or the default first non-CPU accelerator) is already loaded.
                            if (projectFrameCut.Render.HwAccelEngine.AcceleratorsManager.DefaultAccelerator is null)
                            {
                                Log("WARNING: No ILGPU accelerator found on this device. GPU-accelerated effects will be unavailable.");
                            }
#endif
                            await p.PostInit();
                            foreach (var plugin in PluginManager.LoadedPlugins.Values.OfType<IApplicationPluginBase>())
                            {
                                try
                                {
                                    plugin.InjectUI(p);
                                    var name = plugin.ReadLocalizationItem("_PluginBase_Name_", Localized._LocaleId_) ?? plugin.Name;
                                    var items = plugin.GetMenuItems(p);
                                    var sub = new MenuFlyoutSubItem { Text = name, IsEnabled = items.Any() };
                                    items.ForEach(c => sub.Add(c));
                                    p.ExtensionsMenuBar.Add(sub);

                                }
                                catch (Exception ex)
                                {
                                    Log(ex, $"plugin {plugin.Name} InjectUI", this);
                                    if (!await DisplayAlertAsync(Localized._Warn, Localized.HomePage_InitPlugin_Fail(plugin.Name, ex), Localized.HomePage_SourceNotFound_Continue, Localized._Cancel))
                                    {
                                        pageCreationCancelled = true;
                                        return;
                                    }
                                }
                            }
                            createdPage = p;
                            break;
                        }
                        catch (Exception ex)
                        {
                            attempt++;
                            Log(ex, $"Exception while loading DraftPage attempt {attempt}", this);
                            if (attempt >= maxRetries)
                            {
                                throw;
                            }
                            await Task.Delay(500);
                            continue;
                        }
                    }
                });
                if (pageCreationCancelled)
                {
                    page = null;
                    return;
                }
                page = createdPage;

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
        MainThread.BeginInvokeOnMainThread(() =>
        {
            try
            {
                cancelButton.IsVisible = false;
                Content.InvalidateMeasure();
                Content = origContent;
            }
            catch { }
        });
        initTimer.Stop();
        LogDiagnostic($"Initialize project {project?.ProjectName} cost {initTimer.ElapsedMilliseconds} ms.");
        if (initTimer.Elapsed.TotalSeconds < 2) await Task.Delay(2000 - (int)initTimer.Elapsed.TotalMilliseconds);


        if (!cancelled && page != null && project != null)
        {
            //Remember the last opened project so it can be resumed with the --continue command line switch.
            try
            {
                SettingsManager.WriteSetting("General_LastOpenedProject", draftSourcePath);
            }
            catch (Exception ex)
            {
                Log(ex, "record last opened project", this);
            }
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
                        AppShell.instance?.HideNavView();
                        Shell.SetTabBarIsVisible(page, false);
                        Shell.SetNavBarIsVisible(page, true);
                        lastPage = page;
                        await Navigation.PushAsync(page);
#if WINDOWS
                        if (!string.IsNullOrWhiteSpace(_pendingIntegratedMcpAddress))
                        {
                            string address = _pendingIntegratedMcpAddress;
                            _pendingIntegratedMcpAddress = null;
                            try
                            {
                                await StartIntegratedMcpServerAsync(page, address);
                                page.SetStatusText($"Integrated MCP server: {address.TrimEnd('/')}/mcp");
                            }
                            catch (Exception serverEx)
                            {
                                Log(serverEx, "start integrated mcp server", this);
                                await DisplayAlertAsync(
                                    Localized._Warn,
                                    $"Failed to start the integrated MCP server.\r\n{serverEx.Message}",
                                    Localized._OK);
                            }
                        }
#endif
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

    private async Task StartIntegratedMcpServerAsync(DraftPage page, string address)
    {
        if (!Uri.TryCreate(address, UriKind.Absolute, out var listenUri))
        {
            throw new ArgumentException("--mcp must contain an absolute HTTP or HTTPS URL.", nameof(address));
        }

        await StopIntegratedMcpServerAsync();

        var backend = new IntegratedApiBackend(page);
        var server = new IntegratedApiServer();
        string? transportWarning = null;
        try
        {
            await server.StartAsync(
                new IntegratedApiServerOptions
                {
                    ListenUri = listenUri,
                    WarningSink = warning =>
                    {
                        transportWarning = warning;
                        Log(warning, "warn");
                    },
                },
                backend);
        }
        catch
        {
            await server.DisposeAsync();
            await backend.DisposeAsync();
            throw;
        }

        _integratedApiBackend = backend;
        _integratedApiServer = server;
        if (!string.IsNullOrWhiteSpace(transportWarning))
        {
            await DisplayAlertAsync(Localized._Warn, transportWarning, Localized._OK);
        }
    }

    private static string? GetCommandLineOption(IEnumerable<string> args, string optionName)
    {
        string prefix = optionName + "=";
        string? value = args.FirstOrDefault(arg => arg.StartsWith(prefix, StringComparison.OrdinalIgnoreCase));
        return value is null ? null : value[prefix.Length..];
    }

    private static string? GetRemoteUriValue(Uri uri, string key)
    {
        foreach (string part in uri.Query.TrimStart('?').Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            string[] pair = part.Split('=', 2);
            if (pair.Length == 2 && pair[0].Equals(key, StringComparison.OrdinalIgnoreCase))
                return Uri.UnescapeDataString(pair[1].Replace('+', ' '));
        }

        return string.IsNullOrWhiteSpace(uri.UserInfo)
            ? null
            : Uri.UnescapeDataString(uri.UserInfo);
    }

    private static Uri RemoveRemoteCredentials(Uri uri)
    {
        var builder = new UriBuilder(uri)
        {
            UserName = string.Empty,
            Password = string.Empty,
        };
        var query = uri.Query.TrimStart('?')
            .Split('&', StringSplitOptions.RemoveEmptyEntries)
            .Where(part => !part.StartsWith("token=", StringComparison.OrdinalIgnoreCase))
            .ToArray();
        builder.Query = string.Join('&', query);
        return builder.Uri;
    }

    private async Task StopIntegratedMcpServerAsync()
    {
        var server = _integratedApiServer;
        var backend = _integratedApiBackend;
        _integratedApiServer = null;
        _integratedApiBackend = null;

        if (server is not null)
        {
            try
            {
                await server.DisposeAsync();
            }
            catch (Exception ex)
            {
                Log(ex, "stop integrated mcp server", this);
            }
        }

        if (backend is not null)
        {
            await backend.DisposeAsync();
        }
    }
#if WINDOWS
    UserActivitySession? _previousSession;
#endif
    DraftPage? lastPage = null;
    private bool _renderRecoveryAttempted;

    protected override async void OnAppearing()
    {
        base.OnAppearing();
#if WINDOWS
        await StopIntegratedMcpServerAsync();
#endif
        App.Current?.Windows?[0]?.Title = Localized.AppBrand;

        try
        {
            Environment.CurrentDirectory = MauiProgram.DataPath;
        }
        catch
        {
            // iOS blocks chdir() through the app container root.
            // We use absolute paths throughout, so CurrentDirectory is unnecessary.
        }
        try
        {
            if (lastPage is not null && Window is not null)
            {
                Window?.SizeChanged -= lastPage.Window_SizeChanged;
            }
        }
        catch { }
        try
        {
            if (Directory.GetDirectories(Path.Combine(MauiProgram.DataPath, "My Drafts"), "*").Length == 0)
            {
                NoContentLayout.IsVisible = true;
            }
            else
            {
                NoContentLayout.IsVisible = false;
                await _viewModel.LoadDrafts(Path.Combine(MauiProgram.DataPath, "My Drafts"));
                if (_viewModel.LoadFailed)
                {
                    await DisplayAlertAsync(Localized._Info, Localized.HomePage_DraftLoadFailed(), Localized._OK);

                }
                await TryRestoreActiveRenderAsync();
            }
        }
        catch (Exception ex)
        {
            // iOS 17+ sandbox: stat on container root returns EPERM.
            Log(ex, "load draft list", this);
            NoContentLayout.IsVisible = true; // safe fallback
        }

        try
        {
            DynamicPreview.DiskCacheRoot = Path.Combine(MauiProgram.DataPath, "RenderCache", "clipLocalFallback");
        }
        catch (Exception ex)
        {
            Log(ex, "set DiskCache root", this);
        }

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

#endif
    }

    private async Task TryRestoreActiveRenderAsync()
    {
        if (_renderRecoveryAttempted || !RenderRpcBootstrap.SupportsCliRenderProcess) return;
        _renderRecoveryAttempted = true;

        foreach (var project in _viewModel.Projects.ToArray())
        {
            try
            {
                if (!RenderRpcBootstrap.TryReconnectCliRender(project._projectPath, out var jobId)) continue;
                var job = await RenderRpcBootstrap.Client.GetJobStatusAsync(jobId);
                if (job.State is RenderJobState.Queued or RenderJobState.Running)
                {
                    await GoRender(project);
                    return;
                }

                try { await RenderRpcBootstrap.Client.CloseProjectAsync(Guid.Empty); } catch { }
                await RenderRpcBootstrap.DisposeAsync();
            }
            catch (Exception ex)
            {
                Log(ex, "Restore background render after application restart", this);
                try { await RenderRpcBootstrap.DisposeAsync(); } catch { }
            }
        }
    }

    protected override void OnNavigatedTo(NavigatedToEventArgs args)
    {
        base.OnNavigatedTo(args);
#if WINDOWS
        bool haveDrafts = false;
        try
        {
            haveDrafts = Directory.GetDirectories(Path.Combine(MauiProgram.DataPath, "My Drafts"), "*").Length != 0;
        }
        catch { }
        Task.Delay(5000).ContinueWith(async (_) =>
        {
            if (!SettingsManager.IsSettingExists("CreateControlMovedHint") && haveDrafts)
            {
                await MainThread.InvokeOnMainThreadAsync(() =>
                {
                    var tip = new winui.TeachingTip
                    {
                        Title = Localized.HomePage_CreateControlMovedHint_Title,
                        FontSize = 20,
                        Subtitle = Localized.HomePage_CreateControlMovedHint_SubTitle,
                        CloseButtonContent = Localized._OK,
                        ActionButtonContent = Localized.HomePage_CreateControlMovedHint_DontShowAnymore,
                        IconSource = new winui.SymbolIconSource { Symbol = winui.Symbol.Add },
                        IsOpen = false
                    };
                    tip.ActionButtonClick += (s, e) =>
                    {
                        SettingsManager.WriteSetting("CreateControlMovedHint", true.ToString());
                        tip.IsOpen = false;
                    };
                    (this.Handler?.PlatformView as winui.Panel)?.Children?.Add(tip);
                    if (App.createItem is not null)
                    {
                        tip.Target = App.createItem;
                        if (App.createItem.IsLoaded)
                        {
                            tip.IsOpen = true;
                        }
                        else
                        {
                            void OnLoaded(object s, Microsoft.UI.Xaml.RoutedEventArgs e)
                            {
                                App.createItem!.Loaded -= OnLoaded;
                                tip.IsOpen = true;
                            }
                            App.createItem.Loaded += OnLoaded;
                        }
                    }
                    else
                    {
                        tip.IsOpen = true;
                    }
                });
            }
        });
#endif
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
#if !iDevices
        try
        {
            if (File.Exists(Path.Combine(FileSystem.AppDataDirectory, "OverrideUserDataPath.txt")) && !Directory.Exists(File.ReadAllText(Path.Combine(FileSystem.AppDataDirectory, "OverrideUserDataPath.txt"))))
            {
                await DisplayAlertAsync(Localized._Warn, Localized.HomePage_UserdataPathNotFoundWarn(File.ReadAllText(Path.Combine(FileSystem.AppDataDirectory, "OverrideUserDataPath.txt"))), Localized._OK);

            }
        }
        catch { }
#endif
        MainSettingsPage.SyncSettingToModules();
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
        => await GoRender(vmItem._projectPath);

    private async Task GoRender(string draftSourcePath)
    {
        try
        {
            string projectPath = File.Exists(Path.Combine(draftSourcePath, "project.pjfc"))
                ? Path.Combine(draftSourcePath, "project.pjfc")
                : Path.Combine(draftSourcePath, "project.json");
            var project = JsonSerializer.Deserialize<ProjectJSONStructure>(File.ReadAllText(projectPath), DraftPage.DraftJSONOption);
            var tml = JsonSerializer.Deserialize<DraftStructureJSON>(File.ReadAllText(Path.Combine(draftSourcePath, "timeline.json")), DraftPage.DraftJSONOption);
            if (tml is null || project is null)
            {
                await DisplayAlertAsync(Localized._Warn, $"{Localized.HomePage_GoDraft_DraftBroken_InvaildInfo}", Localized._OK);
                return;
            }
            (var dict, var trackCount) = DraftImportAndExportHelper.ImportFromJSON(tml, project);
            var draftPage = new DraftPage(project, dict, new(), trackCount, draftSourcePath, project.ProjectName ?? "?", false);
            var draft = DraftImportAndExportHelper.ExportFromDraftPage(draftPage, true, false);
            var renderPage = new RenderPage(draftSourcePath, tml.Duration, project, draft);
            await Dispatcher.DispatchAsync(async () =>
            {
                App.Current?.Windows?[0]?.Title = $"{Localized.AppBrand} - {project.ProjectName}";
                Shell.SetTabBarIsVisible(renderPage, false);
                Shell.SetNavBarIsVisible(renderPage, true);
#if WINDOWS
                AppShell.instance.CollapseNavView();
                AppShell.instance.HideNavView();
#endif
                await Navigation.PushAsync(renderPage);
            });

        }
        catch (Exception ex)
        {
            Log(ex, "open render page", this);
            await DisplayAlertAsync(Localized._Warn, $"{Localized.HomePage_GoDraft_DraftBroken_InvaildInfo}\r\n({ex.Message})", Localized._OK);
            return;
        }

    }

    private async Task RenameProject(ProjectsViewModel vmItem)
    {
        try
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
                await DisplayAlertAsync(Localized._Error, GetInvalidFileNameWarn(), Localized._OK);
                return;
            }
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

            try
            {
                Directory.Move(Path.GetDirectoryName(vmItem._projectPath) ?? throw new InvalidOperationException("Cannot find project root."), newPath);
            }
            catch { } //ignore the exception, because we already changed the project name in the project.pjfc file, so it won't affect the draft itself.

            await _viewModel.LoadDrafts(Path.Combine(MauiProgram.DataPath, "My Drafts"));
        }
        catch (Exception ex)
        {
            Log(ex, "rename project", this);
            await DisplayAlertAsync(Localized._Error, Localized.HomePage_ProjectContextMenu_Rename_Fail(vmItem.Name, ex), Localized._OK);
        }
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
                    // CollectionView recycles item containers.  The border can therefore
                    // display a different project after the list is reloaded, while this
                    // callback itself remains registered from the original Loaded event.
                    // Always resolve the item that is currently displayed by the border.
                    if (border.BindingContext is not ProjectsViewModel currentItem)
                    {
                        return;
                    }

                    _lastSelectedItemName = currentItem.Name;
                    ProjectsCollection.SelectedItem = currentItem;
                },
                OnClicked: async () =>
                {
                    if (border.BindingContext is not ProjectsViewModel currentItem)
                    {
                        return;
                    }

                    if (currentItem._name == CreateButtonName)
                    {
                        await CreateDraft();
                    }
                    else
                    {
                        await GoDraft(currentItem);
                    }

                    ProjectsCollection.SelectedItem = null;
                    _lastSelectedItemName = string.Empty;
                },
                OnContextMenuClick: async () =>
                {
                    if (border.BindingContext is not ProjectsViewModel currentItem)
                    {
                        return;
                    }

                    if (OperatingSystem.IsAndroid() || OperatingSystem.IsIOS())
                    {
                        Vibration.Vibrate(120);
                    }
                    await ShowContextMenu(currentItem);
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

    private async void CreateNewProjectButton_Clicked(object sender, EventArgs e)
    {
        await Navigation.PushAsync(new CreatePage());
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
                LastOpenAppName = MauiProgram.AssemblyName,
                LastOpenAppIdentifier = MauiProgram.AppIdentifier,
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
            element.ExtraData["TextEntries"] = TextEntryMigration.MigrateFromTextClipEntries(new List<TextClipEntry>
            {
                template.First().Value with { text = $"Happy April fools day!" }
            });

            File.WriteAllText(
                Path.Combine(draftSourcePath, "timeline.json"),
                JsonSerializer.Serialize(new DraftStructureJSON
                {
                    Clips = [DraftImportAndExportHelper.ExportClipElementFromDraftPage(_draftPage, element, false)],
                }));
            File.WriteAllText(
                Path.Combine(draftSourcePath, "assets.json"),
                JsonSerializer.Serialize(Array.Empty<AssetItem>()));
            File.WriteAllText(
                Path.Combine(draftSourcePath, "project.pjfc"),
                JsonSerializer.Serialize(ProjectInfo));
            DraftImportAndExportHelper.EnsureProjectDirectoryShellIntegration(draftSourcePath);

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
            //Projects.Add(new ProjectsViewModel
            //{
            //    _name = "!!CreateButton!!",
            //    _thumbPath = "!!CreateButton!!"
            //});
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
                    Projects.Add(item);
                }
                // Insert failed (invalid) projects after valid ones, so they appear closer to the bottom
                foreach (var f in failedProjects)
                {
                    Projects.Add(f);
                }
            }
            catch (Exception ex)
            {
                Log(ex, "render draft list", this);
                LoadFailed = true;
            }
        }

#if WINDOWS || LINUX
        var origMode = GCSettings.LargeObjectHeapCompactionMode;
        GCSettings.LargeObjectHeapCompactionMode = GCLargeObjectHeapCompactionMode.CompactOnce;
        GC.Collect(2, GCCollectionMode.Forced, blocking: true, compacting: true);
        GC.WaitForPendingFinalizers();
        GC.Collect(2, GCCollectionMode.Forced, blocking: true, compacting: true);
        GCSettings.LargeObjectHeapCompactionMode = origMode;
#else
        GC.Collect();
        GC.WaitForPendingFinalizers();
#endif
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
