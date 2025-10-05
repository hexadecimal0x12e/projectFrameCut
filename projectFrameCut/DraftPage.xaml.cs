using Microsoft.Maui.Controls.Shapes;
using Microsoft.Maui.Layouts;
using projectFrameCut.Shared;
using projectFrameCut.ViewModels;
using System.Collections.Concurrent;
using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.ComponentModel;
using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Serialization;
using Path = System.IO.Path;
using System.Globalization;
using projectFrameCut.Render;
using System.Threading.Tasks;
using System.IO.Pipes;



#region platform usings

#if WINDOWS
using projectFrameCut.Platforms.Windows;
#endif

#if ANDROID 
using projectFrameCut.Platforms.Android;
#endif

#if IOS 
using projectFrameCut.Platforms.iOS;
#endif

#if MACCATALYST
using projectFrameCut.Platforms.MacCatalyst;
#endif

#endregion

namespace projectFrameCut
{
    public partial class DraftPage : ContentPage
    {
        #region handle changes

#if ANDROID
        private async Task RenderPreviewOnAndroid(double playheadSeconds)
        {

        }
#elif IOS || MACCATALYST
        private async Task RenderPreviewOniDevices(double playheadSeconds)
        {

        }
#elif WINDOWS
        private async Task RenderPreviewOnWindows(double playheadSeconds)
        {
            SetStateBusy();
            int frameId = (int)Math.Floor(playheadSeconds * frameRate);

            try
            {
                CancellationTokenSource cts = new();
                if (_status is not null)
                    _status.Text = Localized.RenderPage_RenderOneFrame(frameId, TimeSpan.FromSeconds(playheadSeconds));
                if (_rpc is null) return;
                cts.CancelAfter(10000);
                var result = await _rpc.SendAsync("RenderOne", JsonSerializer.SerializeToElement(frameId), cts.Token);
                if (result is null) return;
                var path = result.Value.GetProperty("path").GetString();
                PreviewBox.Source = ImageSource.FromFile(path);
                WorkingState = Localized.RenderPage_RenderDone;
                SetStateOK();

            }
            catch (TaskCanceledException)
            {
                WorkingState = Localized.RenderPage_RenderTimeout;
                SetStateFail();
            }
            catch (Exception ex)
            {
                await DisplayAlert($"{ex.GetType()} Error", ex.Message, "ok");
                SetStateFail();
            }



            return;
        }
#endif

        private async void OnProjectChanged(string reason)
        {
            if (reason == "Timeline.PlayheadSeconds")
            {
#if WINDOWS
                await RenderPreviewOnWindows(_vm.PlayheadSeconds);
#elif ANDROID
                await RenderPreviewOnAndroid(_vm.PlayheadSeconds);
#elif IOS || MACCATALYST
                await RenderPreviewOniDevices(_vm.PlayheadSeconds);
#endif
                return;
            }

            SetStateBusy();
            if (_status is not null)
            {
                WorkingState = $"saving changes";
                _status.TextColor = Colors.White;
            }

            var draft = await BuildDraft(ProjectInfo.projectName);
            var opts = new JsonSerializerOptions { WriteIndented = true };
            await File.WriteAllTextAsync(Path.Combine(workingDirectory, "timeline.json"), JsonSerializer.Serialize(draft, opts), default);

            if (_status is not null)
                WorkingState = $"changes saved on {DateTime.Now:T} ";


#if WINDOWS
            if (await UpdateClipToBackend())
            {

                SetStateOK();
            }
            else
            {
                SetStateFail();
            }
#else
            SetStateOK();

#endif

        }

        private async void OnAddAsset(object sender, EventArgs e)
        {
            try
            {
#if ANDROID || IOS || MACCATALYST || WINDOWS
                var result = await FilePicker.PickAsync(new PickOptions
                {
                    PickerTitle = "选择素材文件"
                });
                if (result != null)
                {
                    Assets.Add(new AssetItem
                    {
                        Name = result.FileName,
                        Path = result.FullPath
                    });
                }
#else
                Assets.Add(new AssetItem { Name = $"Dummy_{Assets.Count + 1}.png", Path = null });
#endif
            }
            catch (Exception ex)
            {
                Log(ex);
                await DisplayAlert("Error", ex.Message, "OK");
            }
        }

        private async void AddSolidColorClip_Clicked(object sender, EventArgs e)
        {
            var track = _vm.Tracks[0]; // always first track for now
            var clip = new ClipViewModel
            {
                Name = $"SolidColor #{Assets.Count(c => c.Type == Shared.ClipMode.SolidColorClip) + 1}",
                Type = Shared.ClipMode.SolidColorClip,
                StartSeconds = _vm.PlayheadSeconds,
                DurationSeconds = 3,
                Metadata = { { "R", (ushort)11 }, { "G", (ushort)22 }, { "B", (ushort)33 }, { "A", 1f } }
            };
            track.Clips.Add(clip);
            clip.StartSeconds = ResolveOverlapStart(track, clip, clip.StartSeconds, clip.DurationSeconds);
            await RebuildTracksUI();


        }
        #endregion

        #region values

        private readonly TimelineViewModel _vm = new();
        private readonly Dictionary<ClipViewModel, Grid> _clipToView = new();
        private ClipViewModel? _selectedClip;
        private double _dragStartX;
        private double _dragStartSeconds;
        private bool _isResizingLeft;
        private bool _isResizingRight;
        private double _resizeInitialStart;
        private double _resizeInitialDuration;

        // Track drag helpers
        private const double TrackHeight = 48; // must match lane HeightRequest
        private int _dragStartTrackIndex = -1;
        private int _currentDragTrackIndex = -1;

        // References to named elements
        private VerticalStackLayout? _tracksHeader;
        private VerticalStackLayout? _tracksPanel;
        private AbsoluteLayout? _ruler;
        private BoxView? _playhead;
        private Label? _status;
        private Label _backendStatus;
        private ScrollView? _timelineScroll;

        private readonly HashSet<TrackViewModel> _subscribedTracks = new();
        private readonly HashSet<ClipViewModel> _subscribedClips = new();
        private CancellationTokenSource? _changeDebounceCts;

        private string WorkingState
        {
            get
            {
                if (_status is not null)
                    return _status.Text;
                return string.Empty;
            }
            set
            {
                if (_status is not null)
                {
                    Dispatcher.Dispatch(() =>
                    {
                        _status.Text = value;
                    });
                }
            }
        }

        private int frameRate = 30;

        public class AssetItem
        {
            public string Name { get; set; } = string.Empty;
            public string? Path { get; set; }
            public projectFrameCut.Shared.ClipMode Type { get; set; }


            public string? ThumbnailPath { get; set; }
            public string? AssetId { get; set; }

            [JsonIgnore()]
            public string? Icon
            {
                get => Type switch
                {
                    projectFrameCut.Shared.ClipMode.VideoClip => "📽️",
                    projectFrameCut.Shared.ClipMode.PhotoClip => "🖼️",
                    projectFrameCut.Shared.ClipMode.SolidColorClip => "🟦",
                    _ => "❔"
                };
            }

            [JsonIgnore()]
            public DateTime AddedAt { get; set; } = DateTime.Now;
        }

        public ObservableCollection<AssetItem> Assets { get; } = new();

        public ProjectJSONStructure ProjectInfo { get; set; } = new();

        private string workingDirectory = string.Empty;

        #endregion

        #region constructors and init
        public DraftPage()
        {
            InitializeComponent();

            BindingContext = _vm; // ensure binding for TimelineWidth

            // Resolve named elements from XAML
            _tracksHeader = this.FindByName<VerticalStackLayout>("TracksHeader");
            _tracksPanel = this.FindByName<VerticalStackLayout>("TracksPanel");
            _ruler = this.FindByName<AbsoluteLayout>("Ruler");
            _playhead = this.FindByName<BoxView>("Playhead");
            _status = this.FindByName<Label>("Status");
            _backendStatus = this.FindByName<Label>("BackendStatus");
            _timelineScroll = this.FindByName<ScrollView>("TimelineScroll");

#if !WINDOWS
            BackendStateIndicator.IsVisible = false;
            new Thread(MemoryUsageListener).Start();
#endif

            // Initialize with two tracks
            _vm.AddTrack(Localized.RenderPage_Processing);
            _vm.AddTrack(Localized.RenderPage_Track(1));

            RebuildTracksUI();
            UpdatePlayhead();
            BuildRuler();

            Title = "Untitled Project 1";

            StateIndicator.Children.Clear();
            StateIndicator.Children.Add(new Microsoft.Maui.Controls.Shapes.Path
            {
                Stroke = Colors.Green,
                StrokeThickness = 3,
                Data = (Geometry)new PathGeometryConverter().ConvertFromInvariantString("M 4,12 L 9,17 L 20,6"),
                WidthRequest = 20,
                HeightRequest = 20,
                Margin = new Thickness(0, -3, 0, 0)
            });
            SetStateOK();


#if WINDOWS

            Loaded += async (sender, e) => await BootRPC();
#else
            BackendStateIndicator.IsVisible = false;
            //_backendStatus.IsVisible = false;
#endif

        }

