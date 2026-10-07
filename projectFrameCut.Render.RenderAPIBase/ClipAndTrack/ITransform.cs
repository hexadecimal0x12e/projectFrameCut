using projectFrameCut.Drawing.Base;
using projectFrameCut.Render.RenderAPIBase.EffectAndMixture;
using projectFrameCut.Shared;

namespace projectFrameCut.Render.RenderAPIBase.ClipAndTrack;

public enum TransformSide { Left, Right }
public enum TransformInputMode { OneInput, TwoInput }
public enum TransformRenderOrder { AfterEffects, BeforeEffects }

public interface ITransform : IEffect
{
    EffectType IEffect.TypeOfEffect => EffectType.Transform;
    bool IEffect.IsReorderable => false;
    TransformDefinition Definition { get; }
    IPicture Render(IPicture left, IPicture? right, float progress, TransformSide side, int targetWidth, int targetHeight);
    /// <summary>Processes a sample window at an offset from the start of the complete transition.</summary>
    IAudioSamples Render(IAudioSamples left, IAudioSamples? right, long sampleOffset, long durationSamples, TransformSide side) =>
        throw new NotSupportedException($"Transform {TypeName} does not support audio.");
}

public abstract class TransformEffectBase : ITransform
{
    public abstract string FromPlugin { get; }
    public abstract string TypeName { get; }
    public abstract TransformDefinition Definition { get; }
    public virtual EffectImplementType ImplementType => EffectImplementType.IPicture;
    public string Name { get; set; } = "Transform";
    public string Id { get; set; } = string.Empty;
    public bool Enabled { get; set; } = true;
    public int Index { get; set; }
    public int RelativeWidth { get; set; }
    public int RelativeHeight { get; set; }
    public string? BindedEffectProvidingSystemID { get; set; }
    public Dictionary<string, object> Parameters { get; set; } = new();
    public abstract IEffect WithParameters(Dictionary<string, object> parameters);
    public abstract IPicture Render(IPicture left, IPicture? right, float progress, TransformSide side, int targetWidth, int targetHeight);
    public virtual IAudioSamples Render(IAudioSamples left, IAudioSamples? right, long sampleOffset, long durationSamples, TransformSide side) =>
        throw new NotSupportedException($"Transform {TypeName} does not support audio.");
}
