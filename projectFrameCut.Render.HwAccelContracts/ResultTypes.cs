namespace projectFrameCut.Render.HwAccelContracts;

/// <summary>
/// 四通道 float 结果，用于标准单帧效果计算机。
/// </summary>
public readonly struct FourChannelResult
{
    public float[] R { get; }
    public float[] G { get; }
    public float[] B { get; }
    public float[] A { get; }

    public FourChannelResult(float[] r, float[] g, float[] b, float[] a)
        => (R, G, B, A) = (r, g, b, a);

    public void Deconstruct(out float[] r, out float[] g, out float[] b, out float[] a)
        => (r, g, b, a) = (R, G, B, A);
}

/// <summary>
/// 四通道 byte 结果，用于 8bpp 路径（例如 Resize）。
/// </summary>
public readonly struct FourChannelResult8
{
    public byte[] R { get; }
    public byte[] G { get; }
    public byte[] B { get; }
    public float[] A { get; }

    public FourChannelResult8(byte[] r, byte[] g, byte[] b, float[] a)
        => (R, G, B, A) = (r, g, b, a);
}

/// <summary>
/// 四通道 ushort 结果，用于 16bpp 路径（例如 Resize）。
/// </summary>
public readonly struct FourChannelResult16
{
    public ushort[] R { get; }
    public ushort[] G { get; }
    public ushort[] B { get; }
    public float[] A { get; }

    public FourChannelResult16(ushort[] r, ushort[] g, ushort[] b, float[] a)
        => (R, G, B, A) = (r, g, b, a);
}

/// <summary>
/// 混合/叠加模式的 8bpp 结果。
/// </summary>
public readonly struct BlendResult8
{
    public byte[] Color { get; }
    public float[] Alpha { get; }

    public BlendResult8(byte[] color, float[] alpha)
        => (Color, Alpha) = (color, alpha);
}

/// <summary>
/// 混合/叠加模式的 16bpp 结果。
/// </summary>
public readonly struct BlendResult16
{
    public ushort[] Color { get; }
    public float[] Alpha { get; }

    public BlendResult16(ushort[] color, float[] alpha)
        => (Color, Alpha) = (color, alpha);
}

/// <summary>
/// 混合/叠加模式的 HDR（float）结果。
/// </summary>
public readonly struct BlendResultHdr
{
    public float[] Color { get; }
    public float[] Alpha { get; }

    public BlendResultHdr(float[] color, float[] alpha)
        => (Color, Alpha) = (color, alpha);
}
