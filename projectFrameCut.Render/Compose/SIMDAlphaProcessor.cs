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
            if (source == null) throw new ArgumentNullException(nameof(source));
            if (destination == null) throw new ArgumentNullException(nameof(destination));
            if (count < 0 || count > source.Length || count > destination.Length)
                throw new ArgumentOutOfRangeException(nameof(count));

            int simdCount = Vector<float>.Count;
            int i = 0;

            // Process in SIMD chunks
            for (; i <= count - simdCount; i += simdCount)
            {
                var v = new Vector<float>(source, i);
                v = Vector.Min(v, Vector<float>.One);
                v = Vector.Max(v, Vector<float>.Zero);
                v.CopyTo(destination, i);
            }

            // Handle remaining elements scalar
            for (; i < count; i++)
            {
                float value = source[i];
                destination[i] = value < 0f ? 0f : value > 1f ? 1f : value;
            }
        }

        /// <summary>
        /// Clamps float array into destination at specified offset using SIMD.
        /// </summary>
        [MethodImpl(MethodImplOptions.AggressiveOptimization)]
        public static void ClampAlphaOffset(float[] source, float[] destination, int destOffset, int count)
        {
            if (source == null) throw new ArgumentNullException(nameof(source));
            if (destination == null) throw new ArgumentNullException(nameof(destination));
            if (count < 0 || count > source.Length || destOffset + count > destination.Length)
                throw new ArgumentOutOfRangeException(nameof(count));

            int simdCount = Vector<float>.Count;
            int i = 0;

            // Process in SIMD chunks
            for (; i <= count - simdCount; i += simdCount)
            {
                var v = new Vector<float>(source, i);
                v = Vector.Min(v, Vector<float>.One);
                v = Vector.Max(v, Vector<float>.Zero);
                v.CopyTo(destination, destOffset + i);
            }

            // Handle remaining elements scalar
            for (; i < count; i++)
            {
                float value = source[i];
                destination[destOffset + i] = value < 0f ? 0f : value > 1f ? 1f : value;
            }
        }

        /// <summary>
        /// In-place clamping of alpha values using SIMD.
        /// </summary>
        [MethodImpl(MethodImplOptions.AggressiveOptimization)]
        public static void ClampAlphaInPlace(float[] values, int count)
        {
            if (values == null) throw new ArgumentNullException(nameof(values));
            if (count < 0 || count > values.Length)
                throw new ArgumentOutOfRangeException(nameof(count));

            int simdCount = Vector<float>.Count;
            int i = 0;

            // Process in SIMD chunks
            for (; i <= count - simdCount; i += simdCount)
            {
                var v = new Vector<float>(values, i);
                v = Vector.Min(v, Vector<float>.One);
                v = Vector.Max(v, Vector<float>.Zero);
                v.CopyTo(values, i);
            }

            // Handle remaining elements scalar
            for (; i < count; i++)
            {
                float value = values[i];
                values[i] = value < 0f ? 0f : value > 1f ? 1f : value;
            }
        }

        /// <summary>
        /// Fill default alpha (1.0f) for a range using SIMD.
        /// </summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static void FillDefaultAlpha(float[] alphas, int startIndex, int count)
        {
            if (alphas == null) throw new ArgumentNullException(nameof(alphas));
            if (startIndex < 0 || count < 0 || startIndex + count > alphas.Length)
                throw new ArgumentOutOfRangeException();

            int simdCount = Vector<float>.Count;
            int i = startIndex;
            int end = startIndex + count;
            var one = Vector<float>.One;

            // Fill with SIMD
            for (; i <= end - simdCount; i += simdCount)
            {
                one.CopyTo(alphas, i);
            }

            // Handle remaining
            for (; i < end; i++)
            {
                alphas[i] = 1f;
            }
        }

        /// <summary>
        /// Estimates brightness from RGB and clamps to [0,1] using SIMD.
        /// Uses ITU-R BT.709 luma coefficients: 0.2627*R + 0.6780*G + 0.0593*B
        /// </summary>
        [MethodImpl(MethodImplOptions.AggressiveOptimization)]
        public static void EstimateBrightnessFromUshort(
            ushort[] r, ushort[] g, ushort[] b,
            float[] destination, int count)
        {
            if (r == null || g == null || b == null || destination == null)
                throw new ArgumentNullException();
            if (count < 0 || count > r.Length || count > g.Length || count > b.Length || count > destination.Length)
                throw new ArgumentOutOfRangeException(nameof(count));

            const float inv65535 = 1f / 65535f;
            var coeffR = new Vector<float>(0.2627f);
            var coeffG = new Vector<float>(0.6780f);
            var coeffB = new Vector<float>(0.0593f);
            var scaleFactor = new Vector<float>(inv65535);
            var one = Vector<float>.One;

            int simdCount = Vector<float>.Count;
            int i = 0;

            // Process in SIMD chunks
            for (; i <= count - simdCount; i += simdCount)
            {
                Span<float> tempR = stackalloc float[simdCount];
                Span<float> tempG = stackalloc float[simdCount];
                Span<float> tempB = stackalloc float[simdCount];

                for (int j = 0; j < simdCount; j++)
                {
                    tempR[j] = r[i + j];
                    tempG[j] = g[i + j];
                    tempB[j] = b[i + j];
                }

                var vR = new Vector<float>(tempR);
                var vG = new Vector<float>(tempG);
                var vB = new Vector<float>(tempB);

                var brightness = (vR * coeffR + vG * coeffG + vB * coeffB) * scaleFactor;
                brightness = Vector.Min(brightness, one);
                brightness.CopyTo(destination, i);
            }

            // Handle remaining elements scalar
            const float cR = 0.2627f;
            const float cG = 0.6780f;
            const float cB = 0.0593f;
            for (; i < count; i++)
            {
                float brightness = (cR * r[i] + cG * g[i] + cB * b[i]) * inv65535;
                destination[i] = brightness > 1f ? 1f : brightness;
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
            if (r == null || g == null || b == null || destination == null)
                throw new ArgumentNullException();
            if (count < 0 || sourceOffset + count > r.Length || sourceOffset + count > g.Length || 
                sourceOffset + count > b.Length || destOffset + count > destination.Length)
                throw new ArgumentOutOfRangeException(nameof(count));

            const float inv65535 = 1f / 65535f;
            var coeffR = new Vector<float>(0.2627f);
            var coeffG = new Vector<float>(0.6780f);
            var coeffB = new Vector<float>(0.0593f);
            var scaleFactor = new Vector<float>(inv65535);
            var one = Vector<float>.One;

            int simdCount = Vector<float>.Count;
            int i = 0;

            // Process in SIMD chunks
            for (; i <= count - simdCount; i += simdCount)
            {
                Span<float> tempR = stackalloc float[simdCount];
                Span<float> tempG = stackalloc float[simdCount];
                Span<float> tempB = stackalloc float[simdCount];

                for (int j = 0; j < simdCount; j++)
                {
                    tempR[j] = r[sourceOffset + i + j];
                    tempG[j] = g[sourceOffset + i + j];
                    tempB[j] = b[sourceOffset + i + j];
                }

                var vR = new Vector<float>(tempR);
                var vG = new Vector<float>(tempG);
                var vB = new Vector<float>(tempB);

                var brightness = (vR * coeffR + vG * coeffG + vB * coeffB) * scaleFactor;
                brightness = Vector.Min(brightness, one);
                brightness.CopyTo(destination, destOffset + i);
            }

            // Handle remaining elements scalar
            const float cR = 0.2627f;
            const float cG = 0.6780f;
            const float cB = 0.0593f;
            for (; i < count; i++)
            {
                float brightness = (cR * r[sourceOffset + i] + cG * g[sourceOffset + i] + cB * b[sourceOffset + i]) * inv65535;
                destination[destOffset + i] = brightness > 1f ? 1f : brightness;
            }
        }
    }
}
