using System.Buffers;
using System.Numerics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace projectFrameCut.Render.Compose;

internal enum CompositionMode
{
    Normal, Add, Subtract, Multiply, Screen, Overlay, Darken, Lighten, Difference
}

internal static class PictureCompositor
{
    internal delegate (Array Color, float[] Alpha) ChannelBlend(float[] top, float[] bottom, float[] topAlpha, float[] bottomAlpha, int count);

    internal static IPicture Mix(IPicture bottom, IPicture top, IPicture.PicturePixelMode pixelMode,
        int x, int y, int width, int height, CompositionMode mode, ChannelBlend? blend = null, ChannelBlend? blendBrightness = null)
    {
        var b = new Channels(bottom);
        var t = new Channels(top);
        int pixels = checked(width * height);
        bool alpha = bottom.HasAlphaChannel || top.HasAlphaChannel || bottom.Width != width || bottom.Height != height;
        bool hdr = (int)pixelMode != 8 && (!b.Brightness.IsEmpty || !t.Brightness.IsEmpty);
        float peak = b.Brightness.IsEmpty ? t.MaximumBrightness : t.Brightness.IsEmpty
            ? b.MaximumBrightness : Math.Max(b.MaximumBrightness, t.MaximumBrightness);

        int left = Math.Clamp(x, 0, width);
        int upper = Math.Clamp(y, 0, height);
        int right = (int)Math.Clamp((long)x + top.Width, 0, width);
        int lower = (int)Math.Clamp((long)y + top.Height, 0, height);
        bool covered = mode == CompositionMode.Normal && t.Alpha.IsEmpty
            && left == 0 && upper == 0 && right == width && lower == height;
        if (covered && !alpha && !hdr && top is not IHDRPicture<ushort> && top is ISharedPicture shared
            && x == 0 && y == 0 && top.Width == width && top.Height == height && (int)top.BitPerPixel == (int)pixelMode)
            return shared.Snapshot();
        bool readBase = blend is null && !covered && left == 0 && upper == 0 && right == width && lower == height
            && bottom.Width == width && bottom.Height == height && (int)bottom.BitPerPixel == (int)pixelMode;
        var background = readBase ? b : default;
        float[]? a = alpha ? new float[pixels] : null;
        float[]? brightness = hdr ? new float[pixels] : null;
        if ((int)pixelMode == 8)
        {
            var r = new byte[pixels];
            var g = new byte[pixels];
            var blue = new byte[pixels];
            if (!covered && !readBase) FillBase(b, bottom.Width, bottom.Height, width, height, r, g, blue, a, brightness);
            Compose(t, top.Width, x, y, width, left, upper, right, lower, r, g, blue, a, brightness, mode, blend, blendBrightness, background);
            return Picture8bpp.FromOwnedChannels(width, height, r, g, blue, a);
        }
        else
        {
            var r = new ushort[pixels];
            var g = new ushort[pixels];
            var blue = new ushort[pixels];
            if (!covered && !readBase) FillBase(b, bottom.Width, bottom.Height, width, height, r, g, blue, a, brightness);
            Compose(t, top.Width, x, y, width, left, upper, right, lower, r, g, blue, a, brightness, mode, blend, blendBrightness, background);
            return brightness is null
                ? Picture16bpp.FromOwnedChannels(width, height, r, g, blue, a)
                : HDRPicture16bpp.FromOwnedChannels(width, height, r, g, blue, a, brightness, peak);
        }
    }

    private static void FillBase<T>(Channels source, int sourceWidth, int sourceHeight, int width, int height,
        T[] r, T[] g, T[] b, float[]? alpha, float[]? brightness) where T : unmanaged
    {
        int copyWidth = Math.Min(sourceWidth, width);
        int copyHeight = Math.Min(sourceHeight, height);
        if (sourceWidth == width)
        {
            Copy(source, 0, 0, checked(copyWidth * copyHeight), r, g, b, alpha, brightness);
            return;
        }
        for (int y = 0; y < copyHeight; y++)
            Copy(source, y * sourceWidth, y * width, copyWidth, r, g, b, alpha, brightness);
    }

