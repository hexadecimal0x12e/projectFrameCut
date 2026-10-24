using projectFrameCut.Drawing.Base;
using projectFrameCut.Drawing.Base.Picture;
using projectFrameCut.Render.Plugin;
using projectFrameCut.Render.RenderAPIBase.EffectAndMixture;
using projectFrameCut.Shared;

namespace projectFrameCut.Render.Effect;

public sealed class OscilloscopeEffectProvider : EffectProviderBase
{
    public OscilloscopeEffectProvider()
    {
        Name = "Oscilloscope";
        AnchorsBindingState["AudioSample"] = ValueProviderFrameContext.BuiltInAudioSampleProviderId;
    }

    public override string TypeName => "Oscilloscope";
    public override string FromPlugin => InternalPluginBase.InternalPluginBaseID;
    public override EffectType TypeOfEffect => EffectType.NormalEffect;
    public override EffectTarget Target => EffectTarget.Video;
    public override IReadOnlyList<Stage> SupportedStages => [Stage.Picture];
    public override bool IsDeterministic => true;

    protected override IReadOnlyList<EffectArgumentFieldDescriptor> DefineFields() =>
    [
        Field("AudioSample", EffectArgumentFieldType.AudioSample | EffectArgumentFieldType.CanNotBeStatic | EffectArgumentFieldType.Mandatory, ""),
        Field("Color", EffectArgumentFieldType.Color, "#00FF00"),
        Field("Gain", EffectArgumentFieldType.Numeric, "1", min: "0"),
        Field("LineWidth", EffectArgumentFieldType.Integer, "2", min: "1"),
    ];

    protected override EffectImplementType[] SupportedImplementTypes() => [EffectImplementType.IPicture];
}

public sealed class OscilloscopeEffect : INormalEffect
{
    public string FromPlugin => InternalPluginBase.InternalPluginBaseID;
    public string TypeName => "Oscilloscope";
    public string Name { get; set; } = "Oscilloscope";
    public string Id { get; set; } = Guid.NewGuid().ToString();
    public string? BindedEffectProvidingSystemID { get; set; }
    public EffectImplementType ImplementType => EffectImplementType.IPicture;
    public bool IsDeterministic => true;
    public bool Enabled { get; set; } = true;
    public bool IsReorderable => true;
    public int Index { get; set; }
    public int RelativeWidth { get; set; }
    public int RelativeHeight { get; set; }
    public Dictionary<string, object> Parameters { get; set; } = new();

    public IEffect WithParameters(Dictionary<string, object> parameters) => new OscilloscopeEffect { Parameters = parameters };

    public IPicture Render(IPicture source, int targetWidth, int targetHeight)
    {
        var audio = DynamicParam.Resolve<IAudioSamples<float>?>(Parameters.GetValueOrDefault("AudioSample"), null)
            ?? throw new InvalidOperationException("Oscilloscope requires an AudioSample input.");
        var color = EffectFieldTypes.ConvertInput(EffectArgumentFieldType.Color,
            DynamicParam.Resolve<object>(Parameters.GetValueOrDefault("Color"), "#00FF00")) switch
        {
            PictureExtensions.Pixel<ushort> c => c,
            PictureExtensions.Pixel<byte> c => new PictureExtensions.Pixel<ushort> { r = (ushort)(c.r * 257), g = (ushort)(c.g * 257), b = (ushort)(c.b * 257), a = c.a },
            _ => throw new ArgumentException("Invalid waveform color."),
        };
        float gain = DynamicParam.Resolve(Parameters.GetValueOrDefault("Gain"), 1f);
        if (!float.IsFinite(gain) || gain < 0) throw new ArgumentOutOfRangeException("Gain");
        int width = Math.Max(1, targetWidth), height = Math.Max(1, targetHeight);
        int lineWidth = Math.Clamp(DynamicParam.Resolve(Parameters.GetValueOrDefault("LineWidth"), 2), 1, Math.Max(width, height));
        IPicture output = source switch
        {
            IHDRPicture<ushort> hdr => new HDRPicture16bpp(width, height) { MaximumBrightness = hdr.MaximumBrightness },
            IPicture<ushort> => new Picture16bpp(width, height),
            IPicture<byte> => new Picture8bpp(width, height),
            _ => throw new NotSupportedException($"Unsupported waveform picture: {source.GetType().Name}."),
        };
        try
        {
            if (output is IPicture<byte> p8)
                Draw(p8, audio, gain, lineWidth, (byte)(color.r / 257), (byte)(color.g / 257), (byte)(color.b / 257), color.a);
            else Draw((IPicture<ushort>)output, audio, gain, lineWidth, color.r, color.g, color.b, color.a);
            return output;
        }
        catch
        {
            output.Dispose();
            throw;
        }
    }

    private static void Draw<T>(IPicture<T> picture, IAudioSamples<float> audio, float gain, int lineWidth, T red, T green, T blue, float alpha)
    {
        picture.HasAlphaChannel = true;
        picture.a = new float[picture.Pixels];
        var r = picture.r;
        var g = picture.g;
        var b = picture.b;
        var a = picture.a;
        var hdr = picture as IHDRPicture<ushort>;
        for (int channel = 0; channel < Math.Min(2, audio.channelCount); channel++)
        {
            var samples = audio.GetSamples(channel);
            int count = Math.Min(audio.SampleCount, samples.Length);
            int top = channel * picture.Height / Math.Min(2, audio.channelCount);
            int bottom = (channel + 1) * picture.Height / Math.Min(2, audio.channelCount) - 1;
            if (bottom < top) continue;
            float center = (top + bottom) * 0.5f, amplitude = (bottom - top) * 0.5f;
            int previous = (int)Math.Round(center);
            for (int x = 0; x < picture.Width; x++)
            {
                ValueProviderFrameContext.CancellationToken.ThrowIfCancellationRequested();
                float min = 0, max = 0;
                int first = (int)((long)x * count / picture.Width);
                int end = Math.Min(count, Math.Max(first + 1, (int)((long)(x + 1) * count / picture.Width)));
                if (first < count)
                {
                    min = max = float.IsFinite(samples[first]) ? samples[first] : 0;
                    for (int i = first + 1; i < end; i++)
                    {
                        float value = float.IsFinite(samples[i]) ? samples[i] : 0;
                        min = Math.Min(min, value);
                        max = Math.Max(max, value);
                    }
                }
                int y0 = (int)Math.Round(center - Math.Clamp(max * gain, -1, 1) * amplitude);
                int y1 = (int)Math.Round(center - Math.Clamp(min * gain, -1, 1) * amplitude);
                int current = y1;
                if (count <= picture.Width && x > 0)
                {
                    y0 = Math.Min(y0, previous);
                    y1 = Math.Max(y1, previous);
                }
                previous = current;
                for (int dx = -(lineWidth / 2); dx < lineWidth - lineWidth / 2; dx++)
                {
                    int px = x + dx;
                    if (px < 0 || px >= picture.Width) continue;
                    for (int y = Math.Max(top, y0 - lineWidth / 2); y <= Math.Min(bottom, y1 + (lineWidth - 1) / 2); y++)
                    {
                        int index = y * picture.Width + px;
                        r[index] = red;
                        g[index] = green;
                        b[index] = blue;
                        a[index] = Math.Clamp(alpha, 0, 1);
                        if (hdr is not null) hdr.Brightness[index] = 203f / Math.Max(1, hdr.MaximumBrightness);
                    }
                }
            }
        }
    }
}
