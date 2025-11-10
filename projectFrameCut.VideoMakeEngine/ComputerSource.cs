using projectFrameCut.Shared;

namespace projectFrameCut.Render.Effects
{

    public static class ComputerSource
    {
        public const string _version = "1.0.0";

        public static IAcceleratedComputer? ILGpuAccelerator { get; set; }
        public static IAcceleratedComputer? OpenGLAccelerator { get; set; }
        public static IAcceleratedComputer? MetalAccelerator { get; set; }

        public static readonly AcceleratedComputer OverlayComputer;


        // Crop and Resize are geometry operations and do not change per-pixel values; keep a pass-through ManagedSource.
        public static readonly AcceleratedComputer CropComputer;
        public static readonly AcceleratedComputer ResizeComputer;

        // Color related effects
        public static readonly AcceleratedComputer ColorCorrectionComputer;
        public static readonly AcceleratedComputer RemoveColorComputer;
        public static readonly AcceleratedComputer ReplaceAlphaComputer;

        // Simple mixture arithmetic as per RenderMode (operates on normalized values0..1)
        public static readonly AcceleratedComputer MixtureAddComputer;
        public static readonly AcceleratedComputer MixtureMinusComputer;
        public static readonly AcceleratedComputer MixtureMultiplyComputer;

        static ComputerSource()
        {
            // Crop and Resize: identity per-pixel function
            CropComputer = CreateComputer((a, b, c, d, e, f) => a);
            ResizeComputer = CreateComputer((a, b, c, d, e, f) => a);

            // Color correction: A = pixel (normalized0..1), B = brightness (multiplier), C = contrast (multiplier), D = saturation (unused here)
            ColorCorrectionComputer = CreateComputer((a, brightness, contrast, saturation, e, f) =>
            {
                // apply contrast around0.5 then brightness
                float centered = (a - 0.5f) * contrast + 0.5f;
                float outv = centered * brightness;
                if (outv < 0f) outv = 0f;
                if (outv > 1f) outv = 1f;
                return outv;
            });

            // RemoveColor: A = pixel channel normalized (0..1), B = low bound (0..1), C = high bound (0..1)
            // returns0 if within [B,C], otherwise returns original value
            RemoveColorComputer = CreateComputer((a, low, high, d, e, f) =>
            {
                if (low <= high)
                {
                    return (a >= low && a <= high) ? 0f : a;
                }
                else
                {
                    // if bounds are reversed, treat as no removal
                    return a;
                }
            });

            // ReplaceAlpha: A = oldPixel, B = newAlpha (0..1) => return B (alpha channel replacement)
            ReplaceAlphaComputer = CreateComputer((a, b, c, d, e, f) => b);

            // Mixture arithmetic - operate on normalized values0..1 and clamp
            MixtureAddComputer = CreateComputer((a, b, c, d, e, f) =>
            {
                float v = a + b;
                if (v > 1f) v = 1f;
                return v;
            });

            MixtureMinusComputer = CreateComputer((a, b, c, d, e, f) =>
            {
                float v = a - b;
                if (v < 0f) v = 0f;
                return v;
            });

            MixtureMultiplyComputer = CreateComputer((a, b, c, d, e, f) =>
            {
                float v = a * b;
                if (v < 0f) v = 0f;
                if (v > 1f) v = 1f;
                return v;
            });

            // Overlay: A = base pixel (normalized0..1), B = overlay pixel (normalized0..1)
            OverlayComputer = CreateComputer((a, b, c, d, e, f) =>
            {
                // Simple overlay algorithm
                if (b < 0.5f)
                {
                    return 2f * a * b;
                }
                else
                {
                    return 1f - 2f * (1f - a) * (1f - b);
                }
            });
        }

        // Helper to create an AcceleratedComputer and populate all properties.
        private static AcceleratedComputer CreateComputer(Func<float, float, float, float, float, float, float> managed)
        {
            var acc = new AcceleratedComputer();
            acc.ManagedSource = managed;

            // Create a simple IAcceleratedComputer that calls the ManagedSource per-element on CPU.
            var wrapper = new ManagedAcceleratedComputer(managed);
            acc.ILGpuSource = ILGpuAccelerator ?? wrapper;
            acc.OpenGLSource = OpenGLAccelerator ?? wrapper;
            acc.MetalSource = MetalAccelerator ?? wrapper;
            return acc;
        }

        // Simple implementation of IAcceleratedComputer that executes the provided managed function on CPU.
        private sealed class ManagedAcceleratedComputer : IAcceleratedComputer
        {
            private readonly Func<float, float, float, float, float, float, float> _func;

            public ManagedAcceleratedComputer(Func<float, float, float, float, float, float, float> func)
            {
                _func = func ?? throw new ArgumentNullException(nameof(func));
            }

            public float[] Compute(MyAcceleratorType type, int numOfArgs, float[] A, float[] B, float[] C, float[] D, float[] E, float[] F, object? extend = null)
            {
                if (A == null) throw new ArgumentNullException(nameof(A));
                int n = A.Length;
                var result = new float[n];
                for (int i = 0; i < n; i++)
                {
                    float a = A[i];
                    float b = B != null && B.Length > i ? B[i] : 0f;
                    float c = C != null && C.Length > i ? C[i] : 0f;
                    float d = D != null && D.Length > i ? D[i] : 0f;
                    float e = E != null && E.Length > i ? E[i] : 0f;
                    float f = F != null && F.Length > i ? F[i] : 0f;
                    result[i] = _func(a, b, c, d, e, f);
                }
                return result;
            }
        }
    }
}
