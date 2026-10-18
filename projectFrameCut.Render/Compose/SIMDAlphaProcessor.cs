using System;
using System.Numerics;
using System.Runtime.CompilerServices;

namespace projectFrameCut.Render.Compose
{
    /// <summary>
    /// SIMD-optimized batch processing for alpha channel and brightness clamping operations.
    /// </summary>
    public static class SIMDAlphaProcessor
    {
        /// <summary>
        /// Clamps an array of float values to [0, 1] range using SIMD when possible.
        /// </summary>
        [MethodImpl(MethodImplOptions.AggressiveOptimization)]
        public static void ClampAlpha(float[] source, float[] destination, int count)
        {
            ClampAlphaOffset(source, destination, 0, 0, count);
        }

        /// <summary>
        /// Clamps a float array range into a destination range using SIMD.
        /// </summary>
        [MethodImpl(MethodImplOptions.AggressiveOptimization)]
        public static void ClampAlphaOffset(float[] source, float[] destination, int sourceOffset, int destOffset, int count)
        {
            ArgumentNullException.ThrowIfNull(source);
            ClampAlphaOffset(source.AsSpan(), destination, sourceOffset, destOffset, count);
        }

        public static void ClampAlphaOffset(ReadOnlySpan<float> source, float[] destination, int sourceOffset, int destOffset, int count)
        {
            ArgumentNullException.ThrowIfNull(destination);
            source = source.Slice(sourceOffset, count);
            var dst = destination.AsSpan(destOffset, count);

            int simdCount = Vector<float>.Count;
            int i = 0;

            // Process in SIMD chunks
            for (; i <= count - simdCount; i += simdCount)
            {
                var v = new Vector<float>(source.Slice(i));
                Vector.ConditionalSelect(Vector.LessThanOrEqual(Vector.Abs(v), new Vector<float>(float.MaxValue)),
                    Vector.Min(Vector.Max(v, Vector<float>.Zero), Vector<float>.One), Vector<float>.Zero).CopyTo(dst.Slice(i));
            }

            // Handle remaining elements scalar
            for (; i < count; i++)
            {
                float value = source[i];
                dst[i] = float.IsFinite(value) ? Math.Clamp(value, 0f, 1f) : 0f;
            }
        }

        /// <summary>
        /// In-place clamping of alpha values using SIMD.
        /// </summary>
        [MethodImpl(MethodImplOptions.AggressiveOptimization)]
        public static void ClampAlphaInPlace(float[] values, int count)
        {
            ClampAlpha(values, values, count);
        }

        /// <summary>
        /// Fill default alpha (1.0f) for a range using SIMD.
        /// </summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static void FillDefaultAlpha(float[] alphas, int startIndex, int count)
        {
            ArgumentNullException.ThrowIfNull(alphas);
            alphas.AsSpan(startIndex, count).Fill(1f);
        }

        /// <summary>
        /// Estimates brightness from RGB and clamps to [0,1] using SIMD.
        /// Uses BT.2020 luma coefficients.
        /// </summary>
        [MethodImpl(MethodImplOptions.AggressiveOptimization)]
        public static void EstimateBrightnessFromUshort(
            ushort[] r, ushort[] g, ushort[] b,
            float[] destination, int count)
        {
            EstimateBrightnessFromUshortOffset(r, g, b, 0, destination, 0, count);
        }

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
            float[] destination, int destOffset, int count)
        {
            ArgumentNullException.ThrowIfNull(r);
            ArgumentNullException.ThrowIfNull(g);
            ArgumentNullException.ThrowIfNull(b);
            EstimateBrightnessFromUshortOffset(r.AsSpan(), g.AsSpan(), b.AsSpan(), sourceOffset, destination, destOffset, count);
        }

        public static void EstimateBrightnessFromUshortOffset(
            ReadOnlySpan<ushort> r, ReadOnlySpan<ushort> g, ReadOnlySpan<ushort> b, int sourceOffset,
            float[] destination, int destOffset, int count)
        {
            ArgumentNullException.ThrowIfNull(destination);
            r = r.Slice(sourceOffset, count);
            g = g.Slice(sourceOffset, count);
            b = b.Slice(sourceOffset, count);
            var dst = destination.AsSpan(destOffset, count);

            const float inv65535 = 1f / 65535f;
            var coeffR = new Vector<float>(0.2627f);
            var coeffG = new Vector<float>(0.6780f);
            var coeffB = new Vector<float>(0.0593f);
            var scaleFactor = new Vector<float>(inv65535);
            var one = Vector<float>.One;

            int i = 0;
            for (; i <= count - Vector<ushort>.Count; i += Vector<ushort>.Count)
            {
                Vector.Widen(new Vector<ushort>(r.Slice(i)), out var rLo, out var rHi);
                Vector.Widen(new Vector<ushort>(g.Slice(i)), out var gLo, out var gHi);
                Vector.Widen(new Vector<ushort>(b.Slice(i)), out var bLo, out var bHi);
                Vector.Min((Vector.ConvertToSingle(rLo) * coeffR + Vector.ConvertToSingle(gLo) * coeffG
                    + Vector.ConvertToSingle(bLo) * coeffB) * scaleFactor, one).CopyTo(dst.Slice(i));
                Vector.Min((Vector.ConvertToSingle(rHi) * coeffR + Vector.ConvertToSingle(gHi) * coeffG
                    + Vector.ConvertToSingle(bHi) * coeffB) * scaleFactor, one).CopyTo(dst.Slice(i + Vector<float>.Count));
            }

            // Handle remaining elements scalar
            const float cR = 0.2627f;
            const float cG = 0.6780f;
            const float cB = 0.0593f;
            for (; i < count; i++)
            {
                float brightness = (cR * r[i] + cG * g[i] + cB * b[i]) * inv65535;
                dst[i] = brightness > 1f ? 1f : brightness;
            }
        }
    }
}
