using projectFrameCut.Drawing.Effect;
using projectFrameCut.Render.HwAccelContracts;
using projectFrameCut.Render.Plugin;
using projectFrameCut.Render.RenderAPIBase.EffectAndMixture;
using projectFrameCut.Shared;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;

namespace projectFrameCut.Render.Effect
{
    public class VignetteEffect_IPicture : INormalEffect
    {
        public bool Enabled { get; set; } = true;
        public int Index { get; set; }
        public string Name { get; set; } = "Vignette";
        public int RelativeWidth { get; set; }
        public int RelativeHeight { get; set; }

        public float Strength { get; init; } = 0.5f;
        public float Radius { get; init; } = 0.65f;

        public Dictionary<string, object> Parameters => new Dictionary<string, object>
        {
            { "Strength", Strength },
            { "Radius", Radius }
        };

        public string? NeedComputer => null;
        public string FromPlugin => InternalPluginBase.InternalPluginBaseID;
        public EffectImplementType ImplementType { get; init; } = EffectImplementType.IPicture;
        public bool IsReorderable => true;

        public static List<string> ParametersNeeded { get; } = ["Strength", "Radius"];
        public static Dictionary<string, string> ParametersType { get; } = new Dictionary<string, string>
        {
            { "Strength", "float" },
            { "Radius", "float" }
        };

        public string TypeName => "Vignette";
        public string? BindedEffectGroupID { get; set; }
        public string Id { get; set; } = string.Empty;

        public static IEffect FromParametersDictionary(Dictionary<string, object> parameters, EffectImplementType implementType = EffectImplementType.IPicture)
        {
            ArgumentNullException.ThrowIfNull(parameters);
            if (!ParametersNeeded.All(parameters.ContainsKey))
            {
                throw new ArgumentException($"Missing parameters: {string.Join(", ", ParametersNeeded.Where(p => !parameters.ContainsKey(p)))}");
            }

            return new VignetteEffect_IPicture
            {
                Strength = Convert.ToSingle(parameters["Strength"]),
                Radius = Convert.ToSingle(parameters["Radius"]),
                ImplementType = implementType
            };
        }

        public IEffect WithParameters(Dictionary<string, object> parameters) => FromParametersDictionary(parameters);

        public IPicture Render(IPicture source, IComputer? computer, int targetWidth, int targetHeight)
        {
            return VignetteEffect.Process(source, Strength, Radius);
        }
    }

    public class VignetteEffect_HwAccel : INormalEffect
    {
        public bool Enabled { get; set; } = true;
        public int Index { get; set; }
        public string Name { get; set; } = "Vignette";
        public int RelativeWidth { get; set; }
        public int RelativeHeight { get; set; }

        public float Strength { get; init; } = 0.5f;
        public float Radius { get; init; } = 0.65f;

        public Dictionary<string, object> Parameters => new Dictionary<string, object>
        {
            { "Strength", Strength },
            { "Radius", Radius }
        };

        public string? NeedComputer => "VignetteComputer";
        public string FromPlugin => InternalPluginBase.InternalPluginBaseID;
        public EffectImplementType ImplementType => EffectImplementType.HwAcceleration;
        public bool IsReorderable => true;

        public static List<string> ParametersNeeded { get; } = ["Strength", "Radius"];
        public static Dictionary<string, string> ParametersType { get; } = new Dictionary<string, string>
        {
            { "Strength", "float" },
            { "Radius", "float" }
        };

        public string TypeName => "Vignette";
        public string? BindedEffectGroupID { get; set; }
        public string Id { get; set; } = string.Empty;

        public static IEffect FromParametersDictionary(Dictionary<string, object> parameters)
        {
            ArgumentNullException.ThrowIfNull(parameters);
            if (!ParametersNeeded.All(parameters.ContainsKey))
            {
                throw new ArgumentException($"Missing parameters: {string.Join(", ", ParametersNeeded.Where(p => !parameters.ContainsKey(p)))}");
            }
            return new VignetteEffect_HwAccel
            {
                Strength = Convert.ToSingle(parameters["Strength"]),
                Radius = Convert.ToSingle(parameters["Radius"])
            };
        }

