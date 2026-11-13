using projectFrameCut.Drawing.Base;
using projectFrameCut.Render.Plugin;
using projectFrameCut.Render.RenderAPIBase.ClipAndTrack;
using projectFrameCut.Render.RenderAPIBase.EffectAndMixture;
using projectFrameCut.Shared;

namespace projectFrameCut.Render.Transform;

public abstract class AudioTransformBase : TransformEffectBase
{
    public override string FromPlugin => InternalPluginBase.InternalPluginBaseID;
    public override EffectImplementType ImplementType => EffectImplementType.NotSpecified;
    public override IPicture Render(IPicture left, IPicture? right, float progress, TransformSide side, int targetWidth, int targetHeight) =>
        throw new NotSupportedException($"Transform {TypeName} only supports audio.");

    protected static IAudioSamples Blend(IAudioSamples left, IAudioSamples? right, long offset, long duration, TransformSide side)
    {
        if (right is not null && (left.SampleCount != right.SampleCount || left.SamplePerSecond != right.SamplePerSecond || left.channelCount != right.channelCount))
            throw new ArgumentException("Audio transform inputs must have matching sample counts, rates and channels.");
        var channels = new float[left.channelCount][];
        for (int c = 0; c < channels.Length; c++)
        {
            var l = Samples(left, c);
            var r = right is null ? null : Samples(right, c);
            channels[c] = new float[left.SampleCount];
            for (int i = 0; i < left.SampleCount; i++)
            {
                float p = duration <= 1 ? 1 : (float)Math.Clamp((offset + i) / (double)(duration - 1), 0, 1);
                channels[c][i] = r is null ? l[i] * (side == TransformSide.Left ? p : 1 - p) : l[i] * (1 - p) + r[i] * p;
            }
        }
        return new FloatAudioSamples { Channels = channels, SampleCount = left.SampleCount, SamplePerSecond = left.SamplePerSecond };
    }

    private static float[] Samples(IAudioSamples input, int channel) => input is IAudioSamples<float> samples
        ? samples.GetSamples(channel) : input.GetSamples(channel).Select(Convert.ToSingle).ToArray();
}

public sealed class AudioFadeTransform : AudioTransformBase
{
    public override string TypeName => "AudioFade";
    public override TransformDefinition Definition => TransformDefinition.Audio | TransformDefinition.SupportOneInput;
    public override IEffect WithParameters(Dictionary<string, object> parameters) => new AudioFadeTransform { Parameters = parameters };
    public override IAudioSamples Render(IAudioSamples left, IAudioSamples? right, long sampleOffset, long durationSamples, TransformSide side) =>
        Blend(left, null, sampleOffset, durationSamples, side);
}

public sealed class AudioCrossfadeTransform : AudioTransformBase
{
    public override string TypeName => "AudioCrossfade";
    public override TransformDefinition Definition => TransformDefinition.Audio | TransformDefinition.SupportTwoInput;
    public override IEffect WithParameters(Dictionary<string, object> parameters) => new AudioCrossfadeTransform { Parameters = parameters };
    public override IAudioSamples Render(IAudioSamples left, IAudioSamples? right, long sampleOffset, long durationSamples, TransformSide side)
    {
        ArgumentNullException.ThrowIfNull(right);
        return Blend(left, right, sampleOffset, durationSamples, side);
    }
}

public sealed class AudioFadeTransformProvider : AudioTransformProviderBase
{
    public override string TypeName => "AudioFade";
}

public sealed class AudioCrossfadeTransformProvider : AudioTransformProviderBase
{
    public override string TypeName => "AudioCrossfade";
}

public abstract class AudioTransformProviderBase : TransformEffectProviderBase
{
    public override EffectTarget Target => EffectTarget.Audio | EffectTarget.Transform | EffectTarget.IsNotVisibleInNewEffectSelector;
    protected override EffectImplementType[] SupportedImplementTypes() => [EffectImplementType.NotSpecified];
    protected override IReadOnlyList<EffectArgumentFieldDescriptor> DefineFields() => [];
    protected override EffectArgumentFieldDescriptor DefineOutField() => Field(OutputAnchorKey, EffectArgumentFieldType.Unknown, "");
}
