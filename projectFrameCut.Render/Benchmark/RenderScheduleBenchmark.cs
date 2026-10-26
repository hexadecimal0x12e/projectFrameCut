using projectFrameCut.Render.Contracts;
using projectFrameCut.Render.EncodeAndDecode;
using projectFrameCut.Render.Effect;
using projectFrameCut.Render.RenderAPIBase.ClipAndTrack;
using projectFrameCut.Render.Rendering;
using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;

namespace projectFrameCut.Render.Benchmark;

public sealed record RenderScheduleOptions
{
    public required string OutputDirectory { get; init; }
    public string? Encoder { get; init; }
    public string PixelFormat { get; init; } = "AV_PIX_FMT_YUV420P";
    public string FFmpegVersion { get; init; } = "";
    public string[] Accelerators { get; init; } = [];
    public int Seed { get; init; } = 42;
    public int CandidateCount { get; init; } = 120;
}

public sealed record RenderScheduleConfiguration(
    int MaxThreads, bool AllowWorkerOversubscription, int RenderAheadFrames,
    bool EnableThreadAffinity, bool EnableMemoryBudget, long MaxBufferedBytes,
    bool EnableDeferredComposition)
{
    public string ToArguments() => FormattableString.Invariant(
        $"-maxParallelThreads={MaxThreads} -allowWorkerOversubscription={AllowWorkerOversubscription} -renderAheadFrames={RenderAheadFrames} -enableThreadAffinity={EnableThreadAffinity} -enableMemoryBudget={EnableMemoryBudget} -maxBufferedBytes={MaxBufferedBytes} -deferredComposition={EnableDeferredComposition}");
}

public sealed record RenderScheduleRun(
    int Index, string Stage, RenderScheduleConfiguration Configuration, string Encoder,
    int Frames, double Seconds, long PeakWorkingSetBytes, string? Error)
{
    public double Fps => Error is null && Seconds > 0 ? Frames / Seconds : 0;
}

public sealed record RenderScheduleRanking(RenderScheduleConfiguration Configuration, int Runs, double Fps);

public sealed class RenderScheduleReport
{
    public required RenderScheduleOptions Options { get; init; }
    public DateTime StartedUtc { get; init; } = DateTime.UtcNow;
    public string OS { get; init; } = RuntimeInformation.OSDescription;
    public string Architecture { get; init; } = RuntimeInformation.ProcessArchitecture.ToString();
    public string Runtime { get; init; } = RuntimeInformation.FrameworkDescription;
    public string RenderAssembly { get; init; } = typeof(Renderer).Assembly.FullName ?? "";
    public Guid RenderModule { get; init; } = typeof(Renderer).Module.ModuleVersionId;
    public int Processors { get; init; } = Environment.ProcessorCount;
    public long AvailableMemoryBytes { get; init; } = GC.GetGCMemoryInfo().TotalAvailableMemoryBytes;
    public string EffectImplementation { get; init; } = EffectHelper.ForcePreferToType?.ToString() ?? "auto";
    public string PictureResizer { get; init; } = Drawing.Processing.Resizing.PictureResizer.Default.GetType().Name;
    public int Width => 1920;
    public int Height => 1080;
    public int FrameRate => 30;
    public uint Duration => 300;
    public long BitRate => 8_000_000;
    public string[] Accelerators => Options.Accelerators;
    public string Encoder { get; set; } = "";
    public string Status { get; set; } = "running";
    public string? Error { get; set; }
    public string? RecommendedArguments { get; set; }
    public double? ImprovementPercent { get; set; }
    public RenderScheduleRanking? BestConfirmed { get; set; }
    public bool DeferredCompositionSkipped { get; set; }
    public List<RenderScheduleRun> Runs { get; } = [];
    public RenderScheduleRanking[] Ranking => Runs.Where(r => r.Stage != "warmup")
        .GroupBy(r => r.Configuration)
        .Where(g => g.Count() >= 2 && g.All(r => r.Error is null))
        .Select(g => new RenderScheduleRanking(g.Key, g.Count(), g.Sum(r => (long)r.Frames) / g.Sum(r => r.Seconds)))
        .OrderByDescending(r => r.Fps).ToArray();
}

