using FFmpeg.AutoGen;
using Microsoft.Maui.ApplicationModel;
using projectFrameCut.Shared;
using System;
using System.Collections.Concurrent;
using System.ComponentModel;
using System.Diagnostics;
using System.Runtime;
using System.Runtime.CompilerServices;
using System.Runtime.Intrinsics.X86;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;
using projectFrameCut.Setting.SettingManager;
using projectFrameCut.Render.RenderAPIBase.Project;
using projectFrameCut.Render.Videos;
using projectFrameCut.Render.Rendering;
using projectFrameCut.Render.RenderAPIBase.ClipAndTrack;
using projectFrameCut.Render.Plugin;





#if ANDROID
using projectFrameCut.Render.AndroidOpenGL;
using projectFrameCut.Render.AndroidOpenGL.Platforms.Android;
#endif

#if WINDOWS
using ILGPU;
#endif

namespace projectFrameCut;

public partial class RenderPage : ContentPage
{
    public string _workingPath;
    ProjectJSONStructure _project;
    uint _duration;

    public bool running;


    // 日志缓冲区
    private readonly StringBuilder _logBuffer = new StringBuilder();
    private readonly ConcurrentQueue<string> _logQueue = new ConcurrentQueue<string>();
    private System.Timers.Timer? _logUpdateTimer;
    private readonly SemaphoreSlim _logSemaphore = new SemaphoreSlim(1, 1);

#if WINDOWS
    Platforms.Windows.RenderHelper render = new projectFrameCut.Platforms.Windows.RenderHelper();
    Platforms.Windows.ffmpegHelper ffmpeg = new projectFrameCut.Platforms.Windows.ffmpegHelper();
#endif

    private CancellationTokenSource _cts = new CancellationTokenSource();

    public RenderPage()
    {
        InitializeComponent();
        var vmDefault = new RenderPageViewModel();
        try
        {
            vmDefault.Resoultion = SettingsManager.GetSetting("render_DefaultResolution", vmDefault.Resoultion);
            vmDefault.FramerateDisplay = SettingsManager.GetSetting("render_DefaultFramerate", vmDefault.FramerateDisplay);
            vmDefault.EncodingDisplay = SettingsManager.GetSetting("render_DefaultEncoding", vmDefault.EncodingDisplay);
            vmDefault.BitDepthDisplay = SettingsManager.GetSetting("render_DefaultBitDepth", vmDefault.BitDepthDisplay);
        }
        catch { }
        BindingContext = vmDefault;
        InitializeLogTimer();
    }

    public RenderPage(string path, uint projectDuration, ProjectJSONStructure projectInfo)
    {
        InitializeComponent();
        _workingPath = path;
        _duration = projectDuration;
        _project = projectInfo;
        Title = Localized.RenderPage_ExportTitle(projectInfo.projectName);

        var vm = new RenderPageViewModel();
        try
        {
            vm.Resoultion = SettingsManager.GetSetting("render_DefaultResolution", vm.Resoultion);
            vm.FramerateDisplay = SettingsManager.GetSetting("render_DefaultFramerate", vm.FramerateDisplay);
            vm.EncodingDisplay = SettingsManager.GetSetting("render_DefaultEncoding", vm.EncodingDisplay);
            vm.BitDepthDisplay = SettingsManager.GetSetting("render_DefaultBitDepth", vm.BitDepthDisplay);
        }
        catch { }
        BindingContext = vm;
        MaxParallelThreadsCount.Value = Environment.ProcessorCount * 2;
        MaxParallelThreadsCountLabel.Text = Localized.RenderPage_MaxParallelThreadsCount((int)MaxParallelThreadsCount.Value);
        CancelRender.IsEnabled = false;
        InitializeLogTimer();
    }
    private void InitializeLogTimer()
    {
        _logUpdateTimer = new System.Timers.Timer(800);
        _logUpdateTimer.Elapsed += async (s, e) => await FlushLogQueue();
        _logUpdateTimer.AutoReset = true;
    }

