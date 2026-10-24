using FFmpeg.AutoGen;
using projectFrameCut.Render.Benchmark;
using projectFrameCut.Render.HwAccelEngine;
using projectFrameCut.Render.Rendering;
using projectFrameCut.Services;
using System.Globalization;

namespace projectFrameCut.Setting.SettingPages;

using static SettingManager.SettingsManager;

public sealed class ScheduleBenchmarkPage : ContentPage
{
    private static readonly SemaphoreSlim benchmarkGate = new(1, 1);
    private readonly Picker mode = new();
    private readonly Button start = new() { Text = Localized.BenchmarkPage_StartBenchmark };
    private readonly Button cancel = new() { Text = Localized.BenchmarkPage_CancelBenchmark, IsEnabled = false };
    private readonly Label status = new();
    private readonly Label result = new();
    private readonly ProgressBar progress = new();
#if ANDROID
    private readonly VerticalStackLayout computeView = new();
#endif
    private CancellationTokenSource? cts;

    public ScheduleBenchmarkPage()
    {
        Title = SettingLocalizedResources.Render_ScheduleBenchmark;
        mode.Items.Add(SettingLocalizedResources.Render_ScheduleBenchmark_Quick);
        mode.Items.Add(SettingLocalizedResources.Render_ScheduleBenchmark_Standard);
        mode.SelectedIndex = 0;
        start.Clicked += Start;
        cancel.Clicked += (_, _) =>
        {
            cts?.Cancel();
            cancel.IsEnabled = false;
        };
        Content = new ScrollView
        {
            Content = new VerticalStackLayout
            {
                Padding = 20,
                Spacing = 12,
                Children =
                {
                    new Label { Text = SettingLocalizedResources.Render_ScheduleBenchmark_Description },
                    mode, start, cancel, status, progress, result
                }
            }
        };
#if ANDROID
        ((VerticalStackLayout)((ScrollView)Content).Content).Children.Add(computeView);
#endif
    }

