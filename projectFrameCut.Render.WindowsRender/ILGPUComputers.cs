using ILGPU;
using ILGPU.Runtime;
using ILGPU.Runtime.OpenCL;
using projectFrameCut.Render.RenderAPIBase.EffectAndMixture;
using System;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace projectFrameCut.Render.WindowsRender
{
    public class OverlayComputer : IComputer
    {
        public string FromPlugin => "projectFrameCut.Render.WindowsRender.WindowsComputers";
        public string SupportedEffectOrMixture => "Overlay";

        [SetsRequiredMembers]
        public OverlayComputer(Accelerator[] accel, bool? sync)
        {
            this.accelerators = accel;
            Sync = sync ?? accel.Any(a => a.AcceleratorType == AcceleratorType.OpenCL);
        }

        public required Accelerator[] accelerators { get; init; }
        public bool Sync { get; set; } = false;

        private static readonly System.Collections.Concurrent.ConcurrentDictionary<int, float[]> OnesCache = new();
        private static float[] GetOnes(int length)
        {
            if (length <= 0) return Array.Empty<float>();
            return OnesCache.GetOrAdd(length, static len =>
            {
                var arr = new float[len];
                Array.Fill(arr, 1f);
                return arr;
            });
        }

        private static readonly System.Collections.Concurrent.ConcurrentDictionary<Accelerator, Action<Index1D, ArrayView<float>, ArrayView<float>, ArrayView<float>, ArrayView<float>, ArrayView<float>, ArrayView<float>>> KernelFloatCache = new();
        private static Action<Index1D, ArrayView<float>, ArrayView<float>, ArrayView<float>, ArrayView<float>, ArrayView<float>, ArrayView<float>> GetKernelFloat(Accelerator accelerator)
        {
            return KernelFloatCache.GetOrAdd(accelerator, static acc =>
            {
                return acc.LoadAutoGroupedStreamKernel((Index1D i,
                    ArrayView<float> a,
                    ArrayView<float> b,
                    ArrayView<float> aAlpha,
                    ArrayView<float> bAlpha,
                    ArrayView<float> c,
                    ArrayView<float> cAlpha) =>
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
                            if (outC < 0f) outC = 0f;
                            if (outC > ushort.MaxValue) outC = ushort.MaxValue;
                            c[i] = (ushort)outC;
                            cAlpha[i] = outA;
                        }
                    }
                });
            });
        }

        private static readonly System.Collections.Concurrent.ConcurrentDictionary<Accelerator, Action<Index1D, ArrayView<float>, ArrayView<float>, ArrayView<float>, ArrayView<float>, ArrayView<ushort>, ArrayView<float>>> KernelUShortCache = new();
        private static Action<Index1D, ArrayView<float>, ArrayView<float>, ArrayView<float>, ArrayView<float>, ArrayView<ushort>, ArrayView<float>> GetKernelUShort(Accelerator accelerator)
        {
            return KernelUShortCache.GetOrAdd(accelerator, static acc =>
            {
                return acc.LoadAutoGroupedStreamKernel((Index1D i,
                    ArrayView<float> a,
                    ArrayView<float> b,
                    ArrayView<float> aAlpha,
                    ArrayView<float> bAlpha,
                    ArrayView<ushort> c,
                    ArrayView<float> cAlpha) =>
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
                            if (outC < 0f) outC = 0f;
                            if (outC > ushort.MaxValue) outC = ushort.MaxValue;
                            c[i] = (ushort)outC;
                            cAlpha[i] = outA;
                        }
                    }
                });
            });
        }

        private static readonly System.Collections.Concurrent.ConcurrentDictionary<Accelerator, Action<Index1D, ArrayView<float>, ArrayView<float>, ArrayView<float>, ArrayView<float>, ArrayView<byte>, ArrayView<float>>> KernelByteCache = new();
        private static Action<Index1D, ArrayView<float>, ArrayView<float>, ArrayView<float>, ArrayView<float>, ArrayView<byte>, ArrayView<float>> GetKernelByte(Accelerator accelerator)
        {
            return KernelByteCache.GetOrAdd(accelerator, static acc =>
            {
                return acc.LoadAutoGroupedStreamKernel((Index1D i,
                    ArrayView<float> a,
                    ArrayView<float> b,
                    ArrayView<float> aAlpha,
                    ArrayView<float> bAlpha,
                    ArrayView<byte> c,
                    ArrayView<float> cAlpha) =>
                {
                    if (aAlpha[i] == 1f)
                    {
                        float v = a[i] / 257.0f;
                        if (v < 0f) v = 0f;
                        if (v > 255f) v = 255f;
                        c[i] = (byte)v;
                        cAlpha[i] = 1f;
                    }
                    else if (aAlpha[i] <= 0.05f)
                    {
                        float v = b[i] / 257.0f;
                        if (v < 0f) v = 0f;
                        if (v > 255f) v = 255f;
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
                            if (outC < 0f) outC = 0f;
                            if (outC > ushort.MaxValue) outC = ushort.MaxValue;
                            float v = outC / 257.0f;
                            if (v < 0f) v = 0f;
                            if (v > 255f) v = 255f;
                            c[i] = (byte)v;
                            cAlpha[i] = outA;
                        }
                    }
                });
            });
        }

        private int accelIdx = 0;

        public object[] Compute(object[] args)
        {
            Accelerator accelerator;
            if (accelerators.Length > 1)
            {
                if (accelIdx >= accelerators.Length) accelIdx = 0;
                accelerator = accelerators[accelIdx++];
            }
            else
            {
                accelerator = accelerators[0];
            }

            var A = args[0] as float[] ?? throw new InvalidDataException("Invalid argument for A");
            var B = args[1] as float[] ?? throw new InvalidDataException("Invalid argument for B");
            var aAlpha = args.Length > 2 ? (args[2] as float[]) : null;
            var bAlpha = args.Length > 3 ? (args[3] as float[]) : null;
            var outputBpp = args.Length > 4 ? Convert.ToInt32(args[4]) : 16;
            var pixelCount = args.Length > 5 ? Convert.ToInt32(args[5]) : A.Length;
            aAlpha ??= GetOnes(pixelCount);
            bAlpha ??= GetOnes(pixelCount);

            using var a = accelerator.Allocate1D(A.Take(pixelCount).ToArray());
            using var b = accelerator.Allocate1D(B.Take(pixelCount).ToArray());
            using var aAlphaBuffer = accelerator.Allocate1D(aAlpha.Take(pixelCount).ToArray());
            using var bAlphaBuffer = accelerator.Allocate1D(bAlpha.Take(pixelCount).ToArray());

            if (outputBpp == 8)
            {
                var outBuffer = accelerator.Allocate1D<byte>(pixelCount);
                var outAlphaBuffer = accelerator.Allocate1D<float>(pixelCount);
                var krnl = GetKernelByte(accelerator);

                if (Sync)
                {
                    using (ILGPUComputerHelper.locker.EnterScope())
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
                outBuffer.Dispose();
                var alphaResult = outAlphaBuffer.GetAsArray1D();
                outAlphaBuffer.Dispose();

                return [result, alphaResult];
            }
            else if (outputBpp == 16)
            {
                var outBuffer = accelerator.Allocate1D<ushort>(pixelCount);
                var outAlphaBuffer = accelerator.Allocate1D<float>(pixelCount);
                var krnl = GetKernelUShort(accelerator);

                if (Sync)
                {
                    using (ILGPUComputerHelper.locker.EnterScope())
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
                outBuffer.Dispose();
                var alphaResult = outAlphaBuffer.GetAsArray1D();
                outAlphaBuffer.Dispose();

                return [result, alphaResult];
            }
            else
            {
                var outBuffer = accelerator.Allocate1D<float>(pixelCount);
                var outAlphaBuffer = accelerator.Allocate1D<float>(pixelCount);
                var krnl = GetKernelFloat(accelerator);

                if (Sync)
                {
                    using (ILGPUComputerHelper.locker.EnterScope())
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
                outBuffer.Dispose();
                var alphaResult = outAlphaBuffer.GetAsArray1D();
                outAlphaBuffer.Dispose();

                return [result, alphaResult];
            }
        }
    }

    public class RemoveColorComputer : IComputer
    {
        public string FromPlugin => "projectFrameCut.Render.WindowsRender.WindowsComputers";
        public string SupportedEffectOrMixture => "RemoveColor";

        [SetsRequiredMembers]
        public RemoveColorComputer(Accelerator[] accel, bool? sync)
        {
            this.accelerators = accel;
            ForceSync = sync ?? accel.Any(a => a.AcceleratorType == AcceleratorType.OpenCL);
        }

        public required Accelerator[] accelerators { get; init; }
        public bool ForceSync { get; set; } = false;

        private int accelIdx = 0;

        private static readonly System.Collections.Concurrent.ConcurrentDictionary<Accelerator, Action<Index1D, ArrayView1D<float, Stride1D.Dense>, ArrayView1D<float, Stride1D.Dense>, ArrayView1D<float, Stride1D.Dense>, ArrayView1D<float, Stride1D.Dense>, float, float, float, float, float, float, ArrayView1D<float, Stride1D.Dense>>> KernelCache = new();
        private static Action<Index1D, ArrayView1D<float, Stride1D.Dense>, ArrayView1D<float, Stride1D.Dense>, ArrayView1D<float, Stride1D.Dense>, ArrayView1D<float, Stride1D.Dense>, float, float, float, float, float, float, ArrayView1D<float, Stride1D.Dense>> GetKernel(Accelerator accelerator)
        {
            return KernelCache.GetOrAdd(accelerator, static acc =>
            {
                return acc.LoadAutoGroupedStreamKernel<
                    Index1D,
                    ArrayView1D<float, Stride1D.Dense>,
                    ArrayView1D<float, Stride1D.Dense>,
                    ArrayView1D<float, Stride1D.Dense>,
                    ArrayView1D<float, Stride1D.Dense>,
                    float, float, float, float, float, float,
                    ArrayView1D<float, Stride1D.Dense>
                >(static (i, r, g, b, sourceA, lowR, highR, lowG, highG, lowB, highB, outA) =>
                {
                    bool inR = lowR <= r[i] && r[i] <= highR;
                    bool inG = lowG <= g[i] && g[i] <= highG;
                    bool inB = lowB <= b[i] && b[i] <= highB;
                    outA[i] = (inR && inG && inB) ? 0f : sourceA[i];
                });
            });
        }

        public object[] Compute(object[] args)
        {
            Accelerator accelerator;
            if (accelerators.Length > 1)
            {
                if (accelIdx >= accelerators.Length) accelIdx = 0;
                accelerator = accelerators[accelIdx++];
            }
            else
            {
                accelerator = accelerators[0];
            }

            var Nullable_aR = args[0];
            var Nullable_aG = args[1];
            var Nullable_aB = args[2];
            var Nullable_sourceA = args[3];
            var toRemoveR = Convert.ToSingle(args[4]);
            var toRemoveG = Convert.ToSingle(args[5]);
            var toRemoveB = Convert.ToSingle(args[6]);
            var range = Convert.ToSingle(args[7]);

            float[] aR, aG, aB, sourceA;

            if (Nullable_aR is float[] arrR && Nullable_aG is float[] arrG && Nullable_aB is float[] arrB && Nullable_sourceA is float[] arrA)
            {
                aR = arrR;
                aG = arrG;
                aB = arrB;
                sourceA = arrA;
            }
            else
            {
                throw new ArgumentNullException("Input color channels cannot be null.");
            }

            var size = args.Length > 8 ? Convert.ToInt32(args[8]) : aR.Length;

            float lowR = toRemoveR - range;
            float lowG = toRemoveG - range;
            float lowB = toRemoveB - range;
            float highR = toRemoveR + range;
            float highG = toRemoveG + range;
            float highB = toRemoveB + range;

            if (lowR < 0) lowR = 0;
            if (lowG < 0) lowG = 0;
            if (lowB < 0) lowB = 0;
            if (highR > 65535) highR = 65535;
            if (highG > 65535) highG = 65535;
            if (highB > 65535) highB = 65535;

            var kernel = GetKernel(accelerator);
            using var rBuf = accelerator.Allocate1D<float>(size);
            using var gBuf = accelerator.Allocate1D<float>(size);
            using var bBuf = accelerator.Allocate1D<float>(size);
            using var aBuf = accelerator.Allocate1D<float>(size);
            using var outABuf = accelerator.Allocate1D<float>(size);

            rBuf.CopyFromCPU(aR.Take(size).ToArray());
            gBuf.CopyFromCPU(aG.Take(size).ToArray());
            bBuf.CopyFromCPU(aB.Take(size).ToArray());
            aBuf.CopyFromCPU(sourceA.Take(size).ToArray());

            LockRun(() => kernel(size, rBuf.View, gBuf.View, bBuf.View, aBuf.View, lowR, highR, lowG, highG, lowB, highB, outABuf.View));
            if (ForceSync) accelerator.Synchronize();

            var alpha = outABuf.GetAsArray1D();
            return [alpha];
        }

        private void LockRun(Action action)
        {
            if (ForceSync)
            {
                using (ILGPUComputerHelper.locker.EnterScope())
                {
                    action();
                }

            }
            else
            {
                action();
            }
        }
    }

    public class ResizeComputer : IComputer
    {
        public string FromPlugin => "projectFrameCut.Render.WindowsRender.WindowsComputers";
        public string SupportedEffectOrMixture => "Resize";

        [SetsRequiredMembers]
        public ResizeComputer(Accelerator[] accel, bool? sync)
        {
            this.accelerators = accel;
            Sync = sync ?? accel.Any(a => a.AcceleratorType == AcceleratorType.OpenCL);
        }

        public required Accelerator[] accelerators { get; init; }
        public bool Sync { get; set; } = false;

        private int accelIdx = 0;

        public object[] Compute(object[] args)
        {
            Accelerator accelerator;
            if (accelerators.Length > 1)
            {
                if (accelIdx >= accelerators.Length) accelIdx = 0;
                accelerator = accelerators[accelIdx++];
            }
            else
            {
                accelerator = accelerators[0];
            }

            var rIn = args[0] as float[] ?? throw new ArgumentException("Invalid argument for R");
            var gIn = args[1] as float[] ?? throw new ArgumentException("Invalid argument for G");
            var bIn = args[2] as float[] ?? throw new ArgumentException("Invalid argument for B");
            var aIn = args[3] as float[] ?? throw new ArgumentException("Invalid argument for A");
            float srcW = (float)args[4];
            float srcH = (float)args[5];
            float dstW = (float)args[6];
            float dstH = (float)args[7];

            int iDstW = (int)dstW;
            int iDstH = (int)dstH;
            int iSrcW = (int)srcW;
            int iSrcH = (int)srcH;

            // Handle 0 size to avoid crash
            if (iDstW <= 0 || iDstH <= 0) return [Array.Empty<float>(), Array.Empty<float>(), Array.Empty<float>(), Array.Empty<float>()];

            int dstLength = iDstW * iDstH;
            int srcLength = iSrcW * iSrcH;

            // Allocate buffers
            using var rBufIn = accelerator.Allocate1D(rIn.Take(srcLength).ToArray());
            using var gBufIn = accelerator.Allocate1D(gIn.Take(srcLength).ToArray());
            using var bBufIn = accelerator.Allocate1D(bIn.Take(srcLength).ToArray());
            using var aBufIn = accelerator.Allocate1D(aIn.Take(srcLength).ToArray());

            using var rBufOut = accelerator.Allocate1D<float>(dstLength);
            using var gBufOut = accelerator.Allocate1D<float>(dstLength);
            using var bBufOut = accelerator.Allocate1D<float>(dstLength);
            using var aBufOut = accelerator.Allocate1D<float>(dstLength);

            var kernel = accelerator.LoadAutoGroupedStreamKernel((
                Index1D i,
                ArrayView<float> rOut, ArrayView<float> gOut, ArrayView<float> bOut, ArrayView<float> aOut,
                ArrayView<float> rIn, ArrayView<float> gIn, ArrayView<float> bIn, ArrayView<float> aIn,
                int dstW, int srcW, int srcH, float ratioX, float ratioY) =>
            {
                int x = i % dstW;
                int y = i / dstW;

                // Nearest neighbor
                int srcX = (int)(x * ratioX);
                int srcY = (int)(y * ratioY);

                if (srcX >= srcW) srcX = srcW - 1;
                if (srcY >= srcH) srcY = srcH - 1;
                if (srcX < 0) srcX = 0;
                if (srcY < 0) srcY = 0;

                int srcIdx = srcY * srcW + srcX;

                rOut[i] = rIn[srcIdx];
                gOut[i] = gIn[srcIdx];
                bOut[i] = bIn[srcIdx];
                aOut[i] = aIn[srcIdx];
            });

            // Ensure we use float division for ratios
            float rX = (float)srcW / dstW;
            float rY = (float)srcH / dstH;

            if (Sync)
            {
                using (ILGPUComputerHelper.locker.EnterScope())
                {
                    kernel(dstLength, rBufOut.View, gBufOut.View, bBufOut.View, aBufOut.View,
                           rBufIn.View, gBufIn.View, bBufIn.View, aBufIn.View,
                           iDstW, iSrcW, iSrcH, rX, rY);
                    accelerator.Synchronize();
                }
            }
            else
            {
                kernel(dstLength, rBufOut.View, gBufOut.View, bBufOut.View, aBufOut.View,
                       rBufIn.View, gBufIn.View, bBufIn.View, aBufIn.View,
                       iDstW, iSrcW, iSrcH, rX, rY);
            }

            var rRes = rBufOut.GetAsArray1D();
            var gRes = gBufOut.GetAsArray1D();
            var bRes = bBufOut.GetAsArray1D();
            var aRes = aBufOut.GetAsArray1D();

            // Buffers disposed by using
            return [rRes, gRes, bRes, aRes];
        }
    }

    public class CropComputer : IComputer
    {
        public string FromPlugin => "projectFrameCut.Render.WindowsRender.WindowsComputers";
        public string SupportedEffectOrMixture => "Crop";

        [SetsRequiredMembers]
        public CropComputer(Accelerator[] accel, bool? sync)
        {
            accelerators = accel;
            Sync = sync ?? accel.Any(a => a.AcceleratorType == AcceleratorType.OpenCL);
        }

        public required Accelerator[] accelerators { get; init; }
        public bool Sync { get; set; } = false;

        private int accelIdx = 0;

        private static readonly System.Collections.Concurrent.ConcurrentDictionary<Accelerator,
            Action<Index1D,
                ArrayView<float>, ArrayView<float>, ArrayView<float>, ArrayView<float>,
                ArrayView<float>, ArrayView<float>, ArrayView<float>, ArrayView<float>,
                int, int, int, int, int>> KernelCache = new();

        private static Action<Index1D,
            ArrayView<float>, ArrayView<float>, ArrayView<float>, ArrayView<float>,
            ArrayView<float>, ArrayView<float>, ArrayView<float>, ArrayView<float>,
            int, int, int, int, int> GetKernel(Accelerator accelerator)
        {
            return KernelCache.GetOrAdd(accelerator, static acc =>
                acc.LoadAutoGroupedStreamKernel((
                    Index1D i,
                    ArrayView<float> rOut,
                    ArrayView<float> gOut,
                    ArrayView<float> bOut,
                    ArrayView<float> aOut,
                    ArrayView<float> rIn,
                    ArrayView<float> gIn,
                    ArrayView<float> bIn,
                    ArrayView<float> aIn,
                    int dstW,
                    int srcW,
                    int srcH,
                    int startX,
                    int startY) =>
                {
                    int x = i % dstW;
                    int y = i / dstW;
                    int srcX = startX + x;
                    int srcY = startY + y;

                    if (srcX >= 0 && srcX < srcW && srcY >= 0 && srcY < srcH)
                    {
                        int srcIdx = srcY * srcW + srcX;
                        rOut[i] = rIn[srcIdx];
                        gOut[i] = gIn[srcIdx];
                        bOut[i] = bIn[srcIdx];
                        aOut[i] = aIn[srcIdx];
                    }
                    else
                    {
                        rOut[i] = 0f;
                        gOut[i] = 0f;
                        bOut[i] = 0f;
                        aOut[i] = 0f;
                    }
                }));
        }

        public object[] Compute(object[] args)
        {
            Accelerator accelerator;
            if (accelerators.Length > 1)
            {
                if (accelIdx >= accelerators.Length) accelIdx = 0;
                accelerator = accelerators[accelIdx++];
            }
            else
            {
                accelerator = accelerators[0];
            }

            var rIn = args[0] as float[] ?? throw new ArgumentException("Invalid argument for R");
            var gIn = args[1] as float[] ?? throw new ArgumentException("Invalid argument for G");
            var bIn = args[2] as float[] ?? throw new ArgumentException("Invalid argument for B");
            var aIn = args[3] as float[] ?? throw new ArgumentException("Invalid argument for A");

            int srcW = Convert.ToInt32(args[4]);
            int srcH = Convert.ToInt32(args[5]);
            int startX = Convert.ToInt32(args[6]);
            int startY = Convert.ToInt32(args[7]);
            int cropW = Convert.ToInt32(args[8]);
            int cropH = Convert.ToInt32(args[9]);

            if (srcW <= 0 || srcH <= 0 || cropW <= 0 || cropH <= 0)
            {
                return [Array.Empty<float>(), Array.Empty<float>(), Array.Empty<float>(), Array.Empty<float>()];
            }

            int srcLength = checked(srcW * srcH);
            int dstLength = checked(cropW * cropH);

            using var rBufIn = accelerator.Allocate1D(rIn.Take(srcLength).ToArray());
            using var gBufIn = accelerator.Allocate1D(gIn.Take(srcLength).ToArray());
            using var bBufIn = accelerator.Allocate1D(bIn.Take(srcLength).ToArray());
            using var aBufIn = accelerator.Allocate1D(aIn.Take(srcLength).ToArray());

            using var rBufOut = accelerator.Allocate1D<float>(dstLength);
            using var gBufOut = accelerator.Allocate1D<float>(dstLength);
            using var bBufOut = accelerator.Allocate1D<float>(dstLength);
            using var aBufOut = accelerator.Allocate1D<float>(dstLength);

            var kernel = GetKernel(accelerator);
            if (Sync)
            {
                using (ILGPUComputerHelper.locker.EnterScope())
                {
                    kernel(dstLength,
                        rBufOut.View, gBufOut.View, bBufOut.View, aBufOut.View,
                        rBufIn.View, gBufIn.View, bBufIn.View, aBufIn.View,
                        cropW, srcW, srcH, startX, startY);
                    accelerator.Synchronize();
                }
            }
            else
            {
                kernel(dstLength,
                    rBufOut.View, gBufOut.View, bBufOut.View, aBufOut.View,
                    rBufIn.View, gBufIn.View, bBufIn.View, aBufIn.View,
                    cropW, srcW, srcH, startX, startY);
            }

            var rRes = rBufOut.GetAsArray1D();
            var gRes = gBufOut.GetAsArray1D();
            var bRes = bBufOut.GetAsArray1D();
            var aRes = aBufOut.GetAsArray1D();
            return [rRes, gRes, bRes, aRes];
        }
    }

    public class PlaceComputer : IComputer
    {
        public string FromPlugin => "projectFrameCut.Render.WindowsRender.WindowsComputers";
        public string SupportedEffectOrMixture => "Place";

        [SetsRequiredMembers]
        public PlaceComputer(Accelerator[] accel, bool? sync)
        {
            accelerators = accel;
            Sync = sync ?? accel.Any(a => a.AcceleratorType == AcceleratorType.OpenCL);
        }

        public required Accelerator[] accelerators { get; init; }
        public bool Sync { get; set; } = false;

        private int accelIdx = 0;

        private static readonly System.Collections.Concurrent.ConcurrentDictionary<Accelerator,
            Action<Index1D,
                ArrayView<float>, ArrayView<float>, ArrayView<float>, ArrayView<float>,
                ArrayView<float>, ArrayView<float>, ArrayView<float>, ArrayView<float>,
                int, int, int, int, int>> KernelCache = new();

        private static Action<Index1D,
            ArrayView<float>, ArrayView<float>, ArrayView<float>, ArrayView<float>,
            ArrayView<float>, ArrayView<float>, ArrayView<float>, ArrayView<float>,
            int, int, int, int, int> GetKernel(Accelerator accelerator)
        {
            return KernelCache.GetOrAdd(accelerator, static acc =>
                acc.LoadAutoGroupedStreamKernel((
                    Index1D i,
                    ArrayView<float> rOut,
                    ArrayView<float> gOut,
                    ArrayView<float> bOut,
                    ArrayView<float> aOut,
                    ArrayView<float> rIn,
                    ArrayView<float> gIn,
                    ArrayView<float> bIn,
                    ArrayView<float> aIn,
                    int dstW,
                    int srcW,
                    int srcH,
                    int startX,
                    int startY) =>
                {
                    int x = i % dstW;
                    int y = i / dstW;
                    int srcX = x - startX;
                    int srcY = y - startY;

                    if (srcX >= 0 && srcX < srcW && srcY >= 0 && srcY < srcH)
                    {
                        int srcIdx = srcY * srcW + srcX;
                        rOut[i] = rIn[srcIdx];
                        gOut[i] = gIn[srcIdx];
                        bOut[i] = bIn[srcIdx];
                        aOut[i] = aIn[srcIdx];
                    }
                    else
                    {
                        rOut[i] = 0f;
                        gOut[i] = 0f;
                        bOut[i] = 0f;
                        aOut[i] = 0f;
                    }
                }));
        }

        public object[] Compute(object[] args)
        {
            Accelerator accelerator;
            if (accelerators.Length > 1)
            {
                if (accelIdx >= accelerators.Length) accelIdx = 0;
                accelerator = accelerators[accelIdx++];
            }
            else
            {
                accelerator = accelerators[0];
            }

            var rIn = args[0] as float[] ?? throw new ArgumentException("Invalid argument for R");
            var gIn = args[1] as float[] ?? throw new ArgumentException("Invalid argument for G");
            var bIn = args[2] as float[] ?? throw new ArgumentException("Invalid argument for B");
            var aIn = args[3] as float[] ?? throw new ArgumentException("Invalid argument for A");

            int srcW = Convert.ToInt32(args[4]);
            int srcH = Convert.ToInt32(args[5]);
            int startX = Convert.ToInt32(args[6]);
            int startY = Convert.ToInt32(args[7]);
            int dstW = Convert.ToInt32(args[8]);
            int dstH = Convert.ToInt32(args[9]);

            if (srcW <= 0 || srcH <= 0 || dstW <= 0 || dstH <= 0)
            {
                return [Array.Empty<float>(), Array.Empty<float>(), Array.Empty<float>(), Array.Empty<float>()];
            }

            int srcLength = checked(srcW * srcH);
            int dstLength = checked(dstW * dstH);

            using var rBufIn = accelerator.Allocate1D(rIn.Take(srcLength).ToArray());
            using var gBufIn = accelerator.Allocate1D(gIn.Take(srcLength).ToArray());
            using var bBufIn = accelerator.Allocate1D(bIn.Take(srcLength).ToArray());
            using var aBufIn = accelerator.Allocate1D(aIn.Take(srcLength).ToArray());

            using var rBufOut = accelerator.Allocate1D<float>(dstLength);
            using var gBufOut = accelerator.Allocate1D<float>(dstLength);
            using var bBufOut = accelerator.Allocate1D<float>(dstLength);
            using var aBufOut = accelerator.Allocate1D<float>(dstLength);

            var kernel = GetKernel(accelerator);
            if (Sync)
            {
                using (ILGPUComputerHelper.locker.EnterScope())
                {
                    kernel(dstLength,
                        rBufOut.View, gBufOut.View, bBufOut.View, aBufOut.View,
                        rBufIn.View, gBufIn.View, bBufIn.View, aBufIn.View,
                        dstW, srcW, srcH, startX, startY);
                    accelerator.Synchronize();
                }
            }
            else
            {
                kernel(dstLength,
                    rBufOut.View, gBufOut.View, bBufOut.View, aBufOut.View,
                    rBufIn.View, gBufIn.View, bBufIn.View, aBufIn.View,
                    dstW, srcW, srcH, startX, startY);
            }

            var rRes = rBufOut.GetAsArray1D();
            var gRes = gBufOut.GetAsArray1D();
            var bRes = bBufOut.GetAsArray1D();
            var aRes = aBufOut.GetAsArray1D();
            return [rRes, gRes, bRes, aRes];
        }
    }

    public static class ILGPUComputerHelper
    {
        public static Lock locker = new();

        public static Device? PickOneAccel(string accelType, int acceleratorId, List<Device> devices)
        {
            Device? pick = null;
            if (acceleratorId >= 0)
            {
                if (acceleratorId > devices.Count)
                {
                    Log($"ERROR: Accelerator {acceleratorId} is not exist.", "error");
                    return null;
                }
                pick = devices[acceleratorId];
            }
            else if (accelType == "cuda")
                pick = devices.FirstOrDefault(d => d.AcceleratorType == AcceleratorType.Cuda);
            else if (accelType == "opencl")
                pick = devices.FirstOrDefault(d => d.AcceleratorType == AcceleratorType.OpenCL
                            && (d.Name.Contains("NVIDIA", StringComparison.OrdinalIgnoreCase) || d.Name.Contains("AMD", StringComparison.OrdinalIgnoreCase))) //优先用独显
                        ?? devices.FirstOrDefault(d => d.AcceleratorType == AcceleratorType.OpenCL);
            else if (accelType == "cpu")
                pick = devices.FirstOrDefault(d => d.AcceleratorType == AcceleratorType.CPU);
            else if (accelType == "auto")
                pick =
                    devices.FirstOrDefault(d => d.AcceleratorType == AcceleratorType.Cuda)
                    ?? devices.FirstOrDefault(d => d.AcceleratorType == AcceleratorType.OpenCL && (d.Name.Contains("NVIDIA", StringComparison.OrdinalIgnoreCase) || d.Name.Contains("AMD", StringComparison.OrdinalIgnoreCase)))
                    ?? devices.FirstOrDefault(d => d.AcceleratorType == AcceleratorType.OpenCL)
                    ?? devices.FirstOrDefault(d => d.AcceleratorType == AcceleratorType.CPU);
            else
            {
                Log($"ERROR: acceleratorType {accelType} is not supported.");
            }
            return pick;
        }


    }
}
