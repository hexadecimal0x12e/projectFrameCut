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
using projectFrameCut.Services;
using projectFrameCut.DraftStuff;
using projectFrameCut.Render.EncodeAndDecode;
using JsonElement = System.Text.Json.JsonElement;
using System.Runtime.InteropServices;

using projectFrameCut.ApplicationAPIBase.Helpers;
using System.Globalization;
using IPicture = projectFrameCut.Drawing.Base.IPicture;

using static System.Net.Mime.MediaTypeNames;
using projectFrameCut.Render.Compose;
using System.Reflection;
using projectFrameCut.Drawing.Base;
using projectFrameCut.Render.HwAccelEngine;
using projectFrameCut.Render.RenderAPIBase.Context;
using projectFrameCut.Render.Benchmark;
using projectFrameCut.Render.Contracts;





#if ANDROID
using projectFrameCut.Render.HwAccelEngine.Platforms.Android;
using projectFrameCut.Platforms.Android;

#elif WINDOWS
using projectFrameCut.Render.HwAccelEngine.Platforms.Windows;
using Woohoo.Platform.Windows.Taskbar;

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

    public bool ProjectUsesHDR => _project.Properties.TryGetValue("EnableHDR", out var enableHDR) && bool.TryParse(enableHDR, out var enableHDRBool) && enableHDRBool;


#if WINDOWS
    Platforms.Windows.ffmpegHelper ffmpeg = new projectFrameCut.Platforms.Windows.ffmpegHelper();
