#if WINDOWS || LINUX || WINNETCORE
using ILGPU;
using ILGPU.Runtime;
using projectFrameCut.Render.WindowsRender;
using System.Runtime.CompilerServices;

namespace projectFrameCut.Render.HwAccelEngine.Effect;
public partial class CropEffect_HwAccel
{
    private int acceleratorIndex;
    private readonly struct CropParameters(int sourceWidth, int sourceHeight, int x, int y, int width, int height, float angle)
    {
        public readonly int SourceWidth = sourceWidth, SourceHeight = sourceHeight, X = x, Y = y, Width = width, Height = height;
        public readonly float Cos = MathF.Cos(angle * MathF.PI / 180f), Sin = MathF.Sin(angle * MathF.PI / 180f);
    }

    private static readonly ConditionalWeakTable<Accelerator, Action<Index1D, ArrayView<float>, ArrayView<float>, CropParameters>> kernels = new();
    private static void CropKernel(Index1D index, ArrayView<float> source, ArrayView<float> output, CropParameters p)
    {
        int count = p.Width * p.Height;
        int sourceCount = p.SourceWidth * p.SourceHeight;
        float rx = index % p.Width - p.Width / 2f;
        float ry = index / p.Width - p.Height / 2f;
        float sx = p.Cos * rx - p.Sin * ry + p.X + p.Width / 2f;
        float sy = p.Sin * rx + p.Cos * ry + p.Y + p.Height / 2f;
        for (int channel = 0; channel < 4; channel++)
        {
            float value = 0;
            if (sx >= 0 && sx < p.SourceWidth && sy >= 0 && sy < p.SourceHeight)
            {
                int x0 = (int)sx, y0 = (int)sy;
                int x1 = x0 + 1 < p.SourceWidth ? x0 + 1 : x0;
                int y1 = y0 + 1 < p.SourceHeight ? y0 + 1 : y0;
                float fx = sx - x0, fy = sy - y0;
                int offset = channel * sourceCount;
                value = source[offset + y0 * p.SourceWidth + x0] * (1 - fx) * (1 - fy) + source[offset + y0 * p.SourceWidth + x1] * fx * (1 - fy) + source[offset + y1 * p.SourceWidth + x0] * (1 - fx) * fy + source[offset + y1 * p.SourceWidth + x1] * fx * fy;
            }

            output[channel * count + index] = value;
        }
    }

    private FourChannelResult ComputeCrop(float[] r, float[] g, float[] b, float[] a, int srcW, int srcH, int startX, int startY, int cropW, int cropH, float angle)
    {
        using var deviceLease = AcceleratorsManager.AcquireExecution();
        var accelerator = ILGPUExecutionHelper.SelectAccelerator(deviceLease.Accelerators, ref acceleratorIndex);
        int sourceCount = checked(srcW * srcH), count = checked(cropW * cropH);
        var input = new float[checked(sourceCount * 4)];
        Array.Copy(r, 0, input, 0, sourceCount);
        Array.Copy(g, 0, input, sourceCount, sourceCount);
        Array.Copy(b, 0, input, sourceCount * 2, sourceCount);
        Array.Copy(a, 0, input, sourceCount * 3, sourceCount);
        using var source = accelerator.Allocate1D(input);
        using var output = accelerator.Allocate1D<float>(checked(count * 4));
        var kernel = kernels.GetValue(accelerator, static acc => acc.LoadAutoGroupedStreamKernel<Index1D, ArrayView<float>, ArrayView<float>, CropParameters>(CropKernel));
        if (ILGPUExecutionHelper.SyncOverride ?? accelerator.AcceleratorType == AcceleratorType.OpenCL)
        {
            using var scope = ILGPUExecutionHelper.locker.EnterScope();
            kernel(count, source.View, output.View, new(srcW, srcH, startX, startY, cropW, cropH, angle));
            accelerator.Synchronize();
        }
        else
        {
            kernel(count, source.View, output.View, new(srcW, srcH, startX, startY, cropW, cropH, angle));
        }

        var result = output.GetAsArray1D();
        return new(result[..count], result[count..(count * 2)], result[(count * 2)..(count * 3)], result[(count * 3)..]);
    }
}
#endif
