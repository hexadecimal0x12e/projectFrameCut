using System.Collections.Concurrent;
using System.Diagnostics;
using System.Runtime;
using System.Text;
using projectFrameCut.Drawing.Base;
using projectFrameCut.Render.Benchmark;
using projectFrameCut.Render.Compose;
using projectFrameCut.Render.Effect;
using projectFrameCut.Render.HwAccelEngine;
using projectFrameCut.Render.Plugin;
using projectFrameCut.Render.RenderAPIBase.ClipAndTrack;
using projectFrameCut.Render.Rendering;
using projectFrameCut.Shared;
using IPicture = projectFrameCut.Drawing.Base.IPicture;

namespace projectFrameCut;

public partial class BenchmarkPage : ContentPage
{
    // ── 状态字段 ─────────────────────────────────────────────
    private CancellationTokenSource _cts = new();
    private bool _running;
    private IClip[]? _clips;
    private VideoBuilder? _builder;
    private Renderer? _renderer;
    private Stopwatch _benchmarkSw = new();
    private bool _boostMode;

    // ── 日志基础设施（参考 RenderPage 模式） ────────────────
    private readonly StringBuilder _logBuffer = new();
    private readonly ConcurrentQueue<string> _logQueue = new();
    private System.Timers.Timer? _logUpdateTimer;
    private readonly SemaphoreSlim _logSemaphore = new(1, 1);
    private bool _isLogPanelVisible;

    public BenchmarkPage()
    {
        InitializeComponent();
        InitializeLogTimer();
        SetLogPanelVisible(false);
        GCOptionPicker.ItemsSource = new List<string>
        {
             SettingsManager.SettingLocalizedResources.Render_GCOption_LetCLRDoGC,
             SettingsManager.SettingLocalizedResources.Render_GCOption_DoNormalCollection,
#if WINDOWS || LINUX
             SettingsManager.SettingLocalizedResources.Render_GCOption_DoLOHCompression 
#endif
        };
        GCOptionPicker.SelectedIndex = 0;
        OutputModePicker.SelectedIndex = 0;
        BoostModeSwitch.IsToggled = true;
#if ANDROID
        Render.HwAccelEngine.Platforms.Android.ComputerHelper.AddPlatformComputeViewHandler = ComputeView.Children.Add;
        Render.HwAccelEngine.Platforms.Android.ComputerHelper.Init();
#elif iDevices

#elif WINDOWS
        // AcceleratorsManager was initialized during plugin load.
        // Switch to rendering mode so all configured accelerators are used for benchmarking.
        projectFrameCut.Render.HwAccelEngine.AcceleratorsManager.IsRendering = true;
        if (!projectFrameCut.Render.HwAccelEngine.AcceleratorsManager.Accelerators.Any())
            throw new InvalidDataException("No valid ILGPU accelerators found.");
#endif
    }

    // ═══════════════════════════════════════════════════════════
    //  日志
    // ═══════════════════════════════════════════════════════════

    private void InitializeLogTimer()
    {
        _logUpdateTimer = new System.Timers.Timer(800);
        _logUpdateTimer.Elapsed += async (s, e) => await FlushLogQueue();
        _logUpdateTimer.AutoReset = true;
    }

