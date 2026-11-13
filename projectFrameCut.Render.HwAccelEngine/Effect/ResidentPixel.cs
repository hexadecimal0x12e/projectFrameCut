using System.Buffers.Binary;
using System.Runtime.InteropServices;

namespace projectFrameCut.Render.HwAccelEngine.Effect;
[StructLayout(LayoutKind.Sequential, Pack = 4)]
internal struct ResidentPixel
{
    internal const int Size = 12;
    public Half Red, Green, Blue, Alpha, Brightness, Padding;
    internal static byte[] PackParameters(float[] values)
    {
        var bytes = MemoryMarshal.AsBytes<float>(values.AsSpan()).ToArray();
        void WriteHalf(int index)
        {
            BinaryPrimitives.WriteHalfLittleEndian(bytes.AsSpan(index * 4, 2), (Half)values[index]);
            bytes[index * 4 + 2] = bytes[index * 4 + 3] = 0;
        }

        WriteHalf(10);
        WriteHalf(11);
        switch ((ResidentOperation)(int)values[0])
        {
            case ResidentOperation.Opacity:
                WriteHalf(15);
                break;
            case ResidentOperation.ColorAdjustment:
                WriteHalf(24);
                break;
            case ResidentOperation.Crossfade:
                WriteHalf(15);
                WriteHalf(16);
                break;
        }

        return bytes;
    }
}
