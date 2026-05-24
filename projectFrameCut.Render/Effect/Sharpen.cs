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
    public class SharpenEffect_ImageSharp : INormalEffect
    {
        public bool Enabled { get; set; } = true;
        public int Index { get; set; }
        public string Name { get; set; } = "Sharpen";
        public int RelativeWidth { get; set; }
        public int RelativeHeight { get; set; }

        public float Amount { get; init; } = 1f;

        public Dictionary<string, object> Parameters => new Dictionary<string, object>
        {
            { "Amount", Amount }
        };

        public string? NeedComputer => null;
        public string FromPlugin => InternalPluginBase.InternalPluginBaseID;
        public bool YieldProcessStep => true;
        public EffectImplementType ImplementType { get; init; } = EffectImplementType.ImageSharp;

        public static List<string> ParametersNeeded { get; } = ["Amount"];
        public static Dictionary<string, string> ParametersType { get; } = new Dictionary<string, string>
        {
            { "Amount", "float" }
        };

        public string TypeName => "Sharpen";
        public string? BindedEffectGroupID { get; set; }
        public string Id { get; set; } = string.Empty;

        public static IEffect FromParametersDictionary(Dictionary<string, object> parameters, EffectImplementType implementType = EffectImplementType.ImageSharp)
        {
            ArgumentNullException.ThrowIfNull(parameters);
            if (!ParametersNeeded.All(parameters.ContainsKey))
            {
                throw new ArgumentException($"Missing parameters: {string.Join(", ", ParametersNeeded.Where(p => !parameters.ContainsKey(p)))}");
            }

            return new SharpenEffect_ImageSharp
            {
                Amount = Convert.ToSingle(parameters["Amount"]),
                ImplementType = implementType
            };
        }

        public IEffect WithParameters(Dictionary<string, object> parameters) => FromParametersDictionary(parameters);

        public IPicture Render(IPicture source, IComputer? computer, int targetWidth, int targetHeight)
        {
            throw new NotImplementedException();
        }

        public IPictureProcessStep GetStep(IPicture source, int targetWidth, int targetHeight)
        {
            return new SharpenProcessStep(Amount);
        }
    }

    public class SharpenProcessStep : IPictureProcessStep
    {
        private TimeSpan? _elapsed;
        public string Name => "Sharpen";
        public Dictionary<string, object?> Properties { get; set; } = new();

        public float Amount { get; }

        public SharpenProcessStep(float amount)
        {
            Amount = amount;
            Properties = new Dictionary<string, object?>
            {
                { nameof(Amount), Amount }
            };
        }

        public IPicture Process(IPicture source)
        {
            var sw = Stopwatch.StartNew();
            var result = EffectHelper.SharpenPicture(source, Amount, "Sharpen", typeof(SharpenProcessStep));
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
            OperationDisplayName = "Sharpen",
            Operator = typeof(SharpenProcessStep),
            ProcessingFuncStackTrace = new StackTrace(true),
            StepUsed = this,
            Properties = new Dictionary<string, object>
            {
                { nameof(Amount), Amount }
            }
        };
    }

    public class SharpenEffectFactory : IEffectFactory
    {
        public string FromPlugin => InternalPluginBase.InternalPluginBaseID;
        public string TypeName => "Sharpen";
        public EffectTarget Target => EffectTarget.Video;
        public List<string> ParametersNeeded { get; } = ["Amount"];
        public Dictionary<string, string> ParametersType { get; } = new Dictionary<string, string>
        {
            { "Amount", "float" }
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
                EffectImplementType.ImageSharp => SharpenEffect_ImageSharp.FromParametersDictionary(parameters ?? new Dictionary<string, object>(), implementType),
                EffectImplementType.IPicture => SharpenEffect_ImageSharp.FromParametersDictionary(parameters ?? new Dictionary<string, object>(), implementType),
                _ => throw new NotSupportedException($"Effect '{TypeName}' does not support implement type '{implementType}'.")
            };
        }

        public IEffect BuildWithDefaultType(Dictionary<string, object>? parameters = null)
        {
            parameters ??= new Dictionary<string, object> { { "Amount", 1f } };
            if (!parameters.ContainsKey("Amount")) parameters["Amount"] = 1f;
            return SharpenEffect_ImageSharp.FromParametersDictionary(parameters);
        }
    }
}
