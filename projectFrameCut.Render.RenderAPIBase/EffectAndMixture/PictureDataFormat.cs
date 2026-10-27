using projectFrameCut.Drawing.Base;
using projectFrameCut.Drawing.Base.Picture;

namespace projectFrameCut.Render.RenderAPIBase.EffectAndMixture;

[Flags]
public enum PictureDataFormat
{
    Unknown = 0,
    Bit8 = 1,
    Bit16 = 2,
    Bit1 = 4,
    Float16 = 8,
    Cpu = 1 << 8,
    Gpu = 1 << 9,
    Hdr = 1 << 10,
    Rgb = 1 << 11,
    Monochrome = 1 << 12,
    Cpu8 = Cpu | Rgb | Bit8,
    Cpu16 = Cpu | Rgb | Bit16,
    CpuHdr16 = Cpu16 | Hdr,
    CpuFP16 = Cpu | Rgb | Bit16 | Float16,
    CpuHdrFP16 = CpuFP16 | Hdr,
}

public static class PictureDataFormats
{
    public static IEffect Specialize(IEffect effect, object? dataFormat)
    {
        var typed = dataFormat is PictureDataFormat format ? GetPictureType(format) switch
        {
            Type t when t == typeof(PictureFP16) => effect.WithPictureType<PictureFP16>(),
            Type t when t == typeof(HDRPictureFP16) => effect.WithPictureType<HDRPictureFP16>(),
            Type t when t == typeof(Picture8bpp) => effect.WithPictureType<Picture8bpp>(),
            Type t when t == typeof(Picture16bpp) => effect.WithPictureType<Picture16bpp>(),
            Type t when t == typeof(HDRPicture16bpp) => effect.WithPictureType<HDRPicture16bpp>(),
            _ => effect,
        } : effect;
        if (!ReferenceEquals(typed, effect) && (typed is not TypedPictureEffect wrapper || !wrapper.Owns(effect)) && effect is IDisposable d)
            d.Dispose();
        return typed;
    }

    public static IEffect[] BuildForDataFormat(this IEffectProvider provider, object? dataFormat) => dataFormat is PictureDataFormat format
        ? GetPictureType(format) switch
        {
            Type t when t == typeof(PictureFP16) => provider.Build<PictureFP16>(),
            Type t when t == typeof(HDRPictureFP16) => provider.Build<HDRPictureFP16>(),
            Type t when t == typeof(Picture8bpp) => provider.Build<Picture8bpp>(),
            Type t when t == typeof(Picture16bpp) => provider.Build<Picture16bpp>(),
            Type t when t == typeof(HDRPicture16bpp) => provider.Build<HDRPicture16bpp>(),
            _ => provider.Build(),
        } : provider.Build();

    public static PictureDataFormat FromPicture(IPicture picture) =>
        (picture is IHwAccelPicture ? PictureDataFormat.Gpu : PictureDataFormat.Cpu)
        | (picture.BitPerPixel == IPicture.PicturePixelMode.Float16Picture ? PictureDataFormat.Bit16 | PictureDataFormat.Float16 : 0)
        | ((int)picture.BitPerPixel == 8 ? PictureDataFormat.Bit8 : (int)picture.BitPerPixel == 16 ? PictureDataFormat.Bit16 : (int)picture.BitPerPixel == 1 ? PictureDataFormat.Bit1 : 0)
        | (picture is IMonoChromePicture<byte> ? PictureDataFormat.Monochrome : PictureDataFormat.Rgb)
        | (picture is IHDRPicture<Half> || picture is IHDRPicture<ushort> || picture is IHwAccelPicture { IsHdr: true } ? PictureDataFormat.Hdr : 0);

    public static PictureDataFormat FromType<TPicture>() where TPicture : IPicture => FromType(typeof(TPicture));

    public static PictureDataFormat FromType(Type type) =>
        type == typeof(HDRPictureFP16) || type == typeof(IHDRPicture<Half>) ? PictureDataFormat.CpuHdrFP16
        : type == typeof(PictureFP16) || type == typeof(IPicture<Half>) ? PictureDataFormat.CpuFP16
        : type == typeof(Picture8bpp) || type == typeof(IPicture<byte>) ? PictureDataFormat.Cpu8
        : type == typeof(HDRPicture16bpp) || type == typeof(IHDRPicture<ushort>) ? PictureDataFormat.CpuHdr16
        : type == typeof(Picture16bpp) || type == typeof(IPicture<ushort>) ? PictureDataFormat.Cpu16
        : PictureDataFormat.Unknown;

