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
using System.Runtime.InteropServices;

using projectFrameCut.ApplicationAPIBase.Helpers;
using System.Globalization;
using PictureExtensions = projectFrameCut.Shared.PictureExtensions;
using IPicture = projectFrameCut.Shared.IPicture;



#if ANDROID
using projectFrameCut.Render.AndroidOpenGL;
using projectFrameCut.Render.AndroidOpenGL.Platforms.Android;
using projectFrameCut.Platforms.Android;

#endif

#if WINDOWS
using ILGPU;
#endif

namespace projectFrameCut;

public enum PostRenderAction
{
    None,
    CloseApp,
    Shutdown,
    Hibernate
}

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
    private readonly SemaphoreSlim _previewUpdateSemaphore = new SemaphoreSlim(1, 1);
    private ToolbarItem? _toggleLogToolbarItem;
    private bool _isLogPanelVisible;

    private System.Timers.Timer? _screenSaverTimer;
    private System.Timers.Timer? _moveHintTimer;
    private const int ScreenSaverTimeout = 15000;

#if WINDOWS
    Platforms.Windows.ffmpegHelper ffmpeg = new projectFrameCut.Platforms.Windows.ffmpegHelper();
#endif

    private CancellationTokenSource _cts = new CancellationTokenSource();
    private CancellationTokenSource? _countdownCts;

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
            if (Enum.TryParse<PostRenderAction>(SettingsManager.GetSetting("render_DefaultPostRenderAction", "None"), out var action))
            {
                vmDefault.SelectedPostRenderActionEnum = action;
            }
        }
        catch { }
        BindingContext = vmDefault;
        InitializeLogTimer();
        InitializeLogPanel();
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
        Title = Localized.RenderPage_ExportTitle(projectInfo.ProjectName);
        ScreenSaverOverlay.InputTransparent = true;
        ScreenSaverOverlay.CascadeInputTransparent = true;
        var vm = new RenderPageViewModel();
        try
        {
            vm.Resoultion = SettingsManager.GetSetting("render_DefaultResolution", vm.Resoultion);
            vm.FramerateDisplay = SettingsManager.GetSetting("render_DefaultFramerate", vm.FramerateDisplay);
            vm.EncodingDisplay = SettingsManager.GetSetting("render_DefaultEncoding", vm.EncodingDisplay);
            vm.BitDepthDisplay = SettingsManager.GetSetting("render_DefaultBitDepth", vm.BitDepthDisplay);
            if (Enum.TryParse<PostRenderAction>(SettingsManager.GetSetting("render_DefaultPostRenderAction", "None"), out var action))
            {
                vm.SelectedPostRenderActionEnum = action;
            }
        }
        catch { }
        BindingContext = vm;
        MaxParallelThreadsCount.Value = Environment.ProcessorCount * 2;
        MaxParallelThreadsCountLabel.Text = Localized.RenderPage_MaxParallelThreadsCount((int)MaxParallelThreadsCount.Value);
        CancelRender.IsEnabled = false;
        if (SettingsManager.IsBoolSettingTrue("DeveloperMode"))
        {
            ExportProjectJSONButton.IsVisible = true;
            PerformPostRenderActionNowTestButton.IsVisible = true;
        }
        if (!SettingsManager.IsSettingExists("accel_enableMultiAccel")) SettingsManager.WriteSetting("accel_enableMultiAccel", "true");
        InitializeLogTimer();
        InitializeLogPanel();
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

    private void InitializeLogPanel()
    {
        SetLogPanelVisible(false);

        _toggleLogToolbarItem = new ToolbarItem
        {
            Order = ToolbarItemOrder.Secondary
        };
        _toggleLogToolbarItem.Clicked += ToggleLogPanel_Clicked;
        ToolbarItems.Add(_toggleLogToolbarItem);
        UpdateLogPanelToggleText();
    }

    private void ToggleLogPanel_Clicked(object? sender, EventArgs e)
    {
        SetLogPanelVisible(!_isLogPanelVisible);
    }

    private void SetLogPanelVisible(bool visible)
    {
        _isLogPanelVisible = visible;
        LoggingBox.IsVisible = visible;
        LoggingBox.HeightRequest = visible ? -1 : 0;
        if (LoggingBox.Parent is Microsoft.Maui.Controls.Grid progressGrid && progressGrid.RowDefinitions.Count > 4)
        {
            progressGrid.RowDefinitions[4].Height = visible ? GridLength.Star : new GridLength(0);
        }
        UpdateLogPanelToggleText();
        UpdateLogRefreshState();

        if (visible)
        {
            _ = FlushLogQueue();
        }
    }

    private void UpdateLogPanelToggleText()
    {
        var text = _isLogPanelVisible ? Localized.RenderPage_HideLogs : Localized.RenderPage_ShowLogs;
        if (_toggleLogToolbarItem is not null)
        {
            _toggleLogToolbarItem.Text = text;
        }

        if (ToggleLogPanelButton is not null)
        {
            ToggleLogPanelButton.Text = text;
        }
    }

    private void UpdateLogRefreshState()
    {
        if (_logUpdateTimer is null)
        {
            return;
        }

        if (running && _isLogPanelVisible)
        {
            _logUpdateTimer.Start();
        }
        else
        {
            _logUpdateTimer.Stop();
        }
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
        if (!_isLogPanelVisible || _logQueue.IsEmpty) return;
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
            await DisplayAlertAsync(Localized._Info, Localized.RenderPage_NoDraft, Localized._OK);
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
            MoreOptions.IsEnabled = false;
            await SubProgress.ProgressTo(0, 250, Easing.Linear);

            _logBuffer.Clear();
            _logQueue.Clear();
            LoggingBox.Text = string.Empty;
            UpdateLogRefreshState();
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
                string vidOutputPath = Path.Combine(outputDir, $"{_project.ProjectName}_{DateTime.Now:yyyyMMdd_HHmmss}{ext}");
                string audOutputPath = Path.Combine(outputDir, $"{_project.ProjectName}_{DateTime.Now:yyyyMMdd_HHmmss}.wav");
                string compOutputPath = Path.Combine(outputDir, $"{_project.ProjectName}_{DateTime.Now:yyyyMMdd_HHmmss}.composed{ext}");
#if WINDOWS
                var resultPath = await FileSystemService.PickASavePath($"{_project.ProjectName}_{DateTime.Now:yyyyMMdd_HHmmss}{ext}", MauiProgram.DataPath);
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

                running = false;
                CancelRender.IsEnabled = false;
                UpdateLogRefreshState();


                try
                {
#if ANDROID
                    var path = await MediaStoreSaver.SaveMediaFileAsync(resultPath, $"{_project.ProjectName}_{DateTime.Now:yyyyMMdd_HHmmss}{ext}", ext switch { ".mp4" => "video/mp4", ".mov" => "video/quicktime", ".mkv" => "video/x-matroska", _ => "video/mp4" }, subFolder: Localized.AppBrand, mediaType: MediaStoreSaver.MediaType.Video);
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

#endif
#if WINDOWS
                    await FileSystemService.ShowFileInFolderAsync(resultPath);
#endif
                }
                catch (Exception ex)
                {
                    Log(ex, "save media", this);
                }


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
            DeviceDisplay.Current.KeepScreenOn = false;
            await PerformPostRenderAction();

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
            label = s;
        }
        _currentSubProgText = label;
        Dispatcher.Dispatch(() =>
        {
            SubProgLabel.Text = label;

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

            var bpp = vm.BitDepth switch
            {
                "8bit" => IPicture.PicturePixelMode.BytePicture,
                _ => IPicture.PicturePixelMode.UShortPicture
            };

            await SubProgress.ProgressTo(0, 250, Easing.Linear);

            var outTempFile = outputPath + ext;
            Directory.CreateDirectory(Path.GetDirectoryName(outTempFile) ?? throw new NullReferenceException());

            int parallelThreadCount = (int)MaxParallelThreadsCount.Value;

#if ANDROID
            ComputerHelper.AddGLViewHandler = new((v) =>
            {
                ComputeView.Children.Clear();
                v.WidthRequest = 50;
                v.HeightRequest = 50;
                ComputeView.Children.Add(v);
            });
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
            var blockwrite = SettingsManager.IsBoolSettingTrue("render_BlockWrite") || OperatingSystem.IsAndroid(); //on Android there is a issue on parallel rendering, use sync render as workaround
            var draftSrc = _draft ?? throw new NullReferenceException();

            Log($"Draft loaded: duration {draftSrc.Duration}, saved on {draftSrc.SavedAt}, {draftSrc.Clips.Length} clips.");

            if (draftSrc.Duration <= 1)
            {
                await DisplayAlertAsync(Localized._Info, "No clips in the draft.", Localized._OK);
                return;
            }

            var duration = Math.Max(draftSrc.Duration, draftSrc.AudioDuration);

            var clips = DraftImportAndExportHelper.JSONToIClips(draftSrc, false).Where(c => c.ClipType != ClipMode.AudioClip).ToArray();

            if (clips == null || clips.Length == 0)
            {
                Log("ERROR: No clips in the whole draft.");
                return;
            }

            foreach (var item in clips)
            {
                await Task.Run(() => item.ReInit(bpp));
            }

            SetSubProg("PrepareDraft");

            int width = int.Parse(vm.Width);
            int height = int.Parse(vm.Height);
            int fps = (int)Math.Round(double.Parse(vm.Framerate));
            var gcOption = int.TryParse(SettingsManager.GetSetting("render_GCOption", "0"), out var value1) ? value1 : 0;

            VideoBuilder builder = new VideoBuilder(outputPath, width, height, fps, enc, fmt)
            {
                EnablePreview = true,
                DoGCAfterEachWrite = gcOption > 0,
                DisposeFrameAfterEachWrite = true,
                Duration = duration,
                LogStat = false,
                BlockWrite = blockwrite
            };

            Renderer renderer = new Renderer
            {
                builder = builder,
                Clips = clips,
                Duration = duration,
                MaxThreads = parallelThreadCount,
                LogState = false,
                LogStatToLogger = true,
                LogProcessStack = SettingsManager.IsBoolSettingTrue("render_DumpDiagData"),
                GCOption = gcOption,
                Use16Bit = bpp == IPicture.PicturePixelMode.UShortPicture
            };

            renderer.OnProgressChanged += (p, etr) =>
            {
                string fpsStr = renderer.CurrentFps > 0 ? $"{renderer.CurrentFps:n2}" : "--";
                var timeStr = etr.TotalSeconds >= 5 ? (etr.TotalHours >= 1 ? etr.ToString(@"hh\:mm\:ss") : etr.ToString(@"mm\:ss")) : "--";
                Dispatcher.Dispatch(async () =>
                {
                    await SubProgress.ProgressTo(p, 250, Easing.Linear);
                    SubProgLabel.Text = $"{_currentSubProgText} ({Localized.RenderPage_LongStat(p, timeStr, fpsStr)})";
                    if (ScreenSaverOverlay.IsVisible)
                    {
                        HintLabel.Text = $"{Localized.RenderPage_ClickToShowUI}{Environment.NewLine}{Localized.RenderPage_Stat(p, timeStr)} | {fpsStr}";
                    }
                });
            };

            builder.OnPreviewGenerated += async (s, e) =>
            {
                if (!_previewUpdateSemaphore.Wait(0))
                {
                    return;
                }

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
                finally
                {
                    await Task.Delay(500);
                    _previewUpdateSemaphore.Release();
                }
            };

            builder?.Build()?.Start();
            await Task.Run(() => renderer.PrepareRender(_cts.Token), _cts.Token);
            if (_cts.IsCancellationRequested) return;

            Stopwatch sw1 = new();
            SetSubProg("Render");
            Log("Start render...");

            sw1.Restart();
            if (blockwrite)
            {
                await Task.Run(async () => await renderer.GoRenderSync(_cts.Token), _cts.Token);
                Log($"Sync render done,total elapsed {sw1}, avg elapsed {renderer.EachElapsedForPreparing.Average(t => t.TotalSeconds)} spf to prepare and {renderer.EachElapsed.Average(t => t.TotalSeconds)} spf to render");
                builder?.Writer.Finish();
            }
            else
            {
                await renderer.GoRender(_cts.Token);
                Log($"Render done,total elapsed {sw1}, avg elapsed {renderer.EachElapsedForPreparing.Average(t => t.TotalSeconds)} spf to prepare and {renderer.EachElapsed.Average(t => t.TotalSeconds)} spf to render");

                SetSubProg("WriteVideo");
                Log("Finish writing video...");
                builder?.Finish((i) => Timeline.MixtureLayers(Timeline.GetFramesInOneFrame(clips, i, width, height), i, width, height), duration);

            }


            Log($"Releasing resources...");

            foreach (var item in clips)
            {
                item?.Dispose();
            }

            // Drop references to large graphs ASAP.
            builder?.Writer?.Dispose();
            builder = null!;
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

            if (SettingsManager.IsBoolSettingTrue("render_DumpDiagData"))
            {
                Guid SessionId = Guid.NewGuid();
                Render.Benchmark.DiagReportExporter.ExportCsv(Path.Combine(MauiProgram.DataPath, "RenderCheckpoint"), renderer);
                int idx = 0, count = 0;
                StreamWriter? sw = null;
                foreach (var item in renderer.FrameProcessStacks.OrderBy(c => c.Key))
                {
                    sw ??= new(new FileStream(Path.Combine(MauiProgram.DataPath, "RenderCheckpoint", $"ProcessStack_{SessionId}_{++idx}.md"), FileMode.Create, FileAccess.Write, FileShare.ReadWrite));
                    await sw.WriteLineAsync($"# Frame {item.Key}");
                    await sw.WriteLineAsync();
                    await sw.WriteLineAsync(PictureProcessStack.FormatProcessStackForLogMarkdown(item.Value));
                    await sw.WriteLineAsync("---");
                    await sw.WriteLineAsync();
                    count++;
                    if (count > 2500) sw = null;
                }
            }


        }
        catch (Exception ex)
        {
            Log(ex, "Render", this);
            throw;
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

        var clips = DraftImportAndExportHelper.JSONToIClips(draftSrc, false).Where(c => c.ClipType == ClipMode.AudioClip || c.ClipType == ClipMode.VideoClip).ToArray();
        var tracks = DraftImportAndExportHelper.JSONToISoundTracks(draftSrc).ToArray();

        if (!clips.ArrayAny() && !tracks.ArrayAny())
        {
            Log("No sound clips in the whole draft. returning...");
            return;
        }

        Log($"Found {clips.Length} audio clips.");

        Log("Initializing all clips...");
        foreach (var clip in clips)
        {
            await Task.Run(() => clip.ReInit(8));
        }
        foreach (var track in tracks)
        {
            await Task.Run(track.ReInit);
        }

        var writer = new AudioWriter(outputPath, 96000, 2, "pcm_s16le");

        var composer = new AudioComposer<float>
        {
            Clips = clips,
            SoundTracks = tracks,
            Writer = writer
        };

        SetSubProg("ComposeAudio");

        composer.OnProgressChanged += (p, etr) =>
        {
            Dispatcher.Dispatch(async () =>
            {
                string timeStr = "";
                if (etr.TotalSeconds > 0)
                {
                    timeStr = (etr.TotalHours >= 1 ? etr.ToString(@"hh\:mm\:ss") : etr.ToString(@"mm\:ss"));
                    SubProgLabel.Text = $"{_currentSubProgText} ({timeStr})";
                }
                await SubProgress.ProgressTo(p, 25, Easing.Linear);

                if (ScreenSaverOverlay.IsVisible)
                {
                    HintLabel.Text = $"{Localized.RenderPage_ClickToShowUI}{Environment.NewLine}{Localized.RenderPage_Stat(p, timeStr)}";
                }
            });
        };

        await Task.Run(() => composer.Compose((int)_draft.TargetFrameRate, 96000, 2, SettingsManager.GetSettingAs<int>("Render_AudioComposeBufferSize", 40960, 40960), _cts.Token));

        writer.Finish();
        writer.Dispose();

        foreach (var item in clips)
        {
            item?.Dispose();
        }
        foreach (var item in tracks)
        {
            item?.Dispose();
        }
        return;

    }



    #endregion

    private async Task PerformPostRenderAction()
    {
        if (BindingContext is RenderPageViewModel vm)
        {
            var action = vm.SelectedPostRenderActionEnum;
#if WINDOWS
            await MainThread.InvokeOnMainThreadAsync(async () =>
            {
                var window = Microsoft.Maui.Controls.Application.Current?.Windows[0];
                if (window?.Handler?.PlatformView is Microsoft.UI.Xaml.Window nativeWindow)
                {
                    var hwnd = WinRT.Interop.WindowNative.GetWindowHandle(nativeWindow);
                    WinUI.App.FlashWindow(hwnd, true);
                }

                if (action == PostRenderAction.None)
                {
                    WinUI.App.MessageBeep(0x00000040);
                    await DisplayAlertAsync(Localized._Info, Localized.RenderPage_Done, Localized._OK);
                    return;
                }
            });
#endif

            if (action == PostRenderAction.None)
                return;

            _countdownCts?.Cancel();
            _countdownCts = new CancellationTokenSource();
            vm.IsCountdownVisible = true;

            try
            {
                for (int i = 30; i > 0; i--)
                {
                    if (_countdownCts.IsCancellationRequested)
                    {
                        Log("Post-render action cancelled by user.");
                        vm.IsCountdownVisible = false;
                        return;
                    }
                    vm.CountdownText = Localized.RenderPage_PostRenderAction_Countdown(i, RenderPageViewModel.PostRenderActionNames.ReverseLookup(action, action.ToString()));
                    await Task.Delay(1000, _countdownCts.Token);
                }
            }
            catch (TaskCanceledException)
            {
                Log("Post-render action cancelled.");
                vm.IsCountdownVisible = false;
                return;
            }

            vm.IsCountdownVisible = false;
            Log($"Performing post-render action: {action}");

            switch (action)
            {
                case PostRenderAction.CloseApp:
                    Environment.Exit(0);
                    break;
                case PostRenderAction.Shutdown:
#if WINDOWS
                    WinUI.App.ExitWindowsEx(0x00000001 | 0x00400000 | 0x00000004 | 0x00000010, 0x00040000 | 0x80000000);
#endif
                    break;
                case PostRenderAction.Hibernate:
#if WINDOWS
                    if (!WinUI.App.SetSuspendState(true, true, false)) //user may disabled hibernate
                    {
                        if (!WinUI.App.SetSuspendState(false, true, false)) //sleep may not available, shutdown
                        {
                            WinUI.App.ExitWindowsEx(0x00000001 | 0x00400000 | 0x00000004 | 0x00000010, 0x00040000 | 0x80000000);
                        }
                    }
#endif

                    break;
            }
        }
    }

    private void CancelPostRenderCountdown_Clicked(object sender, EventArgs e)
    {
        _countdownCts?.Cancel();
    }


    private void MaxParallelThreadsCount_ValueChanged(object sender, ValueChangedEventArgs e)
    {
        MaxParallelThreadsCountLabel.Text = Localized.RenderPage_MaxParallelThreadsCount((int)e.NewValue);
    }

    private async void MoreOptions_Clicked(object sender, EventArgs e)
    {
        await Navigation.PushAsync(new Setting.SettingPages.RenderSettingPage());
    }

    private async void PerformPostRenderActionNowTestButton_Clicked(object sender, EventArgs e)
    {
        await PerformPostRenderAction();
    }

    private void ExportProjectJSONButton_Clicked(object sender, EventArgs e)
    {
        DraftJSONViewer.Text = JsonSerializer.Serialize(_draft, DraftPage.DraftJSONOption);
        DraftJSONViewer.IsVisible = true;
    }

    private async void CancelRender_Clicked(object sender, EventArgs e)
    {
        if (!running) return;
        var sure = await DisplayAlertAsync(Localized._Warn, Localized.RenderPage_CancelRender_Warn, Localized._OK, Localized._Cancel);
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

            MoreOptions.IsEnabled = true;
            PreviewLayout.IsVisible = false;
            running = false;
            UpdateLogRefreshState();
        }
    }

    private async void GenerateStandaloneArgs_Clicked(object sender, EventArgs e)
    {
        if (BindingContext is not RenderPageViewModel vm)
        {
            return;
        }

        if (string.IsNullOrWhiteSpace(_workingPath) || _project is null)
        {
            await DisplayAlertAsync(Localized._Info, Localized.RenderPage_NoDraft, Localized._OK);
            return;
        }

        if (!TryParseRenderSettings(vm, out var width, out var height, out var fps))
        {
            await DisplayAlertAsync(Localized._Error, "Invalid render settings.", Localized._OK);
            return;
        }

        var (pixelFormat, encoder, ext) = GetStandaloneOutputOptions(vm.BitDepth);

#if WINDOWS
        var outputPath = await FileSystemService.PickASavePath($"{_project.ProjectName}_{DateTime.Now:yyyyMMdd_HHmmss}{ext}", MauiProgram.DataPath);
#else
        var outputDir = Path.Combine(MauiProgram.DataPath, "RenderCache");
        var projectName = string.IsNullOrWhiteSpace(_project.ProjectName) ? "project" : _project.ProjectName;
        var outputPath = Path.Combine(outputDir, $"{projectName}_{DateTime.Now:yyyyMMdd_HHmmss}{ext}");
#endif


        var args = BuildStandaloneRenderArgs(width, height, fps, pixelFormat, encoder, outputPath);
        await DisplayPromptAsync("Standalone render args", "Copy the args below:", "OK", "Cancel", initialValue: args);
    }

    private static bool TryParseRenderSettings(RenderPageViewModel vm, out int width, out int height, out int fps)
    {
        width = 0;
        height = 0;
        fps = 0;

        if (!int.TryParse(vm.Width, NumberStyles.Integer, CultureInfo.InvariantCulture, out width))
        {
            return false;
        }

        if (!int.TryParse(vm.Height, NumberStyles.Integer, CultureInfo.InvariantCulture, out height))
        {
            return false;
        }

        if (!double.TryParse(vm.Framerate, NumberStyles.Float, CultureInfo.InvariantCulture, out var fpsDouble))
        {
            return false;
        }

        fps = (int)Math.Round(fpsDouble);
        return width > 0 && height > 0 && fps > 0;
    }

    private static (string PixelFormat, string Encoder, string Extension) GetStandaloneOutputOptions(string bitDepth)
    {
        return bitDepth switch
        {
            "8bit" => ("AV_PIX_FMT_YUV420P", "libx264", ".mp4"),
            "10bit" => ("AV_PIX_FMT_YUV420P10LE", "libx265", ".mov"),
            "12bit" => ("AV_PIX_FMT_YUV444P12LE", "libx265", ".mov"),
            _ => ("AV_PIX_FMT_GBRP16LE", "ffv1", ".mkv")
        };
    }


    private string BuildStandaloneRenderArgs(int width, int height, int fps, string pixelFormat, string encoder, string outputPath)
    {
        var args = new List<string>
        {
            $"-project={_workingPath}",
            $"-output={outputPath}",
            $"-output_options={width},{height},{fps},{pixelFormat},{encoder}",
            $"-assetDbFile={Path.Combine(MauiProgram.DataPath, "My Assets", ".database", "database.json")}"
        };

        var maxThreads = Math.Max(1, (int)Math.Round(MaxParallelThreadsCount.Value));
        args.Add($"-maxParallelThreads={maxThreads}");

        if (SettingsManager.IsBoolSettingTrue("render_BlockWrite"))
        {
            args.Add("-oneByOneRender=true");
        }

        if (int.TryParse(SettingsManager.GetSetting("render_GCOption", "0"), out var gcOption) && gcOption is >= 0 and <= 2)
        {
            args.Add($"-GCOptions={gcOption}");
        }

#if WINDOWS
        string accelId = "";
        args.Add($"-multiAccelerator={SettingsManager.IsBoolSettingTrue("accel_enableMultiAccel")}");

        if (SettingsManager.IsBoolSettingTrue("accel_enableMultiAccel"))
        {
            args.Add($"-acceleratorDeviceIds={SettingsManager.GetSetting("accel_MultiDeviceID", "all")}");

        }
        else
        {
            args.Add($"-acceleratorDeviceId={SettingsManager.GetSetting("accel_DeviceId", "")}");
        }

#endif

        return "render  " + string.Join(" ", args.Select(s => $"\"{s}\""));
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

    public static Dictionary<string, PostRenderAction> PostRenderActionNames = Enum.GetNames(typeof(PostRenderAction))
        .Select(s => (Localized.DynamicLookup($"RenderPage_PostRenderAction_{s}"), Enum.Parse<PostRenderAction>(s)))
        .ToDictionary(t => t.Item1, t => t.Item2);

    public string[] PostRenderActions { get; } = PostRenderActionNames.Keys.ToArray();

    PostRenderAction _selectedPostRenderAction = PostRenderAction.None;

    public string SelectedPostRenderAction
    {
        get => PostRenderActionNames.ReverseLookup(_selectedPostRenderAction, Localized.RenderPage_PostRenderAction_None) ?? "None";
        set
        {
            _selectedPostRenderAction = PostRenderActionNames.GetValueOrDefault(value, PostRenderAction.None);
            SetProperty(ref _selectedPostRenderAction, _selectedPostRenderAction);
        }
    }

    public PostRenderAction SelectedPostRenderActionEnum
    {
        get => _selectedPostRenderAction;
        set
        {
            _selectedPostRenderAction = value;
            OnPropertyChanged(nameof(SelectedPostRenderAction));
        }
    }

    bool _isCountdownVisible = false;
    public bool IsCountdownVisible
    {
        get => _isCountdownVisible;
        set => SetProperty(ref _isCountdownVisible, value);
    }

    string _countdownText = "";
    public string CountdownText
    {
        get => _countdownText;
        set => SetProperty(ref _countdownText, value);
    }

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
