using System.Buffers.Binary;
using System.Runtime.InteropServices;
using projectFrameCut.AIComponentContracts;
using projectFrameCut.Drawing.Base;
using projectFrameCut.Drawing.Base.Picture;
using projectFrameCut.Shared;
using IPicture = projectFrameCut.Drawing.Base.IPicture;

namespace projectFrameCut.Services.AIComponent;

internal static class AIComponentPayloadCodec
{
    public static byte[] EncodePicture(IPicture picture, out AIPictureDescriptor descriptor)
    {
        ArgumentNullException.ThrowIfNull(picture);
        int pixels = ValidatePicture(picture);
        bool hasAlpha = picture.HasAlphaChannel;

        if (picture is IPicture<byte> p8)
        {
            ValidatePlane(p8.r, pixels, nameof(p8.r));
            ValidatePlane(p8.g, pixels, nameof(p8.g));
            ValidatePlane(p8.b, pixels, nameof(p8.b));
            if (hasAlpha) ValidatePlane(p8.a, pixels, nameof(p8.a));

            descriptor = new AIPictureDescriptor
            {
                Width = picture.Width,
                Height = picture.Height,
                BitDepth = AIPictureBitDepth.Byte,
                HasAlpha = hasAlpha
            };

            int planeBytes = checked(pixels * sizeof(byte));
            int alphaBytes = hasAlpha ? checked(pixels * sizeof(float)) : 0;
            byte[] output = new byte[checked(planeBytes * 3 + alphaBytes)];
            int offset = 0;
            p8.r.AsSpan(0, pixels).CopyTo(output.AsSpan(offset, planeBytes)); offset += planeBytes;
            p8.g.AsSpan(0, pixels).CopyTo(output.AsSpan(offset, planeBytes)); offset += planeBytes;
            p8.b.AsSpan(0, pixels).CopyTo(output.AsSpan(offset, planeBytes)); offset += planeBytes;
            if (hasAlpha) MemoryMarshal.AsBytes(p8.a!.AsSpan(0, pixels)).CopyTo(output.AsSpan(offset, alphaBytes));
            return output;
        }

        if (picture is IPicture<ushort> p16)
        {
            ValidatePlane(p16.r, pixels, nameof(p16.r));
            ValidatePlane(p16.g, pixels, nameof(p16.g));
            ValidatePlane(p16.b, pixels, nameof(p16.b));
            if (hasAlpha) ValidatePlane(p16.a, pixels, nameof(p16.a));

            descriptor = new AIPictureDescriptor
            {
                Width = picture.Width,
                Height = picture.Height,
                BitDepth = AIPictureBitDepth.UShort,
                HasAlpha = hasAlpha
            };

            int planeBytes = checked(pixels * sizeof(ushort));
            int alphaBytes = hasAlpha ? checked(pixels * sizeof(float)) : 0;
            byte[] output = new byte[checked(planeBytes * 3 + alphaBytes)];
            int offset = 0;
            CopyUShortPlane(p16.r.AsSpan(0, pixels), output.AsSpan(offset, planeBytes)); offset += planeBytes;
            CopyUShortPlane(p16.g.AsSpan(0, pixels), output.AsSpan(offset, planeBytes)); offset += planeBytes;
            CopyUShortPlane(p16.b.AsSpan(0, pixels), output.AsSpan(offset, planeBytes)); offset += planeBytes;
            if (hasAlpha) MemoryMarshal.AsBytes(p16.a!.AsSpan(0, pixels)).CopyTo(output.AsSpan(offset, alphaBytes));
            return output;
        }

        throw new NotSupportedException($"Unsupported IPicture implementation: {picture.GetType().FullName}.");
    }

    public static IPicture DecodePicture(AIPictureDescriptor descriptor, ReadOnlySpan<byte> payload)
    {
        if (descriptor.Width <= 0 || descriptor.Height <= 0)
            throw new AIComponentClientException("The extension returned an invalid picture size.");

        int pixels = checked(descriptor.Width * descriptor.Height);
        int sampleBytes = descriptor.BitDepth == AIPictureBitDepth.Byte ? sizeof(byte) : sizeof(ushort);
        int planeBytes = checked(pixels * sampleBytes);
        int alphaBytes = descriptor.HasAlpha ? checked(pixels * sizeof(float)) : 0;
        int expectedLength = checked(planeBytes * 3 + alphaBytes);
        if (payload.Length != expectedLength)
            throw new AIComponentClientException($"The extension returned {payload.Length} picture bytes; expected {expectedLength}.");

        int offset = 0;
        if (descriptor.BitDepth == AIPictureBitDepth.Byte)
        {
            var picture = new Picture8bpp(descriptor.Width, descriptor.Height);
            payload.Slice(offset, planeBytes).CopyTo(picture.r); offset += planeBytes;
            payload.Slice(offset, planeBytes).CopyTo(picture.g); offset += planeBytes;
            payload.Slice(offset, planeBytes).CopyTo(picture.b); offset += planeBytes;
            if (descriptor.HasAlpha)
            {
                picture.a = new float[pixels];
                CopyFloatPlane(payload.Slice(offset, alphaBytes), picture.a);
                picture.HasAlphaChannel = true;
            }
            picture.Tag = "AIComponent result";
            return picture;
        }

        var picture16 = new Picture16bpp(descriptor.Width, descriptor.Height);
        CopyUShortPlane(payload.Slice(offset, planeBytes), picture16.r); offset += planeBytes;
        CopyUShortPlane(payload.Slice(offset, planeBytes), picture16.g); offset += planeBytes;
        CopyUShortPlane(payload.Slice(offset, planeBytes), picture16.b); offset += planeBytes;
        if (descriptor.HasAlpha)
        {
            picture16.a = new float[pixels];
            CopyFloatPlane(payload.Slice(offset, alphaBytes), picture16.a);
            picture16.HasAlphaChannel = true;
        }
        picture16.Tag = "AIComponent result";
        return picture16;
    }

