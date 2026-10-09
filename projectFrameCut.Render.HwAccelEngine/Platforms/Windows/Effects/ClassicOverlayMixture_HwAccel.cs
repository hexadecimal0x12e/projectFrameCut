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
    public partial class ClassicOverlayMixture_HwAccel
    {
        private static readonly System.Collections.Concurrent.ConcurrentDictionary<int, float[]> OnesCache = new();
        private static float[] GetOnes(int length)
        {
            if (length <= 0)
                return Array.Empty<float>();
            return OnesCache.GetOrAdd(length, static len =>
            {
                var arr = new float[len];
                Array.Fill(arr, 1f);
                return arr;
            });
        }

        private static readonly System.Runtime.CompilerServices.ConditionalWeakTable<Accelerator, Action<Index1D, ArrayView<float>, ArrayView<float>, ArrayView<float>, ArrayView<float>, ArrayView<float>, ArrayView<float>>> KernelFloatCache = new();
        private static Action<Index1D, ArrayView<float>, ArrayView<float>, ArrayView<float>, ArrayView<float>, ArrayView<float>, ArrayView<float>> GetKernelFloat(Accelerator accelerator)
        {
            return KernelFloatCache.GetValue(accelerator, static acc =>
            {
                return acc.LoadAutoGroupedStreamKernel((Index1D i, ArrayView<float> a, ArrayView<float> b, ArrayView<float> aAlpha, ArrayView<float> bAlpha, ArrayView<float> c, ArrayView<float> cAlpha) =>
                {
                    if (aAlpha[i] == 1f)
                    {
                        c[i] = a[i];
                        cAlpha[i] = 1f;
                    }
                    else if (aAlpha[i] <= 0.05f)
                    {
                        c[i] = b[i];
                        cAlpha[i] = bAlpha[i];
                    }
                    else
                    {
                        float aA = aAlpha[i];
                        float bA = bAlpha[i];
                        float outA = aA + bA * (1 - aA);
                        if (outA < 1e-6f)
                        {
                            c[i] = 0;
                            cAlpha[i] = 0f;
                        }
                        else
                        {
                            float aC = a[i] * aA / outA;
                            float bC = b[i] * bA * (1 - aA) / outA;
                            float outC = aC + bC;
                            if (outC < 0f)
                                outC = 0f;
                            if (outC > ushort.MaxValue)
                                outC = ushort.MaxValue;
                            c[i] = outC;
                            cAlpha[i] = outA;
                        }
                    }
                });
            });
        }

        private static readonly System.Runtime.CompilerServices.ConditionalWeakTable<Accelerator, Action<Index1D, ArrayView<float>, ArrayView<float>, ArrayView<float>, ArrayView<float>, ArrayView<ushort>, ArrayView<float>>> KernelUShortCache = new();
        private static Action<Index1D, ArrayView<float>, ArrayView<float>, ArrayView<float>, ArrayView<float>, ArrayView<ushort>, ArrayView<float>> GetKernelUShort(Accelerator accelerator)
        {
            return KernelUShortCache.GetValue(accelerator, static acc =>
            {
                return acc.LoadAutoGroupedStreamKernel((Index1D i, ArrayView<float> a, ArrayView<float> b, ArrayView<float> aAlpha, ArrayView<float> bAlpha, ArrayView<ushort> c, ArrayView<float> cAlpha) =>
                {
                    if (aAlpha[i] == 1f)
                    {
                        c[i] = (ushort)a[i];
                        cAlpha[i] = 1f;
                    }
                    else if (aAlpha[i] <= 0.05f)
                    {
                        c[i] = (ushort)b[i];
                        cAlpha[i] = bAlpha[i];
                    }
                    else
                    {
                        float aA = aAlpha[i];
                        float bA = bAlpha[i];
                        float outA = aA + bA * (1 - aA);
                        if (outA < 1e-6f)
                        {
                            c[i] = 0;
                            cAlpha[i] = 0f;
                        }
                        else
                        {
                            float aC = a[i] * aA / outA;
                            float bC = b[i] * bA * (1 - aA) / outA;
                            float outC = aC + bC;
                            if (outC < 0f)
                                outC = 0f;
                            if (outC > ushort.MaxValue)
                                outC = ushort.MaxValue;
                            c[i] = (ushort)outC;
                            cAlpha[i] = outA;
                        }
                    }
                });
            });
        }

        private static readonly System.Runtime.CompilerServices.ConditionalWeakTable<Accelerator, Action<Index1D, ArrayView<float>, ArrayView<float>, ArrayView<float>, ArrayView<float>, ArrayView<byte>, ArrayView<float>>> KernelByteCache = new();
        private static Action<Index1D, ArrayView<float>, ArrayView<float>, ArrayView<float>, ArrayView<float>, ArrayView<byte>, ArrayView<float>> GetKernelByte(Accelerator accelerator)
        {
            return KernelByteCache.GetValue(accelerator, static acc =>
            {
                return acc.LoadAutoGroupedStreamKernel((Index1D i, ArrayView<float> a, ArrayView<float> b, ArrayView<float> aAlpha, ArrayView<float> bAlpha, ArrayView<byte> c, ArrayView<float> cAlpha) =>
                {
                    if (aAlpha[i] == 1f)
                    {
                        float v = a[i] / 257.0f;
                        if (v < 0f)
                            v = 0f;
                        if (v > 255f)
                            v = 255f;
                        c[i] = (byte)v;
                        cAlpha[i] = 1f;
                    }
                    else if (aAlpha[i] <= 0.05f)
                    {
                        float v = b[i] / 257.0f;
                        if (v < 0f)
                            v = 0f;
                        if (v > 255f)
                            v = 255f;
                        c[i] = (byte)v;
                        cAlpha[i] = bAlpha[i];
                    }
                    else
                    {
                        float aA = aAlpha[i];
                        float bA = bAlpha[i];
                        float outA = aA + bA * (1 - aA);
                        if (outA < 1e-6f)
                        {
                            c[i] = 0;
                            cAlpha[i] = 0f;
                        }
                        else
                        {
                            float aC = a[i] * aA / outA;
                            float bC = b[i] * bA * (1 - aA) / outA;
                            float outC = aC + bC;
                            if (outC < 0f)
                                outC = 0f;
                            if (outC > ushort.MaxValue)
                                outC = ushort.MaxValue;
                            float v = outC / 257.0f;
                            if (v < 0f)
                                v = 0f;
                            if (v > 255f)
                                v = 255f;
                            c[i] = (byte)v;
                            cAlpha[i] = outA;
                        }
                    }
                });
            });
        }

        private int accelIdx = 0;
        protected override (byte[] Color, float[] Alpha) Overlay8(float[] top, float[] bottom, float[] topAlpha, float[] bottomAlpha, int pixelCount)
        {
            using var scope = nativeLock.EnterScope();
            ObjectDisposedException.ThrowIf(disposed, this);
            using var deviceLease = AcceleratorsManager.AcquireExecution();
            var accelerator = ILGPUExecutionHelper.SelectAccelerator(deviceLease.Accelerators, ref accelIdx);
            using var a = top.Length == pixelCount ? accelerator.Allocate1D(top) : accelerator.Allocate1D(top.Take(pixelCount).ToArray());
            using var b = bottom.Length == pixelCount ? accelerator.Allocate1D(bottom) : accelerator.Allocate1D(bottom.Take(pixelCount).ToArray());
            using var aAlphaBuffer = topAlpha.Length == pixelCount ? accelerator.Allocate1D(topAlpha) : accelerator.Allocate1D(topAlpha.Take(pixelCount).ToArray());
            using var bAlphaBuffer = bottomAlpha.Length == pixelCount ? accelerator.Allocate1D(bottomAlpha) : accelerator.Allocate1D(bottomAlpha.Take(pixelCount).ToArray());
            using var outBuffer = accelerator.Allocate1D<byte>(pixelCount);
            using var outAlphaBuffer = accelerator.Allocate1D<float>(pixelCount);
            var krnl = GetKernelByte(accelerator);
            if (ILGPUExecutionHelper.SyncOverride ?? accelerator.AcceleratorType == AcceleratorType.OpenCL)
            {
                using (ILGPUExecutionHelper.locker.EnterScope())
                {
                    krnl(pixelCount, a.View, b.View, aAlphaBuffer.View, bAlphaBuffer.View, outBuffer.View, outAlphaBuffer.View);
                    accelerator.Synchronize();
                }
            }
            else
            {
                krnl(pixelCount, a.View, b.View, aAlphaBuffer.View, bAlphaBuffer.View, outBuffer.View, outAlphaBuffer.View);
            }

            var result = outBuffer.GetAsArray1D();
            var alphaResult = outAlphaBuffer.GetAsArray1D();
            return (result, alphaResult);
        }

        protected override (ushort[] Color, float[] Alpha) Overlay16(float[] top, float[] bottom, float[] topAlpha, float[] bottomAlpha, int pixelCount)
        {
            using var scope = nativeLock.EnterScope();
            ObjectDisposedException.ThrowIf(disposed, this);
            using var deviceLease = AcceleratorsManager.AcquireExecution();
            var accelerator = ILGPUExecutionHelper.SelectAccelerator(deviceLease.Accelerators, ref accelIdx);
            using var a = top.Length == pixelCount ? accelerator.Allocate1D(top) : accelerator.Allocate1D(top.Take(pixelCount).ToArray());
            using var b = bottom.Length == pixelCount ? accelerator.Allocate1D(bottom) : accelerator.Allocate1D(bottom.Take(pixelCount).ToArray());
            using var aAlphaBuffer = topAlpha.Length == pixelCount ? accelerator.Allocate1D(topAlpha) : accelerator.Allocate1D(topAlpha.Take(pixelCount).ToArray());
            using var bAlphaBuffer = bottomAlpha.Length == pixelCount ? accelerator.Allocate1D(bottomAlpha) : accelerator.Allocate1D(bottomAlpha.Take(pixelCount).ToArray());
            using var outBuffer = accelerator.Allocate1D<ushort>(pixelCount);
            using var outAlphaBuffer = accelerator.Allocate1D<float>(pixelCount);
            var krnl = GetKernelUShort(accelerator);
            if (ILGPUExecutionHelper.SyncOverride ?? accelerator.AcceleratorType == AcceleratorType.OpenCL)
            {
                using (ILGPUExecutionHelper.locker.EnterScope())
                {
                    krnl(pixelCount, a.View, b.View, aAlphaBuffer.View, bAlphaBuffer.View, outBuffer.View, outAlphaBuffer.View);
                    accelerator.Synchronize();
                }
            }
            else
            {
                krnl(pixelCount, a.View, b.View, aAlphaBuffer.View, bAlphaBuffer.View, outBuffer.View, outAlphaBuffer.View);
            }

            var result = outBuffer.GetAsArray1D();
            var alphaResult = outAlphaBuffer.GetAsArray1D();
            return (result, alphaResult);
        }

        protected override (float[] Color, float[] Alpha) OverlayHdr(float[] top, float[] bottom, float[] topAlpha, float[] bottomAlpha, int pixelCount)
        {
            using var scope = nativeLock.EnterScope();
            ObjectDisposedException.ThrowIf(disposed, this);
            using var deviceLease = AcceleratorsManager.AcquireExecution();
            var accelerator = ILGPUExecutionHelper.SelectAccelerator(deviceLease.Accelerators, ref accelIdx);
            using var a = top.Length == pixelCount ? accelerator.Allocate1D(top) : accelerator.Allocate1D(top.Take(pixelCount).ToArray());
            using var b = bottom.Length == pixelCount ? accelerator.Allocate1D(bottom) : accelerator.Allocate1D(bottom.Take(pixelCount).ToArray());
            using var aAlphaBuffer = topAlpha.Length == pixelCount ? accelerator.Allocate1D(topAlpha) : accelerator.Allocate1D(topAlpha.Take(pixelCount).ToArray());
            using var bAlphaBuffer = bottomAlpha.Length == pixelCount ? accelerator.Allocate1D(bottomAlpha) : accelerator.Allocate1D(bottomAlpha.Take(pixelCount).ToArray());
            using var outBuffer = accelerator.Allocate1D<float>(pixelCount);
            using var outAlphaBuffer = accelerator.Allocate1D<float>(pixelCount);
            var krnl = GetKernelFloat(accelerator);
            if (ILGPUExecutionHelper.SyncOverride ?? accelerator.AcceleratorType == AcceleratorType.OpenCL)
            {
                using (ILGPUExecutionHelper.locker.EnterScope())
                {
                    krnl(pixelCount, a.View, b.View, aAlphaBuffer.View, bAlphaBuffer.View, outBuffer.View, outAlphaBuffer.View);
                    accelerator.Synchronize();
                }
            }
            else
            {
                krnl(pixelCount, a.View, b.View, aAlphaBuffer.View, bAlphaBuffer.View, outBuffer.View, outAlphaBuffer.View);
            }

            var result = outBuffer.GetAsArray1D();
            var alphaResult = outAlphaBuffer.GetAsArray1D();
            return (result, alphaResult);
        }

        private int ApproximateaccelIdx = 0;
        private static readonly System.Collections.Concurrent.ConcurrentDictionary<int, float[]> ApproximateOnesCache = new();
        private static float[] ApproximateGetOnes(int length)
        {
            if (length <= 0)
                return Array.Empty<float>();
            return ApproximateOnesCache.GetOrAdd(length, static len =>
            {
                var arr = new float[len];
                Array.Fill(arr, 1f);
                return arr;
            });
        }

        private static readonly System.Runtime.CompilerServices.ConditionalWeakTable<Accelerator, Action<Index1D, ArrayView<float>, ArrayView<float>, ArrayView<float>, ArrayView<float>, ArrayView<float>, ArrayView<float>>> ApproximateKernelFloatCache = new();
        private static Action<Index1D, ArrayView<float>, ArrayView<float>, ArrayView<float>, ArrayView<float>, ArrayView<float>, ArrayView<float>> ApproximateGetKernelFloat(Accelerator accelerator)
        {
            return ApproximateKernelFloatCache.GetValue(accelerator, static acc =>
            {
                return acc.LoadAutoGroupedStreamKernel((Index1D i, ArrayView<float> a, ArrayView<float> b, ArrayView<float> aAlpha, ArrayView<float> bAlpha, ArrayView<float> c, ArrayView<float> cAlpha) =>
                {
                    float aA = aAlpha[i];
                    float bA = bAlpha[i];
                    float outA = aA + bA * (1 - aA);
                    if (outA < 1e-6f)
                    {
                        c[i] = 0;
                        cAlpha[i] = 0f;
                    }
                    else
                    {
                        float aC = a[i] * aA / outA;
                        float bC = b[i] * bA * (1 - aA) / outA;
                        float outC = aC + bC;
                        if (outC < 0f)
                            outC = 0f;
                        if (outC > ushort.MaxValue)
                            outC = ushort.MaxValue;
                        c[i] = outC;
                        cAlpha[i] = outA;
                    }
                });
            });
        }

        private static readonly System.Runtime.CompilerServices.ConditionalWeakTable<Accelerator, Action<Index1D, ArrayView<float>, ArrayView<float>, ArrayView<float>, ArrayView<float>, ArrayView<ushort>, ArrayView<float>>> ApproximateKernelUShortCache = new();
        private static Action<Index1D, ArrayView<float>, ArrayView<float>, ArrayView<float>, ArrayView<float>, ArrayView<ushort>, ArrayView<float>> ApproximateGetKernelUShort(Accelerator accelerator)
        {
            return ApproximateKernelUShortCache.GetValue(accelerator, static acc =>
            {
                return acc.LoadAutoGroupedStreamKernel((Index1D i, ArrayView<float> a, ArrayView<float> b, ArrayView<float> aAlpha, ArrayView<float> bAlpha, ArrayView<ushort> c, ArrayView<float> cAlpha) =>
                {
                    float aA = aAlpha[i];
                    float bA = bAlpha[i];
                    float outA = aA + bA * (1 - aA);
                    if (outA < 1e-6f)
                    {
                        c[i] = 0;
                        cAlpha[i] = 0f;
                    }
                    else
                    {
                        float aC = a[i] * aA / outA;
                        float bC = b[i] * bA * (1 - aA) / outA;
                        float outC = aC + bC;
                        if (outC < 0f)
                            outC = 0f;
                        if (outC > ushort.MaxValue)
                            outC = ushort.MaxValue;
                        c[i] = (ushort)outC;
                        cAlpha[i] = outA;
                    }
                });
            });
        }

        private static readonly System.Runtime.CompilerServices.ConditionalWeakTable<Accelerator, Action<Index1D, ArrayView<float>, ArrayView<float>, ArrayView<float>, ArrayView<float>, ArrayView<byte>, ArrayView<float>>> ApproximateKernelByteCache = new();
        private static Action<Index1D, ArrayView<float>, ArrayView<float>, ArrayView<float>, ArrayView<float>, ArrayView<byte>, ArrayView<float>> ApproximateGetKernelByte(Accelerator accelerator)
        {
            return ApproximateKernelByteCache.GetValue(accelerator, static acc =>
            {
                return acc.LoadAutoGroupedStreamKernel((Index1D i, ArrayView<float> a, ArrayView<float> b, ArrayView<float> aAlpha, ArrayView<float> bAlpha, ArrayView<byte> c, ArrayView<float> cAlpha) =>
                {
                    float aA = aAlpha[i];
                    float bA = bAlpha[i];
                    float outA = aA + bA * (1 - aA);
                    if (outA < 1e-6f)
                    {
                        c[i] = 0;
                        cAlpha[i] = 0f;
                    }
                    else
                    {
                        float aC = a[i] * aA / outA;
                        float bC = b[i] * bA * (1 - aA) / outA;
                        float outC = aC + bC;
                        if (outC < 0f)
                            outC = 0f;
                        if (outC > byte.MaxValue)
                            outC = byte.MaxValue;
                        c[i] = (byte)outC;
                        cAlpha[i] = outA;
                    }
                });
            });
        }

        protected override (byte[] Color, float[] Alpha) ApproximateOverlay8(float[] top, float[] bottom, float[] topAlpha, float[] bottomAlpha, int pixelCount)
        {
            using var scope = nativeLock.EnterScope();
            ObjectDisposedException.ThrowIf(disposed, this);
            using var deviceLease = AcceleratorsManager.AcquireExecution();
            var accelerator = ILGPUExecutionHelper.SelectAccelerator(deviceLease.Accelerators, ref ApproximateaccelIdx);
            using var a = top.Length == pixelCount ? accelerator.Allocate1D(top) : accelerator.Allocate1D(top.Take(pixelCount).ToArray());
            using var b = bottom.Length == pixelCount ? accelerator.Allocate1D(bottom) : accelerator.Allocate1D(bottom.Take(pixelCount).ToArray());
            using var aAlphaBuffer = topAlpha.Length == pixelCount ? accelerator.Allocate1D(topAlpha) : accelerator.Allocate1D(topAlpha.Take(pixelCount).ToArray());
            using var bAlphaBuffer = bottomAlpha.Length == pixelCount ? accelerator.Allocate1D(bottomAlpha) : accelerator.Allocate1D(bottomAlpha.Take(pixelCount).ToArray());
            using var outBuffer = accelerator.Allocate1D<byte>(pixelCount);
            using var outAlphaBuffer = accelerator.Allocate1D<float>(pixelCount);
            var krnl = ApproximateGetKernelByte(accelerator);
            if (ILGPUExecutionHelper.SyncOverride ?? accelerator.AcceleratorType == AcceleratorType.OpenCL)
            {
                using (ILGPUExecutionHelper.locker.EnterScope())
                {
                    krnl(pixelCount, a.View, b.View, aAlphaBuffer.View, bAlphaBuffer.View, outBuffer.View, outAlphaBuffer.View);
                    accelerator.Synchronize();
                }
            }
            else
            {
                krnl(pixelCount, a.View, b.View, aAlphaBuffer.View, bAlphaBuffer.View, outBuffer.View, outAlphaBuffer.View);
            }

            var result = outBuffer.GetAsArray1D();
            var alphaResult = outAlphaBuffer.GetAsArray1D();
            return (result, alphaResult);
        }

        protected override (ushort[] Color, float[] Alpha) ApproximateOverlay16(float[] top, float[] bottom, float[] topAlpha, float[] bottomAlpha, int pixelCount)
        {
            using var scope = nativeLock.EnterScope();
            ObjectDisposedException.ThrowIf(disposed, this);
            using var deviceLease = AcceleratorsManager.AcquireExecution();
            var accelerator = ILGPUExecutionHelper.SelectAccelerator(deviceLease.Accelerators, ref ApproximateaccelIdx);
            using var a = top.Length == pixelCount ? accelerator.Allocate1D(top) : accelerator.Allocate1D(top.Take(pixelCount).ToArray());
            using var b = bottom.Length == pixelCount ? accelerator.Allocate1D(bottom) : accelerator.Allocate1D(bottom.Take(pixelCount).ToArray());
            using var aAlphaBuffer = topAlpha.Length == pixelCount ? accelerator.Allocate1D(topAlpha) : accelerator.Allocate1D(topAlpha.Take(pixelCount).ToArray());
            using var bAlphaBuffer = bottomAlpha.Length == pixelCount ? accelerator.Allocate1D(bottomAlpha) : accelerator.Allocate1D(bottomAlpha.Take(pixelCount).ToArray());
            using var outBuffer = accelerator.Allocate1D<ushort>(pixelCount);
            using var outAlphaBuffer = accelerator.Allocate1D<float>(pixelCount);
            var krnl = ApproximateGetKernelUShort(accelerator);
            if (ILGPUExecutionHelper.SyncOverride ?? accelerator.AcceleratorType == AcceleratorType.OpenCL)
            {
                using (ILGPUExecutionHelper.locker.EnterScope())
                {
                    krnl(pixelCount, a.View, b.View, aAlphaBuffer.View, bAlphaBuffer.View, outBuffer.View, outAlphaBuffer.View);
                    accelerator.Synchronize();
                }
            }
            else
            {
                krnl(pixelCount, a.View, b.View, aAlphaBuffer.View, bAlphaBuffer.View, outBuffer.View, outAlphaBuffer.View);
            }

            var result = outBuffer.GetAsArray1D();
            var alphaResult = outAlphaBuffer.GetAsArray1D();
            return (result, alphaResult);
        }

        protected override (float[] Color, float[] Alpha) ApproximateOverlayHdr(float[] top, float[] bottom, float[] topAlpha, float[] bottomAlpha, int pixelCount)
        {
            using var scope = nativeLock.EnterScope();
            ObjectDisposedException.ThrowIf(disposed, this);
            using var deviceLease = AcceleratorsManager.AcquireExecution();
            var accelerator = ILGPUExecutionHelper.SelectAccelerator(deviceLease.Accelerators, ref ApproximateaccelIdx);
            using var a = top.Length == pixelCount ? accelerator.Allocate1D(top) : accelerator.Allocate1D(top.Take(pixelCount).ToArray());
            using var b = bottom.Length == pixelCount ? accelerator.Allocate1D(bottom) : accelerator.Allocate1D(bottom.Take(pixelCount).ToArray());
            using var aAlphaBuffer = topAlpha.Length == pixelCount ? accelerator.Allocate1D(topAlpha) : accelerator.Allocate1D(topAlpha.Take(pixelCount).ToArray());
            using var bAlphaBuffer = bottomAlpha.Length == pixelCount ? accelerator.Allocate1D(bottomAlpha) : accelerator.Allocate1D(bottomAlpha.Take(pixelCount).ToArray());
            using var outBuffer = accelerator.Allocate1D<float>(pixelCount);
            using var outAlphaBuffer = accelerator.Allocate1D<float>(pixelCount);
            var krnl = ApproximateGetKernelFloat(accelerator);
            if (ILGPUExecutionHelper.SyncOverride ?? accelerator.AcceleratorType == AcceleratorType.OpenCL)
            {
                using (ILGPUExecutionHelper.locker.EnterScope())
                {
                    krnl(pixelCount, a.View, b.View, aAlphaBuffer.View, bAlphaBuffer.View, outBuffer.View, outAlphaBuffer.View);
                    accelerator.Synchronize();
                }
            }
            else
            {
                krnl(pixelCount, a.View, b.View, aAlphaBuffer.View, bAlphaBuffer.View, outBuffer.View, outAlphaBuffer.View);
            }

            var result = outBuffer.GetAsArray1D();
            var alphaResult = outAlphaBuffer.GetAsArray1D();
            return (result, alphaResult);
        }
    }
}
#endif