    private async Task FlushLogQueue()
    {
        if (_logQueue.IsEmpty) return;
        await _logSemaphore.WaitAsync();
        try
        {
            var batch = new StringBuilder();
            int count = 0;
            const int maxBatchSize = 50; // 每次最多处理 50 条日志

            while (count < maxBatchSize && _logQueue.TryDequeue(out var logEntry))
            {
                batch.AppendLine(logEntry);
                count++;
            }

            if (batch.Length > 0)
            {
                var batchText = batch.ToString();
                _logBuffer.Append(batchText);
                await Dispatcher.DispatchAsync(() =>
                {
                    LoggingBox.Text = _logBuffer.ToString();

                    //                    // 自动滚动到底部
                    //                    if (LoggingBox.Handler?.PlatformView != null)
                    //                    {
                    //#if WINDOWS
                    //                        if (LoggingBox.Handler.PlatformView is Microsoft.UI.Xaml.Controls.TextBox textBox)
                    //                        {
                    //                            textBox.Select(textBox.Text.Length, 0);
                    //                        }
                    //#endif
                    //                    }
                });
            }
        }
        finally
        {
            _logSemaphore.Release();
        }
    }


    protected override bool OnBackButtonPressed()
    {
        return running;
    }

    private async void ContentPage_Loaded(object sender, EventArgs e)
    {
        if (string.IsNullOrWhiteSpace(_workingPath) )
        {
            await DisplayAlert(Localized._Info, Localized.RenderPage_NoDraft, Localized._OK);
        }
    }
    #region rendering

    private async void StartRender_Clicked(object sender, EventArgs e)
    {
        RenderOptionPanel.IsVisible = false;
        PreviewLayout.IsVisible = true;
        ProgressBox.IsVisible = true;
        CancelRender.IsEnabled = true;
        //MoreOptions.IsEnabled = false;
        await SubProgress.ProgressTo(0, 250, Easing.Linear);
        await TotalProgress.ProgressTo(0, 250, Easing.Linear);

        _logBuffer.Clear();
        _logQueue.Clear();
        LoggingBox.Text = string.Empty;
        _logUpdateTimer?.Start();

        if (BindingContext is RenderPageViewModel vm)
        {
            running = true;
            DeviceDisplay.Current.KeepScreenOn = true;
            string outputPath;
            Log("Output options:\r\n" + vm.BuildSummary());
            var outputDir = Path.Combine(MauiProgram.DataPath, "RenderCache");
#if WINDOWS
            outputPath = await PickSavePath(_project.projectName);
#else
            outputPath = Path.Combine(outputDir, $"{_project.projectName}_{DateTime.Now:yyyyMMdd_HHmmss}.mp4");
#endif
            if (!OperatingSystem.IsWindows() || SettingsManager.IsBoolSettingTrue("UseLivePreviewInsteadOfBackend"))
            {
                Directory.CreateDirectory(outputDir);
                await DoComputeOnMobile(vm, outputPath);
            }
            else
            {
#if WINDOWS
                await DoComputeOnWindows(vm, render, ffmpeg, outputPath);
#endif
            }


            _logUpdateTimer?.Stop();
            await FlushLogQueue();
            await TotalProgress.ProgressTo(1, 50, Easing.Linear);


            await DisplayAlertAsync(Localized._Info, Localized.RenderPage_Done, Localized._OK);
            running = false;
            CancelRender.IsEnabled = false;
            DeviceDisplay.Current.KeepScreenOn = false;
        }
    }

