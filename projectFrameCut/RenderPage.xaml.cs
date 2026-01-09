using FFmpeg.AutoGen;
using Microsoft.Maui.ApplicationModel;
using projectFrameCut.Shared;
using System;
using System.Runtime;
using System.Collections.Concurrent;
using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Runtime.Intrinsics.X86;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;
using projectFrameCut.Setting.SettingManager;
using projectFrameCut.Render.RenderAPIBase.Project;
using projectFrameCut.Render.Rendering;
using projectFrameCut.Render.RenderAPIBase.ClipAndTrack;
using projectFrameCut.Render.Plugin;
using SixLabors.ImageSharp;
using projectFrameCut.Services;
using projectFrameCut.DraftStuff;
using projectFrameCut.Render.EncodeAndDecode;
using JsonElement = System.Text.Json.JsonElement;












#if ANDROID
using projectFrameCut.Render.AndroidOpenGL;
using projectFrameCut.Render.AndroidOpenGL.Platforms.Android;
using projectFrameCut.Platforms.Android;

#endif

#if WINDOWS
using ILGPU;
#endif

namespace projectFrameCut;

public partial class RenderPage : ContentPage
{
    public string _workingPath;
    ProjectJSONStructure _project;
    DraftStructureJSON _draft;
    uint _duration;

    public bool running;


    // 日志缓冲区
    private readonly StringBuilder _logBuffer = new StringBuilder();
    private readonly ConcurrentQueue<string> _logQueue = new ConcurrentQueue<string>();
    private System.Timers.Timer? _logUpdateTimer;
    private readonly SemaphoreSlim _logSemaphore = new SemaphoreSlim(1, 1);

