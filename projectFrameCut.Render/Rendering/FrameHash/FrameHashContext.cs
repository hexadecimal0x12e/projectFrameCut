using projectFrameCut.Drawing.Base;
using projectFrameCut.Render.Effect;
using projectFrameCut.Drawing.Vector;
using projectFrameCut.Render.RenderAPIBase.EffectAndMixture;

namespace projectFrameCut.Render.Rendering;

/// <summary>Owns dependency snapshots for one frame operation, including its parallel clip jobs.</summary>
public sealed class FrameHashContext : IDisposable
{
    private static readonly AsyncLocal<FrameHashContext?> current = new();
    private readonly object gate = new();
    private readonly HwAccelPictureScope gpuScope;
    private readonly Dictionary<object, (object? Value, Action Release)> values = new();
    private readonly Dictionary<object, int> evaluating = new();
    private readonly Dictionary<int, int> waiting = new();
    private bool disposed;
    public static FrameHashContext? Current => current.Value;
    public FrameHashOptions Options { get; }
    public CancellationToken Token { get; }
    internal RenderFrameDiagnostics? Diagnostics { get; set; }
    private readonly FrameHashCache? cache;

    public FrameHashContext(FrameHashOptions options, CancellationToken token = default, FrameHashCache? cache = null)
        : this(options, token, cache, null) { }

    public FrameHashContext(FrameHashOptions options, CancellationToken token, FrameHashCache? cache, HwAccelPictureSession? gpuSession)
    {
        Options = options;
        Token = token;
        this.cache = cache;
        gpuScope = new(gpuSession);
    }

    public IDisposable Enter()
    {
        var previous = current.Value;
        current.Value = this;
        return new Scope(previous, gpuScope.Enter());
    }

    private sealed class Scope(FrameHashContext? previous, IDisposable gpuEntry) : IDisposable
    {
        public void Dispose() { gpuEntry.Dispose(); current.Value = previous; }
    }

    public T Evaluate<T>(object key, Func<T> compute, Func<T, T> copy, Action<T> release)
    {
        int thread = Environment.CurrentManagedThreadId;
        lock (gate)
        {
            while (true)
            {
                ObjectDisposedException.ThrowIf(disposed, this);
                Token.ThrowIfCancellationRequested();
                if (values.TryGetValue(key, out var saved)) return copy((T)saved.Value!);
                if (!evaluating.TryGetValue(key, out int owner)) break;
                int dependency = owner;
                while (true)
                {
                    if (dependency == thread) throw new InvalidOperationException($"Frame dependency cycle at {key}.");
                    if (!waiting.TryGetValue(dependency, out int next)) break;
                    dependency = next;
                }
                waiting[thread] = owner;
                try { Monitor.Wait(gate, 50); }
                finally { waiting.Remove(thread); }
            }
            evaluating.Add(key, thread);
        }
        try
        {
            var value = compute();
            T snapshot;
            try { Token.ThrowIfCancellationRequested(); snapshot = copy(value); }
            catch { release(value); throw; }
            try
            {
                lock (gate) values.Add(key, (snapshot, () => release(snapshot)));
            }
            catch
            {
                release(snapshot);
                release(value);
                throw;
            }
            return value;
        }
        finally
        {
            lock (gate)
            {
                evaluating.Remove(key);
                Monitor.PulseAll(gate);
            }
        }
    }

    public IPicture? ReadPicture(object key, Func<IPicture?> compute) => Evaluate((key, PreviewSourceFrameContext.ReadingRaw), compute,
        p => p is null ? null : DynamicEffectGraph.CopyPicture(p), p => p?.Dispose());

    internal static T Compute<T>(IEffect effect, RenderAPIBase.ClipAndTrack.IClip clip, uint frame,
        object? input, int width, int height, int relativeWidth, int relativeHeight, Func<T> compute, float? progress = null)
    {
        if (Current is not { } hashes) return compute();
        var context = new EffectExecutionContext
        {
            Clip = clip, FrameIndex = frame, Input = input, TargetWidth = width, TargetHeight = height,
            RelativeWidth = relativeWidth, RelativeHeight = relativeHeight,
            Progress = progress ?? Convert.ToSingle(ValueProviderFrameContext.Get(ValueProviderFrameContext.BuiltInProgressProviderId) ?? 0f),
            ClipProgress = Convert.ToSingle(ValueProviderFrameContext.Get(ValueProviderFrameContext.BuiltInProgressProviderId) ?? 0f),
            Parameters = effect.Parameters.Where(p => !FrameHashHelper.IsRuntimeValue(p.Value)).ToDictionary(p => p.Key, p => (object?)p.Value),
            CancellationToken = hashes.Token,
        };
        return (T)hashes.ComputeEffect(effect, context, () => compute(),
            !effect.Parameters.Values.Any(FrameHashHelper.IsRuntimeValue),
            clip.EffectProvidersInstances?.FirstOrDefault(p => p.Id.ToString() == effect.BindedEffectProvidingSystemID))!;
    }