#endif

    private CancellationTokenSource _cts = new CancellationTokenSource();
    private Guid _renderRpcSessionId = Guid.NewGuid();
    private Guid? _activeRenderRpcJobId;
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
        SizeChanged += (_, _) => UpdatePreviewViewportSizing();
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
        OnPropertyChanged(nameof(ProjectUsesHDR));
        HDRHintLabel.IsVisible = ProjectUsesHDR;

        _draft = draft;
        Title = Localized.RenderPage_ExportTitle(projectInfo.ProjectName);
        ScreenSaverOverlay.InputTransparent = true;
        ScreenSaverOverlay.CascadeInputTransparent = true;
        var vm = new RenderPageViewModel(ProjectUsesHDR);
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
            if (ProjectUsesHDR)
            {
                vm.Encoding = vm.Encoding switch
                {
                    "h265/hevc" => "h265",
                    "hevc" => "h265",
                    "libx265" => "h265",
                    "h265" => "h265",
                    _ => "h265"
                };
                vm.BitDepth = "10bit";
            }
            if (SettingsManager.IsBoolSettingTrueOrDefault("render_enableThreadAffinity", true))
            {
                MaxParallelThreadsCountLabel.IsVisible = false;
                MaxParallelThreadsCount.IsVisible = false;
                MaxParallelThreadsCount.Value = Environment.ProcessorCount;//fallback
            }
            else
            {
                MaxParallelThreadsCount.Value = (int)SettingsManager.GetSettingAs<double>("render_defaultMaxParallelWorkers", 8, 8);
            }
        }
        catch { }
        BindingContext = vm;
        SizeChanged += (_, _) => UpdatePreviewViewportSizing();
        MaxParallelThreadsCountLabel.Text = Localized.RenderPage_MaxParallelThreadsCount((int)MaxParallelThreadsCount.Value);
        CancelRender.IsEnabled = false;
        DebugView.IsVisible = SettingsManager.IsBoolSettingTrue("DeveloperMode");
        InitializeLogTimer();
        InitializeLogPanel();
        InitializeScreenSaverTimer();

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
        LoggingRowDefinition.Height = visible ? GridLength.Star : new GridLength(0);
        UpdateRenderLayoutForLogPanel();
        UpdateLogPanelToggleText();
        UpdateLogRefreshState();

        if (visible)
        {
            _ = FlushLogQueue();
        }
    }

    private void UpdateRenderLayoutForLogPanel()
    {
        if (!running)
        {
            PreviewRowDefinition.Height = GridLength.Auto;
            ProgressRowDefinition.Height = GridLength.Star;
            PreviewLayout.VerticalOptions = LayoutOptions.Start;
            if (PreviewLayout.RowDefinitions.Count > 1)
            {
                PreviewLayout.RowDefinitions[1].Height = GridLength.Auto;
            }
            UpdatePreviewViewportSizing();
            return;
        }

        if (_isLogPanelVisible)
        {
            PreviewRowDefinition.Height = GridLength.Auto;
            ProgressRowDefinition.Height = GridLength.Star;
            PreviewLayout.VerticalOptions = LayoutOptions.Start;
            if (PreviewLayout.RowDefinitions.Count > 1)
            {
                PreviewLayout.RowDefinitions[1].Height = GridLength.Auto;
            }
        }
        else
        {
            PreviewRowDefinition.Height = GridLength.Star;
            ProgressRowDefinition.Height = GridLength.Auto;
            PreviewLayout.VerticalOptions = LayoutOptions.Fill;
            if (PreviewLayout.RowDefinitions.Count > 1)
            {
                PreviewLayout.RowDefinitions[1].Height = GridLength.Star;
            }
        }

        UpdatePreviewViewportSizing();
    }

    private void UpdatePreviewViewportSizing()
    {
        if (PreviewBorder is null || PreviewLayout is null)
        {
            return;
        }

        if (PreviewLayout.Width <= 0 || PreviewLayout.Height <= 0)
        {
            return;
        }

        var horizontalPadding = 64d;
        var availableWidth = Math.Max(0, PreviewLayout.Width - horizontalPadding);
        var availableHeight = Math.Max(0, PreviewLayout.Height - 48);

        if (availableWidth <= 0 || availableHeight <= 0)
        {
            return;
        }

        var heightRatio = _isLogPanelVisible ? 0.72d : 0.9d;
        var widthRatio = _isLogPanelVisible ? 0.88d : 0.96d;

        PreviewBorder.MaximumWidthRequest = availableWidth * widthRatio;
        PreviewBorder.MaximumHeightRequest = availableHeight * heightRatio;
        PreviewImage.MaximumWidthRequest = PreviewBorder.MaximumWidthRequest - 8;
        PreviewImage.MaximumHeightRequest = PreviewBorder.MaximumHeightRequest - 8;
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
            const int maxBatchSize = 50; // ÿ����ദ�� 50 ����־

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
        if (running) return true;
        Navigation.PopToRootAsync();
        return true;
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
        try
        {
            var cacheDir = Path.Combine(MauiProgram.DataPath, "RenderCache");
            Directory.CreateDirectory(cacheDir);
            await PrepareUIForRender();

            if (BindingContext is RenderPageViewModel vm)
            {
                var fmt = vm.BitDepth switch
                {
                    "8bit" => "AV_PIX_FMT_YUV420P",
                    "10bit" => "AV_PIX_FMT_YUV420P10LE",
                    "12bit" => "AV_PIX_FMT_YUV420P10LE",
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
                    "libx265" => ".mp4",
                    "ffv1" => ".mkv",
                    _ => ".mp4"
                };

                running = true;
                DeviceDisplay.Current.KeepScreenOn = true;
                Log("Output options:\r\n" + vm.BuildSummary());
                string vidOutputPath = Path.Combine(cacheDir, $"{_project.ProjectName}_{DateTime.Now:yyyyMMdd_HHmmss}{ext}");
                string audOutputPath = Path.Combine(cacheDir, $"{_project.ProjectName}_{DateTime.Now:yyyyMMdd_HHmmss}.wav");
                string compOutputPath = Path.Combine(cacheDir, $"{_project.ProjectName}_{DateTime.Now:yyyyMMdd_HHmmss}.composed{ext}");
#if WINDOWS
                var resultPath = await FileSystemService.PickASavePath($"{_project.ProjectName}_{DateTime.Now:yyyyMMdd_HHmmss}{ext}", MauiProgram.DataPath);
                if (string.IsNullOrWhiteSpace(resultPath)) return;
#else
                string resultPath = compOutputPath;
#endif
                if (_cts.IsCancellationRequested) return;

                var mtdDict = new Dictionary<string, string>
                {
                    { "title", _project.ProjectName ?? "Project" },
                    { "author", $"projectFrameCut user - {SettingsManager.GetSetting("UserName", "User")}" },
                    { "artist", $"projectFrameCut user - {SettingsManager.GetSetting("UserName", "User")}" },
                    { "language", new CultureInfo(Localized._LocaleId_).ThreeLetterISOLanguageName },
                    { "year", DateTime.Now.Year.ToString() },
                    { "encoder", $"{MauiProgram.AssemblyName} v{Assembly.GetExecutingAssembly().GetName().Version} ({MauiProgram.ProgramConfig}@{MauiProgram.ProgramCommit})" },
                    { "copyright", $"Made by {Localized.AppBrand}" }
                };

                // The first RPC render path intentionally targets the common SDR/H.264 case.
                // HDR, diagnostic/blackhole writers and specialized render settings retain the
                // legacy in-process compatibility path until those options are represented by the protocol.
                if (!ProjectUsesHDR
                    && vm.BitDepth == "8bit"
                    && string.Equals(enc, "libx264", StringComparison.OrdinalIgnoreCase)
                    && Math.Abs(double.Parse(vm.Framerate) - Math.Round(double.Parse(vm.Framerate))) <= 0.001)
                {
                    await RenderProjectViaRpcAsync(vm, resultPath, enc, fmt);
#if ANDROID
                    var savedPath = await MediaStoreSaver.SaveMediaFileAsync(resultPath, Path.GetFileName(resultPath), "video/mp4", subFolder: Localized.AppBrand, mediaType: MediaStoreSaver.MediaType.Video);
                    if (!string.IsNullOrWhiteSpace(savedPath) && !SettingsManager.IsBoolSettingTrue("DeveloperMode"))
                    {
                        try { File.Delete(resultPath); } catch { }
                    }
#elif WINDOWS
                    await FileSystemService.ShowFileInFolderAsync(resultPath);
#endif
                    DeviceDisplay.Current.KeepScreenOn = false;
                    return;
                }

                try
                {
                    await ComposeAudio(vm, audOutputPath);

                }
                catch (Exception ex)
                {

                    Log(ex, "compose audio", this);
                    await DisplayAlertAsync(Localized._Error, Localized.RenderPage_Fail(ex), Localized._OK);
                    if (Debugger.IsAttached && await DisplayAlertAsync(Localized._Info, "Throw?", Localized._OK, Localized._Cancel)) throw;
                    return;
                }
                if (_cts.IsCancellationRequested) return;

                try
                {
                    await DoCompute(vm, vidOutputPath, mtdDict, audOutputPath);
                }
                catch (Exception ex)
                {
                    Log(ex, "render frames", this);
                    await DisplayAlertAsync(Localized._Error, Localized.RenderPage_Fail(ex), Localized._OK);
                    if (Debugger.IsAttached && await DisplayAlertAsync(Localized._Info, "Throw?", Localized._OK, Localized._Cancel)) throw;
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
                        await Task.Run(async () =>
                        {
                            string args = $"-i \"{tempVid}\" -r {targetFps} -c:v {enc} -crf 18 -preset fast \"{vidOutputPath}\"";
                            if (enc == "ffv1") args = $"-i \"{tempVid}\" -r {targetFps} -c:v ffv1 \"{vidOutputPath}\"";

                            await ffmpeg.Run(args);
                        }, _cts.Token);
                        File.Delete(tempVid);
                    }
#endif
                }


                if (_cts.IsCancellationRequested) return;
                SetSubProg("FinalEncoding");

                await Task.Run(async () =>
                {
                    try
                    {
                        VideoAudioMuxer.MuxFromFiles(vidOutputPath, audOutputPath, resultPath, true, mtdDict);
                        if (!SettingsManager.IsBoolSettingTrue("DeveloperMode"))
                        {
                            try
                            {
                                File.Delete(vidOutputPath);
                                File.Delete(audOutputPath);
                            }
                            catch { }
                        }
                    }
                    catch (Exception ex)
                    {
                        Log(ex, "compose media", this);
                        if (Debugger.IsAttached && await DisplayAlertAsync(Localized._Info, "Throw?", Localized._OK, Localized._Cancel)) throw;
                        await Dispatcher.DispatchAsync(async () =>
                        {
                            await DisplayAlertAsync(Localized._Error, Localized.RenderPage_Fail(ex), Localized._OK);
                        });
                        return;
                    }

                });


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
                    //await Task.Run(() => File.Move(compOutputPath, resultPath));

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
            if (Debugger.IsAttached && await DisplayAlertAsync(Localized._Info, "Throw?", Localized._OK, Localized._Cancel)) throw;
            return;
        }
        finally
        {
            await CleanupUIForRenderDone();
        }

    }

    private async Task RenderProjectViaRpcAsync(RenderPageViewModel vm, string resultPath, string encoder, string pixelFormat)
    {
        SetSubProg("PrepareDraft");
        var openRequest = new OpenProjectRequest
        {
            SessionId = _renderRpcSessionId,
            ProjectRoot = _workingPath,
            ProjectJson = JsonSerializer.Serialize(_project, DraftPage.DraftJSONOption),
            TimelineJson = JsonSerializer.Serialize(_draft, DraftPage.DraftJSONOption),
            ProjectWidth = Math.Max(1, _project.RelativeWidth),
            ProjectHeight = Math.Max(1, _project.RelativeHeight),
            FrameRate = Math.Max(1, (int)_project.TargetFrameRate),
            ProxyRoot = Path.Combine(_workingPath, "proxy"),
            Assets = Asset.AssetDatabase.Assets.Select(static item => new AssetPathEntry
            {
                AssetId = item.Key,
                Path = item.Value.Path ?? string.Empty,
            }).Where(static item => !string.IsNullOrWhiteSpace(item.Path)).ToList(),
        };
        await RenderRpcBootstrap.Client.OpenProjectAsync(openRequest, _cts.Token);

        SetSubProg("Render");
        var job = await RenderRpcBootstrap.Client.RenderProjectAsync(new RenderProjectRequest
        {
            SessionId = _renderRpcSessionId,
            Width = int.Parse(vm.Width),
            Height = int.Parse(vm.Height),
            FrameRate = (int)Math.Round(double.Parse(vm.Framerate)),
            Encoder = encoder,
            PixelFormat = pixelFormat,
            IncludeAudio = true,
            OutputFileName = Path.GetFileName(resultPath),
        }, _cts.Token);
        _activeRenderRpcJobId = job.JobId;

        try
        {
            while (job.State is RenderJobState.Queued or RenderJobState.Running)
            {
                _cts.Token.ThrowIfCancellationRequested();
                await SubProgress.ProgressTo(job.Progress, 100, Easing.Linear);
                var eta = TimeSpan.FromTicks(Math.Max(0, job.EstimatedRemainingTicks));
                SubProgLabel.Text = $"{_currentSubProgText} ({job.Progress:P1}, ETA {eta:hh\\:mm\\:ss})";
                await Task.Delay(150, _cts.Token);
                job = await RenderRpcBootstrap.Client.GetJobStatusAsync(job.JobId, _cts.Token);
            }

            if (job.State == RenderJobState.Canceled) throw new OperationCanceledException(_cts.Token);
            if (job.State != RenderJobState.Completed || job.Artifact is null)
                throw new InvalidOperationException(job.Error?.Message ?? $"Render job ended in state {job.State}.");

            var artifactPath = RenderRpcBootstrap.ResolveArtifactPath(_workingPath, job.Artifact);
            Directory.CreateDirectory(Path.GetDirectoryName(resultPath) ?? throw new InvalidOperationException("Output path has no parent directory."));
            File.Copy(artifactPath, resultPath, overwrite: true);
            await SubProgress.ProgressTo(1, 100, Easing.Linear);
        }
        catch (OperationCanceledException)
        {
            try { await RenderRpcBootstrap.Client.CancelJobAsync(job.JobId); } catch { }
            throw;
        }
        finally
        {
            _activeRenderRpcJobId = null;
            try { await RenderRpcBootstrap.Client.CloseProjectAsync(_renderRpcSessionId); } catch { }
        }
    }

    private async void RenderToVoidButton_Clicked(object sender, EventArgs e)
    {
        try
        {
            await PrepareUIForRender();

            if (BindingContext is RenderPageViewModel vm)
            {
                running = true;
                DeviceDisplay.Current.KeepScreenOn = true;

                try
                {
                    await DoCompute(vm, "", null!, null, "blank");
                }
                catch (Exception ex)
                {
                    Log(ex, "render frames", this);
                    await DisplayAlertAsync(Localized._Error, Localized.RenderPage_Fail(ex), Localized._OK);
                    if (Debugger.IsAttached && await DisplayAlertAsync(Localized._Info, "Throw?", Localized._OK, Localized._Cancel)) throw;
                    return;
                }

                DeviceDisplay.Current.KeepScreenOn = false;
            }
        }
        catch (Exception ex)
        {
            Log(ex, "render", this);
            await DisplayAlertAsync(Localized._Error, Localized.RenderPage_Fail(ex), Localized._OK);
            if (Debugger.IsAttached && await DisplayAlertAsync(Localized._Info, "Throw?", Localized._OK, Localized._Cancel)) throw;
            return;
        }
        finally
        {
            await CleanupUIForRenderDone();
        }

    }

    private async void RenderNoWritingButton_Clicked(object sender, EventArgs e)
    {
        try
        {
            await PrepareUIForRender();

            if (BindingContext is RenderPageViewModel vm)
            {
                running = true;
                DeviceDisplay.Current.KeepScreenOn = true;

                try
                {
                    await DoCompute(vm, "", null!, null, "null");
                }
                catch (Exception ex)
                {
                    Log(ex, "render frames", this);
                    await DisplayAlertAsync(Localized._Error, Localized.RenderPage_Fail(ex), Localized._OK);
                    if (Debugger.IsAttached && await DisplayAlertAsync(Localized._Info, "Throw?", Localized._OK, Localized._Cancel)) throw;
                    return;
                }

                DeviceDisplay.Current.KeepScreenOn = false;
            }
        }
        catch (Exception ex)
        {
            Log(ex, "render", this);
            await DisplayAlertAsync(Localized._Error, Localized.RenderPage_Fail(ex), Localized._OK);
            if (Debugger.IsAttached && await DisplayAlertAsync(Localized._Info, "Throw?", Localized._OK, Localized._Cancel)) throw;
            return;
        }
        finally
        {
            await CleanupUIForRenderDone();
        }
    }


    double totalProg = 0, lastProg = 0;
    string _currentSubProgText = "";
    VideoBuilder? builder = null;


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

    async Task DoCompute(RenderPageViewModel vm, string outputPath, Dictionary<string, string>? metadata = null, string? audioPath = null, string? writerOverride = null)
    {
        try
        {
            var fmt = vm.BitDepth switch
            {
                "8bit" => "AV_PIX_FMT_YUV420P",
                "10bit" => "AV_PIX_FMT_YUV420P10LE",
                "12bit" => "AV_PIX_FMT_YUV420P10LE",
                _ => "AV_PIX_FMT_GBRP16LE"
            };
            var enc = vm.Encoding;
            var ext = vm.Encoding switch
            {
                "libx264" => ".mp4",
                "h264" => ".mp4",
                "libx265" => ".mp4",
                "h265" => ".mp4",
                "h265/hevc" => ".mp4",
                "hevc" => ".mp4",
                "av1" => ".mkv",
                "ffv1" => ".mkv",
                _ => ".mkv"
            };

            var bpp = vm.BitDepth switch
            {
                "8bit" => IPicture.PicturePixelMode.BytePicture,
                _ => IPicture.PicturePixelMode.UShortPicture
            };

            if (ProjectUsesHDR)
            {
                bpp = IPicture.PicturePixelMode.UShortPicture;
                fmt = "AV_PIX_FMT_YUV420P10LE";
                ext = ".mp4";
                enc = "libx265";
            }
            bool dumpDiagData = SettingsManager.IsBoolSettingTrue("render_DumpDiagData");

            if (dumpDiagData && PictureLifecycleTracker.Enabled)
            {
                PictureLifecycleTracker.Clear();
            }

            await SubProgress.ProgressTo(0, 250, Easing.Linear);

            int[] CPUAffinityOverride = Array.Empty<int>(), preparerAffinityCpuIndexes = [];
            bool EnableThreadAffinity = SettingsManager.IsBoolSettingTrueOrDefault("render_enableThreadAffinity", true);
            if (EnableThreadAffinity)
            {
                try
                {
                    if (SettingsManager.IsSettingExists("render_coreAffinityOverride") && !string.IsNullOrWhiteSpace(SettingsManager.GetSetting("render_coreAffinityOverride", "")))
                    {
                        CPUAffinityOverride = SettingsManager.GetSetting("render_coreAffinityOverride", "0").Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Where(c => uint.TryParse(c, out _)).Select(int.Parse).ToArray();
                    }
                    else
                    {
                        try
                        {
                            var group = ThreadAffinityHelper.GetCpuCoreGroups();
                            var bigGroup = group.OrderBy(c => c.MaxFrequencyKHz ?? 0 + c.Capacity ?? 0 + c.EfficiencyClass ?? 0).Last();
                            CPUAffinityOverride = bigGroup.CpuIndexes.ToArray();

                        }
                        catch { }
                    }
                }
                catch { }
                preparerAffinityCpuIndexes = CPUAffinityOverride.ArrayAny() ? Enumerable.Range(0, Environment.ProcessorCount).Except(CPUAffinityOverride).ToArray() : [];

            }
            int parallelThreadCount = CPUAffinityOverride.Length > 0 ? CPUAffinityOverride.Length : (int)MaxParallelThreadsCount.Value;
            if (CPUAffinityOverride.Length > 0 && (DeviceInfo.Idiom == DeviceIdiom.Desktop || OperatingSystem.IsIOS())) parallelThreadCount = (int)(parallelThreadCount * 1.5);

            Log($"Parallel options: Physical core count: {Environment.ProcessorCount}, Enable Thread Affinity: {EnableThreadAffinity}, Prepare in worker: {SettingsManager.IsBoolSettingTrueOrDefault("render_prepareInWorker", true)}, Worker target cores: {string.Join(",", CPUAffinityOverride)}, parallelThreadCount: {parallelThreadCount}");

#if ANDROID
            ComputerHelper.AddPlatformComputeViewHandler = new((v) =>
            {
                ComputeView.Children.Clear();
                v.WidthRequest = 50;
                v.HeightRequest = 50;
                ComputeView.Children.Add(v);
            });
            ComputerHelper.Init();
#elif iDevices

#elif WINDOWS
            // AcceleratorsManager was initialized during plugin load.
            // The configured accelerators (from accels.json) are ready to use.
            AcceleratorsManager.IsRendering = true;
            if (!AcceleratorsManager.Accelerators.Any()) throw new InvalidDataException("No valid ILGPU accelerators found.");

            TaskbarManager.Instance.SetProgressState(TaskbarProgressBarState.Indeterminate);
#endif
            var blockwrite = SettingsManager.IsBoolSettingTrue("render_BlockWrite");
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

            if (!string.IsNullOrWhiteSpace(writerOverride))
            {
                switch (writerOverride)
                {
                    case "blank":
                        Log("writeToVoid is enabled, no file will be written, only rendering will be performed.", "warn");
                        builder = new VideoBuilder(new BlackholeVideoWriter() { Width = width, Height = height, FramePerSecond = fps, PixelFormat = fmt, OutputPath = "/dev/null" })
                        {
                            EnablePreview = true,
                            minFrameCountToGeneratePreview = 1,
                            DoGCAfterEachWrite = gcOption > 0,
                            DisposeFrameAfterEachWrite = true,
                            Duration = duration,
                            LogStat = false,
                            BlockWrite = blockwrite,
                            EnableDiskCacheRouting = SettingsManager.IsBoolSettingTrueOrDefault("render_enableDiskCacheRouting", true),
                            DiskCacheMaxFrameCount = SettingsManager.GetSettingAs("render_MaxDiskBufferCount", 500, 500),
                            DiskCacheThreshold = SettingsManager.GetSettingAs("render_DiskBufferThreshold", 0.7, 0.7),
                            DiskCacheDirectory = Path.Combine(VideoFrameDiskCache.CacheBaseDir ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "VideoCache"), "RenderingCache")
                        };
                        break;
                    case "null":
                        Log("writer is disabled.", "warn");
                        builder = null; 
                        break;
                }
            }
            else
            {
                if (string.IsNullOrWhiteSpace(outputPath)) throw new InvalidOperationException("No output path specified for rendering.");
                Directory.CreateDirectory(Path.GetDirectoryName(outputPath) ?? throw new NullReferenceException());

                builder = new VideoBuilder(outputPath, width, height, fps, enc, fmt, ProjectUsesHDR ? "HDRVideoWriter" : null)
                {
                    EnablePreview = true,
                    DoGCAfterEachWrite = gcOption > 0,
                    DisposeFrameAfterEachWrite = true,
                    Duration = duration,
                    LogStat = false,
                    BlockWrite = blockwrite
                };
            }


            builder?.Writer?.Metadata = metadata ?? new();

            Renderer renderer = new Renderer
            {
                builder = builder,
                Clips = clips,
                TargetWidth = width,
                TargetHeight = height,
                ProjectRelativeWidth = Math.Max(1, _project.RelativeWidth),
                ProjectRelativeHeight = Math.Max(1, _project.RelativeHeight),
                Duration = duration,
                LogRenderState = false,
                LogStaticsData = true,
                LogProcessStack = dumpDiagData,
                GCOption = gcOption,
                Use16Bit = bpp == IPicture.PicturePixelMode.UShortPicture,
                MaxThreads = parallelThreadCount,
                EnableThreadAffinity = EnableThreadAffinity && ThreadAffinityHelper.GetCpuCoreGroups().Count > 1,
                WorkerCPUCoreIndexs = CPUAffinityOverride,
                OneByOneRender = blockwrite,
                PrepareInWorkerThreads = SettingsManager.IsBoolSettingTrueOrDefault("render_prepareInWorkerThreads", true),
                AllowReorderEffect = SettingsManager.IsBoolSettingTrueOrDefault("render_allowEffectOutOfOrder", true),
                EnableGPUBatchProcess = SettingsManager.IsBoolSettingTrueOrDefault("render_enableBatchProcess", true),
                RenderByLayers = SettingsManager.IsBoolSettingTrueOrDefault("render_RenderByLayer", true),
                EnableRenderWatchdogForceStart = DeviceInfo.Idiom != DeviceIdiom.Desktop,
                MinSchedulePreparedFrames = parallelThreadCount,
                MaxPendingWriteFrames = SettingsManager.GetSettingAs("render_maxPendingWriteFrames", (int)(Environment.WorkingSet / ((width * height * (bpp.Value / 8) * 3) + 32)) / 2, 150),
                UseHDR = ProjectUsesHDR,
                MaximumHDRBrightness = _project.Properties.TryGetValue("HdrMaximumBrightness", out var maxHdrBrightness) && int.TryParse(maxHdrBrightness, out var maxHdrBrightnessInt) ? maxHdrBrightnessInt : 1000,
                SDRClipsBrightnessInHDRMode =
                    _project.Properties.TryGetValue("SdrClipBrightness", out var sdrBrightnessInHdr) && int.TryParse(sdrBrightnessInHdr, out var sdrBrightnessInHdrInt)
                        ? sdrBrightnessInHdrInt
                        : (_project.Properties.TryGetValue("sdrClipBrightness", out var legacySdrBrightnessInHdr) && int.TryParse(legacySdrBrightnessInHdr, out var legacySdrBrightnessInHdrInt)
                            ? legacySdrBrightnessInHdrInt
                            : 203),
                AudioFilePath = audioPath
            };

            renderer.OnProgressChanged += (p, etr) =>
            {
                string fpsStr = renderer.CurrentFps > 0 ? $"{renderer.CurrentFps:n2}" : "--";
                var timeStr = etr.TotalSeconds >= 5 ? (etr.TotalHours >= 1 ? etr.ToString(@"hh\:mm\:ss") : etr.ToString(@"mm\:ss")) : "--";
                Dispatcher.Dispatch(async () =>
                {
                    await SubProgress.ProgressTo(p, 250, Easing.Linear);
                    SubProgLabel.Text = $"{_currentSubProgText} ({(renderer.CurrentSecondPerFrame <= 1.5 ? Localized.RenderPage_LongStat(p, timeStr, fpsStr) : Localized.RenderPage_LongStat_SecondPerFrame(p, timeStr, renderer.CurrentFps > 0 ? $"{(1 / renderer.CurrentFps):n2}" : "--"))})";
                    if (ScreenSaverOverlay.IsVisible)
                    {
                        HintLabel.Text = $"{Localized.RenderPage_ClickToShowUI}{Environment.NewLine}{Localized.RenderPage_Stat(p, timeStr)} | {fpsStr}fps";
                    }
                });

#if WINDOWS
                TaskbarManager.Instance.SetProgressValue((int)(p * 100), 100);
#endif
            };

            builder?.OnPreviewGenerated += async (s, e) =>
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
                    await Task.Delay(50);
                    _previewUpdateSemaphore.Release();
                }
            };

            builder?.Build(preparerAffinityCpuIndexes)?.Start();
            await Task.Run(() => renderer.PrepareRender(_cts.Token), _cts.Token);
            if (_cts.IsCancellationRequested) return;

            Stopwatch sw1 = new();
            SetSubProg("Render");
            Log("Start render...");

            sw1.Restart();
            await Task.Run(async () => await renderer.GoRender(_cts.Token), _cts.Token);
            Log($"Render done,total elapsed {sw1}, avg elapsed {renderer.EachElapsedForPreparing.Average(t => t.TotalSeconds)} spf to prepare and {renderer.EachElapsed.Average(t => t.TotalSeconds)} spf to render");

            if (blockwrite)
            {
                SetSubProg("WriteVideo");
                Log("Closing result video stream...");
                builder?.Writer?.Finish();
            }
            else
            {
                SetSubProg("WriteVideo");
                Log("Finish writing video...");
                await Task.Run(() =>
                {
                    int projectRelativeWidth = Math.Max(1, _project.RelativeWidth);
                    int projectRelativeHeight = Math.Max(1, _project.RelativeHeight);
                    builder?.Finish((i) => Timeline.MixtureLayers(
                        Timeline.GetFramesInOneFrame(
                            clips,
                            i,
                            width,
                            height,
                            projectRelativeWidth: projectRelativeWidth,
                            projectRelativeHeight: projectRelativeHeight),
                        i,
                        width,
                        height,
                        projectRelativeWidth: projectRelativeWidth,
                        projectRelativeHeight: projectRelativeHeight),
                        duration,
                        (_, p) =>
                        {
                            Dispatcher.Dispatch(async () =>
                            {
                                await SubProgress.ProgressTo(p, 250, Easing.Linear);
                                SubProgLabel.Text = $"{_currentSubProgText} ({p:p2})";

                            });
                        });
                });


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
            TaskbarManager.Instance.SetProgressState(TaskbarProgressBarState.NoProgress);
#else
            GC.Collect();
            GC.WaitForPendingFinalizers();
#endif

            Log($"All done! Total elapsed {sw1}.");

            if (dumpDiagData)
            {
                Guid SessionId = Guid.NewGuid();
                string renderCheckpointPath = Path.Combine(MauiProgram.DataPath, "RenderDiag");
                Render.Benchmark.DiagReportExporter.ExportCsv(Path.Combine(renderCheckpointPath, $"RenderDiag-{SessionId}.csv"), renderer);
                int idx = 0, count = 0;
                StreamWriter? sw = null;
                foreach (var item in renderer.FrameProcessStacks.OrderBy(c => c.Key))
                {
                    sw ??= new(new FileStream(Path.Combine(renderCheckpointPath, $"ProcessStack-{SessionId}_{++idx}.md"), FileMode.Create, FileAccess.Write, FileShare.ReadWrite));
                    await sw.WriteLineAsync($"# Frame {item.Key}");
                    await sw.WriteLineAsync();
                    await sw.WriteLineAsync(PictureProcessStack.FormatProcessStackForLogMarkdown(item.Value));
                    await sw.WriteLineAsync("---");
                    await sw.WriteLineAsync();
                    count++;
                    if (count > 2500) sw = null;
                }

                await PictureLifecycleTracker.ExportPictureLifecycleTrackerSnapshots(Path.Combine(renderCheckpointPath, $"PictureLifeCycle-{SessionId}.csv"));
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

        await Task.Run(() => composer.Compose((int)_project.TargetFrameRate, 96000, 2, SettingsManager.GetSettingAs<int>("Render_AudioComposeBufferSize", 40960, 40960), _cts.Token));

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
                    return;
                }
            });
