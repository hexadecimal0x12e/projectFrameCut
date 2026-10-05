using projectFrameCut.Drawing.Effect;
using projectFrameCut.Render.Plugin;
using projectFrameCut.Render.RenderAPIBase.EffectAndMixture;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;

namespace projectFrameCut.Render.Effect
{
    public class BlurEffect_IPicture : INormalEffect
    {
        public bool Enabled { get; set; } = true;
        public int Index { get; set; }
        public string Name { get; set; } = "Blur";
        public int RelativeWidth { get; set; }
        public int RelativeHeight { get; set; }

        public float Sigma { get; init; }
        public Dictionary<string, object> Parameters { get; set; } = new();

        public string FromPlugin => projectFrameCut.Render.Plugin.InternalPluginBase.InternalPluginBaseID;
        public EffectImplementType ImplementType => EffectImplementType.IPicture;
        public bool IsReorderable => true;
        bool IEffect.CanProcessFromCanvas => true;

        public static List<string> ParametersNeeded { get; } = new List<string>
        {
            "Sigma"
        };
        public static Dictionary<string, string> ParametersType { get; } = new Dictionary<string, string>
        {
            {"Sigma", "float" }
        };

        public string TypeName => "Blur";
        public string? BindedEffectProvidingSystemID { get; set; }
        public string Id { get; set; } = string.Empty;

        public static IEffect FromParametersDictionary(Dictionary<string, object> parameters)
        {
            ArgumentNullException.ThrowIfNull(parameters);
            if (!ParametersNeeded.All(parameters.ContainsKey))
            {
                throw new ArgumentException($"Missing parameters: {string.Join(", ", ParametersNeeded.Where(p => !parameters.ContainsKey(p)))}");
            }
            float sigma = 0f;
            if (parameters.TryGetValue("Sigma", out var val))
            {
                sigma = DynamicParam.ToFloat(val);
            }
            var effect = new BlurEffect_IPicture { Sigma = sigma };
            effect.Parameters = parameters;
            return effect;
        }

        public IEffect WithParameters(Dictionary<string, object> parameters) => FromParametersDictionary(parameters);

        public IPicture Render(IPicture source, int targetWidth, int targetHeight)
        {
            float sigma = DynamicParam.Resolve(Parameters.GetValueOrDefault("Sigma"), Sigma);
            return PictureEffectChannels.PreserveHdr(BlurEffect.Process(source, sigma), source);
        }
    }

    /// <summary>
    /// The Render-side provider of the Blur effect. It owns the factory capability and the property metadata
    /// (previously split between the provider and factory implementations).
    /// </summary>
    public class BlurEffectProvider : EffectProviderBase
    {
        public BlurEffectProvider()
        {
            Name = "Blur";
            SetField("Sigma", 4f);
        }

        public override string TypeName => "Blur";

        public override EffectType TypeOfEffect => EffectType.NormalEffect;

        public override EffectTarget Target => EffectTarget.Video;

        public override string FromPlugin => InternalPluginBase.InternalPluginBaseID;

        protected override IReadOnlyList<EffectArgumentFieldDescriptor> DefineFields()
        {
            return
            [
                Field("Sigma", EffectArgumentFieldType.Numeric, "4", min: "0", max: "128")
            ];
        }

        protected override EffectImplementType[] SupportedImplementTypes() => [EffectImplementType.IPicture, EffectImplementType.HwAcceleration];


    }
}
