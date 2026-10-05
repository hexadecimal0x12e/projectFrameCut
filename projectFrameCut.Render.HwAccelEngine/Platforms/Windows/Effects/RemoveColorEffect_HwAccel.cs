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
    public partial class RemoveColorEffect_HwAccel
    {
        private int accelIdx = 0;
        private static readonly System.Runtime.CompilerServices.ConditionalWeakTable<Accelerator, Action<Index1D, ArrayView1D<float, Stride1D.Dense>, ArrayView1D<float, Stride1D.Dense>, ArrayView1D<float, Stride1D.Dense>, ArrayView1D<float, Stride1D.Dense>, float, float, float, float, float, float, ArrayView1D<float, Stride1D.Dense>>> KernelCache = new();
        private static Action<Index1D, ArrayView1D<float, Stride1D.Dense>, ArrayView1D<float, Stride1D.Dense>, ArrayView1D<float, Stride1D.Dense>, ArrayView1D<float, Stride1D.Dense>, float, float, float, float, float, float, ArrayView1D<float, Stride1D.Dense>> GetKernel(Accelerator accelerator)
        {
            return KernelCache.GetValue(accelerator, static acc =>
            {
                return acc.LoadAutoGroupedStreamKernel<Index1D, ArrayView1D<float, Stride1D.Dense>, ArrayView1D<float, Stride1D.Dense>, ArrayView1D<float, Stride1D.Dense>, ArrayView1D<float, Stride1D.Dense>, float, float, float, float, float, float, ArrayView1D<float, Stride1D.Dense>>(static (i, r, g, b, sourceA, lowR, highR, lowG, highG, lowB, highB, outA) =>
                {
                    bool inR = lowR <= r[i] && r[i] <= highR;
                    bool inG = lowG <= g[i] && g[i] <= highG;
                    bool inB = lowB <= b[i] && b[i] <= highB;
                    outA[i] = (inR && inG && inB) ? 0f : sourceA[i];
                });
            });
        }

        private float[] ComputeRemoveColor(float[] r, float[] g, float[] b, float[] a, float targetR, float targetG, float targetB, float range, int pixels)
        {
            using var deviceLease = AcceleratorsManager.AcquireExecution();
            var accelerator = ILGPUExecutionHelper.SelectAccelerator(deviceLease.Accelerators, ref accelIdx);
            int size = pixels > 0 ? Math.Min(pixels, r.Length) : r.Length;
            float lowR = targetR - range;
            float lowG = targetG - range;
            float lowB = targetB - range;
            float highR = targetR + range;
            float highG = targetG + range;
            float highB = targetB + range;
            if (lowR < 0)
                lowR = 0;
            if (lowG < 0)
                lowG = 0;
            if (lowB < 0)
                lowB = 0;
            if (highR > 65535)
                highR = 65535;
            if (highG > 65535)
                highG = 65535;
            if (highB > 65535)
                highB = 65535;
            var kernel = GetKernel(accelerator);
            using var rBuf = size == r.Length ? accelerator.Allocate1D(r) : accelerator.Allocate1D(r.Take(size).ToArray());
            using var gBuf = size == g.Length ? accelerator.Allocate1D(g) : accelerator.Allocate1D(g.Take(size).ToArray());
            using var bBuf = size == b.Length ? accelerator.Allocate1D(b) : accelerator.Allocate1D(b.Take(size).ToArray());
            using var aBuf = size == a.Length ? accelerator.Allocate1D(a) : accelerator.Allocate1D(a.Take(size).ToArray());
            using var outABuf = accelerator.Allocate1D<float>(size);
            if (ILGPUExecutionHelper.SyncOverride ?? accelerator.AcceleratorType == AcceleratorType.OpenCL)
            {
                using var scope = ILGPUExecutionHelper.locker.EnterScope();
                kernel(size, rBuf.View, gBuf.View, bBuf.View, aBuf.View, lowR, highR, lowG, highG, lowB, highB, outABuf.View);
                accelerator.Synchronize();
            }
            else
            {
                kernel(size, rBuf.View, gBuf.View, bBuf.View, aBuf.View, lowR, highR, lowG, highG, lowB, highB, outABuf.View);
            }
            return outABuf.GetAsArray1D();
        }
    }
}
#endif
