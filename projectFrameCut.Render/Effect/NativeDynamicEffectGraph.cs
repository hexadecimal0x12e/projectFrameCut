using projectFrameCut.Drawing.Vector;
using projectFrameCut.Render.RenderAPIBase.EffectAndMixture;

namespace projectFrameCut.Render.Effect;

internal sealed class NativeDynamicEffectGraph(DynamicEffectGraph graph) : IVectorPictureEffect, IDisposable
{
    public string TypeName => "__NativeDynamicEffectGraph__";
    public string FromPlugin => graph.FromPlugin;
    public string Id { get => graph.Id; set => graph.Id = value; }
    public string Name { get => graph.Name; set => graph.Name = value; }
    public bool Enabled { get => graph.Enabled; set => graph.Enabled = value; }
    public int Index { get => graph.Index; set => graph.Index = value; }
    public int RelativeWidth { get => graph.RelativeWidth; set => graph.RelativeWidth = value; }
    public int RelativeHeight { get => graph.RelativeHeight; set => graph.RelativeHeight = value; }
    public string? BindedEffectProvidingSystemID { get; set; } = "__NativeDynamicEffectGraph__";
    public EffectImplementType ImplementType => graph.ImplementType;
    public Dictionary<string, object> Parameters => graph.Parameters;
    public bool IsReorderable => false;
    public void Initialize() => graph.Initialize();
    public IEffect WithParameters(Dictionary<string, object> parameters) => throw new NotSupportedException("Rebuild the provider graph instead.");
    public VectorPicture Process(VectorPicture source, float progress) => (VectorPicture)graph.EvaluateContent(source, null,
        (uint)Convert.ToSingle(ValueProviderFrameContext.Get(ValueProviderFrameContext.BuiltInFrameProviderId) ?? 0f),
        RelativeWidth, RelativeHeight, RelativeWidth, RelativeHeight, new(0, 0, RelativeWidth, RelativeHeight, false), clipProgress: progress).Content;
    public VectorPicture Process(VectorEffectFrameContext context, VectorPicture source) => (VectorPicture)graph.EvaluateContent(source,
        context.Clip, context.FrameIndex, context.Width, context.Height, context.Width, context.Height,
        new(0, 0, context.Width, context.Height, false), cancellationToken: ValueProviderFrameContext.CancellationToken,
        afterNode: ValueProviderFrameContext.NodeObserver,
        vectorProgress: context.Progress).Content;
    public void Dispose() => graph.Dispose();
}
