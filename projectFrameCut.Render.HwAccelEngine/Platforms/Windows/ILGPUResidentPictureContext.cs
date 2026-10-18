#if WINDOWS || LINUX || HEADLESS
using ILGPU;
using ILGPU.Runtime;
using System.Runtime.InteropServices;
using projectFrameCut.Render.HwAccelEngine.Effect;
using projectFrameCut.Render.RenderAPIBase.EffectAndMixture;
using projectFrameCut.Render.WindowsRender;

namespace projectFrameCut.Render.HwAccelEngine;
internal sealed class ILGPUResidentPictureContext : ResidentPictureContext
{
    private static int deviceIndex;
    private readonly DeviceLease lease;
    private readonly Accelerator accelerator;
    private readonly AcceleratorStream stream;
    private readonly Action<AcceleratorStream, Index1D, ArrayView<float>, ArrayView<float>, ArrayView<float>, ArrayView<float>> kernel;
    private static readonly System.Runtime.CompilerServices.ConditionalWeakTable<Accelerator, Action<AcceleratorStream, Index1D, ArrayView<float>, ArrayView<float>, ArrayView<float>, ArrayView<float>>> kernels = new();

    private sealed class DeviceLease(AcceleratorsManager.ExecutionLease execution) : IDisposable
    {
        private int references = 1;
        internal Accelerator[] Accelerators => execution.Accelerators;
        internal DeviceLease Retain() { Interlocked.Increment(ref references); return this; }
        public void Dispose() { if (Interlocked.Decrement(ref references) == 0) execution.Dispose(); }
    }

    internal ILGPUResidentPictureContext() : this(null, null) { }
    private ILGPUResidentPictureContext(Accelerator? selected, DeviceLease? shared)
    {
        lease = shared?.Retain() ?? new DeviceLease(AcceleratorsManager.AcquireExecution());
        try
        {
            accelerator = selected ?? ILGPUExecutionHelper.SelectAccelerator(lease.Accelerators, ref deviceIndex);
            kernel = kernels.GetValue(accelerator, static a => a.LoadAutoGroupedKernel<Index1D, ArrayView<float>, ArrayView<float>, ArrayView<float>, ArrayView<float>>(ResidentKernel.Kernel));
            stream = accelerator.CreateStream();
        }
        catch { lease.Dispose(); throw; }
    }

    public override IEnumerable<IHwAccelPictureContext> CreateSiblingContexts()
    {
        foreach (var a in lease.Accelerators)
            if (!ReferenceEquals(a, accelerator)) yield return new ILGPUResidentPictureContext(a, lease);
    }

    private sealed class Buffer(MemoryBuffer1D<float, Stride1D.Dense> data) : ResidentBuffer
    {
        internal readonly MemoryBuffer1D<float, Stride1D.Dense> Data = data;
        internal override int Length => checked((int)Data.Length);
        internal override void Destroy() => Data.Dispose();
    }

    private sealed class PinnedArray(float[] values) : IDisposable
    {
        private GCHandle pin = GCHandle.Alloc(values, GCHandleType.Pinned);
        public void Dispose() { if (pin.IsAllocated) pin.Free(); }
    }

    protected override ResidentBuffer CreateBuffer(int length) => new Buffer(accelerator.Allocate1D<float>(length));
    protected override void WriteBuffer(ResidentBuffer buffer, float[] values)
    {
        BeginSubmission();
        var pin = new PinnedArray(values);
        KeepUntilComplete(pin, (long)values.Length * sizeof(float));
        ((Buffer)buffer).Data.View.BaseView.CopyFromCPUUnsafeAsync(stream, ref values[0], values.Length);
    }
    protected override float[] Read(ResidentBuffer buffer) => ((Buffer)buffer).Data.GetAsArray1D(stream);
    protected override ResidentBuffer Dispatch(ResidentBuffer source, ResidentBuffer second, float[] parameters, int count)
    {
        BeginSubmission();
        var output = RentBuffer(checked((int)parameters[3] * (int)parameters[4] * (parameters[14] > 0 ? 5 : 4)));
        try
        {
            using var p = UploadBuffer(parameters);
            if (ILGPUExecutionHelper.SyncOverride ?? accelerator.AcceleratorType == AcceleratorType.OpenCL)
            {
                using var scope = ILGPUExecutionHelper.locker.EnterScope();
                kernel(stream, count, ((Buffer)source).Data.View, ((Buffer)second).Data.View, ((Buffer)output).Data.View, ((Buffer)p).Data.View);
                if (ILGPUExecutionHelper.SyncOverride == true) Complete();
            }
            else kernel(stream, count, ((Buffer)source).Data.View, ((Buffer)second).Data.View, ((Buffer)output).Data.View, ((Buffer)p).Data.View);
            return output;
        }
        catch { output.Dispose(); throw; }
    }

    protected override void Synchronize() => stream.Synchronize();
    protected override void Release()
    {
        try { stream.Dispose(); }
        finally { lease.Dispose(); }
    }
}
#endif
