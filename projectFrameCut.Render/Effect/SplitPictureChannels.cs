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
    private static readonly string[] ChannelIds = ["R", "G", "B", "A", "Brightness"];

    public SplitPictureChannelsEffectProvider() => Name = "Split Picture Channels";

    public override string TypeName => "SplitPictureChannels";
    public override string FromPlugin => InternalPluginBase.InternalPluginBaseID;
    public override EffectType TypeOfEffect => EffectType.NormalEffect;
    public override EffectTarget Target => EffectTarget.Video;

    public IReadOnlyDictionary<string, EffectArgumentFieldDescriptor> OutFields => ChannelIds.ToDictionary(
        id => id,
        id => Field(id, EffectArgumentFieldType.IPicture, "", remarks: $"Isolated {id} channel."));

    protected override IReadOnlyList<EffectArgumentFieldDescriptor> DefineFields() => [];

    public new IEffect[] Build() => [new SplitPictureChannelsEffect()];
    IEffect[] IEffectProvider.Build() => Build();
}

public sealed class SplitPictureChannelsEffect : IEffect, IMultipleOutputEffect
{
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
    public IEffect WithParameters(Dictionary<string, object> parameters) => new SplitPictureChannelsEffect();

    public IReadOnlyDictionary<string, object?> ComputeOutputs(EffectExecutionContext context)
    {
        if (context.Input is not IPicture input) throw new ArgumentException("The primary input must be an IPicture.");
        if (input is IHDRPicture<ushort> hdr) return Split(hdr, hdr);

        return input switch
        {
            IPicture<ushort> p => Split(p, null),
            IPicture<byte> p => Split(p),
            _ => throw new NotSupportedException($"Unsupported picture type: {input.GetType().FullName}")
        };
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
                    if (output is HDRPicture16bpp a) Array.Fill(a.Brightness, 1f);
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
        output.a = new float[source.Pixels];
        for (int i = 0; i < source.Pixels; i++) output.a[i] = Alpha(source, i);
        output.HasAlphaChannel = true;
    }

    private static HDRPicture16bpp BrightnessOutput<T>(IPicture<T> source, double maximum, IHDRPicture<ushort>? hdr = null)
    {
        var output = new HDRPicture16bpp(source.Width, source.Height)
        {
            MaximumBrightness = hdr?.MaximumBrightness ?? 203f
        };
        Array.Fill(output.r, ushort.MaxValue);
        Array.Fill(output.g, ushort.MaxValue);
        Array.Fill(output.b, ushort.MaxValue);
        if (hdr is not null && hdr.Brightness.Length == source.Pixels)
            Array.Copy(hdr.Brightness, output.Brightness, source.Pixels);
        else
        {
            for (int i = 0; i < source.Pixels; i++)
                output.Brightness[i] = (float)Math.Clamp((0.2627 * Convert.ToDouble(source.r[i])
                    + 0.6780 * Convert.ToDouble(source.g[i]) + 0.0593 * Convert.ToDouble(source.b[i])) / maximum, 0, 1);
        }
        return output;
    }

    private static float Alpha<T>(IPicture<T> picture, int index) =>
        picture.HasAlphaChannel && picture.a is { } alpha && index < alpha.Length && float.IsFinite(alpha[index])
            ? Math.Clamp(alpha[index], 0f, 1f) : 1f;
}
