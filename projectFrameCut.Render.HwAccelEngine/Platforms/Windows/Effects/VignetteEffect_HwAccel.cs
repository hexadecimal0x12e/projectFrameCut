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
    public partial class VignetteEffect_HwAccel
    {
        private int accelIdx = 0;
        private static readonly System.Runtime.CompilerServices.ConditionalWeakTable<Accelerator, Action<Index1D, ArrayView<float>, ArrayView<float>, ArrayView<float>, ArrayView<float>, ArrayView<float>, ArrayView<float>, ArrayView<float>, ArrayView<float>, int, int, float, float>> KernelCache = new();
        private static Action<Index1D, ArrayView<float>, ArrayView<float>, ArrayView<float>, ArrayView<float>, ArrayView<float>, ArrayView<float>, ArrayView<float>, ArrayView<float>, int, int, float, float> GetKernel(Accelerator accelerator)
        {
            return KernelCache.GetValue(accelerator, static acc => acc.LoadAutoGroupedStreamKernel((Index1D i, ArrayView<float> rOut, ArrayView<float> gOut, ArrayView<float> bOut, ArrayView<float> aOut, ArrayView<float> rIn, ArrayView<float> gIn, ArrayView<float> bIn, ArrayView<float> aIn, int w, int h, float strength, float radius) =>
            {
                int x = i % w;
                int y = i / w;
                float cx = w * 0.5f;
                float cy = h * 0.5f;
                float dx = (x - cx) / cx;
                float dy = (y - cy) / cy;
                float dist = MathF.Sqrt(dx * dx + dy * dy);
                float factor = 1f;
                if (dist > radius)
                {
                    float t = MathF.Min((dist - radius) / (1f - radius), 1f);
                    factor = 1f - t * t * strength;
                }

                rOut[i] = rIn[i] * factor;
                gOut[i] = gIn[i] * factor;
                bOut[i] = bIn[i] * factor;
                aOut[i] = aIn[i];
            }));
        }

        private FourChannelResult ComputeVignette(float[] r, float[] g, float[] b, float[] a, int w, int h, float strength, float radius)
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
            var kernel = GetKernel(accelerator);
            if (ILGPUExecutionHelper.SyncOverride ?? accelerator.AcceleratorType == AcceleratorType.OpenCL)
            {
                using (ILGPUExecutionHelper.locker.EnterScope())
                {
                    kernel(length, rBufOut.View, gBufOut.View, bBufOut.View, aBufOut.View, rBufIn.View, gBufIn.View, bBufIn.View, aBufIn.View, w, h, strength, radius);
                    accelerator.Synchronize();
                }
            }
            else
            {
                kernel(length, rBufOut.View, gBufOut.View, bBufOut.View, aBufOut.View, rBufIn.View, gBufIn.View, bBufIn.View, aBufIn.View, w, h, strength, radius);
            }

            return new FourChannelResult(rBufOut.GetAsArray1D(), gBufOut.GetAsArray1D(), bBufOut.GetAsArray1D(), aBufOut.GetAsArray1D());
        }
    }
}
#endif