    public static byte[] EncodeAudio(IAudioSamples<float> audio, out AIAudioDescriptor descriptor)
    {
        ArgumentNullException.ThrowIfNull(audio);
        if (audio.SamplePerSecond <= 0 || audio.SampleCount < 0 || audio.channelCount <= 0)
            throw new ArgumentException("Audio metadata is invalid.", nameof(audio));

        descriptor = new AIAudioDescriptor
        {
            SampleRate = audio.SamplePerSecond,
            ChannelCount = audio.channelCount,
            SampleCount = audio.SampleCount,
            SampleFormat = AIAudioSampleFormat.Float32,
            Layout = "planar"
        };

        int channelBytes = checked(audio.SampleCount * sizeof(float));
        byte[] output = new byte[checked(channelBytes * audio.channelCount)];
        int offset = 0;
        for (int channel = 0; channel < audio.channelCount; channel++)
        {
            float[] samples = audio.GetSamples(channel);
            if (samples.Length < audio.SampleCount)
                throw new ArgumentException($"Audio channel {channel} is shorter than SampleCount.", nameof(audio));
            MemoryMarshal.AsBytes(samples.AsSpan(0, audio.SampleCount)).CopyTo(output.AsSpan(offset, channelBytes));
            offset += channelBytes;
        }
        return output;
    }

    public static IAudioSamples<float> DecodeAudio(AIAudioDescriptor descriptor, ReadOnlySpan<byte> payload)
    {
        if (descriptor.SampleRate <= 0 || descriptor.SampleCount < 0 || descriptor.ChannelCount <= 0 || descriptor.Layout != "planar")
            throw new AIComponentClientException("The extension returned invalid audio metadata.");
        if (descriptor.SampleFormat != AIAudioSampleFormat.Float32)
            throw new AIComponentClientException("The extension returned an unsupported audio format.");

        int channelBytes = checked(descriptor.SampleCount * sizeof(float));
        int expectedLength = checked(channelBytes * descriptor.ChannelCount);
        if (payload.Length != expectedLength)
            throw new AIComponentClientException($"The extension returned {payload.Length} audio bytes; expected {expectedLength}.");

        float[][] channels = new float[descriptor.ChannelCount][];
        int offset = 0;
        for (int channel = 0; channel < channels.Length; channel++)
        {
            channels[channel] = new float[descriptor.SampleCount];
            CopyFloatPlane(payload.Slice(offset, channelBytes), channels[channel]);
            offset += channelBytes;
        }

        return new FloatAudioSamples
        {
            Channels = channels,
            SampleCount = descriptor.SampleCount,
            SamplePerSecond = descriptor.SampleRate
        };
    }

    private static int ValidatePicture(IPicture picture)
    {
        if (picture.Width <= 0 || picture.Height <= 0)
            throw new ArgumentException("Picture dimensions must be positive.", nameof(picture));
        int pixels = checked(picture.Width * picture.Height);
        if (picture.Pixels != pixels)
            throw new ArgumentException("Picture.Pixels does not match Width * Height.", nameof(picture));
        return pixels;
    }

    private static void ValidatePlane<T>(T[]? plane, int pixels, string name)
    {
        if (plane is null || plane.Length < pixels)
            throw new ArgumentException($"Picture plane '{name}' is missing or too short.", name);
    }

    private static void CopyUShortPlane(ReadOnlySpan<ushort> source, Span<byte> destination)
    {
        if (BitConverter.IsLittleEndian)
        {
            MemoryMarshal.AsBytes(source).CopyTo(destination);
            return;
        }

        for (int i = 0; i < source.Length; i++)
            BinaryPrimitives.WriteUInt16LittleEndian(destination.Slice(i * sizeof(ushort), sizeof(ushort)), source[i]);
    }

    private static void CopyUShortPlane(ReadOnlySpan<byte> source, ushort[] destination)
    {
        if (BitConverter.IsLittleEndian)
        {
            MemoryMarshal.Cast<byte, ushort>(source).CopyTo(destination);
            return;
        }

        for (int i = 0; i < destination.Length; i++)
            destination[i] = BinaryPrimitives.ReadUInt16LittleEndian(source.Slice(i * sizeof(ushort), sizeof(ushort)));
    }

    private static void CopyFloatPlane(ReadOnlySpan<byte> source, float[] destination)
    {
        if (BitConverter.IsLittleEndian)
        {
            MemoryMarshal.Cast<byte, float>(source).CopyTo(destination);
            return;
        }

        for (int i = 0; i < destination.Length; i++)
        {
            int bits = BinaryPrimitives.ReadInt32LittleEndian(source.Slice(i * sizeof(float), sizeof(float)));
            destination[i] = BitConverter.Int32BitsToSingle(bits);
        }
    }
}
