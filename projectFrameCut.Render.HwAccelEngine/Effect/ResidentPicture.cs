using System.Runtime.InteropServices;
using projectFrameCut.Shared;
using projectFrameCut.Drawing.Base;
using projectFrameCut.Drawing.Base.Picture;
using projectFrameCut.Render.RenderAPIBase.EffectAndMixture;

namespace projectFrameCut.Render.HwAccelEngine.Effect;
internal abstract class ResidentBuffer : IDisposable
{
    internal ResidentPictureContext? Owner;
    internal int Returned;
    internal abstract int Length { get; }
    internal abstract void Destroy();
    public void Dispose()
    {
        if (Interlocked.Exchange(ref Returned, 1) != 0) return;
        if (Owner is { } owner) owner.ReturnBuffer(this);
        else Destroy();
    }
}

internal abstract class ResidentPictureContext : IHwAccelPictureContext, IHwAccelPictureGeometry, IHwAccelPictureResources
{
    protected readonly object Gate = new();
    private int references = 1;
    private int disposed;
    public IHwAccelPicture Resize(IHwAccelPicture source, int width, int height, bool preserveAspect) => ResidentPictureOperations.Resize(source, width, height, preserveAspect);
    public IHwAccelPicture Rotate(IHwAccelPicture source, float angle, bool expandCanvas)
    {
        var result = ResidentPictureOperations.Rotate(source, -angle, expandCanvas, pixelCenters: true);
        result.HasAlphaChannel = true;
        return result;
    }

    public IHwAccelPicture ToHdr(IHwAccelPicture source, int brightness)
    {
        using var p = ResidentPictureOperations.Own(source);
        if (p.IsHdr)
            return p.Retain();
        return Run(p, ResidentOperation.Hdr, p.Width, p.Height, [], bits: 16, hdr: true, peak: (Half)Math.Clamp(brightness, 100, 10000));
    }

    private HwAccelResourceBudget budget = new();
    private readonly LinkedList<ResidentBuffer> idle = new();
    private readonly List<ResidentBuffer> retired = [];
    private readonly List<IDisposable> pendingResources = [];
    private long pendingBytes, allocations, reuses, synchronizations;
    private int submissions;
    private bool closing;
    private System.Runtime.ExceptionServices.ExceptionDispatchInfo? failure;
    protected abstract ResidentBuffer CreateBuffer(int length);
    protected abstract void WriteBuffer(ResidentBuffer buffer, byte[] values);
    protected abstract byte[] Read(ResidentBuffer buffer);
    protected abstract ResidentBuffer Dispatch(ResidentBuffer source, ResidentBuffer second, float[] parameters, int count);
    protected abstract void Synchronize();
    protected abstract void Release();
    public virtual IEnumerable<IHwAccelPictureContext> CreateSiblingContexts() => [];

    public void SetBudget(HwAccelResourceBudget value)
    {
        lock (Gate) { TrimPool(); budget = value; }
    }

    protected ResidentBuffer RentBuffer(int length)
    {
        failure?.Throw();
        for (var node = idle.Last; node is not null; node = node.Previous)
        {
            if (node.Value.Length != length) continue;
            var buffer = node.Value;
            idle.Remove(node);
            budget.Release((long)length);
            buffer.Returned = 0;
            reuses++;
            return buffer;
        }
        allocations++;
        var result = CreateBuffer(length);
        result.Owner = this;
        return result;
    }

    protected ResidentBuffer UploadBuffer(byte[] values)
    {
        var buffer = RentBuffer(values.Length);
        try { WriteBuffer(buffer, values); return buffer; }
        catch { buffer.Dispose(); throw; }
    }

    protected void BeginSubmission()
    {
        failure?.Throw();
        if (submissions >= 32 || pendingBytes >= HwAccelResourceBudget.DefaultBytes) Complete();
        submissions++;
    }

    protected void KeepUntilComplete(IDisposable resource, long bytes = 0)
    {
        pendingResources.Add(resource);
        pendingBytes += bytes;
    }

