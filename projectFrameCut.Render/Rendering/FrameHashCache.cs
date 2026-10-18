using projectFrameCut.Drawing.Base;

namespace projectFrameCut.Render.Rendering;

/// <summary>A bounded, caller-owned cache of deterministic node outputs.</summary>
public sealed class FrameHashCache : IDisposable
{
    private const long MaxBytes = 64 * 1024 * 1024;
    private readonly object gate = new();
    private readonly Dictionary<string, Entry> values = new(StringComparer.Ordinal);
    private readonly Queue<string> order = new();
    private long bytes;
    private long hits, misses, oversized;
    private bool disposed;

    private sealed class Entry(object? value, long size)
    {
        public object? Value = value;
        public long Size { get; } = size;
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
        long size = Estimate(value);
        if (size > MaxBytes)
        {
            Interlocked.Increment(ref oversized);
            return;
        }
        lock (gate)
        {
            if (disposed || values.ContainsKey(key)) return;
        }
        Entry? copy = new(FrameHashContext.CopyValue(value), size);
        var stale = new List<Entry>();
        try
        {
            lock (gate)
            {
                if (disposed || values.ContainsKey(key)) return;
                while (order.Count > 0 && (bytes + size > MaxBytes || values.Count >= 32))
                {
                    string old = order.Dequeue();
                    stale.Add(values[old]);
                    bytes -= values[old].Size;
                    values.Remove(old);
                }
                values.Add(key, copy);
                order.Enqueue(key);
                bytes += size;
                copy = null;
            }
        }
        finally
        {
            if (copy is not null) Release(copy);
            foreach (var entry in stale) Release(entry);
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

    private static long Estimate(object? value) => value switch
    {
        RenderAPIBase.EffectAndMixture.IHwAccelPicture => MaxBytes + 1,
        projectFrameCut.Drawing.Vector.VectorPicture => MaxBytes + 1,
        IPicture p => (long)p.Width * p.Height * 16,
        IReadOnlyDictionary<string, object?> outputs => outputs.Values.Sum(Estimate),
        null => 0,
        string s => (long)s.Length * 2,
        Array a => a.LongLength * 16,
        RenderAPIBase.ClipAndTrack.IClip => 256,
        _ when value.GetType().IsValueType => 256,
        _ => MaxBytes + 1,
    };

    internal string GetStatusInfo() => $"node cache: {Interlocked.Read(ref hits)} hits, {Interlocked.Read(ref misses)} misses, {Interlocked.Read(ref oversized)} oversized/unsupported writes skipped";

    public void Dispose()
    {
        Entry[] saved;
        lock (gate)
        {
            if (disposed) return;
            disposed = true;
            saved = values.Values.ToArray();
            values.Clear();
            order.Clear();
            bytes = 0;
        }
        foreach (var entry in saved) Release(entry);
    }
}
