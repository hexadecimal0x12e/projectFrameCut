using System;
using System.Numerics;
using System.Runtime.CompilerServices;

namespace projectFrameCut.Render.Compose
{
    public static class SIMDAlphaProcessor
    {
        public static void ClampAlpha(Half[] source, Half[] destination, int count) =>
            ClampAlphaOffset(source, destination, 0, 0, count);

        public static void ClampAlphaOffset(Half[] source, Half[] destination, int sourceOffset, int destOffset, int count) =>
            ClampAlphaOffset(source.AsSpan(), destination, sourceOffset, destOffset, count);

        public static void ClampAlphaOffset(ReadOnlySpan<Half> source, Half[] destination, int sourceOffset, int destOffset, int count)
        {
            source = source.Slice(sourceOffset, count);
            var dst = destination.AsSpan(destOffset, count);
            for (int i = 0; i < count; i++) dst[i] = projectFrameCut.Shared.HalfChannelMath.Clamp(source[i]);
        }

        public static void ClampAlphaInPlace(Half[] values, int count) => ClampAlpha(values, values, count);

        public static void FillDefaultAlpha(Half[] alphas, int startIndex, int count) => alphas.AsSpan(startIndex, count).Fill(Half.One);

        public static void EstimateBrightnessFromUshort(ushort[] r, ushort[] g, ushort[] b, Half[] destination, int count) =>
            EstimateBrightnessFromUshortOffset(r, g, b, 0, destination, 0, count);

        /// <summary>
        /// SIMD copy of three byte (8bpp) RGB channels from source to destination arrays.
        /// </summary>
        [MethodImpl(MethodImplOptions.AggressiveOptimization)]
        public static void CopyBytesRgb(
            byte[] srcR, byte[] srcG, byte[] srcB,
            byte[] dstR, byte[] dstG, byte[] dstB,
            int srcOffset, int dstOffset, int count)
        {
            ArgumentNullException.ThrowIfNull(srcR);
            ArgumentNullException.ThrowIfNull(srcG);
            ArgumentNullException.ThrowIfNull(srcB);
            CopyBytesRgb(srcR.AsSpan(), srcG.AsSpan(), srcB.AsSpan(), dstR, dstG, dstB, srcOffset, dstOffset, count);
        }

        public static void CopyBytesRgb(
            ReadOnlySpan<byte> srcR, ReadOnlySpan<byte> srcG, ReadOnlySpan<byte> srcB,
            byte[] dstR, byte[] dstG, byte[] dstB,
            int srcOffset, int dstOffset, int count)
        {
            srcR.Slice(srcOffset, count).CopyTo(dstR.AsSpan(dstOffset, count));
            srcG.Slice(srcOffset, count).CopyTo(dstG.AsSpan(dstOffset, count));
            srcB.Slice(srcOffset, count).CopyTo(dstB.AsSpan(dstOffset, count));
        }

        /// <summary>
        /// SIMD copy of three ushort (16bpp) RGB channels from source to destination arrays.
        /// </summary>
        [MethodImpl(MethodImplOptions.AggressiveOptimization)]
        public static void CopyUshortsRgb(
            ushort[] srcR, ushort[] srcG, ushort[] srcB,
            ushort[] dstR, ushort[] dstG, ushort[] dstB,
            int srcOffset, int dstOffset, int count)
        {
            ArgumentNullException.ThrowIfNull(srcR);
            ArgumentNullException.ThrowIfNull(srcG);
            ArgumentNullException.ThrowIfNull(srcB);
            CopyUshortsRgb(srcR.AsSpan(), srcG.AsSpan(), srcB.AsSpan(), dstR, dstG, dstB, srcOffset, dstOffset, count);
        }

        public static void CopyUshortsRgb(
            ReadOnlySpan<ushort> srcR, ReadOnlySpan<ushort> srcG, ReadOnlySpan<ushort> srcB,
            ushort[] dstR, ushort[] dstG, ushort[] dstB,
            int srcOffset, int dstOffset, int count)
        {
            srcR.Slice(srcOffset, count).CopyTo(dstR.AsSpan(dstOffset, count));
            srcG.Slice(srcOffset, count).CopyTo(dstG.AsSpan(dstOffset, count));
            srcB.Slice(srcOffset, count).CopyTo(dstB.AsSpan(dstOffset, count));
        }

