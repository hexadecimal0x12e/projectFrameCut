using Microsoft.Maui.Controls.Shapes;
using Microsoft.Maui.Devices;
using Microsoft.Maui.Storage;
using projectFrameCut.DraftStuff;
using projectFrameCut.PropertyPanel;
using projectFrameCut.Render;
using projectFrameCut.Shared;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading.Tasks;
using projectFrameCut.Setting.SettingManager;
using projectFrameCut.LivePreview;
using System.Windows.Input;

using Path = System.IO.Path;
using Grid = Microsoft.Maui.Controls.Grid;
using Image = Microsoft.Maui.Controls.Image;
using Application = Microsoft.Maui.Controls.Application;
using Microsoft.Maui.Controls;
using InteractableEditor;
using Microsoft.Maui.Layouts;
using projectFrameCut.Render.ClipsAndTracks;
using projectFrameCut.Render.RenderAPIBase.Project;
using projectFrameCut.Render.ClipsAndTracks;
using projectFrameCut.Render.RenderAPIBase.Project;
using projectFrameCut.Render.Videos;
using projectFrameCut.Render.Plugin;




#if WINDOWS
using Windows.ApplicationModel.DataTransfer;
using Windows.Storage;
using projectFrameCut.Platforms.Windows;
using Microsoft.UI.Xaml.Media.Imaging;
using ILGPU;
using ILGPU.Runtime;
using projectFrameCut.Render.WindowsRender;

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


#endif

namespace projectFrameCut;

public partial class DraftPage : ContentPage
{
    #region const
    const int ClipHeight = 62;
    const double MinClipWidth = 30.0;

    public readonly string[] DirectoriesNeeded =
    [
        "saveSlots",
        "thumbs",
        "assets",
        "export",
        "temp"
    ];

    readonly JsonSerializerOptions savingOpts = new() { WriteIndented = true, NumberHandling = JsonNumberHandling.AllowNamedFloatingPointLiterals };
    #endregion

    #region members
    public ProjectJSONStructure ProjectInfo { get; set; } = new();

    ConcurrentDictionary<string, double> HandleStartWidth = new();

    ClipElementUI? _selected = null;
    private double _currentFrame = 0;

    TapGestureRecognizer nopGesture = new(), rulerTapGesture = new();
    DropGestureRecognizer fileDropGesture = new();

    int trackCount = 0;
    double tracksViewOffset = 0;
    double tracksZoomOffest = 1d;

    string popupShowingDirection = "none";
    Border Popup = new();

    public InteractableEditor.InteractableEditor ClipEditor;

    private Size WindowSize = new(500, 500);

    private const double SnapGridPixels = 10.0;
    private const double SnapThresholdPixels = 8.0;
    private bool SnapEnabled = true;

    private AssetItem? _draggingAsset = null;
    private Point? _lastDragPoint = null;

    _RoundRectangleRadiusType[] RoundRectangleRadius = [];

    PanDeNoise Xdenoiser = new(), Ydenoiser = new();

    Lock saveLocker = new();

#if WINDOWS
    Process backendProc;
    RpcClient _rpc;
    private volatile bool _isClosing = false;
    string backendAccessToken;
    int backendPort = -1;
#endif

    ClipInfoBuilder infoBuilder;
    LivePreviewer previewer = new();

    DateTime lastSyncTime = DateTime.MinValue;

    #endregion

    #region public members 
    public ConcurrentDictionary<string, ClipElementUI> Clips = new();
    public ConcurrentDictionary<int, AbsoluteLayout> Tracks = new();
    public ConcurrentDictionary<string, AssetItem> Assets = new();
    public string workingPath { get; set; } = "";
    public event EventHandler<ClipUpdateEventArgs>? OnClipChanged;
    public double SecondsPerFrame { get; set; } = 1 / 30d;
    public double FramePerPixel { get; set; } = 1d;
    public uint projectDuration { get; set; } = 0;

    public ICommand ExportCommand { get; private set; }
    public ICommand GoRenderCommand { get; private set; }
    public ICommand SettingsCommand { get; private set; }
    public ICommand UndoCommand { get; private set; }
    public ICommand RedoCommand { get; private set; }
    public ICommand SpiltCommand { get; private set; }
    public ICommand DeleteCommand { get; private set; }
    public ICommand SaveCommand { get; private set; }
    public ICommand ManageJobsCommand { get; private set; }
    #endregion

    #region options
    public string ProjectName { get; set; } = "Unknown project";
    public bool ShowShadow { get; set; } = true;
    public bool LogUIMessageToLogger { get; set; } = false;
    public bool Denoise { get; set; } = false;
    public int MaximumSaveSlot { get; set; } = 8;
    public int CurrentSaveSlotIndex { get; set; } = 0;
    public bool IsReadonly { get; set; } = false;
    public bool UseLivePreviewInsteadOfBackend { get; set; } = false;
    public string PreferredPopupMode { get; set; } = "right";
    public TimeSpan SyncCooldown { get; set; } = TimeSpan.FromMilliseconds(500);
    public bool AlwaysShowToolbarBtns { get; set; }
    #endregion

    #region init
    public DraftPage()
    {
        BindingContext = this;
        ExportCommand = new Command(() => OnExportedClick(this, EventArgs.Empty));
        GoRenderCommand = new Command(() => OnExportedClick(this, EventArgs.Empty));
        SettingsCommand = new Command(() => SettingsClick(this, EventArgs.Empty));
        UndoCommand = new Command(() => UndoChanges());
        RedoCommand = new Command(() => RedoChanges());
        SpiltCommand = new Command(() => Split_Clicked(this, EventArgs.Empty));
        DeleteCommand = new Command(() => DeleteAClip());
        SaveCommand = new Command(() => OnRefreshButtonClicked(this, EventArgs.Empty));

        InitializeComponent();
        ClipEditor = new InteractableEditor.InteractableEditor { IsVisible = false, HeightRequest = 240, HorizontalOptions = LayoutOptions.Fill };
        ClipEditorHost.Content = ClipEditor;
        ClipEditor.Init(OnClipEditorUpdate, 1920, 1080);
#if ANDROID
        OverlayLayer.IsVisible = false;
        OverlayLayer.InputTransparent = false;
#endif
        PreviewOverlayLayer.InputTransparent = false;
        ClipEditorHost.InputTransparent = false;

        TrackCalculator.HeightPerTrack = ClipHeight;
        infoBuilder = new ClipInfoBuilder(this);

    }

