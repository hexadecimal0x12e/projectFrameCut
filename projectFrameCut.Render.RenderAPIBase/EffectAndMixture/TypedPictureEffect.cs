using projectFrameCut.Drawing.Base;
using projectFrameCut.Render.RenderAPIBase.ClipAndTrack;
using projectFrameCut.Shared;

namespace projectFrameCut.Render.RenderAPIBase.EffectAndMixture;

/// <summary>Keeps hardware execution resident while exposing typed CPU invocation boundaries.</summary>
public abstract class TypedPictureEffect(IEffect inner) : IEffect, IDisposable
{
    protected IEffect Inner { get; } = inner;
    public bool Owns(IEffect effect) => ReferenceEquals(Inner, effect);
    public bool IsDeterministic => Inner.IsDeterministic;
    public string FromPlugin => Inner.FromPlugin;
    public string TypeName => Inner.TypeName;
    public EffectType TypeOfEffect => Inner.TypeOfEffect;
    public EffectImplementType ImplementType => Inner.ImplementType;
    public bool AcceptsHwAccelPictures => Inner.AcceptsHwAccelPictures;
    public bool IsPictureTransportBoundary => Inner.IsPictureTransportBoundary;
    public string Name { get => Inner.Name; set => Inner.Name = value; }
    public string Id { get => Inner.Id; set => Inner.Id = value; }
    public Dictionary<string, object> Parameters => Inner.Parameters;
    public bool Enabled { get => Inner.Enabled; set => Inner.Enabled = value; }
    public int Index { get => Inner.Index; set => Inner.Index = value; }
    public bool IsReorderable => Inner.IsReorderable;
    public bool CanProcessFromCanvas => Inner.CanProcessFromCanvas;
    public int RelativeWidth { get => Inner.RelativeWidth; set => Inner.RelativeWidth = value; }
    public int RelativeHeight { get => Inner.RelativeHeight; set => Inner.RelativeHeight = value; }
    public string? BindedEffectProvidingSystemID { get => Inner.BindedEffectProvidingSystemID; set => Inner.BindedEffectProvidingSystemID = value; }
    public abstract IEffect WithParameters(Dictionary<string, object> parameters);
    public object? Compute(EffectExecutionContext context) => Inner.Compute(context);
    public void Initialize() => Inner.Initialize();
    public void Dispose() { if (Inner is IDisposable d) d.Dispose(); }

    public static IEffect Create<TPicture>(IEffect effect) where TPicture : IPicture => effect switch
    {
        IHwAccelTransform => new HardwareTransform<TPicture>(effect),
        IHwAccelMixture => new HardwareMixture<TPicture>(effect),
        IHwAccelPictureEffect when effect is IContinuousEffect => new HardwareContinuous<TPicture>(effect),
        IHwAccelPictureEffect when effect is IColorAdjustEffect => new HardwareColor<TPicture>(effect),
        IHwAccelPictureEffect when effect is INormalEffect => new HardwareNormal<TPicture>(effect),
        _ => effect,
    };

    public static IEffect CreateTransport<TPicture>(IEffect effect) where TPicture : IPicture => effect switch
    {
        IMultipleOutputEffect when effect is IContinuousEffect => new TransportContinuousMultiple<TPicture>(effect),
        IMultipleOutputEffect => new TransportMultiple<TPicture>(effect),
        ISourceReplacementEffect => new TransportSource<TPicture>(effect),
        ITransform => new TransportTransform<TPicture>(effect),
        IMixture => new TransportMixture<TPicture>(effect),
        IContinuousEffect => new TransportContinuous<TPicture>(effect),
        IColorAdjustEffect => new TransportColor<TPicture>(effect),
        INormalEffect => new TransportNormal<TPicture>(effect),
        _ => effect,
    };

    private class TransportNormal<TPicture>(IEffect effect) : TypedPictureEffect(effect), INormalEffect<TPicture> where TPicture : IPicture
    {
        public override IEffect WithParameters(Dictionary<string, object> parameters) => CreateTransport<TPicture>(Inner.WithParameters(parameters));
        public IPicture Render(TPicture source, int width, int height) => ((INormalEffect)Inner).Render(source, width, height);
    }

    private sealed class TransportColor<TPicture>(IEffect effect) : TransportNormal<TPicture>(effect), IColorAdjustEffect<TPicture> where TPicture : IPicture
    {
        public IPicture Process(TPicture source) => ((IColorAdjustEffect)Inner).Process(source);
    }

