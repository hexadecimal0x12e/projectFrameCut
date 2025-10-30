using ILGPU;
using ILGPU.Runtime;
using ILGPU.Runtime.CPU;
using ILGPU.Runtime.OpenCL;
using projectFrameCut.Shared;
using projectFrameCut.Render.Effects;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.Formats.Png;
using SixLabors.ImageSharp.PixelFormats;
using System;
using System.Collections.Generic;
using System.Drawing;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading.Tasks;

namespace projectFrameCut.Render.ILGpu
{
    public static class Mixture
    {
        public static bool ForceSync = false;
        private static object locker = new();

        public static Picture MixtureFrames(Accelerator? accelerator, RenderMode mode, Picture layerA, Picture layerB, ushort upperBond = ushort.MaxValue, bool allowOverflow = false, object? extend = null, uint frameIndex =0)
        {
            Log($"[#{frameIndex:d6}@Mixer] start mixing2 layer with mode {mode}...");

            if (mode == RenderMode.Overlay)
            {
                return RenderOverlayCPU(layerA, layerB, frameIndex);
            }

            try
            {
                int pixels = layerA.Pixels;
                if (layerA.Pixels != layerB.Pixels) throw new ArgumentException("layerA and layerB must have same pixel count");

                // convert channels to normalized floats
                float[] aR = new float[pixels];
                float[] aG = new float[pixels];
                float[] aB = new float[pixels];
                float[] bR = new float[pixels];
                float[] bG = new float[pixels];
                float[] bB = new float[pixels];
                for (int i =0; i < pixels; i++)
                {
                    aR[i] = layerA.r[i] /65535f;
                    aG[i] = layerA.g[i] /65535f;
                    aB[i] = layerA.b[i] /65535f;
                    bR[i] = layerB.r[i] /65535f;
                    bG[i] = layerB.g[i] /65535f;
                    bB[i] = layerB.b[i] /65535f;
                }

                // zero arrays for unused args
                float[] zeros = new float[pixels];

                float[] oR, oG, oB;
                switch (mode)
                {
                    case RenderMode.Add:
                        oR = EffectsEngine.MixtureAddComputer.Compute(MyAcceleratorType.CUDA,2, aR, bR, zeros, zeros, zeros, zeros);
                        oG = EffectsEngine.MixtureAddComputer.Compute(MyAcceleratorType.CUDA,2, aG, bG, zeros, zeros, zeros, zeros);
                        oB = EffectsEngine.MixtureAddComputer.Compute(MyAcceleratorType.CUDA,2, aB, bB, zeros, zeros, zeros, zeros);
                        break;
                    case RenderMode.Minus:
                        oR = EffectsEngine.MixtureMinusComputer.Compute(MyAcceleratorType.CUDA,2, aR, bR, zeros, zeros, zeros, zeros);
                        oG = EffectsEngine.MixtureMinusComputer.Compute(MyAcceleratorType.CUDA,2, aG, bG, zeros, zeros, zeros, zeros);
                        oB = EffectsEngine.MixtureMinusComputer.Compute(MyAcceleratorType.CUDA,2, aB, bB, zeros, zeros, zeros, zeros);
                        break;
                    case RenderMode.Multiply:
                        oR = EffectsEngine.MixtureMultiplyComputer.Compute(MyAcceleratorType.CUDA,2, aR, bR, zeros, zeros, zeros, zeros);
                        oG = EffectsEngine.MixtureMultiplyComputer.Compute(MyAcceleratorType.CUDA,2, aG, bG, zeros, zeros, zeros, zeros);
                        oB = EffectsEngine.MixtureMultiplyComputer.Compute(MyAcceleratorType.CUDA,2, aB, bB, zeros, zeros, zeros, zeros);
                        break;
                    default:
                        throw new NotSupportedException($"You defined an unsupported mixture mode {mode}.");
                }

                var result = new Picture(layerA)
                {
                    Width = layerA.Width,
                    Height = layerA.Height,
                    frameIndex = frameIndex
                };

                result.r = new ushort[pixels];
                result.g = new ushort[pixels];
                result.b = new ushort[pixels];

                for (int i =0; i < pixels; i++)
                {
                    int rr = (int)Math.Round(oR[i] *65535f);
                    int gg = (int)Math.Round(oG[i] *65535f);
                    int bb = (int)Math.Round(oB[i] *65535f);
                    if (rr <0) rr =0; if (rr >65535) rr =65535;
                    if (gg <0) gg =0; if (gg >65535) gg =65535;
                    if (bb <0) bb =0; if (bb >65535) bb =65535;
                    result.r[i] = (ushort)rr;
                    result.g[i] = (ushort)gg;
                    result.b[i] = (ushort)bb;
                }

                // preserve alpha from layerA
                result.a = layerA.a;
                result.hasAlphaChannel = layerA.hasAlphaChannel;

                return result;
            }
            catch (Exception ex)
            {
                // fall back to throwing original exception
                throw;
            }
        }