    double totalProg = 0, lastProg = 0;

#if WINDOWS
    async Task DoComputeOnWindows(RenderPageViewModel vm, Platforms.Windows.RenderHelper render, Platforms.Windows.ffmpegHelper ffmpeg, string outputPath)
    {
        var fmt = vm.BitDepth switch
        {
            "8bit" => "AV_PIX_FMT_YUV420P",
            "10bit" => "AV_PIX_FMT_YUV420P10LE",
            "12bit" => "AV_PIX_FMT_YUV444P12LE",
            _ => "AV_PIX_FMT_GBRP16LE"
        };
        var enc = vm.BitDepth switch
        {
            "8bit" => "libx264",
            "10bit" => "libx265",
            "12bit" => "libx265",
            _ => "ffv1"
        };
        var ext = enc switch
        {
            "libx264" => "mp4",
            "libx265" => ".mov",
            "ffv1" => ".mkv",
            _ => ""
        };



        var outTempFile = Path.Combine(_workingPath, "export", $"output-{Guid.NewGuid()}.{ext}");
        var outPreview = Path.Combine(_workingPath, "export", $"preview-{Guid.NewGuid()}.png");
        Directory.CreateDirectory(Path.GetDirectoryName(outTempFile) ?? throw new NullReferenceException());
        var args = 
            $"render " +
            $"\"-draft={Path.Combine(_workingPath, "timeline.json")}\" " +
            $"-duration={_duration} " +
            $"\"-output={outTempFile}\" " +
            $"-output_options={vm.Width},{vm.Height},{vm.Framerate},{fmt},{enc}  " +
            $"-maxParallelThreads={(int)MaxParallelThreadsCount.Value} " +
            $"-preview=true " +
            $"\"-previewPath={outPreview}\" " +
            $"\"-Use16bpp={vm.BitDepth == "16"}\" ";
        if (SettingsManager.IsBoolSettingTrue("accel_enableMultiAccel"))
        {
            var accels = SettingsManager.GetSetting("accel_MultiDeviceID", "all");
            args += $" -multiAccelerator=true  \"-acceleratorDeviceIds={accels}\" ";
        }
        else
        {
            var accelId = SettingsManager.GetSetting("accel_DeviceId", "");
            if (int.TryParse(accelId, out var accelIdInt)) args += $" -multiAccelerator=false \"-acceleratorDeviceId={accelIdInt}\" ";
        }

        var userDefOptions = SettingsManager.GetSetting("render_UserDefinedOpts", "");
        if (!string.IsNullOrWhiteSpace(userDefOptions)) args += $"   {userDefOptions}   ";

        Log($"Args to render:{args}");

        long lastPreviewFileSize = 0;

        render.OnProgressChanged += (p) =>
        {
            try
            {
                if (File.Exists(outPreview) && lastPreviewFileSize != new FileInfo(outPreview).Length)
                {
                    lastPreviewFileSize = new FileInfo(outPreview).Length;
                    var src = ImageSource.FromFile(outPreview);
                    if (src is not null)
                    {
                        Dispatcher.Dispatch(() =>
                        {
                            PreviewImage.Source = src;
                        });
                    }

                }

            }
            catch (Exception)
            {
                // ignored
            }

            if (!double.IsNormal(p)) return;

            totalProg = p + lastProg;

            Dispatcher.Dispatch(async () =>
            {
                await SubProgress.ProgressTo(p, 250, Easing.Linear);
                await TotalProgress.ProgressTo(totalProg / 3d, 5, Easing.Linear);

            });
        };

        render.OnSubProgChanged += (s) =>
        {
            lastProg = totalProg;

            string label;
            try
            {
                label = Localized.DynamicLookup($"RenderPage_SubProg_{s}");
            }
            catch (Exception)
            {
                label = $"RenderPage_SubProg_{s}";
            }
            Dispatcher.Dispatch(() =>
            {
                SubProgLabel.Text = label;
            });
        };

        render.OnLog += _logQueue.Enqueue;

        var ret = await render.StartRender(args);
        Log($"Render process exited with code {ret}.");

        //todo: compose audio and attrs (like bitrate) into final video
        Dispatcher.Dispatch(() =>
        {
            SubProgLabel.Text = Localized.RenderPage_SubProg_FinalEncoding;
        });

        ffmpeg.totalFrames = _duration;

        ffmpeg.OnProgressChanged += (p) =>
        {
            totalProg = p + lastProg;

            Dispatcher.Dispatch(async () =>
            {
                await SubProgress.ProgressTo(p, 250, Easing.Linear);
                await TotalProgress.ProgressTo(totalProg / 3d, 5, Easing.Linear);

            });
        };

        ffmpeg.OnLog += _logQueue.Enqueue;

        var ffArgs = $"-i \"{outTempFile}\" " +
            //$"-i \"{audio}\" " +
            $"-c:v h264_qsv " +
            $"-pix_fmt yuv420p " +
            $"\"{outputPath}\" " +
            /*    -b:v {avg}M -maxrate {max}M -bufsize {buf}M //vbr

                  -b:v {avg}M //cbr

                  -crf {num} //crf
             */
            $"-y";
        Log($"FFmpeg args: {ffArgs}");

        var ffRet = await ffmpeg.Run(ffArgs);

        Log($"FFmpeg process exited with code {ffRet}.");
    }
#endif

