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
    public class FlipEffect_ImageSharp : INormalEffect
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
        public bool YieldProcessStep => true;
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

            return new FlipEffect_ImageSharp
            {
                Horizontal = Convert.ToBoolean(parameters["Horizontal"]),
                Vertical = Convert.ToBoolean(parameters["Vertical"]),
                ImplementType = implementType
            };
        }

        public IEffect WithParameters(Dictionary<string, object> parameters) => FromParametersDictionary(parameters);

        public IPicture Render(IPicture source, IComputer? computer, int targetWidth, int targetHeight)
        {
            return GetStep(source, targetWidth, targetHeight).Process(source);
        }

        public IPictureProcessStep GetStep(IPicture source, int targetWidth, int targetHeight)
        {
            return new FlipProcessStep(Horizontal, Vertical);
        }
    }

    public class FlipProcessStep : IPictureProcessStep
    {
        private TimeSpan? _elapsed;
        public string Name => "Flip";
        public Dictionary<string, object?> Properties { get; set; } = new();

        public bool Horizontal { get; }
        public bool Vertical { get; }

        public FlipProcessStep(bool horizontal, bool vertical)
        {
            Horizontal = horizontal;
            Vertical = vertical;
            Properties = new Dictionary<string, object?>
            {
                { nameof(Horizontal), Horizontal },
                { nameof(Vertical), Vertical }
            };
        }

        public IPicture Process(IPicture source)
        {
            var sw = Stopwatch.StartNew();
            var result = EffectHelper.FlipPicture(source, Horizontal, Vertical, "Flip", typeof(FlipProcessStep));
            sw.Stop();
            _elapsed = sw.Elapsed;
            result.ProcessStack = source.ProcessStack.Append(GetProcessStack()).ToList();
            return result;
        }

        public Func<IImageProcessingContext, IImageProcessingContext>? GetSixLaborsImageSharpProcess()
        {
            return null;
        }

        public PictureProcessStack GetProcessStack() => new PictureProcessStack
        {
            Elapsed = _elapsed,
            OperationDisplayName = "Flip",
            Operator = typeof(FlipProcessStep),
            ProcessingFuncStackTrace = new StackTrace(true),
            StepUsed = this,
            Properties = new Dictionary<string, object>
            {
                { nameof(Horizontal), Horizontal },
                { nameof(Vertical), Vertical }
            }
        };
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

        public EffectImplementType[] SupportsImplementTypes => [EffectImplementType.ImageSharp, EffectImplementType.IPicture];

        public IEffect Build(EffectImplementType implementType, Dictionary<string, object>? parameters = null)
        {
            if (implementType == EffectImplementType.NotSpecified)
            {
                return BuildWithDefaultType(parameters);
            }
            return implementType switch
            {
                EffectImplementType.ImageSharp => FlipEffect_ImageSharp.FromParametersDictionary(parameters ?? new Dictionary<string, object>(), implementType),
                EffectImplementType.IPicture => FlipEffect_ImageSharp.FromParametersDictionary(parameters ?? new Dictionary<string, object>(), implementType),
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
            return FlipEffect_ImageSharp.FromParametersDictionary(parameters);
        }
    }
}
