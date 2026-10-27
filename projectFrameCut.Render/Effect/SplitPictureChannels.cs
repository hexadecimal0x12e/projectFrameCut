using projectFrameCut.Drawing.Base;
using projectFrameCut.Drawing.Base.Picture;
using projectFrameCut.Render.Plugin;
using projectFrameCut.Render.RenderAPIBase.Context;
using projectFrameCut.Render.RenderAPIBase.EffectAndMixture;
using System;
using System.Collections.Generic;
using System.Linq;

namespace projectFrameCut.Render.Effect;

public sealed class SplitPictureChannelsEffectProvider : EffectProviderBase, IMultipleOutputEffectProvider
{
    public override IReadOnlyList<Stage> SupportedStages => [Stage.Picture];

    public override bool IsDeterministic => true;

    private static readonly string[] ChannelIds = ["R", "G", "B", "A", "Brightness"];

    public SplitPictureChannelsEffectProvider() => Name = "Split Picture Channels";

    public override string TypeName => "SplitPictureChannels";
    public override string FromPlugin => InternalPluginBase.InternalPluginBaseID;
    public override EffectType TypeOfEffect => EffectType.NormalEffect;
    public override EffectTarget Target => EffectTarget.Video;

    public IReadOnlyDictionary<string, EffectArgumentFieldDescriptor> OutFields => ChannelIds.ToDictionary(
        id => id,
        id => Field(id, EffectArgumentFieldType.IPicture, "", remarks: $"Isolated {id} channel."));

    public override object? ResolveDataFormat(string fieldId, bool isInput, EffectFieldTypeContext context)
    {
        if (isInput) return base.ResolveDataFormat(fieldId, true, context);
        if (fieldId == "Brightness") return PictureDataFormat.CpuHdrFP16;
        if (base.ResolveDataFormat(PrimaryInputAnchorKey, true, context) is not PictureDataFormat input
            || PictureDataFormats.GetPictureType(input) is null) return null;
        if (input.HasFlag(PictureDataFormat.Float16)) return PictureDataFormat.CpuFP16;
        return input.HasFlag(PictureDataFormat.Hdr) && fieldId == "A" ? PictureDataFormat.CpuHdr16
            : input.HasFlag(PictureDataFormat.Bit16) ? PictureDataFormat.Cpu16 : PictureDataFormat.Cpu8;
    }

    protected override IReadOnlyList<EffectArgumentFieldDescriptor> DefineFields() => [];

    public new IEffect[] Build() => [new SplitPictureChannelsEffect()];
    public override IEffect[] Build<TPicture>() => [new SplitPictureChannelsEffect<TPicture>()];
    IEffect[] IEffectProvider.Build() => Build();
}

public class SplitPictureChannelsEffect : SplitPictureChannelsEffect<IPicture> { }

public class SplitPictureChannelsEffect<TPicture> : IMultipleOutputEffect<TPicture> where TPicture : IPicture
{
    public IEffect WithPictureType<T>() where T : IPicture => new SplitPictureChannelsEffect<T>();

    public bool IsDeterministic => true;

    public string FromPlugin => InternalPluginBase.InternalPluginBaseID;
    public string TypeName => "SplitPictureChannels";
    public EffectType TypeOfEffect => EffectType.NormalEffect;
    public EffectImplementType ImplementType => EffectImplementType.NotSpecified;
    public string Name { get; set; } = "Split Picture Channels";
    public string Id { get; set; } = Guid.NewGuid().ToString();
    public Dictionary<string, object> Parameters { get; } = new();
    public bool Enabled { get; set; } = true;
    public int Index { get; set; }
    public bool IsReorderable => true;
    public int RelativeWidth { get; set; }
    public int RelativeHeight { get; set; }
    public string? BindedEffectProvidingSystemID { get; set; }
    public IEffect WithParameters(Dictionary<string, object> parameters) => new SplitPictureChannelsEffect<TPicture>();

    public IReadOnlyDictionary<string, object?> ComputeOutputs(EffectExecutionContext context) =>
        ComputeOutputs(context.Input is null ? default : (TPicture)context.Input, context);

    public IReadOnlyDictionary<string, object?> ComputeOutputs(TPicture? source, EffectExecutionContext context)
    {
        if (source is not IPicture input) throw new ArgumentException("The primary input must be an IPicture.");
        if (input is IPicture<Half> fp) return SplitFP16(fp);
        if (PictureCalculation<TPicture>.IsHdr) return Split((IPicture<ushort>)(object)input, (IHDRPicture<ushort>)(object)input);
        if (PictureCalculation<TPicture>.Is16) return Split((IPicture<ushort>)(object)input, input as IHDRPicture<ushort>);
        if (PictureCalculation<TPicture>.Is8) return Split((IPicture<byte>)(object)input);
        if (input is IHDRPicture<ushort> hdr) return Split(hdr, hdr);

        return input switch
        {
            IPicture<ushort> p => Split(p, null),
            IPicture<byte> p => Split(p),
            _ => throw new NotSupportedException($"Unsupported picture type: {input.GetType().FullName}")
        };
    }

