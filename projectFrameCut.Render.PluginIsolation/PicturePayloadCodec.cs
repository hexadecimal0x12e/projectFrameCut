using projectFrameCut.Drawing.Base;
using projectFrameCut.Drawing.Base.Picture;
using projectFrameCut.Render.Contracts;
using System.Runtime.InteropServices;

namespace projectFrameCut.Render.PluginIsolation;

public static class PicturePayloadCodec
{
    public static async ValueTask<IsolationPayloadLease> WriteAsync(IPicture picture, IIsolationPayloadExchange exchange, IsolationPayloadKind preferredKind = IsolationPayloadKind.SharedMemory, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(picture);
        if (picture is RenderAPIBase.EffectAndMixture.IHwAccelPicture gpu)
        {
            using var cpu = gpu.Download();
            return await WriteAsync(cpu, exchange, preferredKind, cancellationToken).ConfigureAwait(false);
        }
        int pixels = ValidateDimensions(picture.Width, picture.Height);
        var descriptor = CreateDescriptor(picture, pixels);
        long length = checked(descriptor.RedLength + descriptor.GreenLength + descriptor.BlueLength + descriptor.AlphaLength + descriptor.BrightnessLength);
        var lease = await exchange.AllocateAsync(length, preferredKind, cancellationToken).ConfigureAwait(false);
        lease.Reference.Media = descriptor;
        await using var stream = await exchange.OpenWriteAsync(lease.Reference, cancellationToken).ConfigureAwait(false);
        if (picture is IPicture<Half> fp)
        {
            await WritePlaneAsync(stream, fp.r, cancellationToken).ConfigureAwait(false);
            await WritePlaneAsync(stream, fp.g, cancellationToken).ConfigureAwait(false);
            await WritePlaneAsync(stream, fp.b, cancellationToken).ConfigureAwait(false);
        }
        else if (picture is IPicture<byte> p8)
        {
            await stream.WriteAsync(p8.r, cancellationToken).ConfigureAwait(false);
            await stream.WriteAsync(p8.g, cancellationToken).ConfigureAwait(false);
            await stream.WriteAsync(p8.b, cancellationToken).ConfigureAwait(false);
        }
        else if (picture is IPicture<ushort> p16)
        {
            await WritePlaneAsync(stream, p16.r, cancellationToken).ConfigureAwait(false);
            await WritePlaneAsync(stream, p16.g, cancellationToken).ConfigureAwait(false);
            await WritePlaneAsync(stream, p16.b, cancellationToken).ConfigureAwait(false);
        }
        else throw new NotSupportedException($"Picture type '{picture.GetType().FullName}' is not supported by isolation.");

        if (descriptor.HasAlpha && picture.GetSpecificChannel(IPicture.ChannelId.Alpha) is Half[] alpha)
            await WritePlaneAsync(stream, alpha, cancellationToken).ConfigureAwait(false);
        if (picture is IHDRPicture<Half> hf)
            await WritePlaneAsync(stream, hf.Brightness, cancellationToken).ConfigureAwait(false);
        else if (picture is IHDRPicture<ushort> hdr)
            await WritePlaneAsync(stream, hdr.Brightness, cancellationToken).ConfigureAwait(false);
        await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
        return lease;
    }

    public static async ValueTask<IPicture> ReadAsync(IsolationPayloadReference reference, IIsolationPayloadResolver resolver, CancellationToken cancellationToken = default)
    {
        var d = reference.Media ?? throw new InvalidDataException("Picture payload has no media descriptor.");
        if (d.LayoutVersion is not (1 or 2) || d.UsesFloat16 != (d.LayoutVersion == 2)) throw new InvalidDataException($"Unsupported picture payload layout {d.LayoutVersion}.");
        if (d.UsesFloat16 && d.BitsPerChannel != 16) throw new InvalidDataException("FP16 payload channels must be 16 bits.");
        int pixels = ValidateDimensions(d.Width, d.Height);
        int bytes = d.BitsPerChannel switch { 8 => 1, 16 => 2, _ => throw new InvalidDataException("Picture payload bit depth must be 8 or 16.") };
        long channelLength = checked((long)pixels * bytes);
        long halfLength = checked((long)pixels * sizeof(ushort));
        if (d.RedLength != channelLength || d.GreenLength != channelLength || d.BlueLength != channelLength
            || d.AlphaLength != (d.HasAlpha ? halfLength : 0)
            || d.BrightnessLength != (d.HasHdrBrightness ? halfLength : 0)
            || reference.Length != checked(channelLength * 3 + d.AlphaLength + d.BrightnessLength))
            throw new InvalidDataException("Picture payload plane lengths are invalid.");

        await using var stream = await resolver.OpenReadAsync(reference, cancellationToken).ConfigureAwait(false);
        var red = await ReadColorPlaneAsync(stream, pixels, d, cancellationToken).ConfigureAwait(false);
        var green = await ReadColorPlaneAsync(stream, pixels, d, cancellationToken).ConfigureAwait(false);
        var blue = await ReadColorPlaneAsync(stream, pixels, d, cancellationToken).ConfigureAwait(false);
        var alpha = d.HasAlpha ? await ReadHalfPlaneAsync(stream, pixels, cancellationToken).ConfigureAwait(false) : null;
        return d.HasHdrBrightness
            ? HDRPictureFP16.FromOwnedChannels(d.Width, d.Height, red, green, blue, alpha,
                await ReadHalfPlaneAsync(stream, pixels, cancellationToken).ConfigureAwait(false), d.MaximumBrightness)
            : PictureFP16.FromOwnedChannels(d.Width, d.Height, red, green, blue, alpha);
    }