    internal void ReturnBuffer(ResidentBuffer buffer)
    {
        lock (Gate)
        {
            if (submissions > 0)
            {
                retired.Add(buffer);
                pendingBytes += (long)buffer.Length;
                if (pendingBytes >= HwAccelResourceBudget.DefaultBytes) Complete();
            }
            else CacheBuffer(buffer);
        }
    }

    private void CacheBuffer(ResidentBuffer buffer)
    {
        long size = (long)buffer.Length;
        if (closing || failure is not null || size > HwAccelResourceBudget.DefaultBytes) { buffer.Destroy(); return; }
        while (!budget.TryReserve(size))
        {
            if (idle.First is not { } oldest) { buffer.Destroy(); return; }
            idle.RemoveFirst();
            budget.Release((long)oldest.Value.Length);
            oldest.Value.Destroy();
        }
        idle.AddLast(buffer);
    }

    public void Complete()
    {
        lock (Gate)
        {
            if (submissions == 0) { failure?.Throw(); return; }
            Exception? error = null;
            try { Synchronize(); }
            catch (Exception ex) { error = ex; failure = System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(ex); }
            synchronizations++;
            submissions = 0;
            foreach (var resource in pendingResources)
            {
                try { resource.Dispose(); }
                catch (Exception ex) { error ??= ex; }
            }
            pendingResources.Clear();
            foreach (var buffer in retired)
            {
                try { CacheBuffer(buffer); }
                catch (Exception ex) { error ??= ex; }
            }
            retired.Clear();
            pendingBytes = 0;
            if (error is not null)
            {
                failure ??= System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(error);
                failure.Throw();
            }
        }
    }

    public void TrimPool()
    {
        lock (Gate)
        {
            while (idle.First is { } node)
            {
                idle.RemoveFirst();
                budget.Release((long)node.Value.Length);
                node.Value.Destroy();
            }
        }
    }

    public string GetStatistics()
    {
        lock (Gate) return $"GPU buffers: {allocations} allocations, {reuses} reuses, {synchronizations} waits";
    }

    internal void RetainContext()
    {
        while (true)
        {
            int count = Volatile.Read(ref references);
            ObjectDisposedException.ThrowIf(count == 0, this);
            if (Interlocked.CompareExchange(ref references, count + 1, count) == count)
                return;
        }
    }

    public IDisposable Retain()
    {
        RetainContext();
        return new Lease(this);
    }

