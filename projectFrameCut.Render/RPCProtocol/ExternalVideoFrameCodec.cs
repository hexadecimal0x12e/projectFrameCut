using projectFrameCut.Drawing.Base;
using projectFrameCut.Render.Contracts;

namespace projectFrameCut.Render.RPCProtocol;

public static class ExternalVideoFrameCodec
{
    public static ExternalVideoFrame Encode(IPicture picture)
    {
        ArgumentNullException.ThrowIfNull(picture);
        var frame = new ExternalVideoFrame
        {
            Width = picture.Width,
            Height = picture.Height,
            BitsPerChannel = picture.BitPerPixel,
        };
        if (picture is IPicture<byte> p8)
        {
            frame.Red = p8.r.ToArray();
            frame.Green = p8.g.ToArray();
            frame.Blue = p8.b.ToArray();
        }
        else if (picture is IPicture<ushort> p16)
        {
            frame.Red = ToBytes(p16.r);
            frame.Green = ToBytes(p16.g);
            frame.Blue = ToBytes(p16.b);
        }
        else throw new NotSupportedException($"Picture type '{picture.GetType().FullName}' cannot cross the external RPC boundary.");

        if (picture.HasAlphaChannel && picture.GetSpecificChannel(IPicture.ChannelId.Alpha) is float[] alpha) frame.Alpha = ToBytes(alpha);
        if (picture is IHDRPicture<ushort> hdr)
        {
            frame.Brightness = ToBytes(hdr.Brightness);
            frame.MaximumBrightness = hdr.MaximumBrightness;
        }
        frame.Validate();
        return frame;
    }

    private static byte[] ToBytes<T>(T[] values) where T : unmanaged
    {
        var bytes = new byte[checked(values.Length * System.Runtime.InteropServices.Marshal.SizeOf<T>())];
        Buffer.BlockCopy(values, 0, bytes, 0, bytes.Length);
        return bytes;
    }
}
