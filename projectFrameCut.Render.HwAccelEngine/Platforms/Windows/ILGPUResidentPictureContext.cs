#if WINDOWS || LINUX || HEADLESS
using ILGPU;
using ILGPU.Runtime;
using System.Runtime.InteropServices;
using projectFrameCut.Render.HwAccelEngine.Effect;
using projectFrameCut.Render.RenderAPIBase.EffectAndMixture;
using projectFrameCut.Render.WindowsRender;
using GpuHalf = ILGPU.Half;

namespace projectFrameCut.Render.HwAccelEngine;
internal sealed class ILGPUResidentPictureContext : ResidentPictureContext, IHwAccelPictureScRgb
{
    private static int deviceIndex;
    private readonly DeviceLease lease;
    private readonly Accelerator accelerator;
    private readonly AcceleratorStream stream;
    private readonly Action<AcceleratorStream, Index1D, ArrayView<ResidentKernelPixel>, ArrayView<ResidentKernelPixel>, ArrayView<ResidentKernelPixel>, ArrayView<float>> kernel;
    private static readonly System.Runtime.CompilerServices.ConditionalWeakTable<Accelerator, Action<AcceleratorStream, Index1D, ArrayView<ResidentKernelPixel>, ArrayView<ResidentKernelPixel>, ArrayView<ResidentKernelPixel>, ArrayView<float>>> kernels = new();
    private static readonly System.Runtime.CompilerServices.ConditionalWeakTable<Accelerator, Action<AcceleratorStream, Index1D, ArrayView<ResidentKernelPixel>, ArrayView<GpuHalf>, int, int, float>> scRgbKernels = new();

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
            if (accelerator is ILGPU.Runtime.OpenCL.CLAccelerator cl && !cl.Capabilities.Float16)
                throw new NotSupportedException($"OpenCL device '{accelerator.Name}' requires cl_khr_fp16 for Half channels.");
            kernel = kernels.GetValue(accelerator, static a => a.LoadAutoGroupedKernel<Index1D, ArrayView<ResidentKernelPixel>, ArrayView<ResidentKernelPixel>, ArrayView<ResidentKernelPixel>, ArrayView<float>>(ResidentKernel.Kernel));
            stream = accelerator.CreateStream();
        }
        catch { lease.Dispose(); throw; }
    }

    public override IEnumerable<IHwAccelPictureContext> CreateSiblingContexts()
    {
        foreach (var a in lease.Accelerators)
            if (!ReferenceEquals(a, accelerator)) yield return new ILGPUResidentPictureContext(a, lease);
    }

    private sealed class Buffer(MemoryBuffer1D<byte, Stride1D.Dense> data) : ResidentBuffer
    {
        internal readonly MemoryBuffer1D<byte, Stride1D.Dense> Data = data;
        internal override int Length => checked((int)Data.Length);
        internal override void Destroy() => Data.Dispose();
    }

    private sealed class PinnedArray(byte[] values) : IDisposable
    {
        private GCHandle pin = GCHandle.Alloc(values, GCHandleType.Pinned);
        public void Dispose() { if (pin.IsAllocated) pin.Free(); }
    }

    protected override ResidentBuffer CreateBuffer(int length) => new Buffer(accelerator.Allocate1D<byte>(length));
    protected override void WriteBuffer(ResidentBuffer buffer, byte[] values)
    {
        BeginSubmission();
        var pin = new PinnedArray(values);
        KeepUntilComplete(pin, values.Length);
        ((Buffer)buffer).Data.View.BaseView.CopyFromCPUUnsafeAsync(stream, ref values[0], values.Length);
    }
    protected override byte[] Read(ResidentBuffer buffer) => ((Buffer)buffer).Data.GetAsArray1D(stream);
    protected override ResidentBuffer Dispatch(ResidentBuffer source, ResidentBuffer second, float[] parameters, int count)
    {
        BeginSubmission();
        var output = RentBuffer(checked((int)parameters[3] * (int)parameters[4] * ResidentPixel.Size));
        try
        {
            using var p = UploadBuffer(ResidentPixel.PackParameters(parameters));
            if (ILGPUExecutionHelper.SyncOverride ?? accelerator.AcceleratorType == AcceleratorType.OpenCL)
            {
                using var scope = ILGPUExecutionHelper.locker.EnterScope();
                kernel(stream, count, ((Buffer)source).Data.View.BaseView.Cast<ResidentKernelPixel>(), ((Buffer)second).Data.View.BaseView.Cast<ResidentKernelPixel>(), ((Buffer)output).Data.View.BaseView.Cast<ResidentKernelPixel>(), ((Buffer)p).Data.View.BaseView.Cast<float>());
                if (ILGPUExecutionHelper.SyncOverride == true) Complete();
            }
            else kernel(stream, count, ((Buffer)source).Data.View.BaseView.Cast<ResidentKernelPixel>(), ((Buffer)second).Data.View.BaseView.Cast<ResidentKernelPixel>(), ((Buffer)output).Data.View.BaseView.Cast<ResidentKernelPixel>(), ((Buffer)p).Data.View.BaseView.Cast<float>());
            return output;
        }
        catch { output.Dispose(); throw; }
    }

    public byte[] DownloadScRgb(IHwAccelPicture source, Func<int, byte[]>? allocatePixels = null)
    {
        if (source is not ResidentPicture picture || !ReferenceEquals(picture.Context, this))
            throw new ArgumentException("Device contexts must match.", nameof(source));
        using var input = (ResidentPicture)picture.Retain();
        int length = checked(input.Pixels * 8);
        var pixels = allocatePixels?.Invoke(length) ?? GC.AllocateUninitializedArray<byte>(length);
        if (pixels.Length != length) throw new InvalidDataException("Invalid scRGB preview pixel buffer length.");
        lock (Gate)
        {
            var convert = scRgbKernels.GetValue(accelerator, static a =>
            {
                var loaded = a.LoadAutoGroupedKernel<Index1D, ArrayView<ResidentKernelPixel>, ArrayView<GpuHalf>, int, int, float>(ScRgbKernel.Kernel);
                LogDiagnostic($"[HwAccelEngine] scRGB preview kernel ready: device={a.Name}, backend={a.AcceleratorType}.");
                return loaded;
            });
            BeginSubmission();
            using var output = RentBuffer(length);
            using var pin = new PinnedArray(pixels);
            void Download()
            {
                try
                {
                    convert(stream, input.Pixels, ((Buffer)input.Storage.Buffer).Data.View.BaseView.Cast<ResidentKernelPixel>(),
                        ((Buffer)output).Data.View.BaseView.Cast<GpuHalf>(), input.HasAlphaChannel ? 1 : 0, input.IsHdr ? 1 : 0, (float)input.MaximumBrightness);
                    ((Buffer)output).Data.View.BaseView.CopyToCPUUnsafeAsync(stream, ref pixels[0], pixels.Length);
                }
                finally { Complete(); }
            }
            if (ILGPUExecutionHelper.SyncOverride ?? accelerator.AcceleratorType == AcceleratorType.OpenCL)
            {
                using var scope = ILGPUExecutionHelper.locker.EnterScope();
                Download();
            }
            else Download();
        }
        return pixels;
    }

    protected override void Synchronize() => stream.Synchronize();
    protected override void Release()
    {
        try { stream.Dispose(); }
        finally { lease.Dispose(); }
    }
}
#endif
