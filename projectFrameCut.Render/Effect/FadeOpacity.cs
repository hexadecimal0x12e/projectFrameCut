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
    public class FadeOpacityEffect_IPicture : FadeOpacityEffect<IPicture> { }

    public class FadeOpacityEffect<TPicture> : INormalEffect<TPicture> where TPicture : IPicture
    {
        public IEffect WithPictureType<T>() where T : IPicture => new FadeOpacityEffect<T>();

        public bool IsDeterministic => true;

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

            var effect = new FadeOpacityEffect<TPicture>
            {
                Opacity = DynamicParam.ToFloat(parameters.GetValueOrDefault("Opacity")),
            };
            effect.Parameters = parameters;
            return effect;
        }

        public IEffect WithParameters(Dictionary<string, object> parameters) => FromParametersDictionary(parameters);

        public IPicture Render(TPicture source, int targetWidth, int targetHeight)
        {
            Half opacity = Half.Clamp((Half)DynamicParam.Resolve(Parameters.GetValueOrDefault("Opacity"), Opacity), Half.Zero, Half.One);
            return PictureEffectChannels.PreserveHdr(PictureCalculation<TPicture>.Opacity(source, opacity), source);
        }
    }

    /// <summary>
    /// The Render-side provider of the FadeOpacity effect.
    /// </summary>
    public class FadeOpacityEffectProvider : EffectProviderBase
    {
        public override IReadOnlyList<Stage> SupportedStages => [Stage.Picture];

        public override bool IsDeterministic => true;

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
