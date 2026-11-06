using projectFrameCut.Render.Plugin;
using projectFrameCut.Render.RenderAPIBase.ClipAndTrack;
using projectFrameCut.Render.RenderAPIBase.EffectAndMixture;
using projectFrameCut.Shared;

namespace projectFrameCut.Render.Effect;

public class CustomSpeedVarianceProvider : EffectProviderBase
{
    public const string ProviderTypeName = "CustomSpeedVariance";
    public const string SourceFrameKey = "SourceFrame";
    public const string FinishedKey = "Finished";
    public const string ScanLimitKey = "ScanLimit";
    public override string TypeName => ProviderTypeName;
    public override string FromPlugin => InternalPluginBase.InternalPluginBaseID;
    public override EffectType TypeOfEffect => EffectType.SpeedVarianceProvider;
    public override EffectTarget Target => EffectTarget.SpeedVariance;
    public override IReadOnlyList<EffectStage> SupportedStages => [EffectStage.Composition];
    public override bool IsDeterministic => true;

    public CustomSpeedVarianceProvider()
    {
        Name = "Custom Speed";
        AnchorsBindingState[SourceFrameKey] = ValueProviderFrameContext.SpeedFrameProviderId;
        AnchorsBindingState[FinishedKey] = ValueProviderFrameContext.SpeedFinishedProviderId;
    }

    protected override IReadOnlyDictionary<string, EffectArgumentFieldDescriptor> DefineInFields() => new Dictionary<string, EffectArgumentFieldDescriptor>();
    protected override IReadOnlyList<EffectArgumentFieldDescriptor> DefineFields() =>
    [
        Field(SourceFrameKey, EffectArgumentFieldType.Numeric, "0"),
        Field(FinishedKey, EffectArgumentFieldType.Boolean, "false"),
        Field(ScanLimitKey, EffectArgumentFieldType.Integer | EffectArgumentFieldType.CannotBeDynamic, "1000000", min: "1", max: "2147483646"),
    ];

    internal static CustomSpeedVarianceEffect BuildGraph(IEffectProvider provider, IReadOnlyDictionary<Guid, IEffectProvider> providers)
    {
        ValidateGraph(provider, providers);
        var graph = new InlineValueGraph(providers, cacheFrameHashes: false);
        graph.AddOwner();
        try
        {
            foreach (var (_, binding) in provider.EnumerateFieldBindings())
                if (EffectProviderOutputExtensions.TryParseOutputSourceId(binding, out var id, out _)) graph.GetEffects(id);
            var effect = CreateEffect(provider, graph);
            effect.ReleaseGraph = () => graph.ReleaseWhenIdle(() =>
            {
                foreach (var owned in graph.Effects.OfType<IDisposable>().Distinct()) owned.Dispose();
            });
            return effect;
        }
        catch
        {
            graph.ReleaseWhenIdle(() =>
            {
                foreach (var effect in graph.Effects.OfType<IDisposable>().Distinct()) effect.Dispose();
            });
            throw;
        }
    }

    internal static CustomSpeedVarianceEffect CreateEffect(IEffectProvider provider, InlineValueGraph graph)
    {
        var state = EffectProviderTypeState.Capture(provider);
        var parameters = state.Parameters.ToDictionary(p => p.Key, p => p.Value!);
        foreach (var (key, binding) in provider.EnumerateFieldBindings())
        {
            if (key is not (SourceFrameKey or FinishedKey) || binding == IEffectProvider.NoConnectionGUID.ToString()) continue;
            // Each scan evaluates the expression with its own speed frame and lengths.
            parameters[key] = (Func<object>)(() => graph.GetValue(binding)!);
        }
        return new CustomSpeedVarianceEffect { TypeName = provider.TypeName, Parameters = parameters, BindedEffectProvidingSystemID = provider.Id.ToString() };
    }