    private async void Start(object? sender, EventArgs e)
    {
        if (cts is not null) return;
        if (!benchmarkGate.Wait(0))
        {
            await DisplayAlertAsync(Title, SettingLocalizedResources.Render_ScheduleBenchmark_Busy, Localized._OK);
            return;
        }

        using var source = new CancellationTokenSource();
        cts = source;
        start.IsEnabled = mode.IsEnabled = false;
        cancel.IsEnabled = true;
        result.Text = "";
        status.Text = Localized.BenchmarkPage_Status_Preparing;
        progress.Progress = 0;
        int candidateCount = mode.SelectedIndex == 0 ? 12 : 120;
        int maxRuns = 1 + candidateCount * 2 + 15;
        int round = 0;
        Renderer? renderer = null;
        var timer = Dispatcher.CreateTimer();
        timer.Interval = TimeSpan.FromMilliseconds(500);
        timer.Tick += (_, _) =>
        {
            double p = Volatile.Read(ref renderer)?.CurrentFinishedPercentage ?? 0;
            int n = Volatile.Read(ref round);
            status.Text = $"{SettingLocalizedResources.Render_ScheduleBenchmark_Progress}: {n}/{maxRuns} · {p:P0}";
            progress.Progress = Math.Clamp((Math.Max(0, n - 1) + p) / maxRuns, 0, 1);
        };
#if WINDOWS || LINUX
        bool wasRendering = AcceleratorsManager.IsRendering;
        bool wasExternalBackend = AcceleratorsManager.UseExternalBackend;
#elif ANDROID
        var previousComputeViewHandler = Render.HwAccelEngine.Platforms.Android.AndroidExecutionHelper.AddPlatformComputeViewHandler;
#endif
        try
        {
#if WINDOWS || LINUX
            AcceleratorsManager.UseExternalBackend = false;
            AcceleratorsManager.IsRendering = true;
#elif ANDROID
            Render.HwAccelEngine.Platforms.Android.AndroidExecutionHelper.AddPlatformComputeViewHandler = computeView.Children.Add;
            Render.HwAccelEngine.Platforms.Android.AndroidExecutionHelper.Init();
#endif
            timer.Start();
            var report = await Task.Run(() => RenderScheduleBenchmark.RunAsync(new RenderScheduleOptions
            {
                OutputDirectory = Path.Combine(MauiProgram.CachePath, "ScheduleBenchmark"),
                CandidateCount = candidateCount,
                FrameHashMode = RenderHashSettings.Mode,
                FFmpegVersion = ffmpeg.av_version_info(),
#if WINDOWS || LINUX
                Accelerators = AcceleratorsManager.AcceleratorsForRendering.Select(a => a.Name).ToArray(),
#endif
            }, source.Token, r =>
            {
                Interlocked.Exchange(ref renderer, r);
                Interlocked.Increment(ref round);
                return null;
            }));
            source.Token.ThrowIfCancellationRequested();
            var best = report.BestConfirmed ?? throw new InvalidOperationException("No confirmed schedule configuration.");
            Apply(best.Configuration);
            timer.Stop();
            progress.Progress = 1;
            status.Text = SettingLocalizedResources.Render_ScheduleBenchmark_Applied;
            result.Text = $"{best.Fps:F2} FPS\n" +
                $"{SettingLocalizedResources.Render_MaxParallelWorkers}: {best.Configuration.MaxThreads}\n" +
                $"{SettingLocalizedResources.Render_RenderAheadFrames}: {best.Configuration.RenderAheadFrames}\n" +
                $"{SettingLocalizedResources.Render_MaxBufferedMiB}: {best.Configuration.MaxBufferedBytes / (1024 * 1024)}\n" +
                $"{SettingLocalizedResources.Render_OutputCacheBehavior}: " +
                (best.Configuration.EnableDeferredComposition ? SettingLocalizedResources.Render_OutputCacheBehavior_Frame : SettingLocalizedResources.Render_OutputCacheBehavior_Disabled) + "\n\n" +
                $"{SettingLocalizedResources.Render_ScheduleBenchmark_Report}: {report.Options.OutputDirectory}";
            Log($"Schedule benchmark applied: {best.Configuration.ToArguments()}");
        }
        catch (OperationCanceledException) when (source.IsCancellationRequested)
        {
            status.Text = Localized.BenchmarkPage_Status_Cancelled;
        }
        catch (Exception ex)
        {
            Log(ex, "Run schedule benchmark", this);
            status.Text = Localized._Error;
            result.Text = Localized._ExceptionTemplate(ex);
        }
        finally
        {
            timer.Stop();
#if WINDOWS || LINUX
            AcceleratorsManager.IsRendering = wasRendering;
            AcceleratorsManager.UseExternalBackend = wasExternalBackend;
#elif ANDROID
            Render.HwAccelEngine.Platforms.Android.AndroidExecutionHelper.AddPlatformComputeViewHandler = previousComputeViewHandler;
            computeView.Children.Clear();
#endif
            cts = null;
            start.IsEnabled = mode.IsEnabled = true;
            cancel.IsEnabled = false;
            benchmarkGate.Release();
        }
    }

    private static void Apply(RenderScheduleConfiguration c)
    {
        WriteSetting("render_defaultMaxParallelWorkers", c.MaxThreads.ToString(CultureInfo.InvariantCulture));
        WriteSetting("render_allowWorkerOversubscription", c.AllowWorkerOversubscription.ToString());
        WriteSetting("render_renderAheadFrames", c.RenderAheadFrames.ToString(CultureInfo.InvariantCulture));
        WriteSetting("render_enableThreadAffinity", c.EnableThreadAffinity.ToString());
        WriteSetting("render_coreAffinityOverride", "");
        WriteSetting("render_enableMemoryBudget", c.EnableMemoryBudget.ToString());
        WriteSetting("render_maxBufferedMiB", (c.MaxBufferedBytes / (1024 * 1024)).ToString(CultureInfo.InvariantCulture));
        WriteSetting("render_FrameHashMode", c.FrameHashMode.ToString());
        WriteSetting("render_enableChunkRender", false.ToString());
        WriteSetting("render_enableDeferredComposition", c.EnableDeferredComposition.ToString());
    }

    protected override void OnDisappearing()
    {
        cts?.Cancel();
        base.OnDisappearing();
    }
}
