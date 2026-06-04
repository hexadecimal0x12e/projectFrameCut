using projectFrameCut.Render.Plugin;
using projectFrameCut.Render.RenderAPIBase.EffectAndMixture;
using projectFrameCut.Shared;
using SixLabors.ImageSharp.Processing;
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
        public bool YieldProcessStep => false;
        public EffectImplementType ImplementType { get; init; } = EffectImplementType.ImageSharp;

        public static List<string> ParametersNeeded { get; } = ["Horizontal", "Vertical"];

        public static Dictionary<string, string> ParametersType { get; } = new Dictionary<string, string>
        {
            { "Horizontal", "bool" },
            { "Vertical", "bool" }
        };

        public string TypeName => "Flip";
        public string? BindedEffectGroupID { get; set; }
        public string Id { get; set; } = string.Empty;

        public static IEffect FromParametersDictionary(Dictionary<string, object> parameters, EffectImplementType implementType = EffectImplementType.ImageSharp)
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
            var sw = Stopwatch.StartNew();
            var result = EffectHelper.FlipPicture(source, Horizontal, Vertical, "Flip", typeof(FlipEffect_IPicture));
            sw.Stop();
            result.ProcessStack = source.ProcessStack.Append(new PictureProcessStack
            {
                Elapsed = sw.Elapsed,
                OperationDisplayName = "Flip",
                Operator = typeof(FlipEffect_IPicture),
                ProcessingFuncStackTrace = new StackTrace(true),
                Properties = Parameters
            }).ToList();
            return result;
        }

        public IPictureProcessStep GetStep(IPicture source, int targetWidth, int targetHeight)
        {
            throw new NotImplementedException();
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

        public EffectImplementType[] SupportsImplementTypes => [EffectImplementType.IPicture];

        public IEffect Build(EffectImplementType implementType, Dictionary<string, object>? parameters = null)
        {
            if (implementType == EffectImplementType.NotSpecified)
            {
                return BuildWithDefaultType(parameters);
            }
            return implementType switch
            {
                EffectImplementType.IPicture => FlipEffect_IPicture.FromParametersDictionary(parameters ?? new Dictionary<string, object>(), implementType),
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
