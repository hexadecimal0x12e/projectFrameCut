using projectFrameCut.Drawing.Base;
using projectFrameCut.Drawing.Base.Picture;
using projectFrameCut.Drawing.Processing.Resizing;
using projectFrameCut.Render.RenderAPIBase.ClipAndTrack;
using projectFrameCut.Render.RenderAPIBase.Context;

namespace projectFrameCut.Render.RenderAPIBase.EffectAndMixture;
/// <summary>A device image snapshot. Retained handles share pixels and are disposed independently.</summary>
public interface IHwAccelPicture : IPicture
{
    IHwAccelPictureContext Context { get; }

    object ContentIdentity { get; }

    bool IsHdr { get; }

    float MaximumBrightness { get; }

    IHwAccelPicture Retain();
    IPicture Download();
}

public interface IHwAccelPictureContext : IDisposable
{
    IDisposable Retain();
    IHwAccelPicture Upload(IPicture source);
}

public interface IHwAccelPictureGeometry
{
    IHwAccelPicture Resize(IHwAccelPicture source, int width, int height, bool preserveAspect);
    IHwAccelPicture Rotate(IHwAccelPicture source, float angle, bool expandCanvas);
    IHwAccelPicture ToHdr(IHwAccelPicture source, int brightness);
}

public interface IHwAccelPictureEffect : IEffect
{
    IHwAccelPicture RenderHwAccel(IHwAccelPicture source, EffectExecutionContext context);
}

public interface IHwAccelMixture : IMixture
{
    IHwAccelPicture MixHwAccel(IHwAccelPicture bottom, IHwAccelPicture top, IPicture.PicturePixelMode pixelMode, int x, int y, int width, int height);
}

public interface IHwAccelTransform : ITransform
{
    IHwAccelPicture RenderHwAccel(IHwAccelPicture left, IHwAccelPicture? right, float progress, TransformSide side, int width, int height);
}

/// <summary>Owns uploads and a device context for one frame, including parallel clip jobs.</summary>
public sealed class HwAccelPictureScope : IDisposable
{
    private static readonly AsyncLocal<HwAccelPictureScope?> current = new();
    private readonly object gate = new();
    private readonly Dictionary<object, IHwAccelPicture> uploads = new();
    private readonly HwAccelPictureSession? session;
    public HwAccelPictureScope() : this(null) { }
    public HwAccelPictureScope(HwAccelPictureSession? session) => this.session = session;
    private readonly Dictionary<IHwAccelPictureContext, IDisposable> contexts = new(ReferenceEqualityComparer.Instance);
    private IHwAccelPictureContext? context;
    private bool disposed;
    public static Func<IHwAccelPictureContext>? CreateContext { get; set; }
    public static HwAccelPictureScope? Current => current.Value;

    public IDisposable Enter()
    {
        var previous = current.Value;
        current.Value = this;
        return new Entry(previous);
    }

    private sealed class Entry(HwAccelPictureScope? previous) : IDisposable
    {
        public void Dispose() => current.Value = previous;
    }

    public IHwAccelPicture Upload(IPicture source)
    {
        lock (gate)
        {
            ObjectDisposedException.ThrowIf(disposed, this);
            if (source is IHwAccelPicture gpu)
            {
                if (context is null) UseContext(gpu);
                return ReferenceEquals(gpu.Context, context) ? gpu.Retain() : context!.Upload(source);
            }

            if (context is null)
            {
                context = session?.GetContext() ?? (CreateContext ?? throw new NotSupportedException("No hardware picture backend is available."))();
                contexts.Add(context, session is null ? context : context.Retain());
            }
            if (session is not null) return session.Upload(context, source);
            if ((source as ISharedPicture)?.ContentIdentity is not { } identity)
                return context.Upload(source);
            var key = (identity, source.Width, source.Height, source.BitPerPixel, source.HasAlphaChannel,
                source is IHDRPicture<ushort>, (source as IHDRPicture<ushort>)?.MaximumBrightness ?? 203);
            if (uploads.TryGetValue(key, out var cached))
            {
                var retained = cached.Retain();
                retained.Tag = source.Tag;
                retained.ProcessStack = source.ProcessStack.ToList();
                return retained;
            }
            var picture = context.Upload(source);
            uploads.Add(key, picture);
            return picture.Retain();
        }
    }

    internal void UseContext(IHwAccelPicture picture)
    {
        lock (gate)
        {
            ObjectDisposedException.ThrowIf(disposed, this);
            context ??= session?.GetContext() ?? picture.Context;
            if (!contexts.ContainsKey(context))
                contexts.Add(context, context.Retain());
        }
    }

    public void Complete()
    {
        lock (gate)
            foreach (var c in contexts.Keys)
                if (c is IHwAccelPictureResources resources) resources.Complete();
    }

