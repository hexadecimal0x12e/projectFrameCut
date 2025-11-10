using ILGPU;
using ILGPU.Runtime;
using projectFrameCut.Shared;
using System.Diagnostics.CodeAnalysis;

namespace projectFrameCut.Render.ILGPU
{
    public class ILGPUAccelerator : IAcceleratedComputer
    {
        static object? globalLocker = new();

        public required Accelerator accelerator
        {
            get => accel;
            set => accel = value;
        }

        [NotNull]
        Accelerator accel;

        public bool Sync { get; set; } = false;

        public float[] Compute(MyAcceleratorType type, int numOfArgs, float[] A, float[] B, float[] C, float[] D, float[] E, float[] F, object? extend = null)
        {

            if (extend is not Func<float, float, float, float, float, float, float> func)
                throw new NotSupportedException("The managedSource is invaild.");

            if (numOfArgs < 1 || numOfArgs > 6)
                throw new NotSupportedException("The number of arguments is not supported.");

            using var a = accel.Allocate1D(A);
            using var b = numOfArgs > 1 ? accel.Allocate1D(B) : default!;
            using var c = numOfArgs > 2 ? accel.Allocate1D(C) : default!;
            using var d = numOfArgs > 3 ? accel.Allocate1D(D) : default!;
            using var e = numOfArgs > 4 ? accel.Allocate1D(E) : default!;
            using var f = numOfArgs > 5 ? accel.Allocate1D(F) : default!;

            var output = accel.Allocate1D<float>(A.Length);

            void _compute()
            {
                switch (numOfArgs)
                {
                    case 1:
                        accel.LoadAutoGroupedStreamKernel((Index1D index, ArrayView<float> aView, ArrayView<float> outputView) =>
                        {
                            outputView[index] = func(aView[index], 0, 0, 0, 0, 0);
                        })(A.Length, a.View, output.View);
                        break;
                    case 2:
                        accel.LoadAutoGroupedStreamKernel((Index1D index, ArrayView<float> aView, ArrayView<float> bView, ArrayView<float> outputView) =>
                        {
                            outputView[index] = func(aView[index], bView[index], 0, 0, 0, 0);
                        })(A.Length, a.View, b.View, output.View);
                        break;
                    case 3:
                        accel.LoadAutoGroupedStreamKernel((Index1D index, ArrayView<float> aView, ArrayView<float> bView, ArrayView<float> cView, ArrayView<float> outputView) =>
                        {
                            outputView[index] = func(aView[index], bView[index], cView[index], 0, 0, 0);
                        })(A.Length, a.View, b.View, c.View, output.View);
                        break;
                    case 4:
                        accel.LoadAutoGroupedStreamKernel((Index1D index, ArrayView<float> aView, ArrayView<float> bView, ArrayView<float> cView, ArrayView<float> dView, ArrayView<float> outputView) =>
                        {
                            outputView[index] = func(aView[index], bView[index], cView[index], dView[index], 0, 0);
                        })(A.Length, a.View, b.View, c.View, d.View, output.View);
                        break;
                    case 5:
                        accel.LoadAutoGroupedStreamKernel((Index1D index, ArrayView<float> aView, ArrayView<float> bView, ArrayView<float> cView, ArrayView<float> dView, ArrayView<float> eView, ArrayView<float> outputView) =>
                        {
                            outputView[index] = func(aView[index], bView[index], cView[index], dView[index], eView[index], 0);
                        })(A.Length, a.View, b.View, c.View, d.View, e.View, output.View);
                        break;
                    case 6:
                        accel.LoadAutoGroupedStreamKernel((Index1D index, ArrayView<float> aView, ArrayView<float> bView, ArrayView<float> cView, ArrayView<float> dView, ArrayView<float> eView, ArrayView<float> fView, ArrayView<float> outputView) =>
                        {
                            outputView[index] = func(aView[index], bView[index], cView[index], dView[index], eView[index], fView[index]);
                        })(A.Length, a.View, b.View, c.View, d.View, e.View, f.View, output.View);
                        break;
                    default:
                        throw new NotSupportedException("The number of arguments is not supported.");
                }
            }

            if (Sync)
            {
                lock (globalLocker!)
                {
                    _compute();
                    accel.Synchronize();
                }
            }
            else
            {
                _compute();
            }

            var result = output.GetAsArray1D();
            output.Dispose();
            return result;
        }


    }

}
