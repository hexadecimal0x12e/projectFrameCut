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

    public class ApproximateOverlayComputer : IComputer
    {
        public string FromPlugin => "projectFrameCut.Render.WindowsRender.WindowsComputers";
        public string SupportedEffectOrMixture => "OverlayApproximate";

        [SetsRequiredMembers]
        public ApproximateOverlayComputer(Accelerator[] accel, bool? sync)
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
                        if (outC > byte.MaxValue) outC = byte.MaxValue;
                        c[i] = (byte)outC;
                        cAlpha[i] = outA;
                    }
                });
            });
        }

        public object[] Compute(object[] args)
        {
            int accelIdx = 0;
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

            // Optional pixel type hint (args[8]): 8=byte, 16=ushort, omit=float (backward compat)
            bool wantByte = args.Length > 8 && args[8] is int pixelType && pixelType == 8;
            bool wantUShort = args.Length > 8 && args[8] is int pixelType2 && pixelType2 == 16;

            int iDstW = (int)dstW;
            int iDstH = (int)dstH;
            int iSrcW = (int)srcW;
            int iSrcH = (int)srcH;

            // Handle 0 size to avoid crash
            if (iDstW <= 0 || iDstH <= 0) return [Array.Empty<float>(), Array.Empty<float>(), Array.Empty<float>(), Array.Empty<float>()];

            int dstLength = iDstW * iDstH;
            int srcLength = iSrcW * iSrcH;

            // Allocate input buffers
            using var rBufIn = accelerator.Allocate1D(rIn.Take(srcLength).ToArray());
            using var gBufIn = accelerator.Allocate1D(gIn.Take(srcLength).ToArray());
            using var bBufIn = accelerator.Allocate1D(bIn.Take(srcLength).ToArray());
            using var aBufIn = accelerator.Allocate1D(aIn.Take(srcLength).ToArray());

            float rX = (float)srcW / dstW;
            float rY = (float)srcH / dstH;

            // --- 8-bit output path ---
            if (wantByte)
            {
                using var rBufOut = accelerator.Allocate1D<byte>(dstLength);
                using var gBufOut = accelerator.Allocate1D<byte>(dstLength);
                using var bBufOut = accelerator.Allocate1D<byte>(dstLength);
                using var aBufOut = accelerator.Allocate1D<float>(dstLength);

                var kernel = accelerator.LoadAutoGroupedStreamKernel((
                    Index1D i,
                    ArrayView<byte> rOut, ArrayView<byte> gOut, ArrayView<byte> bOut, ArrayView<float> aOut,
                    ArrayView<float> rIn, ArrayView<float> gIn, ArrayView<float> bIn, ArrayView<float> aIn,
                    int dstW, int srcW, int srcH, float ratioX, float ratioY) =>
                {
                    int x = i % dstW;
                    int y = i / dstW;
                    int srcX = (int)(x * ratioX);
                    int srcY = (int)(y * ratioY);
                    if (srcX >= srcW) srcX = srcW - 1;
                    if (srcY >= srcH) srcY = srcH - 1;
                    if (srcX < 0) srcX = 0;
                    if (srcY < 0) srcY = 0;
                    int srcIdx = srcY * srcW + srcX;
                    float v = rIn[srcIdx];
                    if (v < 0f) v = 0f; else if (v > 255f) v = 255f;
                    rOut[i] = (byte)v;
                    v = gIn[srcIdx];
                    if (v < 0f) v = 0f; else if (v > 255f) v = 255f;
                    gOut[i] = (byte)v;
                    v = bIn[srcIdx];
                    if (v < 0f) v = 0f; else if (v > 255f) v = 255f;
                    bOut[i] = (byte)v;
                    aOut[i] = aIn[srcIdx];
                });

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

                return [rBufOut.GetAsArray1D(), gBufOut.GetAsArray1D(), bBufOut.GetAsArray1D(), aBufOut.GetAsArray1D()];
            }

            // --- 16-bit output path ---
            if (wantUShort)
            {
                using var rBufOut = accelerator.Allocate1D<ushort>(dstLength);
                using var gBufOut = accelerator.Allocate1D<ushort>(dstLength);
                using var bBufOut = accelerator.Allocate1D<ushort>(dstLength);
                using var aBufOut = accelerator.Allocate1D<float>(dstLength);

                var kernel = accelerator.LoadAutoGroupedStreamKernel((
                    Index1D i,
                    ArrayView<ushort> rOut, ArrayView<ushort> gOut, ArrayView<ushort> bOut, ArrayView<float> aOut,
                    ArrayView<float> rIn, ArrayView<float> gIn, ArrayView<float> bIn, ArrayView<float> aIn,
                    int dstW, int srcW, int srcH, float ratioX, float ratioY) =>
                {
                    int x = i % dstW;
                    int y = i / dstW;
                    int srcX = (int)(x * ratioX);
                    int srcY = (int)(y * ratioY);
                    if (srcX >= srcW) srcX = srcW - 1;
                    if (srcY >= srcH) srcY = srcH - 1;
                    if (srcX < 0) srcX = 0;
                    if (srcY < 0) srcY = 0;
                    int srcIdx = srcY * srcW + srcX;
                    float v = rIn[srcIdx];
                    if (v < 0f) v = 0f; else if (v > 65535f) v = 65535f;
                    rOut[i] = (ushort)v;
                    v = gIn[srcIdx];
                    if (v < 0f) v = 0f; else if (v > 65535f) v = 65535f;
                    gOut[i] = (ushort)v;
                    v = bIn[srcIdx];
                    if (v < 0f) v = 0f; else if (v > 65535f) v = 65535f;
                    bOut[i] = (ushort)v;
                    aOut[i] = aIn[srcIdx];
                });

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

                return [rBufOut.GetAsArray1D(), gBufOut.GetAsArray1D(), bBufOut.GetAsArray1D(), aBufOut.GetAsArray1D()];
            }

            // --- Default float output path (backward compatible) ---
            using var rBufOutF = accelerator.Allocate1D<float>(dstLength);
            using var gBufOutF = accelerator.Allocate1D<float>(dstLength);
            using var bBufOutF = accelerator.Allocate1D<float>(dstLength);
            using var aBufOutF = accelerator.Allocate1D<float>(dstLength);

            var kernelF = accelerator.LoadAutoGroupedStreamKernel((
                Index1D i,
                ArrayView<float> rOut, ArrayView<float> gOut, ArrayView<float> bOut, ArrayView<float> aOut,
                ArrayView<float> rIn, ArrayView<float> gIn, ArrayView<float> bIn, ArrayView<float> aIn,
                int dstW, int srcW, int srcH, float ratioX, float ratioY) =>
            {
                int x = i % dstW;
                int y = i / dstW;
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

            if (Sync)
            {
                using (ILGPUComputerHelper.locker.EnterScope())
                {
                    kernelF(dstLength, rBufOutF.View, gBufOutF.View, bBufOutF.View, aBufOutF.View,
                            rBufIn.View, gBufIn.View, bBufIn.View, aBufIn.View,
                            iDstW, iSrcW, iSrcH, rX, rY);
                    accelerator.Synchronize();
                }
            }
            else
            {
                kernelF(dstLength, rBufOutF.View, gBufOutF.View, bBufOutF.View, aBufOutF.View,
                        rBufIn.View, gBufIn.View, bBufIn.View, aBufIn.View,
                        iDstW, iSrcW, iSrcH, rX, rY);
            }

            return [rBufOutF.GetAsArray1D(), gBufOutF.GetAsArray1D(), bBufOutF.GetAsArray1D(), aBufOutF.GetAsArray1D()];
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

    public class BlendAddComputer : IComputer
    {
        public string FromPlugin => "projectFrameCut.Render.WindowsRender.WindowsComputers";
        public string SupportedEffectOrMixture => "AddComputer";

        [SetsRequiredMembers]
        public BlendAddComputer(Accelerator[] accel, bool? sync)
        {
            accelerators = accel;
            Sync = sync ?? accel.Any(a => a.AcceleratorType == AcceleratorType.OpenCL);
        }

        public required Accelerator[] accelerators { get; init; }
        public bool Sync { get; set; }
        private int accelIdx;

        private static readonly System.Collections.Concurrent.ConcurrentDictionary<Accelerator, Action<Index1D, ArrayView<float>, ArrayView<float>, ArrayView<float>, ArrayView<float>, ArrayView<float>, ArrayView<float>>> KernelCache = new();

        private static Action<Index1D, ArrayView<float>, ArrayView<float>, ArrayView<float>, ArrayView<float>, ArrayView<float>, ArrayView<float>> GetKernel(Accelerator acc)
        {
            return KernelCache.GetOrAdd(acc, static a => a.LoadAutoGroupedStreamKernel((Index1D i,
                ArrayView<float> top, ArrayView<float> bottom, ArrayView<float> topAlpha, ArrayView<float> bottomAlpha,
                ArrayView<float> outC, ArrayView<float> outA) =>
            {
                float aA = topAlpha[i];
                float bA = bottomAlpha[i];
                float outAlpha = aA + bA * (1f - aA);
                if (outAlpha < 1e-6f) { outC[i] = 0f; outA[i] = 0f; }
                else
                {
                    float blended = top[i] + bottom[i];
                    if (blended > 65535f) blended = 65535f;
                    float result = (blended * aA + bottom[i] * bA * (1f - aA)) / outAlpha;
                    if (result < 0f) result = 0f; if (result > 65535f) result = 65535f;
                    outC[i] = result; outA[i] = outAlpha;
                }
            }));
        }

        public object[] Compute(object[] args) => BlendModeILGPUHelper.BlendModeCompute(accelerators, ref accelIdx, Sync, GetKernel, args);
    }

    public class BlendSubtractComputer : IComputer
    {
        public string FromPlugin => "projectFrameCut.Render.WindowsRender.WindowsComputers";
        public string SupportedEffectOrMixture => "SubtractComputer";

        [SetsRequiredMembers]
        public BlendSubtractComputer(Accelerator[] accel, bool? sync)
        {
            accelerators = accel;
            Sync = sync ?? accel.Any(a => a.AcceleratorType == AcceleratorType.OpenCL);
        }

        public required Accelerator[] accelerators { get; init; }
        public bool Sync { get; set; }
        private int accelIdx;

        private static readonly System.Collections.Concurrent.ConcurrentDictionary<Accelerator, Action<Index1D, ArrayView<float>, ArrayView<float>, ArrayView<float>, ArrayView<float>, ArrayView<float>, ArrayView<float>>> KernelCache = new();

        private static Action<Index1D, ArrayView<float>, ArrayView<float>, ArrayView<float>, ArrayView<float>, ArrayView<float>, ArrayView<float>> GetKernel(Accelerator acc)
        {
            return KernelCache.GetOrAdd(acc, static a => a.LoadAutoGroupedStreamKernel((Index1D i,
                ArrayView<float> top, ArrayView<float> bottom, ArrayView<float> topAlpha, ArrayView<float> bottomAlpha,
                ArrayView<float> outC, ArrayView<float> outA) =>
            {
                float aA = topAlpha[i];
                float bA = bottomAlpha[i];
                float outAlpha = aA + bA * (1f - aA);
                if (outAlpha < 1e-6f) { outC[i] = 0f; outA[i] = 0f; }
                else
                {
                    float blended = bottom[i] - top[i];
                    if (blended < 0f) blended = 0f;
                    float result = (blended * aA + bottom[i] * bA * (1f - aA)) / outAlpha;
                    if (result < 0f) result = 0f; if (result > 65535f) result = 65535f;
                    outC[i] = result; outA[i] = outAlpha;
                }
            }));
        }

        public object[] Compute(object[] args) => BlendModeILGPUHelper.BlendModeCompute(accelerators, ref accelIdx, Sync, GetKernel, args);
    }

    public class BlendMultiplyComputer : IComputer
    {
        public string FromPlugin => "projectFrameCut.Render.WindowsRender.WindowsComputers";
        public string SupportedEffectOrMixture => "MultiplyComputer";

        [SetsRequiredMembers]
        public BlendMultiplyComputer(Accelerator[] accel, bool? sync)
        {
            accelerators = accel;
            Sync = sync ?? accel.Any(a => a.AcceleratorType == AcceleratorType.OpenCL);
        }

        public required Accelerator[] accelerators { get; init; }
        public bool Sync { get; set; }
        private int accelIdx;

        private static readonly System.Collections.Concurrent.ConcurrentDictionary<Accelerator, Action<Index1D, ArrayView<float>, ArrayView<float>, ArrayView<float>, ArrayView<float>, ArrayView<float>, ArrayView<float>>> KernelCache = new();

        private static Action<Index1D, ArrayView<float>, ArrayView<float>, ArrayView<float>, ArrayView<float>, ArrayView<float>, ArrayView<float>> GetKernel(Accelerator acc)
        {
            return KernelCache.GetOrAdd(acc, static a => a.LoadAutoGroupedStreamKernel((Index1D i,
                ArrayView<float> top, ArrayView<float> bottom, ArrayView<float> topAlpha, ArrayView<float> bottomAlpha,
                ArrayView<float> outC, ArrayView<float> outA) =>
            {
                float aA = topAlpha[i];
                float bA = bottomAlpha[i];
                float outAlpha = aA + bA * (1f - aA);
                if (outAlpha < 1e-6f) { outC[i] = 0f; outA[i] = 0f; }
                else
                {
                    float blended = top[i] * bottom[i] / 65535f;
                    float result = (blended * aA + bottom[i] * bA * (1f - aA)) / outAlpha;
                    if (result < 0f) result = 0f; if (result > 65535f) result = 65535f;
                    outC[i] = result; outA[i] = outAlpha;
                }
            }));
        }

        public object[] Compute(object[] args) => BlendModeILGPUHelper.BlendModeCompute(accelerators, ref accelIdx, Sync, GetKernel, args);
    }

    public class BlendScreenComputer : IComputer
    {
        public string FromPlugin => "projectFrameCut.Render.WindowsRender.WindowsComputers";
        public string SupportedEffectOrMixture => "ScreenComputer";

        [SetsRequiredMembers]
        public BlendScreenComputer(Accelerator[] accel, bool? sync)
        {
            accelerators = accel;
            Sync = sync ?? accel.Any(a => a.AcceleratorType == AcceleratorType.OpenCL);
        }

        public required Accelerator[] accelerators { get; init; }
        public bool Sync { get; set; }
        private int accelIdx;

        private static readonly System.Collections.Concurrent.ConcurrentDictionary<Accelerator, Action<Index1D, ArrayView<float>, ArrayView<float>, ArrayView<float>, ArrayView<float>, ArrayView<float>, ArrayView<float>>> KernelCache = new();

        private static Action<Index1D, ArrayView<float>, ArrayView<float>, ArrayView<float>, ArrayView<float>, ArrayView<float>, ArrayView<float>> GetKernel(Accelerator acc)
        {
            return KernelCache.GetOrAdd(acc, static a => a.LoadAutoGroupedStreamKernel((Index1D i,
                ArrayView<float> top, ArrayView<float> bottom, ArrayView<float> topAlpha, ArrayView<float> bottomAlpha,
                ArrayView<float> outC, ArrayView<float> outA) =>
            {
                float aA = topAlpha[i];
                float bA = bottomAlpha[i];
                float outAlpha = aA + bA * (1f - aA);
                if (outAlpha < 1e-6f) { outC[i] = 0f; outA[i] = 0f; }
                else
                {
                    float blended = 65535f - (65535f - top[i]) * (65535f - bottom[i]) / 65535f;
                    float result = (blended * aA + bottom[i] * bA * (1f - aA)) / outAlpha;
                    if (result < 0f) result = 0f; if (result > 65535f) result = 65535f;
                    outC[i] = result; outA[i] = outAlpha;
                }
            }));
        }

        public object[] Compute(object[] args) => BlendModeILGPUHelper.BlendModeCompute(accelerators, ref accelIdx, Sync, GetKernel, args);
    }

    public class BlendOverlayBlendComputer : IComputer
    {
        public string FromPlugin => "projectFrameCut.Render.WindowsRender.WindowsComputers";
        public string SupportedEffectOrMixture => "OverlayBlendComputer";

        [SetsRequiredMembers]
        public BlendOverlayBlendComputer(Accelerator[] accel, bool? sync)
        {
            accelerators = accel;
            Sync = sync ?? accel.Any(a => a.AcceleratorType == AcceleratorType.OpenCL);
        }

        public required Accelerator[] accelerators { get; init; }
        public bool Sync { get; set; }
        private int accelIdx;

        private static readonly System.Collections.Concurrent.ConcurrentDictionary<Accelerator, Action<Index1D, ArrayView<float>, ArrayView<float>, ArrayView<float>, ArrayView<float>, ArrayView<float>, ArrayView<float>>> KernelCache = new();

        private static Action<Index1D, ArrayView<float>, ArrayView<float>, ArrayView<float>, ArrayView<float>, ArrayView<float>, ArrayView<float>> GetKernel(Accelerator acc)
        {
            return KernelCache.GetOrAdd(acc, static a => a.LoadAutoGroupedStreamKernel((Index1D i,
                ArrayView<float> top, ArrayView<float> bottom, ArrayView<float> topAlpha, ArrayView<float> bottomAlpha,
                ArrayView<float> outC, ArrayView<float> outA) =>
            {
                float aA = topAlpha[i];
                float bA = bottomAlpha[i];
                float outAlpha = aA + bA * (1f - aA);
                if (outAlpha < 1e-6f) { outC[i] = 0f; outA[i] = 0f; }
                else
                {
                    float blended;
                    if (bottom[i] < 32768f)
                        blended = 2f * top[i] * bottom[i] / 65535f;
                    else
                        blended = 65535f - 2f * (65535f - top[i]) * (65535f - bottom[i]) / 65535f;
                    float result = (blended * aA + bottom[i] * bA * (1f - aA)) / outAlpha;
                    if (result < 0f) result = 0f; if (result > 65535f) result = 65535f;
                    outC[i] = result; outA[i] = outAlpha;
                }
            }));
        }

        public object[] Compute(object[] args) => BlendModeILGPUHelper.BlendModeCompute(accelerators, ref accelIdx, Sync, GetKernel, args);
    }

    public class BlendDarkenComputer : IComputer
    {
        public string FromPlugin => "projectFrameCut.Render.WindowsRender.WindowsComputers";
        public string SupportedEffectOrMixture => "DarkenComputer";

        [SetsRequiredMembers]
        public BlendDarkenComputer(Accelerator[] accel, bool? sync)
        {
            accelerators = accel;
            Sync = sync ?? accel.Any(a => a.AcceleratorType == AcceleratorType.OpenCL);
        }

        public required Accelerator[] accelerators { get; init; }
        public bool Sync { get; set; }
        private int accelIdx;

        private static readonly System.Collections.Concurrent.ConcurrentDictionary<Accelerator, Action<Index1D, ArrayView<float>, ArrayView<float>, ArrayView<float>, ArrayView<float>, ArrayView<float>, ArrayView<float>>> KernelCache = new();

        private static Action<Index1D, ArrayView<float>, ArrayView<float>, ArrayView<float>, ArrayView<float>, ArrayView<float>, ArrayView<float>> GetKernel(Accelerator acc)
        {
            return KernelCache.GetOrAdd(acc, static a => a.LoadAutoGroupedStreamKernel((Index1D i,
                ArrayView<float> top, ArrayView<float> bottom, ArrayView<float> topAlpha, ArrayView<float> bottomAlpha,
                ArrayView<float> outC, ArrayView<float> outA) =>
            {
                float aA = topAlpha[i];
                float bA = bottomAlpha[i];
                float outAlpha = aA + bA * (1f - aA);
                if (outAlpha < 1e-6f) { outC[i] = 0f; outA[i] = 0f; }
                else
                {
                    float blended = top[i] < bottom[i] ? top[i] : bottom[i];
                    float result = (blended * aA + bottom[i] * bA * (1f - aA)) / outAlpha;
                    if (result < 0f) result = 0f; if (result > 65535f) result = 65535f;
                    outC[i] = result; outA[i] = outAlpha;
                }
            }));
        }

        public object[] Compute(object[] args) => BlendModeILGPUHelper.BlendModeCompute(accelerators, ref accelIdx, Sync, GetKernel, args);
    }

    public class BlendLightenComputer : IComputer
    {
        public string FromPlugin => "projectFrameCut.Render.WindowsRender.WindowsComputers";
        public string SupportedEffectOrMixture => "LightenComputer";

        [SetsRequiredMembers]
        public BlendLightenComputer(Accelerator[] accel, bool? sync)
        {
            accelerators = accel;
            Sync = sync ?? accel.Any(a => a.AcceleratorType == AcceleratorType.OpenCL);
        }

        public required Accelerator[] accelerators { get; init; }
        public bool Sync { get; set; }
        private int accelIdx;

        private static readonly System.Collections.Concurrent.ConcurrentDictionary<Accelerator, Action<Index1D, ArrayView<float>, ArrayView<float>, ArrayView<float>, ArrayView<float>, ArrayView<float>, ArrayView<float>>> KernelCache = new();

        private static Action<Index1D, ArrayView<float>, ArrayView<float>, ArrayView<float>, ArrayView<float>, ArrayView<float>, ArrayView<float>> GetKernel(Accelerator acc)
        {
            return KernelCache.GetOrAdd(acc, static a => a.LoadAutoGroupedStreamKernel((Index1D i,
                ArrayView<float> top, ArrayView<float> bottom, ArrayView<float> topAlpha, ArrayView<float> bottomAlpha,
                ArrayView<float> outC, ArrayView<float> outA) =>
            {
                float aA = topAlpha[i];
                float bA = bottomAlpha[i];
                float outAlpha = aA + bA * (1f - aA);
                if (outAlpha < 1e-6f) { outC[i] = 0f; outA[i] = 0f; }
                else
                {
                    float blended = top[i] > bottom[i] ? top[i] : bottom[i];
                    float result = (blended * aA + bottom[i] * bA * (1f - aA)) / outAlpha;
                    if (result < 0f) result = 0f; if (result > 65535f) result = 65535f;
                    outC[i] = result; outA[i] = outAlpha;
                }
            }));
        }

        public object[] Compute(object[] args) => BlendModeILGPUHelper.BlendModeCompute(accelerators, ref accelIdx, Sync, GetKernel, args);
    }

    public class BlendDifferenceComputer : IComputer
    {
        public string FromPlugin => "projectFrameCut.Render.WindowsRender.WindowsComputers";
        public string SupportedEffectOrMixture => "DifferenceComputer";

        [SetsRequiredMembers]
        public BlendDifferenceComputer(Accelerator[] accel, bool? sync)
        {
            accelerators = accel;
            Sync = sync ?? accel.Any(a => a.AcceleratorType == AcceleratorType.OpenCL);
        }

        public required Accelerator[] accelerators { get; init; }
        public bool Sync { get; set; }
        private int accelIdx;

        private static readonly System.Collections.Concurrent.ConcurrentDictionary<Accelerator, Action<Index1D, ArrayView<float>, ArrayView<float>, ArrayView<float>, ArrayView<float>, ArrayView<float>, ArrayView<float>>> KernelCache = new();

        private static Action<Index1D, ArrayView<float>, ArrayView<float>, ArrayView<float>, ArrayView<float>, ArrayView<float>, ArrayView<float>> GetKernel(Accelerator acc)
        {
            return KernelCache.GetOrAdd(acc, static a => a.LoadAutoGroupedStreamKernel((Index1D i,
                ArrayView<float> top, ArrayView<float> bottom, ArrayView<float> topAlpha, ArrayView<float> bottomAlpha,
                ArrayView<float> outC, ArrayView<float> outA) =>
            {
                float aA = topAlpha[i];
                float bA = bottomAlpha[i];
                float outAlpha = aA + bA * (1f - aA);
                if (outAlpha < 1e-6f) { outC[i] = 0f; outA[i] = 0f; }
                else
                {
                    float diff = top[i] - bottom[i];
                    float blended = diff < 0f ? -diff : diff;
                    float result = (blended * aA + bottom[i] * bA * (1f - aA)) / outAlpha;
                    if (result < 0f) result = 0f; if (result > 65535f) result = 65535f;
                    outC[i] = result; outA[i] = outAlpha;
                }
            }));
        }

        public object[] Compute(object[] args) => BlendModeILGPUHelper.BlendModeCompute(accelerators, ref accelIdx, Sync, GetKernel, args);
    }

    public class OpacityComputer : IComputer, ISessionComputer
    {
        public string FromPlugin => "projectFrameCut.Render.WindowsRender.WindowsComputers";
        public string SupportedEffectOrMixture => "FadeOpacity";

        [SetsRequiredMembers]
        public OpacityComputer(Accelerator[] accel, bool? sync)
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
                float>> KernelCache = new();

        private static Action<Index1D,
            ArrayView<float>, ArrayView<float>, ArrayView<float>, ArrayView<float>,
            ArrayView<float>, ArrayView<float>, ArrayView<float>, ArrayView<float>,
            float> GetKernel(Accelerator accelerator)
        {
            return KernelCache.GetOrAdd(accelerator, static acc =>
                acc.LoadAutoGroupedStreamKernel((
                    Index1D i,
                    ArrayView<float> rOut, ArrayView<float> gOut, ArrayView<float> bOut, ArrayView<float> aOut,
                    ArrayView<float> rIn, ArrayView<float> gIn, ArrayView<float> bIn, ArrayView<float> aIn,
                    float opacity) =>
                {
                    rOut[i] = rIn[i];
                    gOut[i] = gIn[i];
                    bOut[i] = bIn[i];
                    aOut[i] = aIn[i] * opacity;
                }));
        }

        public object[] Compute(object[] args)
        {
            Accelerator accelerator;
            if (accelerators.Length > 1) { if (accelIdx >= accelerators.Length) accelIdx = 0; accelerator = accelerators[accelIdx++]; }
            else { accelerator = accelerators[0]; }

            var rIn = args[0] as float[] ?? throw new ArgumentException("Invalid argument for R");
            var gIn = args[1] as float[] ?? throw new ArgumentException("Invalid argument for G");
            var bIn = args[2] as float[] ?? throw new ArgumentException("Invalid argument for B");
            var aIn = args[3] as float[] ?? throw new ArgumentException("Invalid argument for A");
            float opacity = Convert.ToSingle(args[4]);

            int length = rIn.Length;

            using var rBufIn = accelerator.Allocate1D(rIn);
            using var gBufIn = accelerator.Allocate1D(gIn);
            using var bBufIn = accelerator.Allocate1D(bIn);
            using var aBufIn = accelerator.Allocate1D(aIn);
            using var rBufOut = accelerator.Allocate1D<float>(length);
            using var gBufOut = accelerator.Allocate1D<float>(length);
            using var bBufOut = accelerator.Allocate1D<float>(length);
            using var aBufOut = accelerator.Allocate1D<float>(length);

            var kernel = GetKernel(accelerator);
            if (Sync)
            {
                using (ILGPUComputerHelper.locker.EnterScope()) { kernel(length, rBufOut.View, gBufOut.View, bBufOut.View, aBufOut.View, rBufIn.View, gBufIn.View, bBufIn.View, aBufIn.View, opacity); accelerator.Synchronize(); }
            }
            else { kernel(length, rBufOut.View, gBufOut.View, bBufOut.View, aBufOut.View, rBufIn.View, gBufIn.View, bBufIn.View, aBufIn.View, opacity); }

            return [rBufOut.GetAsArray1D(), gBufOut.GetAsArray1D(), bBufOut.GetAsArray1D(), aBufOut.GetAsArray1D()];
        }

        bool ISessionComputer.SupportsBatching => !Sync;

        IGpuEffectSession ISessionComputer.CreateSession(float[] r, float[] g, float[] b, float[] a, int width, int height)
        {
            var accel = accelerators.Length > 1 ? accelerators[(accelIdx++) % accelerators.Length] : accelerators[0];
            return new ILGPUGpuEffectSession(accel, r, g, b, a, width, height);
        }

        void ISessionComputer.ExecuteOnSession(IGpuEffectSession session, IReadOnlyDictionary<string, object> parameters)
        {
            var sess = (ILGPUGpuEffectSession)session;
            var kernel = GetKernel(sess.Accelerator);
            int length = (int)sess.CurBufR.Length;
            float opacity = Convert.ToSingle(parameters["Opacity"]);
            kernel(length,
                sess.AltBufR.View, sess.AltBufG.View, sess.AltBufB.View, sess.AltBufA.View,
                sess.CurBufR.View, sess.CurBufG.View, sess.CurBufB.View, sess.CurBufA.View,
                opacity);
            sess.SwapBuffers();
        }
    }

    public class VignetteComputer : IComputer, ISessionComputer
    {
        public string FromPlugin => "projectFrameCut.Render.WindowsRender.WindowsComputers";
        public string SupportedEffectOrMixture => "Vignette";

        [SetsRequiredMembers]
        public VignetteComputer(Accelerator[] accel, bool? sync)
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
                int, int, float, float>> KernelCache = new();

        private static Action<Index1D,
            ArrayView<float>, ArrayView<float>, ArrayView<float>, ArrayView<float>,
            ArrayView<float>, ArrayView<float>, ArrayView<float>, ArrayView<float>,
            int, int, float, float> GetKernel(Accelerator accelerator)
        {
            return KernelCache.GetOrAdd(accelerator, static acc =>
                acc.LoadAutoGroupedStreamKernel((
                    Index1D i,
                    ArrayView<float> rOut, ArrayView<float> gOut, ArrayView<float> bOut, ArrayView<float> aOut,
                    ArrayView<float> rIn, ArrayView<float> gIn, ArrayView<float> bIn, ArrayView<float> aIn,
                    int w, int h, float strength, float radius) =>
                {
                    int x = i % w;
                    int y = i / w;
                    float cx = w * 0.5f;
                    float cy = h * 0.5f;
                    float dx = (x - cx) / cx;
                    float dy = (y - cy) / cy;
                    float dist = MathF.Sqrt(dx * dx + dy * dy);
                    float factor = 1f;
                    if (dist > radius)
                    {
                        float t = MathF.Min((dist - radius) / (1f - radius), 1f);
                        factor = 1f - t * t * strength;
                    }
                    rOut[i] = rIn[i] * factor;
                    gOut[i] = gIn[i] * factor;
                    bOut[i] = bIn[i] * factor;
                    aOut[i] = aIn[i];
                }));
        }

        public object[] Compute(object[] args)
        {
            Accelerator accelerator;
            if (accelerators.Length > 1) { if (accelIdx >= accelerators.Length) accelIdx = 0; accelerator = accelerators[accelIdx++]; }
            else { accelerator = accelerators[0]; }

            var rIn = args[0] as float[] ?? throw new ArgumentException("Invalid argument for R");
            var gIn = args[1] as float[] ?? throw new ArgumentException("Invalid argument for G");
            var bIn = args[2] as float[] ?? throw new ArgumentException("Invalid argument for B");
            var aIn = args[3] as float[] ?? throw new ArgumentException("Invalid argument for A");
            int w = Convert.ToInt32(args[4]);
            int h = Convert.ToInt32(args[5]);
            float strength = Convert.ToSingle(args[6]);
            float radius = Convert.ToSingle(args[7]);

            int length = rIn.Length;

            using var rBufIn = accelerator.Allocate1D(rIn);
            using var gBufIn = accelerator.Allocate1D(gIn);
            using var bBufIn = accelerator.Allocate1D(bIn);
            using var aBufIn = accelerator.Allocate1D(aIn);
            using var rBufOut = accelerator.Allocate1D<float>(length);
            using var gBufOut = accelerator.Allocate1D<float>(length);
            using var bBufOut = accelerator.Allocate1D<float>(length);
            using var aBufOut = accelerator.Allocate1D<float>(length);

            var kernel = GetKernel(accelerator);
            if (Sync)
            {
                using (ILGPUComputerHelper.locker.EnterScope()) { kernel(length, rBufOut.View, gBufOut.View, bBufOut.View, aBufOut.View, rBufIn.View, gBufIn.View, bBufIn.View, aBufIn.View, w, h, strength, radius); accelerator.Synchronize(); }
            }
            else { kernel(length, rBufOut.View, gBufOut.View, bBufOut.View, aBufOut.View, rBufIn.View, gBufIn.View, bBufIn.View, aBufIn.View, w, h, strength, radius); }

            return [rBufOut.GetAsArray1D(), gBufOut.GetAsArray1D(), bBufOut.GetAsArray1D(), aBufOut.GetAsArray1D()];
        }

        bool ISessionComputer.SupportsBatching => !Sync;

        IGpuEffectSession ISessionComputer.CreateSession(float[] r, float[] g, float[] b, float[] a, int width, int height)
        {
            var accel = accelerators.Length > 1 ? accelerators[(accelIdx++) % accelerators.Length] : accelerators[0];
            return new ILGPUGpuEffectSession(accel, r, g, b, a, width, height);
        }

        void ISessionComputer.ExecuteOnSession(IGpuEffectSession session, IReadOnlyDictionary<string, object> parameters)
        {
            var sess = (ILGPUGpuEffectSession)session;
            var kernel = GetKernel(sess.Accelerator);
            int length = (int)sess.CurBufR.Length;
            int w = parameters.TryGetValue("BuiltIn.TargetWidth", out var wObj) ? Convert.ToInt32(wObj) : sess.Width;
            int h = parameters.TryGetValue("BuiltIn.TargetHeight", out var hObj) ? Convert.ToInt32(hObj) : sess.Height;
            float strength = Convert.ToSingle(parameters["Strength"]);
            float radius = Convert.ToSingle(parameters["Radius"]);
            kernel(length,
                sess.AltBufR.View, sess.AltBufG.View, sess.AltBufB.View, sess.AltBufA.View,
                sess.CurBufR.View, sess.CurBufG.View, sess.CurBufB.View, sess.CurBufA.View,
                w, h, strength, radius);
            sess.SwapBuffers();
        }
    }

    public class FlipComputer : IComputer, ISessionComputer
    {
        public string FromPlugin => "projectFrameCut.Render.WindowsRender.WindowsComputers";
        public string SupportedEffectOrMixture => "Flip";

        [SetsRequiredMembers]
        public FlipComputer(Accelerator[] accel, bool? sync)
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
                int, int, int, int>> KernelCache = new();

        private static Action<Index1D,
            ArrayView<float>, ArrayView<float>, ArrayView<float>, ArrayView<float>,
            ArrayView<float>, ArrayView<float>, ArrayView<float>, ArrayView<float>,
            int, int, int, int> GetKernel(Accelerator accelerator)
        {
            return KernelCache.GetOrAdd(accelerator, static acc =>
                acc.LoadAutoGroupedStreamKernel((
                    Index1D i,
                    ArrayView<float> rOut, ArrayView<float> gOut, ArrayView<float> bOut, ArrayView<float> aOut,
                    ArrayView<float> rIn, ArrayView<float> gIn, ArrayView<float> bIn, ArrayView<float> aIn,
                    int w, int h, int horizontal, int vertical) =>
                {
                    int x = i % w;
                    int y = i / w;
                    int srcX = horizontal != 0 ? w - 1 - x : x;
                    int srcY = vertical != 0 ? h - 1 - y : y;
                    int srcIdx = srcY * w + srcX;
                    rOut[i] = rIn[srcIdx];
                    gOut[i] = gIn[srcIdx];
                    bOut[i] = bIn[srcIdx];
                    aOut[i] = aIn[srcIdx];
                }));
        }

        public object[] Compute(object[] args)
        {
            Accelerator accelerator;
            if (accelerators.Length > 1) { if (accelIdx >= accelerators.Length) accelIdx = 0; accelerator = accelerators[accelIdx++]; }
            else { accelerator = accelerators[0]; }

            var rIn = args[0] as float[] ?? throw new ArgumentException("Invalid argument for R");
            var gIn = args[1] as float[] ?? throw new ArgumentException("Invalid argument for G");
            var bIn = args[2] as float[] ?? throw new ArgumentException("Invalid argument for B");
            var aIn = args[3] as float[] ?? throw new ArgumentException("Invalid argument for A");
            int w = Convert.ToInt32(args[4]);
            int h = Convert.ToInt32(args[5]);
            int horizontal = Convert.ToBoolean(args[6]) ? 1 : 0;
            int vertical = Convert.ToBoolean(args[7]) ? 1 : 0;

            int length = rIn.Length;

            using var rBufIn = accelerator.Allocate1D(rIn);
            using var gBufIn = accelerator.Allocate1D(gIn);
            using var bBufIn = accelerator.Allocate1D(bIn);
            using var aBufIn = accelerator.Allocate1D(aIn);
            using var rBufOut = accelerator.Allocate1D<float>(length);
            using var gBufOut = accelerator.Allocate1D<float>(length);
            using var bBufOut = accelerator.Allocate1D<float>(length);
            using var aBufOut = accelerator.Allocate1D<float>(length);

            var kernel = GetKernel(accelerator);
            if (Sync)
            {
                using (ILGPUComputerHelper.locker.EnterScope()) { kernel(length, rBufOut.View, gBufOut.View, bBufOut.View, aBufOut.View, rBufIn.View, gBufIn.View, bBufIn.View, aBufIn.View, w, h, horizontal, vertical); accelerator.Synchronize(); }
            }
            else { kernel(length, rBufOut.View, gBufOut.View, bBufOut.View, aBufOut.View, rBufIn.View, gBufIn.View, bBufIn.View, aBufIn.View, w, h, horizontal, vertical); }

            return [rBufOut.GetAsArray1D(), gBufOut.GetAsArray1D(), bBufOut.GetAsArray1D(), aBufOut.GetAsArray1D()];
        }

        bool ISessionComputer.SupportsBatching => !Sync;

        IGpuEffectSession ISessionComputer.CreateSession(float[] r, float[] g, float[] b, float[] a, int width, int height)
        {
            var accel = accelerators.Length > 1 ? accelerators[(accelIdx++) % accelerators.Length] : accelerators[0];
            return new ILGPUGpuEffectSession(accel, r, g, b, a, width, height);
        }

        void ISessionComputer.ExecuteOnSession(IGpuEffectSession session, IReadOnlyDictionary<string, object> parameters)
        {
            var sess = (ILGPUGpuEffectSession)session;
            var kernel = GetKernel(sess.Accelerator);
            int length = (int)sess.CurBufR.Length;
            int w = parameters.TryGetValue("BuiltIn.TargetWidth", out var wObj) ? Convert.ToInt32(wObj) : sess.Width;
            int h = parameters.TryGetValue("BuiltIn.TargetHeight", out var hObj) ? Convert.ToInt32(hObj) : sess.Height;
            int horizontal = Convert.ToBoolean(parameters["Horizontal"]) ? 1 : 0;
            int vertical = Convert.ToBoolean(parameters["Vertical"]) ? 1 : 0;
            kernel(length,
                sess.AltBufR.View, sess.AltBufG.View, sess.AltBufB.View, sess.AltBufA.View,
                sess.CurBufR.View, sess.CurBufG.View, sess.CurBufB.View, sess.CurBufA.View,
                w, h, horizontal, vertical);
            sess.SwapBuffers();
        }
    }

    public class SharpenComputer : IComputer, ISessionComputer
    {
        public string FromPlugin => "projectFrameCut.Render.WindowsRender.WindowsComputers";
        public string SupportedEffectOrMixture => "Sharpen";

        [SetsRequiredMembers]
        public SharpenComputer(Accelerator[] accel, bool? sync)
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
                int, float>> KernelCache = new();

        private static Action<Index1D,
            ArrayView<float>, ArrayView<float>, ArrayView<float>, ArrayView<float>,
            ArrayView<float>, ArrayView<float>, ArrayView<float>, ArrayView<float>,
            int, float> GetKernel(Accelerator accelerator)
        {
            return KernelCache.GetOrAdd(accelerator, static acc =>
                acc.LoadAutoGroupedStreamKernel((
                    Index1D i,
                    ArrayView<float> rOut, ArrayView<float> gOut, ArrayView<float> bOut, ArrayView<float> aOut,
                    ArrayView<float> rIn, ArrayView<float> gIn, ArrayView<float> bIn, ArrayView<float> aIn,
                    int w, float amount) =>
                {
                    int x = i % w;
                    int y = i / w;
                    int h = (int)(rIn.Length / w);

                    float avgR, avgG, avgB, origR, origG, origB;
                    origR = rIn[i]; origG = gIn[i]; origB = bIn[i];

                    int left = x > 0 ? i - 1 : i;
                    int right = x < w - 1 ? i + 1 : i;
                    int top = y > 0 ? i - w : i;
                    int bottom = y < h - 1 ? i + w : i;

                    avgR = (rIn[left] + rIn[right] + rIn[top] + rIn[bottom]) * 0.25f;
                    avgG = (gIn[left] + gIn[right] + gIn[top] + gIn[bottom]) * 0.25f;
                    avgB = (bIn[left] + bIn[right] + bIn[top] + bIn[bottom]) * 0.25f;

                    rOut[i] = origR + amount * (origR - avgR);
                    gOut[i] = origG + amount * (origG - avgG);
                    bOut[i] = origB + amount * (origB - avgB);
                    aOut[i] = aIn[i];
                }));
        }

        public object[] Compute(object[] args)
        {
            Accelerator accelerator;
            if (accelerators.Length > 1) { if (accelIdx >= accelerators.Length) accelIdx = 0; accelerator = accelerators[accelIdx++]; }
            else { accelerator = accelerators[0]; }

            var rIn = args[0] as float[] ?? throw new ArgumentException("Invalid argument for R");
            var gIn = args[1] as float[] ?? throw new ArgumentException("Invalid argument for G");
            var bIn = args[2] as float[] ?? throw new ArgumentException("Invalid argument for B");
            var aIn = args[3] as float[] ?? throw new ArgumentException("Invalid argument for A");
            int w = Convert.ToInt32(args[4]);
            float amount = Convert.ToSingle(args[5]);

            int length = rIn.Length;

            using var rBufIn = accelerator.Allocate1D(rIn);
            using var gBufIn = accelerator.Allocate1D(gIn);
            using var bBufIn = accelerator.Allocate1D(bIn);
            using var aBufIn = accelerator.Allocate1D(aIn);
            using var rBufOut = accelerator.Allocate1D<float>(length);
            using var gBufOut = accelerator.Allocate1D<float>(length);
            using var bBufOut = accelerator.Allocate1D<float>(length);
            using var aBufOut = accelerator.Allocate1D<float>(length);

            var kernel = GetKernel(accelerator);
            if (Sync)
            {
                using (ILGPUComputerHelper.locker.EnterScope()) { kernel(length, rBufOut.View, gBufOut.View, bBufOut.View, aBufOut.View, rBufIn.View, gBufIn.View, bBufIn.View, aBufIn.View, w, amount); accelerator.Synchronize(); }
            }
            else { kernel(length, rBufOut.View, gBufOut.View, bBufOut.View, aBufOut.View, rBufIn.View, gBufIn.View, bBufIn.View, aBufIn.View, w, amount); }

            return [rBufOut.GetAsArray1D(), gBufOut.GetAsArray1D(), bBufOut.GetAsArray1D(), aBufOut.GetAsArray1D()];
        }

        bool ISessionComputer.SupportsBatching => !Sync;

        IGpuEffectSession ISessionComputer.CreateSession(float[] r, float[] g, float[] b, float[] a, int width, int height)
        {
            var accel = accelerators.Length > 1 ? accelerators[(accelIdx++) % accelerators.Length] : accelerators[0];
            return new ILGPUGpuEffectSession(accel, r, g, b, a, width, height);
        }

        void ISessionComputer.ExecuteOnSession(IGpuEffectSession session, IReadOnlyDictionary<string, object> parameters)
        {
            var sess = (ILGPUGpuEffectSession)session;
            var kernel = GetKernel(sess.Accelerator);
            int length = (int)sess.CurBufR.Length;
            int w = parameters.TryGetValue("BuiltIn.TargetWidth", out var wObj) ? Convert.ToInt32(wObj) : sess.Width;
            float amount = Convert.ToSingle(parameters["Amount"]);
            kernel(length,
                sess.AltBufR.View, sess.AltBufG.View, sess.AltBufB.View, sess.AltBufA.View,
                sess.CurBufR.View, sess.CurBufG.View, sess.CurBufB.View, sess.CurBufA.View,
                w, amount);
            sess.SwapBuffers();
        }
    }

    public class RotationComputer : IComputer, ISessionComputer
    {
        public string FromPlugin => "projectFrameCut.Render.WindowsRender.WindowsComputers";
        public string SupportedEffectOrMixture => "Rotation";

        [SetsRequiredMembers]
        public RotationComputer(Accelerator[] accel, bool? sync)
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
                int, int, int, int, float>> KernelCache = new();

        private static Action<Index1D,
            ArrayView<float>, ArrayView<float>, ArrayView<float>, ArrayView<float>,
            ArrayView<float>, ArrayView<float>, ArrayView<float>, ArrayView<float>,
            int, int, int, int, float> GetKernel(Accelerator accelerator)
        {
            return KernelCache.GetOrAdd(accelerator, static acc =>
                acc.LoadAutoGroupedStreamKernel((
                    Index1D i,
                    ArrayView<float> rOut, ArrayView<float> gOut, ArrayView<float> bOut, ArrayView<float> aOut,
                    ArrayView<float> rIn, ArrayView<float> gIn, ArrayView<float> bIn, ArrayView<float> aIn,
                    int srcW, int srcH, int outW, int outH, float angleDeg) =>
                {
                    int x = i % outW;
                    int y = i / outW;
                    float angleRad = angleDeg * MathF.PI / 180f;
                    float cosA = MathF.Cos(-angleRad);
                    float sinA = MathF.Sin(-angleRad);

                    float srcCx = srcW * 0.5f;
                    float srcCy = srcH * 0.5f;
                    float outCx = outW * 0.5f;
                    float outCy = outH * 0.5f;

                    float ox = x - outCx;
                    float oy = y - outCy;
                    float srcXf = cosA * ox - sinA * oy + srcCx;
                    float srcYf = sinA * ox + cosA * oy + srcCy;

                    if (srcXf >= 0f && srcXf < srcW - 1 && srcYf >= 0f && srcYf < srcH - 1)
                    {
                        int sx0 = (int)srcXf;
                        int sy0 = (int)srcYf;
                        int sx1 = sx0 + 1;
                        int sy1 = sy0 + 1;
                        float fx = srcXf - sx0;
                        float fy = srcYf - sy0;

                        int i00 = sy0 * srcW + sx0;
                        int i10 = sy0 * srcW + sx1;
                        int i01 = sy1 * srcW + sx0;
                        int i11 = sy1 * srcW + sx1;

                        rOut[i] = (rIn[i00] * (1f - fx) + rIn[i10] * fx) * (1f - fy) + (rIn[i01] * (1f - fx) + rIn[i11] * fx) * fy;
                        gOut[i] = (gIn[i00] * (1f - fx) + gIn[i10] * fx) * (1f - fy) + (gIn[i01] * (1f - fx) + gIn[i11] * fx) * fy;
                        bOut[i] = (bIn[i00] * (1f - fx) + bIn[i10] * fx) * (1f - fy) + (bIn[i01] * (1f - fx) + bIn[i11] * fx) * fy;
                        aOut[i] = (aIn[i00] * (1f - fx) + aIn[i10] * fx) * (1f - fy) + (aIn[i01] * (1f - fx) + aIn[i11] * fx) * fy;
                    }
                    else
                    {
                        rOut[i] = 0f; gOut[i] = 0f; bOut[i] = 0f; aOut[i] = 0f;
                    }
                }));
        }

        public object[] Compute(object[] args)
        {
            Accelerator accelerator;
            if (accelerators.Length > 1) { if (accelIdx >= accelerators.Length) accelIdx = 0; accelerator = accelerators[accelIdx++]; }
            else { accelerator = accelerators[0]; }

            var rIn = args[0] as float[] ?? throw new ArgumentException("Invalid argument for R");
            var gIn = args[1] as float[] ?? throw new ArgumentException("Invalid argument for G");
            var bIn = args[2] as float[] ?? throw new ArgumentException("Invalid argument for B");
            var aIn = args[3] as float[] ?? throw new ArgumentException("Invalid argument for A");
            int srcW = Convert.ToInt32(args[4]);
            int srcH = Convert.ToInt32(args[5]);
            int outW = Convert.ToInt32(args[6]);
            int outH = Convert.ToInt32(args[7]);
            float angleDeg = Convert.ToSingle(args[8]);

            int srcLength = checked(srcW * srcH);
            int dstLength = checked(outW * outH);

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
                using (ILGPUComputerHelper.locker.EnterScope()) { kernel(dstLength, rBufOut.View, gBufOut.View, bBufOut.View, aBufOut.View, rBufIn.View, gBufIn.View, bBufIn.View, aBufIn.View, srcW, srcH, outW, outH, angleDeg); accelerator.Synchronize(); }
            }
            else { kernel(dstLength, rBufOut.View, gBufOut.View, bBufOut.View, aBufOut.View, rBufIn.View, gBufIn.View, bBufIn.View, aBufIn.View, srcW, srcH, outW, outH, angleDeg); }

            return [rBufOut.GetAsArray1D(), gBufOut.GetAsArray1D(), bBufOut.GetAsArray1D(), aBufOut.GetAsArray1D()];
        }

        bool ISessionComputer.SupportsBatching => !Sync;

        IGpuEffectSession ISessionComputer.CreateSession(float[] r, float[] g, float[] b, float[] a, int width, int height)
        {
            var accel = accelerators.Length > 1 ? accelerators[(accelIdx++) % accelerators.Length] : accelerators[0];
            return new ILGPUGpuEffectSession(accel, r, g, b, a, width, height);
        }

        void ISessionComputer.ExecuteOnSession(IGpuEffectSession session, IReadOnlyDictionary<string, object> parameters)
        {
            var sess = (ILGPUGpuEffectSession)session;
            var kernel = GetKernel(sess.Accelerator);
            int length = (int)sess.CurBufR.Length;
            int outW = parameters.TryGetValue("BuiltIn.TargetWidth", out var wObj) ? Convert.ToInt32(wObj) : sess.Width;
            int outH = parameters.TryGetValue("BuiltIn.TargetHeight", out var hObj) ? Convert.ToInt32(hObj) : sess.Height;
            float angleDeg = Convert.ToSingle(parameters["Angle"]);
            // In session mode, src and dst have the same dimensions
            kernel(length,
                sess.AltBufR.View, sess.AltBufG.View, sess.AltBufB.View, sess.AltBufA.View,
                sess.CurBufR.View, sess.CurBufG.View, sess.CurBufB.View, sess.CurBufA.View,
                outW, outH, outW, outH, angleDeg);
            sess.SwapBuffers();
        }
    }

    public class BlurComputer : IComputer, ISessionComputer
    {
        public string FromPlugin => "projectFrameCut.Render.WindowsRender.WindowsComputers";
        public string SupportedEffectOrMixture => "Blur";

        [SetsRequiredMembers]
        public BlurComputer(Accelerator[] accel, bool? sync)
        {
            accelerators = accel;
            Sync = sync ?? accel.Any(a => a.AcceleratorType == AcceleratorType.OpenCL);
        }

        public required Accelerator[] accelerators { get; init; }
        public bool Sync { get; set; } = false;
        private int accelIdx = 0;

        private static readonly System.Collections.Concurrent.ConcurrentDictionary<Accelerator,
            Action<Index1D,
                ArrayView<float>, ArrayView<float>,
                ArrayView<float>, ArrayView<float>,
                int, int>> KernelHorizontalCache = new();

        private static Action<Index1D,
            ArrayView<float>, ArrayView<float>,
            ArrayView<float>, ArrayView<float>,
            int, int> GetKernelHorizontal(Accelerator accelerator)
        {
            return KernelHorizontalCache.GetOrAdd(accelerator, static acc =>
                acc.LoadAutoGroupedStreamKernel((
                    Index1D i,
                    ArrayView<float> rTmp, ArrayView<float> gTmp,
                    ArrayView<float> rIn, ArrayView<float> gIn,
                    int w, int radius) =>
                {
                    int x = i % w;

                    float sumR = 0f, sumG = 0f;
                    int count = 0;
                    for (int k = x - radius; k <= x + radius; k++)
                    {
                        int idx = (i / w) * w + (k < 0 ? 0 : (k >= w ? w - 1 : k));
                        sumR += rIn[idx];
                        sumG += gIn[idx];
                        count++;
                    }
                    rTmp[i] = sumR / count;
                    gTmp[i] = sumG / count;
                }));
        }

        private static readonly System.Collections.Concurrent.ConcurrentDictionary<Accelerator,
            Action<Index1D,
                ArrayView<float>, ArrayView<float>,
                ArrayView<float>, ArrayView<float>,
                int, int, int>> KernelVerticalCache = new();

        private static Action<Index1D,
            ArrayView<float>, ArrayView<float>,
            ArrayView<float>, ArrayView<float>,
            int, int, int> GetKernelVertical(Accelerator accelerator)
        {
            return KernelVerticalCache.GetOrAdd(accelerator, static acc =>
                acc.LoadAutoGroupedStreamKernel((
                    Index1D i,
                    ArrayView<float> rOut, ArrayView<float> gOut,
                    ArrayView<float> rTmp, ArrayView<float> gTmp,
                    int w, int h, int radius) =>
                {
                    int y = i / w;

                    float sumR = 0f, sumG = 0f;
                    int count = 0;
                    for (int k = y - radius; k <= y + radius; k++)
                    {
                        int row = k < 0 ? 0 : (k >= h ? h - 1 : k);
                        int idx = row * w + (i % w);
                        sumR += rTmp[idx];
                        sumG += gTmp[idx];
                        count++;
                    }
                    rOut[i] = sumR / count;
                    gOut[i] = sumG / count;
                }));
        }

        public object[] Compute(object[] args)
        {
            Accelerator accelerator;
            if (accelerators.Length > 1) { if (accelIdx >= accelerators.Length) accelIdx = 0; accelerator = accelerators[accelIdx++]; }
            else { accelerator = accelerators[0]; }

            var rIn = args[0] as float[] ?? throw new ArgumentException("Invalid argument for R");
            var gIn = args[1] as float[] ?? throw new ArgumentException("Invalid argument for G");
            var bIn = args[2] as float[] ?? throw new ArgumentException("Invalid argument for B");
            var aIn = args[3] as float[] ?? throw new ArgumentException("Invalid argument for A");
            int w = Convert.ToInt32(args[4]);
            float sigma = Convert.ToSingle(args[5]);

            int radius = (int)MathF.Ceiling(sigma);
            if (radius <= 0) radius = 1;
            int length = rIn.Length;
            int h = length / w;

            using var rBufIn = accelerator.Allocate1D(rIn);
            using var gBufIn = accelerator.Allocate1D(gIn);
            using var bBufIn = accelerator.Allocate1D(bIn);
            using var aBufIn = accelerator.Allocate1D(aIn);
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

            if (Sync)
            {
                using (ILGPUComputerHelper.locker.EnterScope())
                {
                    kernelH(length, rBufTmp.View, gBufTmp.View, rBufIn.View, gBufIn.View, w, radius);
                    kernelH(length, bBufTmp.View, aBufTmp.View, bBufIn.View, aBufIn.View, w, radius);
                    accelerator.Synchronize();
                    kernelV(length, rBufOut.View, gBufOut.View, rBufTmp.View, gBufTmp.View, w, h, radius);
                    kernelV(length, bBufOut.View, aBufOut.View, bBufTmp.View, aBufTmp.View, w, h, radius);
                    accelerator.Synchronize();
                }
            }
            else
            {
                kernelH(length, rBufTmp.View, gBufTmp.View, rBufIn.View, gBufIn.View, w, radius);
                kernelH(length, bBufTmp.View, aBufTmp.View, bBufIn.View, aBufIn.View, w, radius);
                kernelV(length, rBufOut.View, gBufOut.View, rBufTmp.View, gBufTmp.View, w, h, radius);
                kernelV(length, bBufOut.View, aBufOut.View, bBufTmp.View, aBufTmp.View, w, h, radius);
            }

            return [rBufOut.GetAsArray1D(), gBufOut.GetAsArray1D(), bBufOut.GetAsArray1D(), aBufOut.GetAsArray1D()];
        }

        bool ISessionComputer.SupportsBatching => !Sync;

        IGpuEffectSession ISessionComputer.CreateSession(float[] r, float[] g, float[] b, float[] a, int width, int height)
        {
            var accel = accelerators.Length > 1 ? accelerators[(accelIdx++) % accelerators.Length] : accelerators[0];
            return new ILGPUGpuEffectSession(accel, r, g, b, a, width, height);
        }

        void ISessionComputer.ExecuteOnSession(IGpuEffectSession session, IReadOnlyDictionary<string, object> parameters)
        {
            var sess = (ILGPUGpuEffectSession)session;
            int length = (int)sess.CurBufR.Length;
            int w = parameters.TryGetValue("BuiltIn.TargetWidth", out var wObj) ? Convert.ToInt32(wObj) : sess.Width;
            float sigma = Convert.ToSingle(parameters["Sigma"]);
            int radius = (int)MathF.Ceiling(sigma);
            if (radius <= 0) radius = 1;
            int h = length / w;

            using var rBufTmp = sess.Accelerator.Allocate1D<float>(length);
            using var gBufTmp = sess.Accelerator.Allocate1D<float>(length);
            using var bBufTmp = sess.Accelerator.Allocate1D<float>(length);
            using var aBufTmp = sess.Accelerator.Allocate1D<float>(length);

            var kernelH = GetKernelHorizontal(sess.Accelerator);
            var kernelV = GetKernelVertical(sess.Accelerator);

            kernelH(length, rBufTmp.View, gBufTmp.View, sess.CurBufR.View, sess.CurBufG.View, w, radius);
            kernelH(length, bBufTmp.View, aBufTmp.View, sess.CurBufB.View, sess.CurBufA.View, w, radius);
            kernelV(length, sess.AltBufR.View, sess.AltBufG.View, rBufTmp.View, gBufTmp.View, w, h, radius);
            kernelV(length, sess.AltBufB.View, sess.AltBufA.View, bBufTmp.View, aBufTmp.View, w, h, radius);
            sess.SwapBuffers();
        }
    }

    public class ColorAdjustmentComputer : IComputer, ISessionComputer
    {
        public string FromPlugin => "projectFrameCut.Render.WindowsRender.WindowsComputers";
        public string SupportedEffectOrMixture => "ColorAdjustment";

        [SetsRequiredMembers]
        public ColorAdjustmentComputer(Accelerator[] accel, bool? sync)
        {
            accelerators = accel;
            Sync = sync ?? accel.Any(a => a.AcceleratorType == AcceleratorType.OpenCL);
        }

        public required Accelerator[] accelerators { get; init; }
        public bool Sync { get; set; } = false;
        private int accelIdx = 0;

        // 11 params packed as: brightness, contrast, saturation, hue, gamma,
        //                       vibrance, temperature, invertF, grayscale, opacity, maxV
        private static readonly System.Collections.Concurrent.ConcurrentDictionary<Accelerator,
            Action<Index1D,
                ArrayView<float>, ArrayView<float>, ArrayView<float>, ArrayView<float>,
                ArrayView<float>, ArrayView<float>, ArrayView<float>, ArrayView<float>,
                ArrayView<float>>> KernelCache = new();

        private static Action<Index1D,
            ArrayView<float>, ArrayView<float>, ArrayView<float>, ArrayView<float>,
            ArrayView<float>, ArrayView<float>, ArrayView<float>, ArrayView<float>,
            ArrayView<float>> GetKernel(Accelerator accelerator)
        {
            return KernelCache.GetOrAdd(accelerator, static acc =>
                acc.LoadAutoGroupedStreamKernel((
                    Index1D i,
                    ArrayView<float> rOut, ArrayView<float> gOut, ArrayView<float> bOut, ArrayView<float> aOut,
                    ArrayView<float> rIn, ArrayView<float> gIn, ArrayView<float> bIn, ArrayView<float> aIn,
                    ArrayView<float> p) =>
                {
                    float r = rIn[i], g = gIn[i], b = bIn[i], a = aIn[i];
                    float brightness = p[0], contrast = p[1], saturation = p[2], hue = p[3], gamma = p[4];
                    float vibrance = p[5], temperature = p[6], invertF = p[7], grayscale = p[8], opacity = p[9], maxV = p[10];

                    // 1. Brightness
                    float bf = brightness - 1f;
                    r = bf >= 0f ? r + (maxV - r) * bf : r * (1f + bf);
                    g = bf >= 0f ? g + (maxV - g) * bf : g * (1f + bf);
                    b = bf >= 0f ? b + (maxV - b) * bf : b * (1f + bf);

                    // 2. Contrast
                    r = ((r / maxV - 0.5f) * contrast + 0.5f) * maxV;
                    g = ((g / maxV - 0.5f) * contrast + 0.5f) * maxV;
                    b = ((b / maxV - 0.5f) * contrast + 0.5f) * maxV;

                    // 3. Saturation
                    float gray = 0.2126f * r + 0.7152f * g + 0.0722f * b;
                    r = gray + saturation * (r - gray);
                    g = gray + saturation * (g - gray);
                    b = gray + saturation * (b - gray);

                    // 4. Hue (RGB->HSL, rotate H, HSL->RGB all inline)
                    if (MathF.Abs(hue) > 1e-6f)
                    {
                        float nr = r / maxV, ng = g / maxV, nb = b / maxV;
                        float cMax = MathF.Max(nr, MathF.Max(ng, nb));
                        float cMin = MathF.Min(nr, MathF.Min(ng, nb));
                        float delta = cMax - cMin;

                        float h = 0f;
                        if (delta > 1e-6f)
                        {
                            if (cMax == nr) { float t = (ng - nb) / delta; h = 60f * (t - 6f * (int)(t / 6f)); }
                            else if (cMax == ng) h = 60f * (((nb - nr) / delta) + 2f);
                            else h = 60f * (((nr - ng) / delta) + 4f);
                            if (h < 0f) h += 360f;
                        }
                        float l = (cMax + cMin) * 0.5f;
                        float s = (l > 0f && l < 1f) ? delta / (1f - MathF.Abs(2f * l - 1f)) : 0f;

                        h += hue;
                        if (h < 0f) h += 360f;
                        if (h >= 360f) h -= 360f;

                        if (s < 1e-6f) { r = l * maxV; g = l * maxV; b = l * maxV; }
                        else
                        {
                            float qq = l < 0.5f ? l * (1f + s) : l + s - l * s;
                            float pp = 2f * l - qq;
                            float hN = h / 360f;

                            float Tr = hN + 1f / 3f; if (Tr < 0f) Tr += 1f; if (Tr > 1f) Tr -= 1f;
                            float Tg = hN; if (Tg < 0f) Tg += 1f; if (Tg > 1f) Tg -= 1f;
                            float Tb = hN - 1f / 3f; if (Tb < 0f) Tb += 1f; if (Tb > 1f) Tb -= 1f;

                            r = (Tr < 1f / 6f ? pp + (qq - pp) * 6f * Tr : Tr < 1f / 2f ? qq : Tr < 2f / 3f ? pp + (qq - pp) * (2f / 3f - Tr) * 6f : pp) * maxV;
                            g = (Tg < 1f / 6f ? pp + (qq - pp) * 6f * Tg : Tg < 1f / 2f ? qq : Tg < 2f / 3f ? pp + (qq - pp) * (2f / 3f - Tg) * 6f : pp) * maxV;
                            b = (Tb < 1f / 6f ? pp + (qq - pp) * 6f * Tb : Tb < 1f / 2f ? qq : Tb < 2f / 3f ? pp + (qq - pp) * (2f / 3f - Tb) * 6f : pp) * maxV;
                        }
                    }

                    // 5. Gamma
                    if (MathF.Abs(gamma - 1f) > 1e-6f)
                    {
                        float invGamma = 1f / gamma;
                        r = maxV * MathF.Pow(r / maxV, invGamma);
                        g = maxV * MathF.Pow(g / maxV, invGamma);
                        b = maxV * MathF.Pow(b / maxV, invGamma);
                    }

                    // 6. Vibrance
                    if (MathF.Abs(vibrance) > 1e-6f)
                    {
                        float vSat = 1f + vibrance * 0.5f;
                        float vGray = 0.2126f * r + 0.7152f * g + 0.0722f * b;
                        r = vGray + vSat * (r - vGray);
                        g = vGray + vSat * (g - vGray);
                        b = vGray + vSat * (b - vGray);
                    }

                    // 7. Temperature
                    if (MathF.Abs(temperature) > 1e-6f)
                    {
                        r *= 1f + temperature * 0.01f;
                        b *= 1f - temperature * 0.01f;
                    }

                    // 8. Invert
                    if (invertF > 0.5f)
                    {
                        r = maxV - r; g = maxV - g; b = maxV - b;
                    }

                    // 9. Grayscale
                    if (grayscale > 1e-6f)
                    {
                        float lum = 0.2126f * r + 0.7152f * g + 0.0722f * b;
                        float gs = grayscale >= 1f ? 1f : 1f - grayscale;
                        r = lum + gs * (r - lum);
                        g = lum + gs * (g - lum);
                        b = lum + gs * (b - lum);
                    }

                    // 10. Opacity
                    a *= opacity;

                    rOut[i] = r; gOut[i] = g; bOut[i] = b; aOut[i] = a;
                }));
        }

        public object[] Compute(object[] args)
        {
            Accelerator accelerator;
            if (accelerators.Length > 1) { if (accelIdx >= accelerators.Length) accelIdx = 0; accelerator = accelerators[accelIdx++]; }
            else { accelerator = accelerators[0]; }

            var rIn = args[0] as float[] ?? throw new ArgumentException("Invalid argument for R");
            var gIn = args[1] as float[] ?? throw new ArgumentException("Invalid argument for G");
            var bIn = args[2] as float[] ?? throw new ArgumentException("Invalid argument for B");
            var aIn = args[3] as float[] ?? throw new ArgumentException("Invalid argument for A");
            float brightness = Convert.ToSingle(args[4]);
            float contrast = Convert.ToSingle(args[5]);
            float saturation = Convert.ToSingle(args[6]);
            float hue = Convert.ToSingle(args[7]);
            float gamma = Convert.ToSingle(args[8]);
            float vibrance = Convert.ToSingle(args[9]);
            float temperature = Convert.ToSingle(args[10]);
            float invertF = Convert.ToSingle(args[11]);
            float grayscale = Convert.ToSingle(args[12]);
            float opacity = Convert.ToSingle(args[13]);
            float maxV = Convert.ToSingle(args[14]);

            int length = rIn.Length;
            float[] paramArr = [brightness, contrast, saturation, hue, gamma, vibrance, temperature, invertF, grayscale, opacity, maxV];

            using var rBufIn = accelerator.Allocate1D(rIn);
            using var gBufIn = accelerator.Allocate1D(gIn);
            using var bBufIn = accelerator.Allocate1D(bIn);
            using var aBufIn = accelerator.Allocate1D(aIn);
            using var paramBuf = accelerator.Allocate1D(paramArr);
            using var rBufOut = accelerator.Allocate1D<float>(length);
            using var gBufOut = accelerator.Allocate1D<float>(length);
            using var bBufOut = accelerator.Allocate1D<float>(length);
            using var aBufOut = accelerator.Allocate1D<float>(length);

            var kernel = GetKernel(accelerator);
            if (Sync)
            {
                using (ILGPUComputerHelper.locker.EnterScope())
                {
                    kernel(length, rBufOut.View, gBufOut.View, bBufOut.View, aBufOut.View,
                        rBufIn.View, gBufIn.View, bBufIn.View, aBufIn.View, paramBuf.View);
                    accelerator.Synchronize();
                }
            }
            else
            {
                kernel(length, rBufOut.View, gBufOut.View, bBufOut.View, aBufOut.View,
                    rBufIn.View, gBufIn.View, bBufIn.View, aBufIn.View, paramBuf.View);
            }

            return [rBufOut.GetAsArray1D(), gBufOut.GetAsArray1D(), bBufOut.GetAsArray1D(), aBufOut.GetAsArray1D()];
        }

        bool ISessionComputer.SupportsBatching => !Sync;

        IGpuEffectSession ISessionComputer.CreateSession(float[] r, float[] g, float[] b, float[] a, int width, int height)
        {
            var accel = accelerators.Length > 1 ? accelerators[(accelIdx++) % accelerators.Length] : accelerators[0];
            return new ILGPUGpuEffectSession(accel, r, g, b, a, width, height);
        }

        void ISessionComputer.ExecuteOnSession(IGpuEffectSession session, IReadOnlyDictionary<string, object> parameters)
        {
            var sess = (ILGPUGpuEffectSession)session;
            int length = (int)sess.CurBufR.Length;
            float brightness = Convert.ToSingle(parameters["Brightness"]);
            float contrast = Convert.ToSingle(parameters["Contrast"]);
            float saturation = Convert.ToSingle(parameters["Saturation"]);
            float hue = Convert.ToSingle(parameters["Hue"]);
            float gamma = Convert.ToSingle(parameters["Gamma"]);
            float vibrance = Convert.ToSingle(parameters["Vibrance"]);
            float temperature = Convert.ToSingle(parameters["Temperature"]);
            float invertF = Convert.ToBoolean(parameters["Invert"]) ? 1f : 0f;
            float grayscale = Convert.ToSingle(parameters["Grayscale"]);
            float opacity = Convert.ToSingle(parameters["Opacity"]);
            float maxV = 65535f;
            float[] paramArr = [brightness, contrast, saturation, hue, gamma, vibrance, temperature, invertF, grayscale, opacity, maxV];

            using var paramBuf = sess.Accelerator.Allocate1D(paramArr);
            var kernel = GetKernel(sess.Accelerator);

            kernel(length,
                sess.AltBufR.View, sess.AltBufG.View, sess.AltBufB.View, sess.AltBufA.View,
                sess.CurBufR.View, sess.CurBufG.View, sess.CurBufB.View, sess.CurBufA.View,
                paramBuf.View);
            sess.SwapBuffers();
        }
    }

    internal static class BlendModeILGPUHelper
    {
        internal static object[] BlendModeCompute(
            Accelerator[] accelerators, ref int accelIdx, bool sync,
            Func<Accelerator, Action<Index1D, ArrayView<float>, ArrayView<float>, ArrayView<float>, ArrayView<float>, ArrayView<float>, ArrayView<float>>> getKernel,
            object[] args)
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

            var top = args[0] as float[] ?? throw new InvalidDataException("Invalid argument for top channel");
            var bottom = args[1] as float[] ?? throw new InvalidDataException("Invalid argument for base channel");
            var topAlpha = args.Length > 2 ? (args[2] as float[]) : null;
            var bottomAlpha = args.Length > 3 ? (args[3] as float[]) : null;
            var outputBpp = args.Length > 4 ? Convert.ToInt32(args[4]) : 16;
            var pixelCount = args.Length > 5 ? Convert.ToInt32(args[5]) : top.Length;

            if (topAlpha is null)
            {
                topAlpha = new float[pixelCount];
                Array.Fill(topAlpha, 1f);
            }
            if (bottomAlpha is null)
            {
                bottomAlpha = new float[pixelCount];
                Array.Fill(bottomAlpha, 1f);
            }

            using var topBuf = accelerator.Allocate1D(top);
            using var bottomBuf = accelerator.Allocate1D(bottom);
            using var topAlphaBuf = accelerator.Allocate1D(topAlpha);
            using var bottomAlphaBuf = accelerator.Allocate1D(bottomAlpha);
            using var outCBuf = accelerator.Allocate1D<float>(pixelCount);
            using var outABuf = accelerator.Allocate1D<float>(pixelCount);

            var kernel = getKernel(accelerator);

            if (sync)
            {
                using (ILGPUComputerHelper.locker.EnterScope())
                {
                    kernel(pixelCount, topBuf.View, bottomBuf.View, topAlphaBuf.View, bottomAlphaBuf.View, outCBuf.View, outABuf.View);
                    accelerator.Synchronize();
                }
            }
            else
            {
                kernel(pixelCount, topBuf.View, bottomBuf.View, topAlphaBuf.View, bottomAlphaBuf.View, outCBuf.View, outABuf.View);
            }

            var outC = outCBuf.GetAsArray1D();
            var outA = outABuf.GetAsArray1D();

            if (outputBpp == 8)
            {
                var byteOut = new byte[pixelCount];
                for (int i = 0; i < pixelCount; i++)
                {
                    float v = outC[i] / 257f;
                    if (v < 0f) v = 0f;
                    if (v > 255f) v = 255f;
                    byteOut[i] = (byte)v;
                }
                return [byteOut, outA];
            }
            else if (outputBpp == 16)
            {
                var ushortOut = new ushort[pixelCount];
                for (int i = 0; i < pixelCount; i++)
                {
                    float v = outC[i];
                    if (v < 0f) v = 0f;
                    if (v > 65535f) v = 65535f;
                    ushortOut[i] = (ushort)v;
                }
                return [ushortOut, outA];
            }
            else
            {
                return [outC, outA];
            }
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
                    Logger.Log($"ERROR: Accelerator {acceleratorId} is not exist.", "error");
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
                Logger.Log($"ERROR: acceleratorType {accelType} is not supported.");
            }
            return pick;
        }


    }
}