    async Task DoComputeOnMobile(RenderPageViewModel vm, string outputPath)
    {
        void SetSubProg(string s)
        {
            lastProg = totalProg;
            string label;
            try
            {
                label = Localized.DynamicLookup($"RenderPage_SubProg_{s}");
            }
            catch (Exception)
            {
                label = $"RenderPage_SubProg_{s}";
            }
            Dispatcher.Dispatch(() =>
            {
                SubProgLabel.Text = label;
                TotalProgress.ProgressTo(totalProg / 3d, 5, Easing.Linear);

            });
        }

        void _WriteToLogBox(string s, string l)
        {
            _logQueue.Enqueue(s);
        }

        MyLoggerExtensions.OnLog += _WriteToLogBox;

        var outTempFile = Path.Combine(_workingPath, "export", $"output-{Guid.NewGuid()}.mp4");
        var outPreview = Path.Combine(_workingPath, "export", $"preview-{Guid.NewGuid()}.png");
        Directory.CreateDirectory(Path.GetDirectoryName(outTempFile) ?? throw new NullReferenceException());

        int parallelThreadCount = (int)MaxParallelThreadsCount.Value / 2;
        if (parallelThreadCount < Environment.ProcessorCount / 2)
        {
            parallelThreadCount = Environment.ProcessorCount / 2;
        }
#if ANDROID
        NativeGLSurfaceView view = new NativeGLSurfaceView
        {
            WorkGroupSize = 512,
            //each computer will do the things left
        };

        ComputerHelper.AddGLViewHandler = ComputeView.Children.Add;
#elif iDevices

#elif WINDOWS
        Context context = Context.CreateDefault();
        var devices = context.Devices.ToList();
        projectFrameCut.Render.WindowsRender.ILGPUPlugin.accelerators = devices.Select(d => d.CreateAccelerator(context)).ToArray();
#endif
        var draftSrc = JsonSerializer.Deserialize<DraftStructureJSON>
                                                 (File.ReadAllText(Path.Combine(_workingPath, "timeline.json"))) ?? throw new NullReferenceException();

        Log($"Draft loaded: duration {draftSrc.Duration}, saved on {draftSrc.SavedAt}, {draftSrc.Clips.Length} clips.");

        if (draftSrc.Duration <= 1)
        {
            await DisplayAlertAsync(Localized._Info, "Draft invalid", Localized._OK);
            return;
        }

        var duration = draftSrc.Duration;

        var frameRange = Enumerable.Range(0, (int)duration).Select(i => (uint)i).ToArray();

        List<JsonElement> clipsJson = draftSrc.Clips.Select(c => (JsonElement)c).ToList();

        var clipsList = new List<IClip>();

        foreach (var clip in clipsJson)
        {
            clipsList.Add(PluginManager.CreateClip(clip));
        }

        var clips = clipsList.ToArray();

        if (clips == null || clips.Length == 0)
        {
            Log("ERROR: No clips in the whole draft.");
            return;
        }

        SetSubProg("PrepareDraft");

        Log("Initializing all clips...");
        foreach (IClip clip in clips)
        {
            clip.ReInit();
        }

        int width = 1280;// int.Parse(vm.Width);
        int height = 720;// int.Parse(vm.Height);
        int fps = int.Parse(vm.Framerate);

        VideoBuilder builder = new VideoBuilder(outputPath, width, height, fps, "libx264", AVPixelFormat.AV_PIX_FMT_YUV420P)
        {
            EnablePreview = false,
            DoGCAfterEachWrite = true,
            DisposeFrameAfterEachWrite = true,
            Duration = duration
        };

        Renderer renderer = new Renderer
        {
            builder = builder,
            Clips = clips,
            Duration = duration,
            MaxThreads = parallelThreadCount,
            LogState = false,
            LogStatToLogger = true,
            GCOption = 0,
        };

        renderer.OnProgressChanged += (p) =>
        {
            totalProg = p + lastProg;
            Dispatcher.Dispatch(async () =>
            {
                await SubProgress.ProgressTo(p, 250, Easing.Linear);
                await TotalProgress.ProgressTo(totalProg / 3d, 5, Easing.Linear);
            });
        };

        builder?.Build()?.Start();
        renderer.PrepareRender(_cts.Token);
        if (_cts.IsCancellationRequested) return;

        Stopwatch sw1 = new();
        SetSubProg("Render");
        Log("Start render...");

        sw1.Restart();
        await renderer.GoRender(_cts.Token);
        if (_cts.IsCancellationRequested) return;

        Log($"Render done,total elapsed {sw1}, avg elapsed {renderer.EachElapsedForPreparing.Average(t => t.TotalSeconds)} spf to prepare and {renderer.EachElapsed.Average(t => t.TotalSeconds)} spf to render");

        GC.Collect();
        SetSubProg("WriteVideo");
        Log("Finish writing video...");
        builder?.Finish((i) => Timeline.MixtureLayers(Timeline.GetFramesInOneFrame(clips, i, width, height), i, width, height), duration);

        Log($"Releasing resources...");

        foreach (var item in clips)
        {
            item?.Dispose();
        }

        MyLoggerExtensions.OnLog -= _WriteToLogBox;

        Log($"All done! Total elapsed {sw1}.");


    }




