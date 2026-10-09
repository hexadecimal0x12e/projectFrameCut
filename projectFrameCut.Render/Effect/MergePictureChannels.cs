using projectFrameCut.Drawing.Base;
using projectFrameCut.Drawing.Base.Picture;
using projectFrameCut.Render.Plugin;
using projectFrameCut.Render.RenderAPIBase.EffectAndMixture;
using projectFrameCut.Shared;

namespace projectFrameCut.Render.Effect;

public sealed class MergePictureChannelsEffectProvider : EffectProviderBase, IMultipleOutputEffectProvider
{
    public MergePictureChannelsEffectProvider() => Name = "Merge Picture Channels";

    public override string TypeName => "MergePictureChannels";
    public override string FromPlugin => InternalPluginBase.InternalPluginBaseID;
    public override EffectType TypeOfEffect => EffectType.NormalEffect;
    public override EffectTarget Target => EffectTarget.Video;

    public IReadOnlyDictionary<string, EffectArgumentFieldDescriptor> OutFields => new Dictionary<string, EffectArgumentFieldDescriptor>
    {
        ["Picture"] = Field("Picture", EffectArgumentFieldType.IPicture | EffectArgumentFieldType.Mandatory, "",
            remarks: "Merged picture. Supplying Brightness produces HDR output.")
    };

    EffectArgumentFieldDescriptor IEffectProvider.OutField => throw new NotSupportedException("Select the Picture output port.");

    protected override IReadOnlyList<EffectArgumentFieldDescriptor> DefineFields() => [];

    protected override IReadOnlyDictionary<string, EffectArgumentFieldDescriptor> DefineInFields() => new Dictionary<string, EffectArgumentFieldDescriptor>
    {
        ["R"] = Field("R", EffectArgumentFieldType.IPicture | EffectArgumentFieldType.Mandatory, "", remarks: "Source red channel."),
        ["G"] = Field("G", EffectArgumentFieldType.IPicture | EffectArgumentFieldType.Mandatory, "", remarks: "Source green channel."),
        ["B"] = Field("B", EffectArgumentFieldType.IPicture | EffectArgumentFieldType.Mandatory, "", remarks: "Source blue channel."),
        ["A"] = Field("A", EffectArgumentFieldType.IPicture, "", remarks: "Source alpha channel. Opaque if absent."),
        ["Brightness"] = Field("Brightness", EffectArgumentFieldType.IPicture, "", remarks: "HDR brightness channel, or SDR luminance at 203 nits.")
    };
}

public sealed class MergePictureChannelsEffect : IEffect, IMultipleOutputEffect
{
    public string FromPlugin => InternalPluginBase.InternalPluginBaseID;
    public string TypeName => "MergePictureChannels";
    public EffectType TypeOfEffect => EffectType.NormalEffect;
    public EffectImplementType ImplementType => EffectImplementType.NotSpecified;
    public string Name { get; set; } = "Merge Picture Channels";
    public string Id { get; set; } = Guid.NewGuid().ToString();
    public Dictionary<string, object> Parameters { get; set; } = new();
    public bool Enabled { get; set; } = true;
    public int Index { get; set; }
    public bool IsReorderable => true;
    public int RelativeWidth { get; set; }
    public int RelativeHeight { get; set; }
    public string? BindedEffectProvidingSystemID { get; set; }
    public IEffect WithParameters(Dictionary<string, object> parameters) => new MergePictureChannelsEffect { Parameters = parameters };

