using projectFrameCut.Render;
using projectFrameCut.Shared;
using projectFrameCut.Render.Effects;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace projectFrameCut.VideoMakeEngine
{
    public class RemoveColorEffect : IEffect
    {
        public ushort R { get; init; }
        public ushort G { get; init; }
        public ushort B { get; init; }
        public ushort A { get; init; }
        public ushort Tolerance { get; init; } = 0;

        public Dictionary<string, object> Parameters => new Dictionary<string, object>
        {
            { "R", R },
            { "G", G },
            { "B", B },
            { "A", A },
            { "Tolerance", Tolerance },
        };

        public AcceleratedComputer Computer { get; set; } = ComputerSource.RemoveColorComputer;

        public List<string> ParametersNeeded { get; } = new List<string>
        {
            "R",
            "G",
            "B",
            "A",
            "Tolerance",
        };

        public Dictionary<string, string> ParametersType { get; } = new Dictionary<string, string>
        {
            { "R", "ushort" },
            { "G", "ushort" },
            { "B", "ushort" },
            { "A", "ushort" },
            { "Tolerance", "ushort" },
        };


        public IEffect FromParametersDictionary(Dictionary<string, object> parameters, IAcceleratedComputer computer)
        {
            ArgumentNullException.ThrowIfNull(parameters);
            ArgumentNullException.ThrowIfNull(computer);
            if (!ParametersNeeded.All(parameters.ContainsKey))
            {
                throw new ArgumentException($"Missing parameters: {string.Join(", ", ParametersNeeded.Where(p => !parameters.ContainsKey(p)))}");
            }
            if (parameters.Count != ParametersNeeded.Count)
            {
                throw new ArgumentException("Too many parameters provided.");
            }

            // build an AcceleratedComputer that uses the managed kernel from ComputerSource and the provided accelerator
            Computer = new AcceleratedComputer
            {
                ManagedSource = ComputerSource.RemoveColorComputer.ManagedSource,
                ILGpuSource = computer,
                OpenGLSource = computer,
                MetalSource = computer
            };

            return new RemoveColorEffect
            {
                R = Convert.ToUInt16(parameters["R"]),
                G = Convert.ToUInt16(parameters["G"]),
                B = Convert.ToUInt16(parameters["B"]),
                A = Convert.ToUInt16(parameters["A"]),
                Tolerance = Convert.ToUInt16(parameters["Tolerance"]),
                Computer = Computer
            };
        }

        public Picture Render(Picture source)
        {
            ArgumentNullException.ThrowIfNull(source);
            int pixels = source.Pixels;
            if (pixels <= 0) return source;

            // compute 16-bit inclusive ranges per channel around the provided R,G,B with tolerance
            int lowR = Math.Max(0, R - Tolerance);
            int lowG = Math.Max(0, G - Tolerance);
            int lowB = Math.Max(0, B - Tolerance);
            int highR = Math.Min(65535, R + Tolerance);
            int highG = Math.Min(65535, G + Tolerance);
            int highB = Math.Min(65535, B + Tolerance);

            float lowRf = lowR / 65535f;
            float lowGf = lowG / 65535f;
            float lowBf = lowB / 65535f;
            float highRf = highR / 65535f;
            float highGf = highG / 65535f;
            float highBf = highB / 65535f;

            // normalize source channels
            float[] aR = new float[pixels];
            float[] aG = new float[pixels];
            float[] aB = new float[pixels];
            for (int i = 0; i < pixels; i++)
            {
                aR[i] = source.r[i] / 65535f;
                aG[i] = source.g[i] / 65535f;
                aB[i] = source.b[i] / 65535f;
            }

            float[] zeros = new float[pixels];
            float[] loR = Enumerable.Repeat(lowRf, pixels).ToArray();
            float[] loG = Enumerable.Repeat(lowGf, pixels).ToArray();
            float[] loB = Enumerable.Repeat(lowBf, pixels).ToArray();
            float[] hiR = Enumerable.Repeat(highRf, pixels).ToArray();
            float[] hiG = Enumerable.Repeat(highGf, pixels).ToArray();
            float[] hiB = Enumerable.Repeat(highBf, pixels).ToArray();

            // compute per-channel masks: 0 inside the range, source value outside
            float[] rMask = Computer.Compute(MyAcceleratorType.CPU, 3, aR, loR, hiR, zeros, zeros, zeros);
            float[] gMask = Computer.Compute(MyAcceleratorType.CPU, 3, aG, loG, hiG, zeros, zeros, zeros);
            float[] bMask = Computer.Compute(MyAcceleratorType.CPU, 3, aB, loB, hiB, zeros, zeros, zeros);

            source.EnsureAlpha();
            float[] newAlpha = new float[pixels];
            // mutate copies of rgb
            ushort[] outR = new ushort[pixels];
            ushort[] outG = new ushort[pixels];
            ushort[] outB = new ushort[pixels];

            for (int i = 0; i < pixels; i++)
            {
                bool removed = rMask[i] == 0f && gMask[i] == 0f && bMask[i] == 0f;
                newAlpha[i] = removed ? 0f : source.a![i];
                if (removed)
                {
                    outR[i] = 0; outG[i] = 0; outB[i] = 0;
                }
                else
                {
                    outR[i] = source.r[i];
                    outG[i] = source.g[i];
                    outB[i] = source.b[i];
                }
            }

            var result = new Picture(source)
            {
                r = outR,
                g = outG,
                b = outB,
                a = newAlpha,
                hasAlphaChannel = true
            };
            return result;
        }
    }
}