        public DraftPage(string workingPath)
        {
            InitializeComponent();
            SetStateBusy();
            workingDirectory = workingPath;
            BindingContext = _vm; // ensure binding for TimelineWidth

            // Resolve named elements from XAML
            _tracksHeader = this.FindByName<VerticalStackLayout>("TracksHeader");
            _tracksPanel = this.FindByName<VerticalStackLayout>("TracksPanel");
            _ruler = this.FindByName<AbsoluteLayout>("Ruler");
            _playhead = this.FindByName<BoxView>("Playhead");
            _status = this.FindByName<Label>("Status");
            _backendStatus = this.FindByName<Label>("BackendStatus");
            _timelineScroll = this.FindByName<ScrollView>("TimelineScroll");

#if !WINDOWS
            BackendStateIndicator.IsVisible = false;
            new Thread(MemoryUsageListener).Start();
#endif

            if (!Directory.Exists(workingPath))
                throw new DirectoryNotFoundException($"Working path not found: {workingPath}");

            var filesShouldExist = new[] { "project.json", "timeline.json", "assets.json" };

            if (filesShouldExist.Any((f) => !File.Exists(Path.Combine(workingPath, f))))
            {
                throw new FileNotFoundException("One or more required files are missing.", workingPath);
            }

            var project = JsonSerializer.Deserialize<ProjectJSONStructure>(File.ReadAllText(Path.Combine(workingPath, "project.json")));

            ProjectInfo = JsonSerializer.Deserialize<ProjectJSONStructure>(File.ReadAllText(Path.Combine(workingPath, "project.json"))) ?? new();
            var assets = JsonSerializer.Deserialize<ObservableCollection<AssetItem>>(File.ReadAllText(Path.Combine(workingPath, "assets.json"))) ?? new();
            Title = ProjectInfo.projectName;
            ImportDraft(File.ReadAllText(Path.Combine(workingPath, "timeline.json")));

            SetStateOK();

#if WINDOWS
            Loaded += async (sender, e) =>
            {
                await BootRPC();
                await UpdateClipToBackend();

            };
#else
            BackendStateIndicator.IsVisible = false;
            //_backendStatus.IsVisible = false;
#endif
        }

#if DEBUG && WINDOWS
        public DraftPage(string workingPath, NamedPipeClientStream npc)
        {
            InitializeComponent();
            SetStateBusy();

            workingDirectory = workingPath;
            BindingContext = _vm; // ensure binding for TimelineWidth

            // Resolve named elements from XAML
            _tracksHeader = this.FindByName<VerticalStackLayout>("TracksHeader");
            _tracksPanel = this.FindByName<VerticalStackLayout>("TracksPanel");
            _ruler = this.FindByName<AbsoluteLayout>("Ruler");
            _playhead = this.FindByName<BoxView>("Playhead");
            _status = this.FindByName<Label>("Status");
            _backendStatus = this.FindByName<Label>("BackendStatus");
            _timelineScroll = this.FindByName<ScrollView>("TimelineScroll");


            if (!Directory.Exists(workingPath))
                throw new DirectoryNotFoundException($"Working path not found: {workingPath}");

            var filesShouldExist = new[] { "project.json", "timeline.json", "assets.json" };

            if (filesShouldExist.Any((f) => !File.Exists(Path.Combine(workingPath, f))))
            {
                throw new FileNotFoundException("One or more required files are missing.", workingPath);
            }

            var project = JsonSerializer.Deserialize<ProjectJSONStructure>(File.ReadAllText(Path.Combine(workingPath, "project.json")));

            ProjectInfo = JsonSerializer.Deserialize<ProjectJSONStructure>(File.ReadAllText(Path.Combine(workingPath, "project.json"))) ?? new();
            var assets = JsonSerializer.Deserialize<ObservableCollection<AssetItem>>(File.ReadAllText(Path.Combine(workingPath, "assets.json"))) ?? new();
            Title = ProjectInfo.projectName;
            ImportDraft(File.ReadAllText(Path.Combine(workingPath, "timeline.json")));

            SetStateOK();
            Loaded += async (sender, e) =>
            {
                await BootRPC(npc);
                await UpdateClipToBackend();

            };





        }

#endif
        protected override void OnNavigatedTo(NavigatedToEventArgs args)
        {
            base.OnNavigatedTo(args);
            SubscribeToTimelineChanges(reset: true);
        }

        protected override void OnNavigatedFrom(NavigatedFromEventArgs args)
        {
            base.OnNavigatedFrom(args);
            UnsubscribeAllTimelineChanges();

#if WINDOWS
            try
            {
                _rpc.ShutdownAsync();

                rpcProc.Kill();

            }
            catch
            {

            }
#endif
        }

        #endregion

        #region backend

#if WINDOWS
        private RpcClient? _rpc;
        private Process rpcProc;
        public bool VerboseBackendLog { get; private set; } = false;


        private async Task BootRPC()
        {
            var pipeId = "pjfc_rpc_V1_" + Guid.NewGuid().ToString();
            var tmpDir = Path.Combine(Path.GetTempPath(), "pjfc_temp");
            Directory.CreateDirectory(tmpDir);
            rpcProc = new Process
            {
                StartInfo = new ProcessStartInfo
                {
                    FileName = Path.Combine(AppContext.BaseDirectory, "projectFrameCut.Render.WindowsRender.exe"),
                    WorkingDirectory = Path.Combine(AppContext.BaseDirectory),
                    Arguments = $" rpc_backend  -pipe={pipeId} -output_options=3840,2160,42,AV_PIX_FMT_NONE,nope -tempFolder={tmpDir} ",
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    RedirectStandardError = true,
                    RedirectStandardOutput = true,
                }
            };
            if (VerboseBackendLog)
            {
                rpcProc.StartInfo.RedirectStandardError = false;
                rpcProc.StartInfo.RedirectStandardOutput = false;
                rpcProc.StartInfo.CreateNoWindow = false;
                rpcProc.StartInfo.UseShellExecute = true;
            }

            Log($"Starting RPC backend with pipe ID: {pipeId}, args:{rpcProc.StartInfo.Arguments}");

            rpcProc.OutputDataReceived += (sender, e) =>
            {
                if (e.Data != null)
                {
                    Log(e.Data, "Backend_STDOUT");

                }
            };

            rpcProc.ErrorDataReceived += (sender, e) =>
            {
                if (e.Data != null)
                {
                    Log(e.Data, "Backend_STDERR");
                    Dispatcher.Dispatch(() =>
                    {
                        _backendStatus.Text = e.Data;
                        _backendStatus.TextColor = Colors.Red;
                    });
                }
            };

            rpcProc.EnableRaisingEvents = true;
            rpcProc.Exited += (s, e) =>
            {
                Dispatcher.Dispatch(async () =>
                {
                    await DisplayAlert("Error", $"The backend exited unexpectedly with code {rpcProc.ExitCode}. Please reload project.", "ok");
                });
            };
            rpcProc.Start();
            if (!VerboseBackendLog)
            {
                rpcProc.BeginErrorReadLine();
                rpcProc.BeginOutputReadLine();
            }

            Thread.Sleep(500);
            _rpc = new RpcClient();
            await _rpc.StartAsync(pipeId);
            Log("RPC started");


            if (_backendStatus is not null)
                _backendStatus.Text = "Waiting for backend...";


            new Thread(RpcListenerThread)
            { CurrentCulture = CultureInfo.InvariantCulture }.Start();


        }

#if DEBUG
        private async Task BootRPC(NamedPipeClientStream npc)
        {
            var tmpDir = Path.Combine(Path.GetTempPath(), "pjfc_temp");
            Directory.CreateDirectory(tmpDir);


            Thread.Sleep(500);
            _rpc = new RpcClient();
            await _rpc.StartAsync(npc);
            Log("RPC started");

            BackendStateIndicator.Fill = Colors.Gray;

            if (_backendStatus is not null)
                _backendStatus.Text = "You are debugging backend.";


            //new Thread(RpcListenerThread)
            //{ CurrentCulture = CultureInfo.InvariantCulture }.Start();


        }

#endif