    private static void Copy<T>(Channels source, int src, int dst, int count,
        T[] r, T[] g, T[] b, float[]? alpha, float[]? brightness) where T : unmanaged
    {
        CopyColor(source.R8, source.R16, src, r.AsSpan(dst, count));
        CopyColor(source.G8, source.G16, src, g.AsSpan(dst, count));
        CopyColor(source.B8, source.B16, src, b.AsSpan(dst, count));
        if (alpha is not null)
        {
            if (source.Alpha.IsEmpty) alpha.AsSpan(dst, count).Fill(1f);
            else SIMDAlphaProcessor.ClampAlphaOffset(source.Alpha, alpha, src, dst, count);
        }
        if (brightness is not null)
        {
            if (!source.Brightness.IsEmpty)
                SIMDAlphaProcessor.ClampAlphaOffset(source.Brightness, brightness, src, dst, count);
            else
            {
                int i = 0;
                int readWidth = source.R8.IsEmpty ? Vector<ushort>.Count : Vector<byte>.Count;
                for (; i <= count - readWidth; i += Vector<float>.Count)
                {
                    var value = (Load(source.R8, source.R16, src + i) * new Vector<float>(0.2627f)
                        + Load(source.G8, source.G16, src + i) * new Vector<float>(0.6780f)
                        + Load(source.B8, source.B16, src + i) * new Vector<float>(0.0593f)) / new Vector<float>(65535f);
                    Vector.Min(value, Vector<float>.One).CopyTo(brightness, dst + i);
                }
                for (; i < count; i++)
                    brightness[dst + i] = EstimateBrightness(source.ReadRed(src + i), source.ReadGreen(src + i), source.ReadBlue(src + i));
            }
        }
    }

    private static void CopyColor<T>(ReadOnlySpan<byte> bytes, ReadOnlySpan<ushort> words, int src, Span<T> dst) where T : unmanaged
    {
        if (typeof(T) == typeof(byte))
        {
            var output = MemoryMarshal.Cast<T, byte>(dst);
            if (!bytes.IsEmpty) bytes.Slice(src, dst.Length).CopyTo(output);
            else
                for (int i = 0; i < dst.Length; i++) output[i] = (byte)(words[src + i] / 257);
        }
        else
        {
            var output = MemoryMarshal.Cast<T, ushort>(dst);
            if (bytes.IsEmpty) words.Slice(src, dst.Length).CopyTo(output);
            else
            {
                int i = 0;
                var scale = new Vector<ushort>(257);
                for (; i <= dst.Length - Vector<byte>.Count; i += Vector<byte>.Count)
                {
                    Vector.Widen(new Vector<byte>(bytes.Slice(src + i)), out var lo, out var hi);
                    (lo * scale).CopyTo(output.Slice(i));
                    (hi * scale).CopyTo(output.Slice(i + Vector<ushort>.Count));
                }
                for (; i < dst.Length; i++) output[i] = (ushort)(bytes[src + i] * 257);
            }
        }
    }

    private static void Compose<T>(Channels top, int topWidth, int x, int y, int width,
        int left, int upper, int right, int lower, T[] r, T[] g, T[] b, float[]? alpha,
        float[]? brightness, CompositionMode mode, ChannelBlend? blend, ChannelBlend? blendBrightness, Channels bottom) where T : unmanaged
    {
        if (right <= left || lower <= upper) return;
        if (blend is not null && (mode != CompositionMode.Normal || !top.Alpha.IsEmpty))
        {
            BlendLegacy(top, topWidth, x, y, width, left, upper, right, lower, r, g, b, alpha, brightness, mode, blend, blendBrightness);
            return;
        }
        int count = right - left;
        if (count == width && count == topWidth)
        {
            BlendRow(top, (upper - y) * topWidth, upper * width, checked(count * (lower - upper)),
                r, g, b, alpha, brightness, mode, bottom);
            return;
        }
        for (int row = upper; row < lower; row++)
            BlendRow(top, (row - y) * topWidth + left - x, row * width + left, count,
                r, g, b, alpha, brightness, mode, bottom);
    }