    private async Task FlushLogQueue()
    {
        if (!_isLogPanelVisible || _logQueue.IsEmpty) return;
        await _logSemaphore.WaitAsync();
        try
        {
            var batch = new StringBuilder();
            int count = 0;
            const int maxBatchSize = 50;

            while (count < maxBatchSize && _logQueue.TryDequeue(out var logEntry))
            {
                batch.AppendLine(logEntry);
                count++;
            }

            if (batch.Length > 0)
            {
                _logBuffer.Append(batch.ToString());
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

    private void _WriteToLogBox(string msg, string level)
    {
        _logQueue.Enqueue($"[{level}] {msg}");
    }

    private void Log(string message, string level = "info")
    {
        _WriteToLogBox(message, level);
    }

    private void SetLogPanelVisible(bool visible)
    {
        _isLogPanelVisible = visible;
        LoggingBox.IsVisible = visible;
        LoggingBox.HeightRequest = visible ? 200 : 0;
        ToggleLogButton.Text = visible
            ? Localized.RenderPage_HideLogs
            : Localized.RenderPage_ShowLogs;

        if (visible)
        {
            _ = FlushLogQueue();
        }

        if (_running && visible)
        {
            _logUpdateTimer?.Start();
        }
        else
        {
            _logUpdateTimer?.Stop();
        }
    }

    private void ToggleLogButton_Clicked(object? sender, EventArgs e)
    {
        SetLogPanelVisible(!_isLogPanelVisible);
    }

    protected override bool OnBackButtonPressed()
    {
        if (_running) return true;
        return base.OnBackButtonPressed();
    }

    // ═══════════════════════════════════════════════════════════
    //  UI 状态管理
    // ═══════════════════════════════════════════════════════════

    private async Task PrepareUIForBenchmark()
    {
        _running = true;
        StartButton.IsEnabled = false;
        CancelButton.IsEnabled = true;
        ResultsPanel.IsVisible = false;
        ResultsLabel.Text = string.Empty;
        BenchmarkProgress.Progress = 0;
        StatusLabel.Text = Localized.BenchmarkPage_Status_Preparing;

        _logBuffer.Clear();
        _logQueue.Clear();
        LoggingBox.Text = string.Empty;
        MyLoggerExtensions.OnLog += _WriteToLogBox;
        _logUpdateTimer?.Start();
    }

    private async Task CleanupUIForBenchmarkDone(bool cancelled)
    {
        _logUpdateTimer?.Stop();
        MyLoggerExtensions.OnLog -= _WriteToLogBox;
        await FlushLogQueue();

        _running = false;
        StartButton.IsEnabled = true;
        CancelButton.IsEnabled = false;
        StatusLabel.Text = cancelled
            ? Localized.BenchmarkPage_Status_Cancelled
            : Localized.BenchmarkPage_Status_Complete;

#if WINDOWS || LINUX
        AcceleratorsManager.IsRendering = false;
#endif
    }

    // ═══════════════════════════════════════════════════════════
    //  事件处理
    // ═══════════════════════════════════════════════════════════

    private async void StartButton_Clicked(object sender, EventArgs e)
    {
        if (_running) return;

        _cts = new CancellationTokenSource();
        var ct = _cts.Token;

        try
        {
            await PrepareUIForBenchmark();
            await RunBenchmark(ct);
        }
        catch (TaskCanceledException)
        {
            Log("Benchmark was cancelled.", "warn");
        }
        catch (Exception ex)
        {
            Log($"Benchmark failed: {ex.Message}", "error");
            Log(ex.ToString(), "debug");
            await Dispatcher.DispatchAsync(async () =>
            {
                await DisplayAlertAsync(Localized._Error, $"Benchmark failed:\n{ex.Message}", Localized._OK);
            });
        }
        finally
        {
            await CleanupUIForBenchmarkDone(ct.IsCancellationRequested);

            // 清理资源
            if (_clips is not null)
            {
                foreach (var clip in _clips) clip?.Dispose();
                _clips = null;
            }
            _builder = null;
            _renderer = null;
        }
    }

    private async void CancelButton_Clicked(object sender, EventArgs e)
    {
        if (!_running) return;
        var sure = await DisplayAlertAsync(Localized._Warn, Localized.BenchmarkPage_CancelBenchmark_Warn, Localized._OK, Localized._Cancel);
        if (sure)
        {
            _builder?.Interrupt();
            _cts.Cancel();
            Log("Benchmark cancelled by user.", "warn");
        }
    }

    // ═══════════════════════════════════════════════════════════
    //  核心 Benchmark 逻辑（对应 GoBench）
    // ═══════════════════════════════════════════════════════════

    private async Task RunBenchmark(CancellationToken ct)
    {
        // ── 常量 ────────────────────────────────────────────
        const int width = 1920;
        const int height = 1080;
        const int fps = 60;

        // ── 生成测试结构 ────────────────────────────────────
        _clips = BenchmarkSourceGenerator.GetDraftStructure();
        if (_clips is null || _clips.Length == 0)
        {
            Log("ERROR: Benchmark structure is empty.", "error");
            return;
        }
        Log($"Generated {_clips.Length} test clips.");

        uint duration = 0;
        foreach (var clip in _clips)
        {
            duration = Math.Max(clip.StartFrame + clip.Duration, duration);
        }

        Log($"Running benchmark: {duration} frames, {width}x{height}, {fps} fps.");

        // ── 初始化 clips ───────────────────────────────────
        Log("Initializing clips...");
        foreach (var clip in _clips)
        {
            clip.ExtraData ??= new Dictionary<string, object>();
            await Task.Run(() => clip.ReInit(IPicture.PicturePixelMode.BytePicture), ct);
            Log($"Clip {clip.Name}: {clip.ClipType}, StartFrame={clip.StartFrame}, Duration={clip.Duration}");
        }

        // ── 创建 BlackholeVideoWriter ──────────────────────
        bool writeToNull = OutputModePicker.SelectedIndex == 0;
        if (writeToNull)
        {
            _builder = new VideoBuilder(new BlackholeVideoWriter
            {
                Width = width,
                Height = height,
                FramePerSecond = fps,
                PixelFormat = "AV_PIX_FMT_YUV420P",
                OutputPath = "/dev/null"
            })
            {
                Duration = duration,
                LogStat = false,
                BlockWrite = false,
                DisposeFrameAfterEachWrite = true,
            };
            Log("BlackholeVideoWriter enabled (write to null).");
        }
        else
        {
            _builder = null;
            Log("VideoBuilder disabled (no writer).");
        }

        // ── 读取 GCOption ──────────────────────────────────
        var gcOption = GCOptionPicker.SelectedIndex;
        if (gcOption == 2)
        {
            GCSettings.LargeObjectHeapCompactionMode = GCLargeObjectHeapCompactionMode.CompactOnce;
        }
        Log($"GC Option: {gcOption}");

        // ── 读取 boostMode ─────────────────────────────────
        _boostMode = BoostModeSwitch.IsToggled;
        Log($"Boost mode: {_boostMode}");

        // ── 设置加速器 ─────────────────────────────────────
#if WINDOWS || LINUX
        AcceleratorsManager.IsRendering = true;
        if (!AcceleratorsManager.Accelerators.Any())
        {
            Log("ERROR: No valid ILGPU accelerators found.", "error");
            return;
        }
        Log($"Accelerators ready: {AcceleratorsManager.Accelerators.Length} device(s).");
#endif

        // ── Setup renderer 全局配置（与 GoBench 一致） ─────
        ClassicOverlayMixture.EnableApproximatePath = false;
        var maxThreads = Environment.ProcessorCount * 2;

        // ── 创建 Renderer ──────────────────────────────────
        _renderer = new Renderer
        {
            builder = _builder,
            Clips = _clips,
            Duration = duration,
            TargetWidth = width,
            TargetHeight = height,
            ProjectRelativeWidth = width,
            ProjectRelativeHeight = height,
            Use16Bit = false,
            LogProcessStack = true,           // 关键：启用逐步骤耗时记录
            LogRenderState = false,
            LogStaticsData = false,
            GCOption = gcOption,
            EnableGPUBatchProcess = true,
            AllowReorderEffect = true,
            AutoSetupRenderContext = false,
            UseHDR = false,
            StartFrame = 0,
            EnableRenderWatchdogForceStart = false,
            MaxRenderScheduleTimeout = 0,
            RenderSchedulerIdleDelayMs = 0,
            MinSchedulePreparedFrames = 0,
            ThrottleThreshold = (int)(duration * 8),
            // boostMode：全并行，每帧一线程，禁用所有节流
            MaxThreads = _boostMode ? (int)duration : SettingsManager.GetSettingAs("render_defaultMaxParallelWorkers", 8, 8),
            BlockPreparingBeforeRendering = _boostMode,
            DisableAllThrottleOptions = _boostMode,
            OneByOneRender = false,
            RenderByLayers = false,
            PrepareInWorkerThreads = false,
            EnableThreadAffinity = true,
        };

        // ── 进度回调 ──────────────────────────────────────
        _renderer.OnProgressChanged += (progress, eta) =>
        {
            Dispatcher.Dispatch(async () =>
            {
                await BenchmarkProgress.ProgressTo(progress, 250, Easing.Linear);
                string fpsStr = _renderer?.CurrentFps > 0 ? $"{_renderer.CurrentFps:N2}" : "--";
                string etaStr = eta.TotalSeconds >= 5
                    ? (eta.TotalHours >= 1 ? eta.ToString(@"hh\:mm\:ss") : eta.ToString(@"mm\:ss"))
                    : "--";
                StatusLabel.Text = $"{Localized.BenchmarkPage_Status_Running}  " +
                    $"{progress:P1}  ETA: {etaStr}  FPS: {fpsStr}  " +
                    $"Frame: {_renderer?.CurrentFinished ?? 0}/{_renderer?.Duration ?? 0}";
            });
        };

        // ── 执行渲染 ──────────────────────────────────────
        Log("Starting benchmark render...");
        _benchmarkSw.Restart();

        try
        {
            _builder?.Build()?.Start();
            await Task.Run(() => _renderer.PrepareRender(ct), ct);
            await Task.Run(() => _renderer.GoRender(ct), ct);
            _benchmarkSw.Stop();
            Log($"Render done, total elapsed {_benchmarkSw.Elapsed}");
        }
        catch (TaskCanceledException)
        {
            _benchmarkSw.Stop();
            throw;
        }
        catch (Exception ex)
        {
            _benchmarkSw.Stop();
            Log($"Render error: {ex.Message}", "error");
            throw;
        }

        // ── 显示结果 ──────────────────────────────────────
        if (!ct.IsCancellationRequested)
        {
            var resultText = FormatBenchmarkResults(_renderer, _benchmarkSw);
            await Dispatcher.DispatchAsync(() =>
            {
                ResultsLabel.Text = resultText;
                ResultsPanel.IsVisible = true;
            });
            Log("Benchmark results displayed.");
        }
    }

    // ═══════════════════════════════════════════════════════════
    //  统计结果（对应 GoBench lines 1256-1330）
    // ═══════════════════════════════════════════════════════════

    private static string FormatBenchmarkResults(Renderer renderer, Stopwatch sw)
    {
        // ── 收集 ProcessStacks ──────────────────────────────
        var stacksSnapshot = renderer.FrameProcessStacks
            .Select(s => s.Value).ToList();

        // ── 扁平化嵌套步骤（直接复制 GoBench 的递归逻辑） ──
        static IEnumerable<PictureProcessStack> FlattenStacks(IEnumerable<PictureProcessStack>? steps)
        {
            if (steps is null) yield break;
            foreach (var step in steps)
            {
                if (step is null) continue;
                yield return step;
                if (step is OverlayedPictureProcessStack overlay)
                {
                    foreach (var s in FlattenStacks(overlay.TopSteps)) yield return s;
                    foreach (var s in FlattenStacks(overlay.BaseSteps)) yield return s;
                }
            }
        }

        static string GetStepKey(PictureProcessStack step)
        {
            var name = step.OperationDisplayName;
            if (string.IsNullOrWhiteSpace(name)) name = step.Operator?.Name;
            return string.IsNullOrWhiteSpace(name) ? "(unknown)" : name;
        }

        // ── 按操作聚合耗时 ──────────────────────────────────
        var sumTicksByKey = new Dictionary<string, long>(StringComparer.Ordinal);
        var countByKey = new Dictionary<string, int>(StringComparer.Ordinal);
        var orderedKeys = new List<string>();

        foreach (var frameStack in stacksSnapshot)
        {
            foreach (var step in FlattenStacks(frameStack))
            {
                if (step?.Elapsed is not TimeSpan elapsed) continue;
                var key = GetStepKey(step);
                if (!orderedKeys.Contains(key)) orderedKeys.Add(key);
                sumTicksByKey[key] = sumTicksByKey.GetValueOrDefault(key) + elapsed.Ticks;
                countByKey[key] = countByKey.GetValueOrDefault(key) + 1;
            }
        }

        // ── 计算平均值 ──────────────────────────────────────
        var avgTime = renderer.EachElapsed.Count > 0
            ? renderer.EachElapsed.Average(ts => ts.TotalMilliseconds)
            : 0;
        var avgPrepTime = renderer.EachElapsedForPreparing.Count > 0
            ? renderer.EachElapsedForPreparing.Average(ts => ts.TotalMilliseconds)
            : 0;
        var totalTime = sw.Elapsed;
        var renderedFrames = renderer.EachElapsed.Count;

        // ── 格式化输出 ──────────────────────────────────────
        var sb = new StringBuilder();
        sb.AppendLine("========================================");
        sb.AppendLine("  Benchmark Results");
        sb.AppendLine("========================================");
        sb.AppendLine($"  Total frames rendered : {renderedFrames}");
        sb.AppendLine($"  Total time            : {totalTime.TotalSeconds:F2}s");
        sb.AppendLine($"  Overall FPS           : {renderedFrames / Math.Max(totalTime.TotalSeconds, 0.001):F2}");
        sb.AppendLine($"  Avg frame render time : {avgTime:F3}ms ({1000.0 / Math.Max(avgTime + avgPrepTime, 0.001):F1} FPS)");
        sb.AppendLine($"  Avg prepare time      : {avgPrepTime:F3}ms");
        sb.AppendLine($"  Avg total per frame   : {avgTime + avgPrepTime:F3}ms");

        if (orderedKeys.Count > 0)
        {
            sb.AppendLine();
            sb.AppendLine("  Per-step breakdown:");
            for (int i = 0; i < orderedKeys.Count; i++)
            {
                var key = orderedKeys[i];
                var count = countByKey.GetValueOrDefault(key);
                if (count <= 0) continue;
                var avg = TimeSpan.FromTicks(sumTicksByKey.GetValueOrDefault(key) / count);
                sb.AppendLine($"    Step #{i + 1}: {key}");
                sb.AppendLine($"      Avg: {avg.TotalMilliseconds:F3}ms  (n={count})");
            }
        }

        sb.AppendLine("========================================");
        return sb.ToString();
    }
}
