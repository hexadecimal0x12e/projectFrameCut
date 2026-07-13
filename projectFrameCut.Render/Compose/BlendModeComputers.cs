using projectFrameCut.Render.HwAccelContracts;
using projectFrameCut.Render.RenderAPIBase.EffectAndMixture;
using System;
using System.Numerics;
using System.Runtime.CompilerServices;

namespace projectFrameCut.Render.Compose
{
    public class OverlayComputer : IComputer, IOverlayComputer
    {
        public string FromPlugin => Plugin.InternalPluginBase.InternalPluginBaseID;
        public string SupportedEffectOrMixture => "Overlay";

        public BlendResult8 Overlay8(float[] top, float[] bottom, float[] topAlpha, float[] bottomAlpha, int pixelCount)
        {
            var outC = new byte[pixelCount];
            var outA = new float[pixelCount];
            OverlayBlend8Scalar(top, bottom, topAlpha, bottomAlpha, outC, outA, pixelCount);
            return new BlendResult8(outC, outA);
        }

        public BlendResult16 Overlay16(float[] top, float[] bottom, float[] topAlpha, float[] bottomAlpha, int pixelCount)
        {
            var outC = new ushort[pixelCount];
            var outA = new float[pixelCount];
            OverlayBlend16Simd(top, bottom, topAlpha, bottomAlpha, outC, outA, pixelCount);
            return new BlendResult16(outC, outA);
        }

        public BlendResultHdr OverlayHdr(float[] top, float[] bottom, float[] topAlpha, float[] bottomAlpha, int pixelCount)
        {
            var outC = new float[pixelCount];
            var outA = new float[pixelCount];
            OverlayBlendFloat(top, bottom, topAlpha, bottomAlpha, outC, outA, pixelCount);
            return new BlendResultHdr(outC, outA);
        }

        public object[] Compute(object[] args)
        {
            var a = (float[])args[0];
            var b = (float[])args[1];
            var aAlpha = (float[])args[2];
            var bAlpha = (float[])args[3];
            int bitDepth = Convert.ToInt32(args[4]);
            int pixelCount = args.Length > 5 ? Convert.ToInt32(args[5]) : a.Length;

            if (bitDepth == 8)
            {
                var r = Overlay8(a, b, aAlpha, bAlpha, pixelCount);
                return [r.Color, r.Alpha];
            }
            else
            {
                var r = Overlay16(a, b, aAlpha, bAlpha, pixelCount);
                return [r.Color, r.Alpha];
            }
        }

        [MethodImpl(MethodImplOptions.AggressiveOptimization)]
        private static void OverlayBlend8Scalar(float[] a, float[] b, float[] aAlpha, float[] bAlpha,
            byte[] outC, float[] outA, int pixelCount)
        {
            for (int i = 0; i < pixelCount; i++)
            {
                float aA = aAlpha[i];
                float bA = bAlpha[i];
                float outAlpha = aA + bA * (1f - aA);

                if (outAlpha < 1e-6f)
                {
                    outC[i] = 0;
                    outA[i] = 0f;
                }
                else
                {
                    float aC = a[i] * aA / outAlpha;
                    float bC = b[i] * bA * (1f - aA) / outAlpha;
                    float outColor = aC + bC;
                    outC[i] = (byte)Math.Clamp(outColor, 0f, 255f);
                    outA[i] = outAlpha;
                }
            }
        }

