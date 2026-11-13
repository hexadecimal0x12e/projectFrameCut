using projectFrameCut.Drawing.Base;

namespace projectFrameCut.Render.RenderAPIBase.EffectAndMixture;

public interface IHwAccelPictureResources
{
    void SetBufferPoolLimit(bool enabled, long maxBytes);
    void Complete();
    void TrimPool();
    string GetStatistics();
    IEnumerable<IHwAccelPictureContext> CreateSiblingContexts() => [];
}

/// <summary>Renderer-owned device contexts and immutable uploads shared by its frames.</summary>
public sealed class HwAccelPictureSession : IDisposable
{
    public const long DefaultBufferPoolBytes = 128L * 1024 * 1024;
    public static bool DefaultEnableBufferPoolLimit { get; set; } = true;
    public static long DefaultBufferPoolMaxBytes { get; set; } = DefaultBufferPoolBytes;
    public bool EnableBufferPoolLimit { get; set; } = DefaultEnableBufferPoolLimit;
    public long BufferPoolMaxBytes { get; set; } = DefaultBufferPoolMaxBytes;
    private readonly object gate = new();
    private readonly List<IHwAccelPictureContext> contexts = [];
    private readonly Dictionary<UploadKey, CachedUpload> uploads = new();
    private int nextContext, activeUploads;
    private readonly Dictionary<UploadKey, UploadGate> uploading = new();
    private sealed class UploadGate { public readonly object Gate = new(); public int Users; }
    private long hits, misses, uncached, retainedBytes;
    private bool disposed;
    private string? finalStatistics;
    private readonly record struct UploadKey(object Identity, IHwAccelPictureContext Context, int Width, int Height, int Bits, bool Alpha, bool Hdr, Half Peak);
    private sealed record CachedUpload(IHwAccelPicture Picture, WeakReference<IPicture> Source, long Bytes);

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
                    foreach (var c in contexts.OfType<IHwAccelPictureResources>())
                        c.SetBufferPoolLimit(EnableBufferPoolLimit, BufferPoolMaxBytes > 0 ? BufferPoolMaxBytes : DefaultBufferPoolMaxBytes);
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
                        cached.Source.SetTarget(source);
                        return WithMetadata(cached.Picture.Retain(), source);
                    }
                    misses++;
                }
                var picture = WithMetadata(context.Upload(source), source);
                try
                {
                    lock (gate)
                        Cache(key, picture, source, checked((long)source.Pixels * (6 * sizeof(ushort))));
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

    private void Cache(UploadKey key, IHwAccelPicture picture, IPicture source, long size)
    {
        if (disposed) return;
        var retained = picture.Retain();
        try
        {
            uploads.Add(key, new(retained, new(source), size));
            retainedBytes += size;
        }
        catch
        {
            retained.Dispose();
            throw;
        }
    }

    internal void ReleaseUnusedUploads()
    {
        var released = new List<IHwAccelPicture>();
        lock (gate)
        {
            foreach (var entry in uploads.ToArray())
            {
                if (entry.Value.Source.TryGetTarget(out var source) && !source.Disposed)
                {
                    try
                    {
                        if (ReferenceEquals((source as ISharedPicture)?.ContentIdentity, entry.Key.Identity)) continue;
                    }
                    catch (ObjectDisposedException) { }
                    catch (InvalidOperationException) { continue; }
                }
                uploads.Remove(entry.Key);
                retainedBytes -= entry.Value.Bytes;
                released.Add(entry.Value.Picture);
            }
        }
        foreach (var picture in released)
        {
            try { picture.Dispose(); }
            catch (Exception ex) { Logger.Log(ex, "Release unused hardware upload", nameof(HwAccelPictureSession)); }
        }
    }

    private static IHwAccelPicture WithMetadata(IHwAccelPicture picture, IPicture source)
    {
        picture.Tag = source.Tag;
        picture.ProcessStack = source.ProcessStack.ToList();
        return picture;
    }

    public string GetStatistics()
    {
        lock (gate)
            return finalStatistics ?? $"GPU uploads: {hits} hits, {misses} misses, {Interlocked.Read(ref uncached)} uncached, retained={retainedBytes / 1024 / 1024} MiB; "
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
            foreach (var upload in uploads.Values)
            {
                try { upload.Picture.Dispose(); }
                catch (Exception ex) { Logger.Log(ex, "Release cached hardware upload", nameof(HwAccelPictureSession)); }
            }
            uploads.Clear();
            retainedBytes = 0;
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
