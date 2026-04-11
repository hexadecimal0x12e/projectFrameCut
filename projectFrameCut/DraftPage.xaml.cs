using Microsoft.Maui.Controls.Shapes;
using Microsoft.Maui.Devices;
using Microsoft.Maui.Storage;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading.Tasks;
using System.Windows.Input;

using Path = System.IO.Path;
using Grid = Microsoft.Maui.Controls.Grid;
using Image = Microsoft.Maui.Controls.Image;
using Application = Microsoft.Maui.Controls.Application;

using projectFrameCut.Render;
using projectFrameCut.Render.ClipsAndTracks;
using projectFrameCut.Render.RenderAPIBase.Project;
using projectFrameCut.Render.Plugin;
using projectFrameCut.Shared;
using projectFrameCut.DraftStuff;

using projectFrameCut.Setting.SettingManager;
using projectFrameCut.LivePreview;
using CommunityToolkit.Maui.Views;
using CommunityToolkit.Maui.Core;
using projectFrameCut.Services;
using projectFrameCut.Render.EncodeAndDecode;
using projectFrameCut.Asset;
using projectFrameCut.ViewModels;
using projectFrameCut.Render.Rendering;
using PictureExtensions = projectFrameCut.Shared.PictureExtensions;
using System.Runtime;
using projectFrameCut.ApplicationAPIBase.Views.PropertyPanelBuilders;
using projectFrameCut.ApplicationAPIBase.Views.MultiWindowView;
using CommunityToolkit.Maui.Alerts;
using projectFrameCut.ApplicationAPIBase.Helpers;
using projectFrameCut.ApplicationAPIBase.Effect;
using ITransform = projectFrameCut.Render.RenderAPIBase.ClipAndTrack.ITransform;
using projectFrameCut.Render.RenderAPIBase.Plugins;
using System.Reflection;
using projectFrameCut.Render.RenderAPIBase.ClipAndTrack;
using System.Runtime.InteropServices;
using projectFrameCut.ApplicationAPIBase.Project;






#if WINDOWS
using Windows.ApplicationModel.DataTransfer;
using Windows.Storage;
using ILGPU.Runtime;

#endif

#if iDevices
using Foundation;
using UIKit;
using projectFrameCut.iDevicesAPI;
using MobileCoreServices;
using projectFrameCut.MetalAccelerater;


#endif

#if ANDROID
using projectFrameCut.Render.AndroidOpenGL.Platforms.Android;
using projectFrameCut.Render.AndroidOpenGL;
using Microsoft.Maui.Platform;
using Android.Content.Res;
using CommunityToolkit.Maui.Extensions;
using Google.Android.Material.Chip;

#endif

namespace projectFrameCut;

public partial class DraftPage : ContentPage, IDraftPage
{
    #region const
    const int ClipHeight = 62;
    const double MinClipWidth = 30.0;
    const double NarrowTrackHeaderWindowThreshold = 720.0;
    const double NarrowTrackHeaderColumnWidth = 88.0;
    const string MaterialIconFontFamily = "Icons";
    const string MaterialIconPlay = "\ue037";
    const string MaterialIconPause = "\ue034";
    const string MaterialIconClose = "\ue5cd";
    const string MainMultiWindowViewStatePropertyKey = "__DraftPage_MainMultiWindowView_State_v1";
    public const int SubTrackOffset = 10000;

    public readonly string[] DirectoriesNeeded =
    [
        "saveSlots",
        "thumbs",
        "assets",
        "proxy"
    ];

    static readonly JsonSerializerOptions savingOpts = new() { WriteIndented = true, NumberHandling = JsonNumberHandling.AllowNamedFloatingPointLiterals };

    public static JsonSerializerOptions DraftJSONOption => savingOpts;
    #endregion

    #region members
    ConcurrentDictionary<string, double> HandleStartWidth = new();

    ClipElementUI? _selected = null;
    readonly HashSet<string> _selectedClipIds = [];
    readonly ConcurrentDictionary<string, Brush?> _selectedOrigColorByClipId = new();
    private readonly List<TimelineClipboardItem> _timelineClipboard = [];
    private bool _timelineClipboardFromCut = false;
    private double _currentFrame = 0;
    public double CurrentFrame => _currentFrame;

    TapGestureRecognizer nopGesture = new(), rulerTapGesture = new();
    DropGestureRecognizer fileDropGesture = new();

    int trackCount = 0;
    double _startPreviewHeight = 0;
    double tracksZoomOffest = 1d;

    string popupShowingDirection = "none";
    CommunityToolkit.Maui.Views.Popup? _currentCommunityToolkitPopup = null;


    private Size WindowSize = new(500, 500);
    private bool _hasTriedRestoreMainMultiWindowViewState = false;
    private bool _hasAppliedDefaultMainMultiWindowLayout = false;



    private const double SnapGridPixels = 10.0;
    private const double SnapThresholdPixels = 8.0;
    private bool SnapEnabled = true;
    private Func<int, double, ClipElementUI>? _pendingClipPlacementFactory = null;
    private string? _pendingClipPlacementName = null;
    private Predicate<int>? _pendingClipPlacementTrackFilter = null;
    private readonly Dictionary<string, (int Track, double X)> _keyboardMoveOriginalPlacement = new();
    private List<ClipElementUI> _keyboardMoveClips = [];
    private bool _keyboardMoveHasMoved = false;
    private int _keyboardMoveTrackDelta = 0;
    private double _keyboardMovePixelDelta = 0;


    DenoiseHelper Xdenoiser = new(), Ydenoiser = new();

    private double _playbackStartFrame = 0;
    private string? _nextPlaybackPath = null, _lastPlaybackPath = null;
    private string? _lastRealtimeAudioPath = null;
    private bool _isPreRendering = false;
    private bool _isLivePreviewPlayerEventsHooked = false;
    private Grid? _livePreviewRealtimeHost = null;

    Lock saveLocker = new();

    bool AlreadyDisappeared = false;

    ConcurrentDictionary<string, DraftTasks> RunningTasks = new();

    private bool _historyNavigatedByUndoRedo = false;
    private bool _hasResolvedInitialPreviewFrame = false;


    DateTime lastSyncTime = DateTime.MinValue;

    #endregion

    #region public members 
    public Border Popup = new();
    public LivePreviewer previewer = new();
    public ClipInfoBuilder infoBuilder;
    public InteractableEditor.InteractableEditor ClipEditor;
    public InteractableEditor.DynamicPreview DynamicPreviewProvider;
    public AIAssistance.AssistanceChatSessionsView ChatSessionsView = new();
    public ProjectAddClipView AddClipView = null!;
    public bool IsPopupClosableByTapBackground { get; set; } = true;

    public bool _ShouldShowClipMoveControlInCenterInfoBar => (UseCompactLayout ?? DeviceInfo.Idiom == DeviceIdiom.Phone) ? SelectedClip is null : true;
    public bool _ShouldShowCenterCompactControlGrid => (UseCompactLayout ?? DeviceInfo.Idiom == DeviceIdiom.Phone) ? SelectedClip is not null : false;
    public bool IsClipMoving
    {
        get;
        set
        {
            if (field == value)
            {
                return;
            }

            field = value;
            OnPropertyChanged(nameof(IsClipMoving));
        }
    } = false;
    public bool SelectedAnyClip => _selected is not null || _selectedClipIds.Count > 0;
    public ProjectJSONStructure ProjectInfo { get; set; }
    public ConcurrentDictionary<string, ClipElementUI> Clips = new();
    public ConcurrentDictionary<int, AbsoluteLayout> Tracks = new();
    public ConcurrentDictionary<string, AssetItem> Assets = new();
    public string WorkingPath { get; set; } = "";
    public event EventHandler<ClipUpdateEventArgs>? OnClipChanged;
    public double SecondsPerFrame { get; set; } = 1 / 30d;
    public double FramePerPixel { get; set; } = 1d;
    public uint ProjectDuration { get; set; } = 0;
    public bool MultiSelectEnabled { get; set; }
    public bool UseRealtimePreview { get; set; } = true;

    public ICommand AddCommand { get; private set; }
    public ICommand ExportCommand { get; private set; }
    public ICommand GoRenderCommand { get; private set; }
    public ICommand SettingsCommand { get; private set; }
    public ICommand UndoCommand { get; private set; }
    public ICommand RedoCommand { get; private set; }
    public ICommand SelectCommand { get; private set; }
    public ICommand UnselectCommand { get; private set; }
    public ICommand ShowClipInfoPanelCommand { get; private set; }
    public ICommand SpiltCommand { get; private set; }
    public ICommand SpiltOrCombineCommand { get; private set; }
    public ICommand MoveCommand { get; private set; }
    public ICommand CombineCommand { get; private set; }
    public ICommand DeleteCommand { get; private set; }
    public ICommand CopyCommand { get; private set; }
    public ICommand CutCommand { get; private set; }
    public ICommand PasteCommand { get; private set; }
    public ICommand DuplicateCommand { get; private set; }
    public ICommand SaveCommand { get; private set; }
    public ICommand GotoCommand { get; private set; }
    public ICommand ManageJobsCommand { get; private set; }
    public ICommand ClosePopupCommand { get; private set; }
    public ICommand PlayPauseCommand { get; private set; }
    public ICommand CleanRenderCacheCommand { get; private set; }
    public ICommand ArrowRightCommand { get; private set; }
    public ICommand ArrowLeftCommand { get; private set; }
    public ICommand ArrowUpCommand { get; private set; }
    public ICommand ArrowDownCommand { get; private set; }
    public ICommand ReturnCommand { get; private set; }
    public ICommand ExitNoSaveCommand { get; private set; }
    public ICommand ExitCommand { get; private set; }
    public ICommand ManageWindowCommand { get; private set; }
    public ICommand ResetMultiWindowViewCommand { get; private set; }
    public ICommand AddTransformToNeighborsCommand { get; private set; }
    public ICommand ZoomCommand { get; private set; }

    public ClipElementUI? SelectedClip => _selected;
    public event EventHandler? SelectedClipChanged;
    public bool UnNullUseCompactLayout => UseCompactLayout ?? DeviceInfo.Idiom == DeviceIdiom.Phone;
    #endregion

    #region options
#if WINDOWS
    public Accelerator? AcceleratorToUse { get; set; }
#endif

    public string ProjectName { get; set; } = "Unknown project";
    public bool ShowShadow { get; set; } = true;
    public bool LogUIMessageToLogger { get; set; } = false;
    public bool Denoise { get; set; } = false;
    public int MaximumSaveSlot { get; set; } = 8;
    public int CurrentSaveSlotIndex { get; set; } = 0;
    public bool IsReadonly { get; set; } = false;
    public string PreferredPopupMode { get; set; } = "right";
    public TimeSpan SyncCooldown { get; set; } = TimeSpan.FromMilliseconds(500);
    public bool AlwaysShowToolbarBtns { get; set; }
    public bool ShowBackendConsole { get; set; } = false;
    public int LiveVideoPreviewBufferLength { get; set; } = 50;
    public int LivePreviewResolutionFactor { get; set; } = 15;
    public int DefaultPreviewWidth { get; set; } = 1280;
    public int DefaultPreviewHeight { get; set; } = 720;
    public string ProxyOption { get; set; }
    public double PreviewAreaHeight { get; set; } = 250;
    public bool AutoSavePreviewAreaHeight { get; set; } = true;
    public bool? UseCompactLayout { get; set; } = null;
    public bool LockScrollViewAfterSelection { get; set; }
    public bool EnableClipInfoPopup { get; set; }
    public bool UseCommunityToolkitPopupInsteadOfOverlayLayer { get { return (OperatingSystem.IsMacCatalyst() || OperatingSystem.IsIOS()) || field; } set; }

    #endregion

    #region init
#pragma warning disable CS8618
    public DraftPage()
    {
        RegisterCommands();
        InitializeComponent();
        DisableTabFocusOnWindows(AddTrackButton);
        SetPlayPauseIconToPlay();
        SetStateBusy();
        SetStatusText(Localized.DraftPage_PleaseWait);
        ClipEditor = new InteractableEditor.InteractableEditor { IsVisible = true, HorizontalOptions = LayoutOptions.Fill, VerticalOptions = LayoutOptions.Fill };
        DynamicPreviewProvider = new InteractableEditor.DynamicPreview { IsVisible = false, HorizontalOptions = LayoutOptions.Fill, VerticalOptions = LayoutOptions.Fill, InputTransparent = true };
        ClipEditorHost.Content = ClipEditor;
        ClipEditor.SetRealtimePreviewContent(EnsureRealtimePreviewHost());
        ApplyClipEditorPreviewOverlayMode();
        ClipEditor.Init(OnClipEditorUpdate, 1920, 1080);
        ClipEditor.ConfigurePreviewRefresh(RefreshPreviewFromCurrentProviderAsync);
        ClipEditor.ConfigureOverlayClipTap(OnClipEditorOverlayTappedAsync);
        ClipEditor.ConfigureBlankAreaTap(OnClipEditorBlankAreaTappedAsync);
        HookPreviewSurfaceSizeSync();
        OverlayLayer.IsVisible = false;
#if ANDROID
        OverlayLayer.InputTransparent = false;
#endif
        ClipEditorHost.InputTransparent = false;

        TrackCalculator.HeightPerTrack = ClipHeight;
        infoBuilder = new ClipInfoBuilder(this);
        var page = this;
        AddClipView = new ProjectAddClipView(ref page);
        ChatSessionsView.GlobalToolCallFactories = AIAssistance.AITools.BuildToolCalls(ref page, OnClipPropertiesChanged);
        AddATrack(0);


    }

    public DraftPage(ProjectJSONStructure info, ConcurrentDictionary<string, ClipElementUI> clips, ConcurrentDictionary<string, AssetItem> assets, int initialTrackCount, string workingDir, string title = "Untitled draft", bool isReadonly = false)
#pragma warning restore CS8618
    {
        ArgumentNullException.ThrowIfNull(info, nameof(info));
        ArgumentNullException.ThrowIfNull(clips, nameof(clips));
        BindingContext = this;
        ProjectInfo = info;
        ProjectInfo.UserDefinedProperties ??= new();
        if (Directory.Exists(workingDir)) Environment.CurrentDirectory = workingDir;
        RegisterCommands();
        InitializeComponent();
        DisableTabFocusOnWindows(AddTrackButton);
        SetPlayPauseIconToPlay();
        SetStateBusy();
        SetStatusText(Localized.DraftPage_PleaseWait);
        ClipEditor = new InteractableEditor.InteractableEditor { IsVisible = true, HorizontalOptions = LayoutOptions.Fill, VerticalOptions = LayoutOptions.Fill };
        DynamicPreviewProvider = new InteractableEditor.DynamicPreview { IsVisible = true, HorizontalOptions = LayoutOptions.Fill, VerticalOptions = LayoutOptions.Fill, InputTransparent = true };
        ClipEditorHost.Content = ClipEditor;
        ClipEditor.SetRealtimePreviewContent(EnsureRealtimePreviewHost());
        ApplyClipEditorPreviewOverlayMode();
        ClipEditor.Init(OnClipEditorUpdate, 1920, 1080);
        ClipEditor.ConfigurePreviewRefresh(RefreshPreviewFromCurrentProviderAsync);
        ClipEditor.ConfigureOverlayClipTap(OnClipEditorOverlayTappedAsync);
        ClipEditor.ConfigureBlankAreaTap(OnClipEditorBlankAreaTappedAsync);
        HookPreviewSurfaceSizeSync();
        OverlayLayer.IsVisible = false;
#if ANDROID
        OverlayLayer.InputTransparent = false;
#endif
        var page = this;
        infoBuilder = new ClipInfoBuilder(this);
        ChatSessionsView = new AIAssistance.AssistanceChatSessionsView();
        ChatSessionsView.GlobalToolCallFactories = AIAssistance.AITools.BuildToolCalls(ref page, OnClipPropertiesChanged);
        AddClipView = new ProjectAddClipView(ref page);
        WorkingPath = workingDir;
        TrackCalculator.HeightPerTrack = ClipHeight;

        Clips = clips;
        Assets = assets ?? new();
        Tracks = new ConcurrentDictionary<int, AbsoluteLayout>();

        trackCount = initialTrackCount;
        var maxMainTrack = Clips.Values.Where(c => c.origTrack < SubTrackOffset).Select(c => c.origTrack ?? 0).DefaultIfEmpty(0).Max();
        for (int i = 0; i <= maxMainTrack; i++)
        {
            AddATrack(i);
        }
        if (Clips.Values.Any(c => c.origTrack >= SubTrackOffset))
        {
            var maxSubTrack = Clips.Values.Where(c => c.origTrack >= SubTrackOffset).Select(c => c.origTrack ?? 0).DefaultIfEmpty(SubTrackOffset).Max();
            for (int i = SubTrackOffset; i <= maxSubTrack; i++)
            {
                AddASubTrack(i);
            }
        }


        foreach (var kv in Clips.OrderBy(kv => kv.Value.origTrack ?? 0).ThenBy(kv => kv.Value.origX))
        {
            var item = kv.Value;
            int t = item.origTrack ?? 0;
            if (!Tracks.ContainsKey(t)) AddATrack(t);
            AddAClip(item);
            RegisterClip(item, true);
        }

        ProjectInfo.ProjectName ??= title;
        SecondsPerFrame = 1d / ProjectInfo.TargetFrameRate;
        IsReadonly = isReadonly;
    }

    private void HookPreviewSurfaceSizeSync()
    {
        ClipEditorHost.SizeChanged += PreviewSurface_SizeChanged;
        SyncPreviewSurfaceSize();
    }

    private void PreviewSurface_SizeChanged(object? sender, EventArgs e)
    {
        SyncPreviewSurfaceSize();
    }

    private void SyncPreviewSurfaceSize()
    {
        var width = ClipEditorHost.Width;
        var height = ClipEditorHost.Height;

        if (width <= 0 || height <= 0)
        {
            width = ClipEditor.Width;
            height = ClipEditor.Height;
        }

        if (width <= 0 || height <= 0)
        {
            return;
        }

        ClipEditor.UpdateCanvasSize(width, height);
        DynamicPreviewProvider.UpdateCanvasSize(width, height);
    }

    private void ApplyClipEditorPreviewOverlayMode()
    {
        ClipEditor.ShowRenderRectOverlay = UseRealtimePreview;
        ClipEditor.ShowClipPreviewOverlays = UseRealtimePreview;
    }

    private void RegisterCommands()
    {
        AddCommand = new Command(() => AddClip_Clicked(this, EventArgs.Empty));
        ExportCommand = new Command(() => OnExportedClick(this, EventArgs.Empty));
        GoRenderCommand = new Command(() => OnExportedClick(this, EventArgs.Empty));
        SettingsCommand = new Command(() => SettingsClick(this, EventArgs.Empty));
        UndoCommand = new Command(() => UndoChanges());
        RedoCommand = new Command(() => RedoChanges());
        SelectCommand = new Command(async () => await SelectAClip());
        UnselectCommand = new Command(() => UnSelectTapGesture_Tapped(this, null!));
        ShowClipInfoPanelCommand = new Command(async () => { if (_selectedClipIds.Count == 1) await ShowAPopup(clip: _selected, border: _selected?.Clip); else SetStateFail(Localized.DraftPage_SelectExactlyOneToContinue); });
        SpiltCommand = new Command(() => Split_Clicked(this, EventArgs.Empty));
        SpiltOrCombineCommand = new Command(async () => { if (MultiSelectEnabled) await CombineSelection(); else Split_Clicked(this, null!); });
        CombineCommand = new Command(async () => await CombineSelection());
        MoveCommand = new Command(async () => await MoveSelection());
        DeleteCommand = new Command(() => DeleteAClip());
        CopyCommand = new Command(() => CaptureSelectionToClipboard(false));
        CutCommand = new Command(async () => await CutSelectionAsync());
        PasteCommand = new Command(async () => await PasteClipboardAsync());
        DuplicateCommand = new Command(async () => await DuplicateSelectionAsync());
        SaveCommand = new Command(() => OnRefreshButtonClicked(this, EventArgs.Empty));
        GotoCommand = new Command(async () => await GotoButtonClicked());
        ManageJobsCommand = new Command(async () => await OnManageJobsClicked());
        PlayPauseCommand = new Command(async () => PlayPauseButton_Clicked(this, EventArgs.Empty));
        CleanRenderCacheCommand = new Command(async () => await CleanRenderCache());
        ArrowLeftCommand = new Command(async () => await HandleMoveArrowAsync(-SnapGridPixels, 0));
        ArrowRightCommand = new Command(async () => await HandleMoveArrowAsync(SnapGridPixels, 0));
        ArrowUpCommand = new Command(async () => await HandleMoveArrowAsync(0, -1));
        ArrowDownCommand = new Command(async () => await HandleMoveArrowAsync(0, 1));
        ReturnCommand = new Command(async () => await ConfirmKeyboardMoveAsync());
        ExitNoSaveCommand = new Command(async () => await ExitButNoSave());
        ExitCommand = new Command(async () => await Navigation.PopToRootAsync());
        ManageWindowCommand = new Command<string?>(ExecuteManageWindowCommand);
        ResetMultiWindowViewCommand = new Command(() => ResetLayout());
        ZoomCommand = new Command<string?>(async (param) => await Task.FromResult(param switch { "+" => PerformZoom(1.2), "0" => PerformZoom(1.0 / tracksZoomOffest), "-" => PerformZoom(1.0 / 1.2), _ => -1 }));
        ClosePopupCommand = new Command(async () => { if (IsClipMoving) { CancelPendingClipPlacement(); SetStateOK(); SetStatusText(Localized.DraftPage_Tasks_Status_Canceled); } else if (_pendingClipPlacementFactory is not null) { CancelPendingClipPlacement(); } else { await HidePopup(); } });

    }

    private static void DisableTabFocusOnWindows(View? view)
    {
#if WINDOWS
        if (view is null)
        {
            return;
        }

        void Apply()
        {
            if (view.Handler?.PlatformView is Microsoft.UI.Xaml.FrameworkElement fe)
            {
                fe.IsTabStop = false;
            }
        }

        view.HandlerChanged += (_, _) => Apply();
        Apply();
#endif
    }

    private bool Inited = false;

    private void SetPlayPauseIconToPlay()
    {
        PlayPauseButton.FontFamily = MaterialIconFontFamily;
        PlayPauseButton.Text = MaterialIconPlay;
    }

    private void SetPlayPauseIconToPause()
    {
        PlayPauseButton.FontFamily = MaterialIconFontFamily;
        PlayPauseButton.Text = MaterialIconPause;
    }

    private void SetPlayPauseIconToClose()
    {
        PlayPauseButton.FontFamily = MaterialIconFontFamily;
        PlayPauseButton.Text = MaterialIconClose;
    }

    public async Task PostInit()
    {
        if (Inited) return;
        Inited = true;

        ApplyClipEditorPreviewOverlayMode();

        previewWidth = ProjectInfo.RelativeWidth;
        previewHeight = ProjectInfo.RelativeHeight;
        previewer.ProjectRelativeWidth = ProjectInfo.RelativeWidth;
        previewer.ProjectRelativeHeight = ProjectInfo.RelativeHeight;
        previewer.TempPath = Path.Combine(WorkingPath, "thumbs");
        DynamicPreviewProvider.SetLivePreviewer(ref previewer!);
        ClipEditor.UpdateVideoResolution(ProjectInfo.RelativeWidth, ProjectInfo.RelativeHeight);

        ProjectInfo.NormallyExited = false;

        nopGesture.Tapped += (s, e) =>
        {
#if ANDROID
            OverlayLayer.IsVisible = false;
#endif
        };


        if (!string.IsNullOrWhiteSpace(WorkingPath))
        {
            foreach (var item in DirectoriesNeeded)
            {
                Directory.CreateDirectory(Path.Combine(WorkingPath, item));
            }
        }

#if ANDROID
        ComputerHelper.AddGLViewHandler = new((v) =>
        {
            ComputeView.Children.Clear();
            v.WidthRequest = 50;
            v.HeightRequest = 50;
            ComputeView.Children.Add(v);

        });
#elif iDevices
        MetalComputerHelper.RegisterComputerBridge();
#elif WINDOWS
        if (AcceleratorToUse is null) throw new InvalidDataException($"Please specific a accelerator.");
        projectFrameCut.Render.WindowsRender.ILGPUPlugin.accelerators = [AcceleratorToUse];
#endif

        await Dispatcher.DispatchAsync(() =>
        {
            if (!UseRealtimePreview)
            {
                var resString = $"{ProjectInfo.RelativeWidth}x{ProjectInfo.RelativeHeight}";
                if (ResolutionPicker.ItemsSource is List<string> list && list.Contains(resString))
                {
                    ResolutionPicker.SelectedItem = resString;
                }
            }

            rulerTapGesture.Tapped += PlayheadTapped;
            OnClipChanged += DraftChanged;
            UpdatePlayheadPosition();
            Loaded += DraftPage_Loaded;
            if (this.Window is not null)
            {
                this.Window.SizeChanged += Window_SizeChanged;
            }
        });
    }

    private async void DraftPage_Loaded(object? sender, EventArgs e)
    {
        if (Debugger.IsAttached) MyLoggerExtensions.OnExceptionLog += MyLoggerExtensions_OnExceptionLog; //user don't want to see a lot of confused error message

        if (UpperContent.Children[0] is Grid previewGrid && PreviewAreaHeight > 100)
        {
            if (AutoSavePreviewAreaHeight)
            {
                var heightString = SettingsManager.GetSetting("Edit_UpperContentHeight", "250");
                var height = double.TryParse(heightString, out var h1) ? h1 : 250;
                previewGrid.HeightRequest = height;
            }
            else
            {
                previewGrid.HeightRequest = PreviewAreaHeight;
            }

            // MultiWindowView uses translation + WidthRequest for window layout; forcing Grid columns
            // here shifts window origins and can push snapped windows outside of the viewport.
            if (previewGrid is not MultiWindowView && previewGrid.ColumnDefinitions.Count < 2)
            {
                previewGrid.ColumnDefinitions.Clear();
                previewGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
                previewGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            }
            else if (previewGrid is not MultiWindowView)
            {
                previewGrid.ColumnDefinitions[0].Width = new GridLength(1, GridUnitType.Star);
                previewGrid.ColumnDefinitions[1].Width = new GridLength(1, GridUnitType.Star);

                int colIndex = 0;
                foreach (var child in previewGrid.Children)
                {
                    if (child.GetType().Name == "MultiWindowItem" && colIndex < 2)
                    {
                        Grid.SetColumn((BindableObject)child, colIndex);
                        colIndex++;
                    }
                }
            }
        }

        PlayheadLine.TranslationX = TrackHeadLayout.Width;
        if (AlwaysShowToolbarBtns || !OperatingSystem.IsWindows()) AddToolbarBtns();
        if (Width < Height) RightMenuBar.IsVisible = false;

        RulerLayout.GestureRecognizers.Add(rulerTapGesture);
        PanGestureRecognizer rulerPanGesture = new();
        rulerPanGesture.PanUpdated += RulerPanUpdated;
        RulerLayout.GestureRecognizers.Add(rulerPanGesture);

        PlayheadLine.HeightRequest = Tracks.Count * ClipHeight;
        Window.SizeChanged += Window_SizeChanged;
        var bgTap = new TapGestureRecognizer();
        bgTap.Tapped += async (s, e) => await HidePopup();
        OverlayLayer.GestureRecognizers.Clear();
        OverlayLayer.GestureRecognizers.Add(bgTap);

        ResolutionPicker.ItemsSource = new List<string> {
                Localized.DraftPage_DynamicPreview,
                "1280x720",
                "1920x1080",
                "2560x1440",
                "3840x2160",
                "7680x4320",
                Localized.DraftPage_PrevResultion_Custom
                };

        ResolutionPicker.SelectedIndex = UseRealtimePreview ? 0 : 1;

        var w = this.Window?.Width ?? 0;
        var h = this.Window?.Height ?? 0;
        if (w > 0 && h > 0)
        {
            WindowSize = new Size(w, h);
        }

        var safeZoneRad = UIServices.GetSafeZone();
        StatusBarGrid.Margin = new Thickness(safeZoneRad, StatusBarGrid.Margin.Top, safeZoneRad, StatusBarGrid.Margin.Bottom);
        UseCompactLayout ??= (DeviceInfo.Idiom == DeviceIdiom.Phone);
        if (UseCompactLayout ?? (DeviceInfo.Idiom == DeviceIdiom.Phone))
        {
            MainMultiWindowView.CloseWindow(PropertiesSubwindow);
            MainMultiWindowView.CloseWindow(AssisstantSubWindow);
            PreviewSubwindow.IsTitleBarVisible = false;
            PreviewSubwindow.IsResizable = false;
            PreviewSubwindow.Maximize();
            RightMenuBar.IsVisible = false;
            RightContentBorder.IsVisible = false;
            //SpiltButton.IsVisible = false;
            PlayingControlLayout.HorizontalOptions = LayoutOptions.End;
            MainControlGrid.ColumnDefinitions = new ColumnDefinitionCollection
            {
                new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) },
                new ColumnDefinition { Width = new GridLength(1, GridUnitType.Auto) }
            };
            Grid.SetColumn(LeftMenuBar, 0);
            Grid.SetColumn(PlayingControlLayout, 1);
            PlayingControlLayout.Margin = new(0, 0, 8, 0);
            var c = MultiSelectControlLayout;
            RightMenuBar.Remove(c);
            LeftMenuBar.Add(c);
            MultiSelectControlLayout.Margin = new(8, 0, 0, 0);
        }
        else
        {
            PreviewSubwindow.IsTitleBarVisible = true;
            RightMenuBar.IsVisible = true;
            RightContentBorder.IsVisible = true;

            foreach (var item in MainMultiWindowView.Windows)
            {
                ViewMenuBarItem.Insert(0, new MenuFlyoutItem { Text = item.Title, Command = ManageWindowCommand, CommandParameter = item.WindowID.ToString() });

            }
            MainMultiWindowView.WindowAdded += (s, e) =>
            {
                ViewMenuBarItem.Insert(0, new MenuFlyoutItem { Text = e.Title, Command = ManageWindowCommand, CommandParameter = e.WindowID.ToString() });
            };
            MainMultiWindowView.WindowClosed += (s, e) =>
            {
                try
                {
                    var wid = e.WindowID.ToString();
                    if (ViewMenuBarItem.OfType<MenuFlyoutItem>().Where(c => c?.CommandParameter is string s && s == wid) is IEnumerable<MenuFlyoutItem> items)
                    {
                        foreach (var item in items.ToList())
                        {
                            ViewMenuBarItem.Remove(item);
                        }
                    }
                }
                catch (Exception ex)
                {
                    Log(ex, "Handle window change", this);
                }
            };

        }

        UpdateTrackHeaderLayoutForViewport();
#if WINDOWS
        if (TimelineScrollView.Handler?.PlatformView is Microsoft.UI.Xaml.Controls.ScrollViewer sv)
        {
            sv.PointerWheelChanged -= OnTimelineScrollViewPointerWheelChanged;
            sv.PointerWheelChanged += OnTimelineScrollViewPointerWheelChanged;
        }
        if (SubTimelineScrollView.Handler?.PlatformView is Microsoft.UI.Xaml.Controls.ScrollViewer ssv)
        {
            ssv.PointerWheelChanged -= OnTimelineScrollViewPointerWheelChanged;
            ssv.PointerWheelChanged += OnTimelineScrollViewPointerWheelChanged;
        }
