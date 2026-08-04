using projectFrameCut.Render.RenderAPIBase.EffectAndMixture;
using projectFrameCut.Shared;
using System;
using System.Collections.Generic;

namespace projectFrameCut.Render.Effect
{
    /// <summary>
    /// The Render-side provider of the ClassicSpeedVarianceProvider.
    /// </summary>
    public class ClassicSpeedVarianceProviderEffectProvider : EffectProviderBase
    {
        public ClassicSpeedVarianceProviderEffectProvider()
        {
            Name = "Classic Speed";
            SetField("Ratio", 1f);
        }

        public override string TypeName => "ClassicSpeedVarianceProvider";

        public override EffectType TypeOfEffect => EffectType.SpeedVarianceProvider;

        public override EffectTarget Target => EffectTarget.SpeedVariance;

        protected override IReadOnlyList<EffectArgumentFieldDescriptor> DefineFields()
        {
            return
            [
                Field("Ratio", EffectArgumentFieldType.Numeric, "1", min: "0.05", max: "8")
            ];
        }

        protected override EffectImplementType[] SupportedImplementTypes() => [EffectImplementType.NotSpecified];

        protected override IEffect[] BuildEffects(EffectImplementType implementType, Dictionary<string, object> parameters)
        {
            if (!parameters.ContainsKey("Ratio")) parameters["Ratio"] = 1f;
            var effect = new ClassicSpeedVarianceProvider { Parameters = parameters };
            effect.Initialize();
            return [effect];
        }
    }
}