        async void RpcListenerThread()
        {
            Thread.Sleep(250);
            int rpcFailedCount = 0;
            float menUsed, menTotalUsed;
            JsonElement? state;
            TimeSpan lantency;
            Color color, textColor;
            string message;
            var nothing = JsonSerializer.SerializeToElement<object?>(null);
            CancellationTokenSource cts = new();

            while (!rpcProc.HasExited)
            {
                menUsed = rpcProc.WorkingSet64 / 1024f / 1024f;
                menTotalUsed = MemoryHelper.GetUsedRAM() / 1024f / 1024f;

                cts = new();
                cts.CancelAfter(5000);
                try
                {
                    state = await _rpc.SendAsync("ping", nothing, cts.Token);
                    lantency = DateTime.Now - state.Value.GetProperty("value").GetDateTime();
                    color = lantency.TotalMilliseconds switch
                    {
                        < 200 => Colors.Green,
                        < 500 => Colors.Orange,
                        _ => Colors.Red
                    };
                    textColor = Colors.White;
                    message = Localized.RenderPage_BackendStatus(lantency.TotalMilliseconds, menUsed, menTotalUsed);

                }
                catch
                {
                    rpcFailedCount++;
                    state = nothing;
                    color = Colors.Red;
                    textColor = Colors.Red;
                    message = Localized.RenderPage_BackendStatus_NotRespond(menUsed, menTotalUsed);
                }

                if (_backendStatus is not null)
                    Dispatcher.Dispatch(() =>
                    {
                        _backendStatus.Text = message;
                        _backendStatus.TextColor = textColor;
                        BackendStateIndicator.Fill = color;
                    });

                if (rpcFailedCount > 5)
                {
                    if (_backendStatus is not null)
                        Dispatcher.Dispatch(async () =>
                        {
                            BackendStateIndicator.Fill = Colors.Gray;

                            _backendStatus.Text = "Backend not respond. Please try reload project...";

                            await DisplayAlert("Error", "Backend not respond in 10 seconds. Please try reload project", "ok");
                        });
                    rpcProc.Kill();

                }
                Thread.Sleep(2500);
            }
        }

        private async Task<bool> UpdateClipToBackend()
        {
            {
                CancellationTokenSource cts = new();
                if (_rpc is null) return false;
                WorkingState = $"applying changes to backend... ";
                try
                {
                    var element = JsonSerializer.SerializeToElement(await BuildDraft(ProjectInfo.projectName));
                    cts.CancelAfter(10000);

                    _ = await _rpc.SendAsync("UpdateClips", element, cts.Token);
                    WorkingState = Localized.RenderPage_ChangesApplied;
                    return true;
                }
                catch (Exception ex)
                {
                    if (_status is not null)
                    {
                        WorkingState = $"failed to applying changes to backend... ";
                        _status.TextColor = Colors.Red;
                    }
                    SetStateFail();

                    await DisplayAlert($"{ex.GetType()} Error", ex.Message, "ok");
                    return false;

                }
                //if (result is null) return;
                //var path = result.Value.GetProperty("path").GetString();
                //PreviewBox.Source = ImageSource.FromFile(path);
            }
        }
#endif
        #endregion

        #region status
        public void SetStateBusy()
        {
            if (StateIndicator is null) return;
            StateIndicator.Children.Clear();
            StateIndicator.Children.Add(new ActivityIndicator
            {
                Color = Colors.Orange,
                IsRunning = true,
                WidthRequest = 16,
                HeightRequest = 16,
                Margin = new(0, 0, 0, 0)
            });
        }

        public void SetStateOK()
        {
            if (StateIndicator is null) return;

            StateIndicator.Children.Clear();
            StateIndicator.Children.Add(new Microsoft.Maui.Controls.Shapes.Path
            {
                Stroke = Colors.Green,
                StrokeThickness = 3,
                Data = (Geometry)new PathGeometryConverter().ConvertFromInvariantString("M 4,12 L 9,17 L 20,6"),
                WidthRequest = 20,
                HeightRequest = 20,
                Margin = new Thickness(0, -3, 0, 0)


            });
        }

        public void SetStateFail()
        {
            if (StateIndicator is null) return;

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
        }

        void MemoryUsageListener()
        {
            float menUsed;
            while (true)
            {
                menUsed = Environment.WorkingSet / 1024f / 1024f;
                Dispatcher.Dispatch(() =>
                {
                    _backendStatus.Text = Localized.RenderPage_BackendStatus_MemoryOnly(menUsed);
                });
                Thread.Sleep(2000);
            }
        }

        #endregion

        #region subscribe to timeline changes

        private void SubscribeToTimelineChanges(bool reset)
        {
            if (reset)
                UnsubscribeAllTimelineChanges();

            // 订阅 Timeline 自身属性变更
            _vm.PropertyChanged -= OnTimelinePropertyChanged;
            _vm.PropertyChanged += OnTimelinePropertyChanged;

            // 订阅 Tracks 集合变更
            _vm.Tracks.CollectionChanged -= OnTracksChanged;
            _vm.Tracks.CollectionChanged += OnTracksChanged;

            // 订阅当前已存在的轨道与片段
            foreach (var track in _vm.Tracks)
                AttachTrack(track);
        }

        private void UnsubscribeAllTimelineChanges()
        {
            _vm.PropertyChanged -= OnTimelinePropertyChanged;
            _vm.Tracks.CollectionChanged -= OnTracksChanged;

            foreach (var t in _subscribedTracks.ToArray())
                DetachTrack(t);
            _subscribedTracks.Clear();

            foreach (var c in _subscribedClips.ToArray())
                DetachClip(c);
            _subscribedClips.Clear();
        }

        private void OnTimelinePropertyChanged(object? sender, PropertyChangedEventArgs e)
        {
            // 例如 PixelsPerSecond、PlayheadSeconds、Snap 设置、TotalSeconds 等
            FireProjectChanged($"Timeline.{e.PropertyName}");
        }

        private void OnTracksChanged(object? sender, NotifyCollectionChangedEventArgs e)
        {
            if (e.Action is NotifyCollectionChangedAction.Add or NotifyCollectionChangedAction.Replace)
            {
                if (e.NewItems != null)
                    foreach (TrackViewModel t in e.NewItems)
                        AttachTrack(t);
            }
            if (e.Action is NotifyCollectionChangedAction.Remove or NotifyCollectionChangedAction.Replace)
            {
                if (e.OldItems != null)
                    foreach (TrackViewModel t in e.OldItems)
                        DetachTrack(t);
            }
            if (e.Action == NotifyCollectionChangedAction.Reset)
            {
                foreach (var t in _subscribedTracks.ToArray())
                    DetachTrack(t);
                _subscribedTracks.Clear();

                foreach (var t in _vm.Tracks)
                    AttachTrack(t);
            }

            FireProjectChanged("Tracks.CollectionChanged");
        }

        private void AttachTrack(TrackViewModel track)
        {
            if (!_subscribedTracks.Add(track))
                return;

            track.PropertyChanged -= OnTrackPropertyChanged;
            track.PropertyChanged += OnTrackPropertyChanged;

            track.Clips.CollectionChanged -= OnTrackClipsChanged;
            track.Clips.CollectionChanged += OnTrackClipsChanged;

            foreach (var clip in track.Clips)
                AttachClip(clip);
        }

        private void DetachTrack(TrackViewModel track)
        {
            if (!_subscribedTracks.Remove(track))
                return;

            track.PropertyChanged -= OnTrackPropertyChanged;
            track.Clips.CollectionChanged -= OnTrackClipsChanged;

            foreach (var clip in track.Clips.ToArray())
                DetachClip(clip);
        }