    private System.Timers.Timer? _screenSaverTimer;
    private System.Timers.Timer? _moveHintTimer;
    private const int ScreenSaverTimeout = 15000;

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
        InitializeScreenSaverTimer();
        ScreenSaverOverlay.InputTransparent = true;
        ScreenSaverOverlay.CascadeInputTransparent = true;
    }

    public RenderPage(string path, uint projectDuration, ProjectJSONStructure projectInfo, DraftStructureJSON draft)
    {
        InitializeComponent();
        _workingPath = path;
        _duration = projectDuration;
        _project = projectInfo;
        _draft = draft;
        Title = Localized.RenderPage_ExportTitle(projectInfo.projectName);
        ScreenSaverOverlay.InputTransparent = true;
        ScreenSaverOverlay.CascadeInputTransparent = true;
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
        DraftJSONViewer.Text = JsonSerializer.Serialize(_draft, DraftPage.DraftJSONOption);
        InitializeLogTimer();
        InitializeScreenSaverTimer();

#if ANDROID
        MaxParallelThreadsCount.Maximum = Environment.ProcessorCount;
        MaxParallelThreadsCount.Value = Math.Max(Environment.ProcessorCount / 2, 6);
#else
        MaxParallelThreadsCount.Maximum = Environment.ProcessorCount * 8;
        MaxParallelThreadsCount.Value = Math.Max(Environment.ProcessorCount * 2, 16);
#endif
    }
    private void InitializeLogTimer()
    {
        _logUpdateTimer = new System.Timers.Timer(800);
        _logUpdateTimer.Elapsed += async (s, e) => await FlushLogQueue();
        _logUpdateTimer.AutoReset = true;
    }

    private void InitializeScreenSaverTimer()
    {
        _screenSaverTimer = new System.Timers.Timer(ScreenSaverTimeout);
        _screenSaverTimer.Elapsed += (s, e) => Dispatcher.Dispatch(() =>
        {
            ScreenSaverOverlay.IsVisible = true;
            ScreenSaverOverlay.InputTransparent = false;
            ScreenSaverOverlay.CascadeInputTransparent = false;
            StartMovingHint();
        });
        _screenSaverTimer.AutoReset = false;

        _moveHintTimer = new System.Timers.Timer(10000);
        _moveHintTimer.Elapsed += (s, e) => Dispatcher.Dispatch(MoveHintLabel);
        _moveHintTimer.AutoReset = true;
    }

    private void StopScreenSaverTimer()
    {
        _screenSaverTimer?.Stop();
        ScreenSaverOverlay.IsVisible = false;
        ScreenSaverOverlay.InputTransparent = true;
        ScreenSaverOverlay.CascadeInputTransparent = true;
        StopMovingHint();
    }

    private void StartMovingHint()
    {
        MoveHintLabel();
        _moveHintTimer?.Start();
    }

    private void StopMovingHint()
    {
        _moveHintTimer?.Stop();
        HintLabel.TranslationX = 0;
        HintLabel.TranslationY = 0;
    }

    private void MoveHintLabel()
    {
        if (ScreenSaverOverlay.Width <= 0 || ScreenSaverOverlay.Height <= 0) return;

        double rangeX = (ScreenSaverOverlay.Width - HintLabel.Width) / 2;
        double rangeY = (ScreenSaverOverlay.Height - HintLabel.Height) / 2;

        if (rangeX < 0) rangeX = 0;
        if (rangeY < 0) rangeY = 0;

        var rnd = Random.Shared;
        HintLabel.TranslationX = (rnd.NextDouble() * 2 - 1) * rangeX;
        HintLabel.TranslationY = (rnd.NextDouble() * 2 - 1) * rangeY;
    }

    private void ScreenSaverOverlay_Tapped(object sender, EventArgs e)
    {
        ScreenSaverOverlay.IsVisible = false;
        StopMovingHint();
        if (running)
        {
            _screenSaverTimer?.Stop();
            _screenSaverTimer?.Start();
        }
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
        StopScreenSaverTimer();
        return running;
    }

    private async void ContentPage_Loaded(object sender, EventArgs e)
    {
        if (string.IsNullOrWhiteSpace(_workingPath))
        {
            await DisplayAlert(Localized._Info, Localized.RenderPage_NoDraft, Localized._OK);
        }
    }
    #region rendering
    [DebuggerNonUserCode]
    void _WriteToLogBox(string s, string l)
    {
        if (!s.StartsWith("[Render]") && !s.StartsWith("[Preparer]"))
        {
            _logQueue.Enqueue($"[{l}] {s}");
        }
    }

    private async void StartRender_Clicked(object sender, EventArgs e)
    {
        Shell.SetNavBarIsVisible(this, false);
        try
        {
            var outputDir = Path.Combine(MauiProgram.DataPath, "RenderCache");

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
            if (!SettingsManager.IsSettingExists("render_EnableScreenSaver"))
            {
#if ANDROID || IOS //oled screen, avoid burn-in
                SettingsManager.WriteSetting("render_EnableScreenSaver", "true");
#else
                SettingsManager.WriteSetting("render_EnableScreenSaver", "false");
#endif
            }
            if (SettingsManager.IsBoolSettingTrue("render_EnableScreenSaver"))
            {
                _screenSaverTimer?.Stop();
                _screenSaverTimer?.Start();
                StopMovingHint();
            }



            MyLoggerExtensions.OnLog += _WriteToLogBox;

            if (BindingContext is RenderPageViewModel vm)
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
                    "libx264" => ".mp4",
                    "libx265" => ".mov",
                    "ffv1" => ".mkv",
                    _ => ".mp4"
                };



                running = true;
                DeviceDisplay.Current.KeepScreenOn = true;
                Log("Output options:\r\n" + vm.BuildSummary());
                string vidOutputPath = Path.Combine(outputDir, $"{_project.projectName}_{DateTime.Now:yyyyMMdd_HHmmss}{ext}");
                string audOutputPath = Path.Combine(outputDir, $"{_project.projectName}_{DateTime.Now:yyyyMMdd_HHmmss}.wav");
                string compOutputPath = Path.Combine(outputDir, $"{_project.projectName}_{DateTime.Now:yyyyMMdd_HHmmss}.composed{ext}");
#if WINDOWS
                var resultPath = await FileSystemService.PickASavePath($"{_project.projectName}_{DateTime.Now:yyyyMMdd_HHmmss}{ext}", outputDir);
                if (string.IsNullOrWhiteSpace(resultPath)) goto done;
#else
                string resultPath = compOutputPath;
#endif
                if (_cts.IsCancellationRequested) return;

                try
                {
                    await ComposeAudio(vm, audOutputPath);

                }
                catch (Exception ex)
                {

                    Log(ex, "compose audio", this);
                    await DisplayAlertAsync(Localized._Error, Localized.RenderPage_Fail(ex), Localized._OK);
                    if (Debugger.IsAttached) throw;
                    return;
                }
                if (_cts.IsCancellationRequested) return;

                try
                {
                    await DoCompute(vm, vidOutputPath);
                }
                catch (Exception ex)
                {
                    Log(ex, "render frames", this);
                    await DisplayAlertAsync(Localized._Error, Localized.RenderPage_Fail(ex), Localized._OK);
                    if (Debugger.IsAttached) throw;
                    return;
                }

                if (_cts.IsCancellationRequested) return;

                double targetFps = double.Parse(vm.Framerate);
                if (Math.Abs(targetFps - Math.Round(targetFps)) > 0.001)
                {
                    Log($"Resampling video from {(int)Math.Round(targetFps)} to {targetFps}...");
                    SetSubProg("Resample");
#if WINDOWS
                    var tempVid = vidOutputPath + ".temp" + ext;
                    if (File.Exists(vidOutputPath))
                    {
                        File.Move(vidOutputPath, tempVid);
                        string args = $"-i \"{tempVid}\" -r {targetFps} -c:v {enc} -crf 18 -preset fast \"{vidOutputPath}\"";
                        if (enc == "ffv1") args = $"-i \"{tempVid}\" -r {targetFps} -c:v ffv1 \"{vidOutputPath}\"";

                        await ffmpeg.Run(args);
                        File.Delete(tempVid);
                    }
#endif
                }


                if (_cts.IsCancellationRequested) return;

                await Task.Run(async () =>
                {
                    try
                    {
                        VideoAudioMuxer.MuxFromFiles(vidOutputPath, audOutputPath, compOutputPath, true);
                    }
                    catch (Exception ex)
                    {
                        Log(ex, "compose media", this);
                        if (Debugger.IsAttached) throw;
                        await Dispatcher.DispatchAsync(async () =>
                        {
                            await DisplayAlertAsync(Localized._Error, Localized.RenderPage_Fail(ex), Localized._OK);
                        });
                        return;
                    }

                });

            done:
                _logUpdateTimer?.Stop();
                _screenSaverTimer?.Stop();
                ScreenSaverOverlay.IsVisible = false;
                StopMovingHint();
                StopScreenSaverTimer();
                await FlushLogQueue();
                MyLoggerExtensions.OnLog -= _WriteToLogBox;
                await TotalProgress.ProgressTo(1, 50, Easing.Linear);

                await DisplayAlertAsync(Localized._Info, Localized.RenderPage_Done, Localized._OK);
                running = false;
                CancelRender.IsEnabled = false;


#if ANDROID
                var path = await MediaStoreSaver.SaveMediaFileAsync(resultPath, $"{_project.projectName}_{DateTime.Now:yyyyMMdd_HHmmss}{ext}", ext switch { ".mp4" => "video/mp4", ".mov" => "video/quicktime", ".mkv" => "video/x-matroska", _ => "video/mp4" }, MediaStoreSaver.MediaType.Video);
                if (!string.IsNullOrWhiteSpace(path) && !SettingsManager.IsBoolSettingTrue("DeveloperMode"))
                {
                    try
                    {
                        File.Delete(resultPath);
                    }
                    catch { }
                }
#else
                await Task.Run(() => File.Move(compOutputPath, resultPath));
#if WINDOWS
                await FileSystemService.ShowFileInFolderAsync(resultPath);
#endif
#endif


                DeviceDisplay.Current.KeepScreenOn = false;
            }
        }
        catch (Exception ex)
        {
            Log(ex, "render", this);
            await DisplayAlertAsync(Localized._Error, Localized.RenderPage_Fail(ex), Localized._OK);
            if (Debugger.IsAttached) throw;
            return;
        }
        finally
        {
            StopScreenSaverTimer();
            Shell.SetNavBarIsVisible(this, true);

        }
    }

    double totalProg = 0, lastProg = 0;
    string _currentSubProgText = "";
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
        _currentSubProgText = label;
        Dispatcher.Dispatch(() =>
        {
            SubProgLabel.Text = label;
            TotalProgress.ProgressTo(totalProg / 3d, 5, Easing.Linear);

        });
    }

    async Task DoCompute(RenderPageViewModel vm, string outputPath)
    {
        try
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
                "libx264" => ".mp4",
                "libx265" => ".mov",
                "ffv1" => ".mkv",
                _ => ".mp4"
            };




            var outTempFile = outputPath + ext;
            Directory.CreateDirectory(Path.GetDirectoryName(outTempFile) ?? throw new NullReferenceException());

            int parallelThreadCount = (int)MaxParallelThreadsCount.Value;