    public IReadOnlyDictionary<string, object?> ComputeOutputs(EffectExecutionContext context)
    {
        context.CancellationToken.ThrowIfCancellationRequested();
        IPicture? Input(string id, bool required = false)
        {
            var value = context.Parameters.GetValueOrDefault(id);
            if (value is null && !required) return null;
            if (value is not IPicture<byte> && value is not IPicture<ushort>)
                throw new ArgumentException($"Input '{id}' must be an 8-bit or 16-bit IPicture.");
            return (IPicture)value;
        }

        var r = Input("R", true)!;
        var g = Input("G", true)!;
        var b = Input("B", true)!;
        var a = Input("A");
        var brightness = Input("Brightness");
        Validate(r, r, "R");
        Validate(g, r, "G");
        Validate(b, r, "B");
        if (a is not null) Validate(a, r, "A");
        if (brightness is not null) Validate(brightness, r, "Brightness");
        var alpha = a switch
        {
            IPicture<byte> p => p.a,
            IPicture<ushort> p => p.a,
            _ => null
        };
        if (a is { HasAlphaChannel: true } && alpha?.Length != r.Pixels)
            throw new ArgumentException("Input 'A' has an invalid alpha buffer.");
        var hdr = brightness as IHDRPicture<ushort>;
        if (hdr is not null && (hdr.Brightness.Length != r.Pixels
            || !float.IsFinite(hdr.MaximumBrightness) || hdr.MaximumBrightness <= 0))
            throw new ArgumentException("Input 'Brightness' has invalid HDR brightness data.");

        IPicture output = brightness is not null ? new HDRPicture16bpp(r.Width, r.Height)
            : r is IPicture<ushort> || g is IPicture<ushort> || b is IPicture<ushort>
                ? new Picture16bpp(r.Width, r.Height) : new Picture8bpp(r.Width, r.Height);
        try
        {
            var outputAlpha = a is null ? null : new float[r.Pixels];
            if (output is IPicture<ushort> p16)
            {
                CopyChannel(r, p16.r, 0);
                CopyChannel(g, p16.g, 1);
                CopyChannel(b, p16.b, 2);
                p16.a = outputAlpha;
            }
            else if (output is IPicture<byte> p8)
            {
                Array.Copy(((IPicture<byte>)r).r, p8.r, r.Pixels);
                Array.Copy(((IPicture<byte>)g).g, p8.g, r.Pixels);
                Array.Copy(((IPicture<byte>)b).b, p8.b, r.Pixels);
                p8.a = outputAlpha;
            }

            output.HasAlphaChannel = outputAlpha is not null;
            var hdrOutput = output as HDRPicture16bpp;
            if (hdrOutput is not null) hdrOutput.MaximumBrightness = hdr?.MaximumBrightness ?? 203f;
            if (outputAlpha is not null || hdrOutput is not null)
            {
                for (int i = 0; i < r.Pixels; i++)
                {
                    if ((i & 4095) == 0) context.CancellationToken.ThrowIfCancellationRequested();
                    if (outputAlpha is not null)
                        outputAlpha[i] = a!.HasAlphaChannel ? Clamp(alpha![i]) : 1f;
                    if (hdrOutput is not null)
                        hdrOutput.Brightness[i] = hdr is not null ? Clamp(hdr.Brightness[i]) : Clamp(Luminance(brightness!, i));
                }
            }
            context.CancellationToken.ThrowIfCancellationRequested();
            Logger.LogDiagnostic($"Merged picture channels {r.Width}x{r.Height}, {output.BitPerPixel}-bit, HDR: {output is IHDRPicture<ushort>}.");
            return new Dictionary<string, object?> { ["Picture"] = output };
        }
        catch
        {
            output.Dispose();
            throw;
        }
    }

    private static void Validate(IPicture picture, IPicture size, string id)
    {
        ObjectDisposedException.ThrowIf(picture.Disposed, picture);
        if (picture.Width <= 0 || picture.Height <= 0 || picture.Width != size.Width || picture.Height != size.Height
            || picture.Pixels != checked(picture.Width * picture.Height))
            throw new ArgumentException($"Input '{id}' must have the same dimensions as R.");
        bool valid = picture switch
        {
            IPicture<byte> p => p.r.Length == p.Pixels && p.g.Length == p.Pixels && p.b.Length == p.Pixels,
            IPicture<ushort> p => p.r.Length == p.Pixels && p.g.Length == p.Pixels && p.b.Length == p.Pixels,
            _ => false
        };
        if (!valid) throw new ArgumentException($"Input '{id}' has invalid RGB buffers.");
    }

    private static T[] Channel<T>(IPicture<T> picture, int channel) => channel switch
    {
        0 => picture.r,
        1 => picture.g,
        _ => picture.b
    };

    private static void CopyChannel(IPicture source, ushort[] target, int channel)
    {
        if (source is IPicture<ushort> p16)
            Array.Copy(Channel(p16, channel), target, source.Pixels);
        else if (source is IPicture<byte> p8)
        {
            var values = Channel(p8, channel);
            for (int i = 0; i < source.Pixels; i++) target[i] = (ushort)(values[i] * 257);
        }
    }

    private static float Clamp(float value) => float.IsFinite(value) ? Math.Clamp(value, 0f, 1f) : 0f;

    private static float Luminance(IPicture picture, int i) => picture switch
    {
        IPicture<byte> p => (0.2627f * p.r[i] + 0.6780f * p.g[i] + 0.0593f * p.b[i]) / byte.MaxValue,
        IPicture<ushort> p => (0.2627f * p.r[i] + 0.6780f * p.g[i] + 0.0593f * p.b[i]) / ushort.MaxValue,
        _ => throw new NotSupportedException()
    };
}
