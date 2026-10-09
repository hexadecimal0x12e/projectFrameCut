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
    public partial class ScreenMixture_HwAccel
    {
        private int accelIdx;
        private static readonly System.Runtime.CompilerServices.ConditionalWeakTable<Accelerator, Action<Index1D, ArrayView<float>, ArrayView<float>, ArrayView<float>, ArrayView<float>, ArrayView<float>, ArrayView<float>>> KernelCache = new();
        private static Action<Index1D, ArrayView<float>, ArrayView<float>, ArrayView<float>, ArrayView<float>, ArrayView<float>, ArrayView<float>> GetKernel(Accelerator acc)
        {
            return KernelCache.GetValue(acc, static a => a.LoadAutoGroupedStreamKernel((Index1D i, ArrayView<float> top, ArrayView<float> bottom, ArrayView<float> topAlpha, ArrayView<float> bottomAlpha, ArrayView<float> outC, ArrayView<float> outA) =>
            {
                float aA = topAlpha[i];
                float bA = bottomAlpha[i];
                float outAlpha = aA + bA * (1f - aA);
                if (outAlpha < 1e-6f)
                {
                    outC[i] = 0f;
                    outA[i] = 0f;
                }
                else
                {
                    float blended = 65535f - (65535f - top[i]) * (65535f - bottom[i]) / 65535f;
                    float result = (blended * aA + bottom[i] * bA * (1f - aA)) / outAlpha;
                    if (result < 0f)
                        result = 0f;
                    if (result > 65535f)
                        result = 65535f;
                    outC[i] = result;
                    outA[i] = outAlpha;
                }
            }));
        }

        protected override (ushort[] Color, float[] Alpha) ComputeBlend(float[] top, float[] bottom, float[] topAlpha, float[] bottomAlpha, int pixelCount)
        {
            using var scope = nativeLock.EnterScope();
            ObjectDisposedException.ThrowIf(disposed, this);
            return RunBlend(top, bottom, topAlpha, bottomAlpha, pixelCount);
        }

        private static readonly System.Runtime.CompilerServices.ConditionalWeakTable<Accelerator, Action<Index1D, ArrayView<float>, ArrayView<ushort>>> FloatToUshortKernelCache = new();
        private static Action<Index1D, ArrayView<float>, ArrayView<ushort>> GetFloatToUshortKernel(Accelerator accelerator)
        {
            return FloatToUshortKernelCache.GetValue(accelerator, static acc => acc.LoadAutoGroupedStreamKernel((Index1D i, ArrayView<float> src, ArrayView<ushort> dst) =>
            {
                float v = src[i];
                if (v < 0f)
                    v = 0f;
                if (v > 65535f)
                    v = 65535f;
                dst[i] = (ushort)v;
            }));
        }

        /// <summary>

        /// </summary>
        private (ushort[] Color, float[] Alpha) RunBlend(float[] top, float[] bottom, float[] topAlpha, float[] bottomAlpha, int pixelCount)
        {
            using var deviceLease = AcceleratorsManager.AcquireExecution();
            var accelerator = ILGPUExecutionHelper.SelectAccelerator(deviceLease.Accelerators, ref accelIdx);
            using var topBuf = accelerator.Allocate1D(top);
            using var bottomBuf = accelerator.Allocate1D(bottom);
            using var topAlphaBuf = accelerator.Allocate1D(topAlpha);
            using var bottomAlphaBuf = accelerator.Allocate1D(bottomAlpha);
            using var outCBuf = accelerator.Allocate1D<float>(pixelCount);
            using var outCBufUshort = accelerator.Allocate1D<ushort>(pixelCount);
            using var outABuf = accelerator.Allocate1D<float>(pixelCount);
            var kernel = GetKernel(accelerator);
            var convertKernel = GetFloatToUshortKernel(accelerator);
            if (ILGPUExecutionHelper.SyncOverride ?? accelerator.AcceleratorType == AcceleratorType.OpenCL)
            {
                using (ILGPUExecutionHelper.locker.EnterScope())
                {
                    kernel(pixelCount, topBuf.View, bottomBuf.View, topAlphaBuf.View, bottomAlphaBuf.View, outCBuf.View, outABuf.View);
                    convertKernel(pixelCount, outCBuf.View, outCBufUshort.View);
                    accelerator.Synchronize();
                }
            }
            else
            {
                kernel(pixelCount, topBuf.View, bottomBuf.View, topAlphaBuf.View, bottomAlphaBuf.View, outCBuf.View, outABuf.View);
                convertKernel(pixelCount, outCBuf.View, outCBufUshort.View);
            }

            var ushortOut = outCBufUshort.GetAsArray1D();
            var outA = outABuf.GetAsArray1D();
            return (ushortOut, outA);
        }
    }
}
#endif
