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

        private static readonly System.Collections.Concurrent.ConcurrentDictionary<Accelerator, Action<Index1D, ArrayView<float>, ArrayView<float>, ArrayView<float>, ArrayView<float>, ArrayView<float>, ArrayView<float>>> KernelCache = new();
        private static Action<Index1D, ArrayView<float>, ArrayView<float>, ArrayView<float>, ArrayView<float>, ArrayView<float>, ArrayView<float>> GetKernel(Accelerator accelerator)
        {
            return KernelCache.GetOrAdd(accelerator, static acc =>
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
            aAlpha ??= GetOnes(A.Length);
            bAlpha ??= GetOnes(A.Length);

            using var a = accelerator.Allocate1D(A);
            using var b = accelerator.Allocate1D(B);
            using var aAlphaBuffer = accelerator.Allocate1D(aAlpha);
            using var bAlphaBuffer = accelerator.Allocate1D(bAlpha);
            var outBuffer = accelerator.Allocate1D<float>(A.Length);
            var outAlphaBuffer = accelerator.Allocate1D<float>(A.Length);

            var krnl = GetKernel(accelerator);

            if (Sync)
            {
                using (ILGPUComputerHelper.locker.EnterScope())
                {
                    krnl(A.Length, a.View, b.View, aAlphaBuffer.View, bAlphaBuffer.View, outBuffer.View, outAlphaBuffer.View);
                    accelerator.Synchronize();
                }

            }
            else
            {
                krnl(A.Length, a.View, b.View, aAlphaBuffer.View, bAlphaBuffer.View, outBuffer.View, outAlphaBuffer.View);

            }

            var result = outBuffer.GetAsArray1D();
            outBuffer.Dispose();
            var alphaResult = outAlphaBuffer.GetAsArray1D();
            outAlphaBuffer.Dispose();

            return [result, alphaResult];
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

            var size = aR.Length;

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

            rBuf.CopyFromCPU(aR);
            gBuf.CopyFromCPU(aG);
            bBuf.CopyFromCPU(aB);
            aBuf.CopyFromCPU(sourceA);

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

            // Allocate buffers
            using var rBufIn = accelerator.Allocate1D(rIn);
            using var gBufIn = accelerator.Allocate1D(gIn);
            using var bBufIn = accelerator.Allocate1D(bIn);
            using var aBufIn = accelerator.Allocate1D(aIn);

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

    public static class ILGPUComputerHelper
    {
        public static Lock locker = new();

        public static Device? PickOneAccel(string accelType, int acceleratorId, List<Device> devices)
        {
            Device? pick = null;
            if (acceleratorId >= 0)
                pick = devices[acceleratorId];
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