        [MethodImpl(MethodImplOptions.AggressiveOptimization)]
        private static void OverlayBlend16Simd(float[] a, float[] b, float[] aAlpha, float[] bAlpha,
            ushort[] outC, float[] outA, int pixelCount)
        {
            int simdWidth = Vector<float>.Count;
            var one = Vector<float>.One;
            var zero = Vector<float>.Zero;
            var epsilon = new Vector<float>(1e-6f);
            var max16 = new Vector<float>(65535f);

            int i = 0;
            for (; i <= pixelCount - simdWidth; i += simdWidth)
            {
                var vA = new Vector<float>(a, i);
                var vB = new Vector<float>(b, i);
                var vAa = new Vector<float>(aAlpha, i);
                var vBa = new Vector<float>(bAlpha, i);

                var vOutAlpha = vAa + vBa * (one - vAa);
                var vAC = vA * vAa;
                var vBC = vB * vBa * (one - vAa);
                var vOutColor = (vAC + vBC) / vOutAlpha;

                vOutColor = Vector.Min(Vector.Max(vOutColor, zero), max16);
                var mask = Vector.GreaterThanOrEqual(vOutAlpha, epsilon);
                vOutColor = Vector.ConditionalSelect(mask, vOutColor, zero);
                vOutAlpha = Vector.ConditionalSelect(mask, vOutAlpha, zero);

                // Write back: store alpha via CopyTo, convert color via scalar per-element
                vOutAlpha.CopyTo(outA, i);
                for (int j = 0; j < simdWidth; j++)
                    outC[i + j] = (ushort)vOutColor[j];
            }

            // Remainder: scalar fallback
            for (; i < pixelCount; i++)
            {
                float aA = aAlpha[i];
                float bA = bAlpha[i];
                float outAlpha = aA + bA * (1f - aA);

                if (outAlpha < 1e-6f)
                {
                    outC[i] = 0;
                    outA[i] = 0f;
                }
                else
                {
                    float aC = a[i] * aA / outAlpha;
                    float bC = b[i] * bA * (1f - aA) / outAlpha;
                    outC[i] = (ushort)Math.Clamp(aC + bC, 0f, 65535f);
                    outA[i] = outAlpha;
                }
            }
        }

        [MethodImpl(MethodImplOptions.AggressiveOptimization)]
        private static void OverlayBlendFloat(float[] a, float[] b, float[] aAlpha, float[] bAlpha,
            float[] outC, float[] outA, int pixelCount)
        {
            for (int i = 0; i < pixelCount; i++)
            {
                float aA = aAlpha[i];
                float bA = bAlpha[i];
                float outAlpha = aA + bA * (1f - aA);

                if (outAlpha < 1e-6f)
                {
                    outC[i] = 0f;
                    outA[i] = 0f;
                }
                else
                {
                    float aC = a[i] * aA / outAlpha;
                    float bC = b[i] * bA * (1f - aA) / outAlpha;
                    outC[i] = Math.Clamp(aC + bC, 0f, 65535f);
                    outA[i] = outAlpha;
                }
            }
        }
    }

    public class ApproximateComputer : IComputer, IApproximateOverlayComputer
    {
        public string FromPlugin => Plugin.InternalPluginBase.InternalPluginBaseID;
        public string SupportedEffectOrMixture => "OverlayApproximate";

        public BlendResult8 ApproximateOverlay8(float[] top, float[] bottom, float[] topAlpha, float[] bottomAlpha, int pixelCount)
        {
            var outC = new byte[pixelCount];
            var outA = new float[pixelCount];
            for (int i = 0; i < pixelCount; i++)
            {
                float aA = topAlpha[i], bA = bottomAlpha[i], outAlpha = aA + bA * (1f - aA);
                if (outAlpha < 1e-6f) { outC[i] = 0; outA[i] = 0f; }
                else
                {
                    float aC = top[i] * aA / outAlpha, bC = bottom[i] * bA * (1f - aA) / outAlpha;
                    outC[i] = (byte)Math.Clamp(aC + bC, 0f, 255f); outA[i] = outAlpha;
                }
            }
            return new BlendResult8(outC, outA);
        }

        public BlendResult16 ApproximateOverlay16(float[] top, float[] bottom, float[] topAlpha, float[] bottomAlpha, int pixelCount)
        {
            var outC = new ushort[pixelCount];
            var outA = new float[pixelCount];
            for (int i = 0; i < pixelCount; i++)
            {
                float aA = topAlpha[i], bA = bottomAlpha[i], outAlpha = aA + bA * (1f - aA);
                if (outAlpha < 1e-6f) { outC[i] = 0; outA[i] = 0f; }
                else
                {
                    float aC = top[i] * aA / outAlpha, bC = bottom[i] * bA * (1f - aA) / outAlpha;
                    outC[i] = (ushort)Math.Clamp(aC + bC, 0f, 65535f); outA[i] = outAlpha;
                }
            }
            return new BlendResult16(outC, outA);
        }

        public BlendResultHdr ApproximateOverlayHdr(float[] top, float[] bottom, float[] topAlpha, float[] bottomAlpha, int pixelCount)
        {
            var outC = new float[pixelCount];
            var outA = new float[pixelCount];
            for (int i = 0; i < pixelCount; i++)
            {
                float aA = topAlpha[i], bA = bottomAlpha[i], outAlpha = aA + bA * (1f - aA);
                if (outAlpha < 1e-6f) { outC[i] = 0f; outA[i] = 0f; }
                else
                {
                    float aC = top[i] * aA / outAlpha, bC = bottom[i] * bA * (1f - aA) / outAlpha;
                    outC[i] = Math.Clamp(aC + bC, 0f, 65535f); outA[i] = outAlpha;
                }
            }
            return new BlendResultHdr(outC, outA);
        }

