using ILGPU;
using ILGPU.Runtime;
using projectFrameCut.Shared;

namespace projectFrameCut.Render.ILGPU
{
    public class ILGPUAccelerator : IAcceleratedComputer
    {
        static object? globalLocker = new();

        public required Accelerator accel;

        public bool Sync { get; set; } = false;

        public float[] Compute(MyAcceleratorType type, int numOfArgs, float[] A, float[] B, float[] C, float[] D, float[] E, float[] F, object? extend = null)
        {
            if (extend is not Func<float, float, float, float, float, float, float> func) throw new NotSupportedException("The managedSource is invaild.");
            void _compute(Index1D i, ArrayView<float> a, ArrayView<float> b, ArrayView<float> c, ArrayView<float> d, ArrayView<float> e, ArrayView<float> f, ArrayView<float> output)
            {
                output[i] = func(a[i], b[i], c[i], d[i], e[i], f[i]);
            }
            if(numOfArgs < 1 || numOfArgs > 6) throw new NotSupportedException("The number of arguments is not supported.");

            using var a = accel.Allocate1D(A);
            using var b = numOfArgs > 1 ? accel.Allocate1D(B) : default!;
            using var c = numOfArgs > 2 ? accel.Allocate1D(C) : default!;
            using var d = numOfArgs > 3 ? accel.Allocate1D(D) : default!;
            using var e = numOfArgs > 4 ? accel.Allocate1D(E) : default!;
            using var f = numOfArgs > 5 ? accel.Allocate1D(F) : default!;

            var output = accel.Allocate1D<float>(A.Length);

            var kernel = accel.LoadAutoGroupedStreamKernel<Index1D, ArrayView<float>, ArrayView<float>, ArrayView<float>, ArrayView<float>, ArrayView<float>, ArrayView<float>, ArrayView<float>>(_compute);

            if (Sync)
            {
                lock(globalLocker!)
                {
                    kernel(A.Length, a.View, numOfArgs > 1 ? b.View : default!, numOfArgs > 2 ? c.View : default!, numOfArgs > 3 ? d.View : default!, numOfArgs > 4 ? e.View : default!, numOfArgs > 5 ? f.View : default!, output.View);
                    accel.Synchronize();
                }
            }
            else
            {
                kernel(A.Length, a.View, numOfArgs > 1 ? b.View : default!, numOfArgs > 2 ? c.View : default!, numOfArgs > 3 ? d.View : default!, numOfArgs > 4 ? e.View : default!, numOfArgs > 5 ? f.View : default!, output.View);
            }

            var result = output.GetAsArray1D();
            output.Dispose();
            return result;
        }


    }

}
