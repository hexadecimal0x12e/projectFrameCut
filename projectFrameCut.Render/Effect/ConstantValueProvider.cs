using projectFrameCut.Render.Plugin;
using projectFrameCut.Render.RenderAPIBase.EffectAndMixture;
using projectFrameCut.Shared;
using System;
using System.Collections.Generic;

namespace projectFrameCut.Render.Effect
{
    /// <summary>
    /// A value provider effect that always yields the configured constant <see cref="Value"/>.
    /// Useful for testing the dynamic parameter binding pipeline with a predictable source.
    /// </summary>
    public class ConstantValueProviderEffect : IValueProviderEffect
    {
        public bool Enabled { get; set; } = true;
        public int Index { get; set; }
        public string Name { get; set; } = "Constant Value";
        public string Id { get; set; } = Guid.NewGuid().ToString();

        public string TypeName => "ConstantValueProvider";

        public string? BindedEffectGroupID { get; set; }

        public string FromPlugin => InternalPluginBase.InternalPluginBaseID;
        public EffectImplementType ImplementType => EffectImplementType.NotSpecified;
        public bool IsReorderable => false;
        public string? NeedComputer => null;
        public int RelativeWidth { get; set; }
        public int RelativeHeight { get; set; }

        public string OutputAnchorName => "Value";
        public bool GenerateOnce => true;

        public float Value { get; set; } = 1f;

        public Dictionary<string, object> Parameters => new Dictionary<string, object>
        {
            { "Value", Value },
        };

        public static List<string> ParametersNeeded { get; } = new() { "Value" };
        public static Dictionary<string, string> ParametersType { get; } = new()
        {
            { "Value", "float" },
        };

        public static IEffect FromParametersDictionary(Dictionary<string, object> parameters)
        {
            var effect = new ConstantValueProviderEffect();
            if (parameters.TryGetValue("Value", out var value)) effect.Value = Convert.ToSingle(value);
            return effect;
        }

        public IEffect WithParameters(Dictionary<string, object> parameters) => FromParametersDictionary(parameters);

        public object? GenerateValue(uint frameIndex, IComputer? computer, int targetWidth, int targetHeight)
        {
            return Value;
        }

        public void Initialize() { }
    }

    /// <summary>
    /// The Render-side provider of the ConstantValueProvider bindable value source.
    /// </summary>
    public class ConstantValueProviderProvider : EffectProviderBase
    {
        public ConstantValueProviderProvider()
        {
            Name = "Constant Value";
            Parameters = new Dictionary<string, object> { { "Value", 1f } };
        }

        public override string TypeName => "ConstantValueProvider";

        public override EffectType TypeOfEffect => EffectType.BindableEffect;

        public override EffectTarget Target => EffectTarget.ValueProvider | EffectTarget.Video;

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
                Field("Value", EffectArgumentFieldType.Numeric, "1"),
            ];
        }

        protected override EffectImplementType[] SupportedImplementTypes() => [EffectImplementType.NotSpecified];

        protected override IEffect[] BuildEffects(EffectImplementType implementType, Dictionary<string, object> parameters)
        {
            return [ConstantValueProviderEffect.FromParametersDictionary(parameters)];
        }
    }
}
