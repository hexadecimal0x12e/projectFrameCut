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
        int pixels = ValidateDimensions(picture.Width, picture.Height);
        var descriptor = CreateDescriptor(picture, pixels);
        long length = checked(descriptor.RedLength + descriptor.GreenLength + descriptor.BlueLength + descriptor.AlphaLength + descriptor.BrightnessLength);
        var lease = await exchange.AllocateAsync(length, preferredKind, cancellationToken).ConfigureAwait(false);
        lease.Reference.Media = descriptor;
        await using var stream = await exchange.OpenWriteAsync(lease.Reference, cancellationToken).ConfigureAwait(false);
        if (picture is IPicture<byte> p8)
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

        if (picture.GetSpecificChannel(IPicture.ChannelId.Alpha) is float[] alpha)
            await WritePlaneAsync(stream, alpha, cancellationToken).ConfigureAwait(false);
        if (picture is IHDRPicture<ushort> hdr)
            await WritePlaneAsync(stream, hdr.Brightness, cancellationToken).ConfigureAwait(false);
        await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
        return lease;
    }

    public static async ValueTask<IPicture> ReadAsync(IsolationPayloadReference reference, IIsolationPayloadResolver resolver, CancellationToken cancellationToken = default)
    {
        var d = reference.Media ?? throw new InvalidDataException("Picture payload has no media descriptor.");
        if (d.LayoutVersion != 1) throw new InvalidDataException($"Unsupported picture payload layout {d.LayoutVersion}.");
        int pixels = ValidateDimensions(d.Width, d.Height);
        int bytes = d.BitsPerChannel switch { 8 => 1, 16 => 2, _ => throw new InvalidDataException("Picture payload bit depth must be 8 or 16.") };
        long channelLength = checked((long)pixels * bytes);
        long floatLength = checked((long)pixels * sizeof(float));
        if (d.RedLength != channelLength || d.GreenLength != channelLength || d.BlueLength != channelLength
            || d.AlphaLength != (d.HasAlpha ? floatLength : 0)
            || d.BrightnessLength != (d.HasHdrBrightness ? floatLength : 0)
            || reference.Length != checked(channelLength * 3 + d.AlphaLength + d.BrightnessLength))
            throw new InvalidDataException("Picture payload plane lengths are invalid.");

        await using var stream = await resolver.OpenReadAsync(reference, cancellationToken).ConfigureAwait(false);
        if (d.BitsPerChannel == 8)
        {
            var picture = new Picture8bpp(d.Width, d.Height);
            await ReadExactlyAsync(stream, picture.r, cancellationToken).ConfigureAwait(false);
            await ReadExactlyAsync(stream, picture.g, cancellationToken).ConfigureAwait(false);
            await ReadExactlyAsync(stream, picture.b, cancellationToken).ConfigureAwait(false);
            if (d.HasAlpha)
            {
                picture.a = new float[pixels];
                await ReadPlaneAsync(stream, picture.a, cancellationToken).ConfigureAwait(false);
                picture.HasAlphaChannel = true;
            }
            return picture;
        }

        Picture16bpp result = d.HasHdrBrightness ? new HDRPicture16bpp(d.Width, d.Height) : new Picture16bpp(d.Width, d.Height);
        await ReadPlaneAsync(stream, result.r, cancellationToken).ConfigureAwait(false);
        await ReadPlaneAsync(stream, result.g, cancellationToken).ConfigureAwait(false);
        await ReadPlaneAsync(stream, result.b, cancellationToken).ConfigureAwait(false);
        if (d.HasAlpha)
        {
            result.a = new float[pixels];
            await ReadPlaneAsync(stream, result.a, cancellationToken).ConfigureAwait(false);
            result.HasAlphaChannel = true;
        }
        if (result is HDRPicture16bpp hdr)
        {
            hdr.Brightness = new float[pixels];
            hdr.MaximumBrightness = d.MaximumBrightness;
            await ReadPlaneAsync(stream, hdr.Brightness, cancellationToken).ConfigureAwait(false);
        }
        return result;
    }

    private static IsolationMediaDescriptor CreateDescriptor(IPicture picture, int pixels)
    {
        int bytes = picture.BitPerPixel.Value switch { 8 => 1, 16 => 2, _ => throw new NotSupportedException("Only 8-bit and 16-bit pictures can cross the isolation boundary.") };
        long channelLength = checked((long)pixels * bytes);
        long floatLength = checked((long)pixels * sizeof(float));
        bool hasAlpha = picture.HasAlphaChannel && picture.GetSpecificChannel(IPicture.ChannelId.Alpha) is float[] { Length: > 0 };
        bool hdr = picture is IHDRPicture<ushort>;
        return new()
        {
            Width = picture.Width,
            Height = picture.Height,
            BitsPerChannel = picture.BitPerPixel.Value,
            HasAlpha = hasAlpha,
            HasHdrBrightness = hdr,
            MaximumBrightness = hdr ? ((IHDRPicture<ushort>)picture).MaximumBrightness : 0,
            RedLength = channelLength,
            GreenLength = channelLength,
            BlueLength = channelLength,
            AlphaLength = hasAlpha ? floatLength : 0,
            BrightnessLength = hdr ? floatLength : 0,
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

    private static async Task ReadPlaneAsync<T>(Stream stream, T[] values, CancellationToken cancellationToken) where T : unmanaged
    {
        var bytes = new byte[checked(values.Length * Marshal.SizeOf<T>())];
        await ReadExactlyAsync(stream, bytes, cancellationToken).ConfigureAwait(false);
        MemoryMarshal.Cast<byte, T>(bytes).CopyTo(values);
    }
}