#endif
        PreviewSubwindow.IsClosable = false;
        PropertiesSubwindow.IsClosable = false;

        PropertiesSubwindow.HorizontalOptions = LayoutOptions.Fill;
        PropertiesSubwindow.VerticalOptions = LayoutOptions.Fill;
        fileDropGesture.AllowDrop = true;
        fileDropGesture.DragOver += File_DragOver;
        fileDropGesture.Drop += File_Drop;
        if (!Tracks.Any()) AddATrack(0);
        UpdatePlayheadHeight();

        AssisstantSubWindow.Content = ChatSessionsView;
        MainMultiWindowView.CloseWindow(AssisstantSubWindow);
        HistorySubWindow.Content = new DraftSettingPage(this).BuildHistoryTab();
        MainMultiWindowView.CloseWindow(HistorySubWindow);
        //TryRestoreMainMultiWindowViewState();
        ApplyDefaultMainMultiWindowLayout();
        AddClipView.ClipAdded += async (s, args) =>
        {
            this.Popup.Content = null;
            _transformMenuActivatedCenterClip = null;
            _transformMenuActivatedHandle = "none";
            await HidePopup();
        };
        OnPropertyChanged(nameof(UnNullUseCompactLayout));
        OnPropertyChanged(nameof(_ShouldShowClipMoveControlInCenterInfoBar));
        OnPropertyChanged(nameof(_ShouldShowCenterCompactControlGrid));
        DraftChanged(sender, new ClipUpdateEventArgs { NoSave = true });
        SetStateOK();
        SetStatusText(Localized.DraftPage_EverythingFine);
    }

    private string? GetMainMultiWindowItemKey(MultiWindowItem window)
    {
        if (ReferenceEquals(window, PreviewSubwindow)) return nameof(PreviewSubwindow);
        if (ReferenceEquals(window, PropertiesSubwindow)) return nameof(PropertiesSubwindow);
        if (ReferenceEquals(window, AssisstantSubWindow)) return nameof(AssisstantSubWindow);
        return null;
    }

    private bool TryGetMainMultiWindowItemByKey(string? key, out MultiWindowItem window)
    {
        switch (key)
        {
            case nameof(PreviewSubwindow):
                window = PreviewSubwindow;
                return true;
            case nameof(PropertiesSubwindow):
                window = PropertiesSubwindow;
                return true;
            case nameof(AssisstantSubWindow):
                window = AssisstantSubWindow;
                return true;
            default:
                window = PreviewSubwindow;
                return false;
        }
    }

    private static bool IsWindowLikelyMinimized(MultiWindowItem window)
        => window.HeightRequest > 0 && window.HeightRequest <= 40;

    private static bool IsWindowLikelyMaximized(MultiWindowItem window)
        => window.HorizontalOptions.Alignment == LayoutAlignment.Fill
           && window.VerticalOptions.Alignment == LayoutAlignment.Fill
           && window.WidthRequest <= 0
           && window.HeightRequest <= 0;

    private MainMultiWindowStateEnvelope CaptureMainMultiWindowState()
    {
        var state = new MainMultiWindowStateEnvelope();
        var windows = new[] { PreviewSubwindow, PropertiesSubwindow, AssisstantSubWindow };

        foreach (var window in windows)
        {
            var key = GetMainMultiWindowItemKey(window);
            if (string.IsNullOrWhiteSpace(key))
            {
                continue;
            }

            var isOpen = MainMultiWindowView.Children.Contains(window);
            state.Windows.Add(new MainMultiWindowWindowState
            {
                WindowKey = key,
                IsOpen = isOpen,
                IsVisible = window.IsVisible,
                IsMaximized = IsWindowLikelyMaximized(window),
                IsMinimized = IsWindowLikelyMinimized(window),
                TranslationX = window.TranslationX,
                TranslationY = window.TranslationY,
                WidthRequest = window.WidthRequest,
                HeightRequest = window.HeightRequest,
                Column = Grid.GetColumn(window),
                Row = Grid.GetRow(window),
                ColumnSpan = Grid.GetColumnSpan(window),
                RowSpan = Grid.GetRowSpan(window),
                ZIndex = window.ZIndex
            });
        }

        state.ActiveWindowKey = MainMultiWindowView.ActiveWindow is MultiWindowItem active
            ? GetMainMultiWindowItemKey(active)
            : null;

        return state;
    }

    private void SaveMainMultiWindowViewStateToProjectInfo()
    {
        try
        {
            ProjectInfo.UserDefinedProperties ??= new();
            var state = CaptureMainMultiWindowState();
            ProjectInfo.UserDefinedProperties[MainMultiWindowViewStatePropertyKey] = JsonSerializer.Serialize(state, savingOpts);
        }
        catch (Exception ex)
        {
            Log(ex, "Save MainMultiWindowView state", this);
        }
    }

    private void TryRestoreMainMultiWindowViewState()
    {
        if (_hasTriedRestoreMainMultiWindowViewState)
        {
            return;
        }

        _hasTriedRestoreMainMultiWindowViewState = true;

        if (UseCompactLayout ?? (DeviceInfo.Idiom == DeviceIdiom.Phone))
        {
            return;
        }

        if (!(ProjectInfo.UserDefinedProperties?.TryGetValue(MainMultiWindowViewStatePropertyKey, out var rawState) ?? false)
            || string.IsNullOrWhiteSpace(rawState))
        {
            return;
        }

        try
        {
            var state = JsonSerializer.Deserialize<MainMultiWindowStateEnvelope>(rawState, savingOpts);
            if (state is null)
            {
                return;
            }

            foreach (var item in state.Windows)
            {
                if (!TryGetMainMultiWindowItemByKey(item.WindowKey, out var window))
                {
                    continue;
                }

                if (!item.IsOpen)
                {
                    if (window.IsClosable && MainMultiWindowView.Children.Contains(window))
                    {
                        MainMultiWindowView.CloseWindow(window);
                    }
                    continue;
                }

                if (!MainMultiWindowView.Children.Contains(window))
                {
                    MainMultiWindowView.AddWindow(window);
                }

                window.HorizontalOptions = LayoutOptions.Start;
                window.VerticalOptions = LayoutOptions.Start;

                window.TranslationX = Math.Max(0, item.TranslationX);
                window.TranslationY = Math.Max(0, item.TranslationY);
                window.WidthRequest = item.WidthRequest;
                window.HeightRequest = item.HeightRequest;
                Grid.SetColumn(window, Math.Max(0, item.Column));
                Grid.SetRow(window, Math.Max(0, item.Row));
                Grid.SetColumnSpan(window, Math.Max(1, item.ColumnSpan));
                Grid.SetRowSpan(window, Math.Max(1, item.RowSpan));
                window.ZIndex = item.ZIndex;
                window.IsVisible = item.IsVisible;

                if (item.IsMaximized)
                {
                    window.Maximize();
                }

                if (item.IsMinimized && !IsWindowLikelyMinimized(window))
                {
                    window.Minimize();
                }
            }

            if (TryGetMainMultiWindowItemByKey(state.ActiveWindowKey, out var active)
                && MainMultiWindowView.Children.Contains(active))
            {
                MainMultiWindowView.BringToFront(active);
            }
        }
        catch (Exception ex)
        {
            Log(ex, "Restore MainMultiWindowView state", this);
        }
    }

    private void ApplyDefaultMainMultiWindowLayout()
    {
        if (_hasAppliedDefaultMainMultiWindowLayout)
        {
            return;
        }

        if (UseCompactLayout ?? (DeviceInfo.Idiom == DeviceIdiom.Phone))
        {
            return;
        }

        if (MainMultiWindowView.Width <= 0 || MainMultiWindowView.Height <= 0)
        {
            return;
        }

        if (!MainMultiWindowView.Children.Contains(PreviewSubwindow) || !MainMultiWindowView.Children.Contains(PropertiesSubwindow))
        {
            return;
        }

        PreviewSubwindow.IsTitleBarVisible = true;
        PreviewSubwindow.IsResizable = false;
        PropertiesSubwindow.HorizontalOptions = LayoutOptions.Fill;
        PropertiesSubwindow.VerticalOptions = LayoutOptions.Fill;

        if (MainMultiWindowView.SnapWindow(PreviewSubwindow, WindowSnapZone.LeftHalf, bringToFront: false)
            & MainMultiWindowView.SnapWindow(PropertiesSubwindow, WindowSnapZone.RightHalf))
        {
            MainMultiWindowView.BringToFront(PropertiesSubwindow);
            _hasAppliedDefaultMainMultiWindowLayout = true;
        }
    }

    #endregion

    #region add stuff
    public ClipElementUI CreateAndAddClip(
        double startX,
        double width,
        int trackIndex,
        string? id = null,
        string? labelText = null,
        Brush? background = null,
        Border? prototype = null,
        bool resolveOverlap = true,
        uint relativeStart = 0,
        uint maxFrames = 0,
        ClipElementUI? sourceElement = null)
    {
        if (!Tracks.ContainsKey(trackIndex))
            throw new ArgumentOutOfRangeException(nameof(trackIndex));

        var element = ClipElementUI.CreateClip(startX, width, trackIndex, id, labelText, background, prototype, relativeStart, maxFrames);
        if (sourceElement is not null)
        {
            element.ClipType = sourceElement.ClipType;
            element.FromPlugin = sourceElement.FromPlugin;
            element.SecondPerFrameRatio = sourceElement.SecondPerFrameRatio;
            element.SourcePath = sourceElement.SourcePath;
            element.maxFrameCount = sourceElement.maxFrameCount;
            element.isInfiniteLength = sourceElement.isInfiniteLength;
            element.ExtraData = sourceElement.ExtraData;

        }
        element.ApplySpeedRatio();
        RegisterClip(element, resolveOverlap);
        AddAClip(element);

        return element;
    }

    public ClipElementUI CreateFromAsset(AssetItem asset, int trackIndex, string fromPlugin = InternalPluginBase.InternalPluginBaseID, string? path = null)
    {
        var elem = ClipElementUI.CreateClip(
                           startX: 0,
                           width: FrameToPixel(asset.isInfiniteLength ? SettingsManager.GetSettingAs<uint>("Edit_DefaultInfLengthClipLength", 300, 300) : AssetDatabase.DetermineLengthInFrame(asset, ProjectInfo.TargetFrameRate)),
                           trackIndex: trackIndex,
                           labelText: asset.Name,
                           background: ClipElementUI.DetermineAssetColor(asset.AssetType, asset.GetClipMode()),
                           maxFrames: AssetDatabase.DetermineLengthInFrame(asset, ProjectInfo.TargetFrameRate),
                           relativeStart: 0
                          );

        elem.SourcePath = path ?? $"${asset.AssetId}";
        elem.ClipType = asset.GetClipMode();
        elem.FromPlugin = fromPlugin;
        elem.sourceSecondPerFrame = asset.SecondPerFrame;
        elem.SecondPerFrameRatio = 1f;
        elem.ExtraData = new();
        if (asset.IsAIGenerated)
        {
            elem.ExtraData["IsAI"] = true;
        }
        return elem;
    }

    public ClipElementUI CreateFromAsset(AssetItem asset, int trackIndex, double startX, string fromPlugin = InternalPluginBase.InternalPluginBaseID, string? path = null)
    {
        var elem = CreateFromAsset(asset, trackIndex, fromPlugin, path);
        elem.origX = startX;
        elem.Clip.TranslationX = startX;
        return elem;
    }

    public void RegisterClip(ClipElementUI element, bool resolveOverlap)
    {
        var cid = element.Id;

        var clipPanGesture = new PanGestureRecognizer();
        clipPanGesture.PanUpdated += (s, e) => ClipPaned(element.Clip, e);

        var rightHandleGesture = new PanGestureRecognizer();
        rightHandleGesture.PanUpdated += (s, e) => RightHandlePaned(element.RightHandle, e);

        var leftHandleGesture = new PanGestureRecognizer();
        leftHandleGesture.PanUpdated += (s, e) => LeftHandlePanded(element.LeftHandle, e);

        var rightHandleClickGesture = new TapGestureRecognizer
        {
            NumberOfTapsRequired = 2
        };
        rightHandleClickGesture.Tapped += (s, e) => HandleTransformAdd(element, false, true);
        var leftHandleClickGesture = new TapGestureRecognizer
        {
            NumberOfTapsRequired = 2
        };
        leftHandleClickGesture.Tapped += (s, e) => HandleTransformAdd(element, true, false);

        var selectTapGesture = new TapGestureRecognizer
        {
            Buttons = ButtonsMask.Primary
        };
        selectTapGesture.Tapped += SelectTapGesture_Tapped;

        var contextSelectTapGesture = new TapGestureRecognizer
        {
            Buttons = ButtonsMask.Secondary
        };
        contextSelectTapGesture.Tapped += ContextSelectTapGesture_Tapped;

        var doubleTapGesture = new TapGestureRecognizer
        {
            NumberOfTapsRequired = 2
        };
        doubleTapGesture.Tapped += DoubleTapGesture_Tapped;

        element.Clip.GestureRecognizers.Add(clipPanGesture);
        element.Clip.GestureRecognizers.Add(selectTapGesture);
        element.Clip.GestureRecognizers.Add(contextSelectTapGesture);
        element.LeftHandle.GestureRecognizers.Add(leftHandleGesture);
        element.RightHandle.GestureRecognizers.Add(rightHandleGesture);
        element.LeftHandle.GestureRecognizers.Add(leftHandleClickGesture);
        element.RightHandle.GestureRecognizers.Add(rightHandleClickGesture);
        element.Clip.GestureRecognizers.Add(doubleTapGesture);




        // compute X
        if (resolveOverlap)
        {
            double snapped = SnapPixels(element.origX);
            element.Clip.TranslationX = ResolveOverlapStartPixels(element.origTrack ?? 0, cid, snapped, element.origLength);
        }

        Clips.AddOrUpdate(element.Id, element, (_, _) => element);
    }

    public void AddAClip(ClipElementUI c)
    {
        if (c.origTrack is null)
            throw new ArgumentNullException(nameof(c.origTrack));
        if (!Tracks.ContainsKey(c.origTrack ?? 0))
            throw new ArgumentOutOfRangeException(nameof(c.origTrack));

        c.Clip.IsVisible = c.ShouldDisplayInUI;
        Tracks[c.origTrack ?? 0].Children.Add(c.Clip);
        _ = UpdateAdjacencyForTrack();
        UpdateTimelineWidth();
    }

    private void Split_Clicked(object sender, EventArgs e)
    {
        var clip = _selected;
        if (clip is null || _selectedClipIds.Count != 1)
        {
            SetStatusText(Localized.DraftPage_OnlyAvailableWhenOneSelection);
            return;
        }
        try
        {
            // Absolute X of playhead relative to page root
            var playheadAbs = GetAbsolutePosition(PlayheadLine, null);
            var contentAbs = GetAbsolutePosition(TrackContentLayout, null);
            double playheadXInContent = playheadAbs.X - contentAbs.X;

            var border = clip.Clip;
            double clipStartX = border.TranslationX;
            double clipWidth = (border.WidthRequest > 0) ? border.WidthRequest : ((border.Width > 0) ? border.Width : border.WidthRequest);
            double clipEndX = clipStartX + clipWidth;

            if (playheadXInContent <= clipStartX + 1 || playheadXInContent >= clipEndX - 1)
            {
                SetStatusText("Playhead not inside selected clip");
                return;
            }

            double leftWidth = Math.Max(MinClipWidth, playheadXInContent - clipStartX);
            double rightWidth = Math.Max(MinClipWidth, clipEndX - playheadXInContent);

            border.WidthRequest = leftWidth;

            int trackIdx = clip.origTrack ?? Tracks.Keys.Max();
            uint framesOffset = (uint)Math.Round(leftWidth * FramePerPixel * tracksZoomOffest);

            _ = CreateAndAddClip(
                startX: playheadXInContent,
                width: rightWidth,
                trackIndex: trackIdx,
                id: null,
                labelText: $"{clip.DisplayName} (2)",
                background: border.Background,
                prototype: border,
                resolveOverlap: true,
                // pass source total frames (or Infinity) so resize checks use frames
                maxFrames: clip.maxFrameCount,
                // relative start for right clip = original in-point + frames consumed by left clip
                relativeStart: (uint)(clip.relativeStartFrame + framesOffset),
                sourceElement: clip);

            UpdateAdjacencyForTrack();
            SetStatusText("Split done");
        }
        catch (Exception ex)
        {
            Log(ex, "Split_Clicked", this);
            SetStatusText("Split failed");
        }
        finally
        {
            UpdateAdjacencyForTrack();
        }
    }




    private static string GetClipNameForChangeReason(ClipElementUI? clip, string? fallbackId = null)
    {
        if (!string.IsNullOrWhiteSpace(clip?.DisplayName))
        {
            return clip.DisplayName.Trim();
        }

        if (!string.IsNullOrWhiteSpace(clip?.Id))
        {
            return clip.Id.Trim();
        }

        return string.IsNullOrWhiteSpace(fallbackId) ? "Unknown Clip" : fallbackId.Trim();
    }

    private void AddTrackButton_Clicked(object sender, EventArgs e)
    {
        int newId = Tracks.Keys.Where(k => k < SubTrackOffset).DefaultIfEmpty(-1).Max() + 1;
        AddATrack(newId);

        OnClipChanged?.Invoke(this, new ClipUpdateEventArgs
        {
            Reason = ClipUpdateReason.TrackAdd,
            SourceId = newId.ToString(),
            SourceName = $"Track {newId}",
            DetailInfo = ClipUpdateEventArgs.BuildChangeReason(ClipUpdateReason.TrackAdd, details: $"Track {newId} created")
        });
    }

    private int RecalculateMainTrackCount()
    {
        trackCount = Tracks.Keys.Count(k => k < SubTrackOffset);
        return trackCount;
    }

    private int? GetTrackIdFromHeaderBorder(Border? headerBorder)
    {
        if (headerBorder?.Content is not Grid grid)
        {
            return null;
        }

        var label = grid.Children.OfType<Label>().FirstOrDefault();
        return label?.BindingContext is int trackId ? trackId : null;
    }

    private void SetTrackIdToHeaderBorder(Border? headerBorder, int trackId)
    {
        if (headerBorder?.Content is not Grid grid)
        {
            return;
        }

        var label = grid.Children.OfType<Label>().FirstOrDefault();
        if (label is null)
        {
            return;
        }

        label.BindingContext = trackId;
        label.Text = BuildTrackHeaderText(trackId);
    }

    private void EnsureTrackExistsById(int trackId)
    {
        if (trackId < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(trackId));
        }

        while (!Tracks.ContainsKey(trackId))
        {
            if (trackId >= SubTrackOffset)
            {
                AddASubTrack(trackId);
            }
            else
            {
                AddATrack(trackId);
            }
        }
    }

    private void EnsureContinuousTrackIndices()
    {
        var remap = new Dictionary<int, int>();

        var mainContents = TrackContentLayout.Children.OfType<Border>().ToList();
        var mainHeaders = TrackHeadLayout.Children.OfType<Border>().ToList();
        for (int i = 0; i < mainContents.Count; i++)
        {
            var content = mainContents[i];
            var oldId = content.BindingContext is int oldTrackId ? oldTrackId : i;
            var newId = i;
            remap[oldId] = newId;

            content.BindingContext = newId;
            if (content.Content is AbsoluteLayout track)
            {
                track.BindingContext = newId;
            }

            if (i < mainHeaders.Count)
            {
                SetTrackIdToHeaderBorder(mainHeaders[i], newId);
            }
        }

        var subContents = SubTrackContentLayout.Children.OfType<Border>().ToList();
        var subHeaders = SubTrackHeadLayout.Children.OfType<Border>().ToList();
        for (int i = 0; i < subContents.Count; i++)
        {
            var content = subContents[i];
            var oldId = content.BindingContext is int oldTrackId ? oldTrackId : (SubTrackOffset + i);
            var newId = SubTrackOffset + i;
            remap[oldId] = newId;

            content.BindingContext = newId;
            if (content.Content is AbsoluteLayout track)
            {
                track.BindingContext = newId;
            }

            if (i < subHeaders.Count)
            {
                SetTrackIdToHeaderBorder(subHeaders[i], newId);
            }
        }

        var rebuiltTracks = new ConcurrentDictionary<int, AbsoluteLayout>();
        foreach (var content in mainContents.Concat(subContents))
        {
            if (content.BindingContext is int tid && content.Content is AbsoluteLayout track)
            {
                rebuiltTracks[tid] = track;
            }
        }
        Tracks = rebuiltTracks;

        foreach (var clip in Clips.Values)
        {
            if (clip.origTrack is int oldTrack && remap.TryGetValue(oldTrack, out var mappedTrack))
            {
                clip.origTrack = mappedTrack;
            }

            if (remap.TryGetValue(clip.SubLayerIndex, out var mappedSubLayer))
            {
                clip.SubLayerIndex = mappedSubLayer;
            }
        }

        foreach (var clipId in _keyboardMoveOriginalPlacement.Keys.ToList())
        {
            var placement = _keyboardMoveOriginalPlacement[clipId];
            if (remap.TryGetValue(placement.Track, out var mappedTrack))
            {
                _keyboardMoveOriginalPlacement[clipId] = (mappedTrack, placement.X);
            }
        }

        RecalculateMainTrackCount();
        UpdateTrackHeaderLayoutForViewport();
        UpdatePlayheadHeight();
        UpdateTimelineWidth();
        _ = UpdateAdjacencyForTrack();
    }

    private void RemoveTrackAndReindex(int trackId)
    {
        if (!Tracks.TryGetValue(trackId, out var trackLayout))
        {
            return;
        }

        var clipsToDelete = Clips.Values
            .Where(c => c is not null && c.origTrack == trackId)
            .ToList();

        foreach (var clip in clipsToDelete)
        {
            RemoveClipFromSelection(clip);
            trackLayout.Children.Remove(clip.Clip);
            Clips.TryRemove(clip.Id, out _);
            try
            {
                RemoveTransformsReferencingClip(clip.Id);
            }
            catch { }
        }

        if (trackId >= SubTrackOffset)
        {
            var head = SubTrackHeadLayout.Children
                .OfType<Border>()
                .FirstOrDefault(b => GetTrackIdFromHeaderBorder(b) == trackId);
            var content = SubTrackContentLayout.Children
                .OfType<Border>()
                .FirstOrDefault(b => b.BindingContext is int tid && tid == trackId);

            if (head is not null) SubTrackHeadLayout.Children.Remove(head);
            if (content is not null) SubTrackContentLayout.Children.Remove(content);
        }
        else
        {
            var head = TrackHeadLayout.Children
                .OfType<Border>()
                .FirstOrDefault(b => GetTrackIdFromHeaderBorder(b) == trackId);
            var content = TrackContentLayout.Children
                .OfType<Border>()
                .FirstOrDefault(b => b.BindingContext is int tid && tid == trackId);

            if (head is not null) TrackHeadLayout.Children.Remove(head);
            if (content is not null) TrackContentLayout.Children.Remove(content);
        }

        Tracks.TryRemove(trackId, out _);
        EnsureContinuousTrackIndices();
        _ = RefreshSelectionUiAsync();
    }

    public void AddATrack(int trackId)
    {
        if (trackId < 0)
        {
            trackId = 0;
        }

        if (trackId >= SubTrackOffset)
        {
            AddASubTrack(trackId);
            return;
        }

        var nextMainTrackId = Tracks.Keys.Where(k => k < SubTrackOffset).DefaultIfEmpty(-1).Max() + 1;
        if (trackId > nextMainTrackId)
        {
            for (int missing = nextMainTrackId; missing < trackId; missing++)
            {
                AddATrack(missing);
            }
        }

        if (Tracks.ContainsKey(trackId))
        {
            return;
        }

        ImageButton removeBtn = new ImageButton
        {
            Source = ImageHelper.LoadFromAsset("icon_remove"),
            WidthRequest = 16,
            HeightRequest = 16

        };
        DisableTabFocusOnWindows(removeBtn);

        ImageButton optsBtn = new ImageButton
        {
            Source = ImageHelper.LoadFromAsset("icon_option"),
            WidthRequest = 16,
            HeightRequest = 16,
            IsVisible = false //todo
        };

        Label label = new Label
        {
            Text = BuildTrackHeaderText(trackId),
            HorizontalOptions = LayoutOptions.Fill,
            VerticalOptions = LayoutOptions.Center,
            LineBreakMode = LineBreakMode.TailTruncation,
            MaxLines = 1,
            BindingContext = trackId
        };

        var grid = new Grid
        {
            ColumnDefinitions = new ColumnDefinitionCollection
            {
                new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) },
                new ColumnDefinition { Width = GridLength.Auto }
            },
            RowDefinitions = new RowDefinitionCollection
            {
                new RowDefinition { Height = GridLength.Auto }
            },
            Padding = 4
        };
        grid.Children.Add(label);
        grid.Children.Add(removeBtn);
        Grid.SetColumn(label, 0);
        Grid.SetColumn(removeBtn, 1);

        Border head = new Border
        {
            Content = grid,
            HeightRequest = 60.0,
            Margin = new Thickness(0.0, 0.0, 0.0, 2.0)
        };

        AbsoluteLayout track = new AbsoluteLayout();

        Border content = new Border
        {
            Content = track,
            HeightRequest = 60.0,
            Margin = new Thickness(0.0, 0.0, 0.0, 2.0),
            BindingContext = trackId
        };


        var UnselectTapGesture = new TapGestureRecognizer();
        UnselectTapGesture.Tapped += UnSelectTapGesture_Tapped;
        var timelineTrackTapGesture = new TapGestureRecognizer();
        timelineTrackTapGesture.Tapped += TimelineTrackTapGesture_Tapped;

        track.BindingContext = trackId;
        track.GestureRecognizers.Add(timelineTrackTapGesture);
        head.GestureRecognizers.Add(UnselectTapGesture);

        Tracks.AddOrUpdate(trackId, track, (_, _) => track);


        int currentTrack = trackId;



        removeBtn.Clicked += (s, e) =>
        {
            RemoveTrackAndReindex(currentTrack);
        };

        optsBtn.Clicked += (s, e) =>
        {
            //todo
        };




        TrackHeadLayout.Children.Insert(TrackHeadLayout.Count, head);
        TrackContentLayout.Children.Add(content);
        RecalculateMainTrackCount();
        UpdateTrackHeaderLayoutForViewport();
        UpdatePlayheadHeight();
    }

    public void AddASubTrack(int trackId)
    {
        if (trackId < SubTrackOffset)
        {
            trackId = SubTrackOffset;
        }

        var nextSubTrackId = Tracks.Keys.Where(k => k >= SubTrackOffset).DefaultIfEmpty(SubTrackOffset - 1).Max() + 1;
        if (trackId > nextSubTrackId)
        {
            for (int missing = nextSubTrackId; missing < trackId; missing++)
            {
                AddASubTrack(missing);
            }
        }

        if (Tracks.ContainsKey(trackId)) return;

        ImageButton removeBtn = new ImageButton
        {
            Source = ImageHelper.LoadFromAsset("icon_remove"),
            WidthRequest = 16,
            HeightRequest = 16

        };
        DisableTabFocusOnWindows(removeBtn);

        ImageButton optsBtn = new ImageButton
        {
            Source = ImageHelper.LoadFromAsset("icon_option"),
            WidthRequest = 16,
            HeightRequest = 16,
            IsVisible = false //todo
        };

        Label label = new Label
        {
            Text = BuildTrackHeaderText(trackId),
            HorizontalOptions = LayoutOptions.Fill,
            VerticalOptions = LayoutOptions.Center,
            LineBreakMode = LineBreakMode.TailTruncation,
            MaxLines = 1,
            BindingContext = trackId
        };

        var grid = new Grid
        {
            ColumnDefinitions = new ColumnDefinitionCollection
            {
                new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) },
                new ColumnDefinition { Width = GridLength.Auto }
            },
            RowDefinitions = new RowDefinitionCollection
            {
                new RowDefinition { Height = GridLength.Auto }
            },
            Padding = 4
        };
        grid.Children.Add(label);
        grid.Children.Add(removeBtn);
        Grid.SetColumn(label, 0);
        Grid.SetColumn(removeBtn, 1);

        Border head = new Border
        {
            Content = grid,
            HeightRequest = 60.0,
            Margin = new Thickness(0.0, 0.0, 0.0, 2.0)
        };

        AbsoluteLayout track = new AbsoluteLayout();

        var UnselectTapGesture = new TapGestureRecognizer();
        UnselectTapGesture.Tapped += UnSelectTapGesture_Tapped;
        var timelineTrackTapGesture = new TapGestureRecognizer();
        timelineTrackTapGesture.Tapped += TimelineTrackTapGesture_Tapped;

        track.BindingContext = trackId;
        track.GestureRecognizers.Add(timelineTrackTapGesture);
        head.GestureRecognizers.Add(UnselectTapGesture);

        int currentTrack = trackId;

        Tracks.AddOrUpdate(trackId, track, (_, _) => track);

        Border content = new Border
        {
            Content = track,
            HeightRequest = 60.0,
            Margin = new Thickness(0.0, 0.0, 0.0, 2.0),
            BindingContext = trackId
        };

        removeBtn.Clicked += (s, e) =>
        {
            RemoveTrackAndReindex(currentTrack);
        };

        optsBtn.Clicked += (s, e) =>
        {
            //todo
        };

        SubTrackHeadLayout.Children.Add(head);
        SubTrackContentLayout.Children.Add(content);
        UpdateTrackHeaderLayoutForViewport();
        UpdatePlayheadHeight();
    }

    private bool ShouldUseCompactTrackHeaderText()
    {
        var w = this.Window?.Width;
        if (w is not null && w > 0)
        {
            return (UseCompactLayout ?? DeviceInfo.Idiom == DeviceIdiom.Phone) || w <= NarrowTrackHeaderWindowThreshold;
        }

        return (UseCompactLayout ?? DeviceInfo.Idiom == DeviceIdiom.Phone) || (Width > 0 && Width <= NarrowTrackHeaderWindowThreshold);
    }

    private string BuildTrackHeaderText(int trackId)
    {
        if (ShouldUseCompactTrackHeaderText())
        {
            return trackId >= SubTrackOffset ? $"S{trackId - SubTrackOffset}" : $"#{trackId}";
        }

        return trackId >= SubTrackOffset
            ? Localized.DraftPage_Track_Sub(trackId - SubTrackOffset)
            : Localized.DraftPage_Track(trackId);
    }

    private void RefreshTrackHeaderTexts()
    {
        static void RefreshContainer(Layout layout, Func<int, string> textFactory)
        {
            foreach (var child in layout.Children)
            {
                if (child is not Border { Content: Grid grid }) continue;
                var label = grid.Children.OfType<Label>().FirstOrDefault();
                if (label?.BindingContext is int trackId)
                {
                    label.Text = textFactory(trackId);
                }
            }
        }

        RefreshContainer(TrackHeadLayout, BuildTrackHeaderText);
        RefreshContainer(SubTrackHeadLayout, BuildTrackHeaderText);
    }

    private void UpdateTrackHeaderLayoutForViewport()
    {
        if (MainTrackContentGrid?.ColumnDefinitions?.Count < 2 || SubTrackContentGrid?.ColumnDefinitions?.Count < 2)
        {
            return;
        }

        var compact = ShouldUseCompactTrackHeaderText();
        var headCol = compact
            ? new GridLength(NarrowTrackHeaderColumnWidth, GridUnitType.Absolute)
            : new GridLength(1, GridUnitType.Star);
        var contentCol = compact
            ? new GridLength(1, GridUnitType.Star)
            : new GridLength(8, GridUnitType.Star);

        MainTrackContentGrid.ColumnDefinitions[0].Width = headCol;
        MainTrackContentGrid.ColumnDefinitions[1].Width = contentCol;
        SubTrackContentGrid.ColumnDefinitions[0].Width = headCol;
        SubTrackContentGrid.ColumnDefinitions[1].Width = contentCol;
        RefreshTrackHeaderTexts();
    }

    private async void AddClip_Clicked(object sender, EventArgs e)
    {
        await (AddClipView.MainTabView.BindingContext as ProjectAddClipViewModel)?.Refresh();
        await ShowAPopup(AddClipView);
    }

    public void BeginClipPlacement(Func<int, double, ClipElementUI> clipFactory, Predicate<int>? trackFilter = null, string? name = null)
    {
        ArgumentNullException.ThrowIfNull(clipFactory);
        _pendingClipPlacementName = name;
        _pendingClipPlacementFactory = clipFactory;
        _pendingClipPlacementTrackFilter = trackFilter;
        SetStateOK();
        AddClip.IsVisible = false;
        AssetPanelButton.IsVisible = false;
        //SpiltButton.IsVisible = false;

        Dispatcher.Dispatch(() =>
        {
            if (UseCompactLayout ?? DeviceInfo.Idiom == DeviceIdiom.Phone)
            {
                SetPlayPauseIconToClose();
                CurrentPlayheadLabel.IsVisible = false;
            }
            MultiSelectInfoLabel.IsVisible = false;
            MultiSelectCheckBox.IsVisible = false;
        });

        LeftInfoLabel.Text = (UseCompactLayout ?? false) ? Localized.DraftPage_AddClipView_ClickToPlace_Compact(name ?? "Clip") : Localized.DraftPage_AddClipView_ClickToPlace(name ?? "Clip");
        SetStatusText(Localized.DraftPage_AddClipView_ClickToPlace(name ?? "Clip"));
    }

    public void CancelPendingClipPlacement(string? statusText = null, bool restoreKeyboardPreview = true)
    {
        if (IsClipMoving)
        {
            if (restoreKeyboardPreview)
            {
                RestoreKeyboardMovePreview();
            }
            EndKeyboardMoveSession();
        }

        _pendingClipPlacementFactory = null;
        _pendingClipPlacementTrackFilter = null;
        Dispatcher.Dispatch(() =>
        {
            LeftInfoLabel.Text = "";
            AddClip.IsVisible = true;
            AssetPanelButton.IsVisible = true;
            //SpiltButton.IsVisible = !(UseCompactLayout ?? false);
            if (UseCompactLayout ?? DeviceInfo.Idiom == DeviceIdiom.Phone)
            {
                SetPlayPauseIconToPlay();
                CurrentPlayheadLabel.IsVisible = true;
            }
            MultiSelectInfoLabel.IsVisible = true;
            MultiSelectCheckBox.IsVisible = true;
        });
    }

    private async void TimelineTrackTapGesture_Tapped(object? sender, TappedEventArgs e)
    {
        if (IsClipMoving && _keyboardMoveHasMoved)
        {
            SetStatusText(Localized.DraftPage_KeyboardMove_Enabled);
            return;
        }

        if (_pendingClipPlacementFactory is null)
        {
            UnSelectTapGesture_Tapped(sender, e);
            return;
        }

        if (sender is not AbsoluteLayout trackLayout || trackLayout.BindingContext is not int trackId)
        {
            SetStatusText(Localized.DraftPage_AddClipView_ClickToPlace_InvaildPlace);
            return;
        }

        if (_pendingClipPlacementTrackFilter is not null && !_pendingClipPlacementTrackFilter(trackId))
        {
            SetStatusText(trackId >= SubTrackOffset
                ? Localized.DraftPage_AddClipView_ClickToPlace_InvaildPlace_MainOnly(_pendingClipPlacementName ?? "Clip")
                : Localized.DraftPage_AddClipView_ClickToPlace_InvaildPlace_SubOnly(_pendingClipPlacementName ?? "Clip"));
            return;
        }

        var position = e.GetPosition(trackLayout);
        if (position is null)
        {
            SetStatusText(Localized.DraftPage_AddClipView_ClickToPlace_InvaildPlace);
            return;
        }

        var clipFactory = _pendingClipPlacementFactory;
        var trackFilter = _pendingClipPlacementTrackFilter;
        _pendingClipPlacementFactory = null;
        _pendingClipPlacementTrackFilter = null;

        try
        {
            double startX = Math.Max(0, SnapPixels(position.Value.X));
            var clip = clipFactory(trackId, startX);
            string clipName = GetClipNameForChangeReason(clip, clip.Id);

            OnClipChanged?.Invoke(this, new ClipUpdateEventArgs
            {
                SourceId = clip.Id,
                SourceName = clipName,
                Reason = ClipUpdateReason.ClipAdded,
                DetailInfo = ClipUpdateEventArgs.BuildChangeReason(
                    ClipUpdateReason.ClipAdded,
                    clipName,
                    $"Placed at track {trackId}, x={Math.Round(startX, 2)}")
            });

            SetStateOK();
            SetStatusText(Localized.DraftPage_AddClipView_ClickToPlace_Done(clip.DisplayName));
            await RefreshSelectionUiAsync();
            if (IsClipMoving)
            {
                EndKeyboardMoveSession();
            }
        }
        catch (Exception ex)
        {
            _pendingClipPlacementFactory = clipFactory;
            _pendingClipPlacementTrackFilter = trackFilter;
            Log(ex, "TimelineTrackTapGesture_Tapped", this);
            SetStateFail("Failed to place the clip");
        }
        finally
        {
            Dispatcher.Dispatch(() =>
            {
                if (UseCompactLayout ?? DeviceInfo.Idiom == DeviceIdiom.Phone)
                {
                    SetPlayPauseIconToPlay();
                    CurrentPlayheadLabel.IsVisible = true;
                }
                MultiSelectInfoLabel.IsVisible = true;
                MultiSelectCheckBox.IsVisible = true;
                AddClip.IsVisible = true;
                AssetPanelButton.IsVisible = true;
                //SpiltButton.IsVisible = !(UseCompactLayout ?? false);
                LeftInfoLabel.Text = "";
            });

        }
    }
    #endregion

    #region select clip
    private bool IsClipSelected(ClipElementUI clip)
    {
        return _selectedClipIds.Contains(clip.Id);
    }

    private void AddClipToSelection(ClipElementUI clip)
    {
        if (!_selectedClipIds.Add(clip.Id)) return;
        _selectedOrigColorByClipId.TryAdd(clip.Id, clip.Clip.Background);
        clip.Clip.Background = Colors.YellowGreen;
        _selected = clip;
        OnPropertyChanged(nameof(SelectedAnyClip));
    }

    private void RemoveClipFromSelection(ClipElementUI clip)
    {
        if (!_selectedClipIds.Remove(clip.Id)) return;

        if (_selectedOrigColorByClipId.TryRemove(clip.Id, out var origColor))
        {
            clip.Clip.Background = origColor ?? ClipElementUI.DetermineAssetColor(clip.ClipType);
        }
        else
        {
            clip.ApplyClipColor();
        }

        if (_selected?.Id == clip.Id)
        {
            _selected = _selectedClipIds
                .Select(id => Clips.TryGetValue(id, out var selectedClip) ? selectedClip : null)
                .FirstOrDefault(c => c is not null);
        }
    }

    private void ClearSelectionInternal()
    {
        var selectedIds = _selectedClipIds.ToArray();
        foreach (var id in selectedIds)
        {
            if (Clips.TryGetValue(id, out var clip))
            {
                if (_selectedOrigColorByClipId.TryRemove(id, out var origColor))
                {
                    clip.Clip.Background = origColor ?? ClipElementUI.DetermineAssetColor(clip.ClipType);
                }
                else
                {
                    clip.ApplyClipColor();
                }
            }
        }

        _selectedClipIds.Clear();
        _selected = null;
        OnPropertyChanged(nameof(_ShouldShowClipMoveControlInCenterInfoBar));
        OnPropertyChanged(nameof(_ShouldShowCenterCompactControlGrid));
        OnPropertyChanged(nameof(SelectedAnyClip));
    }

    private async Task RefreshSelectionUiAsync()
    {
        if (_selectedClipIds.Count == 0)
        {
            SetStatusText(Localized.DraftPage_EverythingFine);
            ClipEditor.SetClip(null, null);
            SetTimelineScrollEnabled(true);
            RightContentBorder.Content = CreatePropertiesPlaceholder(Localized.DraftPage_PropertyPanel_SelectToContinue);
            SelectedClipChanged?.Invoke(this, EventArgs.Empty);
            return;
        }

        if (_selected is null || !_selectedClipIds.Contains(_selected.Id))
        {
            _selected = _selectedClipIds
                .Select(id => Clips.TryGetValue(id, out var selectedClip) ? selectedClip : null)
                .FirstOrDefault(c => c is not null);
        }

        if (_selectedClipIds.Count == 1 && _selected is not null)
        {
            var clip = _selected;
            SetStatusText(Localized.DraftPage_Selected(clip.DisplayName));
            ClipEditor.SetClip(clip, Assets.TryGetValue(clip.Id, out var asset) ? asset : null);
            SetTimelineScrollEnabled(false);
            RightContentBorder.Content = await BuildPropertyPanel(clip);
            await RefreshPreviewFromCurrentProviderAsync();
            SelectedClipChanged?.Invoke(this, EventArgs.Empty);
            return;
        }

        SetStatusText(Localized.DraftPage_SelectedManyClipsCount(_selectedClipIds.Count));
        ClipEditor.SetClip(null, null);
        SetTimelineScrollEnabled(false);
        RightContentBorder.Content = new Label
        {
            Text = Localized.DraftPage_SelectedManyClips,
            HorizontalOptions = LayoutOptions.Center,
            VerticalOptions = LayoutOptions.Center
        };
        OnPropertyChanged(nameof(_ShouldShowClipMoveControlInCenterInfoBar));
        OnPropertyChanged(nameof(_ShouldShowCenterCompactControlGrid));
        OnPropertyChanged(nameof(SelectedAnyClip));
        SelectedClipChanged?.Invoke(this, EventArgs.Empty);
    }

    private async void DoubleTapGesture_Tapped(object? sender, TappedEventArgs e)
    {
        if (DeviceInfo.Idiom != DeviceIdiom.Phone && !EnableClipInfoPopup) return;
        if (sender is not Border border) return;
        if (border.BindingContext is not ClipElementUI clip) return;
        LogDiagnostic($"Clip {clip.Id} double clicked, state:{clip.MovingStatus}");
        await ShowAPopup(clip: clip, border: border);

    }

    private void MultiSelectLabel_TapGestureRecognizer_Tapped(object sender, TappedEventArgs e)
    {
        MultiSelectCheckBox.IsChecked = !MultiSelectCheckBox.IsChecked;
        OnPropertyChanged(nameof(MultiSelectEnabled));
    }

    private async void MultiSelectCheckBox_CheckedChanged(object? sender, CheckedChangedEventArgs e)
    {
        MultiSelectEnabled = e.Value;
        OnPropertyChanged(nameof(MultiSelectEnabled));
        if (!MultiSelectEnabled && _selectedClipIds.Count > 1)
        {
            var keep = _selected;
            ClearSelectionInternal();
            if (keep is not null)
            {
                AddClipToSelection(keep);
            }
        }

        await RefreshSelectionUiAsync();
    }


    private async void SelectTapGesture_Tapped(object? sender, TappedEventArgs e)
    {
        if (sender is not Border border) return;
        if (border.BindingContext is not ClipElementUI clip) return;
        LogDiagnostic($"Clip {clip.Id} clicked, state:{clip.MovingStatus}");
        if (clip.MovingStatus != ClipMovingStatus.Free) return;

        if (MultiSelectEnabled)
        {
            if (IsClipSelected(clip))
            {
                RemoveClipFromSelection(clip);
            }
            else
            {
                AddClipToSelection(clip);
            }
        }
        else
        {
            ClearSelectionInternal();
            AddClipToSelection(clip);
        }

        OnPropertyChanged(nameof(_ShouldShowClipMoveControlInCenterInfoBar));
        OnPropertyChanged(nameof(_ShouldShowCenterCompactControlGrid));
        await RefreshSelectionUiAsync();
    }

    private Task OnClipEditorOverlayTappedAsync(string clipId)
    {
        if (string.IsNullOrWhiteSpace(clipId) || MultiSelectEnabled || !Clips.TryGetValue(clipId, out var clip) || clip.MovingStatus != ClipMovingStatus.Free)
        {
            return Task.CompletedTask;
        }

        ClearSelectionInternal();
        AddClipToSelection(clip);
        OnPropertyChanged(nameof(_ShouldShowClipMoveControlInCenterInfoBar));
        OnPropertyChanged(nameof(_ShouldShowCenterCompactControlGrid));
        return RefreshSelectionUiAsync();
    }

    private Task OnClipEditorBlankAreaTappedAsync()
    {
        if (_selectedClipIds.Count == 0 && _selected is null)
        {
            return Task.CompletedTask;
        }

        ClearSelectionInternal();
        return RefreshSelectionUiAsync();
    }

    private void ContextSelectTapGesture_Tapped(object? sender, TappedEventArgs e)
    {
        if (sender is not Border border) return;
        if (border.BindingContext is not ClipElementUI clip) return;
        IContextMenuBuilder? builder = null;
#if WINDOWS
        builder = new WindowsContextMenuBuilder();

#endif
        if (builder is not null)
        {
            builder
            .AddCommand(Localized.DraftPage_ContextMenu_Edit, async () =>
            {
                await ShowAPopup(clip: clip, border: border);
            })
            .AddCommand(Localized.DraftPage_CenterMenuBar_Spilt, () => Split_Clicked(this, EventArgs.Empty))
            .AddCommand(Localized.DraftPage_ContextMenu_Delete, () => DeleteAClip(clip));
            if (SettingsManager.IsBoolSettingTrue("DeveloperMode"))
            {
                builder.AddCommand("Show JSON", () =>
                {
                    var edit = new Editor
                    {
                        IsReadOnly = false,
                        Text = JsonSerializer.Serialize(clip, savingOpts),
                    };
                    var wd = new MultiWindowItem
                    {
                        Content = edit
                    };

                    MainMultiWindowView.AddWindow(wd);
                });
            }
            builder.TryShow(border);
        }
    }

    private async Task SelectAClip()
    {
        var clipsKVP = Clips.ToDictionary(c => $"{c.Value.DisplayName} ({(string.IsNullOrWhiteSpace(c.Value.TypeName) ? c.Value.ClipType.ToString() : c.Value.TypeName)},{c.Value.Id})", c => c.Value);
        var selection = await DisplayActionSheetAsync(Localized.DraftPage_MenuBar_Edit_Select, Localized._Cancel, null, clipsKVP.Keys.ToArray());
        if (string.IsNullOrWhiteSpace(selection) || !clipsKVP.TryGetValue(selection, out var c)) return;
        SelectTapGesture_Tapped(c.Clip, null!);
    }

    private void UnSelectTapGesture_Tapped(object? sender, TappedEventArgs e)
    {
        if (_selectedClipIds.Count == 0) return;
        ClearSelectionInternal();
        _ = RefreshSelectionUiAsync();
    }

    public void UnselectClip(ClipElementUI? clip)
    {
        if (clip is null)
        {
            ClearSelectionInternal();
            _ = RefreshSelectionUiAsync();
            return;
        }

        RemoveClipFromSelection(clip);
        _ = RefreshSelectionUiAsync();
    }

    private void SetTimelineScrollEnabled(bool enabled)
    {
        if (!LockScrollViewAfterSelection) return;
#if WINDOWS
        if (TimelineScrollView.Handler?.PlatformView is Microsoft.UI.Xaml.Controls.ScrollViewer sv)
        {
            sv.HorizontalScrollMode = enabled ? Microsoft.UI.Xaml.Controls.ScrollMode.Enabled : Microsoft.UI.Xaml.Controls.ScrollMode.Disabled;
        }
        if (SubTimelineScrollView.Handler?.PlatformView is Microsoft.UI.Xaml.Controls.ScrollViewer sv1)
        {
            sv1.HorizontalScrollMode = enabled ? Microsoft.UI.Xaml.Controls.ScrollMode.Enabled : Microsoft.UI.Xaml.Controls.ScrollMode.Disabled;
        }
#else
        double savedScrollX = TimelineScrollView.ScrollX;
        double savedSubScrollX = SubTimelineScrollView.ScrollX;

        if (enabled)
        {
            TimelineScrollView.Orientation = ScrollOrientation.Horizontal;
            SubTimelineScrollView.Orientation = ScrollOrientation.Horizontal;
        }
        else
        {
            TimelineScrollView.Orientation = ScrollOrientation.Neither;
            SubTimelineScrollView.Orientation = ScrollOrientation.Neither;

        }

        _ = TimelineScrollView.ScrollToAsync(savedScrollX, 0, false);
        _ = SubTimelineScrollView.ScrollToAsync(savedSubScrollX, 0, false);
#endif

    }



    #endregion

    #region move clip
    private int GetTrackIdFromY(double absoluteY, bool isSubTrack)
    {
        VerticalStackLayout layout = isSubTrack ? SubTrackContentLayout : TrackContentLayout;
        Point layoutAbs = GetAbsolutePosition(layout, OverlayLayer);
        double relativeY = absoluteY - layoutAbs.Y;
        double trackTotalHeight = 62.0;

        int visualIndex = (int)Math.Floor(relativeY / trackTotalHeight);

        if (visualIndex < 0) visualIndex = 0;

        if (visualIndex < layout.Children.Count)
        {
            if (layout.Children[visualIndex] is Border b && b.BindingContext is int id)
            {
                return id;
            }
        }

        return -1;
    }

    private void ClipPaned(object? sender, PanUpdatedEventArgs e)
    {
        if (sender is not Border border) return;
        if (border.BindingContext is not ClipElementUI clip) return;

        if (IsClipMoving)
        {
            if (e.StatusType == GestureStatus.Started)
            {
                SetStatusText(Localized.DraftPage_KeyboardMove_Enabled);
            }
            return;
        }

        if (clip.MovingStatus == ClipMovingStatus.Resize) return;
        var cid = clip.Id;

        if (clip.origTrack is null)
        {
            var kv = Tracks.FirstOrDefault(t => t.Value.Children.Contains(border));
            if (kv.Value != null) clip.origTrack = kv.Key;
        }
        int origTrack = clip.origTrack ?? 0;

        if (e.StatusType == GestureStatus.Running)
            HandlePanRunning(e, border, clip, cid, origTrack);
        else
            switch (e.StatusType)
            {
                case GestureStatus.Started:
                    HandlePanStarted(border, clip);
                    break;

                case GestureStatus.Completed:
                    HandlePanCompleted(border, clip, cid);
                    break;
            }
    }

    private void HandlePanStarted(Border border, ClipElementUI clip)
    {
        SetStateBusy();
        SetStatusText(Localized.DraftPage_WaitForUser);
        if (Denoise)
        {
            Xdenoiser.Reset();
            Ydenoiser.Reset();
        }
        clip.MovingStatus = ClipMovingStatus.Move;
        clip.layoutX = border.TranslationX;
        clip.layoutY = border.TranslationY;
        clip.defaultY = border.TranslationY;
    }

    private void HandlePanRunning(PanUpdatedEventArgs e, Border border, ClipElementUI clip, string cid, int origTrack)
    {
        if (clip.MovingStatus != ClipMovingStatus.Free && clip.MovingStatus != ClipMovingStatus.Move) return;

        double xToBe = -1, yToBe = -1;

        if (Denoise)
        {
            xToBe = clip.layoutX + Xdenoiser.Process(e.TotalX);
            yToBe = clip.layoutY + Ydenoiser.Process(e.TotalY);
        }
        else
        {
            xToBe = clip.layoutX + e.TotalX;
            yToBe = clip.layoutY + e.TotalY;
        }

        double actualYToBe = yToBe + UpperContent.Height;

        bool ghostExists = Clips.ContainsKey("ghost_" + cid);

        // If no ghost (still within same track), apply snapping and overlap resolution live
        if (!ghostExists)
        {
            double clipWidth = (border.Width > 0) ? border.Width : border.WidthRequest;
            int trackIndex = clip.origTrack ?? origTrack;
            double snapped = SnapPixels(xToBe);
            double resolved = ResolveOverlapStartPixels(trackIndex, cid, snapped, clipWidth);
            border.TranslationX = resolved;
        }
        else
        {
            border.TranslationX = xToBe;
        }
        if (!ghostExists && Math.Abs(actualYToBe - clip.defaultY) > 50.0)
        {
            InitMoveBetweenTracks(clip, cid, border);
        }
        else if (ghostExists)
        {
            border.TranslationY = yToBe;
            UpdateGhostAndShadow(border, cid, xToBe, origTrack);
        }
    }

    private void UpdateGhostAndShadow(Border border, string cid, double xToBe, int origTrack)
    {
        ClipElementUI ghostClip = Clips["ghost_" + cid];
        Point clipAbsolutePosition = GetAbsolutePosition(border, OverlayLayer);
        ghostClip.Clip.TranslationX = clipAbsolutePosition.X;
        ghostClip.Clip.TranslationY = clipAbsolutePosition.Y;

        bool isSub = origTrack >= SubTrackOffset;
        int newTrack = GetTrackIdFromY(clipAbsolutePosition.Y, isSub);

        ClipElementUI shadow = Clips["shadow_" + cid];
        // Apply snapping and overlap resolution for shadow placement
        double proposed = xToBe;
        double snapped = SnapPixels(proposed);
        double clipWidth = border.Width > 0 ? border.Width : border.WidthRequest;
        if (Tracks.ContainsKey(newTrack))
        {
            // resolve overlaps on the target track
            var resolved = ResolveOverlapStartPixels(newTrack, cid, snapped, clipWidth);
            shadow.Clip.TranslationX = resolved;
        }
        else
        {
            shadow.Clip.TranslationX = snapped;
        }

        if (origTrack == newTrack)
        {
            return;
        }
        if (ShowShadow) UpdateShadowTrack(shadow, newTrack);
    }

    private void UpdateShadowTrack(ClipElementUI shadow, int newTrack)
    {
        try
        {
            if (shadow.origTrack.HasValue && Tracks.TryGetValue(shadow.origTrack.Value, out var oldTrackLayout))
            {
                oldTrackLayout.Children.Remove(shadow.Clip);
            }
            else
            {
                Tracks.Values
                    .FirstOrDefault(t => t.Children.Contains(shadow.Clip))
                    ?.Children.Remove(shadow.Clip);
            }

            bool canDrop = false;
            if (newTrack < SubTrackOffset)
            {
                canDrop = newTrack >= 0 && newTrack <= trackCount;
            }
            else
            {
                int maxSub = Tracks.Keys.Where(k => k >= SubTrackOffset).DefaultIfEmpty(SubTrackOffset - 1).Max();
                canDrop = newTrack <= maxSub + 1;
            }

            if (canDrop)
            {
                if (Tracks.ContainsKey(newTrack))
                {
                    Tracks[newTrack].Children.Add(shadow.Clip);
                    shadow.origTrack = newTrack;
                    SetStatusText(Localized.DraftPage_WaitForUser);
                }
                else
                {
                    shadow.origTrack = null;
                    SetStatusText(Localized.DraftPage_ReleaseToRemove);
                }

            }
            else
            {
                shadow.origTrack = null;
                SetStatusText(Localized.DraftPage_ReleaseToRemove);
            }
        }
        catch (Exception ex) //just ignore it, avoid crash
        {
            Log(ex, $"set shadow for {shadow.Id}", this);
        }
    }

    private async void HandlePanCompleted(Border border, ClipElementUI clip, string cid)
    {
        var subTrackCount = Tracks.Where(c => c.Key >= SubTrackOffset).Count();
        int mainTrackCount = Tracks.Where(c => c.Key < SubTrackOffset).Count();

        if (clip.MovingStatus != ClipMovingStatus.Free && clip.MovingStatus != ClipMovingStatus.Move) return;

        if (ShowShadow && Clips.TryRemove("shadow_" + cid, out var shadowClip))
        {
            if (shadowClip.origTrack is int sTrack && Tracks.TryGetValue(sTrack, out var sLayout))
            {
                sLayout.Children.Remove(shadowClip.Clip);
            }
            else
            {
                Tracks.Values.FirstOrDefault(t => t.Children.Contains(shadowClip.Clip))?.Children.Remove(shadowClip.Clip);
            }
        }

        if (Clips.TryRemove("ghost_" + cid, out var ghostClip))
        {
            bool isSub = clip.origTrack >= SubTrackOffset;
            int newTrack = GetTrackIdFromY(ghostClip.Clip.TranslationY + ghostClip.Clip.Y, isSub);
            OverlayLayer.Children.Remove(ghostClip.Clip);

            if (clip.origTrack is int oldTrack && Tracks.TryGetValue(oldTrack, out var oldTrackLayout))
            {
                oldTrackLayout.Children.Remove(border);
            }

            if (newTrack == -1)
            {
                // Create new track
                if (isSub)
                {
                    // Find next available sub ID
                    int newId = Tracks.Keys.Where(k => k >= SubTrackOffset).DefaultIfEmpty(SubTrackOffset - 1).Max() + 1;
                    AddASubTrack(newId);
                    newTrack = newId;
                }
                else
                {
                    // Add main track
                    AddTrackButton_Clicked(this, EventArgs.Empty);
                    newTrack = Tracks.Keys.Where(k => k < SubTrackOffset).Max();
                }
            }

            if (!Tracks.ContainsKey(newTrack))
            {
                if (_selectedClipIds.Contains(cid))
                {
                    _selectedClipIds.Remove(cid);
                    _selectedOrigColorByClipId.TryRemove(cid, out _);
                    if (_selected?.Id == cid) _selected = null;
                }
                Clips.TryRemove(cid, out _);
                SetStatusText(Localized.DraftPage_Removed);
                SetStateOK();
                LogDiagnostic($"clip {cid} removed.");
                return;
            }
            else
            {

                // snap X and resolve overlaps on target track before inserting
                double clipWidth = (border.Width > 0) ? border.Width : border.WidthRequest;
                double snappedX = SnapPixels(border.TranslationX);
                double resolvedX = ResolveOverlapStartPixels(newTrack, cid, snappedX, clipWidth);
                border.TranslationX = resolvedX;

                if (clip.origTrack != newTrack)
                {
                    await ClipsTrackChanged(border, clip, newTrack);
                }
                else
                {
                    border.TranslationY = 0.0;
                    if (Tracks.TryGetValue(newTrack, out var currentTrack))
                    {
                        try
                        {
                            foreach (var item in Tracks)
                            {
                                item.Value.Children.Remove(border); //avoid add a same view to 2 different container, cause crash
                            }
                            currentTrack.Children.Add(border);
                        }
                        catch (Exception ex)
                        {
                            Log(ex, $"re-add clip {cid} to track {newTrack}", this);
                        }
                    }
                }
                Clips[cid].origTrack = newTrack;
            }

        }


        LogDiagnostic($"{cid} moved to {border.TranslationX},{border.TranslationY} in track:{clip.origTrack} ");
        string movedClipName = GetClipNameForChangeReason(clip, cid);
        OnClipChanged?.Invoke(cid, new ClipUpdateEventArgs
        {
            SourceId = cid,
            SourceName = movedClipName,
            Reason = ClipUpdateReason.ClipItselfMove,
            DetailInfo = ClipUpdateEventArgs.BuildChangeReason(
                ClipUpdateReason.ClipItselfMove,
                movedClipName,
                $"Moved to track {(clip.origTrack?.ToString() ?? "unknown")}, x={Math.Round(border.TranslationX, 2)}")
        });


        clip.MovingStatus = ClipMovingStatus.Free;
        CleanupGhostAndShadow();
        await UpdateAdjacencyForTrack();
        UpdateTimelineWidth();
        SetStateOK();
        SetStatusText(Localized.DraftPage_EverythingFine);
#if ANDROID
        OverlayLayer.IsVisible = false;
#endif


    }

    private async Task ClipsTrackChanged(Border border, ClipElementUI clip, int newTrack)
    {
        await Dispatcher.DispatchAsync(async () =>
        {
            try
            {
                border.TranslationY = 0;
                Tracks[newTrack].Add(border);
                Clips[clip.Id].defaultY = border.TranslationY;
                Clips[clip.Id].Clip = border;
                // update adjacency after adding to new track
                await UpdateAdjacencyForTrack(newTrack);
            }
            catch (Exception ex)
            {
                Log(ex, $"Set clip {clip.Id}", this);
                await DisplayAlertAsync(Localized._Info, Localized.DraftPage_FailToProcess, Localized._OK);
            }
        });

    }

    private void InitMoveBetweenTracks(ClipElementUI clipElementUI, string cid, Border border)
    {
#if ANDROID
        OverlayLayer.IsVisible = true;
#endif
        border.Stroke = Colors.Green;
        Border ghostBorder = new Border
        {
            Stroke = border.Stroke,
            StrokeThickness = border.StrokeThickness,
            Background = new SolidColorBrush(Colors.DeepSkyBlue),
            WidthRequest = border.WidthRequest,
            HeightRequest = border.HeightRequest,
            StrokeShape = border.StrokeShape,
        };

        ClipElementUI ghostElement = new ClipElementUI
        {
            Id = "ghost_" + cid,
            layoutX = clipElementUI.layoutX,
            layoutY = clipElementUI.layoutY,
            defaultY = clipElementUI.defaultY,
            Clip = ghostBorder
        };

        Clips[cid].ghostLayoutX = clipElementUI.layoutX;
        Clips[cid].ghostLayoutY = clipElementUI.layoutY;
        Clips.AddOrUpdate("ghost_" + cid, ghostElement, (_, _) => ghostElement);
        OverlayLayer.Add(ghostBorder);

        Border shadowBorder = new Border
        {
            Stroke = border.Stroke,
            StrokeThickness = border.StrokeThickness,
            Background = new SolidColorBrush(Colors.DeepSkyBlue),
            WidthRequest = border.WidthRequest,
            HeightRequest = border.HeightRequest,
            StrokeShape = border.StrokeShape,
            Opacity = 0.45
        };

        ClipElementUI shadowElement = new ClipElementUI
        {
            Id = "shadow_" + cid,
            Clip = shadowBorder,
            origTrack = 0
        };
        Clips.AddOrUpdate("shadow_" + cid, shadowElement, (_, _) => shadowElement);
    }

    private void DeleteAClip(ClipElementUI? clip = null, bool suppressClipChangedEvent = false)
    {
        List<ClipElementUI> clipsToDelete = [];

        if (clip is not null)
        {
            clipsToDelete.Add(clip);
        }
        else if (_selectedClipIds.Count > 0)
        {
            clipsToDelete.AddRange(
                _selectedClipIds
                    .Select(id => Clips.TryGetValue(id, out var selectedClip) ? selectedClip : null)
                    .Where(c => c is not null)
                    .Cast<ClipElementUI>());
        }
        else if (_selected is not null)
        {
            clipsToDelete.Add(_selected);
        }

        if (clipsToDelete.Count == 0) return;

        var deletedNames = new List<string>(clipsToDelete.Count);

        foreach (var target in clipsToDelete)
        {
            RemoveClipFromSelection(target);

            if (target.origTrack is not null && Tracks.TryGetValue(target.origTrack.Value, out var trackLayout))
            {
                trackLayout.Children.Remove(target.Clip);
            }

            Clips.TryRemove(target.Id, out _);
            deletedNames.Add(GetClipNameForChangeReason(target, target.Id));
            LogDiagnostic($"clip {target.Id} deleted.");

            try
            {
                RemoveTransformsReferencingClip(target.Id);
            }
            catch { }
        }

        if (!suppressClipChangedEvent)
        {
            var previewNames = string.Join(",", deletedNames.Take(3));
            var detail = deletedNames.Count <= 3
                ? $"Deleted {deletedNames.Count} clip(s): {previewNames}"
                : $"Deleted {deletedNames.Count} clip(s): {previewNames}, ...";

            OnClipChanged?.Invoke(this, new ClipUpdateEventArgs
            {
                Reason = ClipUpdateReason.ClipDeleted,
                DetailInfo = ClipUpdateEventArgs.BuildChangeReason(ClipUpdateReason.ClipDeleted, details: detail)
            });
        }

        SetStatusText(Localized.DraftPage_Removed);
        _ = RefreshSelectionUiAsync();
        OnPropertyChanged(nameof(_ShouldShowClipMoveControlInCenterInfoBar));
        OnPropertyChanged(nameof(_ShouldShowCenterCompactControlGrid));
        OnPropertyChanged(nameof(SelectedAnyClip));
    }

    private int CaptureSelectionToClipboard(bool fromCut)
    {
        var selection = _selectedClipIds
            .Select(id => Clips.TryGetValue(id, out var selectedClip) ? selectedClip : null)
            .Where(ShouldParticipateInTimelineLayout)
            .Cast<ClipElementUI>()
            .DistinctBy(c => c.Id)
            .OrderBy(c => c.origTrack ?? 0)
            .ThenBy(c => c.Clip.TranslationX)
            .ToList();

        if (selection.Count == 0 && ShouldParticipateInTimelineLayout(_selected))
        {
            selection.Add(_selected!);
        }

        if (selection.Count == 0)
        {
            SetStatusText(Localized.DraftPage_SelectOneOrManyToContinue);
            return 0;
        }

        _timelineClipboard.Clear();
        foreach (var clip in selection)
        {
            try
            {
                var dto = DraftImportAndExportHelper.ExportClipElementFromDraftPage(this, clip, wrapSoundtrackAsClip: true);
                var clonedDto = JsonSerializer.Deserialize<ClipDraftDTO>(JsonSerializer.Serialize(dto, savingOpts), savingOpts);
                if (clonedDto is null)
                {
                    continue;
                }

                _timelineClipboard.Add(new TimelineClipboardItem
                {
                    Dto = clonedDto,
                    TrackIndex = clip.origTrack ?? (int)clonedDto.LayerIndex,
                    StartPx = clip.Clip.TranslationX,
                    WidthPx = clip.Clip.WidthRequest > 0 ? clip.Clip.WidthRequest : clip.origLength
                });
            }
            catch (Exception ex)
            {
                Log(ex, $"copy clip {clip.Id}", this);
            }
        }

        _timelineClipboardFromCut = fromCut;

        if (_timelineClipboard.Count == 0)
        {
            SetStateFail(Localized.DraftPage_ClipboardNoContent);
            return 0;
        }

        SetStateOK();
        SetStatusText(fromCut
            ? Localized.DraftPage_CopyPaste_Cutted(_timelineClipboard.First().Dto.Name ?? "Clip")
            : Localized.DraftPage_CopyPaste_Copied(_timelineClipboard.First().Dto.Name ?? "Clip"));

        return _timelineClipboard.Count;
    }

    private async Task CutSelectionAsync()
    {
        var copied = CaptureSelectionToClipboard(true);
        if (copied <= 0)
        {
            return;
        }

        DeleteAClip(suppressClipChangedEvent: true);
        await UpdateAdjacencyForTrack();
        UpdateTimelineWidth();

        OnClipChanged?.Invoke(this, new ClipUpdateEventArgs
        {
            Reason = ClipUpdateReason.ClipDeleted,
            DetailInfo = ClipUpdateEventArgs.BuildChangeReason(
                ClipUpdateReason.ClipDeleted,
                details: $"Cut {copied} clip(s) from timeline")
        });
    }

    private async Task PasteClipboardAsync()
    {
        if (_timelineClipboard.Count == 0)
        {
            SetStatusText(Localized.DraftPage_ClipboardNoContent);
            return;
        }

        var orderedItems = _timelineClipboard
            .OrderBy(i => i.TrackIndex)
            .ThenBy(i => i.StartPx)
            .ToList();

        int minTrack = orderedItems.Min(i => i.TrackIndex);
        double minStartPx = orderedItems.Min(i => i.StartPx);
        string placementName = orderedItems.Count == 1
            ? (string.IsNullOrWhiteSpace(orderedItems[0].Dto.Name) ? "Clip" : orderedItems[0].Dto.Name)
            : $"{orderedItems.Count} Clips";

        BeginClipPlacement(
            (clickedTrack, clickedStartX) =>
            {
                return PlaceClipboardAt(clickedTrack, clickedStartX, orderedItems, minTrack, minStartPx);
            },
            null,
            placementName);

        await Task.CompletedTask;
    }

    private async Task DuplicateSelectionAsync()
    {
        var origPasteboard = new List<TimelineClipboardItem>(_timelineClipboard);
        var origFromCut = _timelineClipboardFromCut.ToString();
        var copied = CaptureSelectionToClipboard(false);
        if (copied <= 0)
        {
            return;
        }

        await PasteClipboardAsync();
        _timelineClipboard.Clear();
        _timelineClipboard.AddRange(origPasteboard);
        _timelineClipboardFromCut = bool.Parse(origFromCut);
    }

    private ClipElementUI PlaceClipboardAt(int clickedTrack, double clickedStartX, List<TimelineClipboardItem> orderedItems, int minTrack, double minStartPx)
    {
        int trackDelta = clickedTrack - minTrack;
        int minTargetTrack = orderedItems.Min(i => i.TrackIndex + trackDelta);
        if (minTargetTrack < 0)
        {
            trackDelta += -minTargetTrack;
        }

        double targetStartPx = Math.Max(0, SnapPixels(clickedStartX));

        ClearSelectionInternal();

        var pastedClips = new List<ClipElementUI>();
        foreach (var item in orderedItems)
        {
            int targetTrack = item.TrackIndex + trackDelta;
            EnsureTrackForPaste(targetTrack);

            double relativeOffsetPx = item.StartPx - minStartPx;
            double desiredStartPx = Math.Max(0, targetStartPx + relativeOffsetPx);
            double widthPx = Math.Max(MinClipWidth, item.WidthPx);

            var dto = item.Dto;
            var pasted = CreateAndAddClip(
                startX: desiredStartPx,
                width: widthPx,
                trackIndex: targetTrack,
                id: Guid.NewGuid().ToString(),
                labelText: string.IsNullOrWhiteSpace(dto.Name) ? "Clip" : dto.Name,
                background: ClipElementUI.DetermineAssetColor(dto.ClipType),
                prototype: null,
                resolveOverlap: true,
                relativeStart: dto.RelativeStartFrame,
                maxFrames: (uint)Math.Max(dto.SourceDuration ?? dto.Duration, dto.Duration));

            pasted.DisplayName = string.IsNullOrWhiteSpace(dto.Name) ? pasted.DisplayName : dto.Name;
            pasted.SourcePath = dto.FilePath;
            pasted.ClipType = dto.ClipType;
            pasted.FromPlugin = dto.FromPlugin;
            pasted.TypeName = dto.TypeName;
            pasted.SubLayerIndex = (int)dto.SubLayerIndex;
            pasted.isInfiniteLength = dto.IsInfiniteLength;
            pasted.ShouldDisplayInUI = dto.ShouldDisplayInUI;
            pasted.Clip.IsVisible = dto.ShouldDisplayInUI;
            pasted.sourceSecondPerFrame = dto.FrameTime;
            pasted.SecondPerFrameRatio = dto.SecondPerFrameRatio > 0 ? dto.SecondPerFrameRatio : 1f;
            pasted.TargetWidth = dto.TargetWidth;
            pasted.TargetHeight = dto.TargetHeight;
            pasted.TargetX = dto.TargetX;
            pasted.TargetY = dto.TargetY;
            pasted.ExtraData = dto.MetaData?.ToDictionary(kv => kv.Key, kv => kv.Value) ?? new Dictionary<string, object>();
            pasted.Effects = dto.Effects?.ToDictionary(
                e => string.IsNullOrWhiteSpace(e.Name) ? $"Effect-{Guid.NewGuid()}" : e.Name,
                e => PluginManager.CreateEffect(e, ProjectInfo.RelativeWidth, ProjectInfo.RelativeHeight));

            if (dto.EffectBundles != null)
            {
                var bundles = new Dictionary<Guid, IEffectBundle>();
                foreach (var bundle in dto.EffectBundles)
                {
                    if (!EffectServices.GetAvailableEffectBundles().TryGetValue(bundle.BundleTypeName, out var factory))
                    {
                        continue;
                    }

                    var instance = factory();
                    instance.Id = bundle.Id;
                    instance.Name = bundle.Name;
                    instance.Parameters = bundle.Parameters ?? new Dictionary<string, object>();
                    instance.BindedInputId = bundle.BindedInputId;
                    instance.BindedOutputId = bundle.BindedOutputId;
                    instance.BindedInputIds = bundle.BindedInputIds?.ToList();
                    bundles[instance.Id] = instance;
                }

                pasted.EffectBundles = bundles;
            }

            pasted.ApplySpeedRatio();
            pasted.ApplyClipColor();
            AddClipToSelection(pasted);
            pastedClips.Add(pasted);
            string pastedName = GetClipNameForChangeReason(pasted, pasted.Id);

            OnClipChanged?.Invoke(this, new ClipUpdateEventArgs
            {
                SourceId = pasted.Id,
                SourceName = pastedName,
                Reason = ClipUpdateReason.ClipPasted,
                DetailInfo = ClipUpdateEventArgs.BuildChangeReason(
                    ClipUpdateReason.ClipPasted,
                    pastedName,
                    $"Pasted to track {targetTrack}, x={Math.Round(desiredStartPx, 2)}")
            });
        }

        _ = UpdateAdjacencyForTrack();
        UpdateTimelineWidth();

        if (_timelineClipboardFromCut)
        {
            _timelineClipboardFromCut = false;
        }

        SetStateOK();
        SetStatusText(Localized.DraftPage_CopyPaste_Pasted(pastedClips.First().DisplayName));

        return pastedClips.First();
    }

    private void EnsureTrackForPaste(int trackId)
    {
        EnsureTrackExistsById(trackId);
    }

    public async Task MoveSelection()
    {
        var clipsToMove = _selectedClipIds
            .Select(id => Clips.TryGetValue(id, out var selectedClip) ? selectedClip : null)
            .Where(ShouldParticipateInTimelineLayout)
            .Cast<ClipElementUI>()
            .ToList();


        if (clipsToMove.Count == 0 && ShouldParticipateInTimelineLayout(_selected))
        {
            clipsToMove.Add(_selected!);
        }

        if (clipsToMove.Count == 0)
        {
            SetStatusText(Localized.DraftPage_SelectOneOrManyToContinue);
            return;
        }

        if (clipsToMove.Any(c => c.origTrack is null))
        {
            SetStatusText("Selected clip has no valid track");
            return;
        }

        var orderedClips = clipsToMove
            .DistinctBy(c => c.Id)
            .OrderBy(c => c.Clip.TranslationX)
            .ThenBy(c => c.origTrack ?? 0)
            .ToList();

        var anchorClip = _selected is not null && orderedClips.Any(c => c.Id == _selected.Id)
            ? _selected
            : orderedClips.First();

        if (anchorClip?.origTrack is null)
        {
            SetStatusText("Selected clip has no valid track");
            return;
        }

        int anchorTrack = anchorClip.origTrack.Value;
        double anchorStartX = anchorClip.Clip.TranslationX;
        string placementName = orderedClips.Count == 1 ? anchorClip.DisplayName : $"{orderedClips.Count} Clips";

        bool CanPlaceSelectionAtTrack(int clickedTrack)
        {
            int trackDelta = clickedTrack - anchorTrack;
            foreach (var clip in orderedClips)
            {
                int targetTrack = (clip.origTrack ?? 0) + trackDelta;
                if (targetTrack < 0)
                {
                    return false;
                }
            }
            return true;
        }

        void EnsurePlacementTrackExists(int trackId)
        {
            EnsureTrackExistsById(trackId);
        }

        BeginClipPlacement(
            (clickedTrack, clickedStartX) =>
            {
                int trackDelta = clickedTrack - anchorTrack;
                double snappedAnchorX = Math.Max(0, SnapPixels(clickedStartX));
                double moveDeltaX = snappedAnchorX - anchorStartX;

                foreach (var clip in orderedClips)
                {
                    foreach (var track in Tracks.Values)
                    {
                        track.Children.Remove(clip.Clip);
                    }
                }

                var movePlans = orderedClips
                    .Select(clip => new
                    {
                        Clip = clip,
                        TargetTrack = (clip.origTrack ?? 0) + trackDelta,
                        DesiredX = Math.Max(0, SnapPixels(clip.Clip.TranslationX + moveDeltaX)),
                        Width = Math.Max(MinClipWidth, clip.Clip.WidthRequest > 0 ? clip.Clip.WidthRequest : clip.origLength)
                    })
                    .ToList();

                foreach (var targetTrack in movePlans.Select(plan => plan.TargetTrack).Distinct())
                {
                    EnsurePlacementTrackExists(targetTrack);
                }

                foreach (var trackGroup in movePlans.GroupBy(plan => plan.TargetTrack))
                {
                    double groupStartX = trackGroup.Min(plan => plan.DesiredX);
                    double groupEndX = trackGroup.Max(plan => plan.DesiredX + plan.Width);
                    double groupWidth = Math.Max(MinClipWidth, groupEndX - groupStartX);
                    double resolvedGroupStartX = ResolveOverlapStartPixels(trackGroup.Key, $"move_group_{anchorClip.Id}", groupStartX, groupWidth);
                    double groupOffset = resolvedGroupStartX - groupStartX;

                    foreach (var plan in trackGroup.OrderBy(plan => plan.DesiredX))
                    {
                        var clip = plan.Clip;
                        double finalX = Math.Max(0, SnapPixels(plan.DesiredX + groupOffset));

                        clip.Clip.TranslationX = finalX;
                        clip.Clip.TranslationY = 0;
                        clip.origX = finalX;
                        clip.origTrack = trackGroup.Key;
                        clip.SubLayerIndex = trackGroup.Key;
                        clip.layoutX = finalX;
                        clip.layoutY = 0;
                        clip.defaultY = 0;
                        clip.MovingStatus = ClipMovingStatus.Free;
                        clip.ShouldDisplayInUI = true;
                        clip.Clip.IsVisible = true;

                        Tracks[trackGroup.Key].Children.Add(clip.Clip);
                        string movedGroupClipName = GetClipNameForChangeReason(clip, clip.Id);

                        OnClipChanged?.Invoke(clip.Id, new ClipUpdateEventArgs
                        {
                            SourceId = clip.Id,
                            SourceName = movedGroupClipName,
                            Reason = ClipUpdateReason.ClipItselfMove,
                            DetailInfo = ClipUpdateEventArgs.BuildChangeReason(
                                ClipUpdateReason.ClipItselfMove,
                                movedGroupClipName,
                                $"Moved as group to track {trackGroup.Key}, x={Math.Round(finalX, 2)}")
                        });
                    }
                }

                CleanupGhostAndShadow();
                _ = UpdateAdjacencyForTrack();
                UpdateTimelineWidth();

                return anchorClip;
            },
            CanPlaceSelectionAtTrack,
            placementName);

        StartKeyboardMoveSession(orderedClips);
        UnselectClip(null);
        await Task.CompletedTask;
    }

    private void StartKeyboardMoveSession(List<ClipElementUI> clips)
    {
        _keyboardMoveOriginalPlacement.Clear();
        _keyboardMoveClips = clips
            .Where(c => c.origTrack is not null)
            .DistinctBy(c => c.Id)
            .ToList();

        foreach (var clip in _keyboardMoveClips)
        {
            _keyboardMoveOriginalPlacement[clip.Id] = (clip.origTrack!.Value, clip.Clip.TranslationX);
        }

        _keyboardMoveTrackDelta = 0;
        _keyboardMovePixelDelta = 0;
        _keyboardMoveHasMoved = false;
        IsClipMoving = true;
    }

    private void EndKeyboardMoveSession()
    {
        _keyboardMoveOriginalPlacement.Clear();
        _keyboardMoveClips = [];
        _keyboardMoveTrackDelta = 0;
        _keyboardMovePixelDelta = 0;
        _keyboardMoveHasMoved = false;
        IsClipMoving = false;
    }

    private async Task<bool> TryStartKeyboardMoveFromPendingPlacementAsync()
    {
        var clipFactory = _pendingClipPlacementFactory;
        var trackFilter = _pendingClipPlacementTrackFilter;
        if (clipFactory is null)
        {
            return false;
        }

        var trackCandidates = Tracks.Keys
            .OrderBy(trackId => trackId)
            .Where(trackId => trackFilter?.Invoke(trackId) ?? true)
            .ToList();

        int targetTrackId;
        if (trackCandidates.Count > 0)
        {
            targetTrackId = trackCandidates[0];
        }
        else
        {
            // Fall back to creating a common default track when placement starts from keyboard.
            targetTrackId = (trackFilter?.Invoke(0) ?? true) ? 0 : SubTrackOffset;
            if (trackFilter is not null && !trackFilter(targetTrackId))
            {
                SetStateFail("No available track for keyboard placement.");
                return false;
            }

            EnsureTrackExistsById(targetTrackId);
        }

        _pendingClipPlacementFactory = null;
        _pendingClipPlacementTrackFilter = null;

        try
        {
            var startFrame = (uint)Math.Max(0, _currentFrame);
            double startX = Math.Max(0, SnapPixels(FrameToPixel(startFrame)));
            var clip = clipFactory(targetTrackId, startX);
            string clipName = GetClipNameForChangeReason(clip, clip.Id);

            OnClipChanged?.Invoke(this, new ClipUpdateEventArgs
            {
                SourceId = clip.Id,
                SourceName = clipName,
                Reason = ClipUpdateReason.ClipAdded,
                DetailInfo = ClipUpdateEventArgs.BuildChangeReason(
                    ClipUpdateReason.ClipAdded,
                    clipName,
                    $"Placed by keyboard at track {targetTrackId}, frame {startFrame}, x={Math.Round(startX, 2)}")
            });

            StartKeyboardMoveSession([clip]);
            UnselectClip(null);
            SetStateOK();
            SetStatusText(Localized.DraftPage_KeyboardMove_Enabled);
            await RefreshSelectionUiAsync();
            return true;
        }
        catch (Exception ex)
        {
            _pendingClipPlacementFactory = clipFactory;
            _pendingClipPlacementTrackFilter = trackFilter;
            Log(ex, "TryStartKeyboardMoveFromPendingPlacementAsync", this);
            SetStateFail("Failed to place the clip");
            return false;
        }
    }

    private async Task HandleMoveArrowAsync(double pixelDelta, int trackDelta)
    {
        if (!IsClipMoving)
        {
            if (_pendingClipPlacementFactory is not null)
            {
                if (!await TryStartKeyboardMoveFromPendingPlacementAsync())
                {
                    return;
                }
            }
            else
            {
                if (trackDelta == 0)
                {
                    await MovePlayhead(pixelDelta < 0 ? -10 : 10);
                }
                return;
            }
        }

        if (_keyboardMoveClips.Count == 0)
        {
            EndKeyboardMoveSession();
            return;
        }

        int nextTrackDelta = _keyboardMoveTrackDelta + trackDelta;
        if (_keyboardMoveClips.Any(c => (_keyboardMoveOriginalPlacement[c.Id].Track + nextTrackDelta) < 0))
        {
            return;
        }

        _keyboardMoveTrackDelta = nextTrackDelta;
        _keyboardMovePixelDelta += pixelDelta;
        _keyboardMoveHasMoved = true;

        if (_pendingClipPlacementFactory is not null)
        {
            _pendingClipPlacementFactory = null;
            _pendingClipPlacementTrackFilter = null;
            SetStatusText(Localized.DraftPage_KeyboardMove_Enabled);
        }

        ApplyKeyboardMovePreview();
        await FollowKeyboardMoveViewportAsync();
    }

    private async Task FollowKeyboardMoveViewportAsync()
    {
        if (_keyboardMoveClips.Count == 0)
        {
            return;
        }

        const double horizontalMargin = 80;
        const double verticalMargin = 20;

        double minX = double.MaxValue;
        double maxX = double.MinValue;
        double minY = double.MaxValue;
        double maxY = double.MinValue;

        var layoutAbs = GetAbsolutePosition(TracksAndClipsLayout, null!);

        foreach (var clip in _keyboardMoveClips)
        {
            double width = clip.Clip.Width > 0 ? clip.Clip.Width : clip.Clip.WidthRequest;
            if (width <= 0)
            {
                width = Math.Max(MinClipWidth, clip.origLength);
            }

            double startX = clip.Clip.TranslationX;
            minX = Math.Min(minX, startX);
            maxX = Math.Max(maxX, startX + width);

            var locatedTrack = Tracks.FirstOrDefault(kv => kv.Value.Children.Contains(clip.Clip));
            if (locatedTrack.Value is null)
            {
                continue;
            }

            var trackAbs = GetAbsolutePosition(locatedTrack.Value, null!);
            double clipYInViewport = trackAbs.Y - layoutAbs.Y + clip.Clip.TranslationY;
            double clipHeight = clip.Clip.Height > 0 ? clip.Clip.Height : clip.Clip.HeightRequest;
            if (clipHeight <= 0)
            {
                clipHeight = ClipHeight;
            }

            minY = Math.Min(minY, clipYInViewport);
            maxY = Math.Max(maxY, clipYInViewport + clipHeight);
        }

        if (minX < double.MaxValue && maxX > double.MinValue)
        {
            double currentScrollX = TimelineScrollView.ScrollX;
            double viewportWidth = TimelineScrollView.Width;
            if (viewportWidth > 0)
            {
                double targetX = currentScrollX;
                if (minX < currentScrollX + horizontalMargin)
                {
                    targetX = Math.Max(0, minX - horizontalMargin);
                }
                else if (maxX > currentScrollX + viewportWidth - horizontalMargin)
                {
                    targetX = Math.Max(0, maxX - viewportWidth + horizontalMargin);
                }

                if (Math.Abs(targetX - currentScrollX) > 0.5)
                {
                    await TimelineScrollView.ScrollToAsync(targetX, 0, true);
                }
            }
        }

        if (minY < double.MaxValue && maxY > double.MinValue)
        {
            double currentScrollY = TracksAndClipsLayout.ScrollY;
            double viewportHeight = TracksAndClipsLayout.Height;
            if (viewportHeight > 0)
            {
                double targetY = currentScrollY;
                if (minY < verticalMargin)
                {
                    targetY = Math.Max(0, currentScrollY + minY - verticalMargin);
                }
                else if (maxY > viewportHeight - verticalMargin)
                {
                    targetY = Math.Max(0, currentScrollY + maxY - (viewportHeight - verticalMargin));
                }

                if (Math.Abs(targetY - currentScrollY) > 0.5)
                {
                    await TracksAndClipsLayout.ScrollToAsync(0, targetY, true);
                }
            }
        }
    }

    private void ApplyKeyboardMovePreview()
    {
        var movePlans = _keyboardMoveClips
            .Select(clip =>
            {
                var basePlacement = _keyboardMoveOriginalPlacement[clip.Id];
                return new
                {
                    Clip = clip,
                    TargetTrack = basePlacement.Track + _keyboardMoveTrackDelta,
                    DesiredX = Math.Max(0, SnapPixels(basePlacement.X + _keyboardMovePixelDelta)),
                    Width = Math.Max(MinClipWidth, clip.Clip.WidthRequest > 0 ? clip.Clip.WidthRequest : clip.origLength)
                };
            })
            .ToList();

        foreach (var targetTrack in movePlans.Select(plan => plan.TargetTrack).Distinct())
        {
            EnsureTrackExistsById(targetTrack);
        }

        foreach (var clip in _keyboardMoveClips)
        {
            foreach (var track in Tracks.Values)
            {
                track.Children.Remove(clip.Clip);
            }
        }

        foreach (var trackGroup in movePlans.GroupBy(plan => plan.TargetTrack))
        {
            double groupStartX = trackGroup.Min(plan => plan.DesiredX);
            double groupEndX = trackGroup.Max(plan => plan.DesiredX + plan.Width);
            double groupWidth = Math.Max(MinClipWidth, groupEndX - groupStartX);
            double resolvedGroupStartX = ResolveOverlapStartPixels(trackGroup.Key, $"keyboard_move_{Guid.NewGuid()}", groupStartX, groupWidth);
            double groupOffset = resolvedGroupStartX - groupStartX;

            foreach (var plan in trackGroup.OrderBy(plan => plan.DesiredX))
            {
                double finalX = Math.Max(0, SnapPixels(plan.DesiredX + groupOffset));
                plan.Clip.Clip.TranslationX = finalX;
                plan.Clip.Clip.TranslationY = 0;
                Tracks[trackGroup.Key].Children.Add(plan.Clip.Clip);
            }
        }

        _ = UpdateAdjacencyForTrack();
        UpdateTimelineWidth();
    }

    private void RestoreKeyboardMovePreview()
    {
        if (_keyboardMoveOriginalPlacement.Count == 0)
        {
            return;
        }

        foreach (var clip in _keyboardMoveClips)
        {
            if (!_keyboardMoveOriginalPlacement.TryGetValue(clip.Id, out var basePlacement))
            {
                continue;
            }

            if (!Tracks.ContainsKey(basePlacement.Track))
            {
                EnsureTrackExistsById(basePlacement.Track);
            }

            foreach (var track in Tracks.Values)
            {
                track.Children.Remove(clip.Clip);
            }

            clip.Clip.TranslationX = basePlacement.X;
            clip.Clip.TranslationY = 0;
            Tracks[basePlacement.Track].Children.Add(clip.Clip);
        }

        _ = UpdateAdjacencyForTrack();
        UpdateTimelineWidth();
    }

    private async Task ConfirmKeyboardMoveAsync()
    {
        if (!IsClipMoving)
        {
            return;
        }

        if (_keyboardMoveClips.Count == 0)
        {
            EndKeyboardMoveSession();
            return;
        }

        if (!_keyboardMoveHasMoved)
        {
            CancelPendingClipPlacement();
            return;
        }

        var changedCount = 0;
        foreach (var clip in _keyboardMoveClips)
        {
            foreach (var track in Tracks)
            {
                if (!track.Value.Children.Contains(clip.Clip))
                {
                    continue;
                }

                clip.origTrack = track.Key;
                clip.SubLayerIndex = track.Key;
                clip.origX = clip.Clip.TranslationX;
                clip.layoutX = clip.Clip.TranslationX;
                clip.layoutY = 0;
                clip.defaultY = 0;
                clip.MovingStatus = ClipMovingStatus.Free;
                clip.ShouldDisplayInUI = true;
                clip.Clip.IsVisible = true;
                string keyboardMoveClipName = GetClipNameForChangeReason(clip, clip.Id);

                OnClipChanged?.Invoke(clip.Id, new ClipUpdateEventArgs
                {
                    SourceId = clip.Id,
                    SourceName = keyboardMoveClipName,
                    Reason = ClipUpdateReason.ClipItselfMove,
                    DetailInfo = ClipUpdateEventArgs.BuildChangeReason(
                        ClipUpdateReason.ClipItselfMove,
                        keyboardMoveClipName,
                        $"Keyboard move committed to track {track.Key}, x={Math.Round(clip.Clip.TranslationX, 2)}")
                });

                changedCount++;
                break;
            }
        }

        CleanupGhostAndShadow();
        _ = UpdateAdjacencyForTrack();
        UpdateTimelineWidth();

        CancelPendingClipPlacement(restoreKeyboardPreview: false);
        SetStateOK();
        SetStatusText(Localized._Done);
        await RefreshSelectionUiAsync();
    }

    #endregion

    #region transform
    private void AddTransformClip(
        ClipElementUI prev, ClipElementUI next,
        Func<Guid, Guid, ITransform> factory,
        double startX, double width, int TrackId,
        Action<ClipElementUI>? ElementSetter = null)
    {
        Guid prevGuid = Guid.Empty;
        Guid nextGuid = Guid.Empty;
        if (prev is not null) Guid.TryParse(prev.Id, out prevGuid);
        if (next is not null) Guid.TryParse(next.Id, out nextGuid);

        var transform = factory(prevGuid, nextGuid);
        transform.BindedLeftClip = prevGuid;
        transform.BindedRightClip = nextGuid;
        transform.Duration = PixelToFrame(width);
        try
        {
            transform?.Init();
        }
        catch { }

        var elem = CreateAndAddClip(
            startX: startX + 3,
            width: width - 3,
            trackIndex: TrackId,
            labelText: transform?.Name ?? $"Transform:{transform?.TypeName}",
            background: new SolidColorBrush(Color.FromArgb("#AA33BBFF")),
            resolveOverlap: true);

        elem.ClipType = ClipMode.TransformClip;
        elem.FromPlugin = InternalPluginBase.InternalPluginBaseID;
        elem.TypeName = transform.TypeName;
        elem.ExtraData["transformPrevId"] = prev?.Id ?? string.Empty;
        elem.ExtraData["transformNextId"] = next?.Id ?? string.Empty;
        elem.ExtraData["transformTypeName"] = transform.TypeName;
        // Persist the transform instance so it can be re-created when the project is loaded.
        try
        {
            // Serialize using runtime type to preserve concrete properties (e.g. ExternalSourceTransform.SourcePath).
            elem.ExtraData["TransformElement"] = System.Text.Json.JsonSerializer.SerializeToElement(transform, transform.GetType());
        }
        catch { }
        elem.LeftHandle.IsVisible = false;
        elem.RightHandle.IsVisible = false;
        elem.LeftHandle.GestureRecognizers.Clear();
        elem.RightHandle.GestureRecognizers.Clear();

        // Adjust neighboring clips to make room for the transform visual.
        try
        {
            // Position the transform clip at the requested X
            elem.Clip.TranslationX = startX;
            elem.origX = startX;

            double half = width / 2.0;

            if (prev is not null)
            {
                double prevWidth = prev.Clip.WidthRequest > 0 ? prev.Clip.WidthRequest : prev.origLength;
                double newPrevWidth = Math.Max(MinClipWidth, prevWidth - half);
                prev.Clip.WidthRequest = newPrevWidth;
                prev.origLength = newPrevWidth;
                prev.lengthInFrame = PixelToFrame(newPrevWidth);
                // keep prev.Clip.TranslationX unchanged (shrinking from right)
                string prevName = GetClipNameForChangeReason(prev, prev.Id);
                OnClipChanged?.Invoke(prev.Id, new ClipUpdateEventArgs
                {
                    SourceId = prev.Id,
                    SourceName = prevName,
                    Reason = ClipUpdateReason.ClipResized,
                    DetailInfo = ClipUpdateEventArgs.BuildChangeReason(
                        ClipUpdateReason.ClipResized,
                        prevName,
                        $"Adjusted for transform insertion, new width={Math.Round(newPrevWidth, 2)}")
                });
            }

            if (next is not null)
            {
                double nextWidth = next.Clip.WidthRequest > 0 ? next.Clip.WidthRequest : next.origLength;
                double newNextWidth = Math.Max(MinClipWidth, nextWidth - half);
                // move next clip to the right by half, and shrink from left
                next.Clip.TranslationX = next.Clip.TranslationX + half;
                next.origX = next.origX + half;
                next.Clip.WidthRequest = newNextWidth;
                next.origLength = newNextWidth;
                next.lengthInFrame = PixelToFrame(newNextWidth);
                string nextName = GetClipNameForChangeReason(next, next.Id);
                OnClipChanged?.Invoke(next.Id, new ClipUpdateEventArgs
                {
                    SourceId = next.Id,
                    SourceName = nextName,
                    Reason = ClipUpdateReason.ClipResized,
                    DetailInfo = ClipUpdateEventArgs.BuildChangeReason(
                        ClipUpdateReason.ClipResized,
                        nextName,
                        $"Adjusted for transform insertion, shifted by {Math.Round(half, 2)} px, new width={Math.Round(newNextWidth, 2)}")
                });
            }
            ElementSetter?.Invoke(elem);

            _ = UpdateAdjacencyForTrack();
            UpdateTimelineWidth();
        }
        catch (Exception ex)
        {
            Log(ex, $"adjust neighbors for transform {elem.Id}", this);
        }

        LogDiagnostic($"Transform '{transform.TypeName}' clip added between '{prev?.Id ?? "none"}' and '{next?.Id ?? "none"}'.");
    }

    public bool AddTransformBetweenSelected(string typeKey, ClipElementUI? center, bool left, bool right)
         => center is not null
            && TransformServices.GetAvailableTransforms().TryGetValue(typeKey, out var factory)
            && AddTransformBetweenSelected(factory, center, left, right);

    public bool AddTransformBetweenSelected(Func<Guid, Guid, ITransform> transformFactory, ClipElementUI center, bool left, bool right, Action<ClipElementUI>? ElementSetter = null)
    {
        ArgumentNullException.ThrowIfNull(center, nameof(center));
        if (left && right) throw new InvalidOperationException("Cannot add a transform in both direction.");
        if (!left && !right) throw new InvalidOperationException("Cannot add a transform in neither direction.");

        var (leftNeighbor, rightNeighbor) = FindNeighbors(center);
        if ((left && leftNeighbor is null) || (right && rightNeighbor is null)) return false;

        const double TransformVisualWidth = 40.0;

        double selectedLeft = center.Clip.TranslationX;
        double selectedWidth = center.Clip.WidthRequest > 0 ? center.Clip.WidthRequest : center.origLength;
        double selectedRight = selectedLeft + selectedWidth;


        if (left)
        {
            double posX = selectedLeft - TransformVisualWidth / 2.0;
            AddTransformClip(leftNeighbor, center, transformFactory, posX, TransformVisualWidth, center.origTrack ?? 0, ElementSetter);
            SetStatusText(Localized.DraftPage_TransformAdded(leftNeighbor?.DisplayName ?? "left", center?.DisplayName ?? "right"));
            _ = UpdateAdjacencyForTrack(center.origTrack ?? 0);
            return true;
        }
        else if (right)
        {
            double posX = selectedRight - TransformVisualWidth / 2.0;
            AddTransformClip(center, rightNeighbor, transformFactory, posX, TransformVisualWidth, center.origTrack ?? 0, ElementSetter);
            SetStatusText(Localized.DraftPage_TransformAdded(center?.DisplayName ?? "left", rightNeighbor?.DisplayName ?? "right"));
            _ = UpdateAdjacencyForTrack(center.origTrack ?? 0);
            return true;
        }

        return false;
    }

    public ClipElementUI? _transformMenuActivatedCenterClip = null;
    public string _transformMenuActivatedHandle = "none";

    private void HandleTransformAdd(ClipElementUI center, bool left, bool right)
    {
        var (leftNeighbor, rightNeighbor) = FindNeighbors(center);
        if (left && leftNeighbor is null)
        {
            SetStatusText(Localized.DraftPage_AddClipView_AddTransform_CannotAdd_NoClipInLeft);
        }
        else if (right && rightNeighbor is null)
        {
            SetStatusText(Localized.DraftPage_AddClipView_AddTransform_CannotAdd_NoClipInRight);
        }
        else
        {

            _transformMenuActivatedCenterClip = center;
            _transformMenuActivatedHandle = left ? "left" : (right ? "right" : "none");
            AddClip_Clicked(this, EventArgs.Empty);
            AddClipView.MainTabView.SelectByTag("Transform");
        }

    }

    public void AddTransformToNeighbors(string type)
    {
        if ((_transformMenuActivatedCenterClip ?? _selected) is null)
        {
            SetStatusText(Localized.DraftPage_PropertyPanel_SelectToContinue);
            return;
        }
        AddTransformBetweenSelected(type, _transformMenuActivatedCenterClip ?? _selected, _transformMenuActivatedHandle == "left", _transformMenuActivatedHandle == "right");
    }

    private void RemoveTransformsReferencingClip(string clipId)
    {
        if (string.IsNullOrWhiteSpace(clipId)) return;

        var transformKeys = Clips.Where(kv => kv.Value != null && kv.Value.ClipType == ClipMode.TransformClip)
            .Where(kv =>
            {
                try
                {
                    var ed = kv.Value.ExtraData;
                    if (ed == null) return false;
                    if (ed.TryGetValue("transformPrevId", out var p) && p?.ToString() == clipId) return true;
                    if (ed.TryGetValue("transformNextId", out var n) && n?.ToString() == clipId) return true;
                }
                catch { }
                return false;
            })
            .Select(kv => kv.Key)
            .ToList();

        foreach (var key in transformKeys)
        {
            if (Clips.TryRemove(key, out var removed))
            {
                try
                {
                    if (removed?.Clip != null)
                    {
                        // remove visual from overlay if present
                        try { OverlayLayer?.Children.Remove(removed.Clip); } catch { }
                        // also remove from its track container
                        if (removed.origTrack is int tr && Tracks.TryGetValue(tr, out var tlayout))
                        {
                            try { tlayout.Children.Remove(removed.Clip); } catch { }
                        }
                        else
                        {
                            foreach (var t in Tracks.Values.ToList())
                                try { t.Children.Remove(removed.Clip); } catch { }
                        }
                    }
                }
                catch { }
                LogDiagnostic($"transform clip {key} removed because it referenced {clipId}.");
                SetStatusText(Localized.DraftPage_Removed);
            }
        }
    }


    #endregion

    #region resize clip
    private void LeftHandlePanded(object? sender, PanUpdatedEventArgs e)
    {
        if (sender is not Border border) return;
        if (border.BindingContext is not ClipElementUI clip) return;

        clip.MovingStatus = ClipMovingStatus.Resize;

        switch (e.StatusType)
        {
            case GestureStatus.Started:
                clip.handleLayoutX = border.TranslationX;
                clip.layoutX = clip.Clip.TranslationX;
                clip.Clip.BatchBegin();
                HandleStartWidth.AddOrUpdate(clip.Id, clip.Clip.WidthRequest, (_, __) => clip.Clip.WidthRequest);
                break;

            case GestureStatus.Running:
                double startWidth = HandleStartWidth.TryGetValue(clip.Id, out var sw) ? sw : clip.Clip.WidthRequest;
                double newWidth = Math.Max(MinClipWidth, startWidth - e.TotalX);
                if (clip.isInfiniteLength || clip.maxFrameCount == 0) goto go_resize;
                //Log($"Clip's new width {newWidth}, max width {FrameToPixel(clip.maxFrameCount)}");
                double lengthAvailable = FrameToPixel(clip.relativeStartFrame) * clip.SecondPerFrameRatio * tracksZoomOffest;
                bool reachStartOfSrc = !clip.isInfiniteLength && startWidth + lengthAvailable - newWidth > -0.5d;
                bool isLongerThanSrc = newWidth <= FrameToPixel(clip.maxFrameCount) * clip.SecondPerFrameRatio * tracksZoomOffest;
                if (!reachStartOfSrc || !isLongerThanSrc)
                {
                    clip.Clip.TranslationX = (clip.layoutX + lengthAvailable) * clip.SecondPerFrameRatio * tracksZoomOffest;
                    SetStatusText(Localized.DraftPage_ReachLimit($"{clip.maxFrameCount * SecondsPerFrame}s"));
                    break;
                }

            go_resize:
                clip.Clip.TranslationX = clip.layoutX + e.TotalX;
                clip.Clip.WidthRequest = newWidth;
                SetStatusText(Localized.DraftPage_WaitForUser);
                break;

            case GestureStatus.Completed:
                HandleStartWidth.TryRemove(clip.Id, out _);
                clip.lengthInFrame = PixelToFrame((clip.Clip.WidthRequest > 0) ? clip.Clip.WidthRequest : clip.Clip.Width);
                double deltaPx = clip.Clip.TranslationX - clip.layoutX;
                long deltaFrames = (long)Math.Round(deltaPx * FramePerPixel * clip.SecondPerFrameRatio * tracksZoomOffest);
                long newRel = (long)clip.relativeStartFrame + deltaFrames;
                if (newRel < 0) newRel = 0;
                uint maxRelAllowed = (clip.maxFrameCount >= clip.lengthInFrame) ? (clip.maxFrameCount - clip.lengthInFrame) : 0u;
                if ((ulong)newRel > maxRelAllowed) newRel = maxRelAllowed;
                clip.relativeStartFrame = (uint)newRel;
                clip.Clip.BatchCommit();
                string leftResizeClipName = GetClipNameForChangeReason(clip, clip.Id);
                OnClipChanged?.Invoke(clip.Id, new ClipUpdateEventArgs
                {
                    SourceId = clip.Id,
                    SourceName = leftResizeClipName,
                    Reason = ClipUpdateReason.ClipResized,
                    DetailInfo = ClipUpdateEventArgs.BuildChangeReason(
                        ClipUpdateReason.ClipResized,
                        leftResizeClipName,
                        $"Left handle resize, lengthFrames={clip.lengthInFrame}, relativeStartFrame={clip.relativeStartFrame}")
                });
                clip.MovingStatus = ClipMovingStatus.Free;
                LogDiagnostic($"clip {clip.Id} resized. x:{border.TranslationX} width:{border.WidthRequest}");
                break;
        }
    }

    private void RightHandlePaned(object? sender, PanUpdatedEventArgs e)
    {
        if (sender is not Border border) return;
        if (border.BindingContext is not ClipElementUI clip) return;

        clip.MovingStatus = ClipMovingStatus.Resize;

        switch (e.StatusType)
        {
            case GestureStatus.Started:
                clip.handleLayoutX = border.TranslationX;
                clip.layoutX = clip.Clip.TranslationX;
                clip.Clip.BatchBegin();
                HandleStartWidth.AddOrUpdate(clip.Id, clip.Clip.WidthRequest, (_, __) => clip.Clip.WidthRequest);
                break;

            case GestureStatus.Running:
                double startWidth = HandleStartWidth.TryGetValue(clip.Id, out var sw) ? sw : clip.Clip.WidthRequest;
                double newWidth = Math.Max(MinClipWidth, startWidth + e.TotalX);
                bool isLongerThanSrc = newWidth + FrameToPixel(clip.relativeStartFrame) * clip.SecondPerFrameRatio * tracksZoomOffest >= FrameToPixel(clip.maxFrameCount) * clip.SecondPerFrameRatio * tracksZoomOffest;
                if (clip.isInfiniteLength || clip.maxFrameCount == 0 || !isLongerThanSrc)
                {
                    clip.Clip.WidthRequest = newWidth;
                    SetStatusText(Localized.DraftPage_WaitForUser);
                }
                else
                {
                    clip.Clip.WidthRequest = (FrameToPixel(clip.maxFrameCount) - FrameToPixel(clip.relativeStartFrame)) * clip.SecondPerFrameRatio * tracksZoomOffest;
                    SetStatusText(Localized.DraftPage_ReachLimit($"{clip.maxFrameCount * SecondsPerFrame}s"));
                }
                break;

            case GestureStatus.Completed:
                HandleStartWidth.TryRemove(clip.Id, out _);
                clip.Clip.BatchCommit();
                clip.lengthInFrame = PixelToFrame((clip.Clip.WidthRequest > 0) ? clip.Clip.WidthRequest : clip.Clip.Width);
                string rightResizeClipName = GetClipNameForChangeReason(clip, clip.Id);
                OnClipChanged?.Invoke(clip.Id, new ClipUpdateEventArgs
                {
                    SourceId = clip.Id,
                    SourceName = rightResizeClipName,
                    Reason = ClipUpdateReason.ClipResized,
                    DetailInfo = ClipUpdateEventArgs.BuildChangeReason(
                        ClipUpdateReason.ClipResized,
                        rightResizeClipName,
                        $"Right handle resize, lengthFrames={clip.lengthInFrame}, relativeStartFrame={clip.relativeStartFrame}")
                });
                clip.MovingStatus = ClipMovingStatus.Free;
                LogDiagnostic("clip {clip.Id} resized. x:{border.TranslationX} width:{border.WidthRequest}");
                break;
        }
    }
    #endregion

    #region properties
    private async Task<View> BuildPropertyPanel(ClipElementUI clip)
    {
        if (clip is null)
        {
            Log("A null clip is provided.", "error");
            SetStateFail("No clip selected.");
            return new Label
            {
                Text = "No clip are selected. This SHOULD is a bug, please feedback.\r\n" +
                      $"{Environment.StackTrace.Split(Environment.NewLine).Skip(1).Aggregate((a, b) => $"{a}{Environment.NewLine}{b}")}",
            };
        }
        try
        {
        }
        catch (Exception ex)
        {
            Log(ex, $"build tools for {clip.Id}", this);

        }
        return await infoBuilder.Build(clip, OnClipPropertiesChanged);

    }


    private double CalculateExtendToWholeDraftWidth(ClipElementUI clip)
    {
        if (clip?.Clip == null) return clip?.origLength ?? 0;
        double desiredWidth = Math.Max(MinClipWidth, TrackContentLayout.Width + 50);
        return desiredWidth;
    }

    public async void OnClipPropertiesChanged(object? sender, PropertyPanelPropertyChangedEventArgs e)
    {
        static bool TryReadBool(object? raw, out bool result)
        {
            if (raw is bool b)
            {
                result = b;
                return true;
            }
            if (raw is JsonElement je)
            {
                if (je.ValueKind == JsonValueKind.True)
                {
                    result = true;
                    return true;
                }
                if (je.ValueKind == JsonValueKind.False)
                {
                    result = false;
                    return true;
                }
                if (je.ValueKind == JsonValueKind.String && bool.TryParse(je.GetString(), out var parsed))
                {
                    result = parsed;
                    return true;
                }
            }
            if (bool.TryParse(raw?.ToString(), out var fallback))
            {
                result = fallback;
                return true;
            }

            result = false;
            return false;
        }

        if (_selected is null) return;
        var clip = _selected;

        if (e.Id == "__REFRESH_PANEL__")
        {
            Clips[clip.Id] = clip;
            await ReRenderUI();
            RefreshPropertyPanel(clip);
            return;
        }

        if (e.Id == "ExtendToWholeDraft")
        {
            bool requested = TryReadBool(e.Value, out var val) && val;
            clip.ExtraData ??= new Dictionary<string, object>();

            if (!requested)
            {
                // 关闭ExtendToWholeDraft时，恢复原始宽度
                clip.ExtraData["ExtendToWholeDraft"] = false;
                // 还原到原始长度
                clip.Clip.WidthRequest = clip.origLength * clip.SecondPerFrameRatio;
            }
            else
            {
                bool isSubTrack = (clip.origTrack ?? -1) >= SubTrackOffset;
                bool hasOtherClipOnSameTrack = Clips.Values.Any(c =>
                    ShouldParticipateInTimelineLayout(c)
                    && c.Id != clip.Id
                    && c.origTrack == clip.origTrack);

                if (!isSubTrack)
                {
                    clip.ExtraData["ExtendToWholeDraft"] = false;
                    RefreshPropertyPanel(clip);

                }
                else if (hasOtherClipOnSameTrack)
                {
                    clip.ExtraData["ExtendToWholeDraft"] = false;
                    RefreshPropertyPanel(clip);

                }
                else
                {
                    // 接受ExtendToWholeDraft=true，计算并设置宽度
                    clip.ExtraData["ExtendToWholeDraft"] = true;
                    double extendedWidth = CalculateExtendToWholeDraftWidth(clip);
                    clip.Clip.WidthRequest = extendedWidth;
                    RefreshPropertyPanel(clip);

                }
            }
        }

        SetStatusText($"{clip.DisplayName}'s property '{e.Id}' changed from {e.OriginValue} to {e.Value}");

        Clips[clip.Id] = clip;

        OnClipChanged?.Invoke(this, new ClipUpdateEventArgs { Reason = ClipUpdateReason.PropertyChanged, SourceId = clip.Id, SourceName = clip.DisplayName, DetailInfo = e.Id, NoSave = false });
        HistorySubWindow.Content = new DraftSettingPage(this).BuildHistoryTab();


        await ReRenderUI();

        SetStatusText(Localized.DraftPage_ClipPropertyUpdated(clip.DisplayName));


    }

    public async void RefreshPropertyPanel(ClipElementUI clip)
    {
        var popupPanel = await BuildPropertyPanel(clip);
        Popup.Content = WrapPropertyPanelContent(clip, popupPanel);
        RightContentBorder.Content = await BuildPropertyPanel(clip);
    }

    private static View WrapPropertyPanelContent(ClipElementUI clip, View panel)
    {
        if (clip.ClipType == ClipMode.VideoClip || clip.ClipType == ClipMode.PhotoClip)
        {
            return panel;
        }

        return new ScrollView { Content = panel };
    }


    #endregion

    #region asset
    private async Task AddAsset(string path)
    {
        SetStateBusy(Localized.DraftPage_PrepareAsset);
        try
        {

            if (Assets.Values.Any((v) => v.Name == Path.GetFileNameWithoutExtension(path)))
            {
                var existing = Assets.Values.First((v) => v.Name == Path.GetFileNameWithoutExtension(path));

                string opt = await DisplayActionSheetAsync(
                    Localized.DraftPage_DuplicatedAsset(Path.GetFileNameWithoutExtension(path), existing.Name),
                    null,
                    null,
                    [Localized.DraftPage_DuplicatedAsset_Relpace, Localized.DraftPage_DuplicatedAsset_Skip, Localized.DraftPage_DuplicatedAsset_Together]
                );

                if (opt == Localized.DraftPage_DuplicatedAsset_Relpace)
                {
                    Assets.TryRemove(existing.AssetId, out _);
                    Log($"Replaced existing asset {existing.Name} with new one from {path}");
                }
                else if (opt == Localized.DraftPage_DuplicatedAsset_Skip)
                {
                    Log($"Skipped adding duplicated asset from {path}");
                    SetStateOK(Localized.DraftPage_AssetAdded(Path.GetFileNameWithoutExtension(path)));
                    return;
                }
                else
                {
                    Log($"Adding duplicated asset from {path} together with existing one.");
                }
            }
            var item = AssetDatabase.Create(path, System.IO.Path.GetFileNameWithoutExtension(path), AssetItem.GetAssetType(path));
            if (item is null)
            {
                await DisplayAlertAsync(Localized._Error, Localized.DraftPage_Asset_InvaildSrc(System.IO.Path.GetFileNameWithoutExtension(path)), Localized._OK);
                return;
            }
            item.Path = path;
            var cid = item.AssetId;
            Log($"Added asset '{item.Path}'s info: {item.FrameCount} frames, {1f / item.SecondPerFrame}fps, {item.SecondPerFrame}spf, {item.FrameCount * item.SecondPerFrame} s");
            Assets.AddOrUpdate(cid, item, (_, _) => item);
            Dispatcher.Dispatch(async () =>
            {
                await HidePopup();
                AssetPanelButton_Clicked(this, new());
            });
            SetStateOK(Localized.DraftPage_AssetAdded(Path.GetFileNameWithoutExtension(path)));
            var createProxy = (ProxyOption != "never") && ((ProxyOption == "always") || await DisplayAlertAsync(Localized.DraftPage_CreateProxy(item.Name), Localized.DraftPage_CreateProxy_Info, Localized._Confirm, Localized._Cancel));
            var task = new DraftTasks(cid, (c) => Task.Run(cancellationToken: c, action: async () =>
            {
                if (createProxy)
                {
                    var proxiedPath = Path.Combine(WorkingPath, "proxy", $"{Path.GetFileNameWithoutExtension(path)}.proxy.mp4");
                    VideoResizer.ReencodeToResolution(item.Path, proxiedPath, 1280, 720, "libx264");
                }
                Assets[cid].SourceHash = await HashServices.ComputeFileHashAsync(path, null, c);
            }), $"Add asset {item.Name}", $"Add asset {item.Name}");
            RunningTasks.AddOrUpdate(cid, task, (_, _) => task);

        }
        catch (Exception ex)
        {
            Log(ex, $"Importing file {path}", this);
            SetStatusText(Localized._ExceptionTemplate(ex));
        }
    }
    private async void AssetPanelButton_Clicked(object sender, EventArgs e)
    {
        try
        {
            var draftPage = this;
            var assetView = new ProjectAssetView(ref draftPage);
            await ShowAPopup(assetView);
        }
        catch (Exception ex)
        {
            Log(ex, "Show asset panel", this);
            throw;
        }
    }
    #endregion

    #region task
    public async Task<ScrollView> CreateJobsPanel()
    {
        var ppb = new PropertyPanelBuilder().AddText(new SingleLineLabel(Localized.DraftPage_Tasks_Title, 20));
        if (RunningTasks.IsEmpty)
        {
            ppb.AddText(Localized.DraftPage_Tasks_NoneTasks);
        }
        else
        {
            foreach (var item in RunningTasks)
            {
                ppb.AddSeparator();
                var task = item.Value;
                ppb.AddText(new TitleAndDescriptionLineLabel(item.Value.Name, item.Value.Description))
                    .AddText(item.Value.IsRunningDisplay);
                if (task.InnerTask.IsCompleted)
                {
                    ppb.AddButton($"Remove,{item.Key}", Localized._Remove);
                }
                else
                {
                    ppb.AddButton($"Cancel,{item.Key}", Localized._Cancel);

                }

            }
        }
#if DEBUG
        ppb.AddButton("Add some task", async (s, e) =>
        {
            var t = new DraftTasks("123", (c) => Thread.Sleep(9999), "A sleeping thread", "nothing here");
            RunningTasks.TryAdd(t.Id, t);
            var t1 = new DraftTasks("456", (c) => Task.Delay(99999, c), "A sleeping task with cts", "nothing here");
            RunningTasks.TryAdd(t1.Id, t1);
            Popup.Content = await CreateJobsPanel();

        });
#endif
        ppb.ListenToChanges(async (a) =>
        {
            var action = a.Id.Split(',')[0];
            var id = a.Id.Split(',', 2)[1];
            if (!RunningTasks.TryGetValue(id, out var task))
            {
                await DisplayAlertAsync(Localized._Error, $"Task {id} not found in Tasks.", Localized._OK);
                return;
            }
            switch (action)
            {
                case "Cancel":
                    {
                        var sure = await DisplayAlertAsync(Localized._Warn, Localized.DraftPage_Tasks_CancelWarn(task.Name), Localized._Confirm, Localized._Cancel);
                        if (sure) task.Cancel();
                        break;
                    }
                case "Remove":
                    {
                        RunningTasks.Remove(id, out _);
                        break;
                    }
                default:
                    break;
            }

            Popup.Content = await CreateJobsPanel();
        });
        return ppb.BuildWithScrollView();
    }

    public async Task OnManageJobsClicked()
    {
        await HidePopup();
        await ShowAPopup(await CreateJobsPanel());
    }
    #endregion

    #region adjust track and clip
    public async Task CombineClipsAsGroupAsync(IEnumerable<ClipElementUI> clips)
    {
        ArgumentNullException.ThrowIfNull(clips);

        try
        {
            var clipIds = clips
                .Select(c => c?.Id)
                .Where(id => !string.IsNullOrWhiteSpace(id))
                .Select(id => id!)
                .Distinct(StringComparer.Ordinal)
                .ToArray();

            if (clipIds.Length < 2)
            {
                return;
            }

            // Run on the next UI cycle so placement status/selection updates can finish first.
            await Task.Yield();

            ClearSelectionInternal();
            foreach (var clipId in clipIds)
            {
                if (Clips.TryGetValue(clipId, out var clip) && clip.ShouldDisplayInUI)
                {
                    AddClipToSelection(clip);
                }
            }

            if (_selectedClipIds.Count < 2)
            {
                await RefreshSelectionUiAsync();
                return;
            }

            await CombineSelection();
        }
        catch (Exception ex)
        {
            Log(ex, "CombineClipsAsGroupAsync", this);
        }
    }

    private async Task CombineSelection()
    {
        var selected = _selectedClipIds
            .Select(id => Clips.TryGetValue(id, out var clip) ? clip : null)
            .Where(c => c is not null)
            .Cast<ClipElementUI>()
            .Where(c => !c.Id.StartsWith("ghost_") && !c.Id.StartsWith("shadow_"))
            .ToList();

        if (selected.Count < 2)
        {
            SetStatusText("Please select at least 2 clips to combine");
            return;
        }

        var located = new List<(ClipElementUI clip, int layer)>();
        foreach (var clip in selected)
        {
            int currentLayer = clip.origTrack ?? -1;
            if (currentLayer < 0 || !Tracks.TryGetValue(currentLayer, out var trackLayout) || !trackLayout.Children.Contains(clip.Clip))
            {
                var found = Tracks.FirstOrDefault(kv => kv.Value.Children.Contains(clip.Clip));
                if (found.Value is null)
                {
                    continue;
                }
                currentLayer = found.Key;
            }
            located.Add((clip, currentLayer));
        }

        if (located.Count < 2)
        {
            SetStatusText("No enough visible clips found to combine");
            return;
        }

        int targetLayer = located.Min(x => x.layer);
        if (!Tracks.TryGetValue(targetLayer, out var targetTrack))
        {
            SetStatusText($"Target track {targetLayer} not found");
            return;
        }

        double minX = double.MaxValue;
        double maxX = double.MinValue;

        foreach (var (clip, oldLayer) in located)
        {
            double start = clip.Clip.TranslationX;
            double width = clip.Clip.WidthRequest > 0 ? clip.Clip.WidthRequest : clip.origLength;
            if (width <= 0) width = MinClipWidth;

            minX = Math.Min(minX, start);
            maxX = Math.Max(maxX, start + width);

            clip.SubLayerIndex = oldLayer;
            clip.origTrack = targetLayer;
            clip.ShouldDisplayInUI = false;
            clip.Clip.IsVisible = false;

            if (Tracks.TryGetValue(oldLayer, out var oldTrack) && !ReferenceEquals(oldTrack, targetTrack) && oldTrack.Children.Contains(clip.Clip))
            {
                oldTrack.Children.Remove(clip.Clip);
                targetTrack.Children.Add(clip.Clip);
            }
        }

        if (minX == double.MaxValue || maxX <= minX)
        {
            SetStatusText("Failed to determine combined area");
            return;
        }

        double markerWidth = Math.Max(MinClipWidth, maxX - minX);
        var marker = CreateAndAddClip(
            startX: minX,
            width: markerWidth,
            trackIndex: targetLayer,
            id: $"marking_{Guid.NewGuid():N}",
            labelText: $"Group ({located.Count})",
            background: new SolidColorBrush(Color.FromRgba(51, 136, 255, 96)),
            prototype: null,
            resolveOverlap: false,
            relativeStart: 0,
            maxFrames: (uint)Math.Max(1, PixelToFrame(markerWidth))
        );

        marker.ClipType = ClipMode.MarkingClip;
        marker.FromPlugin = InternalPluginBase.InternalPluginBaseID;
        marker.TypeName = "MarkingClip";
        marker.SubLayerIndex = targetLayer;
        marker.ShouldDisplayInUI = true;
        marker.LeftHandle.IsVisible = false;
        marker.RightHandle.IsVisible = false;
        marker.LeftHandle.GestureRecognizers.Clear();
        marker.RightHandle.GestureRecognizers.Clear();
        marker.ExtraData["GroupedClipIds"] = located.Select(x => x.clip.Id).ToArray();
        marker.ExtraData["IsGroupingMarker"] = true;

        ClearSelectionInternal();
        AddClipToSelection(marker);

        await UpdateAdjacencyForTrack(targetLayer);
        await ReRenderUI();
        await RefreshSelectionUiAsync();
        string markerName = GetClipNameForChangeReason(marker, marker.Id);

        OnClipChanged?.Invoke(marker.Id, new ClipUpdateEventArgs
        {
            SourceId = marker.Id,
            SourceName = markerName,
            Reason = ClipUpdateReason.ClipGrouped,
            DetailInfo = ClipUpdateEventArgs.BuildChangeReason(
                ClipUpdateReason.ClipGrouped,
                markerName,
                $"Grouped {located.Count} clips into marker on track {targetLayer}")
        });

        SetStateOK();
        SetStatusText($"Combined {located.Count} clips in track {targetLayer}");

    }

    private static List<string> ExtractGroupedClipIds(ClipElementUI marker)
    {
        var result = new List<string>();
        if (marker.ExtraData == null || !marker.ExtraData.TryGetValue("GroupedClipIds", out var groupedObj) || groupedObj == null)
        {
            return result;
        }

        switch (groupedObj)
        {
            case string[] strArray:
                result.AddRange(strArray.Where(s => !string.IsNullOrWhiteSpace(s)).Select(s => s.Trim()));
                break;
            case IEnumerable<string> strEnumerable:
                result.AddRange(strEnumerable.Where(s => !string.IsNullOrWhiteSpace(s)).Select(s => s.Trim()));
                break;
            case JsonElement je when je.ValueKind == JsonValueKind.Array:
                foreach (var item in je.EnumerateArray())
                {
                    if (item.ValueKind == JsonValueKind.String)
                    {
                        var id = item.GetString();
                        if (!string.IsNullOrWhiteSpace(id)) result.Add(id.Trim());
                    }
                }
                break;
            case IEnumerable<object> objEnumerable:
                result.AddRange(objEnumerable.Select(o => o?.ToString()).Where(s => !string.IsNullOrWhiteSpace(s)).Select(s => s!.Trim()));
                break;
            default:
                var single = groupedObj.ToString();
                if (!string.IsNullOrWhiteSpace(single)) result.Add(single.Trim());
                break;
        }

        return result.Distinct().ToList();
    }

    public async Task UnbindGroupingMarkerAsync(ClipElementUI marker)
    {
        if (marker == null)
        {
            return;
        }

        if (marker.ClipType != ClipMode.MarkingClip)
        {
            SetStatusText("Selected clip is not a grouping marker");
            return;
        }

        var groupedIds = ExtractGroupedClipIds(marker);
        if (groupedIds.Count == 0)
        {
            SetStatusText("No grouped clips metadata found");
            return;
        }

        int fallbackTrack = marker.origTrack ?? marker.SubLayerIndex;
        int restoredCount = 0;
        string markerNameForUngroup = GetClipNameForChangeReason(marker, marker.Id);

        foreach (var clipId in groupedIds)
        {
            if (!Clips.TryGetValue(clipId, out var groupedClip) || groupedClip?.Clip == null)
            {
                continue;
            }

            int targetTrack = groupedClip.SubLayerIndex;
            if (targetTrack < 0 || !Tracks.ContainsKey(targetTrack))
            {
                targetTrack = fallbackTrack;
            }

            if (!Tracks.TryGetValue(targetTrack, out var targetTrackLayout))
            {
                continue;
            }

            foreach (var kv in Tracks)
            {
                if (!ReferenceEquals(kv.Value, targetTrackLayout) && kv.Value.Children.Contains(groupedClip.Clip))
                {
                    kv.Value.Children.Remove(groupedClip.Clip);
                }
            }

            if (!targetTrackLayout.Children.Contains(groupedClip.Clip))
            {
                targetTrackLayout.Children.Add(groupedClip.Clip);
            }

            groupedClip.origTrack = targetTrack;
            groupedClip.ShouldDisplayInUI = true;
            groupedClip.Clip.IsVisible = true;
            restoredCount++;
            string groupedClipName = GetClipNameForChangeReason(groupedClip, groupedClip.Id);

            OnClipChanged?.Invoke(groupedClip.Id, new ClipUpdateEventArgs
            {
                SourceId = groupedClip.Id,
                SourceName = groupedClipName,
                Reason = ClipUpdateReason.ClipUngrouped,
                DetailInfo = ClipUpdateEventArgs.BuildChangeReason(
                    ClipUpdateReason.ClipUngrouped,
                    groupedClipName,
                    $"Ungrouped from marker {markerNameForUngroup} to track {targetTrack}")
            });
        }

        RemoveClipFromSelection(marker);

        if (marker.origTrack is int markerTrack && Tracks.TryGetValue(markerTrack, out var markerTrackLayout))
        {
            markerTrackLayout.Children.Remove(marker.Clip);
        }
        else
        {
            foreach (var track in Tracks.Values)
            {
                track.Children.Remove(marker.Clip);
            }
        }

        Clips.TryRemove(marker.Id, out _);

        ClearSelectionInternal();
        foreach (var clipId in groupedIds)
        {
            if (Clips.TryGetValue(clipId, out var groupedClip) && groupedClip.ShouldDisplayInUI)
            {
                AddClipToSelection(groupedClip);
            }
        }

        await UpdateAdjacencyForTrack();
        await ReRenderUI();
        await RefreshSelectionUiAsync();

        SetStateOK();
        SetStatusText($"Unbound {restoredCount} clips");
    }

    private async Task ReRenderUI()
    {
        SetStateBusy(Localized._Processing);
        try
        {
            var snapshot = Clips.ToList();

            foreach (var kv in snapshot)
            {
                var key = kv.Key;
                var clip = kv.Value;
                if (string.IsNullOrEmpty(key)) continue;
                if (key.StartsWith("ghost_") || key.StartsWith("shadow_")) continue;
                if (clip == null) continue;

                var border = clip.Clip;
                if (border == null) continue;

                await Dispatcher.DispatchAsync(() =>
                {
                    try
                    {
                        border.BindingContext = clip;
                        clip.Clip = border;
                        border.IsVisible = clip.ShouldDisplayInUI;

                        // Update children: find label(s) and handle borders, rebind them and update texts
                        void UpdateLayoutChildren(Microsoft.Maui.Controls.Layout layout)
                        {
                            Border foundLeft = null;
                            Border foundRight = null;

                            foreach (var child in layout.Children)
                            {
                                if (child is Microsoft.Maui.Controls.Label lab)
                                {
                                    lab.Text = clip.DisplayName;
                                }
                                else if (child is Border b)
                                {
                                    b.BindingContext = clip;

                                    try
                                    {
                                        var col = Grid.GetColumn(b);
                                        if (col == 0) foundLeft = b;
                                        else if (col == 2) foundRight = b;
                                    }
                                    catch { /* ignore if not in a grid */ }
                                }
                                else if (child is Microsoft.Maui.Controls.Layout subLayout)
                                {
                                    // nested layout: search for labels/handles inside
                                    foreach (var sub in subLayout.Children)
                                    {
                                        if (sub is Microsoft.Maui.Controls.Label sl) sl.Text = clip.DisplayName;
                                        if (sub is Border sb)
                                        {
                                            sb.BindingContext = clip;
                                            try
                                            {
                                                var col2 = Grid.GetColumn(sb);
                                                if (col2 == 0) foundLeft = sb;
                                                else if (col2 == 2) foundRight = sb;
                                            }
                                            catch { }
                                        }
                                    }
                                }
                            }

                            // Assign discovered handles back to the model so later code relying on those refs keeps working
                            if (foundLeft != null) clip.LeftHandle = foundLeft;
                            if (foundRight != null) clip.RightHandle = foundRight;
                        }

                        if (border.Content is Microsoft.Maui.Controls.Grid g)
                        {
                            // grid may contain handles and nested layout
                            UpdateLayoutChildren(g);
                        }
                        else if (border.Content is Microsoft.Maui.Controls.Layout layout)
                        {
                            UpdateLayoutChildren(layout);
                        }

                        // Re-apply speed/width calculation and update length-in-frames
                        try
                        {
                            clip.ApplySpeedRatio();

                            static void RemovePanGestures(View? view)
                            {
                                if (view == null) return;
                                var panGestures = view.GestureRecognizers
                                    .OfType<PanGestureRecognizer>()
                                    .Cast<IGestureRecognizer>()
                                    .ToList();
                                foreach (var gesture in panGestures)
                                {
                                    view.GestureRecognizers.Remove(gesture);
                                }
                            }

                            static bool HasPanGesture(View? view)
                                => view != null && view.GestureRecognizers.OfType<PanGestureRecognizer>().Any();

                            // 如果启用了ExtendToWholeDraft，重新计算宽度以延伸到整个项目
                            if (clip.ExtraData != null &&
                                clip.ExtraData.TryGetValue("ExtendToWholeDraft", out var extendValue) &&
                                extendValue is bool isExtended && isExtended)
                            {
                                double extendedWidth = CalculateExtendToWholeDraftWidth(clip);
                                clip.Clip.WidthRequest = extendedWidth;
                                clip.LeftHandle.IsVisible = false;
                                clip.RightHandle.IsVisible = false;
                                clip.Clip.StrokeShape = new Rectangle
                                {
                                };

                                // ExtendToWholeDraft 的 clip 不允许拖动或拉伸，移除所有 Pan 手势
                                RemovePanGestures(clip.Clip);
                                RemovePanGestures(clip.LeftHandle);
                                RemovePanGestures(clip.RightHandle);
                            }
                            else
                            {
                                clip.LeftHandle.IsVisible = true;
                                clip.RightHandle.IsVisible = true;
                                clip.Clip.StrokeShape = new RoundRectangle
                                {
                                    CornerRadius = new Microsoft.Maui.CornerRadius(20)
                                };
                                // 关闭 ExtendToWholeDraft 后恢复可拖动/拉伸的 Pan 手势
                                if (!HasPanGesture(clip.Clip))
                                {
                                    var clipPanGesture = new PanGestureRecognizer();
                                    clipPanGesture.PanUpdated += (s, e) => ClipPaned(clip.Clip, e);
                                    clip.Clip.GestureRecognizers.Add(clipPanGesture);
                                }

                                if (!HasPanGesture(clip.LeftHandle))
                                {
                                    var leftHandleGesture = new PanGestureRecognizer();
                                    leftHandleGesture.PanUpdated += (s, e) => LeftHandlePanded(clip.LeftHandle, e);
                                    clip.LeftHandle.GestureRecognizers.Add(leftHandleGesture);
                                }

                                if (!HasPanGesture(clip.RightHandle))
                                {
                                    var rightHandleGesture = new PanGestureRecognizer();
                                    rightHandleGesture.PanUpdated += (s, e) => RightHandlePaned(clip.RightHandle, e);
                                    clip.RightHandle.GestureRecognizers.Add(rightHandleGesture);
                                }
                            }
                        }
                        catch (Exception ex)
                        {
                            Log(ex, "ApplySpeedRatio in ReRenderUI", this);
                        }

                        // Update cached length in frames to match actual visual width
                        try
                        {
                            var w = (!double.IsNaN(border.Width) && border.Width > 0) ? border.Width : border.WidthRequest;
                            clip.lengthInFrame = PixelToFrame(w);
                        }
                        catch { /* non-critical */ }
                    }
                    catch (Exception ex)
                    {
                        Log(ex, "ReRenderUI update clip", this);
                    }
                });
            }

            // 更新所有已启用ExtendToWholeDraft的clips，使其宽度能随着其他clips的变化而调整
            UpdateAllExtendToWholeDraftClips();
            foreach (var item in new string[] { nameof(SelectedAnyClip), nameof(_ShouldShowClipMoveControlInCenterInfoBar), nameof(_ShouldShowCenterCompactControlGrid), nameof(UseCompactLayout), nameof(MultiSelectEnabled) })
            {
                OnPropertyChanged(item);
            }
            await UpdateAdjacencyForTrack();
        }
        finally
        {
            SetStateOK();
        }
    }


    /// <summary>
    /// 更新所有已启用ExtendToWholeDraft的chips的宽度
    /// </summary>
    private void UpdateAllExtendToWholeDraftClips()
    {
        foreach (var clip in Clips.Values)
        {
            if (clip == null || clip.Clip == null) continue;
            if (!ShouldParticipateInTimelineLayout(clip)) continue;

            // 检查是否启用了ExtendToWholeDraft
            if (clip.ExtraData != null &&
                clip.ExtraData.TryGetValue("ExtendToWholeDraft", out var extendValue) &&
                extendValue is bool isExtended && isExtended)
            {
                try
                {
                    double extendedWidth = CalculateExtendToWholeDraftWidth(clip);
                    clip.Clip.TranslationX = 0;
                    clip.Clip.WidthRequest = extendedWidth;
                }
                catch (Exception ex)
                {
                    Log(ex, "UpdateAllExtendToWholeDraftClips", this);
                }
            }
        }
    }

    public async Task UpdateAdjacencyForTrack()
    {
        foreach (var item in Tracks.Keys)
        {
            await UpdateAdjacencyForTrack(item);
        }
    }

    private async Task UpdateAdjacencyForTrack(int trackIndex)
    {
        SetStateBusy(Localized._Processing);
        if (!Tracks.TryGetValue(trackIndex, out var track)) return;
        var byorder = track.Children.OfType<Border>()
            .Select(b => b.BindingContext)
            .OfType<ClipElementUI>()
            .Where(ShouldParticipateInTimelineLayout)
            .ToList();

        const double defaultRadius = 20.0;

        // Use a local radius array to avoid races between concurrent UpdateAdjacencyForTrack calls
        var localRadius = new RoundRectangleRadiusType[byorder.Count];

        for (int i = 0; i < byorder.Count; i++)
        {
            localRadius[i] = new RoundRectangleRadiusType { tl = defaultRadius, tr = defaultRadius, br = defaultRadius, bl = defaultRadius };
        }

        foreach (var item in byorder)
        {
            try { item.Clip.StrokeShape = new RoundRectangle { CornerRadius = new Microsoft.Maui.CornerRadius(defaultRadius) }; } catch { }
        }

        for (int i = 0; i < byorder.Count; i++)
        {
            var self = byorder[i];

            var (leftNeighbor, rightNeighbor) = FindNeighbors(self);

            if (leftNeighbor is not null)
            {
                int li = byorder.FindIndex(t => t == leftNeighbor);
                if (li >= 0)
                {
                    localRadius[i].tl = 0;
                    localRadius[i].br = 0;
                    localRadius[li].tr = 0;
                    localRadius[li].bl = 0;
                }
            }

            if (rightNeighbor is not null)
            {
                int ri = byorder.FindIndex(t => t == rightNeighbor);
                if (ri >= 0)
                {
                    localRadius[i].tr = 0;
                    localRadius[i].bl = 0;
                    localRadius[ri].tl = 0;
                    localRadius[ri].br = 0;
                }
            }
        }

        await Dispatcher.DispatchAsync(() =>
        {
            foreach (var item in byorder)
            {
                var r = localRadius[byorder.IndexOf(item)];
                try
                {
                    item.Clip.StrokeShape = new RoundRectangle
                    {
                        CornerRadius = new Microsoft.Maui.CornerRadius(r.tl, r.tr, r.br, r.bl)
                    };
                }
                catch (Exception e)
                {
                    Log(e, "update round rectangle", this);
                    SetStateFail("Failed to update clip border.");
                }
            }
        });

    }

    public void CleanupGhostAndShadow()
    {
        var keysToRemove = Clips.Keys.Where(k => k != null && (k.StartsWith("ghost_") || k.StartsWith("shadow_"))).ToList();
        foreach (var key in keysToRemove)
        {
            if (Clips.TryRemove(key, out var removed))
            {
                try
                {
                    // remove visual from overlay if present
                    if (removed?.Clip != null)
                    {
                        try { OverlayLayer?.Children.Remove(removed.Clip); } catch { }
                        // also remove from any track containers just in case
                        foreach (var t in Tracks.Values.ToList())
                            try { t.Children.Remove(removed.Clip); } catch { }
                    }
                }
                catch { }
            }
        }
    }


    private void RulerPanUpdated(object? sender, PanUpdatedEventArgs e)
    {
        if (UpperContent.Children[0] is not Grid previewGrid) return;
        switch (e.StatusType)
        {
            case GestureStatus.Started:
                _startPreviewHeight = previewGrid.Height;
                break;
            case GestureStatus.Running:
                var h = _startPreviewHeight + (Denoise ? Ydenoiser.Process(e.TotalY) : e.TotalY);
                if (h < 100) h = 100;

                var pageHeight = Height > 0 ? Height : WindowSize.Height;
                if (pageHeight > 220 && h > pageHeight - 100) h = pageHeight - 100;

                previewGrid.HeightRequest = h;
                PreviewAreaHeight = h;
                break;
            case GestureStatus.Completed:
                if (AutoSavePreviewAreaHeight)
                {
                    SettingsManager.WriteSetting("Edit_UpperContentHeight", PreviewAreaHeight.ToString());
                }
                break;
        }
    }



    #endregion

    #region drag and drop
    public async void File_Drop(object? sender, DropEventArgs e)
    {
        foreach (var path in await FileDropHelper.GetFilePathsFromDrop(e))
        {
            Log($"Importing file from drag and drop: {path}");
            await AddAsset(path);
        }
    }

    private async void File_DragOver(object? sender, DragEventArgs e)
    {
#if WINDOWS
        var platformArgs = e.PlatformArgs?.DragEventArgs;
        if (platformArgs is null)
        {
            return;
        }

        if (platformArgs.DataView != null && platformArgs.DataView.Contains(StandardDataFormats.StorageItems))
        {
            platformArgs.AcceptedOperation = Windows.ApplicationModel.DataTransfer.DataPackageOperation.Copy;

            var dragUI = platformArgs.DragUIOverride;
            if (dragUI is not null)
            {
                dragUI.Caption = Localized.DraftPage_ImportAssetNotFinished;
                dragUI.IsCaptionVisible = true;
                dragUI.IsContentVisible = true;
            }

        }
        else
        {
            platformArgs.AcceptedOperation = Windows.ApplicationModel.DataTransfer.DataPackageOperation.None;
        }
#else
        e.AcceptedOperation = DataPackageOperation.Copy;

#endif
    }
    #endregion

    #region popup
    private async Task ShowCommunityToolkitPopup(CommunityToolkit.Maui.Views.Popup popup)
    {
        try
        {
            await CommunityToolkit.Maui.Extensions.PopupExtensions.ShowPopupAsync(Navigation, popup, null);
        }
        catch (Exception ex)
        {
            Log(ex, "ShowCommunityToolkitPopup", this);
        }
    }

    public async Task ShowAPopup(View? content = null, Border? border = null, ClipElementUI? clip = null, string mode = "")
    {
        content ??= (border != null && clip != null) ? await BuildPropertyPanel(clip) : new Label { Text = $"No content to show. This SHOULD is a bug, please feedback.\r\n{Environment.StackTrace.Split(Environment.NewLine).Skip(1).Aggregate((a, b) => $"{a}{Environment.NewLine}{b}")}" };
        bool disablePopupScrollWrapping = clip?.ClipType == ClipMode.VideoClip || clip?.ClipType == ClipMode.PhotoClip;

        OverlayLayer.IsVisible = true;

        if (DeviceInfo.Idiom == DeviceIdiom.Phone)
        {
            await ShowAFullscreenPopupInBottom(WindowSize.Height * 0.75, content);
            return;
        }

        if (!SettingsManager.IsSettingExists("PreferredPopupMode"))
        {
#if WINDOWS
            SettingsManager.WriteSetting("PreferredPopupMode", "right");
#else
            SettingsManager.WriteSetting("PreferredPopupMode", "bottom");
#endif
        }
        try
        {
            switch (!string.IsNullOrWhiteSpace(mode) ? mode : SettingsManager.GetSetting("PreferredPopupMode"))
            {
                case "right":
                    {
                        await ShowAFullscreenPopupInRight(WindowSize.Height * 0.75, content, disablePopupScrollWrapping);
                        break;
                    }
                case "bottom":
                    {
                        await ShowAFullscreenPopupInBottom(WindowSize.Height / 1.2, content, disablePopupScrollWrapping);
                        break;
                    }
                case "dialog":
                    {
                        await ShowACenteredPopup(WindowSize.Height / 1.5, WindowSize.Width / 2, content, disablePopupScrollWrapping);
                        break;
                    }
                case "window":
                    {
                        var w = new MultiWindowItem
                        {
                            Content = content,
                            Title = "",
                            IsNavigationVisible = false
                        };
                        MainMultiWindowView.AddWindow(w);
                        await w.OpenInNewWindow();
                        break;
                    }
                case "clip":
                    {
                        if (border is not null && clip is not null)
                            await ShowClipPopup(border, clip);
                        else
                            await ShowAFullscreenPopupInBottom(WindowSize.Height / 1.2, content, disablePopupScrollWrapping);
                        break;
                    }
            }
        }
        catch (Exception ex)
        {
            Log(ex, "ShowClipPopup", clip);
            throw;
        }


    }

    private async Task ShowClipPopup(Border clipBorder, ClipElementUI clip)
    {


        var existing = OverlayLayer.Children.FirstOrDefault(c => (c as VisualElement)?.StyleId == "ClipPopupFrame" || (c as VisualElement)?.StyleId == "ClipPopupTriangle");
        if (existing != null)
        {
            var toRemove = OverlayLayer.Children.Where(c => (c as VisualElement)?.StyleId == "ClipPopupFrame" || (c as VisualElement)?.StyleId == "ClipPopupTriangle").ToList();
            foreach (var r in toRemove)
                OverlayLayer.Children.Remove(r);
        }


        double desiredPopupWidth = 500;
        double desiredPopupHeight = 400;
        double arrowSize = 20;
        double spacing = 8;
        double minPopupWidth = 200;
        double minPopupHeight = 120;
        double margin = 8;

        Point clipAbs = GetAbsolutePosition(clipBorder, null);
        Point overlayAbs = GetAbsolutePosition(OverlayLayer, null);

        int retries = 0;
        while ((OverlayLayer.Width <= 0 || OverlayLayer.Height <= 0 || double.IsNaN(clipAbs.Y) || clipAbs.Y <= 0) && retries < 6)
        {
            await Task.Delay(30);
            clipAbs = GetAbsolutePosition(clipBorder, null);
            overlayAbs = GetAbsolutePosition(OverlayLayer, null);
            retries++;
        }

        double cumulativeScrollY = 0;
        VisualElement? parent = clipBorder.Parent as VisualElement;
        while (parent != null && parent != OverlayLayer)
        {
            if (parent is ScrollView sv)
            {
                cumulativeScrollY += sv.ScrollY;
            }
            parent = parent.Parent as VisualElement;
        }
        double clipWidth = (clipBorder.Width > 0) ? clipBorder.Width : clipBorder.WidthRequest;
        double clipHeight = (clipBorder.Height > 0) ? clipBorder.Height : clipBorder.HeightRequest;

        Point abs = new Point(clipAbs.X - overlayAbs.X, clipAbs.Y - overlayAbs.Y - cumulativeScrollY);

        // for fallback 
        double overlayW = OverlayLayer.Width > 0 ? OverlayLayer.Width : this.Width;
        double overlayH = OverlayLayer.Height > 0 ? OverlayLayer.Height : this.Height;
        if (double.IsNaN(overlayW) || overlayW <= 0) overlayW = 1000;
        if (double.IsNaN(overlayH) || overlayH <= 0) overlayH = 1000;

        double availableBelow = overlayH - (abs.Y + clipHeight) - spacing - arrowSize - margin;
        double availableAbove = abs.Y - spacing - arrowSize - margin;
        if (availableBelow < 0) availableBelow = 0;
        if (availableAbove < 0) availableAbove = 0;

        double popupWidth = Math.Min(desiredPopupWidth, Math.Max(minPopupWidth, overlayW - margin * 2));
        double popupHeight;
        bool popupBelow;

        if (availableBelow >= desiredPopupHeight)
        {
            popupBelow = true;
            popupHeight = desiredPopupHeight;
        }
        else if (availableAbove >= desiredPopupHeight)
        {
            popupBelow = false;
            popupHeight = desiredPopupHeight;
        }
        else
        {
            if (availableBelow >= availableAbove)
            {
                popupBelow = true;
                popupHeight = Math.Max(minPopupHeight, Math.Min(desiredPopupHeight, availableBelow));
            }
            else
            {
                popupBelow = false;
                popupHeight = Math.Max(minPopupHeight, Math.Min(desiredPopupHeight, availableAbove));
            }

            popupHeight = Math.Min(popupHeight, Math.Max(minPopupHeight, overlayH - margin * 2));
        }

        double clipCenterX = abs.X + (clipWidth / 2.0);
        double popupX = clipCenterX - (popupWidth / 2.0);
        if (popupX < margin) popupX = margin;
        if (popupX + popupWidth + margin > overlayW) popupX = Math.Max(margin, overlayW - popupWidth - margin);

        double popupY;
        if (popupBelow)
        {
            popupY = abs.Y + clipHeight + spacing + arrowSize;
            if (popupY + popupHeight + margin > overlayH)
            {
                popupY = Math.Max(margin, overlayH - popupHeight - margin);
            }
        }
        else
        {
            popupY = abs.Y - popupHeight - arrowSize - spacing;
            if (popupY < margin)
            {
                popupY = margin;
            }
        }

        double triangleLeft = clipCenterX - (arrowSize / 2.0);
        double triangleMin = popupX + 6;
        double triangleMax = popupX + popupWidth - arrowSize - 6;
        triangleLeft = Math.Clamp(triangleLeft, triangleMin, triangleMax);
        double triangleTop;

        Polygon triangle;
        if (popupBelow)
        {
            triangle = new Polygon
            {
                StyleId = "ClipPopupTriangle",
                Fill = Colors.Grey,
                Points = new PointCollection
                {
                    new Point(0, arrowSize),
                    new Point(arrowSize / 2.0, 0),
                    new Point(arrowSize, arrowSize)
                }
            };
            triangleTop = popupY - arrowSize;
        }
        else
        {
            triangle = new Polygon
            {
                StyleId = "ClipPopupTriangle",
                Fill = Colors.Grey,
                Points = new PointCollection
                {
                    new Point(0, 0),
                    new Point(arrowSize / 2.0, arrowSize),
                    new Point(arrowSize, 0)
                },
                Opacity = 0.75
            };
            triangleTop = popupY + popupHeight;
        }

        AbsoluteLayout.SetLayoutBounds(triangle, new Rect(triangleLeft, triangleTop, arrowSize, arrowSize));

        var frame = new Border
        {
            StyleId = "ClipPopupFrame",
            Background = new SolidColorBrush(Colors.Grey),
            Stroke = Colors.Black,
            StrokeThickness = 1,
            StrokeShape = new RoundRectangle { CornerRadius = 4 },
            Padding = new Thickness(2),
            Opacity = 0.95,
            Content = WrapPropertyPanelContent(clip, await BuildPropertyPanel(clip))
        };

        frame.GestureRecognizers.Add(nopGesture);

        AbsoluteLayout.SetLayoutBounds(frame, new Rect(popupX, popupY, popupWidth, popupHeight));

        frame.Opacity = 0;
        frame.Scale = 0.95;
        frame.TranslationY = 5;

        triangle.Opacity = 0;
        triangle.Scale = 0.95;
        triangle.TranslationY = 5;

        if (UseCommunityToolkitPopupInsteadOfOverlayLayer)
        {
            // 使用 CommunityToolkit Popup
            var popupContent = new AbsoluteLayout
            {
                WidthRequest = popupWidth,
                HeightRequest = popupHeight + arrowSize,
                Children = { triangle, frame }
            };

            // 调整布局为相对于 popup 内容的坐标
            AbsoluteLayout.SetLayoutBounds(triangle, new Rect(triangleLeft - popupX, popupBelow ? 0 : popupHeight, arrowSize, arrowSize));
            AbsoluteLayout.SetLayoutBounds(frame, new Rect(0, popupBelow ? arrowSize : 0, popupWidth, popupHeight));

            _currentCommunityToolkitPopup = new CommunityToolkit.Maui.Views.Popup
            {
                Content = popupContent,
                CanBeDismissedByTappingOutsideOfPopup = true
            };

            ShowCommunityToolkitPopup(_currentCommunityToolkitPopup);
        }
        else
        {
            OverlayLayer.IsVisible = true;
            OverlayLayer.InputTransparent = false;
            OverlayLayer.Children.Add(frame);
            OverlayLayer.Children.Add(triangle);
        }


        const uint entranceMs = 220u;
        try
        {
            await Task.WhenAll(
                frame.FadeToAsync(0.9, entranceMs, Easing.CubicOut),
                frame.ScaleToAsync(1, entranceMs, Easing.CubicOut),
                frame.TranslateToAsync(0, 0, entranceMs, Easing.CubicOut),
                triangle.FadeToAsync(0.9, entranceMs, Easing.CubicOut),
                triangle.ScaleToAsync(1, entranceMs, Easing.CubicOut),
                triangle.TranslateToAsync(0, 0, entranceMs, Easing.CubicOut)
            );
        }
        catch { }
    }

    public async Task HidePopup(bool force = false)
    {
        if (!force && !IsPopupClosableByTapBackground) return;
        if (UseCommunityToolkitPopupInsteadOfOverlayLayer)
        {
            if (_currentCommunityToolkitPopup is not null)
            {
                await _currentCommunityToolkitPopup.CloseAsync();
                _currentCommunityToolkitPopup = null;
            }
        }
        else
        {
            OverlayLayer.GestureRecognizers?.Remove(fileDropGesture);
            OverlayLayer.InputTransparent = true;
            await Task.WhenAll(HideClipPopup(), HideFullscreenPopup());

            OverlayLayer.IsVisible = false;
            OverlayLayer.InputTransparent = true;
        }
    }

    private async Task HideClipPopup()
    {
        var toRemove = OverlayLayer.Children.Where(c => (c as VisualElement)?.StyleId == "ClipPopupFrame" || (c as VisualElement)?.StyleId == "ClipPopupTriangle").ToList();

        // Animate out (run all animations in parallel)
        const uint exitMs = 220u;
        var visuals = toRemove.OfType<VisualElement>().ToList();
        var tasks = new List<Task>();
        foreach (var v in visuals)
        {
            try
            {
                var t = Task.WhenAll(
                    v.FadeToAsync(0, exitMs, Easing.CubicIn),
                    v.ScaleToAsync(0.95, exitMs, Easing.CubicIn),
                    v.TranslateToAsync(0, 10, exitMs, Easing.CubicIn)
                );
                tasks.Add(t);
            }
            catch { }
        }
        try { await Task.WhenAll(tasks); } catch { }

        foreach (var r in toRemove)
            OverlayLayer.Children.Remove(r);

    }

    private async Task ShowAFullscreenPopupInBottom(double height, View content, bool disableScrollWrapping = false)
    {
        popupShowingDirection = "bottom";

        OverlayLayer.IsVisible = true;
        OverlayLayer.InputTransparent = false;

        var size = WindowSize;

        Popup = new Border
        {
            WidthRequest = size.Width - 40,
            HeightRequest = height,
            TranslationX = 15,
            TranslationY = size.Height + 10,
            Background = new SolidColorBrush(Colors.Grey),

            StrokeShape = new RoundRectangle
            {
                CornerRadius = 8,
                StrokeThickness = 8
            },
            Padding = 12,
            Content = content,
            Opacity = 0.95
        };
        if (UseCommunityToolkitPopupInsteadOfOverlayLayer)
        {
            // 使用 CommunityToolkit Popup
            View popupContentView = disableScrollWrapping
                ? content
                : new ScrollView
                {
                    Content = content
                };

            popupContentView.WidthRequest = size.Width - 40;
            popupContentView.HeightRequest = height;

            _currentCommunityToolkitPopup = new CommunityToolkit.Maui.Views.Popup
            {
                Content = popupContentView,
                CanBeDismissedByTappingOutsideOfPopup = true,
                VerticalOptions = LayoutOptions.End
            };

            ShowCommunityToolkitPopup(_currentCommunityToolkitPopup);
        }
        else
        {
            try
            {
                OverlayLayer.InputTransparent = false;
                Popup.GestureRecognizers.Add(nopGesture);
                OverlayLayer.Add(Popup);
            }
            catch (Exception ex)
            {
                Log(ex, "Show popup", this);
                await DisplayAlertAsync(Localized._Error, "Cannot show a popup.", Localized._OK);
            }

            var targetY = height;
            try
            {
                await Popup.TranslateToAsync(Popup.TranslationX, size.Height - targetY, 300, Easing.SinOut);
            }
            catch { }
        }
    }

    private async Task ShowAFullscreenPopupInRight(double width, View content, bool disableScrollWrapping = false)
    {
        popupShowingDirection = "right";

        OverlayLayer.IsVisible = true;
        OverlayLayer.InputTransparent = false;

        var size = WindowSize;

        Popup = new Border
        {
            WidthRequest = size.Width - width,
            HeightRequest = size.Height * 0.85,
            TranslationX = size.Width + 20,
            TranslationY = 20,
            Background = new SolidColorBrush(Colors.Grey),

            StrokeShape = new RoundRectangle
            {
                CornerRadius = 8,
                StrokeThickness = 8
            },
            Padding = 12,
            Content = content,
            Opacity = 0.95
        };
        if (UseCommunityToolkitPopupInsteadOfOverlayLayer)
        {
            // 使用 CommunityToolkit Popup
            View popupContentView = disableScrollWrapping
                ? content
                : new ScrollView
                {
                    Content = content
                };

            popupContentView.WidthRequest = size.Width - width;
            popupContentView.HeightRequest = size.Height * 0.85;

            _currentCommunityToolkitPopup = new CommunityToolkit.Maui.Views.Popup
            {
                Content = popupContentView,
                CanBeDismissedByTappingOutsideOfPopup = true,
                HorizontalOptions = LayoutOptions.End
            };

            ShowCommunityToolkitPopup(_currentCommunityToolkitPopup);
        }
        else
        {
            try
            {
                OverlayLayer.InputTransparent = false;
                Popup.GestureRecognizers.Add(nopGesture);
                OverlayLayer.Add(Popup);
            }
            catch (Exception ex)
            {
                Log(ex, "Show popup", this);
                await DisplayAlertAsync(Localized._Error, "Cannot show a popup.", Localized._OK);
            }
            var targetX = width;
            try
            {
                await Popup.TranslateToAsync(width, Popup.TranslationY, 300, Easing.SinOut);
            }
            catch { }
        }
    }

    public async Task ShowACenteredPopup(double desiredHeight, double desiredWidth, View content, bool disableScrollWrapping = false)
    {
        popupShowingDirection = "dialog";

        OverlayLayer.IsVisible = true;
        OverlayLayer.InputTransparent = false;


        var size = new Size(OverlayLayer.Width, OverlayLayer.Height);
        if (size.Width <= 0 || size.Height <= 0) size = new Size(this.Width, this.Height);
        if (size.Width <= 0 || size.Height <= 0) size = WindowSize;

        if (double.IsNaN(size.Width) || size.Width <= 0) size.Width = this.Width;
        if (double.IsNaN(size.Height) || size.Height <= 0) size.Height = this.Height;
        if (double.IsNaN(size.Width) || size.Width <= 0) size.Width = 1000;
        if (double.IsNaN(size.Height) || size.Height <= 0) size.Height = 800;

        // NOTE: call site passes (height, width). Keep signature but treat:
        // x => desired height, y => desired width.

        const double margin = 20;
        double popupWidth = Math.Min(desiredWidth, Math.Max(200, size.Width - margin * 2));
        double popupHeight = Math.Min(desiredHeight, Math.Max(120, size.Height - margin * 2));

        double targetX = (size.Width - popupWidth) / 2.0;
        double targetY = (size.Height - popupHeight) / 2.0;
        if (targetX < margin) targetX = margin;
        if (targetY < margin) targetY = margin;

        try
        {
            if (Popup is not null)
                OverlayLayer.Remove(Popup);
        }
        catch { }

        Popup = new Border
        {
            WidthRequest = popupWidth,
            HeightRequest = popupHeight,
            TranslationX = targetX,
            TranslationY = size.Height + 10,
            Background = new SolidColorBrush(Colors.Grey),
            StrokeShape = new RoundRectangle
            {
                CornerRadius = 8,
                StrokeThickness = 8
            },
            Padding = 12,
            Content = content,
            Opacity = 0.0,
            Scale = 0.97
        };

        if (UseCommunityToolkitPopupInsteadOfOverlayLayer)
        {
            // 使用 CommunityToolkit Popup
            View popupContentView = disableScrollWrapping
                ? content
                : new ScrollView
                {
                    Content = content
                };

            popupContentView.WidthRequest = popupWidth;
            popupContentView.HeightRequest = popupHeight;

            _currentCommunityToolkitPopup = new CommunityToolkit.Maui.Views.Popup
            {
                Content = popupContentView,
                CanBeDismissedByTappingOutsideOfPopup = true,
                HorizontalOptions = LayoutOptions.Center,
                VerticalOptions = LayoutOptions.Center
            };

            ShowCommunityToolkitPopup(_currentCommunityToolkitPopup);
        }
        else
        {
            try
            {
                OverlayLayer.InputTransparent = false;
                Popup.GestureRecognizers.Add(nopGesture);
                OverlayLayer.Add(Popup);
            }
            catch (Exception ex)
            {
                Log(ex, "Show popup", this);
                await DisplayAlertAsync(Localized._Error, "Cannot show a popup.", Localized._OK);
            }

            const uint entranceMs = 220u;
            try
            {
                await Task.WhenAll(
                    Popup.FadeToAsync(0.95, entranceMs, Easing.CubicOut),
                    Popup.ScaleToAsync(1, entranceMs, Easing.CubicOut),
                    Popup.TranslateToAsync(targetX, targetY, entranceMs, Easing.CubicOut)
                );
            }
            catch
            {
                try
                {
                    Popup.Opacity = 0.95;
                    Popup.Scale = 1;
                    Popup.TranslationX = targetX;
                    Popup.TranslationY = targetY;
                }
                catch { }
            }
        }
    }

    private async Task HideFullscreenPopup()
    {
        var size = WindowSize;
        try
        {
            switch (popupShowingDirection)
            {
                case "right":
                    await Popup.TranslateToAsync(size.Width + 20, Popup.TranslationY, 300, Easing.SinIn);
                    break;
                case "bottom":
                    await Popup.TranslateToAsync(Popup.TranslationX, size.Height + 10, 300, Easing.SinIn);
                    break;
                case "dialog":
                case "center":
                    await Task.WhenAll(
                        Popup.FadeToAsync(0, 180u, Easing.CubicIn),
                        Popup.ScaleToAsync(0.97, 180u, Easing.CubicIn),
                        Popup.TranslateToAsync(Popup.TranslationX, size.Height + 10, 220u, Easing.CubicIn)
                    );
                    break;
                default:
                    await Popup.TranslateToAsync(Popup.TranslationX, size.Height + 10, 300, Easing.SinIn);
                    break;
            }
        }
        catch { }

        Popup.Content = null;
        OverlayLayer.Remove(Popup);
        OverlayLayer.InputTransparent = true;

        popupShowingDirection = "none";

        _transformMenuActivatedCenterClip = null;
        _transformMenuActivatedHandle = "none";

    }


    private void ActivateMultiWindowItem(MultiWindowItem window)
    {
        if (!MainMultiWindowView.Children.Contains(window))
        {
            MainMultiWindowView.AddWindow(window);
        }

        window.IsVisible = true;
        MainMultiWindowView.BringToFront(window);
    }

    private void ToggleAssistantSubWindow(MultiWindowItem item)
    {
        var isOpened = MainMultiWindowView.Children.Contains(AssisstantSubWindow);
        if (isOpened && item.IsVisible)
        {
            MainMultiWindowView.CloseWindow(item);
            return;
        }

        ActivateMultiWindowItem(item);
    }

    private void ExecuteManageWindowCommand(string? action)
    {
        switch (action)
        {
            case var c when Guid.TryParse(c, out var wid):
                {
                    if (MainMultiWindowView.Windows.FirstOrDefault(x => x.WindowID == wid) is MultiWindowItem window)
                    {
                        ActivateMultiWindowItem(window);
                    }
                    break;
                }
            case "active":
                if (MainMultiWindowView?.ActiveWindow?.IsClosable ?? false) MainMultiWindowView?.ActiveWindow?.Close(false);
                break;
            case "history":
                ToggleAssistantSubWindow(HistorySubWindow);
                break;
            //case "preview":
            //    ActivateMultiWindowItem(PreviewSubwindow);
            //    break;
            //case "properties":
            //    ActivateMultiWindowItem(PropertiesSubwindow);
            //    break;
            //case "assistant":
            //    ActivateMultiWindowItem(AssisstantSubWindow);
            //    break;
            case "assistant-toggle":
                ToggleAssistantSubWindow(AssisstantSubWindow);
                break;
            case "close-extra":
                foreach (var window in MainMultiWindowView.Children.OfType<MultiWindowItem>().Where(x => x.IsClosable).ToList())
                {
                    MainMultiWindowView.CloseWindow(window);
                }
                break;
        }
    }

    private void AssistantToggleButton_Clicked(object sender, EventArgs e)
    {
        ExecuteManageWindowCommand("assistant-toggle");
    }


    private void ResetLayout()
    {
        foreach (var window in MainMultiWindowView.Children.OfType<MultiWindowItem>().Where(x => x.IsClosable).ToList())
        {
            MainMultiWindowView.CloseWindow(window);
        }
        if (UpperContent.Children[0] is Grid previewGrid)
        {
            previewGrid.HeightRequest = 250;
            if (previewGrid is MultiWindowView)
            {
                _hasAppliedDefaultMainMultiWindowLayout = false;
                ApplyDefaultMainMultiWindowLayout();
            }
            else
            {
                previewGrid.ColumnDefinitions.Clear();
                previewGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
                previewGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

                int colIndex = 0;
                foreach (var child in previewGrid.Children)
                {
                    if (child.GetType().Name == "MultiWindowItem" && colIndex < 2)
                    {
                        Grid.SetColumn((BindableObject)child, colIndex);
                        colIndex++;
                    }
                }
            }
        }
    }


    #endregion

    #region compute stuff
    [DebuggerNonUserCode()]
    public uint PixelToFrame(double px) => (uint)(px * FramePerPixel * tracksZoomOffest);
    [DebuggerNonUserCode()]
    public double FrameToPixel(uint f) => f / (FramePerPixel * tracksZoomOffest);

    private void SyncClipEditorCurrentFrame()
    {
        if (ClipEditor is null)
        {
            return;
        }

        var frame = _currentFrame;
        if (double.IsNaN(frame) || double.IsInfinity(frame) || frame < 0)
        {
            frame = 0;
        }

        ClipEditor.SetCurrentFrame((uint)frame);
    }

    private Point GetAbsolutePosition(VisualElement element, VisualElement ancestor)
    {
        double x = element.X + element.TranslationX;
        double y = element.Y + element.TranslationY;

        VisualElement? parent = element.Parent as VisualElement;
        while (parent != null && parent != ancestor)
        {
            if (parent is ScrollView sv)
            {
                x -= sv.ScrollX;
                y -= sv.ScrollY;
            }
            x += parent.X + parent.TranslationX;
            y += parent.Y + parent.TranslationY;
            parent = parent.Parent as VisualElement;
        }

        return new Point(x, y);
    }

    private double SnapPixels(double x)
    {
        if (!SnapEnabled) return Math.Max(0, x);
        double best = x;
        double bestDist = SnapThresholdPixels + 1;

        // 1) grid
        var grid = Math.Round(x / SnapGridPixels) * SnapGridPixels;
        var d = Math.Abs(grid - x);
        if (d < bestDist && d <= SnapThresholdPixels)
        {
            best = grid; bestDist = d;
        }

        // 2) edges of other clips
        foreach (var kv in Clips)
        {
            var key = kv.Key;
            if (string.IsNullOrEmpty(key)) continue;
            if (key.StartsWith("ghost_") || key.StartsWith("shadow_")) continue;
            var b = kv.Value.Clip;
            if (b == null) continue;
            double bx = b.TranslationX;
            double bw = (!double.IsNaN(b.Width) && b.Width > 0) ? b.Width : b.WidthRequest;
            // prefer integer pixel edges
            bx = Math.Round(bx);
            bw = Math.Round(bw);
            var startEdge = bx;
            var endEdge = bx + bw;
            var ds = Math.Abs(startEdge - x);
            if (ds < bestDist && ds <= SnapThresholdPixels) { best = startEdge; bestDist = ds; }
            var de = Math.Abs(endEdge - x);
            if (de < bestDist && de <= SnapThresholdPixels) { best = endEdge; bestDist = de; }
        }

        // clamp and round to integer pixel to avoid sub-pixel gaps
        return Math.Max(0, Math.Round(best));
    }

    private readonly double LeftOverlapDelta = 4.2d;
    private readonly double RightOverlapDelta = 3.2d;

    private bool ShouldParticipateInTimelineLayout(ClipElementUI? clip)
    {
        if (clip is null) return false;
        if (string.IsNullOrWhiteSpace(clip.Id)) return false;
        if (!clip.ShouldDisplayInUI) return false;
        if (clip.Id.StartsWith("ghost_") || clip.Id.StartsWith("shadow_")) return false;
        return true;
    }

    private double ResolveOverlapStartPixels(int trackIndex, string selfId, double startX, double width)
    {
        // Try to find a non-overlapping X on the given track by shifting left/right
        if (!Tracks.TryGetValue(trackIndex, out var trackLayout))
            return startX;

        // use rounded start to avoid fractional pixel gaps
        double s = startX;
        const double eps = 1e-3;
        for (int i = 0; i < 16; i++)
        {
            double end = s + width;
            var overlappers = trackLayout.Children
                .OfType<Border>()
                .Where(b =>
                {
                    // find clip id for this border
                    var pair = Clips.FirstOrDefault(kv => kv.Value.Clip == b);
                    if (pair.Key == null) return false;
                    if (pair.Key == selfId) return false;
                    if (!ShouldParticipateInTimelineLayout(pair.Value)) return false;
                    double bx = Math.Round(b.TranslationX);
                    double bw = (!double.IsNaN(b.Width) && b.Width > 0) ? Math.Round(b.Width) : Math.Round(b.WidthRequest);
                    // overlap check using integer pixels
                    return Math.Max(s, bx) < Math.Min(end, bx + bw);
                })
                .ToList();
            if (overlappers.Count == 0) break;

            // compute integer-edge candidates
            double rightCandidate = overlappers.Max(b => Math.Round(b.TranslationX) + (((!double.IsNaN(b.Width) && b.Width > 0) ? Math.Round(b.Width) : Math.Round(b.WidthRequest))));
            double leftCandidate = overlappers.Min(b => Math.Round(b.TranslationX)) - Math.Round(width);
            if (leftCandidate < 0) leftCandidate = 0;

            // prefer tight adjacency to the right (no gap) to eliminate visible gaps
            // however if shifting left yields strictly smaller movement and does not overlap, pick it
            double moveRight = Math.Abs(rightCandidate - s);
            double moveLeft = Math.Abs(s - leftCandidate);

            // check if leftCandidate would overlap (using integer math)
            bool leftOverlaps = trackLayout.Children
                .OfType<Border>()
                .Any(b =>
                {
                    var pair = Clips.FirstOrDefault(kv => kv.Value.Clip == b);
                    if (pair.Key == null) return false;
                    if (pair.Key == selfId) return false;
                    if (!ShouldParticipateInTimelineLayout(pair.Value)) return false;
                    double bx = Math.Round(b.TranslationX);
                    double bw = (!double.IsNaN(b.Width) && b.Width > 0) ? Math.Round(b.Width) : Math.Round(b.WidthRequest);
                    return Math.Max(leftCandidate, bx) < Math.Min(leftCandidate + Math.Round(width), bx + bw);
                });

            if (!leftOverlaps && moveLeft < moveRight)
            {
                s = leftCandidate + LeftOverlapDelta;
            }
            else
            {
                // default to tight adjacency on right to avoid gaps
                s = rightCandidate - RightOverlapDelta;
            }
        }

        // final rounding
        return Math.Max(0, s);
    }

    public (ClipElementUI? left, ClipElementUI? right) FindNeighbors(ClipElementUI? clip)
    {
        if (clip is null) return (null, null);
        if (clip.origTrack is null) return (null, null);
        int track = clip.origTrack.Value;

        double selectedLeft = clip.Clip.TranslationX;
        double selectedWidth = clip.Clip.WidthRequest > 0 ? clip.Clip.WidthRequest : clip.origLength;
        double selectedRight = selectedLeft + selectedWidth;

        const double tolerance = 8.0; // pixels

        ClipElementUI? leftNeighbor = null;
        ClipElementUI? rightNeighbor = null;

        foreach (var kv in Clips)
        {
            var c = kv.Value;
            if (c.Id == clip.Id || c.origTrack != track) continue;
            if (!ShouldParticipateInTimelineLayout(c)) continue;

            double cWidth = c.Clip.WidthRequest > 0 ? c.Clip.WidthRequest : c.origLength;
            double cRight = c.Clip.TranslationX + cWidth;

            // c ends where selected begins → left neighbor
            if (Math.Abs(cRight - selectedLeft) < tolerance)
                leftNeighbor = c;

            // c starts where selected ends → right neighbor
            if (Math.Abs(c.Clip.TranslationX - selectedRight) < tolerance)
                rightNeighbor = c;
        }

        return (leftNeighbor, rightNeighbor);
    }

    #endregion

    #region live preview
    SemaphoreSlim renderingLock = new(1, 1);
    private async Task RefreshPreviewFromCurrentProviderAsync()
    {
        if (UseRealtimePreview)
        {
            await RefreshDynamicPreviewOverlay();
            return;
        }

        await RenderOneFrame((uint)_currentFrame);
    }

    private async Task<bool> RefreshDynamicPreviewOverlay()
    {
        try
        {
            // Keep dynamic preview generation in project coordinate space so overlay rects stay aligned.
            var targetWidth = Math.Max(1, ProjectInfo.RelativeWidth);
            var targetHeight = Math.Max(1, ProjectInfo.RelativeHeight);
            var preparedPreviews = await DynamicPreviewProvider.PrepareFrameAsync((uint)_currentFrame, targetWidth, targetHeight);
            return await ClipEditor.ApplyPreparedPreviewsAsync(preparedPreviews);
        }
        catch (Exception ex)
        {
            Log(ex, "Render one frame via DynamicPreview", this);
            SetStateFail(Localized._ExceptionTemplate(ex));
#if DEBUG
            if (await DisplayAlertAsync(Localized._Error, Localized.DraftPage_RenderFail((uint)_currentFrame, ex), "Throw", Localized._OK)) throw;
#else
            await DisplayAlertAsync(Localized._Error, Localized.DraftPage_RenderFail((uint)_currentFrame, ex), Localized._OK);
#endif
            return false;
        }
    }

    private async Task RenderOneFrame(uint duration, int? width = null, int? height = null)
    {
        await renderingLock.WaitAsync();
        _currentFrame = duration;
        SyncClipEditorCurrentFrame();
        SetStateBusy();
        SetStatusText(Localized.DraftPage_RenderOneFrame((int)duration, TimeSpan.FromSeconds(duration * SecondsPerFrame)));
        try
        {
            using var cts = new CancellationTokenSource();
#if !DEBUG
            cts.CancelAfter(10000);
#endif
            var targetWidth = width ?? previewWidth;
            var targetHeight = height ?? previewHeight;

            if (UseRealtimePreview)
            {
                if (DynamicPreviewProvider.Clips is null) return;
                await Dispatcher.DispatchAsync(() =>
                {
                    ClipEditor.SetRealtimePreviewContent(EnsureRealtimePreviewHost());
                    LivePreviewPlayer.IsVisible = false;
                    ClipEditor.SetStaticPreviewVisible(false);
                });

                if (!await RefreshDynamicPreviewOverlay())
                {
                    string fallbackPath = string.Empty;
                    await Task.Run(() =>
                    {
                        cts.Token.ThrowIfCancellationRequested();
                        fallbackPath = previewer.RenderFrame(duration, targetWidth, targetHeight);
                        cts.Token.ThrowIfCancellationRequested();
                    }, cts.Token);

                    await ClipEditor.StaticPreviewOverlayImage.ForceLoadPNGToAImage(fallbackPath);
                    await Dispatcher.DispatchAsync(() =>
                    {
                        ClipEditor.SetStaticPreviewVisible(true);
                    });
                }
            }
            else
            {
                if (previewer.Clips is null) return;
                await Dispatcher.DispatchAsync(() =>
                {
                    ClipEditor.SetRealtimePreviewContent(EnsureRealtimePreviewHost());
                    LivePreviewPlayer.IsVisible = false;
                    DynamicPreviewProvider.IsVisible = false;
                });

                string path = string.Empty;
                await Task.Run(() =>
                {
                    cts.Token.ThrowIfCancellationRequested();
                    path = previewer.RenderFrame(duration, targetWidth, targetHeight);
                    cts.Token.ThrowIfCancellationRequested();
                }, cts.Token);

                await ClipEditor.StaticPreviewOverlayImage.ForceLoadPNGToAImage(path);
                await Dispatcher.DispatchAsync(() =>
                {
                    ClipEditor.SetStaticPreviewVisible(true);
                });
            }


            SetStateOK();
            SetStatusText(Localized.DraftPage_EverythingFine);
        }
        catch (OperationCanceledException)
        {
            SetStateFail(Localized.DraftPage_RenderTimeout);
        }
        catch (Exception ex)
        {
            Log(ex, "Render one frame", this);
            SetStateFail(Localized._ExceptionTemplate(ex));
#if DEBUG
            if (await DisplayAlertAsync(Localized._Error, Localized.DraftPage_RenderFail(duration, ex), "Throw", Localized._OK)) throw;
#else
            await DisplayAlertAsync(Localized._Error, Localized.DraftPage_RenderFail(duration, ex), Localized._OK);
#endif
        }
        finally
        {
            renderingLock.Release();
        }
    }

    CancellationTokenSource? _playbackCts;
    CancellationTokenSource? _movePlayheadDebounceCts;
    bool isPlaying = false;
    bool playbackDone = false;
    private async void PlayPauseButton_Clicked(object sender, EventArgs e)
    {
        if ((UseCompactLayout ?? DeviceInfo.Idiom == DeviceIdiom.Phone) && _pendingClipPlacementFactory is not null)
        {
            CancelPendingClipPlacement();
            Dispatcher.Dispatch(() =>
            {
                SetPlayPauseIconToPlay();
                CurrentPlayheadLabel.IsVisible = true;
            });
            return;
        }

        isPlaying = !isPlaying;
        if (isPlaying)
        {
            SetPlayPauseIconToPause();
            LogDiagnostic("Start playing...");
            SetStateBusy();
            if (!_isLivePreviewPlayerEventsHooked)
            {
                LivePreviewPlayer.MediaEnded += (s, e) =>
                {
                    if (!isPlaying) return;
                    playbackDone = true;
                    try
                    {
                        if (_lastPlaybackPath is not null && File.Exists(_lastPlaybackPath))
                            File.Delete(_lastPlaybackPath);
                    }
                    catch { }
                };
                _isLivePreviewPlayerEventsHooked = true;
            }
            await Task.Run(PrepareLivePreview);

        }
        else
        {
            SetPlayPauseIconToPlay();
            LogDiagnostic("Pause playing.");
            await PauseLivePreview();
            SetStateOK();
        }

    }
    MediaElement LivePreviewPlayer = new();
    MediaElement DynamicPreviewAudioProvider = new();

    private Grid EnsureRealtimePreviewHost()
    {
        if (_livePreviewRealtimeHost is not null)
        {
            return _livePreviewRealtimeHost;
        }

        var host = new Grid();
        host.Children.Add(DynamicPreviewProvider);
        host.Children.Add(LivePreviewPlayer);
        _livePreviewRealtimeHost = host;
        return host;
    }

    private async Task PlayRealtimeAudioPreview(int startFrame, CancellationToken token)
    {
        if (!previewer.HasAudioSources())
        {
            return;
        }

        try
        {
            int currentStartFrame = Math.Max(0, startFrame);
            while (!token.IsCancellationRequested)
            {
                var nextAudioPath = await previewer.RenderSomeAudio(currentStartFrame, LiveVideoPreviewBufferLength, (int)ProjectInfo.TargetFrameRate, token);
                if (string.IsNullOrWhiteSpace(nextAudioPath) || !File.Exists(nextAudioPath))
                {
                    return;
                }

                playbackDone = false;
                DynamicPreviewAudioProvider = new MediaElement
                {
                    Source = MediaSource.FromFile(nextAudioPath),
                    ShouldAutoPlay = true,
                    ShouldKeepScreenOn = false,
                    ShouldLoopPlayback = false,
                    ShouldShowPlaybackControls = false,
                    WidthRequest = 10,
                    HeightRequest = 10
                };
                DynamicPreviewAudioProvider.MediaEnded += (s, e) =>
                {
                    try
                    {
                        ComputeView.Children.Remove(DynamicPreviewAudioProvider);
                    }
                    catch { }

                };
                await Dispatcher.DispatchAsync(() =>
                {
                    ComputeView.Add(DynamicPreviewAudioProvider);
                });

                while (!playbackDone && !token.IsCancellationRequested)
                {
                    await Task.Delay(60, token);
                }

                try
                {
                    if (!string.IsNullOrWhiteSpace(_lastRealtimeAudioPath) && File.Exists(_lastRealtimeAudioPath))
                    {
                        File.Delete(_lastRealtimeAudioPath);
                    }
                }
                catch { }

                _lastRealtimeAudioPath = nextAudioPath;
                currentStartFrame += LiveVideoPreviewBufferLength;
            }
        }
        catch (OperationCanceledException)
        {
            // expected on pause/stop
        }
        catch (Exception ex)
        {
            Log(ex, "RealtimeAudioPreview", this);
        }
    }


    private async Task PrepareLivePreview()
    {
        if (_playbackCts != null)
        {
            _playbackCts.Cancel();
            _playbackCts.Dispose();
        }
        _playbackCts = new CancellationTokenSource();
        var token = _playbackCts.Token;

        try
        {
            _playbackStartFrame = _currentFrame;
            _nextPlaybackPath = null;
            await previewer.ResetAudioPlaybackSources();

            if (UseRealtimePreview)
            {
                SetStateBusy();
                await Dispatcher.DispatchAsync(() =>
                {
                    var host = EnsureRealtimePreviewHost();
                    ClipEditor.SetRealtimePreviewContent(host);
                    ClipEditor.SetStaticPreviewVisible(false);
                    LivePreviewPlayer.IsVisible = false;
                });

                var audioTask = PlayRealtimeAudioPreview((int)_currentFrame, token);
                await RenderSomeFrames((int)_currentFrame, token);
                await audioTask;
                return;
            }

            await Dispatcher.DispatchAsync(() =>
            {
                ClipEditor.SetRealtimePreviewContent(EnsureRealtimePreviewHost());
                LivePreviewPlayer.IsVisible = true;
                DynamicPreviewProvider.IsVisible = false;
                LivePreviewPlayer.Opacity = 1;
                LivePreviewPlayer.HeightRequest = -1;
                LivePreviewPlayer.WidthRequest = -1;
                LivePreviewPlayer.InputTransparent = false;
                LivePreviewPlayer.ShouldShowPlaybackControls = false;
                ClipEditor.SetStaticPreviewVisible(false);
            });


            var path = await RenderSomeFrames((int)_currentFrame, token);
            Dispatcher.Dispatch(() =>
            {
                LivePreviewPlayer.Source = MediaSource.FromFile(path);
                LivePreviewPlayer.Play();
            });


            int currentStartFrame = (int)_currentFrame;
            while (!token.IsCancellationRequested)
            {
                try
                {
                    var nextStart = currentStartFrame + LiveVideoPreviewBufferLength;
                    LogDiagnostic($"Start continue Render from {nextStart}...");

                    _nextPlaybackPath = await RenderSomeFrames(nextStart, _playbackCts.Token);

                    _currentFrame = (uint)nextStart;
                    SyncClipEditorCurrentFrame();
                    LogDiagnostic($"Next preview is ready. Path:{_nextPlaybackPath}");
                    while (!playbackDone && !token.IsCancellationRequested) await Task.Delay(100, token);
                    LogDiagnostic("Previewer is ready!");
                    playbackDone = false;
                    Dispatcher.Dispatch(() =>
                    {
                        UpdatePlayheadPosition();
                        LivePreviewPlayer.Stop();
                        LivePreviewPlayer.Source = null;
                        LivePreviewPlayer.Source = MediaSource.FromFile(_nextPlaybackPath);
                        _lastPlaybackPath = _nextPlaybackPath;
                        LivePreviewPlayer.ShouldAutoPlay = true;
                        LivePreviewPlayer.Play();
                    });
                    currentStartFrame += LiveVideoPreviewBufferLength;
                }
                catch (Exception ex)
                {
                    Log(ex, "PreRender", this);
                }
                finally
                {
                    _isPreRendering = false;

                }
            }

        }
        catch (OperationCanceledException)
        {
            // Stopped
        }
        catch (Exception ex)
        {
            Log(ex, "LivePreview", this);
            isPlaying = false;
            await PauseLivePreview();
            SetPlayPauseIconToPlay();
        }
    }

    private async Task PauseLivePreview()
    {
        try
        {
            _playbackCts?.Cancel();
            _playbackCts?.Dispose();
        }
        catch { }
        finally
        {
            _playbackCts = null;
        }

        await Dispatcher.DispatchAsync(() =>
        {
            try
            {
                LivePreviewPlayer.Stop();
                DynamicPreviewAudioProvider.Stop();
                LivePreviewPlayer.Source = null;
                DynamicPreviewAudioProvider.Source = null;
            }
            catch { }
            LivePreviewPlayer.IsVisible = false;
            // Keep preview elements inside a single host to avoid WinUI re-parent exceptions.
            ClipEditor.SetRealtimePreviewContent(EnsureRealtimePreviewHost());
            DynamicPreviewProvider.IsVisible = UseRealtimePreview;
            ClipEditor.SetStaticPreviewVisible(true);
            SetPlayPauseIconToPlay();
        });

        await RefreshPreviewFromCurrentProviderAsync();
        _nextPlaybackPath = null;
        _isPreRendering = false;

        try
        {
            if (!string.IsNullOrWhiteSpace(_lastRealtimeAudioPath) && File.Exists(_lastRealtimeAudioPath))
            {
                File.Delete(_lastRealtimeAudioPath);
            }
        }
        catch { }
        _lastRealtimeAudioPath = null;
    }

    private async Task<string> RenderSomeFrames(int startPoint, CancellationToken ct)
    {
        Stopwatch cd = Stopwatch.StartNew();
        void progChanged(double p, TimeSpan _)
        {
            if (cd.ElapsedMilliseconds < 500) return;
            cd.Restart();
            SetStatusText(Localized._ProcessingWithProg(p));
        }

        if (UseRealtimePreview)
        {
            var fps = Math.Max(1, (int)ProjectInfo.TargetFrameRate);
            var targetInterval = TimeSpan.FromSeconds(1d / fps);
            int frame = Math.Max(0, startPoint);
            int frameStep = 1;
            int maxFrameStep = Math.Max(2, fps / 2);
            double avgLoopSeconds = targetInterval.TotalSeconds;
            const double avgFactor = 0.2d;
            const double slowThreshold = 1.08d;
            const double fastThreshold = 0.65d;

            while (!ct.IsCancellationRequested)
            {
                int stepForThisIteration = frameStep;
                var loopTimer = Stopwatch.StartNew();

                _currentFrame = frame;
                SyncClipEditorCurrentFrame();
                await RefreshDynamicPreviewOverlay();
                await Dispatcher.DispatchAsync(async () =>
                {
                    UpdatePlayheadPosition();
                    CurrentPlayheadLabel.Text = $"{TimeSpan.FromSeconds(frame * SecondsPerFrame):mm\\:ss\\.ff} / {TimeSpan.FromSeconds(ProjectDuration * SecondsPerFrame):mm\\:ss}";
                });

                loopTimer.Stop();
                var workSeconds = loopTimer.Elapsed.TotalSeconds;
                avgLoopSeconds = avgLoopSeconds * (1d - avgFactor) + (workSeconds * avgFactor);

                var expectedSeconds = targetInterval.TotalSeconds * stepForThisIteration;
                if (avgLoopSeconds > expectedSeconds * slowThreshold && frameStep < maxFrameStep)
                {
                    frameStep++;
                }
                else if (avgLoopSeconds < expectedSeconds * fastThreshold && frameStep > 1)
                {
                    frameStep--;
                }

                frame += stepForThisIteration;
                var targetDuration = TimeSpan.FromTicks(targetInterval.Ticks * stepForThisIteration);
                var remain = targetDuration - loopTimer.Elapsed;
                if (remain > TimeSpan.Zero)
                {
                    await Task.Delay(remain, ct);
                }
            }
            SetStateOK();
            // modify the preview view directly, so no need on rendering to file and loading back
            return "";
        }

        previewer.OnProgressChanged += progChanged;
        try
        {
            return await previewer.RenderSomeFrames(
                startPoint,
                LiveVideoPreviewBufferLength,
                (int)(previewWidth / LivePreviewResolutionFactor),
                (int)ProjectInfo.TargetFrameRate,
                (int)(previewHeight / LivePreviewResolutionFactor),
                ct);
        }
        finally
        {
            previewer.OnProgressChanged -= progChanged;
        }
    }

    private static View CreatePropertiesPlaceholder(string text)
    => new Label
    {
        Text = text,
        TextColor = Colors.White,
        HorizontalOptions = LayoutOptions.Center,
        VerticalOptions = LayoutOptions.Center,
        Opacity = 0.85,
        Margin = new Thickness(12)
    };
    #endregion

    #region handle changes
    private void TryMoveToInitialPreviewFrame(DraftStructureJSON draft)
    {
        if (_hasResolvedInitialPreviewFrame)
        {
            return;
        }

        _hasResolvedInitialPreviewFrame = true;

        if (_selected is not null || _selectedClipIds.Count > 0)
        {
            return;
        }

        if (_currentFrame > 0 || draft.Clips is null || draft.Clips.Length == 0)
        {
            return;
        }

        var firstVisualStartFrame = draft.Clips
            .OfType<ClipDraftDTO>()
            .Where(c => c.ShouldDisplayInUI && c.ClipType != ClipMode.AudioClip && c.ClipType != ClipMode.MarkingClip)
            .Select(c => (double)c.StartFrame)
            .DefaultIfEmpty(0)
            .Min();

        if (firstVisualStartFrame <= 0)
        {
            return;
        }

        _currentFrame = firstVisualStartFrame;
        SyncClipEditorCurrentFrame();
        UpdatePlayheadPosition();
        CurrentPlayheadLabel.Text = $"{TimeSpan.FromSeconds(_currentFrame * SecondsPerFrame):mm\\:ss\\.ff} / {TimeSpan.FromSeconds(ProjectDuration * SecondsPerFrame):mm\\:ss}";
    }

    private async void DraftChanged(object? sender, ClipUpdateEventArgs e)
    {
        if (AlreadyDisappeared) return;

        if (string.IsNullOrEmpty(WorkingPath))
        {
            SetStateFail(Localized.DraftPage_CannotSave_NoPath);
        }
        if (IsReadonly)
        {
            SetStateFail(Localized.DraftPage_CannotSave_Readonly);
        }

        foreach (var item in Clips)
        {
            if (!item.Value.isInfiniteLength && item.Value.lengthInFrame > item.Value.maxFrameCount)
            {
                SetStateFail($"Clip {item.Key} has a invalid length {item.Value.lengthInFrame} frames, larger than it's source {item.Value.maxFrameCount}.");
            }
        }
        if (!IsReadonly && (!e?.NoSave ?? false))
        {
            await Save(false, e);
            try
            {
                HistorySubWindow.Content = new DraftSettingPage(this).BuildHistoryTab();
            }
            catch { }
        }

        UpdatePlayheadHeight();
        var d = DraftImportAndExportHelper.ExportFromDraftPage(this, includeUiOnlyClips: false);
        SetStateBusy();
        SetStatusText(Localized.DraftPage_ApplyingChanges);

        try
        {
            ProjectDuration = Math.Max(d.Duration, d.AudioDuration);
            TryMoveToInitialPreviewFrame(d);
            await ClipEditor.UpdateClips(Clips);
            ClipEditor.SetCurrentFrame((uint)Math.Max(0, _currentFrame));
            await previewer.UpdateDraft(d);
            await DynamicPreviewProvider.UpdateDraft(d);
            await RefreshPreviewFromCurrentProviderAsync();
            SetStatusText(Localized.DraftPage_ChangesApplied);
            SetStateOK();
        }
        catch (Exception ex)
        {
            Log(ex, "apply change", this);
            SetStateFail(Localized._ExceptionTemplate(ex));
#if DEBUG
            if (await DisplayAlertAsync(Localized._Error, Localized.DraftPage_ApplyChangesFail(ex), "Throw", Localized._OK)) throw;

#else
            await DisplayAlertAsync(Localized._Error, Localized.DraftPage_ApplyChangesFail(ex), Localized._OK);
#endif

        }

    }

    private async Task OnClipEditorUpdate()
    {
        if (AlreadyDisappeared) return;

        OnClipChanged?.Invoke(this, new ClipUpdateEventArgs { Reason = ClipUpdateReason.ClipPositionMoved, SourceId = _selected?.Id ?? Guid.NewGuid().ToString(), SourceName = _selected?.DisplayName ?? "Clip", DetailInfo = "Size and position", NoSave = false });

        var d = DraftImportAndExportHelper.ExportFromDraftPage(this, includeUiOnlyClips: false);
        await previewer.UpdateDraft(d);
        await DynamicPreviewProvider.UpdateDraft(d);

        var currentX = PlayheadLine.TranslationX - TrackHeadLayout.Width;
        if (currentX < 0) currentX = 0;
        var duration = PixelToFrame(currentX);
        _currentFrame = duration;
        SyncClipEditorCurrentFrame();

        if (UseRealtimePreview)
        {
            await RefreshDynamicPreviewOverlay();
        }
        else
        {
            await RenderOneFrame(duration);
        }
    }


    private async void PlayheadTapped(object? sender, TappedEventArgs e)
    {
        try
        {
            // Get tap position relative to the ruler
            var p = e.GetPosition(RulerLayout);
            if (p is null) return;

            // Convert to OverlayLayer coordinate space
            Point rulerAbs = GetAbsolutePosition(RulerLayout, null);
            Point overlayAbs = GetAbsolutePosition(OverlayLayer, null);
            double xInOverlay = rulerAbs.X - overlayAbs.X + p.Value.X;

            // Optional: snap to grid/clip edges using existing logic
            double snappedX = SnapPixels(xInOverlay);

            // Clamp to overlay bounds
            double overlayWidth = OverlayLayer.Width > 0 ? OverlayLayer.Width : this.Width;
            double playheadWidth = (PlayheadLine.Width > 0) ? PlayheadLine.Width : PlayheadLine.WidthRequest;
            if (double.IsNaN(overlayWidth) || overlayWidth <= 0) overlayWidth = 0;
            double clampedX = Math.Clamp(snappedX, 0, Math.Max(0, overlayWidth - playheadWidth));

            if (clampedX - TrackHeadLayout.Width >= 0 || TimelineScrollView.ScrollX > 0)
            {
                var duration = PixelToFrame(clampedX - TrackHeadLayout.Width + TimelineScrollView.ScrollX);
                _currentFrame = duration;
                SyncClipEditorCurrentFrame();
                UpdatePlayheadPosition();
                CurrentPlayheadLabel.Text = $"{TimeSpan.FromSeconds(duration * SecondsPerFrame):mm\\:ss\\.ff} / {TimeSpan.FromSeconds(ProjectDuration * SecondsPerFrame):mm\\:ss}";
                try
                {
                    await RenderOneFrame(duration);
                }
                catch (Exception ex)
                {
                    Log(ex, $"Render frame {ex}", this);
                    await DisplayAlertAsync(Localized._Error, Localized.DraftPage_RenderFail(duration, ex), Localized._OK);
                }
            }
        }
        catch (Exception ex)
        {
            Log(ex, "playhead tap", this);
        }
    }

    private async Task SyncClipsToMultiClipEditorMode(uint currentFrame)
    {
        try
        {
            if (Clips == null || Clips.Count == 0)
            {
                return;
            }

            await ClipEditor.UpdateClips(Clips);
            ClipEditor.SetCurrentFrame(currentFrame);
        }
        catch (Exception ex)
        {
            LogDiagnostic($"Failed to sync clips to ClipEditor: {ex.Message}");
        }
    }

    private void TimelineScrollView_Scrolled(object sender, ScrolledEventArgs e)
    {
        if (sender == TimelineScrollView)
        {
            if (Math.Abs(SubTimelineScrollView.ScrollX - e.ScrollX) > 0.1)
                SubTimelineScrollView.ScrollToAsync(e.ScrollX, 0, false);
        }
        else if (sender == SubTimelineScrollView)
        {
            if (Math.Abs(TimelineScrollView.ScrollX - e.ScrollX) > 0.1)
                TimelineScrollView.ScrollToAsync(e.ScrollX, 0, false);
        }
        UpdatePlayheadPosition(e.ScrollX);
    }

    private async Task MovePlayhead(int deltaFrames)
    {
        var targetFrame = _currentFrame + deltaFrames;
        if (targetFrame < 0) targetFrame = 0;
        _currentFrame = targetFrame;
        SyncClipEditorCurrentFrame();

        UpdatePlayheadPosition();

        var timeX = FrameToPixel((uint)_currentFrame);

        // Auto Scroll
        var scrollX = TimelineScrollView.ScrollX;
        var viewportWidth = TimelineScrollView.Width;

        if (viewportWidth > 0)
        {
            double margin = 50;

            if (timeX < scrollX + margin)
            {
                await TimelineScrollView.ScrollToAsync(Math.Max(0, timeX - margin), 0, true);
                // After scrolling, the playhead position (overlay) needs update? 
                // The ScrollView.Scrolled event usually handles this, but calling it manually ensures sync.
                UpdatePlayheadPosition();
            }
            else if (timeX > scrollX + viewportWidth - margin)
            {
                await TimelineScrollView.ScrollToAsync(timeX - viewportWidth + margin, 0, true);
                UpdatePlayheadPosition();
            }
        }

        // Render Logic
        _movePlayheadDebounceCts?.Cancel();

        if (previewer.IsFrameRendered((uint)_currentFrame))
        {
            await RenderOneFrame((uint)_currentFrame);
        }
        else
        {
            _movePlayheadDebounceCts = new CancellationTokenSource();
            var token = _movePlayheadDebounceCts.Token;
            try
            {
                await Task.Delay(200, token);
                if (!token.IsCancellationRequested)
                {
                    await RenderOneFrame((uint)_currentFrame);
                }
            }
            catch (TaskCanceledException) { }
        }
    }

    private void UpdatePlayheadPosition() => UpdatePlayheadPosition(TimelineScrollView.ScrollX);

    private void UpdatePlayheadPosition(double scrollX)
    {
        double timeX = FrameToPixel((uint)_currentFrame);
        double screenX = timeX + TrackHeadLayout.Width - scrollX;
        PlayheadLine.TranslationX = screenX;
    }

    private void UpdatePlayheadHeight()
    {
        PlayheadLine.HeightRequest = (TrackContentLayout.Children.Count + SubTrackContentLayout.Children.Count) * ClipHeight;
    }

    private void UpdateTimelineWidth()
    {
        double maxPixel = 0;
        foreach (var clip in Clips.Values)
        {
            double end = clip.Clip.TranslationX + clip.Clip.WidthRequest;
            if (end > maxPixel) maxPixel = end;
        }

        maxPixel += 50;

        double minWidth = Math.Max(1000d, Window?.Width ?? 2000 + 200);
        if (maxPixel < minWidth) maxPixel = minWidth;

        TrackContentLayout.WidthRequest = maxPixel;
        SubTrackContentLayout.WidthRequest = maxPixel;

        // 更新所有ext endToWholeDraft的clips，使其能延伸到整个项目
        UpdateAllExtendToWholeDraftClips();
    }

    public async Task Save(bool noSlot = false, ClipUpdateEventArgs? args = null)
    {
        if (string.IsNullOrEmpty(WorkingPath))
        {
            Log("saving failed: working path is empty", "warn");
            SetStateFail(Localized.DraftPage_CannotSave_NoPath);
            return;
        }
        if (IsReadonly)
        {
            Log("saving failed: project is read-only", "warn");
            SetStateFail(Localized.DraftPage_CannotSave_Readonly);
            return;
        }
        var draft = DraftImportAndExportHelper.ExportFromDraftPage(this, includeUiOnlyClips: true);
        var assets = Assets.Values.ToList();
        string slot = ".";
        if (noSlot)
        {
            ProjectInfo.NormallyExited = true;
            await File.WriteAllTextAsync(Path.Combine(WorkingPath, "timeline.json"), JsonSerializer.Serialize(draft, savingOpts), default);
            await File.WriteAllTextAsync(Path.Combine(WorkingPath, "assets.json"), JsonSerializer.Serialize(assets, savingOpts), default);
            try
            {
                CancellationTokenSource cts = new();
                cts.CancelAfter(10000);
                await Task.Run(() =>
                {
                    try
                    {
                        var thumbPath = ProjectInfo.ThumbPath ?? previewer.RenderFrame(0U, 1280, 720);
                        if (!string.IsNullOrEmpty(thumbPath) && File.Exists(thumbPath))
                        {
                            var destPath = Path.Combine(WorkingPath, "thumbs", "_project.png");
                            File.Copy(thumbPath, destPath, true);
                        }
                    }
                    catch { }

                }, cts.Token);

            }
            catch { }
        }
        else //avoid worst condition (crashes while saving)
        {
            if (_historyNavigatedByUndoRedo)
            {
                PruneNewerSaveSlotsFromCurrent();
            }

            if (CurrentSaveSlotIndex + 1 < MaximumSaveSlot)
            {
                slot = $"slot_{CurrentSaveSlotIndex + 1}";
                CurrentSaveSlotIndex++;
            }
            else
            {
                slot = "slot_0";
                CurrentSaveSlotIndex = 0;
            }
            ProjectInfo.SaveSlotIndicator = CurrentSaveSlotIndex;
            LogDiagnostic($"Switching slot to {CurrentSaveSlotIndex}...");
            saveLocker.Enter();
            try
            {
                if (args is not null)
                {
                    draft.ChangeReason = args.ToString();
                }
                Directory.CreateDirectory(Path.Combine(WorkingPath, "saveSlots", slot));
                await File.WriteAllTextAsync(Path.Combine(WorkingPath, "saveSlots", slot, "timeline.json"), JsonSerializer.Serialize(draft, savingOpts), default);
                await File.WriteAllTextAsync(Path.Combine(WorkingPath, "saveSlots", slot, "assets.json"), JsonSerializer.Serialize(assets, savingOpts), default);
            }
            catch (Exception ex)
            {
                Log(ex, "saving draft failed", this);
                SetStateFail(Localized.DraftPage_CannotSave_Exception(ex));
            }
            finally
            {
                saveLocker.Exit();

            }

            _historyNavigatedByUndoRedo = false;

        }

        //SaveMainMultiWindowViewStateToProjectInfo();

        ProjectDuration = draft.Duration;
        ProjectInfo.LastChanged = DateTime.Now;
        ProjectInfo.LastOpenAPIBaseVersion = IPluginBase.CurrentPluginAPIVersion;
        ProjectInfo.LastOpenAppVersion = Assembly.GetExecutingAssembly()?.GetName()?.Version?.ToString() ?? "0.0.0.0";
        ProjectInfo.PluginUsed =
            draft.Clips.OfType<ClipDraftDTO>()
                       .Select(c => c.FromPlugin)
                       .Concat(draft.Clips.OfType<ClipDraftDTO>().SelectMany(c => c.Effects?.Select(eff => eff.FromPlugin) ?? []))
                       .Concat(draft.Clips.OfType<ClipDraftDTO>().SelectMany(c => c.EffectBundles?.Select(eff => eff.FromPlugin) ?? []))
                       .Where(c => !c.StartsWith("projectFrameCut.Render."))
                       .Distinct().ToList();

        saveLocker.Enter();
        try
        {
            await File.WriteAllTextAsync(Path.Combine(WorkingPath, "project.pjfc"), JsonSerializer.Serialize(ProjectInfo, savingOpts), default);
        }
        catch (Exception ex)
        {
            Log(ex, "saving draft failed", this);
            SetStateFail(Localized.DraftPage_CannotSave_Exception(ex));
        }
        finally
        {
            saveLocker.Exit();
        }

    }

    private List<SaveSlotMeta> GetSaveSlotsSortedByTime()
    {
        List<SaveSlotMeta> slots = [];
        if (string.IsNullOrWhiteSpace(WorkingPath))
        {
            return slots;
        }

        var saveRoot = Path.Combine(WorkingPath, "saveSlots");
        if (!Directory.Exists(saveRoot))
        {
            return slots;
        }

        foreach (var dir in Directory.GetDirectories(saveRoot, "slot_*"))
        {
            var folderName = Path.GetFileName(dir);
            if (string.IsNullOrWhiteSpace(folderName) || !folderName.StartsWith("slot_"))
            {
                continue;
            }

            var indexText = folderName.Substring("slot_".Length);
            if (!int.TryParse(indexText, out var slotIndex))
            {
                continue;
            }

            if (slotIndex < 0 || slotIndex >= MaximumSaveSlot)
            {
                continue;
            }

            var timelinePath = Path.Combine(dir, "timeline.json");
            if (!File.Exists(timelinePath))
            {
                continue;
            }

            try
            {
                DateTime savedAtUtc;
                var tml = File.ReadAllText(timelinePath);
                var draft = JsonSerializer.Deserialize<DraftStructureJSON>(tml, savingOpts);
                if (draft is null || draft.SavedAt == default)
                {
                    savedAtUtc = File.GetLastWriteTimeUtc(timelinePath);
                }
                else
                {
                    savedAtUtc = draft.SavedAt.Kind switch
                    {
                        DateTimeKind.Utc => draft.SavedAt,
                        DateTimeKind.Local => draft.SavedAt.ToUniversalTime(),
                        _ => DateTime.SpecifyKind(draft.SavedAt, DateTimeKind.Local).ToUniversalTime()
                    };
                }

                slots.Add(new SaveSlotMeta
                {
                    SlotIndex = slotIndex,
                    SavedAtUtc = savedAtUtc
                });
            }
            catch
            {
                // Ignore broken slot data to keep undo/redo usable.
            }
        }

        return slots
            .OrderBy(x => x.SavedAtUtc)
            .ThenBy(x => x.SlotIndex)
            .ToList();
    }

    private SaveSlotMeta? GetPreviousSlotByTime()
    {
        var slots = GetSaveSlotsSortedByTime();
        var current = slots.FirstOrDefault(x => x.SlotIndex == CurrentSaveSlotIndex);
        if (current is null)
        {
            return null;
        }

        return slots.LastOrDefault(x =>
            x.SavedAtUtc < current.SavedAtUtc
            || (x.SavedAtUtc == current.SavedAtUtc && x.SlotIndex < current.SlotIndex));
    }

    private SaveSlotMeta? GetNextSlotByTime()
    {
        var slots = GetSaveSlotsSortedByTime();
        var current = slots.FirstOrDefault(x => x.SlotIndex == CurrentSaveSlotIndex);
        if (current is null)
        {
            return null;
        }

        return slots.FirstOrDefault(x =>
            x.SavedAtUtc > current.SavedAtUtc
            || (x.SavedAtUtc == current.SavedAtUtc && x.SlotIndex > current.SlotIndex));
    }

    private void PruneNewerSaveSlotsFromCurrent()
    {
        var slots = GetSaveSlotsSortedByTime();
        var current = slots.FirstOrDefault(x => x.SlotIndex == CurrentSaveSlotIndex);
        if (current is null)
        {
            return;
        }

        foreach (var slot in slots.Where(x =>
                     x.SlotIndex != current.SlotIndex
                     && (x.SavedAtUtc > current.SavedAtUtc
                         || (x.SavedAtUtc == current.SavedAtUtc && x.SlotIndex > current.SlotIndex))))
        {
            try
            {
                var slotPath = Path.Combine(WorkingPath, "saveSlots", $"slot_{slot.SlotIndex}");
                if (Directory.Exists(slotPath))
                {
                    Directory.Delete(slotPath, true);
                    LogDiagnostic($"Pruned newer save slot: slot_{slot.SlotIndex}");
                }
            }
            catch (Exception ex)
            {
                Log(ex, $"prune save slot slot_{slot.SlotIndex}", this);
            }
        }
    }

    private void RedoChanges()
    {
        var nextSlot = GetNextSlotByTime();
        if (nextSlot is null)
        {
            SetStateOK(Localized.DraftPage_RedoAndUndo_NoMoreSlots);
            return;
        }
        if (IsSyncCooldown()) return;
        SetSyncCooldown();
        var oldSlot = CurrentSaveSlotIndex;
        ApplySlot(nextSlot.SlotIndex);
        if (CurrentSaveSlotIndex != oldSlot)
        {
            _historyNavigatedByUndoRedo = true;
        }
    }

    private void UndoChanges()
    {
        var nextSlot = GetPreviousSlotByTime();
        if (nextSlot is null)
        {
            SetStateOK(Localized.DraftPage_RedoAndUndo_NoMoreSlots);
            return;
        }
        if (IsSyncCooldown()) return;
        SetSyncCooldown();
        var oldSlot = CurrentSaveSlotIndex;
        ApplySlot(nextSlot.SlotIndex);
        if (CurrentSaveSlotIndex != oldSlot)
        {
            _historyNavigatedByUndoRedo = true;
        }
    }

    public void ApplySlot(int slotIndex)
    {
        try
        {
            LogDiagnostic($"Switching slot from {CurrentSaveSlotIndex} to {slotIndex}...");
            var slot = $"slot_{slotIndex}";
            var tml = File.ReadAllText(Path.Combine(WorkingPath, "saveSlots", slot, "timeline.json"));
            var assets = JsonSerializer.Deserialize<List<AssetItem>>(File.ReadAllText(Path.Combine(WorkingPath, "saveSlots", slot, "assets.json")), savingOpts) ?? new();
            var draftJson = JsonSerializer.Deserialize<DraftStructureJSON>(tml, savingOpts);
            if (draftJson is null)
            {
                SetStateOK(Localized.DraftPage_RedoAndUndo_Failed);
                return;
            }
            (var clips, var tracks) = DraftImportAndExportHelper.ImportFromJSON(draftJson, ProjectInfo);
            Clips = new ConcurrentDictionary<string, ClipElementUI>(clips);
            Assets = new ConcurrentDictionary<string, AssetItem>(assets.ToDictionary((a) => a.AssetId ?? $"unknown+{Random.Shared.Next()}", (a) => a));

            foreach (var item in Tracks)
            {
                var t = item.Value;
                while (t.Children.Count > 0)
                {
                    t.Children.RemoveAt(0);
                }
            }

            foreach (var kv in Clips.OrderBy(kv => kv.Value.origTrack ?? 0).ThenBy(kv => kv.Value.origX))
            {
                var item = kv.Value;
                int t = item.origTrack ?? 0;
                if (!Tracks.ContainsKey(t)) AddATrack(t);
                AddAClip(item);
                RegisterClip(item, true);
            }

            EnsureContinuousTrackIndices();
            CurrentSaveSlotIndex = slotIndex;
            HistorySubWindow.Content = new DraftSettingPage(this).BuildHistoryTab();
            DraftChanged(this, new() { DetailInfo = "Sync changes", NoSave = true });
            SetStateOK(Localized.DraftPage_RedoAndUndo_Success(draftJson.SavedAt));

        }
        catch (Exception ex)
        {
            if (MyLoggerExtensions.LoggingDiagnosticInfo)
            {
                Log(ex, "apply changes", this);
            }
            SetStateOK(Localized.DraftPage_RedoAndUndo_Failed);
        }
    }

    private bool IsSyncCooldown() => DateTime.Now - lastSyncTime < SyncCooldown;
    private void SetSyncCooldown() => lastSyncTime = DateTime.Now;


    #endregion

    #region misc
    private async Task CleanRenderCache()
    {
        SetStateBusy();
        try
        {
            foreach (var item in Directory.GetFiles(Path.Combine(WorkingPath, "thumbs")))
            {
                File.Delete(item);
            }
        }
        catch (Exception ex)
        {
            Log(ex, "clean render cache", this);
            await DisplayAlertAsync(Localized._Error, $"Failed to cleanup preview.({Localized._ExceptionTemplate(ex)})", Localized._OK);
        }

        SetStateOK(Localized.DraftPage_CleanRenderCache_Done);
    }

    public async void OnRefreshButtonClicked(object sender, EventArgs e)
    {
        SetStateBusy();
        await Save(true);
        await HidePopup(true);
        UnSelectTapGesture_Tapped(sender, null!);
        UpdateTimelineWidth();
        SetTimelineScrollEnabled(true);
        await ReRenderUI();
        await RefreshDynamicPreviewOverlay();
        DraftChanged(sender, new() { NoSave = true });
#if WINDOWS
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
        foreach (var item in new string[] { nameof(SelectedAnyClip), nameof(_ShouldShowClipMoveControlInCenterInfoBar), nameof(_ShouldShowCenterCompactControlGrid), nameof(UseCompactLayout), nameof(UnNullUseCompactLayout), nameof(MultiSelectEnabled), nameof(UseRealtimePreview) })
        {
            OnPropertyChanged(item);
        }
        SetStateOK();
        SetStatusText(Localized.DraftPage_EverythingFine);
    }

    bool ExitNoSave = false;

    private async Task ExitButNoSave()
    {
        if (await DisplayAlertAsync(Localized._Warn, Localized.DraftPage_ExitWithoutSave_Warn, Localized._Confirm, Localized._Cancel))
        {
            ExitNoSave = true;
            await Navigation.PopAsync();
        }
    }


    private async Task GotoButtonClicked()
    {
        var input = await DisplayPromptAsync(Localized._Info, Localized.DraftPage_GotoFrame, Localized._OK, Localized._Cancel, null, 0, null, "");
        if (string.IsNullOrEmpty(input)) return;
        try
        {
            double result = _currentFrame;
            if (input.StartsWith('-') || input.StartsWith('+'))
            {
                var length = input.Substring(1);
                if (length.StartsWith('#'))
                {
                    var delta = int.Parse(length.Substring(1));
                    result += input switch
                    {
                        var v when v.StartsWith('-') => -delta,
                        _ => delta
                    };
                }
                else
                {
                    var delta = int.Parse(length);
                    result += input switch
                    {
                        var v when v.StartsWith('-') => -delta,
                        _ => delta
                    } * (1 / SecondsPerFrame);
                }
            }
            else
            {
                if (input.StartsWith('#'))
                {
                    result = int.Parse(input.Substring(1));
                }
                else
                {
                    var ts = TimeSpan.Parse(input);
                    result = ts.TotalSeconds * (1 / SecondsPerFrame);
                }
            }

            _currentFrame = result;
            SyncClipEditorCurrentFrame();
            UnSelectTapGesture_Tapped(null!, null!);
            SetTimelineScrollEnabled(true);
            UpdatePlayheadPosition();
        }
        catch (Exception ex)
        {
            Log(ex, $"Go to specific position {input}", this);
        }
        var t = TimeSpan.FromSeconds(_currentFrame * SecondsPerFrame).ToString("mm\\:ss\\.ff");
        await DisplayAlertAsync(Localized._Info, Localized.DraftPage_GotoFrame_Success(t), Localized._OK);

    }

    ToolbarItem RunningTaskToolbarItem = new();

    void AddToolbarBtns()
    {
        LogDiagnostic("Adding toolbars buttons...");
        try
        {
            ToolbarItems.Clear();
            ToolbarItems.Add(new ToolbarItem
            {
                Text = Localized.DraftPage_MenuBar_Edit_Undo,
                Order = ToolbarItemOrder.Secondary,
                Priority = 0,
                Command = UndoCommand
            });
            ToolbarItems.Add(new ToolbarItem
            {
                Text = Localized.DraftPage_MenuBar_Edit_Redo,
                Order = ToolbarItemOrder.Secondary,
                Priority = 0,
                Command = RedoCommand
            });
            ToolbarItems.Add(new ToolbarItem
            {
                Text = Localized.DraftPage_MenuBar_Edit_History,
                Order = ToolbarItemOrder.Secondary,
                Priority = 0,
                Command = ManageWindowCommand,
                CommandParameter = "history"
            });
            ToolbarItems.Add(new ToolbarItem
            {
                Text = Localized.DraftPage_MenuBar_Edit_ConfigurePreview,
                Order = ToolbarItemOrder.Secondary,
                Priority = 0,
                Command = new Command(async () =>
                {
                    var option = await DisplayActionSheetAsync(Localized.DraftPage_MenuBar_Edit_ConfigurePreview, Localized._Cancel, null, (ResolutionPicker.ItemsSource as List<string>)?.ToArray() ?? []);
                    if (!string.IsNullOrWhiteSpace(option)) ResolutionPicker.SelectedItem = option;
                })
            });

            RunningTaskToolbarItem = new ToolbarItem
            {
                Text = Localized.DraftPage_MenuBar_Jobs_ManageJobs,
                Order = ToolbarItemOrder.Primary,
                Priority = 0,
                Command = ManageJobsCommand
            };
            ToolbarItems.Add(RunningTaskToolbarItem);

            ToolbarItems.Add(new ToolbarItem
            {
                Text = Localized.DraftPage_GoRender,
                Order = ToolbarItemOrder.Secondary,
                Priority = 0,
                Command = GoRenderCommand
            });

            ToolbarItems.Add(new ToolbarItem
            {
                Text = Localized.DraftPage_MenuBar_Project_Save,
                Order = ToolbarItemOrder.Secondary,
                Priority = 0,
                Command = SaveCommand
            });

            ToolbarItems.Add(new ToolbarItem
            {
                Text = Localized._Settings,
                Order = ToolbarItemOrder.Secondary,
                Priority = 1,
                Command = SettingsCommand
            });

            var MoreOptionButton = new ToolbarItem
            {
                Text = Localized.HomePage_MenuBar_MoreOptions,
                Order = ToolbarItemOrder.Secondary,
                Priority = 1
            };

            MoreOptionButton.Clicked += ShowMoreOptionsMenu;
            ToolbarItems.Add(MoreOptionButton);

        }
        catch
        {
        }
    }

    private async void ShowMoreOptionsMenu(object? sender, EventArgs e)
    {
        Dictionary<string, (ICommand? command, object? argument)> actionsPair = MenuBarItems
            .SelectMany(menuBarItem =>
                menuBarItem.OfType<MenuFlyoutItem>()
                    .Where(item => item.Command != null)
                    .Select(item => (
                        Key: $"{menuBarItem.Text} -> {item.Text}",
                        Value: ((ICommand?)item.Command, (object?)item.CommandParameter)
                    ))
            )
            .ToDictionary(x => x.Key, x => x.Value);
        Dictionary<string, ICommand?> debugActionsPair = new Dictionary<string, ICommand?>
        {
            {
                "Debug_ToggleDebugViewForInteractableEditor" ,new Command(async () =>
                {
                    ClipEditor.ShowPreviewDebugOverlay = !ClipEditor.ShowPreviewDebugOverlay;
                })
            },
            {
                "Debug_CreatePopup" ,new Command(async () =>
                {
                    string[] type = ["right", "bottom","center","dialog"];
                    var select = await DisplayActionSheetAsync("info", "select", null, type);
                    await ShowAPopup(content: await BuildPropertyPanel(_selected), border: _selected?.Clip, clip:_selected, mode: select);
                })
            },
            {
                "Debug_DumpProcessStack", new Command(async () =>
                {
                    var f = previewer.GetFrame((uint)_currentFrame, previewWidth, previewHeight);
                    var text = PictureProcessStack.FormatProcessStackForLog(f.ProcessStack);
                    await Microsoft.Maui.ApplicationModel.DataTransfer.Clipboard.Default.SetTextAsync(text);
                })
            },
            {
                "Debug_OpenAWindow", new Command(async () =>
                {
                    var page = new TestPage();
                    var wd = new MultiWindowItem()
                    {
                        Content = page.Content,
                        Title = "Test Window",
                    };
                    await Dispatcher.DispatchAsync(async () =>
                    {
                        MainMultiWindowView.AddWindow(wd);
                    });
                })
            },
            {
                "Debug_Crash", new Command(async () =>
                {
                    var type = await DisplayActionSheetAsync("Choose a favor you'd like", "Cancel", null, "Environment.FailFast", "Native(null pointer)", "Managed(NullReferenceException)");
                    switch (type)
                    {
                        case "Native(null pointer)":
            #if ANDROID
                            throw new Java.Lang.NullPointerException("test crash from native code");
            #elif WINDOWS
                            IntPtr ptr = IntPtr.Zero;
                            Marshal.WriteInt32(ptr, 42);
            #endif
                            break;
                        case "Managed(NullReferenceException)":
                            throw new NullReferenceException("test crash");
                        case "Environment.FailFast":
                            Environment.FailFast("test crash");
                            break;
                    }
                })
            }
        };

        var option = await DisplayActionSheetAsync(Localized._Info, Localized._Cancel, null, actionsPair.Keys.Concat(SettingsManager.IsBoolSettingTrue("DeveloperMode") ? debugActionsPair.Keys : new List<string>()).ToArray());

        if (string.IsNullOrWhiteSpace(option)) return;

        if (actionsPair.TryGetValue(option, out var cmd))
        {
            cmd.command?.Execute(cmd.argument);
        }
        if (debugActionsPair.TryGetValue(option, out var dbgCmd))
        {
            dbgCmd.Execute(null!);
        }
    }


    private async void OnExportedClick(object sender, EventArgs e)
    {
        await Save(true);
        var draft = DraftImportAndExportHelper.ExportFromDraftPage(this, true, false);
        var page = new RenderPage(WorkingPath, ProjectDuration, ProjectInfo, draft);
        await Dispatcher.DispatchAsync(async () =>
        {
            Shell.SetTabBarIsVisible(page, false);
            Shell.SetNavBarIsVisible(page, true);
            await Navigation.PushAsync(page);
        });

    }

    // Default preview resolution should match InteractableEditor defaults (1920x1080)
    public int previewWidth = 1920;
    public int previewHeight = 1080;

    private async void SettingsClick(object sender, EventArgs e)
    {
        await ShowAPopup(new DraftSettingPage(this).Content, mode: "dialog");
    }

    private async void ResolutionPicker_SelectedIndexChanged(object sender, EventArgs e)
    {
        var picker = sender as Picker;
        var requireCustomResolution = false;
        if (picker != null)
        {
            if (picker.SelectedItem is string picked)
            {
                if (picked == Localized.DraftPage_DynamicPreview)
                {
                    UseRealtimePreview = true;
                    PreviewSubwindow.Title = $"{Localized.AssetPage_ShowPreview} - {Localized.DraftPage_DynamicPreview}";
                }
                else
                {
                    UseRealtimePreview = false;
                    PreviewSubwindow.Title = Localized.AssetPage_ShowPreview;
                    var parts = picked.Split('x');
                    if (parts.Length == 2 &&
                       int.TryParse(parts[0].Trim(), out int w1) &&
                       int.TryParse(parts[1].Trim(), out int h1))
                    {
                        SetStatusText(Localized.DraftPage_PrevResultion_Seted(w1, h1));
                        previewWidth = w1;
                        previewHeight = h1;
                    }
                    else
                    {
                        requireCustomResolution = true;
                    }
                }
            }
        }

        if (requireCustomResolution)
        {
            var widthInput = await DisplayPromptAsync(Localized._Info, Localized.DraftPage_PrevResultion_Custom_InputWidth, initialValue: "1920");
            var heightInput = await DisplayPromptAsync(Localized._Info, Localized.DraftPage_PrevResultion_Custom_InputHeight, initialValue: "1080");
            if (int.TryParse(widthInput, out int w) && int.TryParse(heightInput, out int h))
            {
                SetStatusText(Localized.DraftPage_PrevResultion_Seted(w, h));
                previewWidth = w;
                previewHeight = h;
            }
        }

        // Clip overlay coordinates are always stored in project resolution.
        ClipEditor.UpdateVideoResolution(ProjectInfo.RelativeWidth, ProjectInfo.RelativeHeight);

        await Dispatcher.DispatchAsync(() =>
        {
            ClipEditor.SetRealtimePreviewContent(EnsureRealtimePreviewHost());
            LivePreviewPlayer.IsVisible = false;
            DynamicPreviewProvider.IsVisible = UseRealtimePreview;
            if (!UseRealtimePreview)
            {
                ClipEditor.SetStaticPreviewVisible(true);
            }
        });

        ApplyClipEditorPreviewOverlayMode();

        await RefreshPreviewFromCurrentProviderAsync();
        OnPropertyChanged(nameof(UseRealtimePreview));

    }

    private void ZoomOutButton_Clicked(object sender, EventArgs e)
    {
        PerformZoom(1.2);
    }

    private void ZoomResetButton_Clicked(object sender, EventArgs e)
    {
        if (Math.Abs(tracksZoomOffest - 1.0) > 0.0001)
        {
            PerformZoom(1.0 / tracksZoomOffest);
        }
    }

    private void ZoomInButton_Clicked(object sender, EventArgs e)
    {
        PerformZoom(1.0 / 1.2);
    }

    /// <returns>A meaningless value. This allow to put this method in ... switch { ... => ..., } expression</returns>
    private int PerformZoom(double delta)
    {
        double oldZoom = tracksZoomOffest;
        double newZoom = tracksZoomOffest * delta;

        // Clamp zoom
        if (newZoom < 0.01) newZoom = 0.01;
        if (newZoom > 100) newZoom = 100;

        if (Math.Abs(newZoom - oldZoom) < 0.0001) return -1;

        tracksZoomOffest = newZoom;
        double ratio = oldZoom / newZoom;

        foreach (var kv in Clips)
        {
            var clip = kv.Value;
            if (clip == null) continue;

            clip.origX *= ratio;
            clip.origLength *= ratio;

            if (clip.Clip != null)
            {
                clip.Clip.TranslationX *= ratio;
                clip.ApplySpeedRatio();
            }
        }

        // Update Playhead
        double currentPlayheadX = PlayheadLine.TranslationX - TrackHeadLayout.Width;
        currentPlayheadX *= ratio;
        PlayheadLine.TranslationX = currentPlayheadX + TrackHeadLayout.Width;

        UpdateTimelineWidth();
        return -1; //allow to put this method in <var> switch { <case> } expression 
    }

    private Size GetScreenSizeInDp()
    {
        var info = DeviceDisplay.MainDisplayInfo;
        double widthDp = info.Width / info.Density;
        double heightDp = info.Height / info.Density;
        return new Size(widthDp, heightDp);
    }

    #endregion

    #region events
    protected override async void OnAppearing()
    {
        base.OnAppearing();
        if (AlreadyDisappeared)
        {
            Log($"FATAL: DraftPage has been appeared again since disappeared. \r\nStackTrace:{Environment.StackTrace}", "fatal");
            await Task.Delay(500);
            await Navigation.PopAsync();
            Content = new Label
            {
                Text = "You shouldn't see this page because of AlreadyDisappeared is true. Summit a issue about this in our repo.",
                HorizontalOptions = LayoutOptions.Center,
                VerticalOptions = LayoutOptions.Center
            };

            return;
        }

        await PostInit();

        var size = GetScreenSizeInDp();
        LogDiagnostic($"Window size on appearing: {size.Width:F0} x {size.Height:F0} (DIP)");
        await Task.Delay(50);

        var w = this.Window?.Width ?? 0;
        var h = this.Window?.Height ?? 0;
        WindowSize = new Size(w, h);

        OverlayLayer.InputTransparent = true;
        RightContentBorder.Content = CreatePropertiesPlaceholder(Localized.DraftPage_PropertyPanel_SelectToContinue);

    }

    protected override async void OnDisappearing()
    {
        AlreadyDisappeared = true;
        CancelPendingClipPlacement();
        await HidePopup();

        try
        {
            foreach (var item in MainMultiWindowView.Children.OfType<MultiWindowItem>().ToList())
            {
                try
                {
                    item.Close(true);
                }
                catch (Exception ex)
                {
                    Log(ex, $"close subwindow {item?.Title}", this);
                }
            }
        }
        catch (Exception ex)
        {
            Log(ex, "close subwindows", this);
        }


        try
        {
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
                        Text = ExitNoSave ? Localized.DraftPage_Processing : Localized.DraftPage_SavingChanges,
                        HorizontalTextAlignment = TextAlignment.Center,
                        Margin = new Thickness(0,10,0,0)
                    }
                }
            };
        }
        catch
        {
            //the window maybe closed; just ignore any exception here
        }


        if (this.Window is not null)
        {
            this.Window.SizeChanged -= Window_SizeChanged;
        }
        MyLoggerExtensions.OnExceptionLog -= MyLoggerExtensions_OnExceptionLog;

        //SaveMainMultiWindowViewStateToProjectInfo();


        foreach (var item in PluginManager.LoadedPlugins)
        {
            try
            {
                ProjectInfo = item.Value.OnProjectClose(ProjectInfo) ?? ProjectInfo;
            }
            catch (Exception ex)
            {
                Log(ex, $"plugin {item.Value.Name} OnProjectClose", this);
            }
        }

        try
        {
            if (!ExitNoSave) await Save(true);
            App.Current?.Windows?[0]?.Title = Localized.AppBrand;
            base.OnDisappearing();

        }
        catch (Exception ex)
        {
            Log(ex, "Save on exit", this);
            try
            {
                await (App.Current?.Windows?[0].Page?.DisplayAlertAsync(Localized._Error, Localized.DraftPage_CannotSave_Exception(ex), Localized._OK) ?? Task.CompletedTask);
            }
            catch { }
        }

    }

    public async void Window_SizeChanged(object? sender, EventArgs e)
    {
        double w = this.Window?.Width ?? 0;
        double h = this.Window?.Height ?? 0;
        WindowSize = new(w, h);
        LogDiagnostic($"Window size changed: {w:F0} x {h:F0} (DIP)");
        SyncPreviewSurfaceSize();
        UpdateTrackHeaderLayoutForViewport();
        UpdateTimelineWidth();
        UpdatePlayheadPosition();
        ApplyDefaultMainMultiWindowLayout();
    }

    private bool ignoreRunningTasks = false;

    protected override bool OnBackButtonPressed()
    {
        if (RunningTasks.Any(c => !c.Value.InnerTask.IsCompleted))
        {
            Dispatcher.Dispatch(async () =>
            {
                await Task.Delay(250);
                ignoreRunningTasks = await DisplayAlertAsync(Localized._Warn, Localized.DraftPage_EverythingFine, Localized._Confirm, Localized._Cancel);
                if (ignoreRunningTasks)
                {
                    await Navigation.PopAsync();
                }
            });
            return false;
        }
        if (!ignoreRunningTasks && Window is not null) Window?.SizeChanged -= Window_SizeChanged;
        return ignoreRunningTasks;
    }

    private void MyLoggerExtensions_OnExceptionLog(Exception obj)
    {
        Dispatcher.Dispatch(() =>
        {
            StatusLabel.TextColor = Colors.Red;
            StatusLabel.Text = Localized._ExceptionTemplate(obj);
        });
    }


