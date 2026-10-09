#if WINDOWS || LINUX || HEADLESS
using ILGPU;
using ILGPU.Runtime;
using projectFrameCut.Render.WindowsRender;
using System.Runtime.CompilerServices;

namespace projectFrameCut.Render.HwAccelEngine.Effect;

public partial class CrossfadeTransform_HwAccel
{
    private int acceleratorIndex;
    private static readonly ConditionalWeakTable<Accelerator, Action<Index1D, ArrayView<float>, ArrayView<float>, ArrayView<float>, ArrayView<float>, ArrayView<float>, float>> Kernels = new();

    private float[] ComputeBlend(float[] left, float[] right, float[] leftAlpha, float[] rightAlpha, float progress)
    {
        using var lease = AcceleratorsManager.AcquireExecution();
        var accelerator = ILGPUExecutionHelper.SelectAccelerator(lease.Accelerators, ref acceleratorIndex);
        var kernel = Kernels.GetValue(accelerator, static a => a.LoadAutoGroupedStreamKernel(
            (Index1D i, ArrayView<float> l, ArrayView<float> r, ArrayView<float> la, ArrayView<float> ra, ArrayView<float> output, float p) =>
            {
                float wl = la[i] * (1 - p), wr = ra[i] * p;
                output[i] = wl + wr > 0 ? (l[i] * wl + r[i] * wr) / (wl + wr) : 0;
            }));
        using var l = accelerator.Allocate1D(left);
        using var r = accelerator.Allocate1D(right);
        using var la = accelerator.Allocate1D(leftAlpha);
        using var ra = accelerator.Allocate1D(rightAlpha);
        using var output = accelerator.Allocate1D<float>(left.Length);
        if (ILGPUExecutionHelper.SyncOverride ?? accelerator.AcceleratorType == AcceleratorType.OpenCL)
        {
            using var scope = ILGPUExecutionHelper.locker.EnterScope();
            kernel(left.Length, l.View, r.View, la.View, ra.View, output.View, progress);
            accelerator.Synchronize();
        }
        else
        {
            kernel(left.Length, l.View, r.View, la.View, ra.View, output.View, progress);
        }
        return output.GetAsArray1D();
    }
}
#endif
