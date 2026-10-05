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
    public class FadeOpacityEffect_IPicture : INormalEffect
    {
        public bool Enabled { get; set; } = true;
        public int Index { get; set; }
        public string Name { get; set; } = "FadeOpacity";
        public int RelativeWidth { get; set; }
        public int RelativeHeight { get; set; }

        public float Opacity { get; init; } = 0.8f;
        public Dictionary<string, object> Parameters { get; set; } = new();

        public string FromPlugin => InternalPluginBase.InternalPluginBaseID;
        public EffectImplementType ImplementType => EffectImplementType.IPicture;
        public bool IsReorderable => true;
        bool IEffect.CanProcessFromCanvas => true;

        public static List<string> ParametersNeeded { get; } = ["Opacity"];
        public static Dictionary<string, string> ParametersType { get; } = new Dictionary<string, string>
        {
            { "Opacity", "float" }
        };

        public string TypeName => "FadeOpacity";
        public string? BindedEffectProvidingSystemID { get; set; }
        public string Id { get; set; } = string.Empty;

        public static IEffect FromParametersDictionary(Dictionary<string, object> parameters)
        {
            ArgumentNullException.ThrowIfNull(parameters);
            if (!ParametersNeeded.All(parameters.ContainsKey))
            {
                throw new ArgumentException($"Missing parameters: {string.Join(", ", ParametersNeeded.Where(p => !parameters.ContainsKey(p)))}");
            }

            var effect = new FadeOpacityEffect_IPicture
            {
                Opacity = DynamicParam.ToFloat(parameters.GetValueOrDefault("Opacity")),
            };
            effect.Parameters = parameters;
            return effect;
        }

        public IEffect WithParameters(Dictionary<string, object> parameters) => FromParametersDictionary(parameters);

        public IPicture Render(IPicture source, int targetWidth, int targetHeight)
        {
            float opacity = DynamicParam.Resolve(Parameters.GetValueOrDefault("Opacity"), Opacity);
            return PictureEffectChannels.PreserveHdr(OpacityEffect.Process(source, opacity), source);
        }
    }

    /// <summary>
    /// The Render-side provider of the FadeOpacity effect.
    /// </summary>
    public class FadeOpacityEffectProvider : EffectProviderBase
    {
        public FadeOpacityEffectProvider()
        {
            Name = "FadeOpacity";
            SetField("Opacity", 0.8f);
        }

        public override string TypeName => "FadeOpacity";

        public override EffectType TypeOfEffect => EffectType.NormalEffect;

        public override EffectTarget Target => EffectTarget.Video;

        public override string FromPlugin => InternalPluginBase.InternalPluginBaseID;

        protected override IReadOnlyList<EffectArgumentFieldDescriptor> DefineFields()
        {
            return
            [
                Field("Opacity", EffectArgumentFieldType.Numeric, "0.8", min: "0", max: "1")
            ];
        }

        protected override EffectImplementType[] SupportedImplementTypes() => [EffectImplementType.IPicture, EffectImplementType.HwAcceleration];


    }
}
