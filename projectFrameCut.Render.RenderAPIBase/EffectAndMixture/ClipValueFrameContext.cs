using projectFrameCut.Drawing.Base;
using projectFrameCut.Render.RenderAPIBase.ClipAndTrack;
using projectFrameCut.Shared;

namespace projectFrameCut.Render.RenderAPIBase.EffectAndMixture;

public interface IClipValueEndpointProvider : IEffectProvider
{
    bool IsExport { get; }
    EffectArgumentFieldType ValueType { get; set; }
    Guid SourceClipId { get; set; }
    Guid SourceEndpointId { get; set; }
}

public static class ClipValueEndpointExtensions
{
    public static bool CanPassTo(this IClipValueEndpointProvider output, EffectPipeline pipeline, bool sameClip) =>
        sameClip
            ? output.GetPipeline() == EffectPipeline.NativeContent && pipeline == EffectPipeline.Picture && !output.ValueType.IsContent()
            : output.GetPipeline() == pipeline;
}

public sealed class ClipValueBindingException(string message, Exception? inner = null) : InvalidOperationException(message, inner) { }

/// <summary>Owns endpoint snapshots and graph results for one synchronous timeline-frame evaluation.</summary>
public sealed class ClipValueFrameContext : IDisposable
{
    [ThreadStatic] private static ClipValueFrameContext? current;
    public static ClipValueFrameContext? Current => current;
    private readonly ClipValueFrameContext? previous;
    private readonly IReadOnlyDictionary<Guid, IClip> clips;
    private readonly Action<IClip> evaluate;
    private readonly Func<IPicture, IPicture> copyPicture;
    private readonly Func<IClip, bool>? isAvailable;
    private readonly Dictionary<(Guid Clip, Guid Endpoint), object> values = new();
    private readonly Dictionary<object, object> computed = new();
    private readonly HashSet<Guid> evaluating = [];
    private readonly HashSet<Guid> evaluated = [];
    private readonly HashSet<IPicture> pictures = new(ReferenceEqualityComparer.Instance);
    private bool disposed;
    private Guid? publishingClip;
    private int transformInputDepth;
    public uint FrameIndex { get; }
    public bool IsTransformInput => transformInputDepth > 0;

    public ClipValueFrameContext(IReadOnlyList<IClip> clips, uint frame, Action<IClip> evaluate,
        Func<IPicture, IPicture> copyPicture, Func<IClip, bool>? isAvailable = null)
    {
        this.clips = clips.ToDictionary(c => c.Id);
        FrameIndex = frame;
        this.evaluate = evaluate;
        this.copyPicture = copyPicture;
        this.isAvailable = isAvailable;
        previous = current;
        current = this;
    }

    public object Snapshot(object value)
    {
        if (value is projectFrameCut.Drawing.Vector.VectorPicture vector) return VectorPictureEffectProcessing.Copy(vector);
        if (value is IPicture picture)
        {
            if (pictures.Contains(picture)) return picture;
            var copy = copyPicture(picture);
            pictures.Add(copy);
            return copy;
        }
        if (value is IReadOnlyDictionary<string, object?> outputs)
            return outputs.ToDictionary(p => p.Key, p => p.Value is null ? null : Snapshot(p.Value));
        return value;
    }

    public void Publish(IClip clip, uint frame, Guid endpoint, object value)
    {
        if (frame != FrameIndex || !clip.ContainsFrame(frame) || isAvailable?.Invoke(clip) == false
            || publishingClip is { } owner && owner != clip.Id
            || values.ContainsKey((clip.Id, endpoint))) return;
        values[(clip.Id, endpoint)] = Snapshot(value);
    }

    public bool TryGetComputed<T>(object key, out T? result) where T : class
    {
        result = computed.GetValueOrDefault(key) as T;
        return result is not null;
    }

    public void SetComputed(object key, object result) => computed[key] = result;

    public IDisposable? EnterClip(Guid clip, bool independent = false)
    {
        if (transformInputDepth > 0 && !independent) return null;
        var scope = new EvaluationScope(this, publishingClip, transformInputDepth);
        publishingClip = clip;
        if (independent) transformInputDepth = 0;
        return scope;
    }

    public IDisposable EnterTransformInput()
    {
        var scope = new EvaluationScope(this, publishingClip, transformInputDepth);
        transformInputDepth++;
        return scope;
    }

    private sealed class EvaluationScope(ClipValueFrameContext context, Guid? clip, int depth) : IDisposable
    {
        public void Dispose()
        {
            context.publishingClip = clip;
            context.transformInputDepth = depth;
        }
    }

    public object Read(IClip consumer, uint frame, IClipValueEndpointProvider input)
    {
        ClipValueBindingException Error(string reason) => new(
            $"Clip {consumer.Id}, endpoint input {input.Id}, frame {frame}: source clip {input.SourceClipId}, endpoint {input.SourceEndpointId}: {reason}");
        if (frame != FrameIndex) throw Error("The source is unavailable at this evaluation frame.");
        if (!input.Enabled) throw Error("The endpoint input is disabled.");
        if (!clips.TryGetValue(input.SourceClipId, out var source)) throw Error("The source clip does not exist.");
        bool sameClip = source.Id == consumer.Id;
        if (!sameClip && ClipLayerOrder.BottomToTop.Compare(source, consumer) >= 0)
            throw Error("The source must precede the consumer in clip render order.");
        if (!source.ContainsFrame(frame)) throw Error("The source clip is inactive.");
        if (isAvailable?.Invoke(source) == false) throw Error("The source clip is unavailable at this frame.");
        if (source.EffectProvidersInstances?.FirstOrDefault(p => p.Id == input.SourceEndpointId) is not IClipValueEndpointProvider { IsExport: true } output)
            throw Error("The source endpoint does not exist.");
        if (!output.Enabled) throw Error("The source endpoint is disabled.");
        if (!output.CanPassTo(input.GetPipeline(), sameClip)) throw Error("The endpoint pipeline has changed or does not precede the input stage.");
        if (output.ValueType.BaseType() != input.ValueType.BaseType()) throw Error("The endpoint type has changed.");
        if (output.GetMainInputSource() == IEffectProvider.NoConnectionGUID.ToString()) throw Error("The source endpoint is disconnected.");
        if (!values.ContainsKey((source.Id, output.Id)) && !evaluated.Contains(source.Id))
        {
            if (!evaluating.Add(source.Id)) throw Error("The endpoint dependency contains a cycle.");
            try
            {
                evaluate(source);
                evaluated.Add(source.Id);
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex) { throw new ClipValueBindingException(Error(ex.Message).Message, ex); }
            finally { evaluating.Remove(source.Id); }
        }
        if (!values.TryGetValue((source.Id, output.Id), out var value)) throw Error("The source endpoint did not produce a value.");
        if (!EffectFieldTypes.Accepts(input.ValueType, value)) throw Error("The source returned an incompatible value.");
        return value;
    }

    public void Dispose()
    {
        if (disposed) return;
        disposed = true;
        current = previous;
        foreach (var picture in pictures)
        {
            try { picture.Dispose(); }
            catch (Exception ex) { projectFrameCut.Shared.Logger.Log(ex, "Release clip endpoint frame", this); }
        }
        pictures.Clear();
        values.Clear();
        computed.Clear();
    }
}
