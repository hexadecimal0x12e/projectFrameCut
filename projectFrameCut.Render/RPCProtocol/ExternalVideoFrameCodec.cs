using projectFrameCut.Drawing.Base;
using projectFrameCut.Render.Contracts;
using projectFrameCut.Render.EncodeAndDecode;

namespace projectFrameCut.Render.RPCProtocol;

public static class ExternalVideoFrameCodec
{
    public static IPicture Decode(ExternalVideoFrame frame)
    {
        frame.Validate();
        return frame.Brightness.Length > 0 ? RemoteRpcVideoSource.ToHdr(frame) : RemoteRpcVideoSource.ToPicture(frame);
    }

    public static ExternalVideoFrame Encode(IPicture picture)
    {
        ArgumentNullException.ThrowIfNull(picture);
        var frame = new ExternalVideoFrame
        {
            Width = picture.Width,
            Height = picture.Height,
            BitsPerChannel = picture.BitPerPixel,
            UsesFloat16 = picture is IPicture<Half>,
        };
        if (picture is IPicture<Half> fp)
        {
            frame.Red = ToBytes(fp.r); frame.Green = ToBytes(fp.g); frame.Blue = ToBytes(fp.b);
        }
        else if (picture is IPicture<byte> p8)
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

        if (picture.HasAlphaChannel && picture.GetSpecificChannel(IPicture.ChannelId.Alpha) is Half[] alpha)
            frame.Alpha = ToBytes(alpha);
        if (picture is IHDRPicture<Half> hf)
        {
            frame.Brightness = ToBytes(hf.Brightness); frame.MaximumBrightness = hf.MaximumBrightness;
        }
        else if (picture is IHDRPicture<ushort> hdr)
        {
            frame.Brightness = ToBytes(hdr.Brightness);
            frame.MaximumBrightness = hdr.MaximumBrightness;
        }
        frame.Validate();
        return frame;
    }

    private static byte[] ToBytes<T>(T[] values) where T : unmanaged
    {
        return System.Runtime.InteropServices.MemoryMarshal.AsBytes<T>(values.AsSpan()).ToArray();
    }
}
