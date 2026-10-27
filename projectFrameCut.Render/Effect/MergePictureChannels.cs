using projectFrameCut.Drawing.Base;
using projectFrameCut.Drawing.Base.Picture;
using projectFrameCut.Render.Plugin;
using projectFrameCut.Render.RenderAPIBase.EffectAndMixture;
using projectFrameCut.Shared;

namespace projectFrameCut.Render.Effect;

public sealed class MergePictureChannelsEffectProvider : EffectProviderBase, IMultipleOutputEffectProvider
{
    public override IReadOnlyList<Stage> SupportedStages => [Stage.Picture];

    public override bool IsDeterministic => true;

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

    public override object? ResolveDataFormat(string fieldId, bool isInput, EffectFieldTypeContext context)
    {
        if (isInput) return base.ResolveDataFormat(fieldId, true, context);
        if (context.State.Bindings.TryGetValue("__Brightness__", out var binding) && binding != IEffectProvider.NoConnectionGUID.ToString()
            || context.Parameters.GetValueOrDefault("__Brightness__") is IPicture) return PictureDataFormat.CpuHdrFP16;
        var format = PictureDataFormats.Unify(new[] { "__R__", "__G__", "__B__" }
            .Select(id => base.ResolveDataFormat(id, true, context) is PictureDataFormat f ? f : PictureDataFormat.Unknown));
        return format == PictureDataFormat.Unknown ? null
            : format.HasFlag(PictureDataFormat.Float16) ? PictureDataFormat.CpuFP16 : format.HasFlag(PictureDataFormat.Bit16) ? PictureDataFormat.Cpu16 : PictureDataFormat.Cpu8;
    }

    protected override IReadOnlyList<EffectArgumentFieldDescriptor> DefineFields() => [];

    protected override IReadOnlyDictionary<string, EffectArgumentFieldDescriptor> DefineInFields() => new Dictionary<string, EffectArgumentFieldDescriptor>
    {
        ["__R__"] = Field("__R__", EffectArgumentFieldType.IPicture | EffectArgumentFieldType.Mandatory, "", remarks: "Source red channel."),
        ["__G__"] = Field("__G__", EffectArgumentFieldType.IPicture | EffectArgumentFieldType.Mandatory, "", remarks: "Source green channel."),
        ["__B__"] = Field("__B__", EffectArgumentFieldType.IPicture | EffectArgumentFieldType.Mandatory, "", remarks: "Source blue channel."),
        ["__A__"] = Field("__A__", EffectArgumentFieldType.IPicture, "", remarks: "Source alpha channel. Opaque if absent."),
        ["__Brightness__"] = Field("__Brightness__", EffectArgumentFieldType.IPicture, "", remarks: "HDR brightness channel, or SDR luminance at 203 nits.")
    };
}

public class MergePictureChannelsEffect : MergePictureChannelsEffect<IPicture> { }

public class MergePictureChannelsEffect<TPicture> : IMultipleOutputEffect<TPicture> where TPicture : IPicture
{
    public IEffect WithPictureType<T>() where T : IPicture => new MergePictureChannelsEffect<T>();

    public bool IsDeterministic => true;

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
    public IEffect WithParameters(Dictionary<string, object> parameters) => new MergePictureChannelsEffect<TPicture> { Parameters = parameters };

    public IReadOnlyDictionary<string, object?> ComputeOutputs(EffectExecutionContext context) =>
        ComputeOutputs(default, context);