        public IEffect WithParameters(Dictionary<string, object> parameters) => FromParametersDictionary(parameters);

        public IPicture Render(IPicture source, IComputer? computer, int targetWidth, int targetHeight)
        {
            if (computer is null)
                return VignetteEffect.Process(source, Strength, Radius);

            var sw = Stopwatch.StartNew();
            var (r, g, b, a, sourceHasAlpha) = HwAccelEffectHelper.ExtractFloatChannels(source);

            FourChannelResult computeResult;
            if (computer is IVignetteComputer vc)
            {
                computeResult = vc.ComputeVignette(r, g, b, a, source.Width, source.Height, Strength, Radius);
            }
            else
            {
                var resultArr = computer.Compute([r, g, b, a, source.Width, source.Height, Strength, Radius]);

                if (resultArr.Length != 4 ||
                    resultArr[0] is not float[] rOut ||
                    resultArr[1] is not float[] gOut ||
                    resultArr[2] is not float[] bOut ||
                    resultArr[3] is not float[] aOut)
                {
                    throw new InvalidOperationException("VignetteComputer did not return expected channel buffers.");
                }

                computeResult = new FourChannelResult(rOut, gOut, bOut, aOut);
            }

            var result = HwAccelEffectHelper.BuildPicture(source, source.Width, source.Height,
                computeResult.R, computeResult.G, computeResult.B, computeResult.A, sourceHasAlpha);
            sw.Stop();
            result.ProcessStack = source.ProcessStack.Append(new PictureProcessStack
            {
                Elapsed = sw.Elapsed,
                OperationDisplayName = "Vignette (GPU)",
                Operator = typeof(VignetteEffect_HwAccel),
                ProcessingFuncStackTrace = new StackTrace(true),
                Properties = new Dictionary<string, object> { { "Strength", Strength }, { "Radius", Radius } }
            }).ToList();
            return result;
        }
    }

    public class VignetteEffectFactory : IEffectFactory
    {
        public string FromPlugin => InternalPluginBase.InternalPluginBaseID;
        public string TypeName => "Vignette";
        public EffectTarget Target => EffectTarget.Video;
        public List<string> ParametersNeeded { get; } = ["Strength", "Radius"];
        public Dictionary<string, string> ParametersType { get; } = new Dictionary<string, string>
        {
            { "Strength", "float" },
            { "Radius", "float" }
        };

        public EffectImplementType[] SupportsImplementTypes => [EffectImplementType.IPicture, EffectImplementType.HwAcceleration];

        public IEffect Build(EffectImplementType implementType, Dictionary<string, object>? parameters = null)
        {
            if (implementType == EffectImplementType.NotSpecified)
            {
                return BuildWithDefaultType(parameters);
            }
            return implementType switch
            {
                EffectImplementType.IPicture => VignetteEffect_IPicture.FromParametersDictionary(parameters ?? new Dictionary<string, object>(), implementType),
                EffectImplementType.HwAcceleration => VignetteEffect_HwAccel.FromParametersDictionary(parameters ?? new Dictionary<string, object>()),
                _ => throw new NotSupportedException($"Effect '{TypeName}' does not support implement type '{implementType}'.")
            };
        }

        public IEffect BuildWithDefaultType(Dictionary<string, object>? parameters = null)
        {
            parameters ??= new Dictionary<string, object>
            {
                { "Strength", 0.5f },
                { "Radius", 0.65f }
            };
            if (!parameters.ContainsKey("Strength")) parameters["Strength"] = 0.5f;
            if (!parameters.ContainsKey("Radius")) parameters["Radius"] = 0.65f;
            return VignetteEffect_IPicture.FromParametersDictionary(parameters);
        }
    }
}