        public object[] Compute(object[] args)
        {
            var a = (float[])args[0];
            var b = (float[])args[1];
            var aAlpha = (float[])args[2];
            var bAlpha = (float[])args[3];
            int bitDepth = Convert.ToInt32(args[4]);
            int pixelCount = args.Length > 5 ? Convert.ToInt32(args[5]) : a.Length;

            if (bitDepth == 8)
            {
                var r = ApproximateOverlay8(a, b, aAlpha, bAlpha, pixelCount);
                return [r.Color, r.Alpha];
            }
            else
            {
                var r = ApproximateOverlay16(a, b, aAlpha, bAlpha, pixelCount);
                return [r.Color, r.Alpha];
            }
        }
    }

    public class AddComputer : IComputer, IBlendModeComputer
    {
        public string FromPlugin => Plugin.InternalPluginBase.InternalPluginBaseID;
        public string SupportedEffectOrMixture => "AddMixture";

        public BlendResult16 ComputeBlend(float[] top, float[] bottom, float[] topAlpha, float[] bottomAlpha, int pixelCount)
        {
            var outC = new ushort[pixelCount];
            var outA = new float[pixelCount];
            for (int i = 0; i < pixelCount; i++)
            {
                float aA = topAlpha[i], bA = bottomAlpha[i], outAlpha = aA + bA * (1f - aA);
                if (outAlpha < 1e-6f) { outC[i] = 0; outA[i] = 0f; }
                else
                {
                    float blended = Math.Min(top[i] + bottom[i], 65535f);
                    float result = (blended * aA + bottom[i] * bA * (1f - aA)) / outAlpha;
                    outC[i] = (ushort)Math.Clamp(result, 0f, 65535f); outA[i] = outAlpha;
                }
            }
            return new BlendResult16(outC, outA);
        }

        public object[] Compute(object[] args)
        {
            var a = (float[])args[0]; var b = (float[])args[1];
            var aAlpha = (float[])args[2]; var bAlpha = (float[])args[3];
            int pixelCount = args.Length > 5 ? Convert.ToInt32(args[5]) : a.Length;
            var r = ComputeBlend(a, b, aAlpha, bAlpha, pixelCount);
            return [r.Color, r.Alpha];
        }
    }

    public class SubtractComputer : IComputer, IBlendModeComputer
    {
        public string FromPlugin => Plugin.InternalPluginBase.InternalPluginBaseID;
        public string SupportedEffectOrMixture => "SubtractMixture";

        public BlendResult16 ComputeBlend(float[] top, float[] bottom, float[] topAlpha, float[] bottomAlpha, int pixelCount)
        {
            var outC = new ushort[pixelCount];
            var outA = new float[pixelCount];
            for (int i = 0; i < pixelCount; i++)
            {
                float aA = topAlpha[i], bA = bottomAlpha[i], outAlpha = aA + bA * (1f - aA);
                if (outAlpha < 1e-6f) { outC[i] = 0; outA[i] = 0f; }
                else
                {
                    float blended = Math.Max(bottom[i] - top[i], 0f);
                    float result = (blended * aA + bottom[i] * bA * (1f - aA)) / outAlpha;
                    outC[i] = (ushort)Math.Clamp(result, 0f, 65535f); outA[i] = outAlpha;
                }
            }
            return new BlendResult16(outC, outA);
        }

        public object[] Compute(object[] args)
        {
            var a = (float[])args[0]; var b = (float[])args[1];
            var aAlpha = (float[])args[2]; var bAlpha = (float[])args[3];
            int pixelCount = args.Length > 5 ? Convert.ToInt32(args[5]) : a.Length;
            var r = ComputeBlend(a, b, aAlpha, bAlpha, pixelCount);
            return [r.Color, r.Alpha];
        }
    }

    public class MultiplyComputer : IComputer, IBlendModeComputer
    {
        public string FromPlugin => Plugin.InternalPluginBase.InternalPluginBaseID;
        public string SupportedEffectOrMixture => "MultiplyMixture";