    public DraftPage(ProjectJSONStructure info, ConcurrentDictionary<string, ClipElementUI> clips, ConcurrentDictionary<string, AssetItem> assets, int initialTrackCount, string workingDir, string title = "Untitled draft", bool isReadonly = false, object? dbgBackend = null)
    {
        BindingContext = this;
        ExportCommand = new Command(() => OnExportedClick(this, EventArgs.Empty));
        GoRenderCommand = new Command(() => OnExportedClick(this, EventArgs.Empty));
        SettingsCommand = new Command(() => SettingsClick(this, EventArgs.Empty));
        UndoCommand = new Command(() => UndoChanges());
        RedoCommand = new Command(() => RedoChanges());
        SpiltCommand = new Command(() => Split_Clicked(this, EventArgs.Empty));
        DeleteCommand = new Command(() => DeleteAClip());
        SaveCommand = new Command(() => OnRefreshButtonClicked(this, EventArgs.Empty));

        InitializeComponent();
        ClipEditor = new InteractableEditor.InteractableEditor { IsVisible = false, HeightRequest = 240, HorizontalOptions = LayoutOptions.Fill };
        ClipEditorHost.Content = ClipEditor;
        ClipEditor.Init(OnClipEditorUpdate, 1920, 1080);
#if ANDROID
        OverlayLayer.IsVisible = false;
        OverlayLayer.InputTransparent = false;
#endif
#if WINDOWS
        if (dbgBackend is RpcClient client)
        {
            _rpc = client;
        }

#endif

        infoBuilder = new ClipInfoBuilder(this);
        workingPath = workingDir;
        TrackCalculator.HeightPerTrack = ClipHeight;

        SetStateBusy();
        Clips = clips;
        Assets = assets;
        Tracks = new ConcurrentDictionary<int, AbsoluteLayout>();

        for (int i = 0; i < initialTrackCount; i++)
        {
            if (!Tracks.ContainsKey(i)) AddATrack(i);
        }

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
        IsReadonly = isReadonly;

        PlayheadLine.TranslationX = TrackHeadLayout.Width;

        PostInit();
    }

    protected override async void OnAppearing()
    {
        base.OnAppearing();
#if WINDOWS
        _isClosing = false;
#endif
        PostInit();

        var size = GetScreenSizeInDp();
        LogDiagnostic($"Window size on appearing: {size.Width:F0} x {size.Height:F0} (DIP)");
        await Task.Delay(50);

        var w = this.Window?.Width ?? 0;
        var h = this.Window?.Height ?? 0;
        WindowSize = new Size(w, h);
#if WINDOWS
        if (_rpc is null && !UseLivePreviewInsteadOfBackend)
        {
            try { await BootRPC(); } catch { }
        }
#endif
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
                Order = ToolbarItemOrder.Primary,
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
            {"DraftPage_MenuBar_Jobs_ManageJobs", ManageJobsCommand }
        };

        var localizedActionPair = actionsPair.ToDictionary(kv => Localized.DynamicLookup(kv.Key), kv => kv.Value);

        var option = await DisplayActionSheetAsync(Localized._Info, Localized._Cancel, null, localizedActionPair.Keys.ToArray());

        var optionKey = actionsPair.Keys.FirstOrDefault(k => Localized.DynamicLookup(k) == option) ?? "unknown";

        if(actionsPair.TryGetValue(optionKey, out var cmd))
        {
            cmd?.Execute(null);
        }
    }

    private bool Inited = false;

    private async void PostInit()
    {
        if (Inited) return;
        Inited = true;

        ClipEditor.UpdateVideoResolution(ProjectInfo.RelativeWidth, ProjectInfo.RelativeHeight);
        var resString = $"{ProjectInfo.RelativeWidth}x{ProjectInfo.RelativeHeight}";
        if (ResolutionPicker.ItemsSource is List<string> list && list.Contains(resString))
        {
            ResolutionPicker.SelectedItem = resString;
        }

        ProjectInfo.NormallyExited = false;
        ProjectNameMenuBarItem.Text = ProjectInfo.projectName ?? "Unknown project";
#if WINDOWS
        PreviewBox.IsVisible = false;
        if (!(_rpc is not null || UseLivePreviewInsteadOfBackend)) await BootRPC();
#endif

        rulerTapGesture.Tapped += PlayheadTapped;

        nopGesture.Tapped += (s, e) =>
        {
#if ANDROID
            OverlayLayer.IsVisible = false;
#endif
        };
        if (!string.IsNullOrWhiteSpace(workingPath))
        {
            foreach (var item in DirectoriesNeeded)
            {
                Directory.CreateDirectory(Path.Combine(workingPath, item));
            }
        }

        previewer.TempPath = Path.Combine(workingPath, "thumbs");
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
        if (UseLivePreviewInsteadOfBackend)
        {
            Context context = Context.CreateDefault();
            var devices = context.Devices.ToList();
            projectFrameCut.Render.WindowsRender.ILGPUPlugin.accelerators = devices.Select(d => d.CreateAccelerator(context)).ToArray();

        }

#endif

        await Dispatcher.DispatchAsync(() =>
        {
            UpdatePlayheadPosition();

            Loaded += DraftPage_Loaded;

            OnClipChanged += DraftChanged;

            if (this.Window is not null)
            {
                this.Window.SizeChanged += Window_SizeChanged;
            }
            if (!Directory.Exists(workingPath)) Title = Localized.DraftPage_IsInMode_Special(Title);
        });
    }
