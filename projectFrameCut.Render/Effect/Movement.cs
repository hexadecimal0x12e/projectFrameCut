using projectFrameCut.Render.Plugin;
using projectFrameCut.Render.RenderAPIBase.EffectAndMixture;
using projectFrameCut.Shared;
using System;
using System.Collections.Generic;

namespace projectFrameCut.Render.Effect
{
    /// <summary>
    /// The Render-side provider of the Movement effect. It creates a straight-line movement value producer
    /// bound to a point placer (replaces the legacy movement provider).
    /// </summary>
    public class MovementEffectProvider : EffectProviderBase
    {
        public MovementEffectProvider()
        {
            Name = "Movement";
            SetField("StartX", 0);
            SetField("StartY", 0);
            SetField("EndX", 200);
            SetField("EndY", 0);
            SetField("Duration", 1000);
        }

        public override string TypeName => "Movement";

        public override EffectType TypeOfEffect => EffectType.ContinuousEffect;

        // movement can be replaced by ProgressPlacer; it will be removed in 1.7.0.0
        public override EffectTarget Target => EffectTarget.Video | EffectTarget.IsNotVisibleInNewEffectSelector;

        public override string FromPlugin => InternalPluginBase.InternalPluginBaseID;

        protected override IReadOnlyList<EffectArgumentFieldDescriptor> DefineFields()
        {
            return
            [
                Field("StartX", EffectArgumentFieldType.Integer, "0"),
                Field("StartY", EffectArgumentFieldType.Integer, "0"),
                Field("EndX", EffectArgumentFieldType.Integer, "200"),
                Field("EndY", EffectArgumentFieldType.Integer, "0"),
                Field("Duration", EffectArgumentFieldType.Integer, "1000", min: "100")
            ];
        }

        protected override EffectImplementType[] SupportedImplementTypes() => [EffectImplementType.NotSpecified];

        protected override IEffect[] BuildEffects(EffectImplementType implementType, Dictionary<string, object> parameters)
        {
            string movementProducerId = Guid.NewGuid().ToString();
            var producer = new StraightLineMovementValueProducer
            {
                Id = movementProducerId,
                StartX = GetInt(parameters, "StartX", 0),
                StartY = GetInt(parameters, "StartY", 0),
                EndX = GetInt(parameters, "EndX", 200),
                EndY = GetInt(parameters, "EndY", 0),
            };

            var pointPlacer = new PointPlacer
            {
                Id = Id.ToString(),
                BindedArgumentProviderID = movementProducerId
            };

            return [producer, pointPlacer];
        }

        private static int GetInt(Dictionary<string, object> parameters, string key, int fallback)
        {
            return parameters.TryGetValue(key, out var v) && EffectParamConvert.TryConvertToInt(v, out var i) ? i : fallback;
        }
    }
}