    private sealed class TransportContinuous<TPicture>(IEffect effect) : TypedPictureEffect(effect), IContinuousEffect<TPicture> where TPicture : IPicture
    {
        public int StartPoint { get => ((IContinuousEffect)Inner).StartPoint; set => ((IContinuousEffect)Inner).StartPoint = value; }
        public int EndPoint { get => ((IContinuousEffect)Inner).EndPoint; set => ((IContinuousEffect)Inner).EndPoint = value; }
        public bool IsScoped { get => ((IContinuousEffect)Inner).IsScoped; set => ((IContinuousEffect)Inner).IsScoped = value; }
        public override IEffect WithParameters(Dictionary<string, object> parameters) => CreateTransport<TPicture>(Inner.WithParameters(parameters));
        public IPicture Render(TPicture source, float progress, int width, int height) => ((IContinuousEffect)Inner).Render(source, progress, width, height);
    }

    private class TransportMultiple<TPicture>(IEffect effect) : TypedPictureEffect(effect), IMultipleOutputEffect<TPicture> where TPicture : IPicture
    {
        public override IEffect WithParameters(Dictionary<string, object> parameters) => CreateTransport<TPicture>(Inner.WithParameters(parameters));
        public IReadOnlyDictionary<string, object?> ComputeOutputs(EffectExecutionContext context) => ((IMultipleOutputEffect)Inner).ComputeOutputs(context);
        public IReadOnlyDictionary<string, object?> ComputeOutputs(TPicture? input, EffectExecutionContext context) => ((IMultipleOutputEffect)Inner).ComputeOutputs(context);
    }

    private sealed class TransportContinuousMultiple<TPicture>(IEffect effect) : TransportMultiple<TPicture>(effect), IContinuousEffect<TPicture> where TPicture : IPicture
    {
        public int StartPoint { get => ((IContinuousEffect)Inner).StartPoint; set => ((IContinuousEffect)Inner).StartPoint = value; }
        public int EndPoint { get => ((IContinuousEffect)Inner).EndPoint; set => ((IContinuousEffect)Inner).EndPoint = value; }
        public bool IsScoped { get => ((IContinuousEffect)Inner).IsScoped; set => ((IContinuousEffect)Inner).IsScoped = value; }
        public IPicture Render(TPicture source, float progress, int width, int height) => ((IContinuousEffect)Inner).Render(source, progress, width, height);
    }

    private sealed class TransportMixture<TPicture>(IEffect effect) : TypedPictureEffect(effect), IMixture<TPicture> where TPicture : IPicture
    {
        public override IEffect WithParameters(Dictionary<string, object> parameters) => CreateTransport<TPicture>(Inner.WithParameters(parameters));
        public IPicture Mix(TPicture bottom, TPicture top, IPicture.PicturePixelMode mode) => ((IMixture)Inner).Mix(bottom, top, mode);
        public IPicture Mix(TPicture bottom, TPicture top, IPicture.PicturePixelMode mode, int x, int y, int width, int height) =>
            ((IMixture)Inner).Mix(bottom, top, mode, x, y, width, height);
    }

    private sealed class TransportTransform<TPicture>(IEffect effect) : TypedPictureEffect(effect), ITransform<TPicture> where TPicture : IPicture
    {
        public TransformDefinition Definition => ((ITransform)Inner).Definition;
        public override IEffect WithParameters(Dictionary<string, object> parameters) => CreateTransport<TPicture>(Inner.WithParameters(parameters));
        public IPicture Render(TPicture left, TPicture? right, float progress, TransformSide side, int width, int height) =>
            ((ITransform)Inner).Render(left, right, progress, side, width, height);
        public IAudioSamples Render(IAudioSamples left, IAudioSamples? right, long offset, long duration, TransformSide side) =>
            ((ITransform)Inner).Render(left, right, offset, duration, side);
    }

    private sealed class TransportSource<TPicture>(IEffect effect) : TypedPictureEffect(effect), ISourceReplacementEffect<TPicture> where TPicture : IPicture
    {
        public int ProjectFrameRate { get => ((ISourceReplacementEffect)Inner).ProjectFrameRate; set => ((ISourceReplacementEffect)Inner).ProjectFrameRate = value; }
        public bool SupportsSourceReplacement(IClip input, int width, int height) => ((ISourceReplacementEffect)Inner).SupportsSourceReplacement(input, width, height);
        public override IEffect WithParameters(Dictionary<string, object> parameters) => CreateTransport<TPicture>(Inner.WithParameters(parameters));
        public IPicture Compute(IClip input, TPicture source, int width, int height, uint frame, IPicture.PicturePixelMode mode) =>
            ((ISourceReplacementEffect)Inner).Compute(input, source, width, height, frame, mode);
    }