#if WINDOWS
    private void OnTimelineScrollViewPointerWheelChanged(object sender, Microsoft.UI.Xaml.Input.PointerRoutedEventArgs e)
    {
        if (e.KeyModifiers.HasFlag(Windows.System.VirtualKeyModifiers.Shift) && sender is Microsoft.UI.Xaml.Controls.ScrollViewer sv)
        {
            var pointerPoint = e.GetCurrentPoint(sv);
            if (pointerPoint.Properties.IsHorizontalMouseWheel) return;

            var delta = pointerPoint.Properties.MouseWheelDelta;
            if (delta == 0) return;

            sv.ChangeView(sv.HorizontalOffset - delta, null, null);
            e.Handled = true;
        }
    }
#endif

    #endregion

    #region status
    public void SetStateBusy()
    {
        if (StateIndicator is null) return;
        Dispatcher.Dispatch(() =>
        {
            StateIndicator.Children.Clear();
            StateIndicator.Children.Add(new ActivityIndicator
            {
                Color = Colors.Orange,
                IsRunning = true,
                WidthRequest = 16,
                HeightRequest = 16,
                Margin = new(6, 3, 0, 0)
            });
        });
    }

    public void SetStateBusy(string text)
    {
        SetStateBusy();
        SetStatusText(text);
    }

    public void SetStateOK()
    {
        if (StateIndicator is null) return;
        Dispatcher.Dispatch(() =>
        {
            StateIndicator.Children.Clear();
            StateIndicator.Children.Add(new Microsoft.Maui.Controls.Shapes.Path
            {
                Stroke = Colors.Green,
                StrokeThickness = 3,
                Data = (Geometry)new PathGeometryConverter().ConvertFromInvariantString("M 4,12 L 9,17 L 20,6"),
                WidthRequest = 20,
                HeightRequest = 20,
                Margin = new Thickness(2, -1, 0, 0)
            });
            StatusLabel.TextColor = Colors.White;

        });

    }

    public void SetStateOK(string text)
    {
        SetStateOK();
        SetStatusText(text);
    }

    public void SetStateFail()
    {
        if (StateIndicator is null) return;
        Dispatcher.Dispatch(() =>
        {
            StateIndicator.Children.Clear();
            StateIndicator.Children.Add(new Microsoft.Maui.Controls.Shapes.Path
            {
                Stroke = Colors.Red,
                StrokeThickness = 3,
                Data = (Geometry)new PathGeometryConverter().ConvertFromInvariantString("M 4,4 L 20,20 M 20,4 L 4,20"),
                WidthRequest = 20,
                HeightRequest = 20,
                Margin = new Thickness(0, -3, 0, 0)
            });
        });

    }

    private void SetStateFail(string text)
    {
        SetStateFail();
        Dispatcher.Dispatch(() =>
        {
            StatusLabel.TextColor = Colors.Red;
            StatusLabel.Text = text;
        });
        if (LogUIMessageToLogger) Log(text, "UI err");
    }

    public void SetStatusText(string text)
    {
        Dispatcher.Dispatch(() =>
        {
            StatusLabel.TextColor = Colors.White;
            StatusLabel.Text = text;
            SemanticScreenReader.Default.Announce(text);
        });
        if (LogUIMessageToLogger) Log(text, "UI msg");
    }


    #endregion

    #region IDraftPage adapters
    MultiWindowView IDraftPage.MainMultiWindowView => MainMultiWindowView;
    IClipElementUI? IDraftPage.SelectedClip => SelectedClip;
    ConcurrentDictionary<string, DraftTasks> IDraftPage.RunningTasks => RunningTasks;

    private static ClipElementUI RequireConcreteClip(IClipElementUI clip, string paramName)
    {
        if (clip is ClipElementUI concrete)
        {
            return concrete;
        }

        throw new ArgumentException("Only ClipElementUI is supported by DraftPage.", paramName);
    }

    void IDraftPage.AddAClip(IClipElementUI c) => AddAClip(RequireConcreteClip(c, nameof(c)));

    bool IDraftPage.AddTransformBetweenSelected(Func<Guid, Guid, ITransform> transformFactory, IClipElementUI center, bool left, bool right, Action<IClipElementUI>? elementSetter)
    {
        Action<ClipElementUI>? concreteSetter = null;
        if (elementSetter is not null)
        {
            concreteSetter = clip => elementSetter(clip);
        }

        return AddTransformBetweenSelected(transformFactory, RequireConcreteClip(center, nameof(center)), left, right, concreteSetter);
    }

    void IDraftPage.BeginClipPlacement(Func<int, double, IClipElementUI> clipFactory, Predicate<int>? trackFilter, string? name)
    {
        BeginClipPlacement(
            (track, start) => RequireConcreteClip(clipFactory(track, start), nameof(clipFactory)),
            trackFilter,
            name);
    }

    IClipElementUI IDraftPage.CreateAndAddClip(double startX, double width, int trackIndex, string? id, string? labelText, Brush? background, Border? prototype, bool resolveOverlap, uint relativeStart, uint maxFrames, IClipElementUI? sourceElement)
    {
        return CreateAndAddClip(
            startX,
            width,
            trackIndex,
            id,
            labelText,
            background,
            prototype,
            resolveOverlap,
            relativeStart,
            maxFrames,
            sourceElement is null ? null : RequireConcreteClip(sourceElement, nameof(sourceElement)));
    }

    IClipElementUI IDraftPage.CreateFromAsset(AssetItem asset, int trackIndex, string fromPlugin, string? path)
        => CreateFromAsset(asset, trackIndex, fromPlugin, path);

    IClipElementUI IDraftPage.CreateFromAsset(AssetItem asset, int trackIndex, double startX, string fromPlugin, string? path)
        => CreateFromAsset(asset, trackIndex, startX, fromPlugin, path);

    (IClipElementUI? left, IClipElementUI? right) IDraftPage.FindNeighbors(IClipElementUI? clip)
    {
        var concrete = clip is null ? null : RequireConcreteClip(clip, nameof(clip));
        var (left, right) = FindNeighbors(concrete);
        return (left, right);
    }

    void IDraftPage.RefreshPropertyPanel(IClipElementUI clip) => RefreshPropertyPanel(RequireConcreteClip(clip, nameof(clip)));

    void IDraftPage.RegisterClip(IClipElementUI element, bool resolveOverlap) => RegisterClip(RequireConcreteClip(element, nameof(element)), resolveOverlap);

    Task IDraftPage.ShowAPopup(View? content, Border? border, IClipElementUI? clip, string mode)
    {
        var concrete = clip is null ? null : RequireConcreteClip(clip, nameof(clip));
        return ShowAPopup(content, border, concrete, mode);
    }
    #endregion

    #region subclasses
    class RoundRectangleRadiusType
    {
        public double tl { get; set; }
        public double tr { get; set; }
        public double bl { get; set; }
        public double br { get; set; }
    }

    private sealed class SaveSlotMeta
    {
        public int SlotIndex { get; init; }
        public DateTime SavedAtUtc { get; init; }
    }

    private sealed class TimelineClipboardItem
    {
        public required ClipDraftDTO Dto { get; init; }
        public required double StartPx { get; init; }
        public required double WidthPx { get; init; }
        public required int TrackIndex { get; init; }
    }

    private sealed class MainMultiWindowWindowState
    {
        public required string WindowKey { get; init; }
        public bool IsOpen { get; init; }
        public bool IsVisible { get; init; }
        public bool IsMaximized { get; init; }
        public bool IsMinimized { get; init; }
        public double TranslationX { get; init; }
        public double TranslationY { get; init; }
        public double WidthRequest { get; init; }
        public double HeightRequest { get; init; }
        public int Column { get; init; }
        public int Row { get; init; }
        public int ColumnSpan { get; init; }
        public int RowSpan { get; init; }
        public int ZIndex { get; init; }
    }

    private sealed class MainMultiWindowStateEnvelope
    {
        public List<MainMultiWindowWindowState> Windows { get; init; } = [];
        public string? ActiveWindowKey { get; set; }
    }
    #endregion

}
