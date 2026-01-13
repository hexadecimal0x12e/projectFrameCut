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
using projectFrameCut.PropertyPanel;
using projectFrameCut.Setting.SettingManager;
using projectFrameCut.LivePreview;
using CommunityToolkit.Maui.Views;
using projectFrameCut.Services;
using projectFrameCut.Render.EncodeAndDecode;
using projectFrameCut.Asset;
using projectFrameCut.ViewModels;
using projectFrameCut.Render.Rendering;
using PictureExtensions = projectFrameCut.Shared.PictureExtensions;





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
using CommunityToolkit.Maui.Alerts;
using CommunityToolkit.Maui.Core;
using Android.Content.Res;
using static System.Net.Mime.MediaTypeNames;

#endif

namespace projectFrameCut;

public partial class DraftPage : ContentPage
{
    #region const
    const int ClipHeight = 62;
    const double MinClipWidth = 30.0;
    public const int SubTrackOffset = 10000;

    public readonly string[] DirectoriesNeeded =
    [
        "saveSlots",
        "thumbs",
        "assets",
        "export",
        "proxy"
    ];

    static readonly JsonSerializerOptions savingOpts = new() { WriteIndented = true, NumberHandling = JsonNumberHandling.AllowNamedFloatingPointLiterals };

    public static JsonSerializerOptions DraftJSONOption => savingOpts;
    #endregion

    #region members

    ConcurrentDictionary<string, double> HandleStartWidth = new();

    ClipElementUI? _selected = null;
    Brush? _selectedOrigColor = null;
    private double _currentFrame = 0;

    TapGestureRecognizer nopGesture = new(), rulerTapGesture = new();
    DropGestureRecognizer fileDropGesture = new();

    int trackCount = 0;
    double _startPreviewHeight = 0;
    double tracksZoomOffest = 1d;

    string popupShowingDirection = "none";
    Border Popup = new();

    public InteractableEditor.InteractableEditor ClipEditor;

    private Size WindowSize = new(500, 500);

    private const double SnapGridPixels = 10.0;
    private const double SnapThresholdPixels = 8.0;
    private bool SnapEnabled = true;


    RoundRectangleRadiusType[] RoundRectangleRadius = [];

    PanDeNoise Xdenoiser = new(), Ydenoiser = new();

    private double _playbackStartFrame = 0;
    private string? _nextPlaybackPath = null, _lastPlaybackPath = null;
    private bool _isPreRendering = false;
    private bool _isLivePreviewPlayerEventsHooked = false;

    Lock saveLocker = new();

    bool AlreadyDisappeared = false;

    ConcurrentDictionary<string, DraftTasks> RunningTasks = new();

    ClipInfoBuilder infoBuilder;
    LivePreviewer previewer = new();

    DateTime lastSyncTime = DateTime.MinValue;

    #endregion

    #region public members 
    public ProjectJSONStructure ProjectInfo { get; set; } = new();
    public ConcurrentDictionary<string, ClipElementUI> Clips = new();
    public ConcurrentDictionary<int, AbsoluteLayout> Tracks = new();
    public ConcurrentDictionary<string, AssetItem> Assets = new();
    public string WorkingPath { get; set; } = "";
    public event EventHandler<ClipUpdateEventArgs>? OnClipChanged;
    public double SecondsPerFrame { get; set; } = 1 / 30d;
    public double FramePerPixel { get; set; } = 1d;
    public uint ProjectDuration { get; set; } = 0;

    public ICommand AddCommand { get; private set; }
    public ICommand ExportCommand { get; private set; }
    public ICommand GoRenderCommand { get; private set; }
    public ICommand SettingsCommand { get; private set; }
    public ICommand UndoCommand { get; private set; }
    public ICommand RedoCommand { get; private set; }
    public ICommand SpiltCommand { get; private set; }
    public ICommand DeleteCommand { get; private set; }
    public ICommand SaveCommand { get; private set; }
    public ICommand GotoCommand { get; private set; }
    public ICommand ManageJobsCommand { get; private set; }
    public ICommand ClosePopupCommand { get; private set; }
    public ICommand PlayPauseCommand { get; private set; }
    public ICommand CleanRenderCacheCommand { get; private set; }
    public ICommand PlayheadMoveRightCommand { get; private set; }
    public ICommand PlayheadMoveLeftCommand { get; private set; }
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


    #endregion

    #region init
    public DraftPage()
    {
        BindingContext = this;
        AddCommand = new Command(() => AddClip_Clicked(this, EventArgs.Empty));
        ExportCommand = new Command(() => OnExportedClick(this, EventArgs.Empty));
        GoRenderCommand = new Command(() => OnExportedClick(this, EventArgs.Empty));
        SettingsCommand = new Command(() => SettingsClick(this, EventArgs.Empty));
        UndoCommand = new Command(() => UndoChanges());
        RedoCommand = new Command(() => RedoChanges());
        SpiltCommand = new Command(() => Split_Clicked(this, EventArgs.Empty));
        DeleteCommand = new Command(() => DeleteAClip());
        SaveCommand = new Command(() => OnRefreshButtonClicked(this, EventArgs.Empty));
        GotoCommand = new Command(async () => await GotoButtonClicked());
        ManageJobsCommand = new Command(async () => await OnManageJobsClicked());
        ClosePopupCommand = new Command(async () => await HidePopup());
        PlayPauseCommand = new Command(async () => PlayPauseButton_Clicked(this, EventArgs.Empty));
        CleanRenderCacheCommand = new Command(async () => await CleanRenderCache());
        PlayheadMoveLeftCommand = new Command(async () => await MovePlayhead(-10));
        PlayheadMoveRightCommand = new Command(async () => await MovePlayhead(10));
        InitializeComponent();
        ClipEditor = new InteractableEditor.InteractableEditor { IsVisible = false, HeightRequest = 240, HorizontalOptions = LayoutOptions.Fill };
        ClipEditorHost.Content = ClipEditor;
        ClipEditor.Init(OnClipEditorUpdate, 1920, 1080);
        OverlayLayer.IsVisible = false;
#if ANDROID
        OverlayLayer.InputTransparent = false;
#endif
        PreviewOverlayLayer.InputTransparent = false;
        ClipEditorHost.InputTransparent = false;

        TrackCalculator.HeightPerTrack = ClipHeight;
        infoBuilder = new ClipInfoBuilder(this);

    }

    public DraftPage(ProjectJSONStructure info, ConcurrentDictionary<string, ClipElementUI> clips, ConcurrentDictionary<string, AssetItem> assets, int initialTrackCount, string workingDir, string title = "Untitled draft", bool isReadonly = false)
    {
        Environment.CurrentDirectory = workingDir;

        BindingContext = this;
        AddCommand = new Command(() => AddClip_Clicked(this, EventArgs.Empty));
        ExportCommand = new Command(() => OnExportedClick(this, EventArgs.Empty));
        GoRenderCommand = new Command(() => OnExportedClick(this, EventArgs.Empty));
        SettingsCommand = new Command(() => SettingsClick(this, EventArgs.Empty));
        UndoCommand = new Command(() => UndoChanges());
        RedoCommand = new Command(() => RedoChanges());
        SpiltCommand = new Command(() => Split_Clicked(this, EventArgs.Empty));
        DeleteCommand = new Command(() => DeleteAClip());
        SaveCommand = new Command(() => OnRefreshButtonClicked(this, EventArgs.Empty));
        GotoCommand = new Command(async () => await GotoButtonClicked());
        ManageJobsCommand = new Command(async () => await OnManageJobsClicked());
        ClosePopupCommand = new Command(async () => await HidePopup());
        PlayPauseCommand = new Command(async () => PlayPauseButton_Clicked(this, EventArgs.Empty));
        CleanRenderCacheCommand = new Command(async () => await CleanRenderCache());
        PlayheadMoveLeftCommand = new Command(async () => await MovePlayhead(-10));
        PlayheadMoveRightCommand = new Command(async () => await MovePlayhead(10));
        InitializeComponent();
        ClipEditor = new InteractableEditor.InteractableEditor { IsVisible = false, HeightRequest = 240, HorizontalOptions = LayoutOptions.Fill };
        ClipEditorHost.Content = ClipEditor;
        ClipEditor.Init(OnClipEditorUpdate, 1920, 1080);
        OverlayLayer.IsVisible = false;
#if ANDROID
        OverlayLayer.InputTransparent = false;
#endif

        infoBuilder = new ClipInfoBuilder(this);
        WorkingPath = workingDir;
        TrackCalculator.HeightPerTrack = ClipHeight;

        SetStateBusy();
        Clips = clips;
        Assets = assets;
        Tracks = new ConcurrentDictionary<int, AbsoluteLayout>();

        foreach (var kv in Clips.OrderBy(kv => kv.Value.origTrack ?? 0).ThenBy(kv => kv.Value.origX))
        {
            var item = kv.Value;
            int t = item.origTrack ?? 0;
            if (!Tracks.ContainsKey(t)) AddATrack(t);
            AddAClip(item);
            RegisterClip(item, true);
        }

        trackCount = initialTrackCount;
        ProjectName = isReadonly ? Localized.DraftPage_IsInMode_Readonly(title) : title;
        ProjectInfo.projectName = title;
        ProjectNameMenuBarItem.Text = ProjectInfo.projectName ?? "Unknown project";
        SecondsPerFrame = 1d / ProjectInfo.targetFrameRate;
        IsReadonly = isReadonly;

    }