    private static async Task<Half[]> ReadColorPlaneAsync(Stream stream, int pixels, IsolationMediaDescriptor d, CancellationToken ct)
    {
        if (d.UsesFloat16) return await ReadHalfPlaneAsync(stream, pixels, ct).ConfigureAwait(false);
        var data = new byte[checked(pixels * (d.BitsPerChannel / 8))];
        await ReadExactlyAsync(stream, data, ct).ConfigureAwait(false);
        var result = new Half[pixels];
        for (int i = 0; i < pixels; i++) result[i] = (Half)(d.BitsPerChannel == 8 ? data[i] / 255f
            : System.Buffers.Binary.BinaryPrimitives.ReadUInt16LittleEndian(data.AsSpan(i * 2, 2)) / 65535f);
        return result;
    }

    private static IsolationMediaDescriptor CreateDescriptor(IPicture picture, int pixels)
    {
        int bytes = picture.BitPerPixel.Value switch { 116 => 2, 8 => 1, 16 => 2, _ => throw new NotSupportedException("Only 8-bit and 16-bit pictures can cross the isolation boundary.") };
        long channelLength = checked((long)pixels * bytes);
        long halfLength = checked((long)pixels * sizeof(ushort));
        bool hasAlpha = picture.HasAlphaChannel && picture.GetSpecificChannel(IPicture.ChannelId.Alpha) is Half[] { Length: > 0 };
        bool hdr = picture is IHDRPicture<Half> or IHDRPicture<ushort>;
        return new()
        {
            LayoutVersion = picture is IPicture<Half> ? 2 : 1,
            UsesFloat16 = picture is IPicture<Half>,
            Width = picture.Width,
            Height = picture.Height,
            BitsPerChannel = picture is IPicture<Half> ? 16 : picture.BitPerPixel.Value,
            HasAlpha = hasAlpha,
            HasHdrBrightness = hdr,
            MaximumBrightness = picture is IHDRPicture<Half> hf ? hf.MaximumBrightness : hdr ? ((IHDRPicture<ushort>)picture).MaximumBrightness : Half.Zero,
            RedLength = channelLength,
            GreenLength = channelLength,
            BlueLength = channelLength,
            AlphaLength = hasAlpha ? halfLength : 0,
            BrightnessLength = hdr ? halfLength : 0,
        };
    }

    private static int ValidateDimensions(int width, int height)
    {
        if (width <= 0 || height <= 0 || width > 65536 || height > 65536) throw new InvalidDataException("Picture dimensions are invalid.");
        return checked(width * height);
    }

    private static async Task ReadExactlyAsync(Stream stream, Memory<byte> buffer, CancellationToken cancellationToken)
    {
        var offset = 0;
        while (offset < buffer.Length)
        {
            var read = await stream.ReadAsync(buffer[offset..], cancellationToken).ConfigureAwait(false);
            if (read == 0) throw new EndOfStreamException("Picture payload was truncated.");
            offset += read;
        }
    }

    private static ValueTask WritePlaneAsync<T>(Stream stream, T[] values, CancellationToken cancellationToken) where T : unmanaged
        => stream.WriteAsync(MemoryMarshal.AsBytes<T>(values.AsSpan()).ToArray(), cancellationToken);

    private static async Task<Half[]> ReadHalfPlaneAsync(Stream stream, int pixels, CancellationToken cancellationToken)
    {
        var values = GC.AllocateUninitializedArray<Half>(pixels);
        await ReadPlaneAsync(stream, values, cancellationToken).ConfigureAwait(false);
        return values;
    }

    private static async Task ReadPlaneAsync<T>(Stream stream, T[] values, CancellationToken cancellationToken) where T : unmanaged
    {
        var bytes = new byte[MemoryMarshal.AsBytes<T>(values.AsSpan()).Length];
        await ReadExactlyAsync(stream, bytes, cancellationToken).ConfigureAwait(false);
        MemoryMarshal.Cast<byte, T>(bytes).CopyTo(values);
    }
}
