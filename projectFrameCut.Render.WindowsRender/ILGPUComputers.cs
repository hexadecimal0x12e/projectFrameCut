using ILGPU;
using ILGPU.Runtime;
using ILGPU.Runtime.OpenCL;
using projectFrameCut.Render.RenderCLI;
using projectFrameCut.Shared;
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

        [SetsRequiredMembers]
        public OverlayComputer(Accelerator[] accel, bool? sync)
        {
            this.accelerators = accel;
            Sync = sync ?? accel.Any(a => a.AcceleratorType == AcceleratorType.OpenCL);
        }

        public required Accelerator[] accelerators { get; init; }
        public bool Sync { get; set; } = false;

        private int accelIdx = 0;

        public float[][] Compute(float[][] args)
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

            var A = args[0];
            var B = args[1];
            var aAlpha = args[2];
            var bAlpha = args[3];
            var output = new float[A.Length];

            using var a = accelerator.Allocate1D(A);
            using var b = accelerator.Allocate1D(B);
            using var aAlphaBuffer = accelerator.Allocate1D(aAlpha ?? Enumerable.Repeat(1f, A.Length).ToArray());
            using var bAlphaBuffer = accelerator.Allocate1D(bAlpha ?? Enumerable.Repeat(1f, A.Length).ToArray());
            var outBuffer = accelerator.Allocate1D<float>(A.Length);
            var outAlphaBuffer = accelerator.Allocate1D<float>(A.Length);
            var krnl = accelerator.LoadAutoGroupedStreamKernel((Index1D i, ArrayView<float> a, ArrayView<float> b, ArrayView<float> aAlpha, ArrayView<float> bAlpha, ArrayView<float> c, ArrayView<float> cAlpha) =>
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

            if (Sync)
            {
                lock (Program.globalLocker!)
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


        [SetsRequiredMembers]
        public RemoveColorComputer(Accelerator[] accel, bool? sync)
        {
            this.accelerators = accel;
            ForceSync = sync ?? accel.Any(a => a.AcceleratorType == AcceleratorType.OpenCL);
        }

        public required Accelerator[] accelerators { get; init; }
        public bool ForceSync { get; set; } = false;

        private int accelIdx = 0;

        public float[][] Compute(float[][] args)
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
            var toRemoveR = (ushort)args[4][0];
            var toRemoveG = (ushort)args[5][0];
            var toRemoveB = (ushort)args[6][0];
            var range = (ushort)args[7][0];

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



            var kernel = accelerator.LoadAutoGroupedStreamKernel<
                Index1D,                             //i 
                ArrayView1D<float, Stride1D.Dense>, //src
                ArrayView1D<ushort, Stride1D.Dense>, //lowR
                ArrayView1D<ushort, Stride1D.Dense>, //highR
                ArrayView1D<byte, Stride1D.Dense>   //c
                >(static
                (i, a, low, high, c) =>
                {
                    if (low[0] <= a[i] && a[i] <= high[0])
                    {
                        c[i] = 1;

                    }
                    else
                    {
                        c[i] = 0;

                    }

                });



            //Log($"[Render #{frameIndex:d6}] Loading data...");



            using MemoryBuffer1D<ushort, Stride1D.Dense> lowRed = accelerator.Allocate1D<ushort>(1);
            using MemoryBuffer1D<ushort, Stride1D.Dense> lowGreen = accelerator.Allocate1D<ushort>(1);
            using MemoryBuffer1D<ushort, Stride1D.Dense> lowBlue = accelerator.Allocate1D<ushort>(1);

            using MemoryBuffer1D<ushort, Stride1D.Dense> highRed = accelerator.Allocate1D<ushort>(1);
            using MemoryBuffer1D<ushort, Stride1D.Dense> highGreen = accelerator.Allocate1D<ushort>(1);
            using MemoryBuffer1D<ushort, Stride1D.Dense> highBlue = accelerator.Allocate1D<ushort>(1);

            using MemoryBuffer1D<byte, Stride1D.Dense> cAlphaR = accelerator.Allocate1D<byte>(size);
            using MemoryBuffer1D<byte, Stride1D.Dense> cAlphaG = accelerator.Allocate1D<byte>(size);
            using MemoryBuffer1D<byte, Stride1D.Dense> cAlphaB = accelerator.Allocate1D<byte>(size);

            //把8bit颜色转换到16bit
            int lowR = toRemoveR - range,
                lowG = toRemoveG - range,
                lowB = toRemoveB - range,
                highR = toRemoveR + range,
                highG = toRemoveG + range,
                highB = toRemoveB + range;

            lowR = lowR < 0 ? 0 : lowR;
            lowG = lowG < 0 ? 0 : lowG;
            lowB = lowB < 0 ? 0 : lowB;
            highR = highR > 65535 ? 65535 : highR;
            highG = highG > 65535 ? 65535 : highG;
            highB = highB > 65535 ? 65535 : highB;

            checked
            {
                lowRed.CopyFromCPU([(ushort)lowR]);
                lowGreen.CopyFromCPU([(ushort)lowG]);
                lowBlue.CopyFromCPU([(ushort)lowB]);

                highRed.CopyFromCPU([(ushort)highR]);
                highGreen.CopyFromCPU([(ushort)highG]);
                highBlue.CopyFromCPU([(ushort)highB]);
            }


            cAlphaR.CopyFromCPU(new byte[size]);
            cAlphaG.CopyFromCPU(new byte[size]);
            cAlphaB.CopyFromCPU(new byte[size]);

            using MemoryBuffer1D<float, Stride1D.Dense> aRed = accelerator.Allocate1D<float>(size);
            aRed.CopyFromCPU(aR);
            LockRun(() => kernel(size,
                            aRed.View,
                            lowRed.View,
                            highRed.View,
                            cAlphaR.View));
            if (ForceSync) accelerator.Synchronize();

            aRed.Dispose();
            lowRed.Dispose();
            highRed.Dispose();

            using MemoryBuffer1D<float, Stride1D.Dense> aGreen = accelerator.Allocate1D<float>(size);
            aGreen.CopyFromCPU(aG);
            LockRun(() => kernel(size,
                        aGreen.View,
                        lowGreen.View,
                        highGreen.View,
                        cAlphaG.View));
            if (ForceSync) accelerator.Synchronize();

            aGreen.Dispose();
            highGreen.Dispose();
            lowGreen.Dispose();

            using MemoryBuffer1D<float, Stride1D.Dense> aBlue = accelerator.Allocate1D<float>(size);
            aBlue.CopyFromCPU(aB);
            LockRun(() => kernel(size,
                        aBlue.View,
                        lowBlue.View,
                        highBlue.View,
                        cAlphaB.View));
            if (ForceSync) accelerator.Synchronize();

            aBlue.Dispose();
            highBlue.Dispose();
            lowBlue.Dispose();


            byte[] AlphaR = new byte[size], AlphaG = new byte[size], AlphaB = new byte[size];
            float[] alpha = new float[size];
            cAlphaR.CopyToCPU(AlphaR);
            cAlphaG.CopyToCPU(AlphaG);
            cAlphaB.CopyToCPU(AlphaB);

            for (int i = 0; i < size; i++)
            {
                alpha[i] = AlphaR[i] == 1 && AlphaG[i] == 1 && AlphaB[i] == 1 ? 0f : sourceA[i];
            }

            return [alpha];
        }

        private void LockRun(Action action)
        {
            if (ForceSync)
            {
                lock (Program.globalLocker)
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

    public static class ILGPUComputerHelper
    {
        //public static void RegisterComputerGetter(Accelerator[] accels)
        //{
        //    AcceleratedComputerBridge.RequireAComputer = new((name) =>
        //    {
        //        switch (name)
        //        {
        //            case "Overlay":
        //                return new OverlayComputer(accels, null);
        //            case "RemoveColor":
        //                return new RemoveColorComputer(accels, null);
        //            default:
        //                Log($"Computer {name} not found.", "Error");
        //                return null;

        //        }
        //    });
        //}

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
