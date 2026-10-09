using projectFrameCut.Drawing.Base;
using projectFrameCut.Render.Plugin;
using projectFrameCut.Render.RenderAPIBase.Context;
using projectFrameCut.Render.RenderAPIBase.EffectAndMixture;
using System.Numerics;

namespace projectFrameCut.Render.Effect;

public sealed class PictureChannelAverageEffectProvider : EffectProviderBase, IMultipleOutputEffectProvider
{
    public PictureChannelAverageEffectProvider() => Name = "Picture Channel Average";

    public override string TypeName => "PictureChannelAverage";
    public override string FromPlugin => InternalPluginBase.InternalPluginBaseID;
    public override EffectType TypeOfEffect => EffectType.NormalEffect;
    public override EffectTarget Target => EffectTarget.Video | EffectTarget.ValueProvider;

    public IReadOnlyDictionary<string, EffectArgumentFieldDescriptor> OutFields => new Dictionary<string, EffectArgumentFieldDescriptor>
    {
        ["R"] = Field("R", EffectArgumentFieldType.Numeric, "0", remarks: "Mean red channel value in the input's original range."),
        ["G"] = Field("G", EffectArgumentFieldType.Numeric, "0", remarks: "Mean green channel value in the input's original range."),
        ["B"] = Field("B", EffectArgumentFieldType.Numeric, "0", remarks: "Mean blue channel value in the input's original range."),
        ["A"] = Field("A", EffectArgumentFieldType.Numeric, "1", remarks: "Mean alpha; 1 when the input has no alpha channel."),
        ["Brightness"] = Field("Brightness", EffectArgumentFieldType.Numeric, "1", remarks: "Mean HDR brightness; 1 for SDR input."),
        ["RInt"] = Field("RInt", EffectArgumentFieldType.Integer, "0", remarks: "Mean red channel value rounded to the nearest integer, with midpoint rounding away from zero."),
        ["GInt"] = Field("GInt", EffectArgumentFieldType.Integer, "0", remarks: "Mean green channel value rounded to the nearest integer, with midpoint rounding away from zero."),
        ["BInt"] = Field("BInt", EffectArgumentFieldType.Integer, "0", remarks: "Mean blue channel value rounded to the nearest integer, with midpoint rounding away from zero."),
    };

    protected override IReadOnlyList<EffectArgumentFieldDescriptor> DefineFields() => [];
    protected override EffectImplementType[] SupportedImplementTypes() => [EffectImplementType.IPicture];
}

public sealed class PictureChannelAverageEffect : IEffect, IMultipleOutputEffect
{
    public string FromPlugin => InternalPluginBase.InternalPluginBaseID;
    public string TypeName => "PictureChannelAverage";
    public EffectType TypeOfEffect => EffectType.NormalEffect;
    public EffectImplementType ImplementType => EffectImplementType.IPicture;
    public string Name { get; set; } = "Picture Channel Average";
    public string Id { get; set; } = Guid.NewGuid().ToString();
    public Dictionary<string, object> Parameters { get; } = new();
    public bool Enabled { get; set; } = true;
    public int Index { get; set; }
    public bool IsReorderable => true;
    public int RelativeWidth { get; set; }
    public int RelativeHeight { get; set; }
    public string? BindedEffectProvidingSystemID { get; set; }
    public IEffect WithParameters(Dictionary<string, object> parameters) => new PictureChannelAverageEffect();

    public IReadOnlyDictionary<string, object?> ComputeOutputs(EffectExecutionContext context)
    {
        context.CancellationToken.ThrowIfCancellationRequested();
        if (context.Input is not IPicture input)
            throw new ArgumentException("The primary input must be an IPicture.");

        return input switch
        {
            IPicture<byte> p => Average(p, context.CancellationToken),
            IPicture<ushort> p => Average(p, context.CancellationToken),
            _ => throw new NotSupportedException($"Unsupported picture type: {input.GetType().FullName}")
        };
    }

    private IReadOnlyDictionary<string, object?> Average<T>(IPicture<T> input, CancellationToken cancellationToken) where T : INumber<T>
    {
        double r = 0, g = 0, b = 0, a = 0, brightness = 0;
        var alpha = input.HasAlphaChannel ? input.a : null;
        var hdr = input as IHDRPicture<T>;
        for (int i = 0; i < input.Pixels; i++)
        {
            if ((i & 4095) == 0) cancellationToken.ThrowIfCancellationRequested();
            r += double.CreateChecked(input.r[i]);
            g += double.CreateChecked(input.g[i]);
            b += double.CreateChecked(input.b[i]);
            a += alpha is null ? 1 : alpha[i];
            brightness += hdr is null ? 1 : hdr.Brightness[i];
        }

        if (input.Pixels > 0)
        {
            r /= input.Pixels;
            g /= input.Pixels;
            b /= input.Pixels;
            a /= input.Pixels;
            brightness /= input.Pixels;
        }

        Log($"PictureChannelAverage {Id}/{Name}: {input.Width}x{input.Height}, R={r}, G={g}, B={b}, A={a}, Brightness={brightness}");
        return new Dictionary<string, object?>
        {
            ["R"] = (float)r,
            ["G"] = (float)g,
            ["B"] = (float)b,
            ["A"] = (float)a,
            ["Brightness"] = (float)brightness,
            ["RInt"] = (int)Math.Round(r, MidpointRounding.AwayFromZero),
            ["GInt"] = (int)Math.Round(g, MidpointRounding.AwayFromZero),
            ["BInt"] = (int)Math.Round(b, MidpointRounding.AwayFromZero),
        };
    }
}
