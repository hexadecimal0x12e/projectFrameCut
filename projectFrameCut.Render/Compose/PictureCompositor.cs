using projectFrameCut.Render.Effect;
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
    internal delegate (Array Color, Half[] Alpha) ChannelBlend(float[] top, float[] bottom, Half[] topAlpha, Half[] bottomAlpha, int count);
    internal delegate (Half[] Color, Half[] Alpha) HalfChannelBlend(Half[] top, Half[] bottom, Half[] topAlpha, Half[] bottomAlpha, int count);

    internal static IPicture Mix<TPicture>(TPicture bottom, TPicture top, IPicture.PicturePixelMode pixelMode,
        int x, int y, int width, int height, CompositionMode mode, ChannelBlend? blend = null, HalfChannelBlend? blendBrightness = null) where TPicture : IPicture
    {
        if (bottom is IPicture<Half> fpBottom && top is IPicture<Half> fpTop)
        {
            if (blend is not null) throw new NotSupportedException($"Integer channel blend {blend.Method.DeclaringType?.FullName} cannot process FP16 pictures.");
            return projectFrameCut.Drawing.Processing.Composing.FP16Composer.Compose(fpBottom, fpTop, mode switch
            {
                CompositionMode.Normal => projectFrameCut.Drawing.Processing.Composing.BlendMode.Overlay,
                CompositionMode.Overlay => projectFrameCut.Drawing.Processing.Composing.BlendMode.OverlayBlend,
                _ => Enum.Parse<projectFrameCut.Drawing.Processing.Composing.BlendMode>(mode.ToString()),
            }, x, y, width, height);
        }
        var b = Channels.From(bottom);
        var t = Channels.From(top);
        int pixels = checked(width * height);
        bool alpha = bottom.HasAlphaChannel || top.HasAlphaChannel || bottom.Width != width || bottom.Height != height;
        bool hdr = (int)pixelMode != 8 && (!b.Brightness.IsEmpty || !t.Brightness.IsEmpty);
        Half peak = b.Brightness.IsEmpty ? t.MaximumBrightness : t.Brightness.IsEmpty
            ? b.MaximumBrightness : Half.Max(b.MaximumBrightness, t.MaximumBrightness);

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
        Half[]? a = alpha ? new Half[pixels] : null;
        Half[]? brightness = hdr ? new Half[pixels] : null;
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
        T[] r, T[] g, T[] b, Half[]? alpha, Half[]? brightness) where T : unmanaged
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
        T[] r, T[] g, T[] b, Half[]? alpha, Half[]? brightness) where T : unmanaged
    {
        CopyColor(source.R8, source.R16, src, r.AsSpan(dst, count));
        CopyColor(source.G8, source.G16, src, g.AsSpan(dst, count));
        CopyColor(source.B8, source.B16, src, b.AsSpan(dst, count));
        if (alpha is not null)
        {
            if (source.Alpha.IsEmpty) alpha.AsSpan(dst, count).Fill(Half.One);
            else SIMDAlphaProcessor.ClampAlphaOffset(source.Alpha, alpha, src, dst, count);
        }
        if (brightness is not null)
        {
            if (!source.Brightness.IsEmpty)
                SIMDAlphaProcessor.ClampAlphaOffset(source.Brightness, brightness, src, dst, count);
            else
                for (int i = 0; i < count; i++)
                    brightness[dst + i] = EstimateBrightness(source.ReadRed(src + i), source.ReadGreen(src + i), source.ReadBlue(src + i));
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
        int left, int upper, int right, int lower, T[] r, T[] g, T[] b, Half[]? alpha,
        Half[]? brightness, CompositionMode mode, ChannelBlend? blend, HalfChannelBlend? blendBrightness, Channels bottom) where T : unmanaged
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
        int left, int upper, int right, int lower, T[] r, T[] g, T[] b, Half[]? alpha,
        Half[]? brightness, CompositionMode mode, ChannelBlend blend, HalfChannelBlend? blendBrightness) where T : unmanaged
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
                    Half ta = top.Alpha.IsEmpty ? Half.One : Clamp01(top.Alpha[src]);
                    if (ta <= (mode == CompositionMode.Normal ? (Half)0.05f : Half.Zero)) continue;
                    float tr = top.ReadRed(src), tg = top.ReadGreen(src), tb = top.ReadBlue(src);
                    if (mode == CompositionMode.Normal && ta >= (Half)0.999f)
                    {
                        r[dst] = ToPixel<T>(tr); g[dst] = ToPixel<T>(tg); b[dst] = ToPixel<T>(tb);
                        if (alpha is not null) alpha[dst] = Half.One;
                        if (brightness is not null) brightness[dst] = (Half)(top.Brightness.IsEmpty ? EstimateBrightness(tr, tg, tb) : Clamp01(top.Brightness[src]));
                        continue;
                    }
                    buffers ??= new BlendBuffers(capacity, brightness is not null);
                    buffers.Indices[count] = dst;
                    buffers.TopR[count] = tr; buffers.TopG[count] = tg; buffers.TopB[count] = tb;
                    buffers.BottomR[count] = FromPixel(r[dst]);
                    buffers.BottomG[count] = FromPixel(g[dst]);
                    buffers.BottomB[count] = FromPixel(b[dst]);
                    buffers.TopA[count] = ta;
                    buffers.BottomA[count] = alpha is null ? Half.One : alpha[dst];
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

    private static void Flush<T>(BlendBuffers buffers, int count, T[] r, T[] g, T[] b, Half[]? alpha,
        Half[]? brightness, ChannelBlend blend, HalfChannelBlend? blendBrightness) where T : unmanaged
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
            var values = result.Color;
            for (int i = 0; i < count; i++) brightness[buffers.Indices[i]] = Clamp01(values[i]);
        }
        else
            for (int i = 0; i < count; i++)
            {
                Half bw = buffers.BottomA[i] * (Half.One - buffers.TopA[i]), a = buffers.TopA[i] + bw;
                brightness[buffers.Indices[i]] = (a < (Half)1e-6f ? Half.Zero
                    : (buffers.TopBrightness![i] * buffers.TopA[i] + buffers.BottomBrightness![i] * bw) / a);
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
        internal readonly float[] TopR, TopG, TopB, BottomR, BottomG, BottomB;
        internal readonly Half[] TopA, BottomA;
        internal readonly Half[]? TopBrightness, BottomBrightness;

        internal BlendBuffers(int count, bool hdr)
        {
            Indices = ArrayPool<int>.Shared.Rent(count);
            TopR = ArrayPool<float>.Shared.Rent(count);
            TopG = ArrayPool<float>.Shared.Rent(count);
            TopB = ArrayPool<float>.Shared.Rent(count);
            BottomR = ArrayPool<float>.Shared.Rent(count);
            BottomG = ArrayPool<float>.Shared.Rent(count);
            BottomB = ArrayPool<float>.Shared.Rent(count);
            TopA = ArrayPool<Half>.Shared.Rent(count);
            BottomA = ArrayPool<Half>.Shared.Rent(count);
            if (hdr)
            {
                TopBrightness = ArrayPool<Half>.Shared.Rent(count);
                BottomBrightness = ArrayPool<Half>.Shared.Rent(count);
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
            ArrayPool<Half>.Shared.Return(TopA);
            ArrayPool<Half>.Shared.Return(BottomA);
            if (TopBrightness is not null) ArrayPool<Half>.Shared.Return(TopBrightness);
            if (BottomBrightness is not null) ArrayPool<Half>.Shared.Return(BottomBrightness);
        }
    }

    private static void BlendRow<T>(Channels top, int src, int dst, int count,
        T[] r, T[] g, T[] b, Half[]? alpha, Half[]? brightness, CompositionMode mode, Channels bottom) where T : unmanaged
    {
        if (mode == CompositionMode.Normal && top.Alpha.IsEmpty)
        {
            Copy(top, src, dst, count, r, g, b, alpha, brightness);
            return;
        }
        bool readBase = !bottom.R8.IsEmpty || !bottom.R16.IsEmpty;
        for (int i = 0; i < count; i++)
        {
            int s = src + i, d = dst + i;
            Half ta = top.Alpha.IsEmpty ? Half.One : Clamp01(top.Alpha[s]);
            if (ta <= (mode == CompositionMode.Normal ? (Half)0.05f : Half.Zero))
            {
                if (readBase) Copy(bottom, d, d, 1, r, g, b, alpha, brightness);
                continue;
            }
            float tr = top.ReadRed(s), tg = top.ReadGreen(s), tb = top.ReadBlue(s);
            if (mode == CompositionMode.Normal && ta >= (Half)0.999f)
            {
                r[d] = ToPixel<T>(tr);
                g[d] = ToPixel<T>(tg);
                b[d] = ToPixel<T>(tb);
                if (alpha is not null) alpha[d] = Half.One;
                if (brightness is not null) brightness[d] = top.Brightness.IsEmpty ? EstimateBrightness(tr, tg, tb) : Clamp01(top.Brightness[s]);
                continue;
            }
            Half ba = readBase ? (bottom.Alpha.IsEmpty ? Half.One : Clamp01(bottom.Alpha[d])) : alpha is null ? Half.One : alpha[d];
            Half bw = ba * (Half.One - ta), a = ta + bw;
            if (a < (Half)1e-6f)
            {
                r[d] = g[d] = b[d] = default;
                if (alpha is not null) alpha[d] = Half.Zero;
                if (brightness is not null) brightness[d] = Half.Zero;
                continue;
            }
            float br = readBase ? bottom.ReadRed(d) : FromPixel(r[d]);
            float bg = readBase ? bottom.ReadGreen(d) : FromPixel(g[d]);
            float bb = readBase ? bottom.ReadBlue(d) : FromPixel(b[d]);
            r[d] = ToPixel<T>((BlendColor(mode, tr, br) * (float)ta + br * (float)bw) / (float)a);
            g[d] = ToPixel<T>((BlendColor(mode, tg, bg) * (float)ta + bg * (float)bw) / (float)a);
            b[d] = ToPixel<T>((BlendColor(mode, tb, bb) * (float)ta + bb * (float)bw) / (float)a);
            if (alpha is not null) alpha[d] = a;
            if (brightness is not null)
            {
                Half previous = readBase ? bottom.Brightness.IsEmpty ? EstimateBrightness(br, bg, bb) : Clamp01(bottom.Brightness[d]) : brightness[d];
                brightness[d] = ((top.Brightness.IsEmpty ? EstimateBrightness(tr, tg, tb) : Clamp01(top.Brightness[s])) * ta + previous * bw) / a;
            }
        }
    }

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

    private static Half Clamp01(Half value) => HalfChannelMath.Clamp(value);
    private static Half EstimateBrightness(float r, float g, float b) => HalfChannelMath.Luminance(r, g, b, 65535f);

    private readonly ref struct Channels
    {
        internal readonly ReadOnlySpan<byte> R8, G8, B8;
        internal readonly ReadOnlySpan<ushort> R16, G16, B16;
        internal readonly ReadOnlySpan<Half> Alpha, Brightness;
        internal readonly Half MaximumBrightness;

        internal static Channels From<TPicture>(TPicture picture) where TPicture : IPicture
        {
            if (PictureCalculation<TPicture>.Is8) return new Channels((IPicture<byte>)(object)picture);
            if (PictureCalculation<TPicture>.Is16) return new Channels((IPicture<ushort>)(object)picture);
            return new Channels(picture);
        }

        private Channels(IPicture<byte> picture) : this((IPicture)picture, true) { }
        private Channels(IPicture<ushort> picture) : this((IPicture)picture, false) { }

        internal Channels(IPicture picture) : this(picture, picture is IPicture<byte>) { }

        private Channels(IPicture picture, bool is8)
        {
            ObjectDisposedException.ThrowIf(picture.Disposed, picture);
            int pixels = checked(picture.Width * picture.Height);
            bool valid;
            if (is8)
            {
                var p8 = (IPicture<byte>)picture;
                var read = new PictureReadChannels<byte>(p8);
                R8 = read.Red; G8 = read.Green; B8 = read.Blue;
                Alpha = picture.HasAlphaChannel ? read.Alpha : default;
                valid = R8.Length == pixels && G8.Length == pixels && B8.Length == pixels;
            }
            else
            {
                var p16 = picture as IPicture<ushort> ?? throw new NotSupportedException($"Unsupported composition picture type: {picture.GetType().Name}.");
                var read = new PictureReadChannels<ushort>(p16);
                R16 = read.Red; G16 = read.Green; B16 = read.Blue;
                Alpha = picture.HasAlphaChannel ? read.Alpha : default;
                valid = R16.Length == pixels && G16.Length == pixels && B16.Length == pixels;
                if (picture is IHDRPicture<ushort> hdr)
                {
                    ReadOnlySpan<Half> brightness = picture is IReadOnlyPictureChannels<ushort> shared ? shared.ReadBrightness : hdr.Brightness;
                    if (brightness.Length == pixels) Brightness = brightness;
                    MaximumBrightness = Half.IsFinite(hdr.MaximumBrightness) && hdr.MaximumBrightness > Half.Zero ? hdr.MaximumBrightness : (Half)1000;
                }
            }

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