    private sealed class Lease(ResidentPictureContext context) : IDisposable
    {
        private int disposed;
        public void Dispose()
        {
            if (Interlocked.Exchange(ref disposed, 1) == 0)
                context.ReleaseReference();
        }
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref disposed, 1) == 0)
            ReleaseReference();
    }

    private void ReleaseReference()
    {
        if (Interlocked.Decrement(ref references) == 0)
            lock (Gate)
            {
                closing = true;
                try { Complete(); }
                finally { try { TrimPool(); } finally { Release(); } }
            }
    }

    public IHwAccelPicture Upload(IPicture source)
    {
        if (source is ResidentPicture same && ReferenceEquals(same.Context, this))
            return same.Retain();
        var picture = EffectPictureProcessing.Materialize(source);
        try
        {
            if (picture is not IPicture<Half> fp) throw new NotSupportedException($"Hardware rendering requires FP16, received {picture.GetType().Name}.");
            int n = picture.Pixels;
            var data = GC.AllocateUninitializedArray<byte>(checked(n * ResidentPixel.Size));
            var pixels = MemoryMarshal.Cast<byte, ResidentPixel>(data.AsSpan());
            var read = new PictureReadChannels<Half>(fp);
            ReadOnlySpan<Half> brightness = fp is IReadOnlyPictureChannels<Half> shared ? shared.ReadBrightness : fp is IHDRPicture<Half> hdr ? hdr.Brightness : [];
            for (int i = 0; i < n; i++)
            {
                pixels[i] = new() { Red = read.Red[i], Green = read.Green[i], Blue = read.Blue[i],
                    Alpha = fp.HasAlphaChannel && !read.Alpha.IsEmpty ? read.Alpha[i] : Half.One,
                    Brightness = brightness.IsEmpty ? Half.One : brightness[i] };
            }
            lock (Gate)
                return new ResidentPicture(new Image(this, UploadBuffer(data)), picture.Width, picture.Height, picture.BitPerPixel, picture.HasAlphaChannel, picture is IHDRPicture<Half>, (picture as IHDRPicture<Half>)?.MaximumBrightness ?? (Half)203, picture.Tag, picture.ProcessStack.ToList());
        }
        finally
        {
            if (!ReferenceEquals(picture, source))
                picture.Dispose();
        }
    }

    internal ResidentPicture Run(ResidentPicture source, ResidentOperation operation, int width, int height, float[] args, bool? alpha = null, int? bits = null, bool? hdr = null, Half? peak = null, ResidentPicture? second = null)
    {
        if (width <= 0 || height <= 0)
            throw new ArgumentOutOfRangeException(nameof(width));
        ObjectDisposedException.ThrowIf(source.Disposed, source);
        if (second is not null && !ReferenceEquals(second.Context, this))
            throw new ArgumentException("Device contexts must match.");
        bool outputHdr = (hdr ?? source.IsHdr);
        int channels = outputHdr ? 5 : 4;
        // Header: operation, dimensions, channel ranges, HDR peaks and HDR flags.
        var parameters = new float[Math.Max(32, 15 + args.Length)];
        parameters[0] = (int)operation;
        parameters[1] = source.Width;
        parameters[2] = source.Height;
        parameters[3] = width;
        parameters[4] = height;
        parameters[5] = 1;
        parameters[6] = 1;
        parameters[7] = second?.Width ?? source.Width;
        parameters[8] = second?.Height ?? source.Height;
        parameters[9] = 1;
        parameters[10] = (float)source.MaximumBrightness;
        parameters[11] = (float)(second?.MaximumBrightness ?? (Half)203);
        parameters[12] = source.IsHdr ? 1 : 0;
        parameters[13] = second?.IsHdr == true ? 1 : 0;
        parameters[14] = outputHdr ? 1 : 0;
        args.CopyTo(parameters, 15);
        int count = operation == ResidentOperation.BlurHorizontal ? checked(height * channels) : operation == ResidentOperation.BlurVertical ? checked(width * channels) : checked(width * height);
        lock (Gate)
            return new(new Image(this, Dispatch(source.Storage.Buffer, second?.Storage.Buffer ?? source.Storage.Buffer, parameters, count)), width, height, IPicture.PicturePixelMode.Float16Picture, alpha ?? source.HasAlphaChannel, outputHdr, peak ?? source.MaximumBrightness, source.Tag, source.ProcessStack.ToList());
    }

    internal sealed class Image
    {
        internal readonly ResidentPictureContext Context;
        internal readonly ResidentBuffer Buffer;
        private int references = 1;
        private readonly object gate = new();
        private IPicture? cpu;
        internal Image(ResidentPictureContext context, ResidentBuffer buffer)
        {
            Context = context;
            Buffer = buffer;
            context.RetainContext();
        }

        internal void Retain() => Interlocked.Increment(ref references);
        internal void Dispose()
        {
            if (Interlocked.Decrement(ref references) != 0)
                return;
            lock (gate)
            {
                try
                {
                    cpu?.Dispose();
                    lock (Context.Gate)
                        Buffer.Dispose();
                }
                finally
                {
                    Context.ReleaseReference();
                }
            }
        }

        internal IPicture Download(ResidentPicture source)
        {
            lock (gate)
            {
                if (cpu is null || cpu.HasAlphaChannel != source.HasAlphaChannel)
                {
                    cpu?.Dispose();
                    cpu = null;
                    byte[] bytes;
                    lock (Context.Gate)
                    {
                        Context.Complete();
                        bytes = Context.Read(Buffer);
                    }
                    var data = MemoryMarshal.Cast<byte, ResidentPixel>(bytes.AsSpan());
                    int n = source.Pixels;
                    var r = GC.AllocateUninitializedArray<Half>(n);
                    var g = GC.AllocateUninitializedArray<Half>(n);
                    var b = GC.AllocateUninitializedArray<Half>(n);
                    var alpha = source.HasAlphaChannel ? GC.AllocateUninitializedArray<Half>(n) : null;
                    var brightness = source.IsHdr ? GC.AllocateUninitializedArray<Half>(n) : null;
                    for (int i = 0; i < n; i++)
                    {
                        var p = data[i];
                        r[i] = p.Red;
                        g[i] = p.Green;
                        b[i] = p.Blue;
                        if (alpha is not null) alpha[i] = p.Alpha;
                        if (brightness is not null) brightness[i] = p.Brightness;
                    }
                    cpu = source.IsHdr
                        ? HDRPictureFP16.FromOwnedChannels(source.Width, source.Height, r, g, b, alpha, brightness!, source.MaximumBrightness)
                        : PictureFP16.FromOwnedChannels(source.Width, source.Height, r, g, b, alpha);
                }

                var copy = ((ISharedPicture)cpu).Snapshot();
                copy.Tag = source.Tag;
                copy.ProcessStack = source.ProcessStack.ToList();
                copy.HasAlphaChannel = source.HasAlphaChannel;
                if (copy is HDRPictureFP16 hdr) hdr.MaximumBrightness = source.MaximumBrightness;

                return copy;
            }
        }
    }
}