public static class RenderScheduleBenchmark
{
    private static readonly JsonSerializerOptions jsonOptions = new() { WriteIndented = true };

    public static async Task<RenderScheduleReport> RunAsync(RenderScheduleOptions options, CancellationToken token,
        Func<Renderer, IDisposable?>? createProgress = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(options.OutputDirectory);
        if (options.CandidateCount < 1) throw new ArgumentOutOfRangeException(nameof(options.CandidateCount));
        string root = Path.GetFullPath(options.OutputDirectory);
        Directory.CreateDirectory(root);
        string job = Path.Combine(root, $"schedule-{DateTime.UtcNow:yyyyMMdd-HHmmss}-{Guid.NewGuid():N}");
        Directory.CreateDirectory(job);
        var report = new RenderScheduleReport { Options = options };
        var baseline = Normalize(new(Environment.ProcessorCount, true, Math.Clamp(Environment.ProcessorCount, 2, 8),
            true, false, RenderMemoryBudget.DefaultBytes, false));
        var random = new Random(options.Seed);
        var candidates = CreateCandidates(baseline).ToList();
        random.Shuffle(CollectionsMarshal.AsSpan(candidates));
        candidates.Remove(baseline);
        candidates.Insert(0, baseline);
        var visited = new HashSet<RenderScheduleConfiguration>();
        int slowerDeferred = 0;

        IEnumerable<RenderScheduleRanking> EligibleRanking() => report.Ranking
            .Where(r => !report.DeferredCompositionSkipped || !r.Configuration.EnableDeferredComposition);

        async Task Measure(RenderScheduleConfiguration c, string stage)
        {
            token.ThrowIfCancellationRequested();
            Log($"[Schedule] Starting #{report.Runs.Count + 1} ({stage}).");
            var result = await RunOnce(c, report, job, stage, token, createProgress);
            report.Runs.Add(result);
            Save(report, job);
            Log($"[Schedule] #{result.Index} {stage}: {result.Fps:F2} FPS, {result.Seconds:F2}s, {c.ToArguments()}" +
                (result.Error is null ? "" : $", failed: {result.Error}"));
        }

        async Task MeasureCandidate(RenderScheduleConfiguration c)
        {
            visited.Add(c);
            string stage = c.EnableDeferredComposition ? "search-deferred" : "search-direct";
            await Measure(c, stage);
            await Measure(c, stage);
        }

        async Task CompareDeferred(RenderScheduleConfiguration c)
        {
            if (report.DeferredCompositionSkipped || visited.Count >= options.CandidateCount) return;
            var direct = report.Ranking.FirstOrDefault(r => r.Configuration == c);
            var deferred = c with { EnableDeferredComposition = true };
            if (direct is null || visited.Contains(deferred)) return;
            await MeasureCandidate(deferred);
            var result = report.Ranking.FirstOrDefault(r => r.Configuration == deferred);
            slowerDeferred = result is not null && result.Fps < direct.Fps ? slowerDeferred + 1 : 0;
            if (result is not null)
                Log($"[Schedule] Matched comparison: direct={direct.Fps:F2} FPS, deferred={result.Fps:F2} FPS.");
            if (slowerDeferred >= 3)
            {
                report.DeferredCompositionSkipped = true;
                Log("[Schedule] Three consecutive deferred configurations were slower than their direct counterparts; skipping remaining deferred configurations.");
                Save(report, job);
            }
        }

        try
        {
            Save(report, job);
            Log($"[Schedule] Reports: {job}. Up to {1L + options.CandidateCount * 2L + 15} full renders.");
            await Measure(baseline, "warmup");
            if (report.Runs[^1].Error is not null) throw new InvalidOperationException("Schedule benchmark warmup failed.");
            if (!FFmpegHelper.IsHardwareVideoEncoder(report.Encoder))
                Log($"[Schedule] Using software encoder: {report.Encoder}.", "warn");

            var initial = candidates.Take(Math.Min(40, Math.Max(1, options.CandidateCount / 2))).ToArray();
            foreach (var c in initial) await MeasureCandidate(c);
            foreach (var c in initial) await CompareDeferred(c);

            while (visited.Count < options.CandidateCount)
            {
                var next = EligibleRanking().Take(5)
                    .SelectMany(r => Neighbors(r.Configuration with { EnableDeferredComposition = false }, candidates))
                    .FirstOrDefault(c => !visited.Contains(c));
                next ??= candidates.FirstOrDefault(c => !visited.Contains(c));
                if (next is null) break;
                await MeasureCandidate(next);
                await CompareDeferred(next);
            }

            var finalists = EligibleRanking().Take(5).Select(r => r.Configuration).ToArray();
            for (int repeat = 0; repeat < 3; repeat++)
                foreach (var c in finalists) await Measure(c, "confirm");

            var winner = report.Ranking.FirstOrDefault(r => finalists.Contains(r.Configuration));
            if (winner is null) throw new InvalidOperationException("No configuration passed confirmation.");
            report.BestConfirmed = winner;
            report.RecommendedArguments = $"-output_options={report.Width},{report.Height},{report.FrameRate},{options.PixelFormat},{report.Encoder} -preferHwAccelEncoder=true -bitRate={report.BitRate} " + winner.Configuration.ToArguments();
            var reference = report.Ranking.FirstOrDefault(r => r.Configuration == baseline);
            if (reference is not null) report.ImprovementPercent = (winner.Fps / reference.Fps - 1) * 100;
            report.Status = "completed";
            Log($"[Schedule] Best confirmed: {winner.Fps:F2} FPS. {report.RecommendedArguments}");
            return report;
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        {
            report.Status = "cancelled";
            throw;
        }
        catch (Exception ex)
        {
            report.Status = "failed";
            report.Error = ex.ToString();
            throw;
        }
        finally
        {
            Save(report, job);
            Log($"[Schedule] {report.Status}. Reports: {job}");
        }
    }

    private static async Task<RenderScheduleRun> RunOnce(RenderScheduleConfiguration c, RenderScheduleReport report,
        string job, string stage, CancellationToken token, Func<Renderer, IDisposable?>? createProgress)
    {
        int index = report.Runs.Count + 1;
        string directory = Path.Combine(job, $"run-{index:D4}");
        Directory.CreateDirectory(directory);
        IClip[] clips = [];
        Renderer? renderer = null;
        VideoBuilder? builder = null;
        VideoWriterHWAccel? writer = null;
        string encoder = report.Encoder;
        string? error = null;
        int frames = 0;
        long peak = Environment.WorkingSet;
        var sw = new Stopwatch();
        using var sampler = new Timer(_ =>
        {
            try { InterlockedMax(ref peak, Environment.WorkingSet); }
            catch (InvalidOperationException) { }
        }, null, 0, 100);
        try
        {
            clips = BenchmarkSourceGenerator.GetDraftStructure(report.Duration, 1f / report.FrameRate, report.Width, report.Height);
            foreach (var clip in clips)
            {
                clip.ExtraData ??= new Dictionary<string, object>();
                clip.ReInit(IPicture.PicturePixelMode.BytePicture);
            }
            token.ThrowIfCancellationRequested();
            sw.Start();
            writer = new VideoWriterHWAccel
            {
                OutputPath = Path.Combine(directory, "output.mp4"),
                Width = report.Width,
                Height = report.Height,
                FramePerSecond = report.FrameRate,
                PixelFormat = report.Options.PixelFormat,
                BitRate = report.BitRate,
                CodecName = report.Encoder.Length > 0 ? report.Encoder : report.Options.Encoder ?? "h264",
                UseExactEncoder = report.Encoder.Length > 0 || report.Options.Encoder is not null,
            };
            builder = new VideoBuilder(writer) { Duration = report.Duration };
            encoder = writer.CodecName;
            if (report.Encoder.Length > 0 && encoder != report.Encoder)
                throw new EncoderChangedException($"Encoder changed from {report.Encoder} to {encoder}.");
            if (report.Encoder.Length == 0) report.Encoder = encoder;
            renderer = new Renderer
            {
                Clips = clips,
                builder = builder,
                Duration = report.Duration,
                TargetWidth = report.Width,
                TargetHeight = report.Height,
                ProjectRelativeWidth = report.Width,
                ProjectRelativeHeight = report.Height,
                Use16Bit = false,
                EnableEffectAutoRetry = false,
                MaxThreads = c.MaxThreads,
                AllowWorkerOversubscription = c.AllowWorkerOversubscription,
                RenderAheadFrames = c.RenderAheadFrames,
                EnableThreadAffinity = c.EnableThreadAffinity,
                EnableMemoryBudget = c.EnableMemoryBudget,
                MaxBufferedBytes = c.MaxBufferedBytes,
                EnableDeferredComposition = c.EnableDeferredComposition,
                ResumeInterruptedRender = false,
                ReuseDynamicPreviewCache = false,
                DynamicPreviewCacheProjectRoot = directory,
            };
            renderer.PrepareRender(token);
            using var progress = createProgress?.Invoke(renderer);
            await renderer.GoRender(token);
            frames = builder.WrittenFramesCount;
            if (frames != report.Duration || renderer.CurrentFinished != report.Duration || writer.DurationWritten != report.Duration)
                throw new InvalidOperationException($"Incomplete output: renderer={renderer.CurrentFinished}, builder={frames}, writer={writer.DurationWritten}, expected={report.Duration}.");
            writer.Finish();
            builder.Dispose();
            sw.Stop();
            if (new FileInfo(writer.OutputPath).Length == 0) throw new InvalidOperationException("Empty video output.");
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { throw; }
        catch (Exception ex) when (!IsFatal(ex))
        {
            frames = builder?.WrittenFramesCount ?? 0;
            error = ex.ToString();
            Log(ex, $"Schedule benchmark run {index}");
        }
        finally
        {
            sw.Stop();
            await sampler.DisposeAsync();
            try { if (builder is not null) builder.Dispose(); else writer?.Dispose(); }
            finally
            {
                try { foreach (var clip in clips) clip.Dispose(); }
                finally
                {
                    renderer?.CleanupCreatedDeferredCache();
                    DeleteRunDirectory(job, directory);
                }
            }
        }
        return new(index, stage, c, encoder, frames, sw.Elapsed.TotalSeconds, peak, error);
    }

    private static bool IsFatal(Exception ex) => ex is UnauthorizedAccessException or System.Security.SecurityException
        or EncoderChangedException || ex is AggregateException a && a.InnerExceptions.Any(IsFatal)
        || ex.InnerException is not null && IsFatal(ex.InnerException);

    private sealed class EncoderChangedException(string message) : Exception(message) { }

    private static void InterlockedMax(ref long target, long value)
    {
        long previous;
        do
        {
            previous = Volatile.Read(ref target);
            if (value <= previous) return;
        }
        while (Interlocked.CompareExchange(ref target, value, previous) != previous);
    }

    private static RenderScheduleConfiguration Normalize(RenderScheduleConfiguration c)
    {
        if (c.MaxThreads == 1)
        {
            c = c with { AllowWorkerOversubscription = false };
            if (!c.EnableDeferredComposition) c = c with { RenderAheadFrames = 1, EnableMemoryBudget = false };
        }
        if (!c.EnableMemoryBudget && !c.EnableDeferredComposition) c = c with { MaxBufferedBytes = RenderMemoryBudget.DefaultBytes };
        return c;
    }

    private static IEnumerable<RenderScheduleConfiguration> CreateCandidates(RenderScheduleConfiguration baseline)
    {
        var threads = new SortedSet<int> { 1, Environment.ProcessorCount };
        for (int n = 2; n <= Environment.ProcessorCount * 2L; n *= 2) threads.Add(n);
        long cap = Math.Max(1, GC.GetGCMemoryInfo().TotalAvailableMemoryBytes / 3);
        var budgets = new[] { 128L, 256, 512, 1024 }.Select(m => Math.Min(m * 1024 * 1024, cap)).Distinct().ToArray();
        var seen = new HashSet<RenderScheduleConfiguration> { baseline };
        yield return baseline;
        foreach (int t in threads)
            foreach (bool soft in new[] { true, false })
                foreach (int ahead in new[] { 1, 2, 4, 8, 16 })
                    foreach (bool affinity in new[] { true, false })
                        foreach (bool limited in new[] { false, true })
                            foreach (long budget in budgets)
                            {
                                var c = Normalize(new(t, soft, ahead, affinity, limited, budget, false));
                                if (seen.Add(c)) yield return c;
                            }
    }

    private static IEnumerable<RenderScheduleConfiguration> Neighbors(RenderScheduleConfiguration c,
        IEnumerable<RenderScheduleConfiguration> candidates) => candidates.Where(n =>
            (n.MaxThreads != c.MaxThreads ? 1 : 0) +
            (n.AllowWorkerOversubscription != c.AllowWorkerOversubscription ? 1 : 0) +
            (n.RenderAheadFrames != c.RenderAheadFrames ? 1 : 0) +
            (n.EnableThreadAffinity != c.EnableThreadAffinity ? 1 : 0) +
            (n.EnableMemoryBudget != c.EnableMemoryBudget || n.MaxBufferedBytes != c.MaxBufferedBytes ? 1 : 0) == 1);

    private static void Save(RenderScheduleReport report, string directory)
    {
        File.WriteAllText(Path.Combine(directory, "report.json.tmp"), JsonSerializer.Serialize(report, jsonOptions));
        File.Move(Path.Combine(directory, "report.json.tmp"), Path.Combine(directory, "report.json"), true);
        using var csv = new StreamWriter(Path.Combine(directory, "runs.csv"), false, new UTF8Encoding(true));
        csv.WriteLine("Index,Stage,Encoder,MaxThreads,AllowWorkerOversubscription,RenderAheadFrames,EnableThreadAffinity,EnableMemoryBudget,MaxBufferedBytes,EnableDeferredComposition,Frames,Seconds,Fps,PeakWorkingSetBytes,Error");
        foreach (var r in report.Runs)
        {
            var c = r.Configuration;
            csv.WriteLine(string.Join(",", new object?[] { r.Index, r.Stage, Escape(r.Encoder), c.MaxThreads,
                c.AllowWorkerOversubscription, c.RenderAheadFrames, c.EnableThreadAffinity, c.EnableMemoryBudget,
                c.MaxBufferedBytes, c.EnableDeferredComposition, r.Frames,
                r.Seconds.ToString("R", CultureInfo.InvariantCulture), r.Fps.ToString("R", CultureInfo.InvariantCulture),
                r.PeakWorkingSetBytes, Escape(r.Error ?? "") }));
        }
    }

    private static string Escape(string value) => "\"" + value.Replace("\"", "\"\"") + "\"";

    private static void DeleteRunDirectory(string job, string directory)
    {
        string root = Path.GetFullPath(job) + Path.DirectorySeparatorChar;
        string path = Path.GetFullPath(directory);
        if (!path.StartsWith(root, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("Run directory is outside the benchmark job.");
        if (Directory.Exists(path)) Directory.Delete(path, true);
    }
}
