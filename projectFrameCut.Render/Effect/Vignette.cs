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
    public class VignetteEffect_ImageSharp : INormalEffect
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
        public bool YieldProcessStep => true;
        public EffectImplementType ImplementType { get; init; } = EffectImplementType.ImageSharp;

        public static List<string> ParametersNeeded { get; } = ["Strength", "Radius"];
        public static Dictionary<string, string> ParametersType { get; } = new Dictionary<string, string>
        {
            { "Strength", "float" },
            { "Radius", "float" }
        };

        public string TypeName => "Vignette";
        public string? BindedEffectGroupID { get; set; }
        public string Id { get; set; } = string.Empty;

        public static IEffect FromParametersDictionary(Dictionary<string, object> parameters, EffectImplementType implementType = EffectImplementType.ImageSharp)
        {
            ArgumentNullException.ThrowIfNull(parameters);
            if (!ParametersNeeded.All(parameters.ContainsKey))
            {
                throw new ArgumentException($"Missing parameters: {string.Join(", ", ParametersNeeded.Where(p => !parameters.ContainsKey(p)))}");
            }

            return new VignetteEffect_ImageSharp
            {
                Strength = Convert.ToSingle(parameters["Strength"]),
                Radius = Convert.ToSingle(parameters["Radius"]),
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
            return new VignetteProcessStep(Strength, Radius);
        }
    }

    public class VignetteProcessStep : IPictureProcessStep
    {
        private TimeSpan? _elapsed;
        public string Name => "Vignette";
        public Dictionary<string, object?> Properties { get; set; } = new();

        public float Strength { get; }
        public float Radius { get; }

        public VignetteProcessStep(float strength, float radius)
        {
            Strength = strength;
            Radius = radius;
            Properties = new Dictionary<string, object?>
            {
                { nameof(Strength), Strength },
                { nameof(Radius), Radius }
            };
        }

        public IPicture Process(IPicture source)
        {
            var sw = Stopwatch.StartNew();
            var result = EffectHelper.VignettePicture(source, Strength, Radius, "Vignette", typeof(VignetteProcessStep));
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
            OperationDisplayName = "Vignette",
            Operator = typeof(VignetteProcessStep),
            ProcessingFuncStackTrace = new StackTrace(true),
            StepUsed = this,
            Properties = new Dictionary<string, object>
            {
                { nameof(Strength), Strength },
                { nameof(Radius), Radius }
            }
        };
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

        public EffectImplementType[] SupportsImplementTypes => [EffectImplementType.ImageSharp, EffectImplementType.IPicture];

        public IEffect Build(EffectImplementType implementType, Dictionary<string, object>? parameters = null)
        {
            if (implementType == EffectImplementType.NotSpecified)
            {
                return BuildWithDefaultType(parameters);
            }
            return implementType switch
            {
                EffectImplementType.ImageSharp => VignetteEffect_ImageSharp.FromParametersDictionary(parameters ?? new Dictionary<string, object>(), implementType),
                EffectImplementType.IPicture => VignetteEffect_ImageSharp.FromParametersDictionary(parameters ?? new Dictionary<string, object>(), implementType),
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
            return VignetteEffect_ImageSharp.FromParametersDictionary(parameters);
        }
    }
}
