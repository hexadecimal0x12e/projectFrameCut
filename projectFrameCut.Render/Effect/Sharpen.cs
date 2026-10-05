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
    public class SharpenEffect_IPicture : INormalEffect
    {
        public bool Enabled { get; set; } = true;
        public int Index { get; set; }
        public string Name { get; set; } = "Sharpen";
        public int RelativeWidth { get; set; }
        public int RelativeHeight { get; set; }

        public float Amount { get; init; } = 1f;
        public Dictionary<string, object> Parameters { get; set; } = new();

        public string FromPlugin => InternalPluginBase.InternalPluginBaseID;
        public EffectImplementType ImplementType => EffectImplementType.IPicture;
        public bool IsReorderable => true;
        bool IEffect.CanProcessFromCanvas => true;

        public static List<string> ParametersNeeded { get; } = ["Amount"];
        public static Dictionary<string, string> ParametersType { get; } = new Dictionary<string, string>
        {
            { "Amount", "float" }
        };

        public string TypeName => "Sharpen";
        public string? BindedEffectProvidingSystemID { get; set; }
        public string Id { get; set; } = string.Empty;

        public static IEffect FromParametersDictionary(Dictionary<string, object> parameters)
        {
            ArgumentNullException.ThrowIfNull(parameters);
            if (!ParametersNeeded.All(parameters.ContainsKey))
            {
                throw new ArgumentException($"Missing parameters: {string.Join(", ", ParametersNeeded.Where(p => !parameters.ContainsKey(p)))}");
            }

            var effect = new SharpenEffect_IPicture
            {
                Amount = DynamicParam.ToFloat(parameters.GetValueOrDefault("Amount")),
            };
            effect.Parameters = parameters;
            return effect;
        }

        public IEffect WithParameters(Dictionary<string, object> parameters) => FromParametersDictionary(parameters);

        public IPicture Render(IPicture source, int targetWidth, int targetHeight)
        {
            float amount = DynamicParam.Resolve(Parameters.GetValueOrDefault("Amount"), Amount);
            return PictureEffectChannels.PreserveHdr(SharpenEffect.Process(source, amount), source);
        }
    }

    /// <summary>
    /// The Render-side provider of the Sharpen effect.
    /// </summary>
    public class SharpenEffectProvider : EffectProviderBase
    {
        public SharpenEffectProvider()
        {
            Name = "Sharpen";
            SetField("Amount", 1f);
        }

        public override string TypeName => "Sharpen";

        public override EffectType TypeOfEffect => EffectType.NormalEffect;

        public override EffectTarget Target => EffectTarget.Video;

        public override string FromPlugin => InternalPluginBase.InternalPluginBaseID;

        protected override IReadOnlyList<EffectArgumentFieldDescriptor> DefineFields()
        {
            return
            [
                Field("Amount", EffectArgumentFieldType.Numeric, "1", min: "0", max: "5")
            ];
        }

        protected override EffectImplementType[] SupportedImplementTypes() => [EffectImplementType.IPicture, EffectImplementType.HwAcceleration];


    }
}