    public void Dispose()
    {
        lock (gate)
        {
            if (disposed)
                return;
            disposed = true;
            foreach (var picture in uploads.Values)
            {
                try
                {
                    picture.Dispose();
                }
                catch (Exception ex)
                {
                    Logger.Log(ex, "Release hardware picture upload", nameof(HwAccelPictureScope));
                }
            }

            uploads.Clear();
            foreach (var lease in contexts.Values)
            {
                try
                {
                    lease.Dispose();
                }
                catch (Exception ex)
                {
                    Logger.Log(ex, "Release hardware picture context", nameof(HwAccelPictureScope));
                }
            }

            contexts.Clear();
        }
    }
}

public static class EffectPictureProcessing
{
    public static IPicture Copy(IPicture picture)
    {
        if (picture is IHwAccelPicture gpu)
            return gpu.Retain();
        if (picture is ISharedPicture shared) return shared.Snapshot();
        if (picture is not IHDRPicture<ushort> hdr)
            return picture.Clone();
        using var copy = picture.Clone();
        return new HDRPicture16bpp((IPicture<ushort>)copy, true)
        {
            Brightness = hdr.Brightness.ToArray(),
            MaximumBrightness = hdr.MaximumBrightness,
            HasAlphaChannel = copy.HasAlphaChannel,
            Tag = copy.Tag,
            ProcessStack = copy.ProcessStack,
        };
    }

    public static IPicture Materialize(IPicture picture) => picture is IHwAccelPicture gpu ? gpu.Download() : picture;
    private static IHwAccelPicture Track(IHwAccelPicture picture, IHwAccelPicture? input = null, IHwAccelPicture? second = null)
    {
        if (ReferenceEquals(picture, input) || ReferenceEquals(picture, second))
            picture = picture.Retain();
        HwAccelPictureScope.Current?.UseContext(picture);
        return picture;
    }

    public static IPicture Resize(IPicture source, int width, int height, bool preserveAspect)
    {
        if (source is IHwAccelPicture gpu && gpu.Context is IHwAccelPictureGeometry geometry)
            return geometry.Resize(gpu, width, height, preserveAspect);
        var picture = Materialize(source);
        try
        {
            return picture is IHDRPicture<ushort> hdr ? hdr.Resize(width, height, preserveAspect) : picture.Resize(width, height, preserveAspect);
        }
        finally
        {
            if (!ReferenceEquals(picture, source))
                picture.Dispose();
        }
    }

    public static IPicture ToHdr(IPicture source, int brightness)
    {
        if (source is IHwAccelPicture gpu && gpu.Context is IHwAccelPictureGeometry geometry)
            return geometry.ToHdr(gpu, brightness);
        var picture = Materialize(source);
        try
        {
            return HDRPicture16bpp.ToHDRPictureBySignal(picture, brightness);
        }
        finally
        {
            if (!ReferenceEquals(picture, source))
                picture.Dispose();
        }
    }

    public static IHwAccelPicture Upload(IPicture source)
    {
        if (HwAccelPictureScope.Current is { } scope)
            return scope.Upload(source);
        if (source is IHwAccelPicture gpu)
            return gpu.Retain();
        using var context = (HwAccelPictureScope.CreateContext ?? throw new NotSupportedException("No hardware picture backend is available."))();
        return context.Upload(source);
    }

    public static IPicture Render(IEffect effect, IPicture source, int width, int height, float progress = 0)
    {
        if (effect is IHwAccelPictureEffect accelerated)
        {
            using var input = Upload(source);
            return Track(accelerated.RenderHwAccel(input, new() { Input = input, Progress = progress, TargetWidth = width, TargetHeight = height, RelativeWidth = effect.RelativeWidth, RelativeHeight = effect.RelativeHeight, CancellationToken = ValueProviderFrameContext.CancellationToken, }), input);
        }

        var picture = Materialize(source);
        try
        {
            var result = effect is IContinuousEffect continuous ? continuous.Render(picture, progress, width, height) : effect is IColorAdjustEffect color ? color.Process(picture) : ((INormalEffect)effect).Render(picture, width, height);
            if (ReferenceEquals(result, picture))
                picture = null !;
            return result;
        }
        finally
        {
            if (!ReferenceEquals(source, picture))
                picture?.Dispose();
        }
    }

    public static IPicture Mix(IMixture mixture, IPicture bottom, IPicture top, IPicture.PicturePixelMode pixelMode, int x, int y, int width, int height)
    {
        if (mixture is IHwAccelMixture accelerated)
        {
            using var a = Upload(bottom);
            using var b = Upload(top);
            return Track(accelerated.MixHwAccel(a, b, pixelMode, x, y, width, height), a, b);
        }

        if (mixture.AcceptsHwAccelPictures)
            return mixture.Mix(bottom, top, pixelMode, x, y, width, height);
        var left = Materialize(bottom);
        var right = Materialize(top);
        IPicture? result = null;
        try
        {
            return result = mixture.Mix(left, right, pixelMode, x, y, width, height);
        }
        finally
        {
            if (!ReferenceEquals(left, bottom) && !ReferenceEquals(left, result))
                left.Dispose();
            if (!ReferenceEquals(right, top) && !ReferenceEquals(right, result))
                right.Dispose();
        }
    }

