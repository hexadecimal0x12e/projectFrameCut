using projectFrameCut.Render.Plugin;
using projectFrameCut.Render.RenderAPIBase.EffectAndMixture;
using projectFrameCut.Shared;
using System;
using System.Collections.Generic;

namespace projectFrameCut.Render.Effect
{
    /// <summary>
    /// A value provider effect that linearly interpolates a value between <see cref="FromValue"/> and
    /// <see cref="ToValue"/> across the clip's duration (using the built-in clip progress source).
    /// Other effects can bind a dynamic parameter to this provider.
    /// </summary>
    public class LinearAnimationValueProviderEffect : IValueProviderEffect
    {
        public bool Enabled { get; set; } = true;
        public int Index { get; set; }
        public string Name { get; set; } = "Linear Animation";
        public string Id { get; set; } = Guid.NewGuid().ToString();

        public string TypeName => "LinearAnimationValueProvider";

        public string? BindedEffectGroupID { get; set; }

        public string FromPlugin => InternalPluginBase.InternalPluginBaseID;
        public EffectImplementType ImplementType => EffectImplementType.NotSpecified;
        public bool IsReorderable => false;
        public string? NeedComputer => null;
        public int RelativeWidth { get; set; }
        public int RelativeHeight { get; set; }

        public string OutputAnchorName => "Value";
        public bool GenerateOnce => false;

        public float FromValue { get; set; }
        public float ToValue { get; set; }

        public Dictionary<string, object> Parameters => new Dictionary<string, object>
        {
            { "FromValue", FromValue },
            { "ToValue", ToValue },
        };

        public static List<string> ParametersNeeded { get; } = new() { "FromValue", "ToValue" };
        public static Dictionary<string, string> ParametersType { get; } = new()
        {
            { "FromValue", "float" },
            { "ToValue", "float" },
        };

        public static IEffect FromParametersDictionary(Dictionary<string, object> parameters)
        {
            var effect = new LinearAnimationValueProviderEffect();
            if (parameters.TryGetValue("FromValue", out var from)) effect.FromValue = Convert.ToSingle(from);
            if (parameters.TryGetValue("ToValue", out var to)) effect.ToValue = Convert.ToSingle(to);
            return effect;
        }

        public IEffect WithParameters(Dictionary<string, object> parameters) => FromParametersDictionary(parameters);

        public object? GenerateValue(uint frameIndex, IComputer? computer, int targetWidth, int targetHeight)
        {
            var progress = ValueProviderFrameContext.Get(ValueProviderFrameContext.BuiltInProgressProviderId) as float? ?? 0f;
            return FromValue + (ToValue - FromValue) * progress;
        }

        public void Initialize() { }
    }

    /// <summary>
    /// The Render-side provider of the LinearAnimationValueProvider bindable value source.
    /// </summary>
    public class LinearAnimationValueProviderProvider : EffectProviderBase
    {
        public LinearAnimationValueProviderProvider()
        {
            Name = "Linear Animation";
            Parameters = new Dictionary<string, object> { { "FromValue", 0f }, { "ToValue", 1f } };
        }

        public override string TypeName => "LinearAnimationValueProvider";

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
                Field("FromValue", EffectArgumentFieldType.Numeric, "0"),
                Field("ToValue", EffectArgumentFieldType.Numeric, "1"),
            ];
        }

        protected override EffectImplementType[] SupportedImplementTypes() => [EffectImplementType.NotSpecified];

        protected override IEffect[] BuildEffects(EffectImplementType implementType, Dictionary<string, object> parameters)
        {
            return [LinearAnimationValueProviderEffect.FromParametersDictionary(parameters)];
        }
    }
}