        public static Picture MapAlphaAndUpperBond(this Picture picture, Accelerator? accelerator, ushort upperBond = ushort.MaxValue)
        {
            if (!picture.hasAlphaChannel) return picture;

            int pixels = picture.Pixels;
            var result = new Picture(picture.Width, picture.Height)
            {
                hasAlphaChannel = false
            };

            result.r = new ushort[pixels];
            result.g = new ushort[pixels];
            result.b = new ushort[pixels];

            for (int i =0; i < pixels; i++)
            {
                float alpha = picture.a![i];
                int rr = (int)Math.Round(picture.r[i] * alpha);
                int gg = (int)Math.Round(picture.g[i] * alpha);
                int bb = (int)Math.Round(picture.b[i] * alpha);
                if (rr > upperBond) rr = upperBond;
                if (gg > upperBond) gg = upperBond;
                if (bb > upperBond) bb = upperBond;
                if (rr <0) rr =0; if (rr >65535) rr =65535;
                if (gg <0) gg =0; if (gg >65535) gg =65535;
                if (bb <0) bb =0; if (bb >65535) bb =65535;
                result.r[i] = (ushort)rr;
                result.g[i] = (ushort)gg;
                result.b[i] = (ushort)bb;
            }

            return result;
        }

        private static Picture RenderOverlayCPU(Picture a, Picture b, uint frameIndex)
        {
            if (!a.hasAlphaChannel) return a;
            b.EnsureAlpha();
            a.EnsureAlpha();
            int pixels = a.Pixels;

            var result = new Picture(a)
            {
                Width = a.Width,
                Height = a.Height,
                frameIndex = frameIndex,
                r = new ushort[pixels],
                g = new ushort[pixels],
                b = new ushort[pixels],
                a = new float[pixels],
                hasAlphaChannel = true
            };

            for (int i =0; i < pixels; i++)
            {
                float aAlpha = a.a![i];
                float bAlpha = b.a![i];
                ushort aVal = a.r[i];
                ushort bVal = b.r[i];

                // R channel
                if (aAlpha ==1f)
                {
                    result.r[i] = a.r[i];
                    result.a[i] =1f;
                }
                else if (aAlpha <=0.05f)
                {
                    result.r[i] = b.r[i];
                    result.a[i] = bAlpha;
                }
                else
                {
                    float outA = aAlpha + bAlpha * (1 - aAlpha);
                    if (outA <1e-6f)
                    {
                        result.r[i] =0;
                        result.a[i] =0f;
                    }
                    else
                    {
                        float aC = a.r[i] * aAlpha / outA;
                        float bC = b.r[i] * bAlpha * (1 - aAlpha) / outA;
                        float outC = aC + bC;
                        if (outC <0f) outC =0f;
                        if (outC > ushort.MaxValue) outC = ushort.MaxValue;
                        result.r[i] = (ushort)outC;
                        result.a[i] = outA;
                    }
                }

                // G channel
                if (aAlpha ==1f)
                {
                    result.g[i] = a.g[i];
                }
                else if (aAlpha <=0.05f)
                {
                    result.g[i] = b.g[i];
                }
                else
                {
                    float outA = result.a[i];
                    if (outA <1e-6f)
                    {
                        result.g[i] =0;
                    }
                    else
                    {
                        float aC = a.g[i] * aAlpha / outA;
                        float bC = b.g[i] * bAlpha * (1 - aAlpha) / outA;
                        float outC = aC + bC;
                        if (outC <0f) outC =0f;
                        if (outC > ushort.MaxValue) outC = ushort.MaxValue;
                        result.g[i] = (ushort)outC;
                    }
                }

                // B channel
                if (aAlpha ==1f)
                {
                    result.b[i] = a.b[i];
                }
                else if (aAlpha <=0.05f)
                {
                    result.b[i] = b.b[i];
                }
                else
                {
                    float outA = result.a[i];
                    if (outA <1e-6f)
                    {
                        result.b[i] =0;
                    }
                    else
                    {
                        float aC = a.b[i] * aAlpha / outA;
                        float bC = b.b[i] * bAlpha * (1 - aAlpha) / outA;
                        float outC = aC + bC;
                        if (outC <0f) outC =0f;
                        if (outC > ushort.MaxValue) outC = ushort.MaxValue;
                        result.b[i] = (ushort)outC;
                    }
                }
            }

            return result;
        }

        private static void LockRun(Action action)
        {
            if (ForceSync)
            {
                lock (locker)
                {
                    action();
                }
            }
            else
            {
                action();
            }
        }

        private static MemoryBuffer1D<T, Stride1D.Dense> Allocate1D<T>(int pixels, Accelerator accelerator) where T : unmanaged
        {
            if (ForceSync)
            {
                lock (locker)
                {
                    return accelerator.Allocate1D<T>(pixels);
                }
            }
            else
            {
                return accelerator.Allocate1D<T>(pixels);
            }

        }
    }


}
