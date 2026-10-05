#if WINDOWS || LINUX || WINNETCORE
using ILGPU;
using ILGPU.Runtime;
using projectFrameCut.Render.WindowsRender;
using System.Runtime.CompilerServices;

namespace projectFrameCut.Render.HwAccelEngine.Effect;

public partial class ResizeEffect_HwAccel
{
    private int acceleratorIndex;
    private readonly struct ResizeParameters(int sourceWidth, int sourceHeight, int width, int height)
    {
        public readonly int SourceWidth = sourceWidth, SourceHeight = sourceHeight, Width = width, Height = height;
        public readonly float RatioX = (float)sourceWidth / width, RatioY = (float)sourceHeight / height;
    }

    private static readonly ConditionalWeakTable<Accelerator, Action<Index1D, ArrayView<float>, ArrayView<float>, ResizeParameters>> kernels = new();

    private static void ResizeKernel(Index1D index, ArrayView<float> source, ArrayView<float> output, ResizeParameters p)
    {
        float sx = Math.Min(Math.Max((index % p.Width + 0.5f) * p.RatioX - 0.5f, 0), p.SourceWidth - 1);
        float sy = Math.Min(Math.Max((index / p.Width + 0.5f) * p.RatioY - 0.5f, 0), p.SourceHeight - 1);
        int x0 = (int)sx, y0 = (int)sy;
        int x1 = Math.Min(x0 + 1, p.SourceWidth - 1), y1 = Math.Min(y0 + 1, p.SourceHeight - 1);
        float fx = sx - x0, fy = sy - y0;
        int sourceCount = p.SourceWidth * p.SourceHeight, count = p.Width * p.Height;
        for (int channel = 0; channel < 4; channel++)
        {
            int offset = channel * sourceCount;
            output[channel * count + index] =
                source[offset + y0 * p.SourceWidth + x0] * (1 - fx) * (1 - fy) +
                source[offset + y0 * p.SourceWidth + x1] * fx * (1 - fy) +
                source[offset + y1 * p.SourceWidth + x0] * (1 - fx) * fy +
                source[offset + y1 * p.SourceWidth + x1] * fx * fy;
        }
    }

    private FourChannelResult ComputeResizeFloat(float[] r, float[] g, float[] b, float[] a, float srcW, float srcH, float dstW, float dstH)
    {
        using var deviceLease = AcceleratorsManager.AcquireExecution();
        var accelerator = ILGPUExecutionHelper.SelectAccelerator(deviceLease.Accelerators, ref acceleratorIndex);
        int sourceCount = checked((int)srcW * (int)srcH), count = checked((int)dstW * (int)dstH);
        var input = new float[checked(sourceCount * 4)];
        Array.Copy(r, 0, input, 0, sourceCount);
        Array.Copy(g, 0, input, sourceCount, sourceCount);
        Array.Copy(b, 0, input, sourceCount * 2, sourceCount);
        Array.Copy(a, 0, input, sourceCount * 3, sourceCount);
        using var source = accelerator.Allocate1D(input);
        using var output = accelerator.Allocate1D<float>(checked(count * 4));
        var kernel = kernels.GetValue(accelerator, static acc => acc.LoadAutoGroupedStreamKernel<Index1D, ArrayView<float>, ArrayView<float>, ResizeParameters>(ResizeKernel));
        if (ILGPUExecutionHelper.SyncOverride ?? accelerator.AcceleratorType == AcceleratorType.OpenCL)
        {
            using var scope = ILGPUExecutionHelper.locker.EnterScope();
            kernel(count, source.View, output.View, new((int)srcW, (int)srcH, (int)dstW, (int)dstH));
            accelerator.Synchronize();
        }
        else
        {
            kernel(count, source.View, output.View, new((int)srcW, (int)srcH, (int)dstW, (int)dstH));
        }
        var result = output.GetAsArray1D();
        return new(result[..count], result[count..(count * 2)], result[(count * 2)..(count * 3)], result[(count * 3)..]);
    }

    private FourChannelResult8 ComputeResizeByte(float[] r, float[] g, float[] b, float[] a, float srcW, float srcH, float dstW, float dstH)
    {
        var result = ComputeResizeFloat(r, g, b, a, srcW, srcH, dstW, dstH);
        return new(result.R.Select(v => (byte)Math.Clamp(v + 0.5f, 0, 255)).ToArray(),
            result.G.Select(v => (byte)Math.Clamp(v + 0.5f, 0, 255)).ToArray(),
            result.B.Select(v => (byte)Math.Clamp(v + 0.5f, 0, 255)).ToArray(), result.A);
    }

    private FourChannelResult16 ComputeResizeUshort(float[] r, float[] g, float[] b, float[] a, float srcW, float srcH, float dstW, float dstH)
    {
        var result = ComputeResizeFloat(r, g, b, a, srcW, srcH, dstW, dstH);
        return new(result.R.Select(v => (ushort)Math.Clamp(v + 0.5f, 0, 65535)).ToArray(),
            result.G.Select(v => (ushort)Math.Clamp(v + 0.5f, 0, 65535)).ToArray(),
            result.B.Select(v => (ushort)Math.Clamp(v + 0.5f, 0, 65535)).ToArray(), result.A);
    }
}
#endif
