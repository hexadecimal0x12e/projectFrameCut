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
    public partial class SharpenEffect_HwAccel
    {
        private int accelIdx = 0;
        private static readonly System.Runtime.CompilerServices.ConditionalWeakTable<Accelerator, Action<Index1D, ArrayView<float>, ArrayView<float>, ArrayView<float>, ArrayView<float>, ArrayView<float>, ArrayView<float>, ArrayView<float>, ArrayView<float>, int, int, float>> KernelCache = new();
        private static Action<Index1D, ArrayView<float>, ArrayView<float>, ArrayView<float>, ArrayView<float>, ArrayView<float>, ArrayView<float>, ArrayView<float>, ArrayView<float>, int, int, float> GetKernel(Accelerator accelerator)
        {
            return KernelCache.GetValue(accelerator, static acc => acc.LoadAutoGroupedStreamKernel((Index1D i, ArrayView<float> rOut, ArrayView<float> gOut, ArrayView<float> bOut, ArrayView<float> aOut, ArrayView<float> rIn, ArrayView<float> gIn, ArrayView<float> bIn, ArrayView<float> aIn, int w, int h, float amount) =>
            {
                int x = i % w;
                int y = i / w;
                float avgR, avgG, avgB, origR, origG, origB;
                origR = rIn[i];
                origG = gIn[i];
                origB = bIn[i];
                int left = x > 0 ? i - 1 : i;
                int right = x < w - 1 ? i + 1 : i;
                int top = y > 0 ? i - w : i;
                int bottom = y < h - 1 ? i + w : i;
                avgR = (rIn[left] + rIn[right] + rIn[top] + rIn[bottom]) * 0.25f;
                avgG = (gIn[left] + gIn[right] + gIn[top] + gIn[bottom]) * 0.25f;
                avgB = (bIn[left] + bIn[right] + bIn[top] + bIn[bottom]) * 0.25f;
                rOut[i] = origR + amount * (origR - avgR);
                gOut[i] = origG + amount * (origG - avgG);
                bOut[i] = origB + amount * (origB - avgB);
                aOut[i] = aIn[i];
            }));
        }

        private FourChannelResult ComputeSharpen(float[] r, float[] g, float[] b, float[] a, int w, float amount)
        {
            using var deviceLease = AcceleratorsManager.AcquireExecution();
            var accelerator = ILGPUExecutionHelper.SelectAccelerator(deviceLease.Accelerators, ref accelIdx);
            int length = r.Length;
            using var rBufIn = accelerator.Allocate1D(r);
            using var gBufIn = accelerator.Allocate1D(g);
            using var bBufIn = accelerator.Allocate1D(b);
            using var aBufIn = accelerator.Allocate1D(a);
            using var rBufOut = accelerator.Allocate1D<float>(length);
            using var gBufOut = accelerator.Allocate1D<float>(length);
            using var bBufOut = accelerator.Allocate1D<float>(length);
            using var aBufOut = accelerator.Allocate1D<float>(length);
            int h = length / w;
            var kernel = GetKernel(accelerator);
            if (ILGPUExecutionHelper.SyncOverride ?? accelerator.AcceleratorType == AcceleratorType.OpenCL)
            {
                using (ILGPUExecutionHelper.locker.EnterScope())
                {
                    kernel(length, rBufOut.View, gBufOut.View, bBufOut.View, aBufOut.View, rBufIn.View, gBufIn.View, bBufIn.View, aBufIn.View, w, h, amount);
                    accelerator.Synchronize();
                }
            }
            else
            {
                kernel(length, rBufOut.View, gBufOut.View, bBufOut.View, aBufOut.View, rBufIn.View, gBufIn.View, bBufIn.View, aBufIn.View, w, h, amount);
            }

            return new FourChannelResult(rBufOut.GetAsArray1D(), gBufOut.GetAsArray1D(), bBufOut.GetAsArray1D(), aBufOut.GetAsArray1D());
        }
    }
}
#endif