    #endregion


    private void MaxParallelThreadsCount_ValueChanged(object sender, ValueChangedEventArgs e)
    {
        MaxParallelThreadsCountLabel.Text = Localized.RenderPage_MaxParallelThreadsCount((int)e.NewValue);
    }





    private void MoreOptions_Clicked(object sender, EventArgs e)
    {



    }

    private static async Task<string> PickSavePath(string? defaultName = null)
    {
#if WINDOWS
        var picker = new Windows.Storage.Pickers.FileSavePicker();

        // 获取当前窗口句柄
        var hwnd = ((MauiWinUIWindow)Application.Current.Windows[0].Handler.PlatformView).WindowHandle;
        WinRT.Interop.InitializeWithWindow.Initialize(picker, hwnd);

        // 设置文件类型过滤器
        picker.FileTypeChoices.Add("视频文件", new List<string> { ".mp4", ".mkv", ".avi", ".mov" });
        //picker.FileTypeChoices.Add("所有文件", new List<string> { ".*" });

        // 设置默认文件名
        picker.SuggestedFileName = defaultName ?? $"Export_{DateTime.Now:yyyyMMdd_HHmmss}";

        // 显示保存对话框
        var file = await picker.PickSaveFileAsync();

        return file?.Path ?? string.Empty;
#else

    return string.Empty;
#endif
    }

    private async void CancelRender_Clicked(object sender, EventArgs e)
    {
        if (!running) return;
        var sure = await DisplayAlert(Localized._Warn, Localized.RenderPage_CancelRender_Warn, Localized._OK, Localized._Cancel);
        if (sure)
        {
#if WINDOWS
            render.Cancel();
#endif
            _cts.Cancel();
            _logUpdateTimer?.Stop();
            await FlushLogQueue();

            RenderOptionPanel.IsVisible = true;
            CancelRender.IsEnabled = false;
            //MoreOptions.IsEnabled = true;
            PreviewLayout.IsVisible = false;
            running = false;
        }
    }
}


public class RenderPageViewModel : INotifyPropertyChanged
{
    public string[] ExportOptions_Resolution { get; } = [
        "1280x720",
        "1920x1080",
        "2560x1440",
        "3840x2160",
        "7680x4320",
        Localized.RenderPage_CustomOption
    ];

    public string[] ExportOptions_Framerate { get; } = [
        "24", "30", "45", "60", "90", "120",
        Localized.RenderPage_CustomOption
    ];

    public string[] ExportOptions_Encoding { get; } = [
        "h264", "h265/hevc", "av1",
        Localized.RenderPage_CustomOption
    ];