    public static IPicture Transform(ITransform transform, IPicture left, IPicture? right, float progress, TransformSide side, int width, int height)
    {
        if (transform is IHwAccelTransform accelerated)
        {
            using var a = Upload(left);
            using var b = right is null ? null : Upload(right);
            return Track(accelerated.RenderHwAccel(a, b, progress, side, width, height), a, b);
        }

        var aCpu = Materialize(left);
        var bCpu = right is null ? null : Materialize(right);
        IPicture? result = null;
        try
        {
            return result = transform.Render(aCpu, bCpu, progress, side, width, height);
        }
        finally
        {
            if (!ReferenceEquals(aCpu, left) && !ReferenceEquals(aCpu, result))
                aCpu.Dispose();
            if (!ReferenceEquals(bCpu, right) && !ReferenceEquals(bCpu, result))
                bCpu?.Dispose();
        }
    }

    public static object? Compute(IEffect effect, EffectExecutionContext context, IReadOnlyDictionary<string, object?>? bindings = null)
    {
        context.CancellationToken.ThrowIfCancellationRequested();
        bool hardware = effect is IHwAccelPictureEffect && context.Input is IPicture && !effect.IsPictureTransportBoundary;
        bool passthrough = !effect.IsPictureTransportBoundary && (effect.AcceptsHwAccelPictures || effect is IClipPositionProvider or IContinuousClipPositionProvider);
        var temporary = new List<IPicture>();
        var uploads = new Dictionary<IPicture, IPicture>(ReferenceEqualityComparer.Instance);
        var downloads = new Dictionary<IPicture, IPicture>(ReferenceEqualityComparer.Instance);
        object? ConvertValue(object? value, EffectArgumentFieldType type = EffectArgumentFieldType.Unknown)
        {
            bool upload = !effect.IsPictureTransportBoundary && (hardware || type.IsHwAccelPicture());
            if (value is not IPicture picture || !upload && (passthrough || picture is not IHwAccelPicture))
                return value;
            var converted = upload ? uploads : downloads;
            if (converted.TryGetValue(picture, out var cached))
                return cached;
            var convertedPicture = upload ? Upload(picture) : Materialize(picture);
            if (!ReferenceEquals(convertedPicture, picture))
                temporary.Add(convertedPicture);
            converted[picture] = convertedPicture;
            return convertedPicture;
        }

        object? ConvertBinding(string key, object? value) => value is IPicture picture && uploads.TryGetValue(picture, out var uploaded) ? uploaded : ConvertValue(value, context.InputTypes.GetValueOrDefault(key));
        object? result = null;
        var previous = IRenderContext.CurrentFrameBuffer;
        try
        {
            var input = ConvertValue(context.Input, context.InputTypes.GetValueOrDefault(EffectProviderAnchorExtensions.InputKey));
            var parameters = context.Parameters.ToDictionary(p => p.Key, p => ConvertValue(p.Value, context.InputTypes.GetValueOrDefault(p.Key)));
            using var scope = ValueProviderFrameContext.PushValues((bindings ?? new Dictionary<string, object?>()).ToDictionary(p => p.Key, p => ConvertBinding(p.Key, p.Value)));
            var invocation = new EffectExecutionContext
            {
                Input = input,
                Parameters = parameters,
                Clip = context.Clip,
                FrameIndex = context.FrameIndex,
                Progress = context.Progress,
                ClipProgress = context.ClipProgress,
                TargetWidth = context.TargetWidth,
                TargetHeight = context.TargetHeight,
                RelativeWidth = context.RelativeWidth,
                RelativeHeight = context.RelativeHeight,
                CancellationToken = context.CancellationToken,
                InputTypes = context.InputTypes,
                PicturesPrepared = true,
            };
            IRenderContext.CurrentFrameBuffer = input as IPicture;
            result = hardware ? ((IHwAccelPictureEffect)effect).RenderHwAccel((IHwAccelPicture)input!, invocation) : effect is IMultipleOutputEffect multiple ? multiple.ComputeOutputs(invocation) : effect.Compute(invocation);
            if (result is IHwAccelPicture gpu)
                Track(gpu);
            return result;
        }
        finally
        {
            IRenderContext.CurrentFrameBuffer = previous;
            foreach (var picture in temporary)
                if (!ReferenceEquals(picture, result) && !(result is IReadOnlyDictionary<string, object?> outputs && outputs.Values.Any(v => ReferenceEquals(v, picture))))
                    picture.Dispose();
        }
    }
}
