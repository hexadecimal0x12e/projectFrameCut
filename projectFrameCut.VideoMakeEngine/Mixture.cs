using projectFrameCut.Render;
using projectFrameCut.Render.Effects;
using projectFrameCut.Shared;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace projectFrameCut.VideoMakeEngine
{
    public enum MixtureMode
    {
        Add,
        Minus,
        Multiply,
        Overlay
    }

    public sealed class SimpleMixture : IMixture
    {
        public MixtureMode Mode { get; init; } = MixtureMode.Add;
        public ushort UpperBound { get; init; } = ushort.MaxValue;
        public bool AllowOverflow { get; init; } = false;

        public AcceleratedComputer Computer { get; set; } = ComputerSource.MixtureAddComputer;

        public List<string> ParametersNeeded { get; } = new List<string>
        {
            "Mode"
        };

        public Dictionary<string, object> Parameters => new Dictionary<string, object>
        {
            { "Mode", Mode.ToString() },
            { "UpperBound", UpperBound },
            { "AllowOverflow", AllowOverflow }
        };

        public Dictionary<string, string> ParametersType { get; } = new Dictionary<string, string>
        {
            { "Mode", nameof(String) },
            { "UpperBound", "ushort" },
            { "AllowOverflow", nameof(Boolean) }
        };

        public IMixture FromParametersDictionary(Dictionary<string, object> parameters, IAcceleratedComputer computer)
        {
            ArgumentNullException.ThrowIfNull(parameters);
            ArgumentNullException.ThrowIfNull(computer);
            if (!ParametersNeeded.All(parameters.ContainsKey))
            {
                throw new ArgumentException($"Missing parameters: {string.Join(", ", ParametersNeeded.Where(p => !parameters.ContainsKey(p)))}");
            }

            MixtureMode mode;
            object modeObj = parameters["Mode"];
            if (modeObj is string s)
            {
                if (!Enum.TryParse<MixtureMode>(s, true, out mode))
                    throw new ArgumentException($"Invalid Mode '{s}'.");
            }
            else if (modeObj is MixtureMode m)
            {
                mode = m;
            }
            else
            {
                mode = (MixtureMode)Convert.ToInt32(modeObj);
            }

            ushort upper = parameters.ContainsKey("UpperBound") ? Convert.ToUInt16(parameters["UpperBound"]) : ushort.MaxValue;
            bool overflow = parameters.ContainsKey("AllowOverflow") && Convert.ToBoolean(parameters["AllowOverflow"]);

            AcceleratedComputer acc = mode switch
            {
                MixtureMode.Add => CloneWithAccelerator(ComputerSource.MixtureAddComputer, computer),
                MixtureMode.Minus => CloneWithAccelerator(ComputerSource.MixtureMinusComputer, computer),
                MixtureMode.Multiply => CloneWithAccelerator(ComputerSource.MixtureMultiplyComputer, computer),
                MixtureMode.Overlay => CloneWithAccelerator(ComputerSource.OverlayComputer, computer),
                _ => CloneWithAccelerator(ComputerSource.MixtureAddComputer, computer)
            };

            return new SimpleMixture
            {
                Mode = mode,
                UpperBound = upper,
                AllowOverflow = overflow,
                Computer = acc
            };
        }

        public Picture Mix(Picture basePicture, Picture topPicture)
        {
            ArgumentNullException.ThrowIfNull(basePicture);
            ArgumentNullException.ThrowIfNull(topPicture);
            if (basePicture.Pixels != topPicture.Pixels)
                throw new ArgumentException("basePicture and topPicture must have same pixel count");

            int pixels = basePicture.Pixels;
            // normalize channels
            float[] aR = new float[pixels];
            float[] aG = new float[pixels];
            float[] aB = new float[pixels];
            float[] bR = new float[pixels];
            float[] bG = new float[pixels];
            float[] bB = new float[pixels];

            for (int i = 0; i < pixels; i++)
            {
                aR[i] = basePicture.r[i] / 65535f;
                aG[i] = basePicture.g[i] / 65535f;
                aB[i] = basePicture.b[i] / 65535f;
                bR[i] = topPicture.r[i] / 65535f;
                bG[i] = topPicture.g[i] / 65535f;
                bB[i] = topPicture.b[i] / 65535f;
            }

            float[] zeros = new float[pixels];

            float[] oR, oG, oB;
            switch (Mode)
            {
                case MixtureMode.Add:
                    oR = ComputerSource.MixtureAddComputer.Compute(MyAcceleratorType.CPU, 2, aR, bR, zeros, zeros, zeros, zeros);
                    oG = ComputerSource.MixtureAddComputer.Compute(MyAcceleratorType.CPU, 2, aG, bG, zeros, zeros, zeros, zeros);
                    oB = ComputerSource.MixtureAddComputer.Compute(MyAcceleratorType.CPU, 2, aB, bB, zeros, zeros, zeros, zeros);
                    break;
                case MixtureMode.Minus:
                    oR = ComputerSource.MixtureMinusComputer.Compute(MyAcceleratorType.CPU, 2, aR, bR, zeros, zeros, zeros, zeros);
                    oG = ComputerSource.MixtureMinusComputer.Compute(MyAcceleratorType.CPU, 2, aG, bG, zeros, zeros, zeros, zeros);
                    oB = ComputerSource.MixtureMinusComputer.Compute(MyAcceleratorType.CPU, 2, aB, bB, zeros, zeros, zeros, zeros);
                    break;
                case MixtureMode.Multiply:
                    oR = ComputerSource.MixtureMultiplyComputer.Compute(MyAcceleratorType.CPU, 2, aR, bR, zeros, zeros, zeros, zeros);
                    oG = ComputerSource.MixtureMultiplyComputer.Compute(MyAcceleratorType.CPU, 2, aG, bG, zeros, zeros, zeros, zeros);
                    oB = ComputerSource.MixtureMultiplyComputer.Compute(MyAcceleratorType.CPU, 2, aB, bB, zeros, zeros, zeros, zeros);
                    break;
                case MixtureMode.Overlay:
                    oR = ComputerSource.OverlayComputer.Compute(MyAcceleratorType.CPU, 2, aR, bR, zeros, zeros, zeros, zeros);
                    oG = ComputerSource.OverlayComputer.Compute(MyAcceleratorType.CPU, 2, aG, bG, zeros, zeros, zeros, zeros);
                    oB = ComputerSource.OverlayComputer.Compute(MyAcceleratorType.CPU, 2, aB, bB, zeros, zeros, zeros, zeros);
                    break;
                default:
                    throw new NotSupportedException($"Unsupported mixture mode {Mode}.");
            }

            var result = new Picture(basePicture)
            {
                r = new ushort[pixels],
                g = new ushort[pixels],
                b = new ushort[pixels],
                a = basePicture.a,
                hasAlphaChannel = basePicture.hasAlphaChannel,
                Width = basePicture.Width,
                Height = basePicture.Height
            };

            for (int i = 0; i < pixels; i++)
            {
                int rr = (int)Math.Round(oR[i] * 65535f);
                int gg = (int)Math.Round(oG[i] * 65535f);
                int bb = (int)Math.Round(oB[i] * 65535f);

                if (!AllowOverflow)
                {
                    if (rr < 0) rr = 0; if (rr > 65535) rr = 65535;
                    if (gg < 0) gg = 0; if (gg > 65535) gg = 65535;
                    if (bb < 0) bb = 0; if (bb > 65535) bb = 65535;
                }

                if (UpperBound < ushort.MaxValue)
                {
                    if (rr > UpperBound) rr = UpperBound;
                    if (gg > UpperBound) gg = UpperBound;
                    if (bb > UpperBound) bb = UpperBound;
                }
                result.r[i] = (ushort)rr;
                result.g[i] = (ushort)gg;
                result.b[i] = (ushort)bb;
            }

            return result;
        }

        private static AcceleratedComputer CloneWithAccelerator(AcceleratedComputer source, IAcceleratedComputer accel)
        {
            return new AcceleratedComputer
            {
                ManagedSource = source.ManagedSource,
                ILGpuSource = accel,
                OpenGLSource = accel,
                MetalSource = accel
            };
        }
    }
}
