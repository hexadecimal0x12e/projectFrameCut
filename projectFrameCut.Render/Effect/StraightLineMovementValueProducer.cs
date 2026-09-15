using projectFrameCut.Render.Plugin;
using projectFrameCut.Render.RenderAPIBase.EffectAndMixture;
using projectFrameCut.Shared;
using System;
using System.Collections.Generic;

namespace projectFrameCut.Render.Effect
{
    public class StraightLineMovementValueProducer : IBindableArgumentEffectValueProvider
    {
        public bool Enabled { get; set; } = true;
        public int Index { get; set; }
        public string Name { get; set; } = "";
        public string Id { get; set; } = Guid.NewGuid().ToString();

        public string? BindedArgumentProviderID { get; set; }

        public int StartX { get; set; }
        public int StartY { get; set; }
        public int EndX { get; set; }
        public int EndY { get; set; }

        public string? NeedComputer => null;
        public string FromPlugin => InternalPluginBase.InternalPluginBaseID;
        public EffectImplementType ImplementType => EffectImplementType.NotSpecified;
        public string TypeName => "StraightLineMovementValueProducer";

        public Dictionary<string, object> Parameters => new Dictionary<string, object>
        {
            { "StartX", StartX },
            { "StartY", StartY },
            { "EndX", EndX },
            { "EndY", EndY },
        };

        public static List<string> ParametersNeeded { get; } = new List<string>
        {
            "StartX",
            "StartY",
            "EndX",
            "EndY",
        };

        public static Dictionary<string, string> ParametersType { get; } = new Dictionary<string, string>
        {
            {"StartX", "int" },
            {"StartY", "int" },
            {"EndX", "int" },
            {"EndY", "int" },
        };

        public static IEffect FromParametersDictionary(Dictionary<string, object> parameters)
        {
            var effect = new StraightLineMovementValueProducer();
            if (parameters.TryGetValue("StartX", out var startX)) effect.StartX = Convert.ToInt32(startX);
            if (parameters.TryGetValue("StartY", out var startY)) effect.StartY = Convert.ToInt32(startY);
            if (parameters.TryGetValue("EndX", out var endX)) effect.EndX = Convert.ToInt32(endX);
            if (parameters.TryGetValue("EndY", out var endY)) effect.EndY = Convert.ToInt32(endY);
            return effect;
        }

        public IEffect WithParameters(Dictionary<string, object> parameters) => FromParametersDictionary(parameters);

        public object GenerateValue(IPicture source, IComputer? computer, int targetWidth, int targetHeight)
        {
            return new Func<double, System.Drawing.Point>((progress) =>
            {
                int x = (int)Math.Round(StartX + (EndX - StartX) * progress);
                int y = (int)Math.Round(StartY + (EndY - StartY) * progress);
                return new System.Drawing.Point(x, y);
            });
        }

        public bool IsValueValid(object value) => value is Func<double, System.Drawing.Point>;

        public void Initialize() { }

        public bool GenerateOnce => true;

        public int RelativeWidth { get; set; }
        public int RelativeHeight { get; set; }
        public string? BindedEffectProvidingSystemID { get; set; }

        public string OutputAnchorName => "Point";
    }

    /// <summary>
    /// The Render-side provider of the StraightLineMovementValueProducer bindable value source.
    /// </summary>
    public class StraightLineMovementValueProducerProvider : EffectProviderBase
    {
        public StraightLineMovementValueProducerProvider()
        {
            Name = "Straight Line Movement";
            SetField("StartX", 0);
            SetField("StartY", 0);
            SetField("EndX", 0);
            SetField("EndY", 0);
        }

        public override string TypeName => "StraightLineMovementValueProducer";

        public override EffectType TypeOfEffect => EffectType.BindableEffect;

        public override EffectTarget Target => EffectTarget.ValueProvider | EffectTarget.Video;

        public override string FromPlugin => InternalPluginBase.InternalPluginBaseID;

        protected override IReadOnlyDictionary<string, EffectArgumentFieldDescriptor> DefineInFields()
        {
            // A value provider has no picture input.
            return new Dictionary<string, EffectArgumentFieldDescriptor>();
        }

        protected override EffectArgumentFieldDescriptor DefineOutField()
        {
            return new EffectArgumentFieldDescriptor
            {
                Id = OutputAnchorKey,
                TypeName = "float",
                FromPlugin = FromPlugin,
                FieldType = EffectArgumentFieldType.Numeric,
                DefaultValue = "0",
            };
        }

        protected override IReadOnlyList<EffectArgumentFieldDescriptor> DefineFields()
        {
            return
            [
                Field("StartX", EffectArgumentFieldType.Integer, "0"),
                Field("StartY", EffectArgumentFieldType.Integer, "0"),
                Field("EndX", EffectArgumentFieldType.Integer, "0"),
                Field("EndY", EffectArgumentFieldType.Integer, "0")
            ];
        }

        protected override EffectImplementType[] SupportedImplementTypes() => [EffectImplementType.NotSpecified];

        protected override IEffect[] BuildEffects(EffectImplementType implementType, Dictionary<string, object> parameters)
        {
            return [StraightLineMovementValueProducer.FromParametersDictionary(parameters)];
        }
    }
}
