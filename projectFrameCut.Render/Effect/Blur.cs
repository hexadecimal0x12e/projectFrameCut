using projectFrameCut.Drawing.Effect;
using projectFrameCut.Render.HwAccelContracts;
using projectFrameCut.Render.Plugin;
using projectFrameCut.Render.RenderAPIBase.EffectAndMixture;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;

namespace projectFrameCut.Render.Effect
{
    public class BlurEffect_IPicture : INormalEffect
    {
        public bool Enabled { get; set; } = true;
        public int Index { get; set; }
        public string Name { get; set; } = "Blur";
        public int RelativeWidth { get; set; }
        public int RelativeHeight { get; set; }

        public float Sigma { get; init; }

        public Dictionary<string, object> Parameters => new Dictionary<string, object>
        {
            {"Sigma", Sigma}
        };

        public string? NeedComputer => null;
        public string FromPlugin => projectFrameCut.Render.Plugin.InternalPluginBase.InternalPluginBaseID;
        public EffectImplementType ImplementType { get; init; } = EffectImplementType.IPicture;
        public bool IsReorderable => true;

        public static List<string> ParametersNeeded { get; } = new List<string>
        {
            "Sigma"
        };
        public static Dictionary<string, string> ParametersType { get; } = new Dictionary<string, string>
        {
            {"Sigma", "float" }
        };

        public string TypeName => "Blur";
        public string? BindedEffectGroupID { get; set; }
        public string Id { get; set; } = string.Empty;

        public static IEffect FromParametersDictionary(Dictionary<string, object> parameters, EffectImplementType implementType = EffectImplementType.IPicture)
        {
            ArgumentNullException.ThrowIfNull(parameters);
            if (!ParametersNeeded.All(parameters.ContainsKey))
            {
                throw new ArgumentException($"Missing parameters: {string.Join(", ", ParametersNeeded.Where(p => !parameters.ContainsKey(p)))}");
            }
            float sigma = 0f;
            if (parameters.TryGetValue("Sigma", out var val))
            {
                sigma = Convert.ToSingle(val);
            }
            return new BlurEffect_IPicture { Sigma = sigma, ImplementType = implementType };
        }

        public IEffect WithParameters(Dictionary<string, object> parameters) => FromParametersDictionary(parameters);

        public IPicture Render(IPicture source, IComputer? computer, int targetWidth, int targetHeight)
        {
            return BlurEffect.Process(source, Sigma);
        }
    }

    public class BlurEffect_HwAccel : INormalEffect
    {
        public bool Enabled { get; set; } = true;
        public int Index { get; set; }
        public string Name { get; set; } = "Blur";
        public int RelativeWidth { get; set; }
        public int RelativeHeight { get; set; }

        public float Sigma { get; init; }

        public Dictionary<string, object> Parameters => new Dictionary<string, object>
        {
            { "Sigma", Sigma }
        };

        public string? NeedComputer => "BlurComputer";
        public string FromPlugin => InternalPluginBase.InternalPluginBaseID;
        public EffectImplementType ImplementType => EffectImplementType.HwAcceleration;
        public bool IsReorderable => true;

        public static List<string> ParametersNeeded { get; } = new List<string> { "Sigma" };
        public static Dictionary<string, string> ParametersType { get; } = new Dictionary<string, string>
        {
            { "Sigma", "float" }
        };

        public string TypeName => "Blur";
        public string? BindedEffectGroupID { get; set; }
        public string Id { get; set; } = string.Empty;

        public static IEffect FromParametersDictionary(Dictionary<string, object> parameters)
        {
            ArgumentNullException.ThrowIfNull(parameters);
            if (!ParametersNeeded.All(parameters.ContainsKey))
            {
                throw new ArgumentException($"Missing parameters: {string.Join(", ", ParametersNeeded.Where(p => !parameters.ContainsKey(p)))}");
            }
            float sigma = 0f;
            if (parameters.TryGetValue("Sigma", out var val))
            {
                sigma = Convert.ToSingle(val);
            }
            return new BlurEffect_HwAccel { Sigma = sigma };
        }

        public IEffect WithParameters(Dictionary<string, object> parameters) => FromParametersDictionary(parameters);

        public IPicture Render(IPicture source, IComputer? computer, int targetWidth, int targetHeight)
        {
            if (Sigma <= float.Epsilon)
                return source;

            if (computer is null)
                return BlurEffect.Process(source, Sigma);

            var sw = Stopwatch.StartNew();
            var (r, g, b, a, sourceHasAlpha) = HwAccelEffectHelper.ExtractFloatChannels(source);

            FourChannelResult computeResult;
            if (computer is IBlurComputer bc)
            {
                computeResult = bc.ComputeBlur(r, g, b, a, source.Width, Sigma);
            }
            else
            {
                var resultArr = computer.Compute([r, g, b, a, source.Width, Sigma]);

                if (resultArr.Length != 4 ||
                    resultArr[0] is not float[] rOut ||
                    resultArr[1] is not float[] gOut ||
                    resultArr[2] is not float[] bOut ||
                    resultArr[3] is not float[] aOut)
                {
                    throw new InvalidOperationException("BlurComputer did not return expected channel buffers.");
                }

                computeResult = new FourChannelResult(rOut, gOut, bOut, aOut);
            }

            var result = HwAccelEffectHelper.BuildPicture(source, source.Width, source.Height,
                computeResult.R, computeResult.G, computeResult.B, computeResult.A, sourceHasAlpha);
            sw.Stop();
            result.ProcessStack = source.ProcessStack.Append(new PictureProcessStack
            {
                Elapsed = sw.Elapsed,
                OperationDisplayName = "Blur (GPU)",
                Operator = typeof(BlurEffect_HwAccel),
                ProcessingFuncStackTrace = new StackTrace(true),
                Properties = new Dictionary<string, object> { { "Sigma", Sigma } }
            }).ToList();
            return result;
        }
    }

    public class BlurEffectFactory : IEffectFactory
    {
        public string FromPlugin => InternalPluginBase.InternalPluginBaseID;
        public string TypeName => "Blur";
        public EffectTarget Target => EffectTarget.Video;
        public List<string> ParametersNeeded { get; } = new List<string> { "Sigma" };
        public Dictionary<string, string> ParametersType { get; } = new Dictionary<string, string> { { "Sigma", "float" } };

        public EffectImplementType[] SupportsImplementTypes => new[] { EffectImplementType.IPicture, EffectImplementType.HwAcceleration };

        public IEffect Build(EffectImplementType implementType, Dictionary<string, object>? parameters = null)
        {
            if (implementType == EffectImplementType.NotSpecified)
            {
                return BuildWithDefaultType(parameters);
            }
            return implementType switch
            {
                EffectImplementType.IPicture => BlurEffect_IPicture.FromParametersDictionary(parameters ?? new Dictionary<string, object>(), implementType),
                EffectImplementType.HwAcceleration => BlurEffect_HwAccel.FromParametersDictionary(parameters ?? new Dictionary<string, object>()),
                _ => throw new NotSupportedException($"Effect '{TypeName}' does not support implement type '{implementType}'.")
            };
        }

        public IEffect BuildWithDefaultType(Dictionary<string, object>? parameters = null)
        {
            parameters ??= new Dictionary<string, object> { { "Sigma", 0f } };
            if (!parameters.ContainsKey("Sigma")) parameters["Sigma"] = 0f;
            return BlurEffect_IPicture.FromParametersDictionary(parameters);
        }
    }
}
