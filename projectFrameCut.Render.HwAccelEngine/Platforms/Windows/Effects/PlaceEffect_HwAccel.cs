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
    public partial class PlaceEffect_HwAccel
    {
        private int accelIdx = 0;
        private static readonly System.Runtime.CompilerServices.ConditionalWeakTable<Accelerator, Action<Index1D, ArrayView<float>, ArrayView<float>, ArrayView<float>, ArrayView<float>, ArrayView<float>, ArrayView<float>, ArrayView<float>, ArrayView<float>, int, int, int, int, int>> KernelCache = new();
        private static Action<Index1D, ArrayView<float>, ArrayView<float>, ArrayView<float>, ArrayView<float>, ArrayView<float>, ArrayView<float>, ArrayView<float>, ArrayView<float>, int, int, int, int, int> GetKernel(Accelerator accelerator)
        {
            return KernelCache.GetValue(accelerator, static acc => acc.LoadAutoGroupedStreamKernel((Index1D i, ArrayView<float> rOut, ArrayView<float> gOut, ArrayView<float> bOut, ArrayView<float> aOut, ArrayView<float> rIn, ArrayView<float> gIn, ArrayView<float> bIn, ArrayView<float> aIn, int dstW, int srcW, int srcH, int startX, int startY) =>
            {
                int x = i % dstW;
                int y = i / dstW;
                int srcX = x - startX;
                int srcY = y - startY;
                if (srcX >= 0 && srcX < srcW && srcY >= 0 && srcY < srcH)
                {
                    int srcIdx = srcY * srcW + srcX;
                    rOut[i] = rIn[srcIdx];
                    gOut[i] = gIn[srcIdx];
                    bOut[i] = bIn[srcIdx];
                    aOut[i] = aIn[srcIdx];
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

        private FourChannelResult ComputePlace(float[] r, float[] g, float[] b, float[] a, int srcW, int srcH, int startX, int startY, int targetW, int targetH)
        {
            using var deviceLease = AcceleratorsManager.AcquireExecution();
            var accelerator = ILGPUExecutionHelper.SelectAccelerator(deviceLease.Accelerators, ref accelIdx);
            if (srcW <= 0 || srcH <= 0 || targetW <= 0 || targetH <= 0)
                return new FourChannelResult([], [], [], []);
            int srcLength = checked(srcW * srcH);
            int dstLength = checked(targetW * targetH);
            using var rBufIn = r.Length == srcLength ? accelerator.Allocate1D(r) : accelerator.Allocate1D(r.Take(srcLength).ToArray());
            using var gBufIn = g.Length == srcLength ? accelerator.Allocate1D(g) : accelerator.Allocate1D(g.Take(srcLength).ToArray());
            using var bBufIn = b.Length == srcLength ? accelerator.Allocate1D(b) : accelerator.Allocate1D(b.Take(srcLength).ToArray());
            using var aBufIn = a.Length == srcLength ? accelerator.Allocate1D(a) : accelerator.Allocate1D(a.Take(srcLength).ToArray());
            using var rBufOut = accelerator.Allocate1D<float>(dstLength);
            using var gBufOut = accelerator.Allocate1D<float>(dstLength);
            using var bBufOut = accelerator.Allocate1D<float>(dstLength);
            using var aBufOut = accelerator.Allocate1D<float>(dstLength);
            var kernel = GetKernel(accelerator);
            if (ILGPUExecutionHelper.SyncOverride ?? accelerator.AcceleratorType == AcceleratorType.OpenCL)
            {
                using (ILGPUExecutionHelper.locker.EnterScope())
                {
                    kernel(dstLength, rBufOut.View, gBufOut.View, bBufOut.View, aBufOut.View, rBufIn.View, gBufIn.View, bBufIn.View, aBufIn.View, targetW, srcW, srcH, startX, startY);
                    accelerator.Synchronize();
                }
            }
            else
            {
                kernel(dstLength, rBufOut.View, gBufOut.View, bBufOut.View, aBufOut.View, rBufIn.View, gBufIn.View, bBufIn.View, aBufIn.View, targetW, srcW, srcH, startX, startY);
            }

            return new FourChannelResult(rBufOut.GetAsArray1D(), gBufOut.GetAsArray1D(), bBufOut.GetAsArray1D(), aBufOut.GetAsArray1D());
        }
    }
}
#endif