        public BlendResult16 ComputeBlend(float[] top, float[] bottom, float[] topAlpha, float[] bottomAlpha, int pixelCount)
        {
            var outC = new ushort[pixelCount];
            var outA = new float[pixelCount];
            for (int i = 0; i < pixelCount; i++)
            {
                float aA = topAlpha[i], bA = bottomAlpha[i], outAlpha = aA + bA * (1f - aA);
                if (outAlpha < 1e-6f) { outC[i] = 0; outA[i] = 0f; }
                else
                {
                    float blended = top[i] * bottom[i] / 65535f;
                    float result = (blended * aA + bottom[i] * bA * (1f - aA)) / outAlpha;
                    outC[i] = (ushort)Math.Clamp(result, 0f, 65535f); outA[i] = outAlpha;
                }
            }
            return new BlendResult16(outC, outA);
        }

        public object[] Compute(object[] args)
        {
            var a = (float[])args[0]; var b = (float[])args[1];
            var aAlpha = (float[])args[2]; var bAlpha = (float[])args[3];
            int pixelCount = args.Length > 5 ? Convert.ToInt32(args[5]) : a.Length;
            var r = ComputeBlend(a, b, aAlpha, bAlpha, pixelCount);
            return [r.Color, r.Alpha];
        }
    }

    public class ScreenComputer : IComputer, IBlendModeComputer
    {
        public string FromPlugin => Plugin.InternalPluginBase.InternalPluginBaseID;
        public string SupportedEffectOrMixture => "ScreenMixture";

        public BlendResult16 ComputeBlend(float[] top, float[] bottom, float[] topAlpha, float[] bottomAlpha, int pixelCount)
        {
            var outC = new ushort[pixelCount];
            var outA = new float[pixelCount];
            for (int i = 0; i < pixelCount; i++)
            {
                float aA = topAlpha[i], bA = bottomAlpha[i], outAlpha = aA + bA * (1f - aA);
                if (outAlpha < 1e-6f) { outC[i] = 0; outA[i] = 0f; }
                else
                {
                    float blended = 65535f - (65535f - top[i]) * (65535f - bottom[i]) / 65535f;
                    float result = (blended * aA + bottom[i] * bA * (1f - aA)) / outAlpha;
                    outC[i] = (ushort)Math.Clamp(result, 0f, 65535f); outA[i] = outAlpha;
                }
            }
            return new BlendResult16(outC, outA);
        }

        public object[] Compute(object[] args)
        {
            var a = (float[])args[0]; var b = (float[])args[1];
            var aAlpha = (float[])args[2]; var bAlpha = (float[])args[3];
            int pixelCount = args.Length > 5 ? Convert.ToInt32(args[5]) : a.Length;
            var r = ComputeBlend(a, b, aAlpha, bAlpha, pixelCount);
            return [r.Color, r.Alpha];
        }
    }

    public class OverlayBlendComputer : IComputer, IBlendModeComputer
    {
        public string FromPlugin => Plugin.InternalPluginBase.InternalPluginBaseID;
        public string SupportedEffectOrMixture => "OverlayBlendMixture";

        public BlendResult16 ComputeBlend(float[] top, float[] bottom, float[] topAlpha, float[] bottomAlpha, int pixelCount)
        {
            var outC = new ushort[pixelCount];
            var outA = new float[pixelCount];
            for (int i = 0; i < pixelCount; i++)
            {
                float aA = topAlpha[i], bA = bottomAlpha[i], outAlpha = aA + bA * (1f - aA);
                if (outAlpha < 1e-6f) { outC[i] = 0; outA[i] = 0f; }
                else
                {
                    float blended;
                    if (bottom[i] < 32768f) blended = 2f * top[i] * bottom[i] / 65535f;
                    else blended = 65535f - 2f * (65535f - top[i]) * (65535f - bottom[i]) / 65535f;
                    float result = (blended * aA + bottom[i] * bA * (1f - aA)) / outAlpha;
                    outC[i] = (ushort)Math.Clamp(result, 0f, 65535f); outA[i] = outAlpha;
                }
            }
            return new BlendResult16(outC, outA);
        }

        public object[] Compute(object[] args)
        {
            var a = (float[])args[0]; var b = (float[])args[1];
            var aAlpha = (float[])args[2]; var bAlpha = (float[])args[3];
            int pixelCount = args.Length > 5 ? Convert.ToInt32(args[5]) : a.Length;
            var r = ComputeBlend(a, b, aAlpha, bAlpha, pixelCount);
            return [r.Color, r.Alpha];
        }
    }