    private static void BlendLegacy<T>(Channels top, int topWidth, int x, int y, int width,
        int left, int upper, int right, int lower, T[] r, T[] g, T[] b, float[]? alpha,
        float[]? brightness, CompositionMode mode, ChannelBlend blend, ChannelBlend? blendBrightness) where T : unmanaged
    {
        // Keep existing channel overrides, with bounded scratch space regardless of frame size.
        int capacity = Math.Min(65536, checked((right - left) * (lower - upper)));
        BlendBuffers? buffers = null;
        int count = 0;
        try
        {
            for (int row = upper; row < lower; row++)
            {
                int src = (row - y) * topWidth + left - x, dst = row * width + left;
                for (int col = left; col < right; col++, src++, dst++)
                {
                    float ta = top.Alpha.IsEmpty ? 1f : Clamp01(top.Alpha[src]);
                    if (ta <= (mode == CompositionMode.Normal ? 0.05f : 0f)) continue;
                    float tr = top.ReadRed(src), tg = top.ReadGreen(src), tb = top.ReadBlue(src);
                    if (mode == CompositionMode.Normal && ta >= 0.999f)
                    {
                        r[dst] = ToPixel<T>(tr); g[dst] = ToPixel<T>(tg); b[dst] = ToPixel<T>(tb);
                        if (alpha is not null) alpha[dst] = 1f;
                        if (brightness is not null) brightness[dst] = top.Brightness.IsEmpty ? EstimateBrightness(tr, tg, tb) : Clamp01(top.Brightness[src]);
                        continue;
                    }
                    buffers ??= new BlendBuffers(capacity, brightness is not null);
                    buffers.Indices[count] = dst;
                    buffers.TopR[count] = tr; buffers.TopG[count] = tg; buffers.TopB[count] = tb;
                    buffers.BottomR[count] = FromPixel(r[dst]);
                    buffers.BottomG[count] = FromPixel(g[dst]);
                    buffers.BottomB[count] = FromPixel(b[dst]);
                    buffers.TopA[count] = ta;
                    buffers.BottomA[count] = alpha is null ? 1f : alpha[dst];
                    if (brightness is not null)
                    {
                        buffers.TopBrightness![count] = top.Brightness.IsEmpty ? EstimateBrightness(tr, tg, tb) : Clamp01(top.Brightness[src]);
                        buffers.BottomBrightness![count] = brightness[dst];
                    }
                    if (++count == capacity)
                    {
                        Flush(buffers, count, r, g, b, alpha, brightness, blend, blendBrightness);
                        count = 0;
                    }
                }
            }
            if (count > 0) Flush(buffers!, count, r, g, b, alpha, brightness, blend, blendBrightness);
        }
        finally
        {
            buffers?.Dispose();
        }
    }

    private static void Flush<T>(BlendBuffers buffers, int count, T[] r, T[] g, T[] b, float[]? alpha,
        float[]? brightness, ChannelBlend blend, ChannelBlend? blendBrightness) where T : unmanaged
    {
        var red = blend(buffers.TopR, buffers.BottomR, buffers.TopA, buffers.BottomA, count);
        var green = blend(buffers.TopG, buffers.BottomG, buffers.TopA, buffers.BottomA, count);
        var blue = blend(buffers.TopB, buffers.BottomB, buffers.TopA, buffers.BottomA, count);
        for (int i = 0; i < count; i++)
        {
            int dst = buffers.Indices[i];
            r[dst] = ToPixel<T>(ReadResult(red.Color, i));
            g[dst] = ToPixel<T>(ReadResult(green.Color, i));
            b[dst] = ToPixel<T>(ReadResult(blue.Color, i));
            if (alpha is not null) alpha[dst] = Clamp01(red.Alpha[i]);
        }
        if (brightness is null) return;
        if (blendBrightness is not null)
        {
            var result = blendBrightness(buffers.TopBrightness!, buffers.BottomBrightness!, buffers.TopA, buffers.BottomA, count);
            var values = (float[])result.Color;
            for (int i = 0; i < count; i++) brightness[buffers.Indices[i]] = Clamp01(values[i]);
        }
        else
            for (int i = 0; i < count; i++)
            {
                float bw = buffers.BottomA[i] * (1f - buffers.TopA[i]), a = buffers.TopA[i] + bw;
                brightness[buffers.Indices[i]] = a < 1e-6f ? 0f
                    : (buffers.TopBrightness![i] * buffers.TopA[i] + buffers.BottomBrightness![i] * bw) / a;
            }
    }

    private static float ReadResult(Array values, int i) => values switch
    {
        byte[] bytes => bytes[i] * 257f,
        ushort[] words => words[i],
        float[] floats => floats[i],
        _ => throw new InvalidOperationException("Invalid composition output channel type.")
    };