#if WINDOWS
    private async Task BootRPC()
    {
        var pipeId = RpcClient.BootRPCServer(out backendProc, out backendAccessToken, out backendPort,
            tmpDir: Path.Combine(workingPath, "thumbs"),
            stderrCallback: new Action<string>((s) =>
            {
                SetStateFail("Backend:" + s);
            }));
        backendProc.Exited += (s, e) =>
        {
            Dispatcher.Dispatch(async () =>
            {
                if (_isClosing) return;
                if (await DisplayAlert(Localized._Info, Localized.DraftPage_BackendExited, Localized._OK, Localized._Cancel))
                {
                    var ct = new CancellationTokenSource();
                    ct.CancelAfter(10000);
                    _rpc = new()
                    {
                        ErrorCallback = new Action<JsonElement>((b) =>
                        {
                            if (b.TryGetProperty("message", out var m))
                            {
                                var msg = b.GetString();
                                if (msg is not null) SetStateFail("Backend: " + msg);
                            }
                        })
                    };
                    await _rpc.StartAsync(pipeId, ct.Token);
                }
            });
        };
        var ct = new CancellationTokenSource();
        ct.CancelAfter(10000);
        _rpc = new()
        {
            ErrorCallback = new Action<JsonElement>((b) =>
            {
                if (b.TryGetProperty("message", out var m))
                {
                    var msg = b.GetString();
                    if (msg is not null) SetStateFail("Backend: " + msg);
                }
            })
        };
        await _rpc.StartAsync(pipeId, ct.Token);
        await Task.Delay(25);
        if (_isClosing || _rpc is null) return;
        var nanoInfo = await _rpc.SendAsync("GetNanoHostInfo", default, default);
        if (nanoInfo is not null && nanoInfo.Value.TryGetProperty("port", out var portElem))
        {
            backendPort = portElem.GetInt32();
        }
    }