internal sealed class ResidentPicture : IHwAccelPicture
{
    internal readonly ResidentPictureContext.Image Storage;
    private int disposed;
    private readonly int width, height, pixels;
    public IHwAccelPictureContext Context => Storage.Context;
    public object ContentIdentity => Storage;

    public int Width
    {
        get => width;
        set
        {
            if (value != width)
                throw new InvalidOperationException("GPU image dimensions are immutable.");
        }
    }

    public int Height
    {
        get => height;
        set
        {
            if (value != height)
                throw new InvalidOperationException("GPU image dimensions are immutable.");
        }
    }

    public int Pixels
    {
        get => pixels;
        set
        {
            if (value != pixels)
                throw new InvalidOperationException("GPU image dimensions are immutable.");
        }
    }

    public IPicture.PicturePixelMode BitPerPixel { get; }
    public bool HasAlphaChannel { get; set; }
    public bool IsHdr { get; }
    public Half MaximumBrightness { get; }
    public string Tag { get; set; }
    public List<PictureProcessStack> ProcessStack { get; set; }
    public bool CanBeDisposed { get; set; } = true;
    public bool Disposed => Volatile.Read(ref disposed) != 0;

    internal ResidentPicture(ResidentPictureContext.Image storage, int width, int height, int bits, bool alpha, bool hdr, Half peak, string tag, List<PictureProcessStack> stack)
    {
        Storage = storage;
        this.width = width;
        this.height = height;
        pixels = checked(width * height);
        BitPerPixel = bits;
        HasAlphaChannel = alpha;
        IsHdr = hdr;
        MaximumBrightness = peak;
        Tag = tag;
        ProcessStack = stack;
    }

    public IHwAccelPicture Retain()
    {
        ObjectDisposedException.ThrowIf(Disposed, this);
        Storage.Retain();
        return new ResidentPicture(Storage, Width, Height, BitPerPixel, HasAlphaChannel, IsHdr, MaximumBrightness, Tag, ProcessStack.ToList());
    }

    public IPicture Download()
    {
        ObjectDisposedException.ThrowIf(Disposed, this);
        return Storage.Download(this);
    }

    public IPicture ToBitPerPixel(IPicture.PicturePixelMode bits)
    {
        if (bits == IPicture.PicturePixelMode.Float16Picture) return Retain();
        using var picture = Download();
        return picture.ToBitPerPixel(bits);
    }

    public object? GetSpecificChannel(IPicture.ChannelId id)
    {
        using var p = Download();
        return (p.GetSpecificChannel(id) as Array)?.Clone();
    }

    public ulong GetUniqueID()
    {
        using var p = Download();
        return p.GetUniqueID();
    }

    public string GetDiagnosticsInfo() => $"GPU {Width}x{Height}, {BitPerPixel} bit, HDR={IsHdr}";
    public void Dispose() => Dispose(false, false);
    public void Dispose(bool force = false, bool appendDisposedProcessStack = true)
    {
        if ((force || CanBeDisposed) && Interlocked.Exchange(ref disposed, 1) == 0)
            Storage.Dispose();
    }
}