    private static IReadOnlyDictionary<string, object?> SplitFP16(IPicture<Half> source)
    {
        var result = new Dictionary<string, object?>();
        foreach (string name in new[] { "R", "G", "B", "A", "Brightness" })
        {
            PictureFP16 p = name == "Brightness" ? new HDRPictureFP16(source.Width, source.Height) { MaximumBrightness = (source as IHDRPicture<Half>)?.MaximumBrightness ?? (Half)203 }
                : new PictureFP16(source.Width, source.Height);
            if (name == "A") p.SetAlpha(true);
            using (var e = p.Edit())
            {
                for (int i = 0; i < source.Pixels; i++)
                {
                    if (name == "R") e.Red[i] = source.r[i];
                    else if (name == "G") e.Green[i] = source.g[i];
                    else if (name == "B") e.Blue[i] = source.b[i];
                    else
                    {
                        e.Red[i] = e.Green[i] = e.Blue[i] = Half.One;
                        if (name == "A") e.Alpha[i] = Alpha(source, i);
                        else e.Brightness[i] = source is IHDRPicture<Half> h ? h.Brightness[i]
                            : PictureFP16.Unit((float)source.r[i] * .2627f + (float)source.g[i] * .678f + (float)source.b[i] * .0593f);
                    }
                }
            }
            result[name] = p;
        }
        return result;
    }

    private static IReadOnlyDictionary<string, object?> Split(IPicture<byte> source)
    {
        var result = new Dictionary<string, object?>();
        foreach (string channel in new[] { "R", "G", "B", "A" })
        {
            var output = new Picture8bpp(source.Width, source.Height);
            switch (channel)
            {
                case "R":
                    Array.Copy(source.r, output.r, source.Pixels);
                    break;
                case "G":
                    Array.Copy(source.g, output.g, source.Pixels);
                    break;
                case "B":
                    Array.Copy(source.b, output.b, source.Pixels);
                    break;
                case "A":
                    FillAlpha(source, output, byte.MaxValue);
                    break;
            }
            result[channel] = output;
        }
        result["Brightness"] = BrightnessOutput(source, byte.MaxValue);
        return result;
    }

    private static IReadOnlyDictionary<string, object?> Split(IPicture<ushort> source, IHDRPicture<ushort>? hdr)
    {
        var result = new Dictionary<string, object?>();
        foreach (string channel in new[] { "R", "G", "B", "A" })
        {
            Picture16bpp output = channel == "A" && hdr is not null
                ? new HDRPicture16bpp(source.Width, source.Height) { MaximumBrightness = hdr.MaximumBrightness }
                : new Picture16bpp(source.Width, source.Height);
            switch (channel)
            {
                case "R":
                    Array.Copy(source.r, output.r, source.Pixels);
                    break;
                case "G":
                    Array.Copy(source.g, output.g, source.Pixels);
                    break;
                case "B":
                    Array.Copy(source.b, output.b, source.Pixels);
                    break;
                case "A":
                    FillAlpha(hdr ?? source, output, ushort.MaxValue);
                    if (output is HDRPicture16bpp a) Array.Fill(a.Brightness, Half.One);
                    break;
            }
            result[channel] = output;
        }
        result["Brightness"] = BrightnessOutput(source, ushort.MaxValue, hdr);
        return result;
    }

    private static void FillAlpha<T>(IPicture<T> source, IPicture<T> output, T maximum)
    {
        Array.Fill(output.r, maximum);
        Array.Fill(output.g, maximum);
        Array.Fill(output.b, maximum);
        output.a = new Half[source.Pixels];
        for (int i = 0; i < source.Pixels; i++) output.a[i] = Alpha(source, i);
        output.HasAlphaChannel = true;
    }

    private static HDRPicture16bpp BrightnessOutput<T>(IPicture<T> source, double maximum, IHDRPicture<ushort>? hdr = null)
    {
        var output = new HDRPicture16bpp(source.Width, source.Height)
        {
            MaximumBrightness = hdr?.MaximumBrightness ?? (Half)203
        };
        Array.Fill(output.r, ushort.MaxValue);
        Array.Fill(output.g, ushort.MaxValue);
        Array.Fill(output.b, ushort.MaxValue);
        if (hdr is not null && hdr.Brightness.Length == source.Pixels)
            Array.Copy(hdr.Brightness, output.Brightness, source.Pixels);
        else
        {
            for (int i = 0; i < source.Pixels; i++)
                output.Brightness[i] = (Half)Math.Clamp((0.2627 * Convert.ToDouble(source.r[i])
                    + 0.6780 * Convert.ToDouble(source.g[i]) + 0.0593 * Convert.ToDouble(source.b[i])) / maximum, 0, 1);
        }
        return output;
    }

    private static Half Alpha<T>(IPicture<T> picture, int index) =>
        picture.HasAlphaChannel && picture.a is { } alpha && index < alpha.Length && Half.IsFinite(alpha[index])
            ? Half.Clamp(alpha[index], Half.Zero, Half.One) : Half.One;
}
