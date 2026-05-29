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
    public class FadeOpacityEffect_ImageSharp : INormalEffect
    {
        public bool Enabled { get; set; } = true;
        public int Index { get; set; }
        public string Name { get; set; } = "FadeOpacity";
        public int RelativeWidth { get; set; }
        public int RelativeHeight { get; set; }

        public float Opacity { get; init; } = 0.8f;

        public Dictionary<string, object> Parameters => new Dictionary<string, object>
        {
            { "Opacity", Opacity }
        };

        public string? NeedComputer => null;
        public string FromPlugin => InternalPluginBase.InternalPluginBaseID;
        public bool YieldProcessStep => true;
        public EffectImplementType ImplementType { get; init; } = EffectImplementType.ImageSharp;

        public static List<string> ParametersNeeded { get; } = ["Opacity"];
        public static Dictionary<string, string> ParametersType { get; } = new Dictionary<string, string>
        {
            { "Opacity", "float" }
        };

        public string TypeName => "FadeOpacity";
        public string? BindedEffectGroupID { get; set; }
        public string Id { get; set; } = string.Empty;

        public static IEffect FromParametersDictionary(Dictionary<string, object> parameters, EffectImplementType implementType = EffectImplementType.ImageSharp)
        {
            ArgumentNullException.ThrowIfNull(parameters);
            if (!ParametersNeeded.All(parameters.ContainsKey))
            {
                throw new ArgumentException($"Missing parameters: {string.Join(", ", ParametersNeeded.Where(p => !parameters.ContainsKey(p)))}");
            }

            return new FadeOpacityEffect_ImageSharp
            {
                Opacity = Convert.ToSingle(parameters["Opacity"]),
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
            return new FadeOpacityProcessStep(Opacity);
        }
    }

    public class FadeOpacityProcessStep : IPictureProcessStep
    {
        private TimeSpan? _elapsed;
        public string Name => "FadeOpacity";
        public Dictionary<string, object?> Properties { get; set; } = new();

        public float Opacity { get; }

        public FadeOpacityProcessStep(float opacity)
        {
            Opacity = opacity;
            Properties = new Dictionary<string, object?>
            {
                { nameof(Opacity), Opacity }
            };
        }

        public IPicture Process(IPicture source)
        {
            var sw = Stopwatch.StartNew();
            var result = EffectHelper.ApplyOpacityPicture(source, Opacity, "FadeOpacity", typeof(FadeOpacityProcessStep));
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
            OperationDisplayName = "FadeOpacity",
            Operator = typeof(FadeOpacityProcessStep),
            ProcessingFuncStackTrace = new StackTrace(true),
            
            Properties = new Dictionary<string, object>
            {
                { nameof(Opacity), Opacity }
            }
        };
    }

    public class FadeOpacityEffectFactory : IEffectFactory
    {
        public string FromPlugin => InternalPluginBase.InternalPluginBaseID;
        public string TypeName => "FadeOpacity";
        public EffectTarget Target => EffectTarget.Video;
        public List<string> ParametersNeeded { get; } = ["Opacity"];
        public Dictionary<string, string> ParametersType { get; } = new Dictionary<string, string>
        {
            { "Opacity", "float" }
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
                EffectImplementType.ImageSharp => FadeOpacityEffect_ImageSharp.FromParametersDictionary(parameters ?? new Dictionary<string, object>(), implementType),
                EffectImplementType.IPicture => FadeOpacityEffect_ImageSharp.FromParametersDictionary(parameters ?? new Dictionary<string, object>(), implementType),
                _ => throw new NotSupportedException($"Effect '{TypeName}' does not support implement type '{implementType}'.")
            };
        }

        public IEffect BuildWithDefaultType(Dictionary<string, object>? parameters = null)
        {
            parameters ??= new Dictionary<string, object>
            {
                { "Opacity", 0.8f }
            };
            if (!parameters.ContainsKey("Opacity")) parameters["Opacity"] = 0.8f;
            return FadeOpacityEffect_ImageSharp.FromParametersDictionary(parameters);
        }
    }
}
