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
    public partial class BlurEffect_HwAccel
    {
        private int accelIdx = 0;
        private static readonly System.Runtime.CompilerServices.ConditionalWeakTable<Accelerator, Action<Index1D, ArrayView<float>, ArrayView<float>, ArrayView<float>, ArrayView<float>, int, int>> KernelHorizontalCache = new();
        private static Action<Index1D, ArrayView<float>, ArrayView<float>, ArrayView<float>, ArrayView<float>, int, int> GetKernelHorizontal(Accelerator accelerator)
        {
            return KernelHorizontalCache.GetValue(accelerator, static acc => acc.LoadAutoGroupedStreamKernel((Index1D row, ArrayView<float> rTmp, ArrayView<float> gTmp, ArrayView<float> rIn, ArrayView<float> gIn, int w, int radius) =>
            {
                // One thread per row — sliding window box blur (O(1) per pixel instead of O(radius))
                int rowBase = row * w;
                // Initialize: x = 0
                float sumR = 0f, sumG = 0f;
                int count = 0;
                for (int k = 0; k <= radius && k < w; k++, count++)
                {
                    sumR += rIn[rowBase + k];
                    sumG += gIn[rowBase + k];
                }

                rTmp[rowBase] = sumR / count;
                gTmp[rowBase] = sumG / count;
                // Slide window right
                for (int x = 1; x < w; x++)
                {
                    if (x - radius - 1 >= 0)
                    {
                        sumR -= rIn[rowBase + x - radius - 1];
                        sumG -= gIn[rowBase + x - radius - 1];
                        count--;
                    }

                    if (x + radius < w)
                    {
                        sumR += rIn[rowBase + x + radius];
                        sumG += gIn[rowBase + x + radius];
                        count++;
                    }

                    rTmp[rowBase + x] = sumR / count;
                    gTmp[rowBase + x] = sumG / count;
                }
            }));
        }

        private static readonly System.Runtime.CompilerServices.ConditionalWeakTable<Accelerator, Action<Index1D, ArrayView<float>, ArrayView<float>, ArrayView<float>, ArrayView<float>, int, int, int>> KernelVerticalCache = new();
        private static Action<Index1D, ArrayView<float>, ArrayView<float>, ArrayView<float>, ArrayView<float>, int, int, int> GetKernelVertical(Accelerator accelerator)
        {
            return KernelVerticalCache.GetValue(accelerator, static acc => acc.LoadAutoGroupedStreamKernel((Index1D col, ArrayView<float> rOut, ArrayView<float> gOut, ArrayView<float> rTmp, ArrayView<float> gTmp, int w, int h, int radius) =>
            {
                // One thread per column — sliding window box blur
                // Initialize: y = 0
                float sumR = 0f, sumG = 0f;
                int count = 0;
                for (int k = 0; k <= radius && k < h; k++, count++)
                {
                    sumR += rTmp[k * w + col];
                    sumG += gTmp[k * w + col];
                }

                rOut[col] = sumR / count;
                gOut[col] = sumG / count;
                // Slide window down
                for (int y = 1; y < h; y++)
                {
                    if (y - radius - 1 >= 0)
                    {
                        sumR -= rTmp[(y - radius - 1) * w + col];
                        sumG -= gTmp[(y - radius - 1) * w + col];
                        count--;
                    }

                    if (y + radius < h)
                    {
                        sumR += rTmp[(y + radius) * w + col];
                        sumG += gTmp[(y + radius) * w + col];
                        count++;
                    }

                    rOut[y * w + col] = sumR / count;
                    gOut[y * w + col] = sumG / count;
                }
            }));
        }

        private FourChannelResult ComputeBlur(float[] r, float[] g, float[] b, float[] a, int w, float sigma)
        {
            using var deviceLease = AcceleratorsManager.AcquireExecution();
            var accelerator = ILGPUExecutionHelper.SelectAccelerator(deviceLease.Accelerators, ref accelIdx);
            int radius = (int)MathF.Ceiling(sigma);
            if (radius <= 0)
                radius = 1;
            int length = r.Length;
            int h = length / w;
            using var rBufIn = accelerator.Allocate1D(r);
            using var gBufIn = accelerator.Allocate1D(g);
            using var bBufIn = accelerator.Allocate1D(b);
            using var aBufIn = accelerator.Allocate1D(a);
            using var rBufTmp = accelerator.Allocate1D<float>(length);
            using var gBufTmp = accelerator.Allocate1D<float>(length);
            using var bBufTmp = accelerator.Allocate1D<float>(length);
            using var aBufTmp = accelerator.Allocate1D<float>(length);
            using var rBufOut = accelerator.Allocate1D<float>(length);
            using var gBufOut = accelerator.Allocate1D<float>(length);
            using var bBufOut = accelerator.Allocate1D<float>(length);
            using var aBufOut = accelerator.Allocate1D<float>(length);
            var kernelH = GetKernelHorizontal(accelerator);
            var kernelV = GetKernelVertical(accelerator);
            if (ILGPUExecutionHelper.SyncOverride ?? accelerator.AcceleratorType == AcceleratorType.OpenCL)
            {
                using (ILGPUExecutionHelper.locker.EnterScope())
                {
                    kernelH(h, rBufTmp.View, gBufTmp.View, rBufIn.View, gBufIn.View, w, radius);
                    kernelH(h, bBufTmp.View, aBufTmp.View, bBufIn.View, aBufIn.View, w, radius);
                    accelerator.Synchronize();
                    kernelV(w, rBufOut.View, gBufOut.View, rBufTmp.View, gBufTmp.View, w, h, radius);
                    kernelV(w, bBufOut.View, aBufOut.View, bBufTmp.View, aBufTmp.View, w, h, radius);
                    accelerator.Synchronize();
                }
            }
            else
            {
                kernelH(h, rBufTmp.View, gBufTmp.View, rBufIn.View, gBufIn.View, w, radius);
                kernelH(h, bBufTmp.View, aBufTmp.View, bBufIn.View, aBufIn.View, w, radius);
                kernelV(w, rBufOut.View, gBufOut.View, rBufTmp.View, gBufTmp.View, w, h, radius);
                kernelV(w, bBufOut.View, aBufOut.View, bBufTmp.View, aBufTmp.View, w, h, radius);
            }

            return new FourChannelResult(rBufOut.GetAsArray1D(), gBufOut.GetAsArray1D(), bBufOut.GetAsArray1D(), aBufOut.GetAsArray1D());
        }
    }
}
#endif
