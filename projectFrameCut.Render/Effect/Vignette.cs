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
    public class VignetteEffect_IPicture : INormalEffect
    {
        public bool Enabled { get; set; } = true;
        public int Index { get; set; }
        public string Name { get; set; } = "Vignette";
        public int RelativeWidth { get; set; }
        public int RelativeHeight { get; set; }

        public float Strength { get; init; } = 0.5f;
        public float Radius { get; init; } = 0.65f;
        public Dictionary<string, object> Parameters { get; set; } = new();

        public string FromPlugin => InternalPluginBase.InternalPluginBaseID;
        public EffectImplementType ImplementType => EffectImplementType.IPicture;
        public bool IsReorderable => true;
        bool IEffect.CanProcessFromCanvas => true;

        public static List<string> ParametersNeeded { get; } = ["Strength", "Radius"];
        public static Dictionary<string, string> ParametersType { get; } = new Dictionary<string, string>
        {
            { "Strength", "float" },
            { "Radius", "float" }
        };

        public string TypeName => "Vignette";
        public string? BindedEffectProvidingSystemID { get; set; }
        public string Id { get; set; } = string.Empty;

        public static IEffect FromParametersDictionary(Dictionary<string, object> parameters)
        {
            ArgumentNullException.ThrowIfNull(parameters);
            if (!ParametersNeeded.All(parameters.ContainsKey))
            {
                throw new ArgumentException($"Missing parameters: {string.Join(", ", ParametersNeeded.Where(p => !parameters.ContainsKey(p)))}");
            }

            var effect = new VignetteEffect_IPicture
            {
                Strength = DynamicParam.ToFloat(parameters.GetValueOrDefault("Strength")),
                Radius = DynamicParam.ToFloat(parameters.GetValueOrDefault("Radius")),
            };
            effect.Parameters = parameters;
            return effect;
        }

        public IEffect WithParameters(Dictionary<string, object> parameters) => FromParametersDictionary(parameters);

        public IPicture Render(IPicture source, int targetWidth, int targetHeight)
        {
            float strength = DynamicParam.Resolve(Parameters.GetValueOrDefault("Strength"), Strength);
            float radius = DynamicParam.Resolve(Parameters.GetValueOrDefault("Radius"), Radius);
            return PictureEffectChannels.PreserveHdr(VignetteEffect.Process(source, strength, radius), source);
        }
    }

    /// <summary>
    /// The Render-side provider of the Vignette effect.
    /// </summary>
    public class VignetteEffectProvider : EffectProviderBase
    {
        public VignetteEffectProvider()
        {
            Name = "Vignette";
            SetField("Strength", 0.5f);
            SetField("Radius", 0.65f);
        }

        public override string TypeName => "Vignette";

        public override EffectType TypeOfEffect => EffectType.NormalEffect;

        public override EffectTarget Target => EffectTarget.Video;

        public override string FromPlugin => InternalPluginBase.InternalPluginBaseID;

        protected override IReadOnlyList<EffectArgumentFieldDescriptor> DefineFields()
        {
            return
            [
                Field("Strength", EffectArgumentFieldType.Numeric, "0.5", min: "0", max: "1"),
                Field("Radius", EffectArgumentFieldType.Numeric, "0.65", min: "0", max: "1")
            ];
        }

        protected override EffectImplementType[] SupportedImplementTypes() => [EffectImplementType.IPicture, EffectImplementType.HwAcceleration];


    }
}