    public static PictureDataFormat WithLocation(PictureDataFormat format, bool gpu) =>
        format == PictureDataFormat.Unknown ? format
        : (format & ~(PictureDataFormat.Cpu | PictureDataFormat.Gpu)) | (gpu ? PictureDataFormat.Gpu : PictureDataFormat.Cpu);

    public static PictureDataFormat Unify(IEnumerable<PictureDataFormat> formats)
    {
        var values = formats.ToArray();
        if (values.Length == 0 || values.Any(f => GetPictureType(f) is null)) return PictureDataFormat.Unknown;
        return WithLocation(values.Any(f => f.HasFlag(PictureDataFormat.Float16))
            ? values.Any(f => f.HasFlag(PictureDataFormat.Hdr)) ? PictureDataFormat.CpuHdrFP16 : PictureDataFormat.CpuFP16
            : values.Any(f => f.HasFlag(PictureDataFormat.Hdr)) ? PictureDataFormat.CpuHdr16
            : values.Any(f => f.HasFlag(PictureDataFormat.Bit16)) ? PictureDataFormat.Cpu16 : PictureDataFormat.Cpu8,
            values.All(f => f.HasFlag(PictureDataFormat.Gpu)));
    }

    public static Type? GetPictureType(PictureDataFormat format)
    {
        var location = format & (PictureDataFormat.Cpu | PictureDataFormat.Gpu);
        if (location != PictureDataFormat.Cpu && location != PictureDataFormat.Gpu) return null;
        var f = format & ~(PictureDataFormat.Cpu | PictureDataFormat.Gpu);
        return f == (PictureDataFormat.Rgb | PictureDataFormat.Bit16 | PictureDataFormat.Float16 | PictureDataFormat.Hdr) ? typeof(HDRPictureFP16)
            : f == (PictureDataFormat.Rgb | PictureDataFormat.Bit16 | PictureDataFormat.Float16) ? typeof(PictureFP16)
            : f == (PictureDataFormat.Rgb | PictureDataFormat.Bit8) ? typeof(Picture8bpp)
            : f == (PictureDataFormat.Rgb | PictureDataFormat.Bit16) ? typeof(Picture16bpp)
            : f == (PictureDataFormat.Rgb | PictureDataFormat.Bit16 | PictureDataFormat.Hdr) ? typeof(HDRPicture16bpp) : null;
    }

    public static object? Resolve(IEffectProvider provider, string fieldId, bool isInput, EffectFieldTypeContext context)
    {
        var fields = isInput ? context.State.Fields : context.State.Outputs;
        if (!fields.TryGetValue(fieldId, out var field)) return null;
        if (field.DataFormat is not null) return field.DataFormat;
        if (isInput)
        {
            if (context.InputDataFormats.TryGetValue(fieldId, out var format) && format is not null) return format;
            return !context.State.DynamicParameters.Contains(fieldId) && context.Parameters.GetValueOrDefault(fieldId) is IPicture p
                ? FromPicture(p) : null;
        }
        if (!field.FieldType.IsPicture()) return null;
        var inputs = context.State.Fields.Where(p => p.Value.FieldType.IsPicture())
            .Select(p => Resolve(provider, p.Key, true, context) is PictureDataFormat f ? f : PictureDataFormat.Unknown).ToArray();
        if (inputs.Length == 0) return null;
        var result = Unify(inputs);
        if (result == PictureDataFormat.Unknown) return null;
        if (provider.TypeOfEffect == EffectType.MixtureProvider)
        {
            if (context.InputDataFormats.GetValueOrDefault(EffectProviderBase.PrimaryInputAnchorKey) is not PictureDataFormat canvas
                || GetPictureType(canvas) is null) return null;
            if (canvas.HasFlag(PictureDataFormat.Bit8)) result = PictureDataFormat.Cpu8;
        }
        var requested = context.State.Metadata.GetValueOrDefault(EffectProviderBase.ImplementTypeParameterKey);
        var implementation = requested is EffectImplementType type ? type
            : Enum.TryParse<EffectImplementType>(requested?.ToString(), out var parsed) ? parsed : provider.DefaultImplementType;
        var registry = Plugins.IPluginBase.EffectImplementations;
        if (provider.TypeOfEffect != EffectType.Transform && registry.ForcePreferToType is { } forced
            && registry.GetImplementTypes(provider.TypeName).Any(t => t is EffectImplementType.IPicture or EffectImplementType.HwAcceleration))
            implementation = forced;
        if (implementation == EffectImplementType.NotSpecified) implementation = provider.DefaultImplementType;
        if (implementation == registry.ForcePreferToType && !registry.GetImplementTypes(provider.TypeName).Contains(implementation))
            implementation = provider.DefaultImplementType;
        return WithLocation(result, implementation == EffectImplementType.HwAcceleration);
    }
}

