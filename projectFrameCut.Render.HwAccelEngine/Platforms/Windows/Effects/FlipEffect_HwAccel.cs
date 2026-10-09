#if WINDOWS || LINUX || HEADLESS
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
    public partial class FlipEffect_HwAccel
    {
        private int accelIdx = 0;
        private static readonly System.Runtime.CompilerServices.ConditionalWeakTable<Accelerator, Action<Index1D, ArrayView<float>, ArrayView<float>, ArrayView<float>, ArrayView<float>, ArrayView<float>, ArrayView<float>, ArrayView<float>, ArrayView<float>, int, int, int, int>> KernelCache = new();
        private static Action<Index1D, ArrayView<float>, ArrayView<float>, ArrayView<float>, ArrayView<float>, ArrayView<float>, ArrayView<float>, ArrayView<float>, ArrayView<float>, int, int, int, int> GetKernel(Accelerator accelerator)
        {
            return KernelCache.GetValue(accelerator, static acc => acc.LoadAutoGroupedStreamKernel((Index1D i, ArrayView<float> rOut, ArrayView<float> gOut, ArrayView<float> bOut, ArrayView<float> aOut, ArrayView<float> rIn, ArrayView<float> gIn, ArrayView<float> bIn, ArrayView<float> aIn, int w, int h, int horizontal, int vertical) =>
            {
                int x = i % w;
                int y = i / w;
                int srcX = horizontal != 0 ? w - 1 - x : x;
                int srcY = vertical != 0 ? h - 1 - y : y;
                int srcIdx = srcY * w + srcX;
                rOut[i] = rIn[srcIdx];
                gOut[i] = gIn[srcIdx];
                bOut[i] = bIn[srcIdx];
                aOut[i] = aIn[srcIdx];
            }));
        }

        private FourChannelResult ComputeFlip(float[] r, float[] g, float[] b, float[] a, int w, int h, bool horizontal, bool vertical)
        {
            using var deviceLease = AcceleratorsManager.AcquireExecution();
            var accelerator = ILGPUExecutionHelper.SelectAccelerator(deviceLease.Accelerators, ref accelIdx);
            int length = r.Length;
            int hInt = horizontal ? 1 : 0;
            int vInt = vertical ? 1 : 0;
            using var rBufIn = accelerator.Allocate1D(r);
            using var gBufIn = accelerator.Allocate1D(g);
            using var bBufIn = accelerator.Allocate1D(b);
            using var aBufIn = accelerator.Allocate1D(a);
            using var rBufOut = accelerator.Allocate1D<float>(length);
            using var gBufOut = accelerator.Allocate1D<float>(length);
            using var bBufOut = accelerator.Allocate1D<float>(length);
            using var aBufOut = accelerator.Allocate1D<float>(length);
            var kernel = GetKernel(accelerator);
            if (ILGPUExecutionHelper.SyncOverride ?? accelerator.AcceleratorType == AcceleratorType.OpenCL)
            {
                using (ILGPUExecutionHelper.locker.EnterScope())
                {
                    kernel(length, rBufOut.View, gBufOut.View, bBufOut.View, aBufOut.View, rBufIn.View, gBufIn.View, bBufIn.View, aBufIn.View, w, h, hInt, vInt);
                    accelerator.Synchronize();
                }
            }
            else
            {
                kernel(length, rBufOut.View, gBufOut.View, bBufOut.View, aBufOut.View, rBufIn.View, gBufIn.View, bBufIn.View, aBufIn.View, w, h, hInt, vInt);
            }

            return new FourChannelResult(rBufOut.GetAsArray1D(), gBufOut.GetAsArray1D(), bBufOut.GetAsArray1D(), aBufOut.GetAsArray1D());
        }
    }
}
#endif
