using projectFrameCut.Render.Effect;
using projectFrameCut.Drawing.Base;
using projectFrameCut.Drawing.Processing.Resizing;
using projectFrameCut.Render.Plugin;
using projectFrameCut.Render.RenderAPIBase.ClipAndTrack;
using projectFrameCut.Render.RenderAPIBase.EffectAndMixture;
using projectFrameCut.Render.RenderAPIBase.Sources;
using projectFrameCut.Shared;

namespace projectFrameCut.Render.Transform;

public class ExternalSourceTransform : TransformEffectBase, IDisposable
{
    private readonly Lock sync = new();
    private IVideoSource? source;
    private string? sourcePath;
    private bool disposed;
    public override string FromPlugin => InternalPluginBase.InternalPluginBaseID;
    public override string TypeName => "ExternalSourceTransform";
    public override TransformDefinition Definition => TransformDefinition.Clip | TransformDefinition.SupportTwoInput;
    public IEffect WithPictureType<TPicture>() where TPicture : IPicture => new ExternalSourceTransform<TPicture>();
    public override IEffect WithParameters(Dictionary<string, object> parameters) => new ExternalSourceTransform { Parameters = parameters };

    public override IPicture Render(IPicture left, IPicture? right, float progress, TransformSide side, int targetWidth, int targetHeight) => RenderSource<IPicture>(progress, targetWidth, targetHeight);

    protected IPicture RenderSource<TPicture>(float progress, int targetWidth, int targetHeight) where TPicture : IPicture
    {
        using var scope = sync.EnterScope();
        ObjectDisposedException.ThrowIf(disposed, this);
        string path = DynamicParam.Resolve(Parameters.GetValueOrDefault("SourcePath"), "");
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        if (source is null || sourcePath != path)
        {
            source?.Dispose();
            source = null;
            sourcePath = null;
            var next = PluginManager.CreateVideoSource(path);
            try { next.Initialize(); }
            catch { next.Dispose(); throw; }
            source = next;
            sourcePath = path;
            Logger.Log($"Initialized external transform source {path}.", nameof(ExternalSourceTransform));
        }
        using var frame = source.GetFrame((uint)(Math.Clamp(progress, 0, 1) * Math.Max(0, source.TotalFrames - 1)));
        using var input = new PictureInput<TPicture>(frame);
        var result = PictureCalculation<TPicture>.Resize(input.Picture, targetWidth, targetHeight, false);
        return ReferenceEquals(result, input.Picture) ? result.Clone() : result;
    }

    public void Dispose()
    {
        using var scope = sync.EnterScope();
        if (disposed) return;
        disposed = true;
        source?.Dispose();
        source = null;
        sourcePath = null;
    }
}

public class ExternalSourceTransform<TPicture> : ExternalSourceTransform, ITransform<TPicture> where TPicture : IPicture
{
    public override IEffect WithParameters(Dictionary<string, object> parameters) => new ExternalSourceTransform<TPicture> { Parameters = parameters };
    public override IPicture Render(IPicture left, IPicture? right, float progress, TransformSide side, int width, int height) =>
        RenderSource<TPicture>(progress, width, height);
    public IPicture Render(TPicture left, TPicture? right, float progress, TransformSide side, int width, int height) =>
        RenderSource<TPicture>(progress, width, height);
}

public sealed class ExternalSourceTransformProvider : TransformEffectProviderBase
{
    public override string TypeName => "ExternalSourceTransform";
    public override object? ResolveDataFormat(string fieldId, bool isInput, EffectFieldTypeContext context) => null;
    protected override IReadOnlyList<EffectArgumentFieldDescriptor> DefineFields() =>
        [Field("SourcePath", EffectArgumentFieldType.String, "")];
    protected override EffectImplementType[] SupportedImplementTypes() => [EffectImplementType.IPicture];
}