    private bool Inited = false;

    public async Task PostInit()
    {
        if (Inited) return;
        Inited = true;

        previewWidth = ProjectInfo.RelativeWidth;
        previewHeight = ProjectInfo.RelativeHeight;
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

        previewer.TempPath = Path.Combine(WorkingPath, "thumbs");
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
            var resString = $"{ProjectInfo.RelativeWidth}x{ProjectInfo.RelativeHeight}";
            if (ResolutionPicker.ItemsSource is List<string> list && list.Contains(resString))
            {
                ResolutionPicker.SelectedItem = resString;
            }

            ProjectNameMenuBarItem.Text = ProjectInfo.projectName ?? "Unknown project";
            rulerTapGesture.Tapped += PlayheadTapped;
            OnClipChanged += DraftChanged;
            if (!Directory.Exists(WorkingPath)) Title = Localized.DraftPage_IsInMode_Special(Title);
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
        if (UpperContent.Children[0] is Grid previewGrid && PreviewAreaHeight > 100)
        {
            if (AutoSavePreviewAreaHeight)
            {
                var heightString = SettingsManager.GetSetting("Edit_UpperContentHeight", "250");
                var height = double.TryParse(heightString, out var h1) ? h1 : 250;
                previewGrid.HeightRequest = height;
                ClipEditor?.HeightRequest = height - 10;
            }
            else
            {
                previewGrid.HeightRequest = PreviewAreaHeight;
                ClipEditor?.HeightRequest = PreviewAreaHeight - 10;
            }
        }

        PlayheadLine.TranslationX = TrackHeadLayout.Width;
        if (AlwaysShowToolbarBtns || !OperatingSystem.IsWindows()) AddToolbarBtns();
        if (Width < Height) RightMenuBar.IsVisible = false;

        //PlayheadLine.TranslationY = UpperContent.Height - RulerLayout.Height;
        RulerLayout.GestureRecognizers.Add(rulerTapGesture);
        // Added by Copilot to support resizing UpperContent
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
                "1280x720",
                "1920x1080",
                "2560x1440",
                "3840x2160",
                "7680x4320",
                Localized.DraftPage_PrevResultion_Custom
                };

        ResolutionPicker.SelectedIndex = 0;

        DraftChanged(sender, new());
        SetStateOK();
        SetStatusText(Localized.DraftPage_EverythingFine);
        MyLoggerExtensions.OnExceptionLog += MyLoggerExtensions_OnExceptionLog;

        var w = this.Window?.Width ?? 0;
        var h = this.Window?.Height ?? 0;
        if (w > 0 && h > 0)
        {
            WindowSize = new Size(w, h);
        }

        var safeZoneRad = UISafeZoneServices.GetSafeZone();
        StatusBarGrid.Margin = new Thickness(safeZoneRad, StatusBarGrid.Margin.Top, safeZoneRad, StatusBarGrid.Margin.Bottom);