    internal static void ValidateGraph(IEffectProvider terminal, IReadOnlyDictionary<Guid, IEffectProvider> providers)
    {
        var visiting = new HashSet<Guid>();
        var visited = new HashSet<Guid>();
        void Visit(IEffectProvider provider)
        {
            if (visited.Contains(provider.Id)) return;
            if (!visiting.Add(provider.Id)) throw new InvalidOperationException("Custom speed graph contains a cycle.");
            if (provider != terminal && (!provider.IsDeterministic || !provider.Enabled
                || provider.Stage != terminal.Stage || !DynamicEffectBindings.CanInlineValue(provider, providers)
                || provider is IClipValueEndpointProvider))
                throw new InvalidOperationException($"Provider {provider.Id} cannot be evaluated in a custom speed graph.");
            foreach (var (_, binding) in provider.AnchorsBindingState.Where(p => !EffectProviderAnchorExtensions.IsOutputBindingKey(p.Key)))
            {
                if (binding == IEffectProvider.NoConnectionGUID.ToString()) continue;
                if (ValueProviderFrameContext.IsSpeedSource(binding)) continue;
                if (ValueProviderFrameContext.IsBuiltInSource(binding))
                    throw new InvalidOperationException("Custom speed graphs must use speed frame and length sources instead of render context sources.");
                if (!EffectProviderOutputExtensions.TryParseOutputSourceId(binding, out var id, out _)
                    || !providers.TryGetValue(id, out var source))
                    throw new InvalidOperationException($"Missing custom speed source '{binding}'.");
                Visit(source);
            }
            visiting.Remove(provider.Id);
            visited.Add(provider.Id);
        }
        Visit(terminal);
    }
}

public sealed class CustomSpeedVarianceEffect : IDirectSpeedVarianceProvider, IDisposable
{
    public string TypeName { get; init; } = CustomSpeedVarianceProvider.ProviderTypeName;
    public string FromPlugin => InternalPluginBase.InternalPluginBaseID;
    public string Id { get; set; } = Guid.NewGuid().ToString();
    public string Name { get; set; } = "Custom Speed";
    public string? BindedEffectProvidingSystemID { get; set; }
    public bool IsDeterministic => true;
    public Dictionary<string, object> Parameters { get; set; } = new();
    internal Action? ReleaseGraph { get; set; }
    private readonly Lock gate = new();
    private readonly CancellationTokenSource lifetime = new();
    private readonly Dictionary<(uint Length, uint? SourceLength), Task<SpeedVarianceFrameMap>> maps = new();
    private readonly List<Task<SpeedVarianceFrameMap>> workers = new();
    private readonly List<CancellationTokenSource> cancellations = new();
    private readonly Lock scanGate = new();
    private bool disposed;

    public void Initialize() { }
    public IEffect WithParameters(Dictionary<string, object> parameters) => new CustomSpeedVarianceEffect { TypeName = TypeName, Parameters = parameters };
    public uint GetTargetFrame(uint sourceFrame) => throw new NotSupportedException("Custom speed maps timeline frames directly to source frames.");
    public uint GetEffectiveLength(uint length) => GetFrameMap(length, null).Length;
    public uint GetEffectiveLength(uint length, uint? sourceLength) => GetFrameMap(length, sourceLength).Length;

    public SpeedVarianceFrameMap GetFrameMap(uint length, uint? sourceLength)
        => GetFrameMapAsync(length, sourceLength, ValueProviderFrameContext.CancellationToken).GetAwaiter().GetResult();

