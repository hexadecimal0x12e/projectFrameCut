using projectFrameCut.Drawing.Base;
using projectFrameCut.Render.Plugin;
using projectFrameCut.Render.RenderAPIBase.Context;
using projectFrameCut.Render.RenderAPIBase.EffectAndMixture;
using System.Numerics;

namespace projectFrameCut.Render.Effect;

public sealed class PictureChannelAverageEffectProvider : EffectProviderBase, IMultipleOutputEffectProvider
{
    public override IReadOnlyList<Stage> SupportedStages => [Stage.Picture];

    public override bool IsDeterministic => true;

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

public class PictureChannelAverageEffect : PictureChannelAverageEffect<IPicture> { }

public class PictureChannelAverageEffect<TPicture> : IMultipleOutputEffect<TPicture> where TPicture : IPicture
{
    public IEffect WithPictureType<T>() where T : IPicture => new PictureChannelAverageEffect<T>();

    public bool IsDeterministic => true;

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
    public IEffect WithParameters(Dictionary<string, object> parameters) => new PictureChannelAverageEffect<TPicture>();

    public IReadOnlyDictionary<string, object?> ComputeOutputs(EffectExecutionContext context) =>
        ComputeOutputs(context.Input is null ? default : (TPicture)context.Input, context);

    public IReadOnlyDictionary<string, object?> ComputeOutputs(TPicture? source, EffectExecutionContext context)
    {
        context.CancellationToken.ThrowIfCancellationRequested();
        if (source is not IPicture input)
            throw new ArgumentException("The primary input must be an IPicture.");

        if (input is IPicture<Half> fp) return Average(fp, context.CancellationToken);
        if (PictureCalculation<TPicture>.Is8) return Average((IPicture<byte>)(object)input, context.CancellationToken);
        if (PictureCalculation<TPicture>.Is16) return Average((IPicture<ushort>)(object)input, context.CancellationToken);
        return input switch
        {
            IPicture<byte> p => Average(p, context.CancellationToken),
            IPicture<ushort> p => Average(p, context.CancellationToken),
            _ => throw new NotSupportedException($"Unsupported picture type: {input.GetType().FullName}")
        };
    }

    private IReadOnlyDictionary<string, object?> Average<T>(IPicture<T> input, CancellationToken cancellationToken) where T : INumber<T>
    {
        double r = 0, g = 0, b = 0;
        var alpha = input.HasAlphaChannel ? input.a : null;
        var hdr = input as IHDRPicture<T>;
        for (int i = 0; i < input.Pixels; i++)
        {
            if ((i & 4095) == 0) cancellationToken.ThrowIfCancellationRequested();
            r += double.CreateChecked(input.r[i]);
            g += double.CreateChecked(input.g[i]);
            b += double.CreateChecked(input.b[i]);
        }

        if (input.Pixels > 0)
        {
            r /= input.Pixels;
            g /= input.Pixels;
            b /= input.Pixels;
        }

        Half a = input.Pixels == 0 ? Half.Zero : alpha is null ? Half.One : HalfChannelMath.Average(alpha, cancellationToken);
        Half brightness = input.Pixels == 0 ? Half.Zero : hdr is null ? Half.One : HalfChannelMath.Average(hdr.Brightness, cancellationToken);
        Log($"PictureChannelAverage {Id}/{Name}: {input.Width}x{input.Height}, R={r}, G={g}, B={b}, A={a}, Brightness={brightness}");
        return new Dictionary<string, object?>
        {
            ["R"] = (float)r,
            ["G"] = (float)g,
            ["B"] = (float)b,
            ["A"] = a,
            ["Brightness"] = brightness,
            ["RInt"] = (int)Math.Round(r * (typeof(T) == typeof(Half) ? 65535 : 1), MidpointRounding.AwayFromZero),
            ["GInt"] = (int)Math.Round(g * (typeof(T) == typeof(Half) ? 65535 : 1), MidpointRounding.AwayFromZero),
            ["BInt"] = (int)Math.Round(b * (typeof(T) == typeof(Half) ? 65535 : 1), MidpointRounding.AwayFromZero),
        };
    }
}