        if (UseCompactLayout ?? (DeviceInfo.Idiom == DeviceIdiom.Phone))
        {
            RightMenuBar.IsVisible = false;
            RightContentBorder.IsVisible = false;
            SpiltButton.IsVisible = false;
            PlayingControlLayout.HorizontalOptions = LayoutOptions.End;
            AddClip.Text = "+";
            RightContentColDefinition.Width = new GridLength(0, GridUnitType.Absolute);
            MainControlGrid.ColumnDefinitions = new ColumnDefinitionCollection
            {
                new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) },
                new ColumnDefinition { Width = new GridLength(1, GridUnitType.Auto) }
            };
            Grid.SetColumn(LeftMenuBar, 0);
            Grid.SetColumn(PlayingControlLayout, 1);
            PlayingControlLayout.Margin = new(0, 0, 8, 0);
        }
        else
        {
            RightMenuBar.IsVisible = true;
            RightContentBorder.IsVisible = true;

        }
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
        fileDropGesture.AllowDrop = true;
        fileDropGesture.DragOver += File_DragOver;
        fileDropGesture.Drop += File_Drop;
        if (!Tracks.Any()) AddATrack(0);
        UpdatePlayheadHeight();
    }

    #endregion

    #region add stuff
    private ClipElementUI CreateAndAddClip(
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
            element.sourcePath = sourceElement.sourcePath;
            element.maxFrameCount = sourceElement.maxFrameCount;
            element.isInfiniteLength = sourceElement.isInfiniteLength;
            element.ExtraData = sourceElement.ExtraData;

        }
        element.ApplySpeedRatio();
        RegisterClip(element, resolveOverlap);
        AddAClip(element);

        return element;
    }

    private ClipElementUI CreateFromAsset(AssetItem asset, int trackIndex, string fromPlugin = InternalPluginBase.InternalPluginBaseID, string? path = null)
    {
        var elem = ClipElementUI.CreateClip(
                           startX: 0,
                           width: FrameToPixel(asset.isInfiniteLength ? 300 : AssetDatabase.DetermineLengthInFrame(asset, ProjectInfo.targetFrameRate)),
                           trackIndex: trackIndex,
                           labelText: asset.Name,
                           background: ClipElementUI.DetermineAssetColor(asset.AssetType, asset.GetClipMode()),
                           maxFrames: AssetDatabase.DetermineLengthInFrame(asset, ProjectInfo.targetFrameRate),
                           relativeStart: 0
                          );

        elem.sourcePath = path ?? $"${asset.AssetId}";
        elem.ClipType = asset.GetClipMode();
        elem.FromPlugin = fromPlugin;
        elem.sourceSecondPerFrame = asset.SecondPerFrame;
        elem.SecondPerFrameRatio = 1f;
        elem.ExtraData = new();
        return elem;
    }

    private void RegisterClip(ClipElementUI element, bool resolveOverlap)
    {
        var cid = element.Id;

        var clipPanGesture = new PanGestureRecognizer();
        clipPanGesture.PanUpdated += (s, e) => ClipPaned(element.Clip, e);

        var rightHandleGesture = new PanGestureRecognizer();
        rightHandleGesture.PanUpdated += (s, e) => RightHandlePaned(element.RightHandle, e);

        var leftHandleGesture = new PanGestureRecognizer();
        leftHandleGesture.PanUpdated += (s, e) => LeftHandlePanded(element.LeftHandle, e);

        var selectTapGesture = new TapGestureRecognizer();
        selectTapGesture.Buttons = ButtonsMask.Primary;
        selectTapGesture.Tapped += SelectTapGesture_Tapped;

        var contextSelectTapGesture = new TapGestureRecognizer();
        contextSelectTapGesture.Buttons = ButtonsMask.Secondary;
        contextSelectTapGesture.Tapped += ContextSelectTapGesture_Tapped;

        var doubleTapGesture = new TapGestureRecognizer();
        doubleTapGesture.NumberOfTapsRequired = 2;
        doubleTapGesture.Tapped += DoubleTapGesture_Tapped;

        element.Clip.GestureRecognizers.Add(clipPanGesture);
        element.Clip.GestureRecognizers.Add(selectTapGesture);
        element.Clip.GestureRecognizers.Add(contextSelectTapGesture);
        element.Clip.GestureRecognizers.Add(doubleTapGesture);
        element.LeftHandle.GestureRecognizers.Add(leftHandleGesture);
        element.RightHandle.GestureRecognizers.Add(rightHandleGesture);

        // compute X
        if (resolveOverlap)
        {
            double snapped = SnapPixels(element.origX);
            element.Clip.TranslationX = ResolveOverlapStartPixels(element.origTrack ?? 0, cid, snapped, element.origLength);
        }

        Clips.AddOrUpdate(element.Id, element, (_, _) => element);
    }

    private void AddAClip(ClipElementUI c)
    {
        if (c.origTrack is null)
            throw new ArgumentNullException(nameof(c.origTrack));
        if (!Tracks.ContainsKey(c.origTrack ?? 0))
            throw new ArgumentOutOfRangeException(nameof(c.origTrack));

        Tracks[c.origTrack ?? 0].Children.Add(c.Clip);
        UpdateAdjacencyForTrack();
        UpdateTimelineWidth();
    }

    private void Split_Clicked(object sender, EventArgs e)
    {
        var clip = _selected;
        UnSelectTapGesture_Tapped(sender, null);
        if (clip is null) { SetStatusText("No clip selected"); return; }
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
                labelText: $"{clip.displayName} (2)",
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




    private void AddTrackButton_Clicked(object sender, EventArgs e)
    {
        int newId = Tracks.Keys.Where(k => k < SubTrackOffset).DefaultIfEmpty(-1).Max() + 1;
        AddATrack(newId);

        OnClipChanged?.Invoke(this, new ClipUpdateEventArgs { Reason = ClipUpdateReason.TrackAdd, SourceId = newId.ToString() });
    }

    public void AddATrack(int trackId)
    {
        if (trackId >= SubTrackOffset)
        {
            AddASubTrack(trackId);
            return;
        }

        ImageButton removeBtn = new ImageButton
        {
            Source = ImageHelper.LoadFromAsset("icon_remove"),
            WidthRequest = 16,
            HeightRequest = 16

        };

        ImageButton optsBtn = new ImageButton
        {
            Source = ImageHelper.LoadFromAsset("icon_option"),
            WidthRequest = 16,
            HeightRequest = 16
        };

        Label label = new Label
        {
            Text = Localized.DraftPage_Track(trackId),
            HorizontalOptions = LayoutOptions.Start,
            VerticalOptions = LayoutOptions.Center
        };

        var grid = new Grid
        {
            ColumnDefinitions = new ColumnDefinitionCollection
            {
                new ColumnDefinition { Width = new GridLength(2, GridUnitType.Star) },
                new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) },
                new ColumnDefinition { Width = new GridLength(4, GridUnitType.Absolute) },
                new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) }
            },
            RowDefinitions = new RowDefinitionCollection
            {
                new RowDefinition { Height = GridLength.Auto }
            },
            Padding = 4
        };
        grid.Children.Add(label);
        grid.Children.Add(optsBtn);
        grid.Children.Add(removeBtn);
        Grid.SetColumn(label, 0);
        Grid.SetColumn(optsBtn, 1);
        Grid.SetColumn(removeBtn, 3);

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

        track.GestureRecognizers.Add(UnselectTapGesture);
        head.GestureRecognizers.Add(UnselectTapGesture);

        Tracks.AddOrUpdate(trackId, track, (int _, AbsoluteLayout _) => track);


        int currentTrack = trackId;



        removeBtn.Clicked += (s, e) =>
        {
            while (Tracks[currentTrack].Children.FirstOrDefault((IView)null) != null)
            {
                Tracks[currentTrack].Children.Remove(Tracks[currentTrack].Children.First());
            }
            Dictionary<string, ClipElementUI> dictionary = Clips.Where(c => Tracks[currentTrack].Children.Contains(c.Value.Clip)).ToDictionary();
            while (dictionary.Count != 0)
            {
                string key = dictionary.First().Key;
                Clips.Remove(key, out var _);
                dictionary.Remove(key);
                if (dictionary.Count <= 0)
                {
                    break;
                }
            }
            TrackHeadLayout.Children.Remove(head);
            TrackContentLayout.Children.Remove(content);
            Tracks.TryRemove(currentTrack, out _);
            UpdatePlayheadHeight();
        };

        optsBtn.Clicked += (s, e) =>
        {
            //todo
        };




        TrackHeadLayout.Children.Insert(TrackHeadLayout.Count, head);
        TrackContentLayout.Children.Add(content);
        trackCount++;
        UpdatePlayheadHeight();
    }

    public void AddASubTrack(int trackId)
    {
        if (Tracks.ContainsKey(trackId)) return;

        ImageButton removeBtn = new ImageButton
        {
            Source = ImageHelper.LoadFromAsset("icon_remove"),
            WidthRequest = 16,
            HeightRequest = 16

        };

        ImageButton optsBtn = new ImageButton
        {
            Source = ImageHelper.LoadFromAsset("icon_option"),
            WidthRequest = 16,
            HeightRequest = 16
        };

        Label label = new Label
        {
            Text = Localized.DraftPage_Track_Sub(trackId - SubTrackOffset),
            HorizontalOptions = LayoutOptions.Start,
            VerticalOptions = LayoutOptions.Center
        };

        var grid = new Grid
        {
            ColumnDefinitions = new ColumnDefinitionCollection
            {
                new ColumnDefinition { Width = new GridLength(2, GridUnitType.Star) },
                new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) },
                new ColumnDefinition { Width = new GridLength(4, GridUnitType.Absolute) },
                new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) }
            },
            RowDefinitions = new RowDefinitionCollection
            {
                new RowDefinition { Height = GridLength.Auto }
            },
            Padding = 4
        };
        grid.Children.Add(label);
        grid.Children.Add(optsBtn);
        grid.Children.Add(removeBtn);
        Grid.SetColumn(label, 0);
        Grid.SetColumn(optsBtn, 1);
        Grid.SetColumn(removeBtn, 3);

        Border head = new Border
        {
            Content = grid,
            HeightRequest = 60.0,
            Margin = new Thickness(0.0, 0.0, 0.0, 2.0)
        };

        AbsoluteLayout track = new AbsoluteLayout();

        var UnselectTapGesture = new TapGestureRecognizer();
        UnselectTapGesture.Tapped += UnSelectTapGesture_Tapped;

        track.GestureRecognizers.Add(UnselectTapGesture);
        head.GestureRecognizers.Add(UnselectTapGesture);

        int currentTrack = trackId;

        Tracks.AddOrUpdate(trackId, track, (int _, AbsoluteLayout _) => track);

        Border content = new Border
        {
            Content = track,
            HeightRequest = 60.0,
            Margin = new Thickness(0.0, 0.0, 0.0, 2.0),
            BindingContext = trackId
        };

        removeBtn.Clicked += (s, e) =>
        {
            if (!Tracks.ContainsKey(currentTrack)) return;

            while (Tracks[currentTrack].Children.FirstOrDefault((IView)null) != null)
            {
                Tracks[currentTrack].Children.Remove(Tracks[currentTrack].Children.First());
            }
            Dictionary<string, ClipElementUI> dictionary = Clips.Where(c => Tracks[currentTrack].Children.Contains(c.Value.Clip)).ToDictionary();
            while (dictionary.Count != 0)
            {
                string key = dictionary.First().Key;
                Clips.Remove(key, out var _);
                dictionary.Remove(key);
                if (dictionary.Count <= 0)
                {
                    break;
                }
            }
            SubTrackHeadLayout.Children.Remove(head);
            SubTrackContentLayout.Children.Remove(content);
            Tracks.TryRemove(currentTrack, out _);
            UpdatePlayheadHeight();
        };

        optsBtn.Clicked += (s, e) =>
        {
            //todo
        };

        SubTrackHeadLayout.Children.Add(head);
        SubTrackContentLayout.Children.Add(content);
        UpdatePlayheadHeight();
    }

    private async void AddClip_Clicked(object sender, EventArgs e)
    {
        int nativeTrackIndex = Tracks.Last().Key;
        PropertyPanelBuilder ppb = new();
        ppb
        .AddCustomChild(BuildAssetPanel(false))
        .AddSeparator()
        .AddButton("textClip", "Create a text clip")
        .AddButton("solidColorClip", "Create a solid color clip")
        .AddButton("subTitleClip", "Create some subtitle")
        .AddButton("alternativeSourceClip", "Add from another source")
        .AddSeparator(s =>
        {
            s.HeightRequest = 350;
            s.BackgroundColor = Colors.Transparent;
            s.Color = Colors.Transparent;
        })
        .ListenToChanges(async (e) =>
        {
            switch (e.Id)
            {
                case "textClip":
                    {
                        await AddATextClip();
                        break;
                    }
                case "solidColorClip":
                    {
                        await AddASolidColorClip();
                        break;
                    }
                case "subTitleClip":
                    {
                        await AddATextClip(true);
                        break;
                    }
                case "alternativeSourceClip":
                    {
                        await AddAlternativeSourceClip();
                        break;
                    }
            }
            await HidePopup();
        });

        await ShowAPopup(new ScrollView { Content = ppb.Build() });
    }

    private async Task AddAlternativeSourceClip()
    {
        var path = await DisplayPromptAsync("Add", "Input source path", placeholder: "#<provider>,<stream is>");
        if (string.IsNullOrWhiteSpace(path)) return;
        var trackIndex = Tracks.Keys.Where(k => k < SubTrackOffset).DefaultIfEmpty(0).Max();
        if (!Tracks.ContainsKey(trackIndex))
        {
            AddATrack(trackIndex);
        }

        var vidSrc = PluginManager.CreateVideoSource(path);

        var element = CreateAndAddClip(
            startX: 0,
            width: FrameToPixel((uint)vidSrc.TotalFrames),
            trackIndex: trackIndex,
            id: null,
            labelText: "Alternative source 1",
            background: new SolidColorBrush(Colors.MediumPurple),
            resolveOverlap: true,
            relativeStart: 0,
            maxFrames: 0
        );

        element.ClipType = ClipMode.VideoClip;
        element.sourcePath = path;
        element.FromPlugin = "projectFrameCut.Render.Plugins.InternalPluginBase";
        element.isInfiniteLength = false;
        element.maxFrameCount = (uint)vidSrc.TotalFrames;
        element.sourceSecondPerFrame = (float)(1 / vidSrc.Fps);
        element.ExtraData = new();


    }

    private async Task AddATextClip(bool sub = false)
    {
        var text = await DisplayPromptAsync("Add Text", "Enter the text to add:");
        if (!string.IsNullOrWhiteSpace(text))
        {
            var entry = new TextClip.TextClipEntry(
                text,
                100, 100,
                "HarmonyOS Sans SC",
                72f,
                65535, 65535, 65535,
                1.0f
            );

            var entries = new List<TextClip.TextClipEntry> { entry };
            int trackIndex = 0;
            if (sub)
            {
                trackIndex = Tracks.Keys.Where(k => k >= SubTrackOffset).DefaultIfEmpty(SubTrackOffset).Max();
                if (!Tracks.ContainsKey(trackIndex))
                {
                    AddASubTrack(trackIndex);
                }
            }
            else
            {
                trackIndex = Tracks.Keys.Where(k => k < SubTrackOffset).DefaultIfEmpty(0).Max();
                if (!Tracks.ContainsKey(trackIndex))
                {
                    AddATrack(trackIndex);
                }
            }


            var element = CreateAndAddClip(
                startX: 0,
                width: FrameToPixel(90),
                trackIndex: trackIndex,
                id: null,
                labelText: text,
                background: new SolidColorBrush(Colors.MediumPurple),
                resolveOverlap: true,
                relativeStart: 0,
                maxFrames: 0
            );

            element.ClipType = ClipMode.TextClip;
            element.FromPlugin = "projectFrameCut.Render.Plugins.InternalPluginBase";
            element.isInfiniteLength = true;
            element.maxFrameCount = 0;
            element.ExtraData["TextEntries"] = entries;
            element.ExtraData = new();

            SetStatusText($"Added text clip: {text}");
        }
    }

    public async Task AddASolidColorClip()
    {
        ushort R = 65535, G = 65535, B = 65535, A = 65535;
#if WINDOWS
        var picker = new Microsoft.UI.Xaml.Controls.ColorPicker
        {
            ColorSpectrumShape = Microsoft.UI.Xaml.Controls.ColorSpectrumShape.Ring,
            IsMoreButtonVisible = true,
            IsColorSliderVisible = true,
            IsColorChannelTextInputVisible = true,
            IsHexInputVisible = true,
            IsAlphaEnabled = true,
            IsAlphaSliderVisible = true,
            IsAlphaTextInputVisible = true,
        };
        Microsoft.UI.Xaml.Controls.ContentDialog diag = new Microsoft.UI.Xaml.Controls.ContentDialog
        {
            Title = "Pick a color",
            Content = picker,
            CloseButtonText = Localized._Cancel,
            PrimaryButtonText = Localized._OK,
        };

        var services = Microsoft.Maui.Controls.Application.Current?.Handler?.MauiContext?.Services;
        var dialogueHelper = services?.GetService(typeof(projectFrameCut.Platforms.Windows.IDialogueHelper)) as projectFrameCut.Platforms.Windows.IDialogueHelper;
        if (dialogueHelper != null)
        {
            var r = await dialogueHelper.ShowContentDialogue(diag);
            var color = picker.Color;
            R = (ushort)(color.R * 257);
            G = (ushort)(color.G * 257);
            B = (ushort)(color.B * 257);
            A = (ushort)(color.A * 257);
        }
#endif

        int trackIndex = Tracks.Keys.Where(k => k < SubTrackOffset).DefaultIfEmpty(0).Max();
        if (!Tracks.ContainsKey(trackIndex))
        {
            AddATrack(trackIndex);
        }

        var element = CreateAndAddClip(
            startX: 0,
            width: FrameToPixel(90),
            trackIndex: trackIndex,
            id: null,
            labelText: $"#{R / 257:X2}{G / 257:X2}{B / 257:X2}{A / 257:X2}",
            background: new SolidColorBrush(Colors.MediumPurple),
            resolveOverlap: true,
            relativeStart: 0,
            maxFrames: 0
        );

        element.ClipType = ClipMode.SolidColorClip;
        element.FromPlugin = "projectFrameCut.Render.Plugins.InternalPluginBase";
        element.isInfiniteLength = true;
        element.maxFrameCount = 0;
        element.ExtraData["R"] = R;
        element.ExtraData["G"] = G;
        element.ExtraData["B"] = B;
        element.ExtraData["A"] = A;
        element.isInfiniteLength = true;
    }


    #endregion

    #region select clip
    private async void DoubleTapGesture_Tapped(object? sender, TappedEventArgs e)
    {
        if (sender is not Border border) return;
        if (border.BindingContext is not ClipElementUI clip) return;
        LogDiagnostic($"Clip {clip.Id} double clicked, state:{clip.MovingStatus}");
        await ShowAPopup(clip: clip, border: border);

    }

    private void SelectTapGesture_Tapped(object? sender, TappedEventArgs e)
    {
        if (sender is not Border border) return;
        if (border.BindingContext is not ClipElementUI clip) return;
        if (_selected is not null)
        {
            _selected.Clip.Background = new SolidColorBrush(Colors.CornflowerBlue);
        }
        LogDiagnostic($"Clip {clip.Id} clicked, state:{clip.MovingStatus}");
        if (clip.MovingStatus != ClipMovingStatus.Free) return;
        _selected = clip;
        _selectedOrigColor = clip.Clip.Background;
        clip.Clip.Background = Colors.YellowGreen;
        SetStatusText(Localized.DraftPage_Selected(clip.displayName));
        ClipEditor.SetClip(clip, Assets.TryGetValue(clip.Id, out var asset) ? asset : null);
        SetTimelineScrollEnabled(false);
        CustomContent1.Content = BuildPropertyPanel(clip);
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
            builder.TryShow(border);
        }
    }

    private void UnSelectTapGesture_Tapped(object? sender, TappedEventArgs e)
    {
        if (_selected is null) return;
        _selected.Clip.Background = _selectedOrigColor ?? new SolidColorBrush(Colors.CornflowerBlue);
        _selected = null;
        SetStatusText(Localized.DraftPage_EverythingFine);
        ClipEditor.SetClip(null, null);
        SetTimelineScrollEnabled(true);
        CustomContent1.Content = new Label { Text = Localized.DraftPage_PropertyPanel_SelectToContinue, HorizontalOptions = LayoutOptions.Center, VerticalOptions = LayoutOptions.Center };
    }

    private void SetTimelineScrollEnabled(bool enabled)
    {
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
        OnClipChanged?.Invoke(cid, new ClipUpdateEventArgs
        {
            SourceId = cid,
            Reason = ClipUpdateReason.ClipItselfMove
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
            layoutX = clipElementUI.layoutX,
            layoutY = clipElementUI.layoutY,
            defaultY = clipElementUI.defaultY,
            Clip = ghostBorder
        };

        Clips[cid].ghostLayoutX = clipElementUI.layoutX;
        Clips[cid].ghostLayoutY = clipElementUI.layoutY;
        Clips.AddOrUpdate("ghost_" + cid, ghostElement, (string _, ClipElementUI _) => ghostElement);
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
            Clip = shadowBorder,
            origTrack = 0
        };
        Clips.AddOrUpdate("shadow_" + cid, shadowElement, (string _, ClipElementUI _) => shadowElement);
    }

    private void DeleteAClip(ClipElementUI? clip = null)
    {
        clip ??= _selected;
        if (clip is null) return;
        if (clip.origTrack is null) return;
        if (Tracks.TryGetValue(clip.origTrack ?? 0, out var trackLayout))
        {
            trackLayout.Children.Remove(clip.Clip);
        }
        Clips.TryRemove(clip.Id, out _);
        LogDiagnostic($"clip {clip.Id} deleted.");
        SetStatusText(Localized.DraftPage_Removed);
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
                OnClipChanged?.Invoke(clip.Id, new ClipUpdateEventArgs
                {
                    SourceId = clip.Id,
                    Reason = ClipUpdateReason.ClipResized
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
                OnClipChanged?.Invoke(clip.Id, new ClipUpdateEventArgs
                {
                    SourceId = clip.Id,
                    Reason = ClipUpdateReason.ClipResized
                });
                clip.MovingStatus = ClipMovingStatus.Free;
                LogDiagnostic("clip {clip.Id} resized. x:{border.TranslationX} width:{border.WidthRequest}");
                break;
        }
    }
    #endregion

    #region properties

    private View BuildPropertyPanel(ClipElementUI clip)
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
        return infoBuilder.Build(clip, OnClipPropertiesChanged);

    }

    private async void OnClipPropertiesChanged(object? sender, PropertyPanelPropertyChangedEventArgs e)
    {
        if (_selected is null) return;
        var clip = _selected;

        if (e.Id == "__REFRESH_PANEL__")
        {
            Popup.Content = new ScrollView { Content = BuildPropertyPanel(clip) };
            CustomContent1.Content = BuildPropertyPanel(clip);
            Clips[clip.Id] = clip;
            await ReRenderUI();
            DraftChanged(sender, new());
            return;
        }

        SetStatusText($"{clip.displayName}'s property '{e.Id}' changed from {e.OriginValue} to {e.Value}");
        switch (e.Id)
        {
            case "displayName":
                clip.displayName = e.Value?.ToString() ?? clip.displayName;
                break;
            case "speedRatio":
                {
                    if (e.Value is double ratio || double.TryParse(e.Value as string, out ratio))
                    {
                        if (ratio != 0f)
                            clip.SecondPerFrameRatio = (float)ratio;
                    }

                    break;
                }
            case "rawJsonEditor":
                {
                    try
                    {
                        if (JsonSerializer.Deserialize<ClipElementUI>(e.Value?.ToString() ?? "") is not ClipElementUI updatedClip)
                        {
                            break;
                        }
                        if (updatedClip.Id != clip.Id)
                        {
                            SetStateFail("ClipId mismatch.");
                            break;
                        }
                        Clips[clip.Id] = updatedClip;
                    }
                    catch (Exception ex)
                    {
                        Log(ex, "Deserialize clip from rawJsonEditor", this);
                    }
                    break;
                }
            default:
                {

                    break;
                }
        }


        Clips[clip.Id] = clip;

        SetStatusText(Localized.DraftPage_ClipPropertyUpdated(clip.displayName));

        await ReRenderUI();

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
            Dispatcher.Dispatch(() =>
            {
                Popup.Content = new ScrollView { Content = BuildAssetPanel() };
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
            await ShowAPopup(BuildAssetPanel());

        }
        catch (Exception ex)
        {
            Log(ex, "Show asset panel", this);
            throw;
        }

    }

    private ScrollView BuildAssetPanel(bool includeHeader = true)
    {
        var layout = new VerticalStackLayout { Spacing = 10 };

        layout.Children.Add(new Label
        {
            Text = Localized.DraftPage_AssetPanel_LocalAssets,
            FontSize = 18,
            FontAttributes = FontAttributes.Bold,
            Margin = new Thickness(0, 10, 0, 5)
        });

        foreach (var kvp in Assets)
        {
            var asset = kvp.Value;
            var label = $"{asset.Icon} {asset.Name}";
            var assetClip = ClipElementUI.CreateClip(0, FontHelper.MeasureTextLength(label) + 50, 0, labelText: label, background: ClipElementUI.DetermineAssetColor(asset.Type));
            ToolTipProperties.SetText(assetClip.Clip, asset.Path);
            assetClip.maxFrameCount = (uint)(asset.FrameCount ?? 0U);
            assetClip.isInfiniteLength = asset.isInfiniteLength;
            assetClip.Clip.WidthRequest = 200;
            var addButton = new Button
            {
                Background = Colors.Green,
                Text = "➕"
            };

            var removeButton = new Button
            {
                Background = Colors.Red,
                Text = "🗑️"
            };

            var childLayout = new HorizontalStackLayout
            {
                Children =
                {
                    new Image
                    {
                        Source = File.Exists(asset.ThumbnailPath) ? ImageSource.FromFile(asset.ThumbnailPath) : null,
                        WidthRequest = 120
                    },
                    assetClip.Clip,

                }
            };
            removeButton.Clicked += (s, e) =>
            {
                Assets.Remove(kvp.Key, out var _asset);
                layout.Children.Remove(childLayout);
            };
            addButton.Clicked += async (s, e) =>
            {
                var mode = ClipElementUI.DetermineClipMode(asset.Path);
                int trackIndex = 0;
                if (mode == ClipMode.AudioClip || mode == ClipMode.SubtitleClip)
                {
                    int maxSub = Tracks.Keys.Where(k => k >= SubTrackOffset).DefaultIfEmpty(SubTrackOffset - 1).Max();
                    if (maxSub < SubTrackOffset) maxSub = SubTrackOffset;
                    if (!Tracks.ContainsKey(maxSub)) AddASubTrack(maxSub);
                    trackIndex = maxSub;
                }
                else
                {
                    int maxMain = Tracks.Keys.Where(k => k < SubTrackOffset).DefaultIfEmpty(0).Max();
                    trackIndex = maxMain;
                }
                var path = Path.GetRelativePath(WorkingPath, asset.Path);
                if (path.Contains("..")) path = asset.Path;
                var elem = CreateFromAsset(asset, trackIndex, InternalPluginBase.InternalPluginBaseID, path);
                RegisterClip(elem, true);
                AddAClip(elem);

                await UpdateAdjacencyForTrack();
                SetStatusText(Localized.DraftPage_AssetAdded(asset.Name));
                await HidePopup();

            };
            childLayout.Children.Add(addButton);
            if (includeHeader) childLayout.Children.Add(removeButton);
            layout.Children.Add(childLayout);
        }

        layout.Children.Add(new BoxView
        {
            HeightRequest = 1,
            BackgroundColor = Colors.Gray,
            Margin = new Thickness(0, 15, 0, 5)
        });

        layout.Children.Add(new Label
        {
            Text = Localized.DraftPage_AssetPanel_SharedAssets,
            FontSize = 18,
            FontAttributes = FontAttributes.Bold,
            Margin = new Thickness(0, 5, 0, 5)
        });

        foreach (var kvp in AssetDatabase.Assets.Where(c => c.Value.AssetType is AssetType.Video or AssetType.Audio or AssetType.Image))
        {
            var asset = kvp.Value;
            var label = $"{asset.Icon} {asset.Name}";
            var assetClip = ClipElementUI.CreateClip(0, FontHelper.MeasureTextLength(label) + 50, 0, labelText: label, background: ClipElementUI.DetermineAssetColor(asset.Type));
            ToolTipProperties.SetText(assetClip.Clip, asset.Path);
            assetClip.maxFrameCount = (uint)(asset.FrameCount ?? 0U);
            assetClip.isInfiniteLength = asset.isInfiniteLength;
            assetClip.Clip.WidthRequest = 200;

            var addToTrackButton = new Button
            {
                Background = Colors.Green,
                Text = "+"
            };


            var childLayout = new HorizontalStackLayout
            {
                Children =
                {
                    new Image
                    {
                        Source = File.Exists(asset.ThumbnailPath) ? ImageSource.FromFile(asset.ThumbnailPath) : null,
                        WidthRequest = 120,
                        HeightRequest = 67.5, //16:9
                        Aspect = Aspect.AspectFit
                    },
                    assetClip.Clip,
                }
            };

            addToTrackButton.Clicked += async (s, e) =>
            {
                var mode = ClipElementUI.DetermineClipMode(asset.Path);
                int trackIndex = 0;
                if (mode == ClipMode.AudioClip || mode == ClipMode.SubtitleClip)
                {
                    int maxSub = Tracks.Keys.Where(k => k >= SubTrackOffset).DefaultIfEmpty(SubTrackOffset - 1).Max();
                    if (maxSub < SubTrackOffset) maxSub = SubTrackOffset;
                    if (!Tracks.ContainsKey(maxSub)) AddASubTrack(maxSub);
                    trackIndex = maxSub;
                }
                else
                {
                    int maxMain = Tracks.Keys.Where(k => k < SubTrackOffset).DefaultIfEmpty(0).Max();
                    trackIndex = maxMain;
                }

                var elem = CreateFromAsset(asset, trackIndex);
                RegisterClip(elem, true);
                AddAClip(elem);

                await UpdateAdjacencyForTrack();
                SetStatusText($"Shared asset '{asset.Name}' added to track.");
                await HidePopup();
            };

            childLayout.Children.Add(addToTrackButton);
            layout.Children.Add(childLayout);
        }

        var addBtn = new Button
        {
            Text = "Add",
            FontSize = 32,

        };

        addBtn.Clicked += async (s, e) =>
        {
            await MainThread.InvokeOnMainThreadAsync(async () =>
            {
                SetStateBusy(Localized.DraftPage_WaitForUser);
                try
                {


                    var result = await FilePicker.PickAsync(new PickOptions
                    {
                        PickerTitle = "Select a asset",
                        FileTypes = new FilePickerFileType(new Dictionary<DevicePlatform, IEnumerable<string>>
                        {
                            { DevicePlatform.WinUI, [".mp4", ".mov", ".mkv", ".avi", ".webm", ".m4v", ".png", ".jpg", ".jpeg", ".webp", ".bmp", ".tiff"] },
                            { DevicePlatform.Android, ["image/*", "video/*"] },
#if iDevices
                            {DevicePlatform.iOS , ["public.image", "public.movie", "public.video", "public.mpeg-4", "com.apple.protected-mpeg-4-video", "com.apple.quicktime-movie", "public.avi", "org.matroska.mkv"]},
                            {DevicePlatform.MacCatalyst , ["public.image", "public.movie", "public.video", "public.mpeg-4", "com.apple.protected-mpeg-4-video", "com.apple.quicktime-movie", "public.avi", "org.matroska.mkv"]}
#endif
                        })
                    });
                    string resultPath = "";
                    if (result is not null)
                    {
                        PropertyPanelBuilder optionPPB = new();
                        optionPPB.AddText(Localized.DraftPage_AssetPanel_Add_SelectMode)
                        .AddIconTitleDescriptionCard("Copy", null, Localized.DraftPage_AssetPanel_AsLocalAssets, Localized.DraftPage_AssetPanel_AsSharedAssets_Desc)
                        .AddIconTitleDescriptionCard("CopyToShared", null, Localized.DraftPage_AssetPanel_AsSharedAssets, Localized.DraftPage_AssetPanel_AsSharedAssets_Desc)
                        .AddIconTitleDescriptionCard("Reference", null, Localized.DraftPage_AssetPanel_Reference, Localized.DraftPage_AssetPanel_Reference_Desc)
                        .AddButton("Cancel", Localized._Cancel);
                        TaskCompletionSource tcs = new();

                        optionPPB.ListenToChanges(async (e) =>
                        {
                            switch (e.Id)
                            {
                                case "Reference":
                                    {
                                        resultPath = result.FullPath;
                                        break;
                                    }
                                case "Copy":
                                    {
                                        resultPath = Path.Combine(WorkingPath, "assets", $"imported-{result.FileName}");
                                        if (!string.IsNullOrWhiteSpace(WorkingPath))
                                        {
#if WINDOWS
                                            File.Copy(result.FullPath, resultPath, true);
#else
                                            File.Move(result.FullPath, resultPath, true);
#endif
                                            await AddAsset(resultPath);

                                        }
                                        break;
                                    }
                                case "CopyToShared":
                                    {
                                        _ = await AssetDatabase.Add(resultPath, this);
                                        break;
                                    }
                                default:
                                    {

                                        break;
                                    }
                            }
                            tcs.SetResult();
                        });
                        await ShowACenteredPopup(600, 400, optionPPB.Build());
                        try
                        {
                            var cts = new CancellationTokenSource();
                            cts.CancelAfter(60 * 1000);
                            await tcs.Task.WaitAsync(cts.Token);
                        }
                        catch (TaskCanceledException)
                        {

                        }
                        await HidePopup();
                        SetStateOK();
                    }
                }
                catch (Exception ex)
                {
                    Log(ex, "Add asset", this);
                }
            });
        };

        if (includeHeader) layout.Add(addBtn);
        var b = new BoxView
        {
            WidthRequest = 50,
            HeightRequest = 250,
            BackgroundColor = Colors.Transparent,
        };
        b.Color = Colors.Transparent;
        layout.Add(b);


        OverlayLayer.GestureRecognizers.Add(fileDropGesture);

        return new ScrollView
        {
            Content = layout,
            Orientation = ScrollOrientation.Horizontal
        };
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

                        // Update children: find label(s) and handle borders, rebind them and update texts
                        void UpdateLayoutChildren(Microsoft.Maui.Controls.Layout layout)
                        {
                            Border foundLeft = null;
                            Border foundRight = null;

                            foreach (var child in layout.Children)
                            {
                                if (child is Microsoft.Maui.Controls.Label lab)
                                {
                                    lab.Text = clip.displayName;
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
                                        if (sub is Microsoft.Maui.Controls.Label sl) sl.Text = clip.displayName;
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

            await UpdateAdjacencyForTrack();
        }
        finally
        {
            SetStateOK();
        }
    }


    class RoundRectangleRadiusType
    {
        public double tl { get; set; }
        public double tr { get; set; }
        public double bl { get; set; }
        public double br { get; set; }
    }

    class TrackClassForUpdateAdjacencyForTrack
    {
        public double Start { get; set; }
        public double End { get; set; }
        public ClipElementUI Clip { get; set; }
    }

    private async Task UpdateAdjacencyForTrack()
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
            .Select(c =>
            {
                double start = Math.Round(c.Clip.X + c.Clip.TranslationX);
                double width = (!double.IsNaN(c.Clip.Width) && c.Clip.Width > 0) ? Math.Round(c.Clip.Width) : Math.Round(c.Clip.WidthRequest);
                double end = start + width;
                return new TrackClassForUpdateAdjacencyForTrack { Start = start, End = end, Clip = c };
            })
            .OrderBy(t => t.Start)
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
            try { item.Clip.Clip.StrokeShape = new RoundRectangle { CornerRadius = new Microsoft.Maui.CornerRadius(defaultRadius) }; } catch { }
        }

        double tol = 3;

        for (int i = 0; i < byorder.Count; i++)
        {
            var self = byorder[i];
            var left = (i > 0) ? byorder[i - 1] : null;
            var right = (i < byorder.Count - 1) ? byorder[i + 1] : null;

            if (i > 0)
            {
                if (Math.Abs(left.End - self.Start) <= tol) //left
                {
                    localRadius[i].tl = 0;
                    localRadius[i].br = 0;
                    localRadius[i - 1].tr = 0;
                    localRadius[i - 1].bl = 0;
                }
            }

            if (i < byorder.Count - 1)
            {
                if (Math.Abs(right.Start - self.End) <= tol)  //right
                {
                    localRadius[i].tr = 0;
                    localRadius[i].bl = 0;
                    localRadius[i + 1].tl = 0;
                    localRadius[i + 1].br = 0;
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
                    item.Clip.Clip.StrokeShape = new RoundRectangle
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
                if (ClipEditor is not null) ClipEditor.HeightRequest = h - 10;
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
#pragma warning disable CS0414 //this stuff only need on iDevices, because of OverlayLayer can't handle any input...... 
    private IView? OrigionalUIContent = null;
#pragma warning restore CS0414
    private async Task ShowAPopup(View? content = null, Border? border = null, ClipElementUI? clip = null, string mode = "")
    {
        content ??= (border != null && clip != null) ? BuildPropertyPanel(clip) : new Label { Text = $"No content to show. This SHOULD is a bug, please feedback.\r\n{Environment.StackTrace.Split(Environment.NewLine).Skip(1).Aggregate((a, b) => $"{a}{Environment.NewLine}{b}")}" };

        if (OperatingSystem.IsMacCatalyst() || OperatingSystem.IsIOS())
        {
            if (OrigionalUIContent is null && MainUpperContent.Children.Count > 0)
            {
                OrigionalUIContent = MainUpperContent.Children.First();
            }
            MainUpperContent.Children.Clear();
            MainUpperContent.Children.Add(new VerticalStackLayout
            {
                Children =
                {
                    content
                },
                HorizontalOptions = LayoutOptions.Center,
                VerticalOptions = LayoutOptions.Center
            });
            OverlayLayer.InputTransparent = true;
            OverlayLayer.IsVisible = false;
            return;
        }


        OverlayLayer.IsVisible = true;

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
                        await ShowAFullscreenPopupInRight(WindowSize.Height * 0.75, content);
                        break;
                    }
                case "bottom":
                    {
                        await ShowAFullscreenPopupInBottom(WindowSize.Height / 1.2, content);
                        break;
                    }
                case "dialog":
                    {
                        await ShowACenteredPopup(WindowSize.Height / 1.5, WindowSize.Width / 2, content);
                        break;
                    }
                case "clip":
                    {
                        if (border is not null && clip is not null)
                            await ShowClipPopup(border, clip);
                        else
                            await ShowAFullscreenPopupInBottom(WindowSize.Height / 1.2, content);
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
        OverlayLayer.IsVisible = true;
        OverlayLayer.InputTransparent = false;

        var existing = OverlayLayer.Children.FirstOrDefault(c => (c as VisualElement)?.StyleId == "ClipPopupFrame" || (c as VisualElement)?.StyleId == "ClipPopupTriangle");
        if (existing != null)
        {
            var toRemove = OverlayLayer.Children.Where(c => (c as VisualElement)?.StyleId == "ClipPopupFrame" || (c as VisualElement)?.StyleId == "ClipPopupTriangle").ToList();
            foreach (var r in toRemove)
                OverlayLayer.Children.Remove(r);
        }

        OverlayLayer.InputTransparent = false;

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
            Content = new ScrollView { Content = BuildPropertyPanel(clip) }
        };

        frame.GestureRecognizers.Add(nopGesture);

        AbsoluteLayout.SetLayoutBounds(frame, new Rect(popupX, popupY, popupWidth, popupHeight));

        frame.Opacity = 0;
        frame.Scale = 0.95;
        frame.TranslationY = 5;

        triangle.Opacity = 0;
        triangle.Scale = 0.95;
        triangle.TranslationY = 5;

        OverlayLayer.Children.Add(frame);
        OverlayLayer.Children.Add(triangle);

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

    private async Task HidePopup()
    {
#if iDevices
        if (OrigionalUIContent is not null)
        {
            MainUpperContent.Children.Clear();
            MainUpperContent.Children.Add(OrigionalUIContent);
            OrigionalUIContent = null;
        }
        else
        {
            if (await DisplayAlertAsync(Localized._Error, Localized.DraftPage_FailToProcess, Localized._Confirm, Localized._Cancel)) await Navigation.PopAsync();
        }
        return;
#endif
        //if (!isPopupShowing) return;
        OverlayLayer.GestureRecognizers?.Remove(fileDropGesture);
        OverlayLayer.InputTransparent = true;
        await Task.WhenAll(HideClipPopup(), HideFullscreenPopup());

        OverlayLayer.IsVisible = false;
        OverlayLayer.InputTransparent = true;

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

    private async Task ShowAFullscreenPopupInBottom(double height, View content)
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
            Content = new ScrollView { Content = content },
            Opacity = 0.95
        };
        OverlayLayer.InputTransparent = false;
        Popup.GestureRecognizers.Add(nopGesture);

        OverlayLayer.Add(Popup);

        var targetY = height;
        try
        {
            await Popup.TranslateTo(Popup.TranslationX, size.Height - targetY, 300, Easing.SinOut);
        }
        catch { }
    }

    private async Task ShowAFullscreenPopupInRight(double width, View content)
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
            Content = new ScrollView { Content = content },
            Opacity = 0.95
        };
        OverlayLayer.InputTransparent = false;
        Popup.GestureRecognizers.Add(nopGesture);

        OverlayLayer.Add(Popup);

        var targetX = width;
        try
        {
            await Popup.TranslateTo(width, Popup.TranslationY, 300, Easing.SinOut);
        }
        catch { }
    }

    private async Task ShowACenteredPopup(double desiredHeight, double desiredWidth, View content)
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
            Content = new ScrollView { Content = content },
            Opacity = 0.0,
            Scale = 0.97
        };

        OverlayLayer.InputTransparent = false;
        Popup.GestureRecognizers.Add(nopGesture);
        OverlayLayer.Add(Popup);

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

        OverlayLayer.Remove(Popup);
        OverlayLayer.InputTransparent = true;

        popupShowingDirection = "none";

    }

    #endregion

    #region math stuff

    [DebuggerNonUserCode()]
    public uint PixelToFrame(double px) => (uint)(px * FramePerPixel * tracksZoomOffest);
    [DebuggerNonUserCode()]
    public double FrameToPixel(uint f) => f / (FramePerPixel * tracksZoomOffest);

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
                    // skip ghost/shadow
                    if (pair.Key.StartsWith("ghost_") || pair.Key.StartsWith("shadow_")) return false;
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
                    if (pair.Key.StartsWith("ghost_") || pair.Key.StartsWith("shadow_")) return false;
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
    #endregion

    #region live preview
    SemaphoreSlim renderingLock = new(1, 1);
    private async Task RenderOneFrame(uint duration, int? width = null, int? height = null)
    {
        await renderingLock.WaitAsync();
        _currentFrame = duration;
        SetStateBusy();
        SetStatusText(Localized.DraftPage_RenderOneFrame((int)duration, TimeSpan.FromSeconds(duration * SecondsPerFrame)));
        try
        {
            var cts = new CancellationTokenSource();
#if !DEBUG
            cts.CancelAfter(10000);
#endif
            string path = "";

            await Task.Run(() =>
            {
                path = previewer.RenderFrame(duration, width ?? previewWidth, height ?? previewHeight);
            });

            await PreviewOverlayImage.ForceLoadPNGToAImage(path);


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
            await DisplayAlertAsync(Localized._Error, Localized.DraftPage_RenderFail(duration, ex), Localized._OK);
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
#if ANDROID
        await Toast.Make(new PlatformNotSupportedException().Message, ToastDuration.Long).Show();
        return;
#else
        isPlaying = !isPlaying;
        if (isPlaying)
        {
            PlayPauseButton.Text = "\u23f8\ufe0f"; //pause
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
                        if (_lastPlaybackPath is not null && File.Exists(_lastPlaybackPath ?? ""))
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
            PlayPauseButton.Text = "\u25b6\ufe0f"; //play
            LogDiagnostic("Pause playing.");
            await PauseLivePreview();
            SetStateOK();
        }
#endif

    }
    MediaElement LivePreviewPlayer = new();


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
            await Dispatcher.DispatchAsync(() =>
            {
                LivePreviewerHost.Content = LivePreviewPlayer;
                LivePreviewPlayer.IsVisible = true;
                LivePreviewPlayer.ShouldShowPlaybackControls = false;
                PreviewOverlayImage.IsVisible = false;
            });

            _playbackStartFrame = _currentFrame;
            _nextPlaybackPath = null;


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
            PlayPauseButton.Text = "\u25b6\ufe0f";
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
                LivePreviewPlayer.Source = null;
            }
            catch { }
            LivePreviewPlayer.IsVisible = false;
            PreviewOverlayImage.IsVisible = true;
        });


        _nextPlaybackPath = null;
        _isPreRendering = false;
        PlayPauseButton.Text = "\u25b6\ufe0f";
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
        previewer.OnProgressChanged += progChanged;
        var path = await previewer.RenderSomeFrames((int)_currentFrame, LiveVideoPreviewBufferLength, (int)(previewWidth / LivePreviewResolutionFactor), (int)(previewHeight / LivePreviewResolutionFactor), (int)ProjectInfo.targetFrameRate, ct);
        previewer.OnProgressChanged -= progChanged;
        return path;
    }
    #endregion

    #region handle changes
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
        if (!e?.NoSave ?? false) await Save();
        UpdatePlayheadHeight();
        var d = DraftImportAndExportHelper.ExportFromDraftPage(this);
        SetStateBusy();
        SetStatusText(Localized.DraftPage_ApplyingChanges);

        try
        {
            previewer.UpdateDraft(d);
            SetStatusText(Localized.DraftPage_ChangesApplied);
            SetStateOK();
        }
        catch (Exception ex)
        {
            SetStateFail(Localized._ExceptionTemplate(ex));
#if DEBUG
            if (await DisplayAlertAsync(Localized._Error, Localized.DraftPage_ApplyChangesFail(ex), "Throw", Localized._OK)) throw;

#else
            await DisplayAlertAsync(Localized._Error, Localized.DraftPage_ApplyChangesFail(ex), Localized._OK);
#endif

        }

    }

    private async void OnClipEditorUpdate()
    {
        if (AlreadyDisappeared) return;

        var d = DraftImportAndExportHelper.ExportFromDraftPage(this);
        previewer.UpdateDraft(d);


        var currentX = PlayheadLine.TranslationX - TrackHeadLayout.Width;
        if (currentX < 0) currentX = 0;
        var duration = PixelToFrame(currentX);
        await RenderOneFrame(duration);
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
    }

    public async Task Save(bool noSlot = false)
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
        var draft = DraftImportAndExportHelper.ExportFromDraftPage(this);
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

        }

        ProjectDuration = draft.Duration;
        ProjectInfo.LastChanged = DateTime.Now;
        saveLocker.Enter();
        try
        {
            await File.WriteAllTextAsync(Path.Combine(WorkingPath, "project.json"), JsonSerializer.Serialize(ProjectInfo, savingOpts), default);
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

    private void RedoChanges()
    {
        var nextSlot = CurrentSaveSlotIndex + 1;
        if (nextSlot < 1 || nextSlot >= MaximumSaveSlot || !Directory.Exists(Path.Combine(WorkingPath, "saveSlots", $"slot_{nextSlot}")))
        {
            SetStateOK(Localized.DraftPage_RedoAndUndo_NoMoreSlots);
            return;
        }
        if (IsSyncCooldown()) return;
        SetSyncCooldown();
        ApplySlot(nextSlot);
    }

    private void UndoChanges()
    {
        var nextSlot = CurrentSaveSlotIndex - 1;
        if (nextSlot >= MaximumSaveSlot || !Directory.Exists(Path.Combine(WorkingPath, "saveSlots", $"slot_{nextSlot}")))
        {
            SetStateOK(Localized.DraftPage_RedoAndUndo_NoMoreSlots);
            return;
        }
        if (IsSyncCooldown()) return;
        SetSyncCooldown();
        ApplySlot(nextSlot);
    }

    private void ApplySlot(int slotIndex)
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
            CurrentSaveSlotIndex = slotIndex;
            DraftChanged(this, new() { NoSave = true});
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
        await HidePopup();
        UnSelectTapGesture_Tapped(sender, null!);
        UpdateTimelineWidth();
        SetTimelineScrollEnabled(true);
        await ReRenderUI();
        DraftChanged(sender, new() { NoSave = true });
        SetStateOK();
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
        Dictionary<string, ICommand?> actionsPair = new Dictionary<string, ICommand?>
        {
            {"DraftPage_GoRender",GoRenderCommand  },
            {"DraftPage_MenuBar_Project_Save", SaveCommand },
            {"DraftPage_MenuBar_Project_Share",null },
            {"DraftPage_MenuBar_Project_Cooperate",null },
            {"DraftPage_MenuBar_Edit_Spilt", SpiltCommand },
            {"DraftPage_MenuBar_Edit_DeleteClip", DeleteCommand },
            {"DraftPage_MenuBar_Edit_Undo", UndoCommand },
            {"DraftPage_MenuBar_Edit_Redo", RedoCommand },
            {"_Settings", SettingsCommand },
            {"DraftPage_MenuBar_Jobs_ManageJobs", ManageJobsCommand },
#if DEBUG
            {
                "Debug_CreatePopup" ,new Command(async () =>
                {
                    string[] type = ["right", "bottom","center","dialog"];
                    var select = await DisplayActionSheetAsync("info", "select", null, type);
                    await ShowAPopup(content: BuildPropertyPanel(_selected), border: _selected?.Clip, clip:_selected, mode: select);
                })
            },
            {
                "Debug_ComposeAudio", new Command(() =>
                {
                    var clip = DraftImportAndExportHelper.JSONToIClips(DraftImportAndExportHelper.ExportFromDraftPage(this,true));
                    var buf = AudioComposer.Compose(clip, null, (int)ProjectInfo.targetFrameRate, 44100, 2);
                    AudioWriter w = new(Path.Combine(MauiProgram.DataPath,$"audioExport-{DateTime.Now:yyyyMMddHHmmss}.wav"));
                    w.Append(buf);
                    w.Finish();
                    w.Dispose();
                })
            },
            {
                "Debug_DumpProcessStack", new Command(async () =>
                {
                    var f = previewer.GetFrame((uint)_currentFrame, previewWidth, previewHeight);
                    var text = PictureExtensions.FormatProcessStackForLog(f.ProcessStack);
                    await Microsoft.Maui.ApplicationModel.DataTransfer.Clipboard.Default.SetTextAsync(text);
                })
            }
#endif
                };

        var localizedActionPair = actionsPair.ToDictionary(kv => Localized.DynamicLookup(kv.Key, kv.Key), kv => kv.Value);

        var option = await DisplayActionSheetAsync(Localized._Info, Localized._Cancel, null, localizedActionPair.Keys.ToArray());

        if (localizedActionPair.TryGetValue(option, out var cmd))
        {
            cmd?.Execute(null);
        }
    }


    private async void OnExportedClick(object sender, EventArgs e)
    {
        await Save(true);
        var draft = DraftImportAndExportHelper.ExportFromDraftPage(this, true);
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
        if (picker != null)
        {
            if (picker.SelectedItem is string picked)
            {
                var parts = picked.Split('x');
                if (parts.Length == 2 &&
                   int.TryParse(parts[0].Trim(), out int w1) &&
                   int.TryParse(parts[1].Trim(), out int h1))
                {
                    SetStatusText(Localized.DraftPage_PrevResultion_Seted(w1, h1));
                    previewWidth = w1;
                    previewHeight = h1;
                    ClipEditor.UpdateVideoResolution(w1, h1);
                    return;
                }
            }
        }

        var widthInput = await DisplayPromptAsync("Output Resolution", Localized.DraftPage_PrevResultion_Custom_InputWidth, initialValue: "1920");
        var heightInput = await DisplayPromptAsync("Output Resolution", Localized.DraftPage_PrevResultion_Custom_InputHeight, initialValue: "1080");
        if (int.TryParse(widthInput, out int w) && int.TryParse(heightInput, out int h))
        {
            SetStatusText(Localized.DraftPage_PrevResultion_Seted(w, h));
            previewWidth = w;
            previewHeight = h;
            ClipEditor.UpdateVideoResolution(w, h);


        }
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

    private void PerformZoom(double delta)
    {
        double oldZoom = tracksZoomOffest;
        double newZoom = tracksZoomOffest * delta;

        // Clamp zoom
        if (newZoom < 0.01) newZoom = 0.01;
        if (newZoom > 100) newZoom = 100;

        if (Math.Abs(newZoom - oldZoom) < 0.0001) return;

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
        CustomContent1.Content = new Label { Text = Localized.DraftPage_PropertyPanel_SelectToContinue, HorizontalOptions = LayoutOptions.Center, VerticalOptions = LayoutOptions.Center };

    }

    protected override async void OnDisappearing()
    {
        AlreadyDisappeared = true;
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
                    Text = Localized.DraftPage_SavingChanges,
                    HorizontalTextAlignment = TextAlignment.Center,
                    Margin = new Thickness(0,10,0,0)
                }
            }
        };
        base.OnDisappearing();

        await HidePopup();
        if (this.Window is not null)
        {
            this.Window.SizeChanged -= Window_SizeChanged;
        }
        MyLoggerExtensions.OnExceptionLog -= MyLoggerExtensions_OnExceptionLog;


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
            await Save(true);
        }
        catch (Exception ex)
        {
            await DisplayAlertAsync("Error", $"Failed to save project on exit: {ex.Message}", "OK");
        }

    }

    public async void Window_SizeChanged(object? sender, EventArgs e)
    {
        double w = this.Window?.Width ?? 0;
        double h = this.Window?.Height ?? 0;
        WindowSize = new(w, h);
        LogDiagnostic($"Window size changed: {w:F0} x {h:F0} (DIP)");
        UpdateTimelineWidth();
        UpdatePlayheadPosition();
        if (IsSyncCooldown()) return;
        SetSyncCooldown();
        if (popupShowingDirection != "none")
        {
            await Dispatcher.DispatchAsync(async () =>
            {
                try
                {
                    await HidePopup();
                    await ShowAPopup(Popup.Content);
                }
                catch { }
            });
        }

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
        });
        if (LogUIMessageToLogger) Log(text, "UI msg");
    }
    #endregion

}