    public string[] ExportOptions_BitDepth { get; } = [
        "8bit", "10bit", "12bit"
    ];

    string _resoultion = "3840x2160";
    public string Resoultion
    {
        get => _resoultion;
        set
        {
            if (SetProperty(ref _resoultion, value))
            {
                OnPropertyChanged(nameof(IsCustomResolutionVisible));
                if (!string.IsNullOrWhiteSpace(value) &&
                    value != Localized.RenderPage_CustomOption &&
                    value.Contains('x'))
                {
                    var parts = value.Split('x', 2, StringSplitOptions.TrimEntries);
                    if (parts.Length == 2)
                    {
                        _width = parts[0];
                        _height = parts[1];
                        OnPropertyChanged(nameof(Width));   // 修正
                        OnPropertyChanged(nameof(Height));  // 修正
                    }
                }
            }
        }
    }

    string _framerate = "30";
    public string Framerate
    {
        get
        {
            if (_framerate == Localized.RenderPage_CustomOption) return "";
            else return _framerate;
        }
        set
        {
            if (SetProperty(ref _framerate, value))
            {
                OnPropertyChanged(nameof(IsCustomFramerateVisible));
            }
        }
    }

    public string FramerateDisplay
    {
        get
        {
            if (ExportOptions_Framerate.Any((x) => x == Framerate)) return Framerate;
            else return Localized.RenderPage_CustomOption;
        }
        set
        {
            Framerate = value;
        }
    }

    string _encoding = "h264";
    public string Encoding
    {
        get
        {
            if (_encoding == Localized.RenderPage_CustomOption) return "";
            else return _encoding;
        }
        set
        {
            if (SetProperty(ref _encoding, value))
            {
                OnPropertyChanged(nameof(IsCustomEncodingVisible));
            }
        }
    }

    public string EncodingDisplay
    {
        get
        {
            if (ExportOptions_Encoding.Any((x) => x == Encoding)) return Encoding;
            else return Localized.RenderPage_CustomOption;
        }
        set
        {
            Encoding = value;
        }
    }

    string _bitDepth = "8bit";
    public string BitDepth
    {
        get
        {
            if (_bitDepth == Localized.RenderPage_CustomOption) return "";
            else return _bitDepth;
        }
        set
        {
            if (SetProperty(ref _bitDepth, value))
            {
                OnPropertyChanged(nameof(IsCustomBitDepthVisible));
            }
        }
    }

    public string BitDepthDisplay
    {
        get
        {
            if (ExportOptions_BitDepth.Any((x) => x == BitDepth)) return BitDepth;
            else return Localized.RenderPage_CustomOption;
        }
        set
        {
            BitDepth = value;
        }
    }

    string _width = "3840";
    public string Width
    {
        get => _width;
        set
        {
            SetProperty(ref _width, value);

        }
    }

    string _height = "2160";
    public string Height
    {
        get => _height;
        set
        {
            SetProperty(ref _height, value);
        }
    }


    public bool IsCustomResolutionVisible => _resoultion == Localized.RenderPage_CustomOption;
    public bool IsCustomFramerateVisible => !ExportOptions_Framerate.Where((x) => x != Localized.RenderPage_CustomOption).Any((x) => x == _framerate);
    public bool IsCustomEncodingVisible => !ExportOptions_Encoding.Where((x) => x != Localized.RenderPage_CustomOption).Any((x) => x == _encoding);
    public bool IsCustomBitDepthVisible => !ExportOptions_BitDepth.Where((x) => x != Localized.RenderPage_CustomOption).Any((x) => x == _bitDepth);

    public string BuildSummary() =>
        $"{_width}x{_height} @ {_framerate} fps\nEncoding: {_encoding}\nBitDepth: {_bitDepth}";

    public event PropertyChangedEventHandler? PropertyChanged;
    protected void OnPropertyChanged([CallerMemberName] string? name = null)
        => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));

    protected bool SetProperty<T>(ref T storage, T value, [CallerMemberName] string? name = null)
    {
        if (EqualityComparer<T>.Default.Equals(storage, value)) return false;
        storage = value!;
        OnPropertyChanged(name);
        return true;
    }

}
