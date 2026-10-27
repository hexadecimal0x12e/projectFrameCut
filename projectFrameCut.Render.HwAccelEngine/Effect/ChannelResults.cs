namespace projectFrameCut.Render.HwAccelEngine.Effect;
internal readonly struct FourChannelResult
{
    public float[] R { get; }
    public float[] G { get; }
    public float[] B { get; }
    public Half[] A { get; }

    public FourChannelResult(float[] r, float[] g, float[] b, Half[] a) => (R, G, B, A) = (r, g, b, a);
    public void Deconstruct(out float[] r, out float[] g, out float[] b, out Half[] a) => (r, g, b, a) = (R, G, B, A);
}

internal readonly struct FourChannelResult8
{
    public byte[] R { get; }
    public byte[] G { get; }
    public byte[] B { get; }
    public Half[] A { get; }

    public FourChannelResult8(byte[] r, byte[] g, byte[] b, Half[] a) => (R, G, B, A) = (r, g, b, a);
}

internal readonly struct FourChannelResult16
{
    public ushort[] R { get; }
    public ushort[] G { get; }
    public ushort[] B { get; }
    public Half[] A { get; }

    public FourChannelResult16(ushort[] r, ushort[] g, ushort[] b, Half[] a) => (R, G, B, A) = (r, g, b, a);
}
