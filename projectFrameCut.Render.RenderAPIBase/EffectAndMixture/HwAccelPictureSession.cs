using projectFrameCut.Drawing.Base;

namespace projectFrameCut.Render.RenderAPIBase.EffectAndMixture;

public sealed class HwAccelResourceBudget
{
    public const long DefaultBytes = 128L * 1024 * 1024;
    private readonly object gate = new();
    private long bytes;
    public long Bytes { get { lock (gate) return bytes; } }
    public bool TryReserve(long size)
    {
        lock (gate)
        {
            if (size > DefaultBytes - bytes) return false;
            bytes += size;
            return true;
        }
    }
    public void Release(long size) { lock (gate) bytes -= size; }
}

public interface IHwAccelPictureResources
{
    void SetBudget(HwAccelResourceBudget budget);
    void Complete();
    void TrimPool();
    string GetStatistics();
    IEnumerable<IHwAccelPictureContext> CreateSiblingContexts() => [];
}

/// <summary>Renderer-owned device contexts and immutable uploads shared by its frames.</summary>
public sealed class HwAccelPictureSession : IDisposable
{
    private readonly object gate = new();
    private readonly HwAccelResourceBudget budget = new();
    private readonly List<IHwAccelPictureContext> contexts = [];
    private readonly Dictionary<UploadKey, LinkedListNode<CachedUpload>> uploads = new();
    private readonly LinkedList<CachedUpload> order = new();
    private int nextContext, activeUploads;
    private readonly Dictionary<UploadKey, UploadGate> uploading = new();
    private sealed class UploadGate { public readonly object Gate = new(); public int Users; }
    private long hits, misses, uncached;
    private bool disposed;
    private string? finalStatistics;
    private readonly record struct UploadKey(object Identity, IHwAccelPictureContext Context, int Width, int Height, int Bits, bool Alpha, bool Hdr, Half Peak);
    private sealed record CachedUpload(UploadKey Key, IHwAccelPicture Picture, long Bytes);

    internal IHwAccelPictureContext GetContext()
    {
        lock (gate)
        {
            ObjectDisposedException.ThrowIf(disposed, this);
            if (contexts.Count == 0)
            {
                var first = (HwAccelPictureScope.CreateContext ?? throw new NotSupportedException("No hardware picture backend is available."))();
                contexts.Add(first);
                try
                {
                    if (first is IHwAccelPictureResources resources)
                        contexts.AddRange(resources.CreateSiblingContexts());
                    foreach (var c in contexts)
                        if (c is IHwAccelPictureResources r) r.SetBudget(budget);
                }
                catch
                {
                    foreach (var c in contexts) c.Dispose();
                    contexts.Clear();
                    throw;
                }
            }
            var selected = contexts[nextContext];
            nextContext = (nextContext + 1) % contexts.Count;
            return selected;
        }
    }

    internal IHwAccelPicture Upload(IHwAccelPictureContext context, IPicture source)
    {
        lock (gate)
        {
            ObjectDisposedException.ThrowIf(disposed, this);
            activeUploads++;
        }
        UploadGate? pending = null;
        UploadKey key = default;
        try
        {
            var identity = (source as ISharedPicture)?.ContentIdentity;
            if (identity is null)
            {
                Interlocked.Increment(ref uncached);
                return WithMetadata(context.Upload(source), source);
            }
            var hdr = source as IHDRPicture<Half>;
            key = new(identity, context, source.Width, source.Height, source.BitPerPixel, source.HasAlphaChannel, hdr is not null, hdr?.MaximumBrightness ?? (Half)203);
            lock (gate)
            {
                if (!uploading.TryGetValue(key, out pending)) uploading.Add(key, pending = new());
                pending.Users++;
            }
            lock (pending.Gate)
            {
                lock (gate)
                {
                    if (uploads.TryGetValue(key, out var cached))
                    {
                        hits++;
                        order.Remove(cached);
                        order.AddLast(cached);
                        return WithMetadata(cached.Value.Picture.Retain(), source);
                    }
                    misses++;
                }
                var picture = WithMetadata(context.Upload(source), source);
                try
                {
                    lock (gate)
                        Cache(key, picture, checked((long)source.Pixels * (6 * sizeof(ushort))));
                    return picture;
                }
                catch { picture.Dispose(); throw; }
            }
        }
        finally
        {
            lock (gate)
            {
                if (pending is not null && --pending.Users == 0) uploading.Remove(key);
                activeUploads--;
                Monitor.PulseAll(gate);
            }
        }
    }

    private void Cache(UploadKey key, IHwAccelPicture picture, long size)
    {
        if (disposed || size > HwAccelResourceBudget.DefaultBytes) return;
        bool reserved = budget.TryReserve(size);
        if (!reserved)
            foreach (var c in contexts)
                if (c is IHwAccelPictureResources resources) resources.TrimPool();
        while (!reserved && !(reserved = budget.TryReserve(size)))
        {
            if (order.First is not { } old) return;
            Remove(old);
            foreach (var c in contexts)
                if (c is IHwAccelPictureResources resources) resources.TrimPool();
        }
        IHwAccelPicture? retained = null;
        LinkedListNode<CachedUpload>? node = null;
        try
        {
            retained = picture.Retain();
            node = order.AddLast(new CachedUpload(key, retained, size));
            uploads.Add(key, node);
        }
        catch
        {
            if (node is not null) order.Remove(node);
            retained?.Dispose();
            budget.Release(size);
            throw;
        }
    }

    private static IHwAccelPicture WithMetadata(IHwAccelPicture picture, IPicture source)
    {
        picture.Tag = source.Tag;
        picture.ProcessStack = source.ProcessStack.ToList();
        return picture;
    }

    private void Remove(LinkedListNode<CachedUpload> node)
    {
        uploads.Remove(node.Value.Key);
        order.Remove(node);
        budget.Release(node.Value.Bytes);
        node.Value.Picture.Dispose();
    }

    public string GetStatistics()
    {
        lock (gate)
            return finalStatistics ?? $"GPU uploads: {hits} hits, {misses} misses, {Interlocked.Read(ref uncached)} uncached, retained={budget.Bytes / 1024 / 1024} MiB; "
                + string.Join("; ", contexts.OfType<IHwAccelPictureResources>().Select(c => c.GetStatistics()));
    }

    public void Dispose()
    {
        lock (gate)
        {
            if (disposed) return;
            disposed = true;
            while (activeUploads > 0) Monitor.Wait(gate);
            foreach (var c in contexts)
            {
                try { if (c is IHwAccelPictureResources resources) resources.Complete(); }
                catch (Exception ex) { Logger.Log(ex, "Complete hardware picture session", nameof(HwAccelPictureSession)); }
            }
            while (order.First is { } node)
            {
                try { Remove(node); }
                catch (Exception ex) { Logger.Log(ex, "Release cached hardware upload", nameof(HwAccelPictureSession)); }
            }
            foreach (var c in contexts)
            {
                try { c.Dispose(); }
                catch (Exception ex) { Logger.Log(ex, "Release hardware picture session", nameof(HwAccelPictureSession)); }
            }
            finalStatistics = GetStatistics();
            contexts.Clear();
        }
    }
}