    public Task<SpeedVarianceFrameMap> GetFrameMapAsync(uint length, uint? sourceLength, CancellationToken token = default)
    {
        Task<SpeedVarianceFrameMap> task;
        lock (gate)
        {
            ObjectDisposedException.ThrowIf(disposed, this);
            if (!maps.TryGetValue((length, sourceLength), out task!) || task.IsCanceled)
            {
                var cancellation = CancellationTokenSource.CreateLinkedTokenSource(lifetime.Token, token);
                cancellations.Add(cancellation);
                task = Task.Run(() => { lock (scanGate) return Scan(length, sourceLength, cancellation.Token); });
                maps[(length, sourceLength)] = task;
                workers.RemoveAll(t => t.IsCompleted);
                workers.Add(task);
                foreach (var key in maps.Where(p => p.Value.IsCompleted && p.Key != (length, sourceLength)).Select(p => p.Key).ToArray())
                {
                    if (maps.Count <= 4) break;
                    maps.Remove(key);
                }
            }
        }
        return token.CanBeCanceled ? task.WaitAsync(token) : task;
    }

    private SpeedVarianceFrameMap Scan(uint length, uint? sourceLength, CancellationToken token)
    {
        try
        {
            int limit = Parameters.TryGetValue(CustomSpeedVarianceProvider.ScanLimitKey, out var raw)
                ? Convert.ToInt32(raw) : 1_000_000;
            if (limit < 1 || limit == int.MaxValue) throw new ArgumentOutOfRangeException(CustomSpeedVarianceProvider.ScanLimitKey);
            var frames = new List<uint>();
            for (uint frame = 0; frame <= (uint)limit; frame++)
            {
                token.ThrowIfCancellationRequested();
                using var scope = ValueProviderFrameContext.PushSpeedFrame(frame, length, sourceLength, token);
                if (ReadFinished(frame, length)) return new(frames.ToArray());
                if (frame == limit) throw new InvalidOperationException($"Custom speed did not finish within {limit} frames.");
                frames.Add(ReadSourceFrame(frame, sourceLength));
            }
            throw new InvalidOperationException("Custom speed scan limit exceeded.");
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            Logger.Log(ex, $"Calculate custom speed {BindedEffectProvidingSystemID ?? Id}", nameof(CustomSpeedVarianceEffect));
            throw;
        }
    }

    internal void InspectFrame(uint frame, uint length, uint? sourceLength, Action<bool, uint?> inspect, CancellationToken token)
    {
        lock (scanGate)
        {
            token.ThrowIfCancellationRequested();
            using var scope = ValueProviderFrameContext.PushSpeedFrame(frame, length, sourceLength, token);
            bool finished = ReadFinished(frame, length);
            inspect(finished, finished ? null : ReadSourceFrame(frame, sourceLength));
        }
    }

    internal bool ReadFinished(uint frame, uint length)
    {
        if (!Parameters.TryGetValue(CustomSpeedVarianceProvider.FinishedKey, out var value)) return frame >= length;
        return Read(value) is bool finished ? finished : throw new InvalidOperationException("Custom speed Finished must be boolean.");
    }

    internal uint ReadSourceFrame(uint frame, uint? sourceLength)
    {
        double value = Parameters.TryGetValue(CustomSpeedVarianceProvider.SourceFrameKey, out var source)
            ? Convert.ToDouble(Read(source) ?? throw new InvalidOperationException("Custom speed SourceFrame is missing.")) : frame;
        uint mapped = ClipTiming.RoundFrame(value);
        if (sourceLength.HasValue && mapped >= sourceLength.Value)
            throw new InvalidOperationException($"Custom speed source frame {mapped} at timeline frame {frame} exceeds available source length {sourceLength}.");
        return mapped;
    }

    private static object? Read(object value) => value is Func<object> getter ? getter() : value;

    public void Dispose()
    {
        Task? task;
        lock (gate)
        {
            if (disposed) return;
            disposed = true;
            lifetime.Cancel();
            task = workers.Count == 0 ? null : Task.WhenAll(workers);
        }
        void Release()
        {
            ReleaseGraph?.Invoke();
            foreach (var cancellation in cancellations) cancellation.Dispose();
            lifetime.Dispose();
        }
        if (task is null) Release();
        else _ = task.ContinueWith(t =>
        {
            if (t.IsFaulted) _ = t.Exception;
            Release();
        }, TaskScheduler.Default);
    }
}