    private sealed class BlendBuffers : IDisposable
    {
        internal readonly int[] Indices;
        internal readonly float[] TopR, TopG, TopB, BottomR, BottomG, BottomB, TopA, BottomA;
        internal readonly float[]? TopBrightness, BottomBrightness;

        internal BlendBuffers(int count, bool hdr)
        {
            Indices = ArrayPool<int>.Shared.Rent(count);
            TopR = ArrayPool<float>.Shared.Rent(count);
            TopG = ArrayPool<float>.Shared.Rent(count);
            TopB = ArrayPool<float>.Shared.Rent(count);
            BottomR = ArrayPool<float>.Shared.Rent(count);
            BottomG = ArrayPool<float>.Shared.Rent(count);
            BottomB = ArrayPool<float>.Shared.Rent(count);
            TopA = ArrayPool<float>.Shared.Rent(count);
            BottomA = ArrayPool<float>.Shared.Rent(count);
            if (hdr)
            {
                TopBrightness = ArrayPool<float>.Shared.Rent(count);
                BottomBrightness = ArrayPool<float>.Shared.Rent(count);
            }
        }

        public void Dispose()
        {
            ArrayPool<int>.Shared.Return(Indices);
            ArrayPool<float>.Shared.Return(TopR);
            ArrayPool<float>.Shared.Return(TopG);
            ArrayPool<float>.Shared.Return(TopB);
            ArrayPool<float>.Shared.Return(BottomR);
            ArrayPool<float>.Shared.Return(BottomG);
            ArrayPool<float>.Shared.Return(BottomB);
            ArrayPool<float>.Shared.Return(TopA);
            ArrayPool<float>.Shared.Return(BottomA);
            if (TopBrightness is not null) ArrayPool<float>.Shared.Return(TopBrightness);
            if (BottomBrightness is not null) ArrayPool<float>.Shared.Return(BottomBrightness);
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveOptimization)]
    private static void BlendRow<T>(Channels top, int src, int dst, int count,
        T[] r, T[] g, T[] b, float[]? alpha, float[]? brightness, CompositionMode mode, Channels bottom) where T : unmanaged
    {
        if (mode == CompositionMode.Normal && top.Alpha.IsEmpty)
        {
            Copy(top, src, dst, count, r, g, b, alpha, brightness);
            return;
        }

        int i = 0;
        bool readBase = !bottom.R8.IsEmpty || !bottom.R16.IsEmpty;
        int readWidth = Math.Max(top.R8.IsEmpty ? Vector<ushort>.Count : Vector<byte>.Count, Vector<T>.Count);
        var zero = Vector<float>.Zero;
        var one = Vector<float>.One;
        var minAlpha = new Vector<float>(mode == CompositionMode.Normal ? 0.05f : 0f);
        var opaqueAlpha = new Vector<float>(0.999f);
        // RGB shares alpha and weights; widened integer loads and packed stores need no staging arrays.
        for (; Vector.IsHardwareAccelerated && i <= count - readWidth; i += Vector<float>.Count)
        {
            int s = src + i, d = dst + i;
            var ta = top.Alpha.IsEmpty ? one : Clamp01(new Vector<float>(top.Alpha.Slice(s)));
            if (Vector.LessThanOrEqualAll(ta, minAlpha))
            {
                if (readBase) Copy(bottom, d, d, Vector<float>.Count, r, g, b, alpha, brightness);
                continue;
            }
            if (mode == CompositionMode.Normal && Vector.GreaterThanOrEqualAll(ta, opaqueAlpha))
            {
                Copy(top, s, d, Vector<float>.Count, r, g, b, null, brightness);
                if (alpha is not null) alpha.AsSpan(d, Vector<float>.Count).Fill(1f);
                continue;
            }

            var ba = readBase ? (bottom.Alpha.IsEmpty ? one : Clamp01(new Vector<float>(bottom.Alpha.Slice(d))))
                : alpha is null ? one : new Vector<float>(alpha, d);
            var bw = ba * (one - ta);
            var a = ta + bw;
            var denominator = Vector.Max(a, new Vector<float>(1e-6f));
            var tr = Load(top.R8, top.R16, s);
            var tg = Load(top.G8, top.G16, s);
            var tb = Load(top.B8, top.B16, s);
            var br = readBase ? Load(bottom.R8, bottom.R16, d) : Load(r, d);
            var bg = readBase ? Load(bottom.G8, bottom.G16, d) : Load(g, d);
            var bb = readBase ? Load(bottom.B8, bottom.B16, d) : Load(b, d);
            var transparent = Vector.LessThanOrEqual(ta, minAlpha);
            var opaque = mode == CompositionMode.Normal ? Vector.GreaterThanOrEqual(ta, opaqueAlpha) : Vector<int>.Zero;
            Store(r, d, SelectColor(mode, tr, br, ta, bw, a, transparent, opaque));
            Store(g, d, SelectColor(mode, tg, bg, ta, bw, a, transparent, opaque));
            Store(b, d, SelectColor(mode, tb, bb, ta, bw, a, transparent, opaque));
            if (alpha is not null)
                Vector.ConditionalSelect(transparent, ba, Vector.ConditionalSelect(opaque, one,
                    Vector.ConditionalSelect(Vector.GreaterThanOrEqual(a, new Vector<float>(1e-6f)), a, zero))).CopyTo(alpha, d);
            if (brightness is not null)
            {
                var tBrightness = top.Brightness.IsEmpty
                    ? (tr * new Vector<float>(0.2627f) + tg * new Vector<float>(0.6780f) + tb * new Vector<float>(0.0593f)) / new Vector<float>(65535f)
                    : Clamp01(new Vector<float>(top.Brightness.Slice(s)));
                var bBrightness = readBase
                    ? bottom.Brightness.IsEmpty
                        ? (br * new Vector<float>(0.2627f) + bg * new Vector<float>(0.6780f) + bb * new Vector<float>(0.0593f)) / new Vector<float>(65535f)
                        : Clamp01(new Vector<float>(bottom.Brightness.Slice(d)))
                    : new Vector<float>(brightness, d);
                Vector.ConditionalSelect(transparent, bBrightness, Vector.ConditionalSelect(opaque, tBrightness,
                    Vector.ConditionalSelect(Vector.GreaterThanOrEqual(a, new Vector<float>(1e-6f)),
                        (tBrightness * ta + bBrightness * bw) / denominator, zero))).CopyTo(brightness, d);
            }
        }

        for (; i < count; i++)
        {
            int s = src + i, d = dst + i;
            float ta = top.Alpha.IsEmpty ? 1f : Clamp01(top.Alpha[s]);
            if (ta <= (mode == CompositionMode.Normal ? 0.05f : 0f))
            {
                if (readBase) Copy(bottom, d, d, 1, r, g, b, alpha, brightness);
                continue;
            }
            float tr = top.ReadRed(s), tg = top.ReadGreen(s), tb = top.ReadBlue(s);
            if (mode == CompositionMode.Normal && ta >= 0.999f)
            {
                r[d] = ToPixel<T>(tr);
                g[d] = ToPixel<T>(tg);
                b[d] = ToPixel<T>(tb);
                if (alpha is not null) alpha[d] = 1f;
                if (brightness is not null) brightness[d] = top.Brightness.IsEmpty ? EstimateBrightness(tr, tg, tb) : Clamp01(top.Brightness[s]);
                continue;
            }
            float ba = readBase ? (bottom.Alpha.IsEmpty ? 1f : Clamp01(bottom.Alpha[d])) : alpha is null ? 1f : alpha[d];
            float bw = ba * (1f - ta), a = ta + bw;
            if (a < 1e-6f)
            {
                r[d] = g[d] = b[d] = default;
                if (alpha is not null) alpha[d] = 0f;
                if (brightness is not null) brightness[d] = 0f;
                continue;
            }
            float br = readBase ? bottom.ReadRed(d) : FromPixel(r[d]);
            float bg = readBase ? bottom.ReadGreen(d) : FromPixel(g[d]);
            float bb = readBase ? bottom.ReadBlue(d) : FromPixel(b[d]);
            r[d] = ToPixel<T>((BlendColor(mode, tr, br) * ta + br * bw) / a);
            g[d] = ToPixel<T>((BlendColor(mode, tg, bg) * ta + bg * bw) / a);
            b[d] = ToPixel<T>((BlendColor(mode, tb, bb) * ta + bb * bw) / a);
            if (alpha is not null) alpha[d] = a;
            if (brightness is not null)
            {
                float bBrightness = readBase ? bottom.Brightness.IsEmpty ? EstimateBrightness(br, bg, bb) : Clamp01(bottom.Brightness[d]) : brightness[d];
                brightness[d] = ((top.Brightness.IsEmpty ? EstimateBrightness(tr, tg, tb) : Clamp01(top.Brightness[s])) * ta + bBrightness * bw) / a;
            }
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static Vector<float> SelectColor(CompositionMode mode, Vector<float> top, Vector<float> bottom,
        Vector<float> ta, Vector<float> bw, Vector<float> a, Vector<int> transparent, Vector<int> opaque) =>
        Vector.ConditionalSelect(transparent, bottom, Vector.ConditionalSelect(opaque, top,
            Vector.ConditionalSelect(Vector.GreaterThanOrEqual(a, new Vector<float>(1e-6f)),
                (BlendColor(mode, top, bottom) * ta + bottom * bw) / Vector.Max(a, new Vector<float>(1e-6f)), Vector<float>.Zero)));

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static float BlendColor(CompositionMode mode, float top, float bottom) => mode switch
    {
        CompositionMode.Add => Math.Min(top + bottom, 65535f),
        CompositionMode.Subtract => Math.Max(bottom - top, 0f),
        CompositionMode.Multiply => top * bottom / 65535f,
        CompositionMode.Screen => 65535f - (65535f - top) * (65535f - bottom) / 65535f,
        CompositionMode.Overlay => bottom < 32768f ? 2f * top * bottom / 65535f : 65535f - 2f * (65535f - top) * (65535f - bottom) / 65535f,
        CompositionMode.Darken => Math.Min(top, bottom),
        CompositionMode.Lighten => Math.Max(top, bottom),
        CompositionMode.Difference => Math.Abs(top - bottom),
        _ => top
    };

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static Vector<float> BlendColor(CompositionMode mode, Vector<float> top, Vector<float> bottom)
    {
        var max = new Vector<float>(65535f);
        return mode switch
        {
            CompositionMode.Add => Vector.Min(top + bottom, max),
            CompositionMode.Subtract => Vector.Max(bottom - top, Vector<float>.Zero),
            CompositionMode.Multiply => top * bottom / max,
            CompositionMode.Screen => max - (max - top) * (max - bottom) / max,
            CompositionMode.Overlay => Vector.ConditionalSelect(Vector.LessThan(bottom, new Vector<float>(32768f)),
                new Vector<float>(2f) * top * bottom / max, max - new Vector<float>(2f) * (max - top) * (max - bottom) / max),
            CompositionMode.Darken => Vector.Min(top, bottom),
            CompositionMode.Lighten => Vector.Max(top, bottom),
            CompositionMode.Difference => Vector.Abs(top - bottom),
            _ => top
        };
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static Vector<float> Load(ReadOnlySpan<byte> bytes, ReadOnlySpan<ushort> words, int index)
    {
        if (!bytes.IsEmpty)
        {
            Vector.Widen(new Vector<byte>(bytes.Slice(index)), out var lo, out _);
            Vector.Widen(lo, out var values, out _);
            return Vector.ConvertToSingle(values) * new Vector<float>(257f);
        }
        Vector.Widen(new Vector<ushort>(words.Slice(index)), out var wide, out _);
        return Vector.ConvertToSingle(wide);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static Vector<float> Load<T>(T[] channel, int index) where T : unmanaged => typeof(T) == typeof(byte)
        ? Load(MemoryMarshal.Cast<T, byte>(channel.AsSpan()), default, index)
        : Load(default, MemoryMarshal.Cast<T, ushort>(channel.AsSpan()), index);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void Store<T>(T[] channel, int index, Vector<float> values) where T : unmanaged
    {
        values = Vector.Min(Vector.Max(values, Vector<float>.Zero), new Vector<float>(65535f));
        if (typeof(T) == typeof(byte)) values /= new Vector<float>(257f);
        var words = Vector.Narrow(Vector.AsVectorUInt32(Vector.ConvertToInt32(values)), Vector<uint>.Zero);
        if (typeof(T) == typeof(byte))
        {
            var bytes = Vector.Narrow(words, Vector<ushort>.Zero);
            Unsafe.CopyBlockUnaligned(ref Unsafe.As<T, byte>(ref channel[index]), ref Unsafe.As<Vector<byte>, byte>(ref bytes), (uint)Vector<float>.Count);
        }
        else
            Unsafe.CopyBlockUnaligned(ref Unsafe.As<T, byte>(ref channel[index]), ref Unsafe.As<Vector<ushort>, byte>(ref words), (uint)(Vector<float>.Count * sizeof(ushort)));
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static float FromPixel<T>(T value) where T : unmanaged => typeof(T) == typeof(byte)
        ? Unsafe.As<T, byte>(ref value) * 257f : Unsafe.As<T, ushort>(ref value);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static T ToPixel<T>(float value) where T : unmanaged
    {
        if (typeof(T) == typeof(byte))
        {
            byte b = (byte)Math.Clamp(value / 257f, 0f, 255f);
            return Unsafe.As<byte, T>(ref b);
        }
        ushort u = (ushort)Math.Clamp(value, 0f, 65535f);
        return Unsafe.As<ushort, T>(ref u);
    }

    private static float Clamp01(float value) => float.IsFinite(value) ? Math.Clamp(value, 0f, 1f) : 0f;
    private static Vector<float> Clamp01(Vector<float> value) => Vector.ConditionalSelect(
        Vector.LessThanOrEqual(Vector.Abs(value), new Vector<float>(float.MaxValue)),
        Vector.Min(Vector.Max(value, Vector<float>.Zero), Vector<float>.One), Vector<float>.Zero);
    private static float EstimateBrightness(float r, float g, float b) => Clamp01((0.2627f * r + 0.6780f * g + 0.0593f * b) / 65535f);

    private readonly ref struct Channels
    {
        internal readonly ReadOnlySpan<byte> R8, G8, B8;
        internal readonly ReadOnlySpan<ushort> R16, G16, B16;
        internal readonly ReadOnlySpan<float> Alpha, Brightness;
        internal readonly float MaximumBrightness;

        internal Channels(IPicture picture)
        {
            ObjectDisposedException.ThrowIf(picture.Disposed, picture);
            int pixels = checked(picture.Width * picture.Height);
            bool valid;
            if (picture is IPicture<byte> p8)
            {
                var read = new PictureReadChannels<byte>(p8);
                R8 = read.Red; G8 = read.Green; B8 = read.Blue;
                Alpha = picture.HasAlphaChannel ? read.Alpha : default;
                valid = R8.Length == pixels && G8.Length == pixels && B8.Length == pixels;
            }
            else if (picture is IPicture<ushort> p16)
            {
                var read = new PictureReadChannels<ushort>(p16);
                R16 = read.Red; G16 = read.Green; B16 = read.Blue;
                Alpha = picture.HasAlphaChannel ? read.Alpha : default;
                valid = R16.Length == pixels && G16.Length == pixels && B16.Length == pixels;
                if (picture is IHDRPicture<ushort> hdr)
                {
                    ReadOnlySpan<float> brightness = picture is IReadOnlyPictureChannels<ushort> shared ? shared.ReadBrightness : hdr.Brightness;
                    if (brightness.Length == pixels) Brightness = brightness;
                    MaximumBrightness = float.IsFinite(hdr.MaximumBrightness) && hdr.MaximumBrightness > 0 ? hdr.MaximumBrightness : 1000f;
                }
            }
            else throw new NotSupportedException($"Unsupported composition picture type: {picture.GetType().Name}.");

            if (!valid || picture.Width <= 0 || picture.Height <= 0 || picture.Pixels != pixels
                || picture.HasAlphaChannel && Alpha.Length != pixels)
            {
                Log($"[PictureCompositor] Invalid channels: {picture.GetType().Name}, {picture.Width}x{picture.Height}.");
                throw new InvalidDataException("Pictures are invalid.");
            }
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        internal float ReadRed(int index) => R8.IsEmpty ? R16[index] : R8[index] * 257f;
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        internal float ReadGreen(int index) => G8.IsEmpty ? G16[index] : G8[index] * 257f;
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        internal float ReadBlue(int index) => B8.IsEmpty ? B16[index] : B8[index] * 257f;
    }
}
