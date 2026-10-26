using projectFrameCut.Drawing.Base;

namespace projectFrameCut.Render.Rendering;

/// <summary>A caller-owned cache of deterministic node outputs.</summary>
public sealed class FrameHashCache : IDisposable
{
    private readonly object gate = new();
    private readonly Dictionary<string, Entry> values = new(StringComparer.Ordinal);
    private long hits, misses, unsupported;
    private bool disposed;

    private sealed class Entry(object? value)
    {
        public object? Value = value;
        public bool Released;
    }

    internal bool TryRead(string key, out object? value)
    {
        value = null;
        Entry saved;
        lock (gate)
        {
            if (disposed || !values.TryGetValue(key, out saved!))
            {
                Interlocked.Increment(ref misses);
                return false;
            }
        }
        lock (saved)
        {
            if (saved.Released)
            {
                Interlocked.Increment(ref misses);
                return false;
            }
            value = FrameHashContext.CopyValue(saved.Value);
            Interlocked.Increment(ref hits);
            return true;
        }
    }

    internal void Write(string key, object? value)
    {
        if (!CanCache(value))
        {
            Interlocked.Increment(ref unsupported);
            return;
        }
        lock (gate)
        {
            if (disposed || values.ContainsKey(key)) return;
        }
        Entry? copy = new(FrameHashContext.CopyValue(value));
        try
        {
            lock (gate)
            {
                if (disposed || values.ContainsKey(key)) return;
                values.Add(key, copy);
                copy = null;
            }
        }
        finally
        {
            if (copy is not null) Release(copy);
        }
    }

    private static void Release(Entry entry)
    {
        lock (entry)
        {
            if (entry.Released) return;
            entry.Released = true;
            FrameHashContext.ReleaseValue(entry.Value);
            entry.Value = null;
        }
    }

    private static bool CanCache(object? value) => value switch
    {
        RenderAPIBase.EffectAndMixture.IHwAccelPicture => false,
        IReadOnlyDictionary<string, object?> outputs => outputs.Values.All(CanCache),
        null or IPicture or projectFrameCut.Drawing.Vector.VectorPicture
            or projectFrameCut.Shared.IAudioSamples<float> or string or Array or RenderAPIBase.ClipAndTrack.IClip => true,
        _ => value.GetType().IsValueType,
    };

    internal string GetStatusInfo() => $"node cache: {Interlocked.Read(ref hits)} hits, {Interlocked.Read(ref misses)} misses, {Interlocked.Read(ref unsupported)} unsupported writes skipped";

    public void Clear()
    {
        Entry[] saved;
        lock (gate)
        {
            saved = values.Values.ToArray();
            values.Clear();
        }
        foreach (var entry in saved) Release(entry);
    }

    public void Dispose()
    {
        lock (gate)
        {
            if (disposed) return;
            disposed = true;
        }
        Clear();
    }
}
