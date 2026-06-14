using projectFrameCut.Drawing.Effect;
using projectFrameCut.Render.Plugin;
using projectFrameCut.Render.RenderAPIBase.EffectAndMixture;
using projectFrameCut.Shared;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;

namespace projectFrameCut.Render.Effect
{
    public class FlipEffect_IPicture : INormalEffect
    {
        public bool Enabled { get; set; } = true;
        public int Index { get; set; }
        public string Name { get; set; } = "Flip";
        public int RelativeWidth { get; set; }
        public int RelativeHeight { get; set; }

        public bool Horizontal { get; init; }
        public bool Vertical { get; init; }

        public Dictionary<string, object> Parameters => new Dictionary<string, object>
        {
            { "Horizontal", Horizontal },
            { "Vertical", Vertical }
        };

        public string? NeedComputer => null;
        public string FromPlugin => InternalPluginBase.InternalPluginBaseID;
        public EffectImplementType ImplementType { get; init; } = EffectImplementType.IPicture;
        public bool IsReorderable => true;

        public static List<string> ParametersNeeded { get; } = ["Horizontal", "Vertical"];

        public static Dictionary<string, string> ParametersType { get; } = new Dictionary<string, string>
        {
            { "Horizontal", "bool" },
            { "Vertical", "bool" }
        };

        public string TypeName => "Flip";
        public string? BindedEffectGroupID { get; set; }
        public string Id { get; set; } = string.Empty;

        public static IEffect FromParametersDictionary(Dictionary<string, object> parameters, EffectImplementType implementType = EffectImplementType.IPicture)
        {
            ArgumentNullException.ThrowIfNull(parameters);
            if (!ParametersNeeded.All(parameters.ContainsKey))
            {
                throw new ArgumentException($"Missing parameters: {string.Join(", ", ParametersNeeded.Where(p => !parameters.ContainsKey(p)))}");
            }

            return new FlipEffect_IPicture
            {
                Horizontal = Convert.ToBoolean(parameters["Horizontal"]),
                Vertical = Convert.ToBoolean(parameters["Vertical"]),
                ImplementType = implementType
            };
        }

        public IEffect WithParameters(Dictionary<string, object> parameters) => FromParametersDictionary(parameters);

        public IPicture Render(IPicture source, IComputer? computer, int targetWidth, int targetHeight)
        {
            return FlipEffect.Process(source, Horizontal, Vertical);
        }
    }

    public class FlipEffect_HwAccel : INormalEffect
    {
        public bool Enabled { get; set; } = true;
        public int Index { get; set; }
        public string Name { get; set; } = "Flip";
        public int RelativeWidth { get; set; }
        public int RelativeHeight { get; set; }

        public bool Horizontal { get; init; }
        public bool Vertical { get; init; }

        public Dictionary<string, object> Parameters => new Dictionary<string, object>
        {
            { "Horizontal", Horizontal },
            { "Vertical", Vertical }
        };

        public string? NeedComputer => "FlipComputer";
        public string FromPlugin => InternalPluginBase.InternalPluginBaseID;
        public EffectImplementType ImplementType => EffectImplementType.HwAcceleration;
        public bool IsReorderable => true;

        public static List<string> ParametersNeeded { get; } = ["Horizontal", "Vertical"];
        public static Dictionary<string, string> ParametersType { get; } = new Dictionary<string, string>
        {
            { "Horizontal", "bool" },
            { "Vertical", "bool" }
        };

        public string TypeName => "Flip";
        public string? BindedEffectGroupID { get; set; }
        public string Id { get; set; } = string.Empty;

        public static IEffect FromParametersDictionary(Dictionary<string, object> parameters)
        {
            ArgumentNullException.ThrowIfNull(parameters);
            if (!ParametersNeeded.All(parameters.ContainsKey))
            {
                throw new ArgumentException($"Missing parameters: {string.Join(", ", ParametersNeeded.Where(p => !parameters.ContainsKey(p)))}");
            }
            return new FlipEffect_HwAccel
            {
                Horizontal = Convert.ToBoolean(parameters["Horizontal"]),
                Vertical = Convert.ToBoolean(parameters["Vertical"])
            };
        }

        public IEffect WithParameters(Dictionary<string, object> parameters) => FromParametersDictionary(parameters);

        public IPicture Render(IPicture source, IComputer? computer, int targetWidth, int targetHeight)
        {
            if (computer is null)
                return FlipEffect.Process(source, Horizontal, Vertical);

            var sw = Stopwatch.StartNew();
            var (r, g, b, a, sourceHasAlpha) = HwAccelEffectHelper.ExtractFloatChannels(source);
            var resultArr = computer.Compute([r, g, b, a, source.Width, source.Height, Horizontal, Vertical]);

            if (resultArr.Length != 4 ||
                resultArr[0] is not float[] rOut ||
                resultArr[1] is not float[] gOut ||
                resultArr[2] is not float[] bOut ||
                resultArr[3] is not float[] aOut)
            {
                throw new InvalidOperationException("FlipComputer did not return expected channel buffers.");
            }

            var result = HwAccelEffectHelper.BuildPicture(source, source.Width, source.Height, rOut, gOut, bOut, aOut, sourceHasAlpha);
            sw.Stop();
            result.ProcessStack = source.ProcessStack.Append(new PictureProcessStack
            {
                Elapsed = sw.Elapsed,
                OperationDisplayName = "Flip (GPU)",
                Operator = typeof(FlipEffect_HwAccel),
                ProcessingFuncStackTrace = new StackTrace(true),
                Properties = new Dictionary<string, object> { { "Horizontal", Horizontal }, { "Vertical", Vertical } }
            }).ToList();
            return result;
        }
    }

    public class FlipEffectFactory : IEffectFactory
    {
        public string FromPlugin => InternalPluginBase.InternalPluginBaseID;
        public string TypeName => "Flip";
        public EffectTarget Target => EffectTarget.Video;
        public List<string> ParametersNeeded { get; } = ["Horizontal", "Vertical"];
        public Dictionary<string, string> ParametersType { get; } = new Dictionary<string, string>
        {
            { "Horizontal", "bool" },
            { "Vertical", "bool" }
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
                EffectImplementType.IPicture => FlipEffect_IPicture.FromParametersDictionary(parameters ?? new Dictionary<string, object>(), implementType),
                EffectImplementType.HwAcceleration => FlipEffect_HwAccel.FromParametersDictionary(parameters ?? new Dictionary<string, object>()),
                _ => throw new NotSupportedException($"Effect '{TypeName}' does not support implement type '{implementType}'.")
            };
        }

        public IEffect BuildWithDefaultType(Dictionary<string, object>? parameters = null)
        {
            parameters ??= new Dictionary<string, object>
            {
                { "Horizontal", false },
                { "Vertical", false }
            };
            if (!parameters.ContainsKey("Horizontal")) parameters["Horizontal"] = false;
            if (!parameters.ContainsKey("Vertical")) parameters["Vertical"] = false;
            return FlipEffect_IPicture.FromParametersDictionary(parameters);
        }
    }
}
