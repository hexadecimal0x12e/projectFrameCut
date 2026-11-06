using System.Collections.Concurrent;
using System.Diagnostics;
using System.Globalization;
using System.Text;
using projectFrameCut.Render.RenderAPIBase.EffectAndMixture;

namespace projectFrameCut.Render.Rendering;

internal sealed class RenderFrameDiagnostics : IDisposable
{
    [ThreadStatic]
    private static RenderFrameDiagnostics? current;
    [ThreadStatic]
    private static Stage? active;
    public static RenderFrameDiagnostics? Current => current;
    private readonly ConcurrentQueue<Stage> stages = new();
    private readonly Stopwatch total = Stopwatch.StartNew();
    private readonly uint frame;
    private readonly Action<RenderFrameDiagnosticSnapshot>? completed;
    private readonly bool logTimings;
    private Stage? workers;
    private int disposed;
    public bool Succeeded { get; set; }

    public RenderFrameDiagnostics(uint frame, Action<RenderFrameDiagnosticSnapshot>? completed = null, bool logTimings = true)
    {
        this.frame = frame;
        this.completed = completed;
        this.logTimings = logTimings;
    }

    public IDisposable Enter(bool worker = false)
    {
        var scope = new Scope(current, active);
        if (!ReferenceEquals(current, this)) active = worker ? workers : null;
        current = this;
        return scope;
    }

    private sealed class Scope(RenderFrameDiagnostics? previous, Stage? parent) : IDisposable
    {
        public void Dispose()
        {
            current = previous;
            active = parent;
        }
    }

    public void BeginWorkers()
    {
        workers = new Stage(this, null, "Prepare clips in parallel (worker sum)", "Prepare clips in parallel", sumChildren: true);
        stages.Enqueue(workers);
    }

    public void CompleteWorkers() => workers?.Dispose();

    public IDisposable Measure(string name, string? category = null)
    {
        var stage = new Stage(this, ReferenceEquals(current, this) ? active : null, name, category ?? name);
        stages.Enqueue(stage);
        active = stage;
        return stage;
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref disposed, 1) != 0) return;
        total.Stop();
        var snapshot = stages.ToArray();
        var children = snapshot.ToLookup(s => s.Parent);
        if (completed is not null)
        {
            var result = new List<RenderStageDiagnosticSnapshot>(snapshot.Length);
            AddStages(null, null);
            completed(new(frame, Succeeded, total.Elapsed, result.AsReadOnly()));

            void AddStages(Stage? parent, string? parentPath)
            {
                int index = 0;
                foreach (var stage in children[parent])
                {
                    var path = index.ToString(CultureInfo.InvariantCulture);
                    if (parentPath is not null) path = parentPath + "/" + path;
                    result.Add(new(path, parentPath, stage.Depth, stage.Name, stage.Category,
                        stage.Elapsed, stage.SelfElapsed, children[stage].Count(), stage.IsWorkerSum));
                    AddStages(stage, path);
                    index++;
                }
            }
        }
        if (!logTimings) return;
        var log = new StringBuilder(FormattableString.Invariant(
            $"[Render diagnostic] Frame {frame}: {(Succeeded ? "completed" : "aborted")}, total={total.Elapsed.TotalMilliseconds:F3} ms"));
        foreach (var stage in children[null]) AppendStage(stage);
        if (snapshot.Any(s => s.Parent is not null))
        {
            log.AppendLine();
            log.Append("  Stage averages (current frame, direct children, inclusive):");
            foreach (var parent in snapshot.Where(s => s.Parent is not null).GroupBy(s => s.Parent!.Category))
            {
                log.AppendLine();
                log.Append(FormattableString.Invariant(
                    $"    {parent.Key}: children={parent.Count()}, child_avg={parent.Average(s => s.Elapsed.TotalMilliseconds):F3} ms"));
                foreach (var group in parent.GroupBy(s => s.Category))
                {
                    int count = group.Count();
                    double elapsed = group.Sum(s => s.Elapsed.TotalMilliseconds);
                    log.AppendLine();
                    log.Append(FormattableString.Invariant(
                        $"      {group.Key}: count={count}, total={elapsed:F3} ms, avg={elapsed / count:F3} ms, self_avg={group.Average(s => s.SelfElapsed.TotalMilliseconds):F3} ms"));
                }
            }
        }
        Log(log.ToString(), "diag");

        void AppendStage(Stage stage)
        {
            var lines = stage.Name.ReplaceLineEndings("\n").Split('\n');
            log.AppendLine();
            log.Append(' ', (stage.Depth + 1) * 2);
            log.Append(FormattableString.Invariant($"{lines[0]}: {stage.Elapsed.TotalMilliseconds:F3} ms"));
            if (stage.ChildTicks > 0)
                log.Append(FormattableString.Invariant($" (self={stage.SelfElapsed.TotalMilliseconds:F3} ms)"));
            foreach (var line in lines.Skip(1))
            {
                log.AppendLine();
                log.Append(' ', (stage.Depth + 2) * 2);
                log.Append(line.TrimStart());
            }
            foreach (var child in children[stage]) AppendStage(child);
        }
    }

    private sealed class Stage(RenderFrameDiagnostics owner, Stage? parent, string name, string category, bool sumChildren = false) : IDisposable
    {
        private readonly long started = Stopwatch.GetTimestamp();
        private bool disposed;
        public string Name { get; } = name;
        public string Category { get; } = category;
        public bool IsWorkerSum { get; } = sumChildren;
        public Stage? Parent { get; } = parent;
        public int Depth { get; } = parent is null ? 0 : parent.Depth + 1;
        public TimeSpan Elapsed { get; private set; }
        public TimeSpan SelfElapsed => TimeSpan.FromTicks(Math.Max(0, Elapsed.Ticks - ChildTicks));
        public long ChildTicks;

        public void Dispose()
        {
            if (disposed) return;
            disposed = true;
            try
            {
                if (!IsWorkerSum) HwAccelPictureScope.Current?.Complete();
            }
            finally
            {
                Elapsed = IsWorkerSum ? TimeSpan.FromTicks(Interlocked.Read(ref ChildTicks)) : Stopwatch.GetElapsedTime(started);
                if (Parent is not null) Interlocked.Add(ref Parent.ChildTicks, Elapsed.Ticks);
                if (ReferenceEquals(current, owner) && ReferenceEquals(active, this)) active = Parent;
            }
        }
    }
}

internal sealed record RenderFrameDiagnosticSnapshot(uint FrameIndex, bool Succeeded, TimeSpan Elapsed,
    IReadOnlyList<RenderStageDiagnosticSnapshot> Stages);

internal sealed record RenderStageDiagnosticSnapshot(string Path, string? ParentPath, int Depth, string Name,
    string Category, TimeSpan Elapsed, TimeSpan SelfElapsed, int ChildCount, bool IsWorkerSum);