#endif

            if (action == PostRenderAction.None)
            {
                if (await DisplayAlertAsync(Localized._Info, Localized.RenderPage_Done, Localized.RenderPage_BackToHome, Localized._OK)) await Navigation.PopToRootAsync();
                return;
            }


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
            if (_activeRenderRpcJobId is Guid rpcJobId)
            {
                try { await RenderRpcBootstrap.Client.CancelJobAsync(rpcJobId); } catch { }
            }
            builder?.Interrupt();
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
        await DisplayPromptAsync(Localized._Info, "Copy the args below:", Localized._OK, null, initialValue: args);
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
            "10bit" => ("AV_PIX_FMT_YUV420P10LE", "libx265", ".mp4"),
            "12bit" => ("AV_PIX_FMT_YUV420P10LE", "libx265", ".mp4"),
            _ => ("AV_PIX_FMT_GBRP16LE", "ffv1", ".mkv")
        };
    }



    private async void ExportAudioOnly_Clicked(object sender, EventArgs e)
    {
        if (BindingContext is not RenderPageViewModel vm) return;
#if WINDOWS
        var resultPath = await FileSystemService.PickASavePath($"{_project.ProjectName}_{DateTime.Now:yyyyMMdd_HHmmss}.wav", MauiProgram.DataPath);
        if (string.IsNullOrWhiteSpace(resultPath)) return;
#else
        string resultPath = Path.Combine(MauiProgram.DataPath, "RenderCache", $"{_project.ProjectName}_{DateTime.Now:yyyyMMdd_HHmmss}.wav");
#endif
        try
        {
            await ComposeAudio(vm, resultPath);
        }
        catch (Exception ex)
        {
            Log(ex, "compose media", this);
            if (Debugger.IsAttached && await DisplayAlertAsync(Localized._Info, "Throw?", Localized._OK, Localized._Cancel)) throw;
            await Dispatcher.DispatchAsync(async () =>
            {
                await DisplayAlertAsync(Localized._Error, Localized.RenderPage_Fail(ex), Localized._OK);
            });
            return;
        }
#if ANDROID
        var ext = ".wav";
        var path = await MediaStoreSaver.SaveMediaFileAsync(resultPath, $"{_project.ProjectName}_{DateTime.Now:yyyyMMdd_HHmmss}{ext}", ext switch { ".mp4" => "video/mp4", ".mov" => "video/quicktime", ".mkv" => "video/x-matroska", _ => "video/mp4" }, subFolder: Localized.AppBrand, mediaType: MediaStoreSaver.MediaType.Video);
        if (!string.IsNullOrWhiteSpace(path) && !SettingsManager.IsBoolSettingTrue("DeveloperMode"))
        {
            try
            {
                File.Delete(resultPath);
            }
            catch { }
        }
#endif
    }

    private async void ExportVideoOnly_Clicked(object sender, EventArgs e)
    {
        if (BindingContext is not RenderPageViewModel vm) return;
        var ext = vm.Encoding switch
        {
            "libx264" => ".mp4",
            "h264" => ".mp4",
            "libx265" => ".mov",
            "h265" => ".mov",
            "h265/hevc" => ".mov",
            "hevc" => ".mov",
            "av1" => ".mkv",
            "ffv1" => ".mkv",
            _ => ".mkv"
        };
#if WINDOWS
        var resultPath = await FileSystemService.PickASavePath($"{_project.ProjectName}_{DateTime.Now:yyyyMMdd_HHmmss}{ext}", MauiProgram.DataPath);
        if (string.IsNullOrWhiteSpace(resultPath)) return;
#else
        string resultPath = Path.Combine(MauiProgram.DataPath, "RenderCache", $"{_project.ProjectName}_{DateTime.Now:yyyyMMdd_HHmmss}{ext}");
#endif
        try
        {
            await PrepareUIForRender();
            await DoCompute(vm, resultPath);
        }
        catch (Exception ex)
        {
            Log(ex, "compose media", this);
            if (Debugger.IsAttached && await DisplayAlertAsync(Localized._Info, "Throw?", Localized._OK, Localized._Cancel)) throw;
            await Dispatcher.DispatchAsync(async () =>
            {
                await DisplayAlertAsync(Localized._Error, Localized.RenderPage_Fail(ex), Localized._OK);
            });
            return;
        }
        finally
        {
            await CleanupUIForRenderDone();
        }
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
#endif

    }

    private async void Export16bitRawVideo_Clicked(object sender, EventArgs e)
    {
        if (BindingContext is not RenderPageViewModel vm) return;
#if WINDOWS
        var resultPath = await FileSystemService.PickASavePath($"{_project.ProjectName}_{DateTime.Now:yyyyMMdd_HHmmss}.mkv", MauiProgram.DataPath);
        if (string.IsNullOrWhiteSpace(resultPath)) return;
#else
        string resultPath = Path.Combine(MauiProgram.DataPath, "RenderCache", $"{_project.ProjectName}_{DateTime.Now:yyyyMMdd_HHmmss}.mkv");
#endif
        try
        {
            await PrepareUIForRender();
            await DoCompute(new RenderPageViewModel { Encoding = "ffv1", BitDepth = "16bit", Width = vm.Width, Height = vm.Height, Framerate = vm.Framerate }, resultPath);
        }
        catch (Exception ex)
        {
            Log(ex, "compose media", this);
            if (Debugger.IsAttached && await DisplayAlertAsync(Localized._Info, "Throw?", Localized._OK, Localized._Cancel)) throw;
            await Dispatcher.DispatchAsync(async () =>
            {
                await DisplayAlertAsync(Localized._Error, Localized.RenderPage_Fail(ex), Localized._OK);
            });
            return;
        }
        finally
        {
            await CleanupUIForRenderDone();
        }
#if ANDROID
        var ext = ".mkv";
        var path = await MediaStoreSaver.SaveMediaFileAsync(resultPath, $"{_project.ProjectName}_{DateTime.Now:yyyyMMdd_HHmmss}{ext}", ext switch { ".mp4" => "video/mp4", ".mov" => "video/quicktime", ".mkv" => "video/x-matroska", _ => "video/mp4" }, subFolder: Localized.AppBrand, mediaType: MediaStoreSaver.MediaType.Video);
        if (!string.IsNullOrWhiteSpace(path) && !SettingsManager.IsBoolSettingTrue("DeveloperMode"))
        {
            try
            {
                File.Delete(resultPath);
            }
            catch { }
        }
#endif
    }

    private async Task PrepareUIForRender()
    {
        running = true;
        Shell.SetNavBarIsVisible(this, false);
        RenderOptionPanel.IsVisible = false;
        PreviewLayout.IsVisible = true;
        ProgressBox.IsVisible = true;
        UpdateRenderLayoutForLogPanel();
        CancelRender.IsEnabled = true;
        MoreOptions.IsEnabled = false;
        ExportAudioOnly.IsEnabled = false;
        ExportVideoOnly.IsEnabled = false;
        Export16bitRawVideo.IsEnabled = false;
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
    }

    private async Task CleanupUIForRenderDone()
    {
        _logUpdateTimer?.Stop();
        _screenSaverTimer?.Stop();
        ScreenSaverOverlay.IsVisible = false;
        StopMovingHint();
        StopScreenSaverTimer();
        await FlushLogQueue();
        MyLoggerExtensions.OnLog -= _WriteToLogBox;

        running = false;
        UpdateRenderLayoutForLogPanel();
        CancelRender.IsEnabled = false;
        UpdateLogRefreshState();

        StopScreenSaverTimer();
        Shell.SetNavBarIsVisible(this, true);
        DeviceDisplay.Current.KeepScreenOn = false;
        await PerformPostRenderAction();
    }

    private string BuildStandaloneRenderArgs(int width, int height, int fps, string pixelFormat, string encoder, string outputPath)
    {
        var args = new List<string>
        {
            $"-project={_workingPath}",
            $"-output={outputPath}",
            $"-output_options={width},{height},{fps},{pixelFormat},{encoder}",
            $"-assetDbFile={Path.Combine(MauiProgram.DataPath, "My Assets", ".database", "database.json")}",
            $"-FFmpegLibraryPath={FFmpeg.AutoGen.ffmpeg.RootPath}"
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
        args.Add($"-multiAccelerator={AcceleratorsManager.IsMultiAccelEnabled}");

        if (AcceleratorsManager.IsMultiAccelEnabled && AcceleratorsManager.AcceleratorsForRendering.Length > 0)
        {
            args.Add($"-acceleratorDeviceNames={string.Join(",", AcceleratorsManager.AcceleratorsForRendering.Select(a => a.Name))}");
        }
        else if (AcceleratorsManager.DefaultAccelerator is not null)
        {
            args.Add($"-acceleratorDeviceName={AcceleratorsManager.DefaultAccelerator.Name}");
        }

#endif

        return "render  " + string.Join(" ", args.Select(s => $"\"{s}\""));
    }



}


public class RenderPageViewModel : INotifyPropertyChanged
{
    bool HDREnabled = false;

    public RenderPageViewModel()
    {

    }

    public RenderPageViewModel(bool hdrEnabled)
    {
        HDREnabled = hdrEnabled;
    }

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

    public string[] ExportOptions_Encoding
    {
        get
        {
            if (HDREnabled)
            {
                return
                [
                    "h265", // Apple playback compatibility: force HEVC for HDR exports
                    Localized.RenderPage_CustomOption
                ];
            }
            else
            {
                return
                [
                    "av1", "h264", "h265", // because of license, provided FFmpeg doesn't have libx264/libx265
                    Localized.RenderPage_CustomOption
                ];
            }
        }
    }

    public string[] ExportOptions_BitDepth
    {
        get
        {
            if (HDREnabled)
            {
                return ["10bit"];
            }
            else
            {
                return ["8bit", "10bit", "12bit"];
            }
        }

    }

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
                        OnPropertyChanged(nameof(Width));   // ����
                        OnPropertyChanged(nameof(Height));  // ����
                    }
                }
            }
        }
    }

    string _framerate = "60";
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

    string _encoding = "av1";
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
