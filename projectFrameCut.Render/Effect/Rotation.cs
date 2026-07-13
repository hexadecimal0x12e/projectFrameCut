using projectFrameCut.Drawing.Effect;
using projectFrameCut.Render.HwAccelContracts;
using projectFrameCut.Render.Plugin;
using projectFrameCut.Render.RenderAPIBase.EffectAndMixture;
using System.Diagnostics;

namespace projectFrameCut.Render.Effect
{
    public class RotationEffect_IPicture : INormalEffect
    {
        private TimeSpan? _elapsed;

        public bool Enabled { get; set; } = true;
        public int Index { get; set; }
        public string Name { get; set; } = "Rotation";
        public int RelativeWidth { get; set; }
        public int RelativeHeight { get; set; }

        /// <summary>
        /// 旋转角度（度），顺时针为正方向。
        /// </summary>
        public float Angle { get; init; }

        /// <summary>
        /// 是否扩展画布以容纳旋转后的完整图像。
        /// false 时保持原始画布大小（旋转超出部分被裁剪）。
        /// </summary>
        public bool ExpandCanvas { get; init; } = false;

        public Dictionary<string, object> Parameters => new Dictionary<string, object>
        {
            { "Angle", Angle },
            { "ExpandCanvas", ExpandCanvas },
        };

        public string? NeedComputer => null;
        public string FromPlugin => InternalPluginBase.InternalPluginBaseID;
        public EffectImplementType ImplementType { get; init; } = EffectImplementType.IPicture;
        public bool IsReorderable => true;

        public static List<string> ParametersNeeded { get; } = new List<string>
        {
            "Angle",
        };

        public static Dictionary<string, string> ParametersType { get; } = new Dictionary<string, string>
        {
            { "Angle", "float" },
            { "ExpandCanvas", "bool" },
        };

        public string TypeName => "Rotation";
        public string? BindedEffectGroupID { get; set; }
        public string Id { get; set; } = string.Empty;

        public static IEffect FromParametersDictionary(Dictionary<string, object> parameters, EffectImplementType implementType = EffectImplementType.IPicture)
        {
            ArgumentNullException.ThrowIfNull(parameters);
            if (!ParametersNeeded.All(parameters.ContainsKey))
            {
                throw new ArgumentException($"Missing parameters: {string.Join(", ", ParametersNeeded.Where(p => !parameters.ContainsKey(p)))}");
            }

            float angle = Convert.ToSingle(parameters["Angle"]);

            bool expandCanvas = false;
            if (parameters.TryGetValue("ExpandCanvas", out var expandVal))
            {
                expandCanvas = Convert.ToBoolean(expandVal);
            }

            return new RotationEffect_IPicture
            {
                Angle = angle,
                ExpandCanvas = expandCanvas,
                ImplementType = implementType,
            };
        }

        public IEffect WithParameters(Dictionary<string, object> parameters) => FromParametersDictionary(parameters);

        public IPicture Render(IPicture source, IComputer? computer, int targetWidth, int targetHeight)
        {
            var sw = Stopwatch.StartNew();

            var result = RotationEffect.Process(source, Angle, ExpandCanvas);

            sw.Stop();
            _elapsed = sw.Elapsed;
            result.ProcessStack = source.ProcessStack.Append(GetProcessStack()).ToList();
            return result;
        }

        private PictureProcessStack GetProcessStack() => new PictureProcessStack
        {
            Elapsed = _elapsed,
            OperationDisplayName = "Rotation",
            Operator = typeof(RotationEffect_IPicture),
            ProcessingFuncStackTrace = new StackTrace(true),
            
            Properties = new Dictionary<string, object>
            {
                { nameof(Angle), Angle },
                { nameof(ExpandCanvas), ExpandCanvas },
            }
        };
    }

    public class RotationEffect_HwAccel : INormalEffect
    {
        public bool Enabled { get; set; } = true;
        public int Index { get; set; }
        public string Name { get; set; } = "Rotation";
        public int RelativeWidth { get; set; }
        public int RelativeHeight { get; set; }

        public float Angle { get; init; }
        public bool ExpandCanvas { get; init; } = false;

        public Dictionary<string, object> Parameters => new Dictionary<string, object>
        {
            { "Angle", Angle },
            { "ExpandCanvas", ExpandCanvas },
        };

        public string? NeedComputer => "RotationComputer";
        public string FromPlugin => InternalPluginBase.InternalPluginBaseID;
        public EffectImplementType ImplementType => EffectImplementType.HwAcceleration;
        public bool IsReorderable => true;

        public static List<string> ParametersNeeded { get; } = new List<string> { "Angle" };
        public static Dictionary<string, string> ParametersType { get; } = new Dictionary<string, string>
        {
            { "Angle", "float" },
            { "ExpandCanvas", "bool" },
        };

        public string TypeName => "Rotation";
        public string? BindedEffectGroupID { get; set; }
        public string Id { get; set; } = string.Empty;