        private void OnTrackPropertyChanged(object? sender, PropertyChangedEventArgs e)
        {
            FireProjectChanged($"Track.{e.PropertyName}");
        }

        private void OnTrackClipsChanged(object? sender, NotifyCollectionChangedEventArgs e)
        {
            if (e.Action is NotifyCollectionChangedAction.Add or NotifyCollectionChangedAction.Replace)
            {
                if (e.NewItems != null)
                    foreach (ClipViewModel c in e.NewItems)
                        AttachClip(c);
            }
            if (e.Action is NotifyCollectionChangedAction.Remove or NotifyCollectionChangedAction.Replace)
            {
                if (e.OldItems != null)
                    foreach (ClipViewModel c in e.OldItems)
                        DetachClip(c);
            }
            if (e.Action == NotifyCollectionChangedAction.Reset && sender is TrackViewModel t)
            {
                foreach (var c in _subscribedClips.Where(c => t.Clips.Contains(c)).ToArray())
                    DetachClip(c);
                foreach (var c in t.Clips)
                    AttachClip(c);
            }

            FireProjectChanged("Clips.CollectionChanged");
        }

        private void AttachClip(ClipViewModel clip)
        {
            if (!_subscribedClips.Add(clip))
                return;

            clip.PropertyChanged -= OnClipPropertyChanged;
            clip.PropertyChanged += OnClipPropertyChanged;
        }

        private void DetachClip(ClipViewModel clip)
        {
            if (!_subscribedClips.Remove(clip))
                return;

            clip.PropertyChanged -= OnClipPropertyChanged;
        }

        private void OnClipPropertyChanged(object? sender, PropertyChangedEventArgs e)
        {
            FireProjectChanged($"Clip.{e.PropertyName}");
        }

        private void FireProjectChanged(string reason)
        {
            _changeDebounceCts?.Cancel();
            _changeDebounceCts = new CancellationTokenSource();
            var token = _changeDebounceCts.Token;

            _ = Task.Run(async () =>
            {
                try
                {
                    await Task.Delay(100, token);
                    if (token.IsCancellationRequested) return;

                    Dispatcher.Dispatch(() => OnProjectChanged(reason));
                }
                catch (TaskCanceledException) { }
            }, token);
        }


        #endregion

        #region import/export draft

        // Helper: determine clip type by file extension
        private static ClipMode DetermineClipMode(string? path)
        {
            if (string.IsNullOrWhiteSpace(path)) return ClipMode.Special;
            var ext = Path.GetExtension(path).ToLowerInvariant();
            // Common video extensions
            string[] video = [".mp4", ".mov", ".mkv", ".avi", ".webm", ".m4v"];
            string[] image = [".png", ".jpg", ".jpeg", ".webp", ".bmp", ".tiff"];
            if (video.Contains(ext)) return ClipMode.VideoClip;
            if (image.Contains(ext)) return ClipMode.PhotoClip;
            return ClipMode.Special; // fallback
        }

        private static uint GetRelativeStartFrameFromMetadata(Dictionary<string, object> metadata)
        {
            if (metadata == null) return 0;
            if (metadata.TryGetValue("RelativeStartFrame", out var v) && v is not null)
            {
                try
                {
                    return v switch
                    {
                        uint u => u,
                        int i => (uint)Math.Max(0, i),
                        long l => (uint)Math.Max(0, l),
                        double d => (uint)Math.Max(0, Math.Round(d)),
                        float f => (uint)Math.Max(0, Math.Round(f)),
                        string s when uint.TryParse(s, out var parsed) => parsed,
                        JsonElement je when je.ValueKind == JsonValueKind.Number && je.TryGetUInt32(out var parsedJe) => parsedJe,
                        _ => 0
                    };
                }
                catch { return 0; }
            }
            return 0;
        }


        // Export timeline to DraftStructureJSON
        private async Task<DraftStructureJSON> BuildDraft(string projectName = "Default Project")
        {
            const uint frameRate = 30; // global framerate assumption
            double frameTime = 1.0 / frameRate;
            var clipDtos = new List<ClipDraftDTO>();

            for (int trackIndex = 0; trackIndex < _vm.Tracks.Count; trackIndex++)
            {
                var track = _vm.Tracks[trackIndex];
                foreach (var clip in track.Clips.OrderBy(c => c.StartSeconds))
                {
                    uint startFrame = (uint)Math.Round(clip.StartSeconds * frameRate);
                    uint durationFrames = (uint)Math.Max(1, Math.Round(clip.DurationSeconds * frameRate));
                    var clipType = DetermineClipMode(clip.SourcePath);
                    var relativeStartFrame = clip.RelativeStartFrame;
                    clipDtos.Add(new ClipDraftDTO
                    {
                        Id = clip.Id,
                        Name = clip.Name,
                        ClipType = clipType,
                        LayerIndex = (uint)trackIndex,
                        StartFrame = startFrame,
                        RelativeStartFrame = relativeStartFrame,
                        Duration = durationFrames,
                        FrameTime = (float)frameTime,
                        MixtureMode = RenderMode.Overlay,
                        FilePath = clip.SourcePath,
                        MetaData = clip.Metadata,
                    });
                }
            }

            ConcurrentBag<JsonElement> elements = new();

            List<Task> serializers = new();

            foreach (var item in clipDtos)
            {
                serializers.Add(Task.Run(() =>
                {
                    elements.Add(JsonSerializer.SerializeToElement(item));
                }));
            }

            await Task.WhenAll(serializers);

            return new DraftStructureJSON
            {
                Name = projectName,
                Clips = clipDtos.Cast<object>().ToArray()
            };
        }

        private async Task<string> ExportDraftToJson()
        {
            var draft = await BuildDraft(ProjectInfo.projectName);
            var opts = new JsonSerializerOptions { WriteIndented = true };
            return JsonSerializer.Serialize(draft, opts);
        }

        private async void OnExportDraft(object sender, EventArgs e)
        {
            try
            {
                var json = await ExportDraftToJson();
                //Log(json);
                await DisplayAlert("Draft Export", json, "OK");
            }
            catch (Exception ex)
            {
                Log(ex);
                await DisplayAlert("Error", ex.Message, "OK");
            }
        }

        // Import DraftStructureJSON and rebuild all tracks/clips
        private async void ImportDraft(string src)
        {
            try
            {
                var opts = new JsonSerializerOptions
                {
                    PropertyNameCaseInsensitive = true
                };
                var draft = JsonSerializer.Deserialize<DraftStructureJSON>(src, opts);
                if (draft == null)
                {
                    await DisplayAlert("错误", "解析 DraftStructureJSON 失败", "OK");
                    return;
                }

                ImportDraftToTimeline(draft);
            }
            catch (Exception ex)
            {
                Log(ex);
                await DisplayAlert("Error", ex.Message, "OK");
            }
        }