    internal object? ComputeEffect(IEffect effect, EffectExecutionContext context, Func<object?> compute,
        bool deterministic = true, IEffectProvider? provider = null)
    {
        if (effect is IHwAccelPictureEffect || context.Input is IHwAccelPicture
            || context.Parameters.Values.Any(p => p is IHwAccelPicture)) return compute();
        deterministic &= provider?.IsDeterministic ?? true;
        if (Options.Mode == Contracts.FrameHashMode.Static)
        {
            if (deterministic && effect.IsDeterministic) return compute();
            return Evaluate((effect, context.Input, context.Clip?.Id, context.FrameIndex, context.Progress, context.TargetWidth,
                context.TargetHeight, context.RelativeWidth, context.RelativeHeight, ClipValueFrameContext.Current?.IsTransformInput),
                compute, CopyValue, ReleaseValue);
        }
        string key;
        try
        {
            key = FrameHashHelper.HashValue(new
            {
                Stage = "node", Options, Effect = FrameHashHelper.DescribeEffect(effect, true), context.Input,
                Provider = provider is null ? null : FrameHashHelper.DescribeProvider(provider),
                context.Parameters, context.FrameIndex, context.Progress, context.ClipProgress,
                context.TargetWidth, context.TargetHeight, context.RelativeWidth, context.RelativeHeight,
                Clip = context.Clip is null ? null : new
                {
                    context.Clip.Id, context.Clip.TypeName, context.Clip.StartFrame, context.Clip.Duration,
                    context.Clip.RelativeStartFrame, context.Clip.FrameTime, context.Clip.TargetX, context.Clip.TargetY,
                    context.Clip.TargetWidth, context.Clip.TargetHeight, context.Clip.Rotation, context.Clip.ExtraData,
                },
            });
        }
        catch (Exception ex) when (ex is not OperationCanceledException) { return compute(); }
        object? Compute()
        {
            bool reusable = Options.Mode == Contracts.FrameHashMode.Dynamic && deterministic && effect.IsDeterministic;
            if (reusable && cache?.TryRead(key, out var saved) == true) return saved;
            var result = compute();
            if (Token.IsCancellationRequested)
            {
                if (!ReferenceEquals(result, context.Input)) ReleaseValue(result);
                Token.ThrowIfCancellationRequested();
            }
            if (reusable) cache?.Write(key, result);
            return result;
        }
        return deterministic && effect.IsDeterministic ? Compute() : Evaluate((effect, key), Compute, CopyValue, ReleaseValue);
    }

    internal object? ReadEndpoint(Guid clip, Guid endpoint)
    {
        lock (gate) return values.GetValueOrDefault(("endpoint", clip, endpoint)).Value;
    }

    internal void PublishEndpoint(Guid clip, Guid endpoint, object value)
    {
        lock (gate)
        {
            var key = ("endpoint", clip, endpoint);
            if (values.ContainsKey(key)) return;
            var snapshot = CopyValue(value);
            values[key] = (snapshot, () => ReleaseValue(snapshot));
        }
    }

    internal static object? CopyValue(object? value) => value switch
    {
        IPicture p => DynamicEffectGraph.CopyPicture(p),
        VectorPicture v => VectorPictureEffectProcessing.Copy(v),
        projectFrameCut.Shared.IAudioSamples<float> audio => EffectFieldTypes.CopyAudio(audio),
        Array a => a.Clone(),
        IReadOnlyDictionary<string, object?> outputs => outputs.ToDictionary(p => p.Key, p => CopyValue(p.Value)),
        _ => value,
    };

    internal static void ReleaseValue(object? value)
    {
        if (value is IPicture picture) picture.Dispose();
        else if (value is IReadOnlyDictionary<string, object?> outputs)
            foreach (var item in outputs.Values.Distinct(ReferenceEqualityComparer.Instance)) ReleaseValue(item);
    }

    public void Dispose()
    {
        lock (gate)
        {
            if (disposed) return;
            if (evaluating.ContainsValue(Environment.CurrentManagedThreadId))
                throw new InvalidOperationException("Cannot dispose a frame context while evaluating one of its dependencies.");
            disposed = true;
            Monitor.PulseAll(gate);
            while (evaluating.Count > 0) Monitor.Wait(gate);
            foreach (var entry in values.Values)
            {
                try { entry.Release(); }
                catch (Exception ex) { Logger.Log(ex, "Release frame dependency", nameof(FrameHashContext)); }
            }
            values.Clear();
            gpuScope.Dispose();
        }
    }
}