    public IReadOnlyDictionary<string, object?> ComputeOutputs(TPicture? source, EffectExecutionContext context)
    {
        context.CancellationToken.ThrowIfCancellationRequested();
        IPicture? Input(string id, bool required = false)
        {
            var value = context.Parameters.GetValueOrDefault(id);
            if (value is null && !required) return null;
            if (value is not IPicture<Half> && value is not IPicture<byte> && value is not IPicture<ushort>)
                throw new ArgumentException($"Input '{id}' must be an 8-bit or 16-bit IPicture.");
            return (IPicture)value;
        }

        using var red = new PictureInput<TPicture>(Input("__R__", true)!);
        using var green = new PictureInput<TPicture>(Input("__G__", true)!);
        using var blue = new PictureInput<TPicture>(Input("__B__", true)!);
        var r = red.Picture;
        var g = green.Picture;
        var b = blue.Picture;
        var a = Input("__A__");
        var brightness = Input("__Brightness__");
        Validate(r, r, "R");
        Validate(g, r, "G");
        Validate(b, r, "B");
        if (a is not null) Validate(a, r, "A");
        if (brightness is not null) Validate(brightness, r, "Brightness");
        if (r is IPicture<Half> rf && g is IPicture<Half> gf && b is IPicture<Half> bf)
        {
            PictureFP16 fpOutput = brightness is null ? new PictureFP16(r.Width, r.Height)
                : new HDRPictureFP16(r.Width, r.Height) { MaximumBrightness = (brightness as IHDRPicture<Half>)?.MaximumBrightness ?? (Half)203 };
            fpOutput.SetAlpha(a is not null);
            using var e = fpOutput.Edit();
            for (int i = 0; i < r.Pixels; i++)
            {
                if ((i & 4095) == 0) context.CancellationToken.ThrowIfCancellationRequested();
                e.Red[i] = rf.r[i]; e.Green[i] = gf.g[i]; e.Blue[i] = bf.b[i];
                if (!e.Alpha.IsEmpty) e.Alpha[i] = a is IPicture<Half> af && af.HasAlphaChannel && af.a is { } al ? al[i] : Half.One;
                if (!e.Brightness.IsEmpty) e.Brightness[i] = brightness is IHDRPicture<Half> hf ? hf.Brightness[i] : Luminance(brightness!, i);
            }
            return new Dictionary<string, object?> { ["Picture"] = fpOutput };
        }
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
            || !Half.IsFinite(hdr.MaximumBrightness) || hdr.MaximumBrightness <= Half.Zero))
            throw new ArgumentException("Input 'Brightness' has invalid HDR brightness data.");

        IPicture output = brightness is not null ? new HDRPicture16bpp(r.Width, r.Height)
            : PictureCalculation<TPicture>.Is16 || !PictureCalculation<TPicture>.Is8 && (r is IPicture<ushort> || g is IPicture<ushort> || b is IPicture<ushort>)
                ? new Picture16bpp(r.Width, r.Height) : new Picture8bpp(r.Width, r.Height);
        try
        {
            var outputAlpha = a is null ? null : new Half[r.Pixels];
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
            if (hdrOutput is not null) hdrOutput.MaximumBrightness = hdr?.MaximumBrightness ?? (Half)203;
            if (outputAlpha is not null || hdrOutput is not null)
            {
                for (int i = 0; i < r.Pixels; i++)
                {
                    if ((i & 4095) == 0) context.CancellationToken.ThrowIfCancellationRequested();
                    if (outputAlpha is not null)
                        outputAlpha[i] = a!.HasAlphaChannel ? HalfChannelMath.Clamp(alpha![i]) : Half.One;
                    if (hdrOutput is not null)
                        hdrOutput.Brightness[i] = hdr is not null ? HalfChannelMath.Clamp(hdr.Brightness[i]) : Luminance(brightness!, i);
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
            IPicture<Half> p => p.r.Length == p.Pixels && p.g.Length == p.Pixels && p.b.Length == p.Pixels,
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

    private static void CopyChannel(TPicture source, ushort[] target, int channel)
    {
        if (PictureCalculation<TPicture>.Is16 || !PictureCalculation<TPicture>.Is8 && source is IPicture<ushort>)
            Array.Copy(Channel((IPicture<ushort>)(object)source, channel), target, source.Pixels);
        else if (source is IPicture<byte> p8)
        {
            var values = Channel(p8, channel);
            for (int i = 0; i < source.Pixels; i++) target[i] = (ushort)(values[i] * 257);
        }
    }

    private static float Clamp(float value) => float.IsFinite(value) ? Math.Clamp(value, 0f, 1f) : 0f;

    private static Half Luminance(IPicture picture, int i) => picture switch
    {
        IPicture<Half> p => PictureFP16.Unit((float)p.r[i] * .2627f + (float)p.g[i] * .678f + (float)p.b[i] * .0593f),
        IPicture<byte> p => HalfChannelMath.Luminance(p.r[i], p.g[i], p.b[i], byte.MaxValue),
        IPicture<ushort> p => HalfChannelMath.Luminance(p.r[i], p.g[i], p.b[i], ushort.MaxValue),
        _ => throw new NotSupportedException()
    };
}