#if ANDROID
            ComputerHelper.AddGLViewHandler = ComputeView.Children.Add;
#elif iDevices

#elif WINDOWS
            Context context = Context.CreateDefault();
            var devices = context.Devices.ToList();
            if (SettingsManager.IsBoolSettingTrue("accel_enableMultiAccel"))
            {
                var accels = SettingsManager.GetSetting("accel_MultiDeviceID", "all");
                if (accels == "all")
                {
                    projectFrameCut.Render.WindowsRender.ILGPUPlugin.accelerators = devices.Where(d => d.AcceleratorType != ILGPU.Runtime.AcceleratorType.CPU).Select(d => d.CreateAccelerator(context)).ToArray();
                }
                else
                {
                    var accelList = accels.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                                .Select(s => int.TryParse(s, out var id) ? id : -1)
                                .Where(id => id >= 0)
                                .ToList();
                    projectFrameCut.Render.WindowsRender.ILGPUPlugin.accelerators = devices.Index().Where(d => accelList.Contains(d.Index)).Select(d => d.Item.CreateAccelerator(context)).ToArray();
                }

            }
            else
            {
                var accelId = SettingsManager.GetSetting("accel_DeviceId", "");
                if (int.TryParse(accelId, out var accelIdInt)) projectFrameCut.Render.WindowsRender.ILGPUPlugin.accelerators = [devices[accelIdInt].CreateAccelerator(context)];
            }

            if (!projectFrameCut.Render.WindowsRender.ILGPUPlugin.accelerators.ArrayAny()) throw new InvalidDataException("No valid ILGPU accelerators found.");