#endif

    private void DraftPage_Loaded(object? sender, EventArgs e)
    {
        if (AlwaysShowToolbarBtns || !OperatingSystem.IsWindows()) AddToolbarBtns();
        if (Width < Height) RightMenuBar.IsVisible = false;

        //PlayheadLine.TranslationY = UpperContent.Height - RulerLayout.Height;
        RulerLayout.GestureRecognizers.Add(rulerTapGesture);
        PlayheadLine.HeightRequest = Tracks.Count * ClipHeight;
        Window.SizeChanged += Window_SizeChanged;
        var bgTap = new TapGestureRecognizer();
        bgTap.Tapped += async (s, e) => await HidePopup();
        OverlayLayer.GestureRecognizers.Clear();
        OverlayLayer.GestureRecognizers.Add(bgTap);

        fileDropGesture.AllowDrop = true;
        fileDropGesture.DragOver += File_DragOver;
        fileDropGesture.Drop += File_Drop;
        MainUpperContent.GestureRecognizers.Add(fileDropGesture);

        ResolutionPicker.ItemsSource = new List<string> {
                "1280x720",
                "1920x1080",
                "2560x1440",
                "3840x2160",
                "7680x4320",
                "Custom..."
                };

        DraftChanged(sender, new());
        SetStateOK();
        SetStatusText(Localized.DraftPage_EverythingFine);
        MyLoggerExtensions.OnExceptionLog += MyLoggerExtensions_OnExceptionLog;

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
        selectTapGesture.Buttons = ButtonsMask.Primary | ButtonsMask.Secondary;
        selectTapGesture.Tapped += SelectTapGesture_Tapped;

        var doubleTapGesture = new TapGestureRecognizer();
        doubleTapGesture.NumberOfTapsRequired = 2;
        doubleTapGesture.Tapped += DoubleTapGesture_Tapped;

        element.Clip.GestureRecognizers.Add(clipPanGesture);
        element.Clip.GestureRecognizers.Add(selectTapGesture);
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
            double clipWidth = border.Width > 0 ? border.Width : border.WidthRequest;
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
        AddATrack(Tracks.Count);

        OnClipChanged?.Invoke(this, new ClipUpdateEventArgs { Reason = ClipUpdateReason.TrackAdd, SourceId = trackCount.ToString() });
    }

    public void AddATrack(int trackId)
    {
        Button removeBtn = new Button
        {
            Text = "Remove",
            BackgroundColor = Colors.Red,
            TextColor = Colors.White,
            HorizontalOptions = LayoutOptions.End
        };

        Border head = new Border
        {
            Content = new VerticalStackLayout
            {
                Children =
                {
                    new Label
                    {
                        Text = "New Track 1"
                    },
                    removeBtn
                }
            },
            HeightRequest = 60.0,
            Margin = new Thickness(0.0, 0.0, 0.0, 2.0)
        };

        AbsoluteLayout track = new AbsoluteLayout();

        var UnselectTapGesture = new TapGestureRecognizer();
        UnselectTapGesture.Tapped += UnSelectTapGesture_Tapped;

        track.GestureRecognizers.Add(UnselectTapGesture);
        head.GestureRecognizers.Add(UnselectTapGesture);

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
            TrackContentLayout.Children.RemoveAt(currentTrack);
        };

        Tracks.AddOrUpdate(trackId, track, (int _, AbsoluteLayout _) => track);

        Border content = new Border
        {
            Content = track,
            HeightRequest = 60.0,
            Margin = new Thickness(0.0, 0.0, 0.0, 2.0)
        };

        TrackHeadLayout.Children.Insert(TrackHeadLayout.Count - 1, head);
        TrackContentLayout.Children.Add(content);
        trackCount++;
    }

    private async void AddClip_Clicked(object sender, EventArgs e)
    {
        int nativeTrackIndex = Tracks.Last().Key;
        PropertyPanelBuilder ppb = new();
        ppb
        .AddText("Add from asset...")
        .AddCustomChild(BuildAssetPanel(false))
        .AddButton("textClip", "Create a text clip")
        .AddButton("solidColorClip", "Create a solid color clip")
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
            }
            await HidePopup();
        });

        await ShowAPopup(new ScrollView { Content = ppb.Build() });
    }

    private async Task AddATextClip()
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

            int trackIndex = Tracks.Any() ? Tracks.Last().Key : 0;
            if (!Tracks.ContainsKey(trackIndex))
            {
                AddATrack(trackIndex);
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

        int trackIndex = Tracks.Any() ? Tracks.Last().Key : 0;
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
        clip.Clip.Background = Colors.YellowGreen;
        SetStatusText(Localized.DraftPage_Selected(clip.displayName));
        ClipEditor.SetClip(clip, Assets.TryGetValue(clip.Id, out var asset) ? asset : null);
        SetTimelineScrollEnabled(false);
    }

    private void UnSelectTapGesture_Tapped(object? sender, TappedEventArgs e)
    {
        if (_selected is null) return;
        _selected.Clip.Background = new SolidColorBrush(Colors.CornflowerBlue);
        _selected = null;
        SetStatusText(Localized.DraftPage_EverythingFine);
        ClipEditor.SetClip(null, null);
        SetTimelineScrollEnabled(true);
    }

    private void SetTimelineScrollEnabled(bool enabled)
    {
#if WINDOWS
        if (TimelineScrollView.Handler?.PlatformView is Microsoft.UI.Xaml.Controls.ScrollViewer sv)
        {
            sv.HorizontalScrollMode = enabled ? Microsoft.UI.Xaml.Controls.ScrollMode.Enabled : Microsoft.UI.Xaml.Controls.ScrollMode.Disabled;
        }
#else
        //todo: lock scrollview in android
#endif
    }
    #endregion

    #region move clip
    private void ClipPaned(object? sender, PanUpdatedEventArgs e)
    {
        if (sender is not Border border) return;
        if (border.BindingContext is not ClipElementUI clip) return;
        if (clip.MovingStatus == ClipMovingStatus.Resize) return;
        var cid = clip.Id;

        int origTrack = TrackCalculator.CalculateWhichTrackShouldIn(border.TranslationY);
        clip.origTrack ??= origTrack;

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
        double actualYToBe = clipAbsolutePosition.Y - UpperContent.Height;

        int newTrack = TrackCalculator.CalculateWhichTrackShouldIn(actualYToBe);

        ClipElementUI shadow = Clips["shadow_" + cid];
        // Apply snapping and overlap resolution for shadow placement
        double proposed = xToBe;
        double snapped = SnapPixels(proposed);
        double clipWidth = border.Width > 0 ? border.Width : border.WidthRequest;
        if (newTrack >= 0 && newTrack < trackCount)
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

            if (newTrack >= 0 && newTrack < trackCount)
            {
                Tracks[newTrack].Children.Add(shadow.Clip);
                shadow.origTrack = newTrack;

            }
            else
            {
                shadow.origTrack = null;
            }
            if (newTrack > trackCount + 1 || newTrack < 0) SetStatusText(Localized.DraftPage_ReleaseToRemove);
            else SetStatusText(Localized.DraftPage_WaitForUser);
        }
        catch (Exception ex) //just ignore it, avoid crash
        {
            Log(ex, $"set shadow for {shadow.Id}", this);
        }
    }

    private async void HandlePanCompleted(Border border, ClipElementUI clip, string cid)
    {
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
            int newTrack = TrackCalculator.CalculateWhichTrackShouldIn(ghostClip.Clip.TranslationY - UpperContent.Height);
            OverlayLayer.Children.Remove(ghostClip.Clip);

            if (clip.origTrack is int oldTrack && Tracks.TryGetValue(oldTrack, out var oldTrackLayout))
            {
                oldTrackLayout.Children.Remove(border);
            }

            if (newTrack < 0 || newTrack > trackCount)
            {
                Clips.TryRemove(cid, out _);
                SetStatusText(Localized.DraftPage_Removed);
                SetStateOK();
                LogDiagnostic($"clip {cid} removed.");
                return;
            }
            else
            {
                if (newTrack == trackCount)
                {
                    AddTrackButton_Clicked(this, EventArgs.Empty);
                }

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
        UpdateAdjacencyForTrack();
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
                UpdateAdjacencyForTrack(newTrack);
            }
            catch (Exception ex)
            {
                Log(ex, $"Set clip {clip.Id}", this);
                await DisplayAlert(Localized._Info, "Failed to set clip. Please reload draft.", Localized._OK);
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
                if (clip.isInfiniteLength) goto go_resize;
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
                clip.lengthInFrame = PixelToFrame(clip.Clip.Width);
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
                StatusLabel.Text = $"clip {clip.Id} resized. x:{border.TranslationX} width:{border.WidthRequest}";
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
                if (clip.isInfiniteLength || !isLongerThanSrc)
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
                clip.lengthInFrame = PixelToFrame(clip.Clip.Width);
                OnClipChanged?.Invoke(clip.Id, new ClipUpdateEventArgs
                {
                    SourceId = clip.Id,
                    Reason = ClipUpdateReason.ClipResized
                });
                clip.MovingStatus = ClipMovingStatus.Free;
                StatusLabel.Text = $"clip {clip.Id} resized. x:{border.TranslationX} width:{border.WidthRequest}";

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

    private void OnClipPropertiesChanged(object? sender, PropertyPanelPropertyChangedEventArgs e)
    {
        if (_selected is null) return;
        var clip = _selected;

        if (e.Id == "__REFRESH_PANEL__")
        {
            Popup.Content = new ScrollView { Content = BuildPropertyPanel(clip) };
            Clips[clip.Id] = clip;
            ReRenderUI();
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

        ReRenderUI();

    }



    #endregion

    #region asset

    private async Task AddAsset(string path)
    {
        SetStateBusy(Localized.DraftPage_PrepareAsset);
        try
        {
            var srcHash = "";// await HashServices.ComputeFileSHA512Async(path, null);
            Log($"New asset {path}'s hash: {srcHash}");
            if (false && Assets.Values.Any((v) => v.SourceHash == srcHash)) //todo: use tasks to do this
            {
                string opt = await DisplayActionSheet(
                    Localized.DraftPage_DuplicatedAsset(Path.GetFileNameWithoutExtension(path), Assets.Values.First((v) => v.SourceHash == srcHash).Name),
                    null,
                    null,
                    [Localized.DraftPage_DuplicatedAsset_Relpace, Localized.DraftPage_DuplicatedAsset_Skip, Localized.DraftPage_DuplicatedAsset_Together]
                );

                if (opt == Localized.DraftPage_DuplicatedAsset_Relpace)
                {
                    var existing = Assets.Values.First((v) => v.SourceHash == srcHash);
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

            var cid = Guid.NewGuid().ToString();
            var item = new AssetItem
            {
                Name = System.IO.Path.GetFileNameWithoutExtension(path),
                Path = path,
                SourceHash = srcHash,
                Type = ClipElementUI.DetermineClipMode(path),
                AssetId = cid
            };

            if (item.Type != ClipMode.VideoClip)
            {
                item.SecondPerFrame = float.PositiveInfinity;
                item.FrameCount = 0;
            }
            else
            {
#if WINDOWS
                var cts = new CancellationTokenSource();
                cts.CancelAfter(5000);
                JsonElement? info = null;
                if (!_isClosing && _rpc is not null)
                    info = await _rpc.SendAsync("GetVideoFileInfo", JsonSerializer.SerializeToElement(item.Path), cts.Token);
                if (info is not null)
                {
                    info.Value.TryGetProperty("frameCount", out var fc);
                    info.Value.TryGetProperty("fps", out var fps);
                    info.Value.TryGetProperty("width", out var w);
                    info.Value.TryGetProperty("height", out var h);
                    if (fc.ValueKind == JsonValueKind.Number && fps.ValueKind == JsonValueKind.Number && w.ValueKind == JsonValueKind.Number && h.ValueKind == JsonValueKind.Number)
                    {
                        item.FrameCount = fc.GetInt64();
                        item.SecondPerFrame = 1 / fps.GetSingle();
                        item.Width = w.GetInt32();
                        item.Height = h.GetInt32();
                    }

                }
                cts = new CancellationTokenSource();
                cts.CancelAfter(5000);
                JsonElement? thumbNail = null;
                if (!_isClosing && _rpc is not null)
                    thumbNail = await _rpc.SendAsync("ReadAFrame", JsonSerializer.SerializeToElement(new Dictionary<string, object>
                        {
                            { "path", item.Path },
                            { "frameToRead", 0U },
                            { "size", "640x480" }
                        }), cts.Token);

                if (thumbNail is not null && thumbNail.Value.TryGetProperty("path", out var tPath))
                {
                    var p = tPath.GetString();
                    if (!string.IsNullOrWhiteSpace(p))
                    {
                        File.Move(p, Path.Combine(workingPath, "thumbs", item.AssetId + "thumb.png"), true);
                        item.ThumbnailPath = Path.Combine(workingPath, "thumbs", item.AssetId + "thumb.png");
                    }
                }
#else

                try
                {
                    var vid = PluginManager.CreateVideoSource(item.Path);
                    item.FrameCount = vid.TotalFrames;
                    item.SecondPerFrame = (float)(1f / vid.Fps);


                }
                catch (Exception ex)
                {
                    Log(ex, "get video length", this);
                    item.FrameCount = 1024;
                    item.SecondPerFrame = 1 / 42f;
                }

#endif
            }
            Log($"Added asset '{item.Path}'s info: {item.FrameCount} frames, {1f / item.SecondPerFrame}fps, {item.SecondPerFrame}spf, {item.FrameCount * item.SecondPerFrame} s");
            Assets.AddOrUpdate(cid, item, (_, _) => item);
            Dispatcher.Dispatch(() =>
            {
                Popup.Content = new ScrollView { Content = BuildAssetPanel() };
            });
            SetStateOK(Localized.DraftPage_AssetAdded(Path.GetFileNameWithoutExtension(path)));

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
        var closeButton = new Button
        {
            Background = Colors.Green,
            Text = "Close"
        };

        closeButton.Clicked += async (s, e) =>
        {
            await HidePopup();
        };
        if (includeHeader) layout.Children.Add(closeButton);
        foreach (var kvp in Assets)
        {
            var asset = kvp.Value;
            var label = $"{asset.Icon} {asset.Name}";
            var assetClip = ClipElementUI.CreateClip(0, 150, 0, labelText: label, background: (Brush?)asset.Background);
            assetClip.maxFrameCount = (uint)(asset.FrameCount ?? 0U);
            assetClip.isInfiniteLength = asset.isInfiniteLength;
            assetClip.Clip.WidthRequest = 200;
            var addButton = new Button
            {
                Background = Colors.Green,
                Text = "Add"
            };

            var removeButton = new Button
            {
                Background = Colors.Red,
                Text = "Delete"
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
                var elem = ClipElementUI.CreateClip(
                            startX: 0,
                            width: FrameToPixel((uint)(asset.FrameCount ?? 1024)),
                            trackIndex: Tracks.Last().Key,
                            labelText: asset.Name,
                            background: (Brush?)asset.Background,
                            maxFrames: (uint)(asset.FrameCount ?? 0U),
                            relativeStart: 0
                           );

                elem.sourcePath = asset.Path;
                elem.ClipType = asset.Type;
                elem.FromPlugin = "projectFrameCut.Render.Plugins.InternalPluginBase";
                elem.sourceSecondPerFrame = asset.SecondPerFrame;
                elem.SecondPerFrameRatio = 1f;
                elem.ExtraData = new();

                RegisterClip(elem, true);
                AddAClip(elem);

                UpdateAdjacencyForTrack();
                SetStatusText($"Asset '{asset.Name}' added to track.");
                await HidePopup();

            };
            childLayout.Children.Add(addButton);
            if (includeHeader) childLayout.Children.Add(removeButton);
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

                var result = await FilePicker.PickAsync(new PickOptions
                {
                    PickerTitle = "1",
                    FileTypes = new FilePickerFileType(new Dictionary<DevicePlatform, IEnumerable<string>>
                {
                    { DevicePlatform.WinUI,[ ".mp4", ".mov", ".mkv", ".avi", ".webm", ".m4v",".png", ".jpg", ".jpeg", ".webp", ".bmp", ".tiff"] },
                    { DevicePlatform.Android, [ "image/*", "video/*"] },
#if iDevices
                    {DevicePlatform.iOS , ["public.image", "public.movie", "public.video", "public.mpeg-4", "com.apple.protected-mpeg-4-video", "com.apple.quicktime-movie", "public.avi", "org.matroska.mkv"]},
                    {DevicePlatform.MacCatalyst , ["public.image", "public.movie", "public.video", "public.mpeg-4", "com.apple.protected-mpeg-4-video", "com.apple.quicktime-movie", "public.avi", "org.matroska.mkv"]}
#endif
                })
                });
                string resultPath = "";
                if (result is not null)
                {
                    if (!OperatingSystem.IsWindows()) 
                    {
                        resultPath = Path.Combine(workingPath, "assets", $"imported-{result.FileName}");
                        if (!string.IsNullOrWhiteSpace(workingPath))
                        {
                            File.Move(result.FullPath, resultPath, true);
                        }
                    }
                    else
                    {
                        resultPath = result.FullPath;
                    }

                    AddAsset(resultPath);

                }
            });
        };

        if (includeHeader) layout.Add(addBtn);

        return new ScrollView
        {
            Content = layout,
            Orientation = ScrollOrientation.Horizontal
        };
    }


    #endregion

    #region handle change
    private async Task RenderOneFrame(uint duration)
    {
        _currentFrame = duration;
        SetStateBusy();
        SetStatusText(Localized.DraftPage_RenderOneFrame((int)duration, TimeSpan.FromSeconds(duration * SecondsPerFrame)));
        var cts = new CancellationTokenSource();
        string path = "";
        if (!OperatingSystem.IsWindows() || UseLivePreviewInsteadOfBackend)
        {
            await Task.Run(() =>
            {
                path = previewer.RenderFrame(duration, 1280, 720);
            });
        }
        else
        {
#if WINDOWS
#if !DEBUG
        cts.CancelAfter(10000);
#endif
            if (_isClosing)
            {
                SetStateFail(Localized.DraftPage_CannotSave_Readonly);
                return;
            }
            if (_rpc is not null)
            {
                path = await RpcClient.RenderOneFrame(duration, _rpc, cts.Token);
            }

            await Task.Delay(2000);
            var src = ImageSource.FromFile(path);

#endif
        }
        await PreviewOverlayImage.ForceLoadPNGToAImage(path);
        await PreviewBox.ForceLoadPNGToAImage(path);

        SetStateOK();
        SetStatusText(Localized.DraftPage_EverythingFine);
    }

    private async void DraftChanged(object? sender, ClipUpdateEventArgs e)
    {
#if WINDOWS
        if (_isClosing) return;

#endif
        if (string.IsNullOrEmpty(workingPath))
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
        await Save();
        PlayheadLine.HeightRequest = Tracks.Count * ClipHeight;
        var d = DraftImportAndExportHelper.ExportFromDraftPage(this);
        SetStateBusy();
        SetStatusText(Localized.DraftPage_ApplyingChanges);
        var cts = new CancellationTokenSource();

        try
        {
#if WINDOWS
            if (_isClosing) return;
#endif // avoid any rpc/update during closing
            if (!OperatingSystem.IsWindows() || UseLivePreviewInsteadOfBackend)
            {
                previewer.UpdateDraft(d);
            }
            else
            {
#if WINDOWS
#if !DEBUG
                cts.CancelAfter(10000);
#endif
                if (_rpc is not null)
                {
                    await RpcClient.UpdateDraft(d, _rpc, cts.Token);
                }
#endif
            }
            SetStatusText(Localized.DraftPage_ChangesApplied);

        }
        catch (Exception ex)
        {
            SetStateFail(Localized._ExceptionTemplate(ex));
            // RPC not connected might be thrown during shutdown
            if (!(ex is InvalidOperationException && ex.Message.Contains("RPC")))
            {
                await DisplayAlert($"{ex.GetType()} Error", ex.Message, "ok");
            }
        }
        finally
        {
            SetStatusText(Localized.DraftPage_ChangesApplied);
            SetStateOK();
        }
    }

    private async void OnClipEditorUpdate()
    {
#if WINDOWS
        if (_isClosing) return;

#endif
        var d = DraftImportAndExportHelper.ExportFromDraftPage(this);
        if (!OperatingSystem.IsWindows() || UseLivePreviewInsteadOfBackend)
        {
            previewer.UpdateDraft(d);
        }
        else
        {
#if WINDOWS
            var cts = new CancellationTokenSource();
#if !DEBUG
            cts.CancelAfter(10000);
#endif
            if (_rpc is not null)
            {
                await RpcClient.UpdateDraft(d, _rpc, cts.Token);
            }
#endif
        }

        var currentX = PlayheadLine.TranslationX - TrackHeadLayout.Width;
        if (currentX < 0) currentX = 0;
        var duration = PixelToFrame(currentX);
        await RenderOneFrame(duration);
    }
    #endregion

    #region adjust track and clip
    private void ReRenderUI()
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

                Dispatcher.Dispatch(() =>
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

            UpdateAdjacencyForTrack();
        }
        finally
        {
            SetStateOK();
        }
    }


    class _RoundRectangleRadiusType
    {
        public double tl { get; set; }
        public double tr { get; set; }
        public double bl { get; set; }
        public double br { get; set; }
    }

    class _TrackClassForUpdateAdjacencyForTrack //why not use anonymous class? because of AOT on IOS/Mac!!!
    {
        public double Start { get; set; }
        public double End { get; set; }
        public ClipElementUI Clip { get; set; }
    }

    private void UpdateAdjacencyForTrack() => Tracks.Keys.ToList().ForEach(UpdateAdjacencyForTrack);


    private void UpdateAdjacencyForTrack(int trackIndex)
    {
        if (!Tracks.TryGetValue(trackIndex, out var track)) return;
        var byorder = track.Children.OfType<Border>()
            .Select(b => b.BindingContext)
            .OfType<ClipElementUI>()
            .Select(c =>
            {
                double start = Math.Round(c.Clip.X + c.Clip.TranslationX);
                double width = (!double.IsNaN(c.Clip.Width) && c.Clip.Width > 0) ? Math.Round(c.Clip.Width) : Math.Round(c.Clip.WidthRequest);
                double end = start + width;
                return new _TrackClassForUpdateAdjacencyForTrack { Start = start, End = end, Clip = c };
            })
            .OrderBy(t => t.Start)
            .ToList();

        const double defaultRadius = 20.0;

        // Use a local radius array to avoid races between concurrent UpdateAdjacencyForTrack calls
        var localRadius = new _RoundRectangleRadiusType[byorder.Count];

        for (int i = 0; i < byorder.Count; i++)
        {
            localRadius[i] = new _RoundRectangleRadiusType { tl = defaultRadius, tr = defaultRadius, br = defaultRadius, bl = defaultRadius };
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

        foreach (var item in byorder)
        {
            var i = byorder.IndexOf(item);
            // capture radius locally to avoid races when dispatcher runs later
            var r = localRadius[i];
            Dispatcher.Dispatch(() =>
            {
                try
                {

                    item.Clip.Clip.StrokeShape = new RoundRectangle
                    {
                        CornerRadius =
                    new Microsoft.Maui.CornerRadius(r.tl, r.tr, r.br, r.bl)
                    };
                }
                catch (Exception e)
                {
                    Log(e, "update round rectangle", this);
                    SetStateFail("Failed to update clip border.");
                }
            });

        }
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


    #endregion

    #region drag and drop
    private async void File_Drop(object? sender, DropEventArgs e)
    {
        var filePaths = new List<string>();

#if WINDOWS
        if (e.PlatformArgs is not null && e.PlatformArgs.DragEventArgs.DataView.Contains(StandardDataFormats.StorageItems))
        {
            var items = await e.PlatformArgs.DragEventArgs.DataView.GetStorageItemsAsync();
            if (items.Any())
            {
                foreach (var item in items)
                {
                    if (item is StorageFile file)
                        filePaths.Add(item.Path);
                }
            }
        }
#elif ANDROID
        return; //android not support drag and drop file yet,
                //although some OS supports it but MAUI doesn't support it, wtf?
#elif iDevices
        static async Task<LoadInPlaceResult?> LoadItemAsync(NSItemProvider itemProvider, List<string> typeIdentifiers)
        {
            try
            {
                if (typeIdentifiers is null || typeIdentifiers.Count == 0)
                    return null;

                var typeIdent = typeIdentifiers.First();

                if (itemProvider.HasItemConformingTo(typeIdent))
                    return await itemProvider.LoadInPlaceFileRepresentationAsync(typeIdent);

                typeIdentifiers.Remove(typeIdent);
                return await LoadItemAsync(itemProvider, typeIdentifiers);
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"Error loading item: {ex.Message}");
                return null;
            }


        }

        var session = e.PlatformArgs?.DropSession;
        if (session == null)
            return;

        foreach (UIDragItem item in session.Items)
        {
            var result = await LoadItemAsync(item.ItemProvider, item.ItemProvider.RegisteredTypeIdentifiers.ToList());
            if (result is not null)
                filePaths.Add(result.FileUrl?.Path!);
            else
                SetStateFail("File is null.");
        }



#endif
        if (filePaths.Count > 0)
        {
            foreach (var path in filePaths)
            {
                Log($"Importing file from drag and drop: {path}");
                AddAsset(path);
            }
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
#pragma warning disable CS0414 //this stuff only need on iDevices, because of OverlayLayer can't handle any input......  wtf?
    private IView? OrigionalUIContent = null;
#pragma warning restore CS0414
    private async Task ShowAPopup(View? content = null, Border? border = null, ClipElementUI? clip = null, string mode = "")
    {
        content ??= (border != null && clip != null) ? BuildPropertyPanel(clip) : new Label { Text = "No content." };
#if iDevices
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
#endif

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
#if ANDROID
        OverlayLayer.IsVisible = true;
#endif
#if iDevices
        OverlayLayer.InputTransparent = false;
#endif
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
        return;
#endif
        //if (!isPopupShowing) return;
        OverlayLayer.InputTransparent = true;
        await Task.WhenAll(HideClipPopup(), HideFullscreenPopup());
#if ANDROID || iDevices
        OverlayLayer.IsVisible = false;
        OverlayLayer.InputTransparent = true;
#endif
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
#if ANDROID || iDevices
        OverlayLayer.IsVisible = true;
        OverlayLayer.InputTransparent = false;
#endif
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
#if ANDROID || iDevices
        OverlayLayer.IsVisible = true;
        OverlayLayer.InputTransparent = false;
#endif
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
                default:
                    await Popup.TranslateToAsync(Popup.TranslationX, size.Height + 10, 300, Easing.SinIn);
                    break;
            }
        }
        catch { }

        OverlayLayer.Remove(Popup);
        OverlayLayer.InputTransparent = true;

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
        Log(text, "UI err");
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

    #region show and save changes
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

                await RenderOneFrame(duration);
            }
        }
        catch (Exception ex)
        {
            Log(ex, "playhead tap", this);
        }
    }

    private void TimelineScrollView_Scrolled(object sender, ScrolledEventArgs e)
    {
        UpdatePlayheadPosition();
    }

    private void UpdatePlayheadPosition()
    {
        double timeX = FrameToPixel((uint)_currentFrame);
        double scrollX = TimelineScrollView.ScrollX;
        double screenX = timeX + TrackHeadLayout.Width - scrollX;
        PlayheadLine.TranslationX = screenX;
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

        double minWidth = Math.Max(1000d, WindowSize.Width + 150);
        if (maxPixel < minWidth) maxPixel = minWidth;

        TrackContentLayout.WidthRequest = maxPixel;
    }

    public async Task Save(bool noSlot = false)
    {
        if (string.IsNullOrEmpty(workingPath))
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
            await File.WriteAllTextAsync(Path.Combine(workingPath, "timeline.json"), JsonSerializer.Serialize(draft, savingOpts), default);
            await File.WriteAllTextAsync(Path.Combine(workingPath, "assets.json"), JsonSerializer.Serialize(assets, savingOpts), default);
            try
            {
#if WINDOWS
                CancellationTokenSource cts = new();
                cts.CancelAfter(10000);
                var thumbPath = await RpcClient.RenderOneFrame(0, _rpc, cts.Token);
                if (!string.IsNullOrEmpty(thumbPath) && File.Exists(thumbPath))
                {
                    var destPath = Path.Combine(workingPath, "thumbs", "_project.png");
                    File.Copy(thumbPath, destPath, true);
                }
#endif
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
            saveLocker.Enter();
            try
            {
                Directory.CreateDirectory(Path.Combine(workingPath, "saveSlots", slot));
                await File.WriteAllTextAsync(Path.Combine(workingPath, "saveSlots", slot, "timeline.json"), JsonSerializer.Serialize(draft, savingOpts), default);
                await File.WriteAllTextAsync(Path.Combine(workingPath, "saveSlots", slot, "assets.json"), JsonSerializer.Serialize(assets, savingOpts), default);
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

        projectDuration = draft.Duration;
        ProjectInfo.LastChanged = DateTime.Now;
        saveLocker.Enter();
        try
        {
            await File.WriteAllTextAsync(Path.Combine(workingPath, "project.json"), JsonSerializer.Serialize(ProjectInfo, savingOpts), default);
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
        if (nextSlot < 1 || nextSlot >= MaximumSaveSlot || !Directory.Exists(Path.Combine(workingPath, "saveSlots", $"slot_{nextSlot}")))
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
        if (nextSlot >= MaximumSaveSlot || !Directory.Exists(Path.Combine(workingPath, "saveSlots", $"slot_{nextSlot}")))
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
            var slot = $"slot_{slotIndex}";
            var tml = File.ReadAllText(Path.Combine(workingPath, "saveSlots", slot, "timeline.json"));
            var assets = JsonSerializer.Deserialize<List<AssetItem>>(File.ReadAllText(Path.Combine(workingPath, "saveSlots", slot, "assets.json"))) ?? new();
            var draftJson = JsonSerializer.Deserialize<DraftStructureJSON>(tml);
            (var clips, var tracks) = DraftImportAndExportHelper.ImportFromJSON(draftJson);
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

            for (int i = 0; i < tracks; i++)
            {
                if (!Tracks.ContainsKey(i)) AddATrack(i);
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
            DraftChanged(this, new());
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

    private async void OnExportedClick(object sender, EventArgs e)
    {
#if WINDOWS
        backendProc.EnableRaisingEvents = false;
#endif
        await Save(true);
        await Navigation.PushAsync(new RenderPage(workingPath, projectDuration, ProjectInfo));

    }

    private async void SettingsClick(object sender, EventArgs e)
    {
        await Navigation.PushAsync(new ContentPage
        {
            Content = new ScrollView
            {
                Content = BuildPropertyPanel(_selected ?? new())
            }
        });
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
                    SetStatusText($"Set output resolution to {w1} x {h1}");
                    ClipEditor.UpdateVideoResolution(w1, h1);

#if WINDOWS
                    if (!_isClosing && _rpc is not null)
                        await _rpc.SendAsync("ConfigurePreview", JsonSerializer.SerializeToElement(new { width = w1, height = h1 }), default);
#endif
                    return;
                }
            }
        }

        var widthInput = await DisplayPromptAsync("Output Resolution", "Enter output width in pixels:", initialValue: "1920");
        var heightInput = await DisplayPromptAsync("Output Resolution", "Enter output height in pixels:", initialValue: "1080");
        if (int.TryParse(widthInput, out int w) && int.TryParse(heightInput, out int h))
        {
            SetStatusText($"Set output resolution to {w} x {h}");
            ClipEditor.UpdateVideoResolution(w, h);
#if WINDOWS
            if (!_isClosing && _rpc is not null)
                await _rpc.SendAsync("ConfigurePreview", JsonSerializer.SerializeToElement(new { width = w, height = h }), default);
#endif

        }
    }

    private void ZoomOutButton_Clicked(object sender, EventArgs e)
    {
        foreach (var item in Tracks)
        {
            foreach (var border in item.Value.Children)
            {
                if (border is Border b)
                {
                    b.WidthRequest *= 1.5;

                    b.TranslationX += b.WidthRequest * 1.5;

                }
            }
        }

        tracksViewOffset *= 1.5;
        tracksZoomOffest *= 1.5;
        UpdateTimelineWidth();
    }

    private void ZoomInButton_Clicked(object sender, EventArgs e)
    {
        foreach (var item in Tracks)
        {
            foreach (var border in item.Value.Children)
            {
                if (border is Border b)
                {
                    b.WidthRequest /= 1.5;

                    b.TranslationX -= b.WidthRequest * 1.5;

                }
            }
        }

        tracksViewOffset /= 1.5;
        tracksZoomOffest /= 1.5;
        UpdateTimelineWidth();
    }

    private Size GetScreenSizeInDp()
    {
        var info = DeviceDisplay.MainDisplayInfo;
        double widthDp = info.Width / info.Density;
        double heightDp = info.Height / info.Density;
        return new Size(widthDp, heightDp);
    }



    private void MyLoggerExtensions_OnExceptionLog(Exception obj)
    {
        Dispatcher.Dispatch(() =>
        {
            StatusLabel.TextColor = Colors.Red;
            StatusLabel.Text = Localized._ExceptionTemplate(obj);
        });
    }



    protected override async void OnDisappearing()
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
                    Text = Localized.DraftPage_SavingChanges,
                    HorizontalTextAlignment = TextAlignment.Center,
                    Margin = new Thickness(0,10,0,0)
                }
            }
        };
        base.OnDisappearing();
#if WINDOWS
        _isClosing = true;
#endif
        HidePopup();
#if WINDOWS
        try
        {
            // note: do NOT unsubscribe OnClipChanged/Loaded permanently; keep them attached for page reuse
            // we'll keep the _isClosing flag to avoid handling events after page closing
            MyLoggerExtensions.OnExceptionLog -= MyLoggerExtensions_OnExceptionLog;
            if (this.Window is not null)
            {
                this.Window.SizeChanged -= Window_SizeChanged;
            }

            if (_rpc is not null)
            {
                try
                {
                    await _rpc.DisposeAsync();
                }
                catch (Exception ex)
                {
                    Log(ex, "Dispose rpc", this);
                }
                finally
                {
                    _rpc = null;
                }
            }
        }
        catch (Exception ex)
        {
            Log(ex, "OnDisappearing cleanup", this);
        }
#endif
        MyLoggerExtensions.OnExceptionLog -= MyLoggerExtensions_OnExceptionLog;
        try
        {
            Save(true);
        }
        catch (Exception ex)
        {
            DisplayAlert("Error", $"Failed to save project on exit: {ex.Message}", "OK");
        }

    }

    private async void Window_SizeChanged(object? sender, EventArgs e)
    {
        double w = this.Window?.Width ?? 0;
        double h = this.Window?.Height ?? 0;
        WindowSize = new(w, h);
        LogDiagnostic($"Window size changed: {w:F0} x {h:F0} (DIP)");
        if (IsSyncCooldown()) return;
        SetSyncCooldown();
        if (popupShowingDirection != "none")
        {
            await HidePopup();
            await ShowClipPopup(Popup, _selected);
        }
        if (Width < Height) RightMenuBar.IsVisible = false;
        else RightMenuBar.IsVisible = true;
    }

    public async void OnRefreshButtonClicked(object sender, EventArgs e)
    {
        await HidePopup();
        UnSelectTapGesture_Tapped(sender, null!);
        SetTimelineScrollEnabled(true);
        DraftChanged(sender, null!);
        await Save(true);
        ReRenderUI();
    }

    #endregion
}