/// <summary>Owns only the conversion required by a typed, multi-input invocation.</summary>
public sealed class PictureInput<TPicture> : IDisposable where TPicture : IPicture
{
    private readonly IPicture source;
    private bool retained;
    public TPicture Picture { get; }

    public PictureInput(IPicture source, bool signalBrightness = true, Half? maximumBrightness = null)
    {
        this.source = source;
        IPicture p = EffectPictureProcessing.Materialize(source);
        try
        {
            var format = PictureDataFormats.FromType<TPicture>();
            if (p is IPicture<Half> && format != PictureDataFormat.Unknown && !format.HasFlag(PictureDataFormat.Float16))
                throw new NotSupportedException($"Integer-only effect input {typeof(TPicture).Name} cannot consume FP16 pictures.");
            if (format != PictureDataFormat.Unknown && PictureDataFormats.FromPicture(p) != format)
            {
                IPicture converted;
                if (format.HasFlag(PictureDataFormat.Float16))
                {
                    var fp = FP16Conversion.FromPicture(p);
                    if (format.HasFlag(PictureDataFormat.Hdr) && fp is not IHDRPicture<Half>)
                    {
                        try { converted = signalBrightness ? HDRPictureFP16.ToHDRPictureBySignal(fp, 203) : fp.ToHDR((Half)203, Half.One); }
                        finally { fp.Dispose(); }
                    }
                    else converted = fp;
                }
                else if (format.HasFlag(PictureDataFormat.Hdr) && !signalBrightness && p is not IHDRPicture<ushort>)
                {
                    var p16 = p.ToBitPerPixel(16);
                    try
                    {
                        var brightness = new Half[p.Pixels];
                        Array.Fill(brightness, Half.One);
                        converted = new HDRPicture16bpp((IPicture<ushort>)p16, true) { Brightness = brightness, MaximumBrightness = (Half)203 };
                    }
                    finally { if (!ReferenceEquals(p16, p)) p16.Dispose(); }
                }
                else converted = format.HasFlag(PictureDataFormat.Hdr)
                    ? EffectPictureProcessing.ToHdr(p, (int)(p is IHDRPicture<ushort> hdr ? (float)hdr.MaximumBrightness : 203))
                    : p.ToBitPerPixel(format.HasFlag(PictureDataFormat.Bit16) ? 16 : 8);
                if (signalBrightness && maximumBrightness is { } fpPeak && converted is HDRPictureFP16 fpHdr && fpPeak > Half.Zero && fpPeak != fpHdr.MaximumBrightness)
                {
                    float scale = (float)fpHdr.MaximumBrightness / (float)fpPeak;
                    using (var edit = fpHdr.Edit())
                        for (int i = 0; i < fpHdr.Pixels; i++) edit.Brightness[i] = PictureFP16.Unit((float)edit.Brightness[i] * scale);
                    fpHdr.MaximumBrightness = fpPeak;
                }
                if (signalBrightness && maximumBrightness is { } peak && format.HasFlag(PictureDataFormat.Hdr)
                    && converted is IHDRPicture<ushort> convertedHdr) convertedHdr.MaximumBrightness = peak;
                if (!ReferenceEquals(p, source) && !ReferenceEquals(p, converted)) p.Dispose();
                p = converted;
            }
            Picture = p is TPicture typed ? typed
                : throw new NotSupportedException($"Cannot use {p.GetType().Name} as {typeof(TPicture).Name}.");
        }
        catch
        {
            if (!ReferenceEquals(p, source)) p.Dispose();
            throw;
        }
    }

    public void Keep(IPicture? output) => retained |= ReferenceEquals(Picture, output);
    public void Dispose()
    {
        if (!retained && !ReferenceEquals(source, Picture)) Picture.Dispose();
    }
}
