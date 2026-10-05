#if WINDOWS || LINUX || WINNETCORE
using ILGPU;
using ILGPU.Runtime;
using ILGPU.Runtime.OpenCL;
using projectFrameCut.Render.RenderAPIBase.EffectAndMixture;
using projectFrameCut.Shared;
using System;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using Device = ILGPU.Runtime.Device;
using projectFrameCut.Render.WindowsRender;

namespace projectFrameCut.Render.HwAccelEngine.Effect
{
    public partial class RotationEffect_HwAccel
    {
        private int accelIdx = 0;
        private static readonly System.Runtime.CompilerServices.ConditionalWeakTable<Accelerator, Action<Index1D, ArrayView<float>, ArrayView<float>, ArrayView<float>, ArrayView<float>, ArrayView<float>, ArrayView<float>, ArrayView<float>, ArrayView<float>, int, int, int, int, float, float>> KernelCache = new();
        private static Action<Index1D, ArrayView<float>, ArrayView<float>, ArrayView<float>, ArrayView<float>, ArrayView<float>, ArrayView<float>, ArrayView<float>, ArrayView<float>, int, int, int, int, float, float> GetKernel(Accelerator accelerator)
        {
            return KernelCache.GetValue(accelerator, static acc => acc.LoadAutoGroupedStreamKernel((Index1D i, ArrayView<float> rOut, ArrayView<float> gOut, ArrayView<float> bOut, ArrayView<float> aOut, ArrayView<float> rIn, ArrayView<float> gIn, ArrayView<float> bIn, ArrayView<float> aIn, int srcW, int srcH, int outW, int outH, float cosA, float sinA) =>
            {
                int x = i % outW;
                int y = i / outW;
                float srcCx = srcW * 0.5f;
                float srcCy = srcH * 0.5f;
                float outCx = outW * 0.5f;
                float outCy = outH * 0.5f;
                float ox = x - outCx;
                float oy = y - outCy;
                float srcXf = cosA * ox - sinA * oy + srcCx;
                float srcYf = sinA * ox + cosA * oy + srcCy;
                if (srcXf >= 0f && srcXf < srcW && srcYf >= 0f && srcYf < srcH)
                {
                    int sx0 = (int)srcXf;
                    int sy0 = (int)srcYf;
                    int sx1 = Math.Min(sx0 + 1, srcW - 1);
                    int sy1 = Math.Min(sy0 + 1, srcH - 1);
                    float fx = srcXf - sx0;
                    float fy = srcYf - sy0;
                    int i00 = sy0 * srcW + sx0;
                    int i10 = sy0 * srcW + sx1;
                    int i01 = sy1 * srcW + sx0;
                    int i11 = sy1 * srcW + sx1;
                    rOut[i] = (rIn[i00] * (1f - fx) + rIn[i10] * fx) * (1f - fy) + (rIn[i01] * (1f - fx) + rIn[i11] * fx) * fy;
                    gOut[i] = (gIn[i00] * (1f - fx) + gIn[i10] * fx) * (1f - fy) + (gIn[i01] * (1f - fx) + gIn[i11] * fx) * fy;
                    bOut[i] = (bIn[i00] * (1f - fx) + bIn[i10] * fx) * (1f - fy) + (bIn[i01] * (1f - fx) + bIn[i11] * fx) * fy;
                    aOut[i] = (aIn[i00] * (1f - fx) + aIn[i10] * fx) * (1f - fy) + (aIn[i01] * (1f - fx) + aIn[i11] * fx) * fy;
                }
                else
                {
                    rOut[i] = 0f;
                    gOut[i] = 0f;
                    bOut[i] = 0f;
                    aOut[i] = 0f;
                }
            }));
        }

        private FourChannelResult ComputeRotation(float[] r, float[] g, float[] b, float[] a, int srcW, int srcH, int dstW, int dstH, float angleDeg)
        {
            using var deviceLease = AcceleratorsManager.AcquireExecution();
            var accelerator = ILGPUExecutionHelper.SelectAccelerator(deviceLease.Accelerators, ref accelIdx);
            int srcLength = checked(srcW * srcH);
            int dstLength = checked(dstW * dstH);
            using var rBufIn = r.Length == srcLength ? accelerator.Allocate1D(r) : accelerator.Allocate1D(r.Take(srcLength).ToArray());
            using var gBufIn = g.Length == srcLength ? accelerator.Allocate1D(g) : accelerator.Allocate1D(g.Take(srcLength).ToArray());
            using var bBufIn = b.Length == srcLength ? accelerator.Allocate1D(b) : accelerator.Allocate1D(b.Take(srcLength).ToArray());
            using var aBufIn = a.Length == srcLength ? accelerator.Allocate1D(a) : accelerator.Allocate1D(a.Take(srcLength).ToArray());
            using var rBufOut = accelerator.Allocate1D<float>(dstLength);
            using var gBufOut = accelerator.Allocate1D<float>(dstLength);
            using var bBufOut = accelerator.Allocate1D<float>(dstLength);
            using var aBufOut = accelerator.Allocate1D<float>(dstLength);
            // Pre-compute rotation trig once on CPU instead of per-pixel in GPU kernel
            float angleRad = angleDeg * MathF.PI / 180f;
            float cosA = MathF.Cos(angleRad);
            float sinA = MathF.Sin(angleRad);
            var kernel = GetKernel(accelerator);
            if (ILGPUExecutionHelper.SyncOverride ?? accelerator.AcceleratorType == AcceleratorType.OpenCL)
            {
                using (ILGPUExecutionHelper.locker.EnterScope())
                {
                    kernel(dstLength, rBufOut.View, gBufOut.View, bBufOut.View, aBufOut.View, rBufIn.View, gBufIn.View, bBufIn.View, aBufIn.View, srcW, srcH, dstW, dstH, cosA, sinA);
                    accelerator.Synchronize();
                }
            }
            else
            {
                kernel(dstLength, rBufOut.View, gBufOut.View, bBufOut.View, aBufOut.View, rBufIn.View, gBufIn.View, bBufIn.View, aBufIn.View, srcW, srcH, dstW, dstH, cosA, sinA);
            }

            return new FourChannelResult(rBufOut.GetAsArray1D(), gBufOut.GetAsArray1D(), bBufOut.GetAsArray1D(), aBufOut.GetAsArray1D());
        }
    }
}
#endif