    protected static IPicture Download(IPicture result)
    {
        if (result is not IHwAccelPicture gpu) return result;
        try { return gpu.Download(); }
        finally { gpu.Dispose(); }
    }

    private class HardwareNormal<TPicture>(IEffect effect) : TypedPictureEffect(effect), INormalEffect<TPicture>, IHwAccelPictureEffect where TPicture : IPicture
    {
        public override IEffect WithParameters(Dictionary<string, object> parameters) => Create<TPicture>(Inner.WithParameters(parameters));
        public IPicture Render(TPicture source, int width, int height) => Download(EffectPictureProcessing.Render(Inner, source, width, height));
        public IHwAccelPicture RenderHwAccel(IHwAccelPicture source, EffectExecutionContext context) => ((IHwAccelPictureEffect)Inner).RenderHwAccel(source, context);
    }

    private sealed class HardwareColor<TPicture>(IEffect effect) : HardwareNormal<TPicture>(effect), IColorAdjustEffect<TPicture> where TPicture : IPicture
    {
        public IPicture Process(TPicture source) => Render(source, source.Width, source.Height);
    }

    private sealed class HardwareContinuous<TPicture>(IEffect effect) : TypedPictureEffect(effect), IContinuousEffect<TPicture>, IHwAccelPictureEffect where TPicture : IPicture
    {
        public int StartPoint { get => ((IContinuousEffect)Inner).StartPoint; set => ((IContinuousEffect)Inner).StartPoint = value; }
        public int EndPoint { get => ((IContinuousEffect)Inner).EndPoint; set => ((IContinuousEffect)Inner).EndPoint = value; }
        public bool IsScoped { get => ((IContinuousEffect)Inner).IsScoped; set => ((IContinuousEffect)Inner).IsScoped = value; }
        public override IEffect WithParameters(Dictionary<string, object> parameters) => Create<TPicture>(Inner.WithParameters(parameters));
        public IPicture Render(TPicture source, float progress, int width, int height) => Download(EffectPictureProcessing.Render(Inner, source, width, height, progress));
        public IHwAccelPicture RenderHwAccel(IHwAccelPicture source, EffectExecutionContext context) => ((IHwAccelPictureEffect)Inner).RenderHwAccel(source, context);
    }

    private sealed class HardwareMixture<TPicture>(IEffect effect) : TypedPictureEffect(effect), IMixture<TPicture>, IHwAccelMixture where TPicture : IPicture
    {
        public override IEffect WithParameters(Dictionary<string, object> parameters) => Create<TPicture>(Inner.WithParameters(parameters));
        public IPicture Mix(TPicture bottom, TPicture top, IPicture.PicturePixelMode mode)
        {
            using var resized = top.Width != bottom.Width || top.Height != bottom.Height
                ? EffectPictureProcessing.Resize(top, bottom.Width, bottom.Height, false) : null;
            return Download(EffectPictureProcessing.Mix((IMixture)Inner, bottom, resized ?? top, mode, 0, 0, bottom.Width, bottom.Height));
        }
        public IPicture Mix(TPicture bottom, TPicture top, IPicture.PicturePixelMode mode, int x, int y, int width, int height) =>
            Download(EffectPictureProcessing.Mix((IMixture)Inner, bottom, top, mode, x, y, width, height));
        public IHwAccelPicture MixHwAccel(IHwAccelPicture bottom, IHwAccelPicture top, IPicture.PicturePixelMode mode, int x, int y, int width, int height) =>
            ((IHwAccelMixture)Inner).MixHwAccel(bottom, top, mode, x, y, width, height);
    }

    private sealed class HardwareTransform<TPicture>(IEffect effect) : TypedPictureEffect(effect), ITransform<TPicture>, IHwAccelTransform where TPicture : IPicture
    {
        public TransformDefinition Definition => ((ITransform)Inner).Definition;
        public override IEffect WithParameters(Dictionary<string, object> parameters) => Create<TPicture>(Inner.WithParameters(parameters));
        public IPicture Render(TPicture left, TPicture? right, float progress, TransformSide side, int width, int height) =>
            Download(EffectPictureProcessing.Transform((ITransform)Inner, left, right, progress, side, width, height));
        public IAudioSamples Render(IAudioSamples left, IAudioSamples? right, long offset, long duration, TransformSide side) =>
            ((ITransform)Inner).Render(left, right, offset, duration, side);
        public IHwAccelPicture RenderHwAccel(IHwAccelPicture left, IHwAccelPicture? right, float progress, TransformSide side, int width, int height) =>
            ((IHwAccelTransform)Inner).RenderHwAccel(left, right, progress, side, width, height);
    }
}
