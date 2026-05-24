using projectFrameCut.Render.Plugin;
using projectFrameCut.Render.RenderAPIBase.EffectAndMixture;
using projectFrameCut.Shared;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.Processing;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;

namespace projectFrameCut.Render.Effect
{
    public class BlurEffect_ImageSharp : INormalEffect
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
        public bool YieldProcessStep => true;
        public EffectImplementType ImplementType { get; init; } = EffectImplementType.ImageSharp;

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

        public static IEffect FromParametersDictionary(Dictionary<string, object> parameters, EffectImplementType implementType = EffectImplementType.ImageSharp)
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
            return new BlurEffect_ImageSharp { Sigma = sigma, ImplementType = implementType };
        }

        public IEffect WithParameters(Dictionary<string, object> parameters) => FromParametersDictionary(parameters);

        public IPicture Render(IPicture source, IComputer? computer, int targetWidth, int targetHeight)
        {
            throw new NotImplementedException();
        }

        public IPictureProcessStep GetStep(IPicture source, int targetWidth, int targetHeight)
        {
            return new BlurProcessStep(Sigma);
        }
    }

    public class BlurProcessStep : IPictureProcessStep
    {
        private TimeSpan? _elapsed;
        public string Name => "Blur";
        public Dictionary<string, object?> Properties { get; set; } = new();

        public float Sigma { get; }

        public BlurProcessStep(float sigma)
        {
            Sigma = sigma;
            Properties = new Dictionary<string, object?> { { nameof(Sigma), Sigma } };
        }

        public IPicture Process(IPicture source)
        {
            var sw = Stopwatch.StartNew();
            var result = EffectHelper.BlurPicture(source, Sigma, "Blur", typeof(BlurProcessStep));
            sw.Stop();
            _elapsed = sw.Elapsed;
            result.ProcessStack = source.ProcessStack.Append(GetProcessStack()).ToList();
            return result;
        }

        public Func<IImageProcessingContext, IImageProcessingContext>? GetSixLaborsImageSharpProcess()
        {
            return ctx => ctx.GaussianBlur(Sigma);
        }

        public PictureProcessStack GetProcessStack() => new PictureProcessStack
        {
            Elapsed = _elapsed,
            OperationDisplayName = "Blur",
            Operator = typeof(BlurProcessStep),
            ProcessingFuncStackTrace = new StackTrace(true),
            StepUsed = this,
            Properties = new Dictionary<string, object>
            {
                { nameof(Sigma), Sigma }
            }
        };
    }

    public class BlurEffectFactory : IEffectFactory
    {
        public string FromPlugin => InternalPluginBase.InternalPluginBaseID;
        public string TypeName => "Blur";
        public EffectTarget Target => EffectTarget.Video;
        public List<string> ParametersNeeded { get; } = new List<string> { "Sigma" };
        public Dictionary<string, string> ParametersType { get; } = new Dictionary<string, string> { { "Sigma", "float" } };

        public EffectImplementType[] SupportsImplementTypes => new[] { EffectImplementType.ImageSharp, EffectImplementType.IPicture };

        public IEffect Build(EffectImplementType implementType, Dictionary<string, object>? parameters = null)
        {
            if (implementType == EffectImplementType.NotSpecified)
            {
                return BuildWithDefaultType(parameters);
            }
            return implementType switch
            {
                EffectImplementType.ImageSharp => BlurEffect_ImageSharp.FromParametersDictionary(parameters ?? new Dictionary<string, object>(), implementType),
                EffectImplementType.IPicture => BlurEffect_ImageSharp.FromParametersDictionary(parameters ?? new Dictionary<string, object>(), implementType),
                _ => throw new NotSupportedException($"Effect '{TypeName}' does not support implement type '{implementType}'.")
            };
        }

        public IEffect BuildWithDefaultType(Dictionary<string, object>? parameters = null)
        {
            parameters ??= new Dictionary<string, object> { { "Sigma", 0f } };
            if (!parameters.ContainsKey("Sigma")) parameters["Sigma"] = 0f;
            return BlurEffect_ImageSharp.FromParametersDictionary(parameters);
        }
    }
}
