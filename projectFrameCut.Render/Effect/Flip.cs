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
    public class FlipEffect_IPicture : INormalEffect
    {
        public bool Enabled { get; set; } = true;
        public int Index { get; set; }
        public string Name { get; set; } = "Flip";
        public int RelativeWidth { get; set; }
        public int RelativeHeight { get; set; }

        public bool Horizontal { get; init; }
        public bool Vertical { get; init; }
        public Dictionary<string, object> Parameters { get; set; } = new();

        public string FromPlugin => InternalPluginBase.InternalPluginBaseID;
        public EffectImplementType ImplementType => EffectImplementType.IPicture;
        public bool IsReorderable => true;
        bool IEffect.CanProcessFromCanvas => true;

        public static List<string> ParametersNeeded { get; } = ["Horizontal", "Vertical"];

        public static Dictionary<string, string> ParametersType { get; } = new Dictionary<string, string>
        {
            { "Horizontal", "bool" },
            { "Vertical", "bool" }
        };

        public string TypeName => "Flip";
        public string? BindedEffectProvidingSystemID { get; set; }
        public string Id { get; set; } = string.Empty;

        public static IEffect FromParametersDictionary(Dictionary<string, object> parameters)
        {
            ArgumentNullException.ThrowIfNull(parameters);
            if (!ParametersNeeded.All(parameters.ContainsKey))
            {
                throw new ArgumentException($"Missing parameters: {string.Join(", ", ParametersNeeded.Where(p => !parameters.ContainsKey(p)))}");
            }

            var effect = new FlipEffect_IPicture
            {
                Horizontal = DynamicParam.ToBool(parameters.GetValueOrDefault("Horizontal")),
                Vertical = DynamicParam.ToBool(parameters.GetValueOrDefault("Vertical")),
            };
            effect.Parameters = parameters;
            return effect;
        }

        public IEffect WithParameters(Dictionary<string, object> parameters) => FromParametersDictionary(parameters);

        public IPicture Render(IPicture source, int targetWidth, int targetHeight)
        {
            bool horizontal = DynamicParam.Resolve(Parameters.GetValueOrDefault("Horizontal"), Horizontal);
            bool vertical = DynamicParam.Resolve(Parameters.GetValueOrDefault("Vertical"), Vertical);
            return PictureEffectChannels.MapHdr(FlipEffect.Process(source, horizontal, vertical), source,
                (x, y) => (horizontal ? source.Width - 1 - x : x, vertical ? source.Height - 1 - y : y));
        }
    }

    /// <summary>
    /// The Render-side provider of the Flip effect.
    /// </summary>
    public class FlipEffectProvider : EffectProviderBase
    {
        public FlipEffectProvider()
        {
            Name = "Flip";
            SetField("Horizontal", false);
            SetField("Vertical", false);
        }

        public override string TypeName => "Flip";

        public override EffectType TypeOfEffect => EffectType.NormalEffect;

        public override EffectTarget Target => EffectTarget.Video;

        public override string FromPlugin => InternalPluginBase.InternalPluginBaseID;

        protected override IReadOnlyList<EffectArgumentFieldDescriptor> DefineFields()
        {
            return
            [
                Field("Horizontal", EffectArgumentFieldType.Boolean, "false"),
                Field("Vertical", EffectArgumentFieldType.Boolean, "false")
            ];
        }

        protected override EffectImplementType[] SupportedImplementTypes() => [EffectImplementType.IPicture, EffectImplementType.HwAcceleration];


    }
}
