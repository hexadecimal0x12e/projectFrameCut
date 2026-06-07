using projectFrameCut.Drawing.Effect;
using projectFrameCut.Render.Plugin;
using projectFrameCut.Render.RenderAPIBase.EffectAndMixture;
using projectFrameCut.Shared;
using System;
using System.Collections.Generic;
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

        public EffectImplementType[] SupportsImplementTypes => [EffectImplementType.IPicture, EffectImplementType.IPicture];

        public IEffect Build(EffectImplementType implementType, Dictionary<string, object>? parameters = null)
        {
            if (implementType == EffectImplementType.NotSpecified)
            {
                return BuildWithDefaultType(parameters);
            }
            return implementType switch
            {
                EffectImplementType.IPicture => VignetteEffect_IPicture.FromParametersDictionary(parameters ?? new Dictionary<string, object>(), implementType),
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