#endif
            var draftSrc = _draft ?? throw new NullReferenceException();

            Log($"Draft loaded: duration {draftSrc.Duration}, saved on {draftSrc.SavedAt}, {draftSrc.Clips.Length} clips.");

            if (draftSrc.Duration <= 1)
            {
                await DisplayAlertAsync(Localized._Info, "Draft invalid", Localized._OK);
                return;
            }

            var duration = Math.Max(draftSrc.Duration, draftSrc.AudioDuration);

            var clips = DraftImportAndExportHelper.JSONToIClips(draftSrc).Where(c => c.ClipType != ClipMode.AudioClip).ToArray();

            if (clips == null || clips.Length == 0)
            {
                Log("ERROR: No clips in the whole draft.");
                return;
            }

            SetSubProg("PrepareDraft");

            int width = int.Parse(vm.Width);
            int height = int.Parse(vm.Height);
            int fps = (int)Math.Round(double.Parse(vm.Framerate));

            VideoBuilder builder = new VideoBuilder(outputPath, width, height, fps, enc, fmt)
            {
                EnablePreview = true,
                DoGCAfterEachWrite = (int.TryParse(SettingsManager.GetSetting("render_GCOption", "0"), out var value1) ? value1 : 0) > 0,
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
                GCOption = (int.TryParse(SettingsManager.GetSetting("render_GCOption", "0"), out var value) ? value : 0)
            };

            var memInfo = GC.GetGCMemoryInfo();
            if (memInfo.TotalAvailableMemoryBytes > 0)
            {
                renderer.MemoryThresholdBytes = (long)(memInfo.TotalAvailableMemoryBytes * 0.8);
            }

            Log($"Available memory for rendering: {memInfo.TotalAvailableMemoryBytes / 1024 / 1024} MB, set memory threshold to {renderer.MemoryThresholdBytes / 1024 / 1024} MB.");

            renderer.OnLowMemory += async (r) =>
            {
                await Dispatcher.DispatchAsync(async () =>
                {
                    r.ClearCaches();
                    bool resume = await DisplayAlert("Memory Low",
                        $"System memory is running low (Usage > {r.MemoryThresholdBytes / 1024 / 1024} MB). Rendering paused and resources cleaned. Do you want to continue?",
                        "Continue", "Stop");

                    if (resume)
                    {
                        r.IsPaused = false;
                    }
                    else
                    {
                        _cts.Cancel();
                        r.IsPaused = false;
                    }
                });
            };

            renderer.OnProgressChanged += (p, etr) =>
            {
                totalProg = p + lastProg;
                Dispatcher.Dispatch(async () =>
                {
                    string timeStr = "";
                    if (etr.TotalSeconds > 0)
                    {
                        timeStr = (etr.TotalHours >= 1 ? etr.ToString(@"hh\:mm\:ss") : etr.ToString(@"mm\:ss"));
                        SubProgLabel.Text = $"{_currentSubProgText} ({timeStr})";
                    }
                    await SubProgress.ProgressTo(p, 250, Easing.Linear);
                    await TotalProgress.ProgressTo(totalProg / 3d, 5, Easing.Linear);

                    if (ScreenSaverOverlay.IsVisible)
                    {
                        HintLabel.Text = $"{Localized.RenderPage_ClickToShowUI}{Environment.NewLine}{Localized.RenderPage_Stat(totalProg / 3d, timeStr)}";
                    }
                });
            };

            builder.OnPreviewGenerated += (s, e) =>
            {
                try
                {
                    var src = e.ToImageSource();
                    if (src is not null)
                    {
                        Dispatcher.Dispatch(() =>
                        {
                            PreviewImage.Source = src;
                        });
                    }
                }
                catch (Exception)
                {
                    // ignored
                }
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

            // Drop references to large graphs ASAP.
            renderer.builder = null;
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

            Log($"All done! Total elapsed {sw1}.");


        }
        catch (Exception ex)
        {
            Log(ex, "Render", this);
            await DisplayAlertAsync(Localized._Error, Localized.RenderPage_Fail(ex), Localized._OK);
        }


    }

    async Task ComposeAudio(RenderPageViewModel vm, string outputPath)
    {
        var draftSrc = _draft ?? throw new NullReferenceException();

        Log($"Draft loaded: audio duration {draftSrc.AudioDuration}, saved on {draftSrc.SavedAt}, {draftSrc.Clips.Length} clips.");

        if (draftSrc.Duration <= 1)
        {
            await DisplayAlertAsync(Localized._Info, "Draft invalid", Localized._OK);
            return;
        }

        var clips = DraftImportAndExportHelper.JSONToIClips(draftSrc).Where(c => c.ClipType == ClipMode.AudioClip || c.ClipType == ClipMode.VideoClip).ToArray();

        if (clips == null || clips.Length == 0)
        {
            Log("No sound clips in the whole draft. returning...");
            return;
        }

        Log($"Found {clips.Length} audio clips.");

        Log("Initializing all clips...");
        foreach (IClip clip in clips)
        {
            clip.ReInit();
        }

        var buf = AudioComposer.Compose(clips, null, (int)_project.targetFrameRate, 48000, 2);
        AudioWriter writer = new(outputPath, 48000, 2);
        writer.Append(buf);
        writer.Finish();
        writer.Dispose();
        foreach (var item in clips)
        {
            item?.Dispose();
        }
        return;

    }



    #endregion


    private void MaxParallelThreadsCount_ValueChanged(object sender, ValueChangedEventArgs e)
    {
        MaxParallelThreadsCountLabel.Text = Localized.RenderPage_MaxParallelThreadsCount((int)e.NewValue);
    }





    private void MoreOptions_Clicked(object sender, EventArgs e)
    {



    }


    private async void CancelRender_Clicked(object sender, EventArgs e)
    {
        if (!running) return;
        var sure = await DisplayAlert(Localized._Warn, Localized.RenderPage_CancelRender_Warn, Localized._OK, Localized._Cancel);
        if (sure)
        {
            _cts.Cancel();
            _logUpdateTimer?.Stop();
            _screenSaverTimer?.Stop();
            ScreenSaverOverlay.IsVisible = false;
            StopMovingHint();
            await FlushLogQueue();
            RenderOptionPanel.IsVisible = true;
            CancelRender.IsEnabled = false;
            MyLoggerExtensions.OnLog -= _WriteToLogBox;

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

    public string[] ExportOptions_Framerate { get; } =
        ["23.97", "24", "29.97", "30", "44.96", "45", "59.94", "60", "89.91", "90", "119.88", "120", Localized.RenderPage_CustomOption];

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