        public async void ImportDraftToTimeline(DraftStructureJSON draft)
        {
            // Reset tracks
            _vm.Tracks.Clear();

            // collect DTOs
            var dtos = new List<ClipDraftDTO>();
            foreach (var obj in draft.Clips ?? Array.Empty<object>())
            {
                switch (obj)
                {
                    case JsonElement je:
                        try
                        {
                            var dto = je.Deserialize<ClipDraftDTO>(new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
                            if (dto != null) dtos.Add(dto);
                        }
                        catch { /* ignore bad clip */ }
                        break;
                    case ClipDraftDTO dto:
                        dtos.Add(dto);
                        break;
                }
            }

            // Ensure enough tracks by LayerIndex
            int trackCount = dtos.Count == 0 ? 1 : (int)(dtos.Max(c => (int)c.LayerIndex) + 1);
            for (int i = 0; i < trackCount; i++)
                _vm.AddTrack(Localized.RenderPage_Track(i + 1));

            // frame time: prefer clip's FrameTime or draft.targetFrameRate
            double frameTime = 0;
            if (dtos.Any() && dtos.All(c => c.FrameTime > 0))
                frameTime = dtos.First().FrameTime;
            else if (draft.targetFrameRate > 0)
                frameTime = 1.0 / draft.targetFrameRate;
            else
                frameTime = 1.0 / 60.0;

            double maxEndSeconds = 0;
            ConcurrentDictionary<projectFrameCut.Shared.ClipMode, int> clipTypeCount = new();
            foreach (var c in dtos.OrderBy(c => c.LayerIndex).ThenBy(c => c.StartFrame))
            {
                var startSeconds = c.StartFrame * frameTime;
                var durationSeconds = Math.Max(0.05, c.Duration * frameTime);

                var clip = new ClipViewModel
                {
                    Id = string.IsNullOrWhiteSpace(c.Id) ? Guid.NewGuid().ToString() : c.Id,
                    Name = string.IsNullOrWhiteSpace(c.Name) ? Path.GetFileName(c.FilePath ?? "Clip") : c.Name,
                    StartSeconds = startSeconds,
                    DurationSeconds = durationSeconds,
                    SourcePath = c.FilePath,
                    RelativeStartFrame = c.RelativeStartFrame,
                    Type = c.ClipType,
                    Metadata = c.MetaData ?? new Dictionary<string, object>()
                };

                int idx = (int)Math.Clamp(c.LayerIndex, 0, Math.Max(0, _vm.Tracks.Count - 1));
                var track = _vm.Tracks[idx];
                // resolve overlaps within the track to be safe
                clip.StartSeconds = ResolveOverlapStart(track, clip, clip.StartSeconds, clip.DurationSeconds);
                track.Clips.Add(clip);
                maxEndSeconds = Math.Max(maxEndSeconds, clip.StartSeconds + clip.DurationSeconds);

                Assets.Add(new AssetItem
                {
                    Name = !string.IsNullOrEmpty(c.FilePath) ? Path.GetFileName(c.FilePath) ?? $"unknown draft@{c.Id}" : $"{c.ClipType} #{clipTypeCount.GetOrAdd(c.ClipType, 0)}",
                    Type = c.ClipType,
                    Path = c.FilePath
                });

                clipTypeCount.AddOrUpdate(c.ClipType, 1, (k, v) => v + 1);
            }

            // Expand timeline length to fit imported content with some padding
            _vm.TotalSeconds = Math.Max(60, Math.Ceiling(maxEndSeconds + 2));

            await RebuildTracksUI();
            UpdatePlayhead();
            BuildRuler();
        }

        #endregion

        #region build UI
        private async Task RebuildTracksUI()
        {
            if (_tracksHeader == null || _tracksPanel == null) return;
            SetStateBusy();
            WorkingState = Localized.RenderPage_Processing;
            await Task.Delay(45);
            _tracksHeader.Children.Clear();
            _tracksPanel.Children.Clear();
            _clipToView.Clear();

            foreach (var track in _vm.Tracks)
            {
                var header = new Grid { HeightRequest = 48, BackgroundColor = Colors.Gray };
                header.Add(new Label { Text = track.Name, TextColor = Colors.White, Padding = new Thickness(8, 0) });
                _tracksHeader.Children.Add(header);

                // Use AbsoluteLayout for precise positioning of clips
                var lane = new AbsoluteLayout { HeightRequest = TrackHeight, BackgroundColor = Color.FromArgb("#222") };
                // background tap to clear selection
                var tap = new TapGestureRecognizer();
                tap.Tapped += (s, e) => SelectClip(null);
                lane.GestureRecognizers.Add(tap);

                // Add clip views
                foreach (var clip in track.Clips)
                {
                    var view = await CreateClipView(clip);
                    Dispatcher.Dispatch(() =>
                    {
                        lane.Children.Add(view);
                        _clipToView[clip] = view;
                        UpdateClipLayout(view, clip);
                    });
                }

                _tracksPanel.Children.Add(lane);
            }

            BuildRuler();
            SyncRulerScroll();
            SetStateOK();
        }

        private void BuildRuler()
        {
            if (_ruler == null) return;
            _ruler.Children.Clear();
            var totalSeconds = (int)Math.Ceiling(_vm.TotalSeconds);
            var pps = _vm.PixelsPerSecond;

            for (int s = 0; s <= totalSeconds; s++)
            {
                var x = s * pps;
                var tick = new BoxView { Color = Colors.LightGray, WidthRequest = 1, HeightRequest = 12 };
                AbsoluteLayout.SetLayoutBounds(tick, new Rect(x, 0, 1, 12));
                AbsoluteLayout.SetLayoutFlags(tick, AbsoluteLayoutFlags.None);
                _ruler.Children.Add(tick);

                var label = new Label { TextColor = Colors.White, FontSize = 10, Text = s.ToString() };
                AbsoluteLayout.SetLayoutBounds(label, new Rect(x + 2, 0, 30, 12));
                AbsoluteLayout.SetLayoutFlags(label, AbsoluteLayoutFlags.None);
                _ruler.Children.Add(label);

                if (s < totalSeconds)
                {
                    for (int i = 1; i < 5; i++)
                    {
                        var subx = x + i * (pps / 5);
                        var subtick = new BoxView { Color = Colors.Gray, WidthRequest = 1, HeightRequest = 8 };
                        AbsoluteLayout.SetLayoutBounds(subtick, new Rect(subx, 0, 1, 8));
                        AbsoluteLayout.SetLayoutFlags(subtick, AbsoluteLayoutFlags.None);
                        _ruler.Children.Add(subtick);
                    }
                }
            }
        }

        private void SyncRulerScroll()
        {
            if (_ruler == null || _timelineScroll == null) return;
            var x = _timelineScroll.ScrollX;
            foreach (var child in _ruler.Children)
            {
                if (child is VisualElement ve)
                {
                    ve.TranslationX = -x;
                }
            }
        }

        private void RepositionClips()
        {
            foreach (var kv in _clipToView)
                UpdateClipLayout(kv.Value, kv.Key);
            UpdatePlayhead();
        }
        #endregion

        #region clip 

        private async Task<Grid> CreateClipView(ClipViewModel clip)
        {
            return await Task.Run(() =>
            {
                var g = new Grid
                {
                    BackgroundColor = Color.FromArgb("#4466AA"),
                    HeightRequest = 44,
                    Margin = new Thickness(0, 2),
                    HorizontalOptions = LayoutOptions.Start,
                };

                // Content label
                var label = new Label { Text = clip.Name, TextColor = Colors.White, Padding = new Thickness(8, 0) };
                g.Add(label);

                // Center move overlay to avoid gesture competition with handles
                var centerOverlay = new BoxView
                {
                    BackgroundColor = Colors.Transparent,
                    HorizontalOptions = LayoutOptions.Fill,
                    VerticalOptions = LayoutOptions.Fill,
                    InputTransparent = false
                };
                g.Add(centerOverlay);

#if WINDOWS
                // Attach native context menu for right-click
                AttachContextMenu(g, clip);
                AttachContextMenu(centerOverlay, clip);
#endif

                // Initial layout
                UpdateClipLayout(g, clip);

                // Move pan
                var movePan = new PanGestureRecognizer();
                movePan.PanUpdated += (s, e) =>
                {
                    if (e.StatusType == GestureStatus.Started)
                    {
                        SelectClip(clip);
                        _dragStartX = e.TotalX;
                        _dragStartSeconds = clip.StartSeconds;
                        _dragStartTrackIndex = GetTrackIndexOfClip(clip);
                        _currentDragTrackIndex = _dragStartTrackIndex;
                    }
                    else if (e.StatusType == GestureStatus.Running && !_isResizingLeft && !_isResizingRight)
                    {
                        // horizontal move with snapping/overlap-resolve
                        ApplyMove(clip, g, _dragStartSeconds, e.TotalX - _dragStartX);

                        // cross-track move with pre-check to prevent overlap
                        if (_tracksPanel != null && _vm.Tracks.Count > 0 && _dragStartTrackIndex >= 0)
                        {
                            int offsetTracks = (int)Math.Round(e.TotalY / TrackHeight);
                            int newIndex = Math.Clamp(_dragStartTrackIndex + offsetTracks, 0, _vm.Tracks.Count - 1);
                            if (newIndex != _currentDragTrackIndex)
                            {
                                var targetTrack = _vm.Tracks[newIndex];
                                var candidateStart = clip.StartSeconds;
                                var candidateDuration = clip.DurationSeconds;
                                if (!WouldCauseOverlapOnTrack(targetTrack, clip, candidateStart, candidateDuration))
                                {
                                    // Move visual
                                    if (g.Parent is AbsoluteLayout oldLane && newIndex < _tracksPanel.Children.Count && _tracksPanel.Children[newIndex] is AbsoluteLayout newLane)
                                    {
                                        oldLane.Children.Remove(g);
                                        newLane.Children.Add(g);
                                    }
                                    // Update model
                                    var oldTrack = _vm.Tracks[_currentDragTrackIndex];
                                    if (oldTrack.Clips.Contains(clip)) oldTrack.Clips.Remove(clip);
                                    if (!targetTrack.Clips.Contains(clip)) targetTrack.Clips.Add(clip);
                                    _currentDragTrackIndex = newIndex;
                                }
                                else
                                {
                                    WorkingState = Localized.RenderPage_CannotMoveBecauseOfOverlap;
                                }
                            }
                        }
                    }
                    else if (e.StatusType == GestureStatus.Completed || e.StatusType == GestureStatus.Canceled)
                    {
                        _isResizingLeft = _isResizingRight = false;
                        UpdateClipLayout(g, clip);
                        SelectClip(clip);
                    }
                };
                centerOverlay.GestureRecognizers.Add(movePan);

                // Resize handles
                var leftHandle = new BoxView { WidthRequest = 14, HorizontalOptions = LayoutOptions.Start, VerticalOptions = LayoutOptions.Fill, BackgroundColor = Color.FromArgb("#55FFFFFF") };
                var rightHandle = new BoxView { WidthRequest = 14, HorizontalOptions = LayoutOptions.End, VerticalOptions = LayoutOptions.Fill, BackgroundColor = Color.FromArgb("#55FFFFFF") };
                g.Add(leftHandle);
                g.Add(rightHandle);

#if WINDOWS
                AttachContextMenu(leftHandle, clip);
                AttachContextMenu(rightHandle, clip);
#endif

                var leftPan = new PanGestureRecognizer();
                leftPan.PanUpdated += (s, e) =>
                {
                    if (e.StatusType == GestureStatus.Started)
                    {
                        SelectClip(clip);
                        _isResizingLeft = true;
                        _dragStartX = e.TotalX;
                        _resizeInitialStart = clip.StartSeconds;
                        _resizeInitialDuration = clip.DurationSeconds;
                    }
                    else if (e.StatusType == GestureStatus.Running && _isResizingLeft)
                    {
                        ApplyResizeLeft(clip, g, _resizeInitialStart, _resizeInitialDuration, e.TotalX - _dragStartX);
                    }
                    else if (e.StatusType == GestureStatus.Completed || e.StatusType == GestureStatus.Canceled)
                    {
                        _isResizingLeft = false;
                        UpdateClipLayout(g, clip);
                    }
                };
                leftHandle.GestureRecognizers.Add(leftPan);

                var rightPan = new PanGestureRecognizer();
                rightPan.PanUpdated += (s, e) =>
                {
                    if (e.StatusType == GestureStatus.Started)
                    {
                        SelectClip(clip);
                        _isResizingRight = true;
                        _dragStartX = e.TotalX;
                        _resizeInitialDuration = clip.DurationSeconds;
                    }
                    else if (e.StatusType == GestureStatus.Running && _isResizingRight)
                    {
                        ApplyResizeRight(clip, g, _resizeInitialDuration, e.TotalX - _dragStartX);
                    }
                    else if (e.StatusType == GestureStatus.Completed || e.StatusType == GestureStatus.Canceled)
                    {
                        _isResizingRight = false;
                        UpdateClipLayout(g, clip);
                    }
                };
                rightHandle.GestureRecognizers.Add(rightPan);

                // Tap to select
                var tap = new TapGestureRecognizer();
                tap.Tapped += (s, e) => SelectClip(clip);
                g.GestureRecognizers.Add(tap);

                return g;
            });
        }

#if WINDOWS
        private void AttachContextMenu(VisualElement element, ClipViewModel clip)
        {
            void Attach()
            {
                if (element?.Handler?.PlatformView is not Microsoft.UI.Xaml.FrameworkElement fe)
                    return;

                // Avoid multiple attachments
                fe.RightTapped -= OnRightTapped;
                fe.RightTapped += OnRightTapped;

                void OnRightTapped(object? sender, Microsoft.UI.Xaml.Input.RightTappedRoutedEventArgs e)
                {
                    SelectClip(clip);
                    var menu = BuildWindowsMenu(clip);
                    var pos = e.GetPosition(fe);
                    var opts = new Microsoft.UI.Xaml.Controls.Primitives.FlyoutShowOptions
                    {
                        Position = pos
                    };
                    menu.ShowAt(fe, opts);
                    e.Handled = true;
                }
            }

            if (element.Handler != null)
                Attach();

            element.HandlerChanged += (s, e) => Attach();
        }

        private Microsoft.UI.Xaml.Controls.MenuFlyout BuildWindowsMenu(ClipViewModel clip)
        {
            var menu = new Microsoft.UI.Xaml.Controls.MenuFlyout();
            var itemSplit = new Microsoft.UI.Xaml.Controls.MenuFlyoutItem { Text = "spilt" };
            itemSplit.Click += (s, e) => Dispatcher.Dispatch(() => { SelectClip(clip); OnSplitClip(s!, EventArgs.Empty); });
            var itemDelete = new Microsoft.UI.Xaml.Controls.MenuFlyoutItem { Text = "delete" };
            itemDelete.Click += (s, e) => Dispatcher.Dispatch(() => { SelectClip(clip); OnDeleteClip(s!, EventArgs.Empty); });
            menu.Items.Add(itemSplit);
            menu.Items.Add(new Microsoft.UI.Xaml.Controls.MenuFlyoutSeparator());
            menu.Items.Add(itemDelete);
            return menu;
        }
#endif

        private void UpdateClipLayout(Grid g, ClipViewModel clip)
        {
            var left = clip.StartSeconds * _vm.PixelsPerSecond;
            var width = clip.DurationSeconds * _vm.PixelsPerSecond;

            g.TranslationX = left;
            g.WidthRequest = width;
        }

        private void SelectClip(ClipViewModel? clip)
        {
            if (_selectedClip == clip || _clipToView == null) return;
            _selectedClip = clip;


            if (clip is not null && _clipToView.TryGetValue(clip, out var view))
            {
                view.BackgroundColor = Color.FromArgb("#66AA44");
            }

            foreach (var kv in _clipToView.Where((v) => v.Key != clip))
                kv.Value.BackgroundColor = Color.FromArgb("#4466AA");

            if (_status != null)
                _status.Text = clip == null ? Localized.RenderPage_EverythingFine : Localized.RenderPage_Selected($"{clip.Name} ({clip.StartSeconds:0.00}s - {clip.EndSeconds:0.00}s)");
        }

        private async void OnAddClip(object sender, EventArgs e)
        {
            if (_vm.Tracks.Count == 0) _vm.AddTrack();
            var track = _vm.Tracks[Math.Max(0, _vm.Tracks.Count - 1)];
            string? pickedPath = null;
#if ANDROID || IOS || MACCATALYST || WINDOWS
            try
            {
                var result = await FilePicker.PickAsync(new PickOptions { PickerTitle = "选择素材文件 (可取消)" });
                if (result != null)
                    pickedPath = result.FullPath;
            }
            catch (Exception ex)
            {
                Log(ex);
            }
#endif
            var clip = new ClipViewModel
            {
                Name = pickedPath != null ? Path.GetFileName(pickedPath) : $"Clip {track.Clips.Count + 1}",
                StartSeconds = _vm.PlayheadSeconds,
                DurationSeconds = 3,
                SourcePath = pickedPath
            };
            track.Clips.Add(clip);
            clip.StartSeconds = ResolveOverlapStart(track, clip, clip.StartSeconds, clip.DurationSeconds);
            await RebuildTracksUI();
        }

        private async void OnSplitClip(object sender, EventArgs e)
        {
            if (_selectedClip == null) return;
            var t = FindTrack(_selectedClip);
            if (t == null) return;
            var cutPos = _vm.PlayheadSeconds;
            if (cutPos <= _selectedClip.StartSeconds || cutPos >= _selectedClip.EndSeconds) return;

            var leftDur = cutPos - _selectedClip.StartSeconds;
            var rightDur = _selectedClip.EndSeconds - cutPos;

            // compute frames for in-point accumulation
            uint baseInPoint = _selectedClip.RelativeStartFrame;
            uint leftDurFrames = (uint)Math.Max(0, Math.Round(leftDur * frameRate));
            uint newRightInPoint = baseInPoint + leftDurFrames;

            var right = new ClipViewModel
            {
                Name = _selectedClip.Name + " (2)",
                StartSeconds = cutPos,
                DurationSeconds = rightDur,
                SourcePath = _selectedClip.SourcePath,
                RelativeStartFrame = newRightInPoint,
                Type = _selectedClip.Type,
                Metadata = new Dictionary<string, object>(_selectedClip.Metadata)
            };
            _selectedClip.DurationSeconds = leftDur;
            // left keeps its original in-point (_selectedClip.RelativeStartFrame remains unchanged)
            t.Clips.Add(right);
            await RebuildTracksUI();
        }

        private async void OnDeleteClip(object sender, EventArgs e)
        {
            if (_selectedClip == null) return;
            var t = FindTrack(_selectedClip);
            if (t == null) return;
            t.Clips.Remove(_selectedClip);
            _selectedClip = null;
            await RebuildTracksUI();
        }

        private int GetTrackIndexOfClip(ClipViewModel clip)
        {
            for (int i = 0; i < _vm.Tracks.Count; i++)
            {
                if (_vm.Tracks[i].Clips.Contains(clip))
                    return i;
            }
            return -1;
        }

        #endregion

        #region add clip
        public async void OnAddAssetToTimeline(object sender, EventArgs e)
        {
            if (sender is Button btn && btn.CommandParameter is AssetItem asset)
            {
                if (_vm.Tracks.Count == 0)
                    _vm.AddTrack("Video #1");
                var track = _vm.Tracks[0]; // always first track for now
                var clip = new ClipViewModel
                {
                    Name = asset.Name,
                    StartSeconds = _vm.PlayheadSeconds,
                    DurationSeconds = 3,
                    SourcePath = asset.Path // capture path so it can be exported
                };
                track.Clips.Add(clip);
                clip.StartSeconds = ResolveOverlapStart(track, clip, clip.StartSeconds, clip.DurationSeconds);
                await RebuildTracksUI();
            }
        }

        private bool WouldCauseOverlapOnTrack(TrackViewModel track, ClipViewModel self, double start, double duration)
        {
            var end = start + duration;
            return GetOverlappingClips(track, self, start, end).Any();
        }

        private async void OnAddTrack(object sender, EventArgs e)
        {
            _vm.AddTrack();
            await RebuildTracksUI();
        }

        #endregion

        #region move clip
        private double SnapSeconds(double seconds, ClipViewModel self)
        {
            if (!_vm.SnapEnabled) return Math.Max(0, seconds);
            double best = seconds;
            double bestDist = _vm.SnapThresholdSeconds + 1; // larger than threshold

            // 1) grid
            if (_vm.SnapGridSeconds > 0)
            {
                var grid = Math.Round(seconds / _vm.SnapGridSeconds) * _vm.SnapGridSeconds;
                var d = Math.Abs(grid - seconds);
                if (d < bestDist && d <= _vm.SnapThresholdSeconds)
                {
                    best = grid; bestDist = d;
                }
            }

            // 2) playhead
            if (_vm.SnapToPlayhead)
            {
                var d = Math.Abs(_vm.PlayheadSeconds - seconds);
                if (d < bestDist && d <= _vm.SnapThresholdSeconds)
                {
                    best = _vm.PlayheadSeconds; bestDist = d;
                }
            }

            // 3) other clips edges (all tracks)
            if (_vm.SnapToClips)
            {
                foreach (var t in _vm.Tracks)
                {
                    foreach (var c in t.Clips)
                    {
                        if (ReferenceEquals(c, self)) continue;
                        // start
                        var d1 = Math.Abs(c.StartSeconds - seconds);
                        if (d1 < bestDist && d1 <= _vm.SnapThresholdSeconds) { best = c.StartSeconds; bestDist = d1; }
                        // end
                        var d2 = Math.Abs(c.EndSeconds - seconds);
                        if (d2 < bestDist && d2 <= _vm.SnapThresholdSeconds) { best = c.EndSeconds; bestDist = d2; }
                    }
                }
            }

            return Math.Max(0, best);
        }

        private (double snappedStart, double snappedEnd) SnapRange(double start, double end, ClipViewModel self)
        {
            var s = SnapSeconds(start, self);
            var e = SnapSeconds(end, self);
            return (s, e);
        }

        private double GetMaxDurationLimit(ClipViewModel clip)
        {
            // Timeline bound
            double timelineLimit = Math.Max(0.05, _vm.TotalSeconds - clip.StartSeconds);

            try
            {
                uint rel = clip.RelativeStartFrame;
                var totalFrames = clip.TotalFrameCount;

                if (totalFrames > 0 && totalFrames > rel)
                {
                    double assetLimit = (totalFrames - rel) / (double)frameRate;
                    return Math.Max(0.05, Math.Min(timelineLimit, assetLimit));
                }
            }
            catch { /* ignore parse */ }

            return timelineLimit; // fallback only timeline bound
        }

        private void ApplyMove(ClipViewModel clip, Grid view, double baseStartSeconds, double deltaPixels)
        {
            var candidate = Math.Max(0, baseStartSeconds + deltaPixels / _vm.PixelsPerSecond);
            var snapped = SnapSeconds(candidate, clip);

            var track = GetTrackForClip(clip);
            if (track != null)
            {
                var resolved = ResolveOverlapStart(track, clip, snapped, clip.DurationSeconds);
                // Clamp so that end doesn't go beyond timeline
                resolved = Math.Min(resolved, Math.Max(0, _vm.TotalSeconds - clip.DurationSeconds));
                // If still overlapping due to unsatisfiable space, resolved may land outside; verify
                var end = resolved + clip.DurationSeconds;
                if (!GetOverlappingClips(track, clip, resolved, end).Any())
                {
                    clip.StartSeconds = resolved;
                    UpdateClipLayout(view, clip);
                    return;
                }
                else
                {
                    WorkingState = Localized.RenderPage_CannotMoveBecauseOfOverlap;
                }
            }
            // fallback: set snapped without overlap resolution if no track
            clip.StartSeconds = Math.Min(snapped, Math.Max(0, _vm.TotalSeconds - clip.DurationSeconds));
            UpdateClipLayout(view, clip);
        }

        private void ApplyResizeLeft(ClipViewModel clip, Grid view, double baseStart, double baseDuration, double deltaPixels)
        {
            const double MinDur = 0.05;
            var initialEnd = baseStart + baseDuration; // right edge fixed during gesture
            var desiredStart = Math.Max(0, baseStart + deltaPixels / _vm.PixelsPerSecond);
            var snapStart = SnapSeconds(desiredStart, clip);

            // Calculate how much we're moving the start point (in seconds)
            var startDelta = snapStart - baseStart;
            // Convert to frames (negative when expanding left, positive when shrinking from left)
            int frameDelta = (int)Math.Round(startDelta * frameRate);

            // Calculate new RelativeStartFrame
            // When expanding left (startDelta < 0), we need to start from an earlier frame (subtract frames)
            // When shrinking from left (startDelta > 0), we start from a later frame (add frames)
            int newRelativeStart = (int)clip.RelativeStartFrame + frameDelta;
            
            // Ensure RelativeStartFrame doesn't go negative
            newRelativeStart = Math.Max(0, newRelativeStart);

            // Calculate the maximum available frames from the asset
            long totalFrames = clip.TotalFrameCount;
            double maxDur = double.PositiveInfinity;
            
            if (totalFrames > 0)
            {
                // Maximum duration is limited by: (totalFrames - newRelativeStart) / frameRate
                long availableFrames = totalFrames - newRelativeStart;
                if (availableFrames <= 0)
                {
                    // Cannot expand left beyond the start of the source material
                    return;
                }
                maxDur = availableFrames / (double)frameRate;
            }

            // Also respect timeline bound
            double timelineLimit = Math.Max(MinDur, initialEnd - 0); // can go back to timeline start
            maxDur = Math.Min(maxDur, timelineLimit);

            // Adjust snapStart if it would cause duration to exceed maxDur
            double minStartAllowed = Math.Max(0, initialEnd - maxDur);
            if (snapStart < minStartAllowed)
            {
                snapStart = minStartAllowed;
                // Recalculate frameDelta and newRelativeStart with the clamped value
                startDelta = snapStart - baseStart;
                frameDelta = (int)Math.Round(startDelta * frameRate);
                newRelativeStart = (int)clip.RelativeStartFrame + frameDelta;
                newRelativeStart = Math.Max(0, newRelativeStart);
            }

            var track = GetTrackForClip(clip);
            double newStart = snapStart;
            if (track != null)
            {
                // Resolve overlap using fixed end (duration changes)
                var durationFromStart = Math.Max(MinDur, initialEnd - snapStart);
                var resolvedStart = ResolveOverlapStart(track, clip, snapStart, durationFromStart);
                
                // If overlap resolution changed the start, recalculate RelativeStartFrame
                if (Math.Abs(resolvedStart - snapStart) > 1e-6)
                {
                    startDelta = resolvedStart - baseStart;
                    frameDelta = (int)Math.Round(startDelta * frameRate);
                    newRelativeStart = (int)clip.RelativeStartFrame + frameDelta;
                    newRelativeStart = Math.Max(0, newRelativeStart);
                }
                
                newStart = Math.Clamp(resolvedStart, minStartAllowed, initialEnd - MinDur);
            }
            
            var newDuration = Math.Max(MinDur, initialEnd - newStart);
            
            // Final validation: ensure duration doesn't exceed what's available from newRelativeStart
            if (totalFrames > 0)
            {
                long availableFrames = totalFrames - newRelativeStart;
                double maxAvailableDur = availableFrames / (double)frameRate;
                if (newDuration > maxAvailableDur)
                {
                    newDuration = Math.Max(MinDur, maxAvailableDur);
                    newStart = initialEnd - newDuration;
                    
                    // Re-adjust RelativeStartFrame based on final newStart
                    startDelta = newStart - baseStart;
                    frameDelta = (int)Math.Round(startDelta * frameRate);
                    newRelativeStart = (int)clip.RelativeStartFrame + frameDelta;
                    newRelativeStart = Math.Max(0, newRelativeStart);
                }
            }

            clip.StartSeconds = newStart;
            clip.DurationSeconds = newDuration;
            clip.RelativeStartFrame = (uint)newRelativeStart;
            UpdateClipLayout(view, clip);
        }

        private void ApplyResizeRight(ClipViewModel clip, Grid view, double baseDuration, double deltaPixels)
        {
            const double MinDur = 0.05;
            var desiredEnd = clip.StartSeconds + Math.Max(MinDur, baseDuration + deltaPixels / _vm.PixelsPerSecond);
            var (_, snapEnd) = SnapRange(clip.StartSeconds, desiredEnd, clip);

            // Limit by track next clip and bounds
            var track = GetTrackForClip(clip);
            double limitedEnd = snapEnd;
            if (track != null)
            {
                limitedEnd = LimitEndToNextClip(track, clip, snapEnd);
                // also ensure no overlap remains
                var start = clip.StartSeconds;
                if (GetOverlappingClips(track, clip, start, limitedEnd).Any())
                {
                    // push end to the left to the nearest non-overlap (to next clip's start)
                    limitedEnd = LimitEndToNextClip(track, clip, start + MinDur);
                }
            }

            // Limit by timeline and optional asset maximum
            double maxDur = GetMaxDurationLimit(clip);
            double maxEnd = clip.StartSeconds + maxDur;
            limitedEnd = Math.Min(limitedEnd, maxEnd);
            limitedEnd = Math.Min(limitedEnd, _vm.TotalSeconds);

            var newDuration = Math.Max(MinDur, limitedEnd - clip.StartSeconds);
            // Finally clamp duration within [MinDur, maxDur]
            newDuration = Math.Clamp(newDuration, MinDur, maxDur);

            clip.DurationSeconds = newDuration;
            UpdateClipLayout(view, clip);
        }

        private TrackViewModel? GetTrackForClip(ClipViewModel clip)
            => _vm.Tracks.FirstOrDefault(t => t.Clips.Contains(clip));

        private static bool RangesOverlap(double aStart, double aEnd, double bStart, double bEnd)
            => Math.Max(aStart, bStart) < Math.Min(aEnd, bEnd);

        private IEnumerable<ClipViewModel> GetOverlappingClips(TrackViewModel track, ClipViewModel self, double start, double end)
            => track.Clips.Where(c => !ReferenceEquals(c, self) && RangesOverlap(start, end, c.StartSeconds, c.EndSeconds));

        private double ResolveOverlapStart(TrackViewModel track, ClipViewModel self, double snappedStart, double duration)
        {
            double start = Math.Max(0, snappedStart);
            const double eps = 1e-6;
            for (int i = 0; i < 16; i++)
            {
                var end = start + duration;
                var overlappers = GetOverlappingClips(track, self, start, end).ToList();
                if (overlappers.Count == 0) break;

                double rightCandidate = overlappers.Max(c => c.EndSeconds);
                double leftCandidate = overlappers.Min(c => c.StartSeconds) - duration;
                if (leftCandidate < 0) leftCandidate = 0;

                // If leftCandidate still overlaps, discard it
                bool leftOverlaps = GetOverlappingClips(track, self, leftCandidate, leftCandidate + duration).Any();

                // If we are already at leftCandidate (e.g., 0) or leftCandidate is invalid, move right
                if (leftOverlaps || Math.Abs(start - leftCandidate) < eps)
                {
                    start = rightCandidate;
                    continue;
                }

                // choose the smaller movement otherwise
                double moveRight = Math.Abs(rightCandidate - start);
                double moveLeft = Math.Abs(start - leftCandidate);
                start = moveRight <= moveLeft ? rightCandidate : leftCandidate;
            }
            return Math.Max(0, start);
        }

        private double LimitEndToNextClip(TrackViewModel track, ClipViewModel self, double snappedEnd)
        {
            double nextStart = double.PositiveInfinity;
            foreach (var c in track.Clips)
            {
                if (ReferenceEquals(c, self)) continue;
                if (c.StartSeconds >= self.StartSeconds)
                    nextStart = Math.Min(nextStart, c.StartSeconds);
            }
            if (double.IsPositiveInfinity(nextStart)) return snappedEnd;
            return Math.Min(snappedEnd, nextStart);
        }

        private void EnforceNoOverlapAfterTrackChange(ClipViewModel clip, Grid view)
        {
            var track = GetTrackForClip(clip);
            if (track == null) return;
            var resolved = ResolveOverlapStart(track, clip, clip.StartSeconds, clip.DurationSeconds);
            // also ensure the clip stays within timeline bounds
            resolved = Math.Min(resolved, Math.Max(0, _vm.TotalSeconds - clip.DurationSeconds));
            clip.StartSeconds = resolved;
            UpdateClipLayout(view, clip);
        }

        #endregion

        #region track UI stuffs

        private void UpdatePlayhead()
        {
            var x = _vm.PlayheadSeconds * _vm.PixelsPerSecond;
            if (_playhead != null)
                _playhead.TranslationX = x;
        }

        private TrackViewModel? FindTrack(ClipViewModel clip)
        {
            foreach (var t in _vm.Tracks)
                if (t.Clips.Contains(clip)) return t;
            return null;
        }

        private void OnZoomIn(object sender, EventArgs e)
        {
            _vm.PixelsPerSecond *= 1.25;
            BuildRuler();
            RepositionClips();
            SyncRulerScroll();
        }

        private void OnZoomOut(object sender, EventArgs e)
        {
            _vm.PixelsPerSecond /= 1.25;
            BuildRuler();
            RepositionClips();
            SyncRulerScroll();
        }

        private void OnTimelineScrolled(object? sender, ScrolledEventArgs e)
        {
            SyncRulerScroll();
        }

        private void OnRulerTapped(object sender, TappedEventArgs e)
        {
            if (_ruler == null) return;
            var p = e.GetPosition(_ruler);
            if (p is Point pt)
            {
                _vm.PlayheadSeconds = pt.X / _vm.PixelsPerSecond;
                UpdatePlayhead();
            }
        }

        #endregion
    }
}







































































































































































































































