    public class DarkenComputer : IComputer, IBlendModeComputer
    {
        public string FromPlugin => Plugin.InternalPluginBase.InternalPluginBaseID;
        public string SupportedEffectOrMixture => "DarkenMixture";

        public BlendResult16 ComputeBlend(float[] top, float[] bottom, float[] topAlpha, float[] bottomAlpha, int pixelCount)
        {
            var outC = new ushort[pixelCount];
            var outA = new float[pixelCount];
            for (int i = 0; i < pixelCount; i++)
            {
                float aA = topAlpha[i], bA = bottomAlpha[i], outAlpha = aA + bA * (1f - aA);
                if (outAlpha < 1e-6f) { outC[i] = 0; outA[i] = 0f; }
                else
                {
                    float blended = Math.Min(top[i], bottom[i]);
                    float result = (blended * aA + bottom[i] * bA * (1f - aA)) / outAlpha;
                    outC[i] = (ushort)Math.Clamp(result, 0f, 65535f); outA[i] = outAlpha;
                }
            }
            return new BlendResult16(outC, outA);
        }

        public object[] Compute(object[] args)
        {
            var a = (float[])args[0]; var b = (float[])args[1];
            var aAlpha = (float[])args[2]; var bAlpha = (float[])args[3];
            int pixelCount = args.Length > 5 ? Convert.ToInt32(args[5]) : a.Length;
            var r = ComputeBlend(a, b, aAlpha, bAlpha, pixelCount);
            return [r.Color, r.Alpha];
        }
    }

    public class LightenComputer : IComputer, IBlendModeComputer
    {
        public string FromPlugin => Plugin.InternalPluginBase.InternalPluginBaseID;
        public string SupportedEffectOrMixture => "LightenMixture";

        public BlendResult16 ComputeBlend(float[] top, float[] bottom, float[] topAlpha, float[] bottomAlpha, int pixelCount)
        {
            var outC = new ushort[pixelCount];
            var outA = new float[pixelCount];
            for (int i = 0; i < pixelCount; i++)
            {
                float aA = topAlpha[i], bA = bottomAlpha[i], outAlpha = aA + bA * (1f - aA);
                if (outAlpha < 1e-6f) { outC[i] = 0; outA[i] = 0f; }
                else
                {
                    float blended = Math.Max(top[i], bottom[i]);
                    float result = (blended * aA + bottom[i] * bA * (1f - aA)) / outAlpha;
                    outC[i] = (ushort)Math.Clamp(result, 0f, 65535f); outA[i] = outAlpha;
                }
            }
            return new BlendResult16(outC, outA);
        }

        public object[] Compute(object[] args)
        {
            var a = (float[])args[0]; var b = (float[])args[1];
            var aAlpha = (float[])args[2]; var bAlpha = (float[])args[3];
            int pixelCount = args.Length > 5 ? Convert.ToInt32(args[5]) : a.Length;
            var r = ComputeBlend(a, b, aAlpha, bAlpha, pixelCount);
            return [r.Color, r.Alpha];
        }
    }

    public class DifferenceComputer : IComputer, IBlendModeComputer
    {
        public string FromPlugin => Plugin.InternalPluginBase.InternalPluginBaseID;
        public string SupportedEffectOrMixture => "DifferenceMixture";

        public BlendResult16 ComputeBlend(float[] top, float[] bottom, float[] topAlpha, float[] bottomAlpha, int pixelCount)
        {
            var outC = new ushort[pixelCount];
            var outA = new float[pixelCount];
            for (int i = 0; i < pixelCount; i++)
            {
                float aA = topAlpha[i], bA = bottomAlpha[i], outAlpha = aA + bA * (1f - aA);
                if (outAlpha < 1e-6f) { outC[i] = 0; outA[i] = 0f; }
                else
                {
                    float blended = Math.Abs(top[i] - bottom[i]);
                    float result = (blended * aA + bottom[i] * bA * (1f - aA)) / outAlpha;
                    outC[i] = (ushort)Math.Clamp(result, 0f, 65535f); outA[i] = outAlpha;
                }
            }
            return new BlendResult16(outC, outA);
        }

        public object[] Compute(object[] args)
        {
            var a = (float[])args[0]; var b = (float[])args[1];
            var aAlpha = (float[])args[2]; var bAlpha = (float[])args[3];
            int pixelCount = args.Length > 5 ? Convert.ToInt32(args[5]) : a.Length;
            var r = ComputeBlend(a, b, aAlpha, bAlpha, pixelCount);
            return [r.Color, r.Alpha];
        }
    }
}
