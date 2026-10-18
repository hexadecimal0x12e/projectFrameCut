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
        return Run(p, ResidentOperation.Hdr, p.Width, p.Height, [], bits: 16, hdr: true, peak: Math.Clamp(brightness, 100, 10000));
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
    protected abstract void WriteBuffer(ResidentBuffer buffer, float[] values);
    protected abstract float[] Read(ResidentBuffer buffer);
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
            budget.Release((long)length * sizeof(float));
            buffer.Returned = 0;
            reuses++;
            return buffer;
        }
        allocations++;
        var result = CreateBuffer(length);
        result.Owner = this;
        return result;
    }

    protected ResidentBuffer UploadBuffer(float[] values)
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
                pendingBytes += (long)buffer.Length * sizeof(float);
                if (pendingBytes >= HwAccelResourceBudget.DefaultBytes) Complete();
            }
            else CacheBuffer(buffer);
        }
    }

    private void CacheBuffer(ResidentBuffer buffer)
    {
        long size = (long)buffer.Length * sizeof(float);
        if (closing || failure is not null || size > HwAccelResourceBudget.DefaultBytes) { buffer.Destroy(); return; }
        while (!budget.TryReserve(size))
        {
            if (idle.First is not { } oldest) { buffer.Destroy(); return; }
            idle.RemoveFirst();
            budget.Release((long)oldest.Value.Length * sizeof(float));
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
                budget.Release((long)node.Value.Length * sizeof(float));
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
            int n = picture.Pixels;
            var data = GC.AllocateUninitializedArray<float>(checked(n * (picture is IHDRPicture<ushort> ? 5 : 4)));
            if (picture is IPicture<byte> p8)
            {
                var read = picture as IReadOnlyPictureChannels<byte>;
                ReadOnlySpan<byte> r = read is null ? p8.r : read.ReadRed;
                ReadOnlySpan<byte> g = read is null ? p8.g : read.ReadGreen;
                ReadOnlySpan<byte> b = read is null ? p8.b : read.ReadBlue;
                for (int i = 0; i < n; i++) { data[i] = r[i]; data[n + i] = g[i]; data[n * 2 + i] = b[i]; }
                ReadOnlySpan<float> alpha = read is null ? p8.a : read.ReadAlpha;
                if (picture.HasAlphaChannel && !alpha.IsEmpty)
                {
                    if (alpha.Length != n) throw new ArgumentException("Invalid alpha channel length.", nameof(source));
                    alpha.CopyTo(data.AsSpan(n * 3, n));
                }
                else data.AsSpan(n * 3, n).Fill(1f);
            }
            else if (picture is IPicture<ushort> p16)
            {
                var read = picture as IReadOnlyPictureChannels<ushort>;
                ReadOnlySpan<ushort> r = read is null ? p16.r : read.ReadRed;
                ReadOnlySpan<ushort> g = read is null ? p16.g : read.ReadGreen;
                ReadOnlySpan<ushort> b = read is null ? p16.b : read.ReadBlue;
                for (int i = 0; i < n; i++) { data[i] = r[i]; data[n + i] = g[i]; data[n * 2 + i] = b[i]; }
                ReadOnlySpan<float> alpha = read is null ? p16.a : read.ReadAlpha;
                if (picture.HasAlphaChannel && !alpha.IsEmpty)
                {
                    if (alpha.Length != n) throw new ArgumentException("Invalid alpha channel length.", nameof(source));
                    alpha.CopyTo(data.AsSpan(n * 3, n));
                }
                else data.AsSpan(n * 3, n).Fill(1f);
                if (picture is IHDRPicture<ushort> hdr)
                {
                    ReadOnlySpan<float> brightness = read is null ? hdr.Brightness : read.ReadBrightness;
                    if (brightness.Length != n) throw new ArgumentException("Invalid brightness channel length.", nameof(source));
                    brightness.CopyTo(data.AsSpan(n * 4, n));
                }
            }
            else throw new NotSupportedException($"Unsupported picture type: {picture.GetType().Name}");
            lock (Gate)
                return new ResidentPicture(new Image(this, UploadBuffer(data)), picture.Width, picture.Height, picture.BitPerPixel, picture.HasAlphaChannel, picture is IHDRPicture<ushort>, (picture as IHDRPicture<ushort>)?.MaximumBrightness ?? 203, picture.Tag, picture.ProcessStack.ToList());
        }
        finally
        {
            if (!ReferenceEquals(picture, source))
                picture.Dispose();
        }
    }

    internal ResidentPicture Run(ResidentPicture source, ResidentOperation operation, int width, int height, float[] args, bool? alpha = null, int? bits = null, bool? hdr = null, float? peak = null, ResidentPicture? second = null)
    {
        if (width <= 0 || height <= 0)
            throw new ArgumentOutOfRangeException(nameof(width));
        ObjectDisposedException.ThrowIf(source.Disposed, source);
        if (second is not null && !ReferenceEquals(second.Context, this))
            throw new ArgumentException("Device contexts must match.");
        bool outputHdr = (bits ?? source.BitPerPixel.Value) == 16 && (hdr ?? source.IsHdr);
        int channels = outputHdr ? 5 : 4;
        // Header: operation, dimensions, channel ranges, HDR peaks and HDR flags.
        var parameters = new float[Math.Max(32, 15 + args.Length)];
        parameters[0] = (int)operation;
        parameters[1] = source.Width;
        parameters[2] = source.Height;
        parameters[3] = width;
        parameters[4] = height;
        parameters[5] = source.BitPerPixel == 8 ? 255 : 65535;
        parameters[6] = (bits ?? source.BitPerPixel.Value) == 8 ? 255 : 65535;
        parameters[7] = second?.Width ?? source.Width;
        parameters[8] = second?.Height ?? source.Height;
        parameters[9] = second?.BitPerPixel == 8 ? 255 : 65535;
        parameters[10] = source.MaximumBrightness;
        parameters[11] = second?.MaximumBrightness ?? 203;
        parameters[12] = source.IsHdr ? 1 : 0;
        parameters[13] = second?.IsHdr == true ? 1 : 0;
        parameters[14] = outputHdr ? 1 : 0;
        args.CopyTo(parameters, 15);
        int count = operation == ResidentOperation.BlurHorizontal ? checked(height * channels) : operation == ResidentOperation.BlurVertical ? checked(width * channels) : checked(width * height);
        lock (Gate)
            return new(new Image(this, Dispatch(source.Storage.Buffer, second?.Storage.Buffer ?? source.Storage.Buffer, parameters, count)), width, height, bits ?? source.BitPerPixel.Value, alpha ?? source.HasAlphaChannel, outputHdr, peak ?? source.MaximumBrightness, source.Tag, source.ProcessStack.ToList());
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
                    float[] data;
                    lock (Context.Gate)
                    {
                        Context.Complete();
                        data = Context.Read(Buffer);
                    }
                    int n = source.Pixels;
                    var alpha = source.HasAlphaChannel ? data.AsSpan(n * 3, n).ToArray() : null;
                    if (source.BitPerPixel == 8)
                    {
                        var r = GC.AllocateUninitializedArray<byte>(n);
                        var g = GC.AllocateUninitializedArray<byte>(n);
                        var b = GC.AllocateUninitializedArray<byte>(n);
                        for (int i = 0; i < n; i++)
                        {
                            r[i] = (byte)Math.Clamp(data[i], 0, 255);
                            g[i] = (byte)Math.Clamp(data[n + i], 0, 255);
                            b[i] = (byte)Math.Clamp(data[n * 2 + i], 0, 255);
                        }
                        cpu = Picture8bpp.FromOwnedChannels(source.Width, source.Height, r, g, b, alpha);
                    }
                    else
                    {
                        var r = GC.AllocateUninitializedArray<ushort>(n);
                        var g = GC.AllocateUninitializedArray<ushort>(n);
                        var b = GC.AllocateUninitializedArray<ushort>(n);
                        for (int i = 0; i < n; i++)
                        {
                            r[i] = (ushort)Math.Clamp(data[i], 0, 65535);
                            g[i] = (ushort)Math.Clamp(data[n + i], 0, 65535);
                            b[i] = (ushort)Math.Clamp(data[n * 2 + i], 0, 65535);
                        }
                        cpu = source.IsHdr
                            ? HDRPicture16bpp.FromOwnedChannels(source.Width, source.Height, r, g, b, alpha, data.AsSpan(n * 4, n).ToArray(), source.MaximumBrightness)
                            : Picture16bpp.FromOwnedChannels(source.Width, source.Height, r, g, b, alpha);
                    }
                }

                var copy = ((ISharedPicture)cpu).Snapshot();
                copy.Tag = source.Tag;
                copy.ProcessStack = source.ProcessStack.ToList();
                copy.HasAlphaChannel = source.HasAlphaChannel;
                if (copy is HDRPicture16bpp hdr) hdr.MaximumBrightness = source.MaximumBrightness;

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
    public float MaximumBrightness { get; }
    public string Tag { get; set; }
    public List<PictureProcessStack> ProcessStack { get; set; }
    public bool CanBeDisposed { get; set; } = true;
    public bool Disposed => Volatile.Read(ref disposed) != 0;

    internal ResidentPicture(ResidentPictureContext.Image storage, int width, int height, int bits, bool alpha, bool hdr, float peak, string tag, List<PictureProcessStack> stack)
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
        if (bits != 8 && bits != 16)
            throw new NotSupportedException("Hardware pictures support 8-bit or 16-bit color.");
        if (bits.Value < BitPerPixel.Value && !IPicture.AllowPixelModeDowngrade)
            throw new InvalidOperationException("Pixel mode downgrade is disabled.");
        return bits == BitPerPixel ? Retain() : Storage.Context.Run(this, ResidentOperation.Convert, Width, Height, [], bits: bits, hdr: bits == 16 && IsHdr);
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
