using projectFrameCut.Drawing.Base;
using projectFrameCut.Render.Compose;
using projectFrameCut.Render.RenderAPIBase.EffectAndMixture;

namespace projectFrameCut.Render.Effect;

internal sealed class DynamicMixtureGraph(DynamicEffectGraph graph) : IMixture, IDisposable
{
    public bool IsDeterministic => graph.IsDeterministic;
    public bool AcceptsHwAccelPictures => true;
    public string TypeName => "__DynamicMixtureGraph__";
    public string FromPlugin => graph.FromPlugin;
    public string Id { get => graph.Id; set => graph.Id = value; }
    public string Name { get => graph.Name; set => graph.Name = value; }
    public bool Enabled { get => graph.Enabled; set => graph.Enabled = value; }
    public int Index { get => graph.Index; set => graph.Index = value; }
    public int RelativeWidth { get => graph.RelativeWidth; set => graph.RelativeWidth = value; }
    public int RelativeHeight { get => graph.RelativeHeight; set => graph.RelativeHeight = value; }
    public string? BindedEffectProvidingSystemID { get; set; } = "__DynamicMixtureGraph__";
    public EffectImplementType ImplementType => graph.ImplementType;
    public Dictionary<string, object> Parameters => graph.Parameters;
    public bool IsReorderable => false;
    public IEnumerable<IEffect> NodeEffects => graph.NodeEffects;
    public void Initialize() => graph.Initialize();
    public IEffect WithParameters(Dictionary<string, object> parameters) => throw new NotSupportedException("Rebuild the provider graph instead.");
    public IPicture Mix(IPicture basePicture, IPicture topPicture, IPicture.PicturePixelMode targetPPB) =>
        Mix(basePicture, topPicture, targetPPB, 0, 0, basePicture.Width, basePicture.Height);

    public IPicture Mix(IPicture basePicture, IPicture topPicture, IPicture.PicturePixelMode targetPPB,
        int topStartX, int topStartY, int targetWidth, int targetHeight)
    {
        var result = graph.EvaluateContent(topPicture, ValueProviderFrameContext.CurrentClip,
            (uint)Convert.ToSingle(ValueProviderFrameContext.Get(ValueProviderFrameContext.BuiltInFrameProviderId) ?? 0f),
            targetWidth, targetHeight, targetWidth, targetHeight,
            new(topStartX, topStartY, topPicture.Width, topPicture.Height, false),
            cancellationToken: ValueProviderFrameContext.CancellationToken,
            afterNode: ValueProviderFrameContext.NodeObserver, underPicture: basePicture, mixturePixelMode: targetPPB);
        var picture = (IPicture)result.Content;
        if (ReferenceEquals(picture, topPicture) || ReferenceEquals(picture, basePicture)) picture = DynamicEffectGraph.CopyPicture(picture);
        if (result.Position.TargetX == 0 && result.Position.TargetY == 0 && picture.Width == targetWidth && picture.Height == targetHeight)
            return picture;
        try
        {
            using var blank = Picture16bpp.GenerateSolidColor(targetWidth, targetHeight, 0, 0, 0, 0);
            return EffectPictureProcessing.Mix(ClassicOverlayMixture.Default, blank, picture, targetPPB,
                result.Position.TargetX, result.Position.TargetY, targetWidth, targetHeight);
        }
        finally { picture.Dispose(); }
    }

    public void Dispose() => graph.Dispose();
}