        /// <summary>
        /// SIMD upconvert of byte RGB channels to ushort (multiply by 257).
        /// Uses Vector.Widen to expand bytes to two ushort vectors, then scales.
        /// </summary>
        [MethodImpl(MethodImplOptions.AggressiveOptimization)]
        public static void UpconvertBytesToUshortsRgb(
            byte[] srcR, byte[] srcG, byte[] srcB,
            ushort[] dstR, ushort[] dstG, ushort[] dstB,
            int srcOffset, int dstOffset, int count)
        {
            ArgumentNullException.ThrowIfNull(srcR);
            ArgumentNullException.ThrowIfNull(srcG);
            ArgumentNullException.ThrowIfNull(srcB);
            UpconvertBytesToUshortsRgb(srcR.AsSpan(), srcG.AsSpan(), srcB.AsSpan(), dstR, dstG, dstB, srcOffset, dstOffset, count);
        }

        public static void UpconvertBytesToUshortsRgb(
            ReadOnlySpan<byte> srcR, ReadOnlySpan<byte> srcG, ReadOnlySpan<byte> srcB,
            ushort[] dstR, ushort[] dstG, ushort[] dstB,
            int srcOffset, int dstOffset, int count)
        {
            var scale = new Vector<ushort>(257);
            int simdWidth = Vector<byte>.Count;
            int ushortSimd = Vector<ushort>.Count;
            int i = 0;

            for (; i <= count - simdWidth; i += simdWidth)
            {
                var vbR = new Vector<byte>(srcR.Slice(srcOffset + i));
                var vbG = new Vector<byte>(srcG.Slice(srcOffset + i));
                var vbB = new Vector<byte>(srcB.Slice(srcOffset + i));

                Vector.Widen(vbR, out var vRlo, out var vRhi);
                Vector.Widen(vbG, out var vGlo, out var vGhi);
                Vector.Widen(vbB, out var vBlo, out var vBhi);

                (vRlo * scale).CopyTo(dstR, dstOffset + i);
                (vRhi * scale).CopyTo(dstR, dstOffset + i + ushortSimd);
                (vGlo * scale).CopyTo(dstG, dstOffset + i);
                (vGhi * scale).CopyTo(dstG, dstOffset + i + ushortSimd);
                (vBlo * scale).CopyTo(dstB, dstOffset + i);
                (vBhi * scale).CopyTo(dstB, dstOffset + i + ushortSimd);
            }
            for (; i < count; i++)
            {
                dstR[dstOffset + i] = (ushort)(srcR[srcOffset + i] * 257);
                dstG[dstOffset + i] = (ushort)(srcG[srcOffset + i] * 257);
                dstB[dstOffset + i] = (ushort)(srcB[srcOffset + i] * 257);
            }
        }

        /// <summary>
        /// Estimates brightness from RGB using offsets into arrays.
        /// </summary>
        [MethodImpl(MethodImplOptions.AggressiveOptimization)]
        public static void EstimateBrightnessFromUshortOffset(
            ushort[] r, ushort[] g, ushort[] b, int sourceOffset,
            Half[] destination, int destOffset, int count)
        {
            ArgumentNullException.ThrowIfNull(r);
            ArgumentNullException.ThrowIfNull(g);
            ArgumentNullException.ThrowIfNull(b);
            EstimateBrightnessFromUshortOffset(r.AsSpan(), g.AsSpan(), b.AsSpan(), sourceOffset, destination, destOffset, count);
        }

        public static void EstimateBrightnessFromUshortOffset(
            ReadOnlySpan<ushort> r, ReadOnlySpan<ushort> g, ReadOnlySpan<ushort> b, int sourceOffset,
            Half[] destination, int destOffset, int count)
        {
            ArgumentNullException.ThrowIfNull(destination);
            r = r.Slice(sourceOffset, count);
            g = g.Slice(sourceOffset, count);
            b = b.Slice(sourceOffset, count);
            var dst = destination.AsSpan(destOffset, count);

            for (int i = 0; i < count; i++)
                dst[i] = projectFrameCut.Shared.HalfChannelMath.Luminance(r[i], g[i], b[i], 65535f);
        }
    }
}