        public static IEffect FromParametersDictionary(Dictionary<string, object> parameters)
        {
            ArgumentNullException.ThrowIfNull(parameters);
            if (!ParametersNeeded.All(parameters.ContainsKey))
            {
                throw new ArgumentException($"Missing parameters: {string.Join(", ", ParametersNeeded.Where(p => !parameters.ContainsKey(p)))}");
            }
            float angle = Convert.ToSingle(parameters["Angle"]);
            bool expandCanvas = false;
            if (parameters.TryGetValue("ExpandCanvas", out var expandVal))
            {
                expandCanvas = Convert.ToBoolean(expandVal);
            }
            return new RotationEffect_HwAccel
            {
                Angle = angle,
                ExpandCanvas = expandCanvas
            };
        }

        public IEffect WithParameters(Dictionary<string, object> parameters) => FromParametersDictionary(parameters);

        public IPicture Render(IPicture source, IComputer? computer, int targetWidth, int targetHeight)
        {
            if (Math.Abs(Angle % 360f) < float.Epsilon)
                return source;

            if (computer is null)
                return RotationEffect.Process(source, Angle, ExpandCanvas);

            float angleRad = Angle * MathF.PI / 180f;
            float cos = MathF.Abs(MathF.Cos(angleRad));
            float sin = MathF.Abs(MathF.Sin(angleRad));

            int outW, outH;
            if (ExpandCanvas)
            {
                outW = (int)MathF.Ceiling(source.Width * cos + source.Height * sin);
                outH = (int)MathF.Ceiling(source.Width * sin + source.Height * cos);
            }
            else
            {
                outW = source.Width;
                outH = source.Height;
            }

            var sw = Stopwatch.StartNew();
            var (r, g, b, a, sourceHasAlpha) = HwAccelEffectHelper.ExtractFloatChannels(source);

            FourChannelResult computeResult;
            if (computer is IRotationComputer rc)
            {
                computeResult = rc.ComputeRotation(r, g, b, a, source.Width, source.Height, outW, outH, Angle);
            }
            else
            {
                var resultArr = computer.Compute([r, g, b, a, source.Width, source.Height, outW, outH, Angle]);

                if (resultArr.Length != 4 ||
                    resultArr[0] is not float[] rOut ||
                    resultArr[1] is not float[] gOut ||
                    resultArr[2] is not float[] bOut ||
                    resultArr[3] is not float[] aOut)
                {
                    throw new InvalidOperationException("RotationComputer did not return expected channel buffers.");
                }

                computeResult = new FourChannelResult(rOut, gOut, bOut, aOut);
            }

            var result = HwAccelEffectHelper.BuildPicture(source, outW, outH,
                computeResult.R, computeResult.G, computeResult.B, computeResult.A, sourceHasAlpha);
            sw.Stop();
            result.ProcessStack = source.ProcessStack.Append(new PictureProcessStack
            {
                Elapsed = sw.Elapsed,
                OperationDisplayName = "Rotation (GPU)",
                Operator = typeof(RotationEffect_HwAccel),
                ProcessingFuncStackTrace = new StackTrace(true),
                Properties = new Dictionary<string, object> { { "Angle", Angle }, { "ExpandCanvas", ExpandCanvas } }
            }).ToList();
            return result;
        }
    }

    public class RotationEffectFactory : IEffectFactory
    {
        public string FromPlugin => InternalPluginBase.InternalPluginBaseID;
        public string TypeName => "Rotation";
        public EffectTarget Target => EffectTarget.Video;
        public List<string> ParametersNeeded { get; } = new List<string> { "Angle" };
        public Dictionary<string, string> ParametersType { get; } = new Dictionary<string, string>
        {
            { "Angle", "float" },
            { "ExpandCanvas", "bool" },
        };

        public EffectImplementType[] SupportsImplementTypes => new[] { EffectImplementType.IPicture, EffectImplementType.HwAcceleration };

        public IEffect Build(EffectImplementType implementType, Dictionary<string, object>? parameters = null)
        {
            if (implementType == EffectImplementType.NotSpecified)
            {
                return BuildWithDefaultType(parameters);
            }
            return implementType switch
            {
                EffectImplementType.IPicture => RotationEffect_IPicture.FromParametersDictionary(parameters ?? new Dictionary<string, object> { { "Angle", 0f } }, implementType),
                EffectImplementType.HwAcceleration => RotationEffect_HwAccel.FromParametersDictionary(parameters ?? new Dictionary<string, object> { { "Angle", 0f } }),
                _ => throw new NotSupportedException($"Effect '{TypeName}' does not support implement type '{implementType}'.")
            };
        }

        public IEffect BuildWithDefaultType(Dictionary<string, object>? parameters = null)
        {
            parameters ??= new Dictionary<string, object> { { "Angle", 0f } };
            if (!parameters.ContainsKey("Angle")) parameters["Angle"] = 0f;
            return RotationEffect_IPicture.FromParametersDictionary(parameters);
        }
    }
}
