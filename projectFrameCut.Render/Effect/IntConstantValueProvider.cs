using projectFrameCut.Render.Plugin;
using projectFrameCut.Render.RenderAPIBase.Context;
using projectFrameCut.Render.RenderAPIBase.EffectAndMixture;
using projectFrameCut.Shared;
using System;
using System.Collections.Generic;

namespace projectFrameCut.Render.Effect
{
    /// <summary>
    /// A value provider effect that exposes a constant integer value (<c>Value</c>).
    /// The constant can be a static value or a dynamic binding to another value provider;
    /// the result is exposed as an int output field (<c>Value</c>).
    /// This is meant for testing the <see cref="IEffectProvider"/> / <see cref="IValueProviderEffect"/>
    /// binding pipeline.
    /// </summary>
    public class IntConstantValueProviderEffect : IValueProviderEffect
    {
        public bool Enabled { get; set; } = true;
        public int Index { get; set; }
        public string Name { get; set; } = "Int Constant";

        // IEffect.Id: effect instance id (provider bundle Guid)
        public string Id { get; set; } = Guid.NewGuid().ToString();

        public string TypeName { get; set; } = "IntConstant";

        public string? BindedEffectProvidingSystemID { get; set; }

        public string FromPlugin => InternalPluginBase.InternalPluginBaseID;
        public EffectImplementType ImplementType => EffectImplementType.NotSpecified;
        public bool IsReorderable => false;
        public string? NeedComputer => null;
        public int RelativeWidth { get; set; }
        public int RelativeHeight { get; set; }

        /// <summary>
        /// The constant integer value to provide.
        /// </summary>
        public int Value { get; set; }

        public Dictionary<string, object> Parameters { get; set; } = new();

        public static IEffect FromParametersDictionary(string typeName, Dictionary<string, object> parameters)
        {
            return new IntConstantValueProviderEffect
            {
                TypeName = typeName,
                Parameters = parameters ?? new Dictionary<string, object>(),
                Value = ResolveInt(parameters, "Value"),
                Name = typeName,
            };
        }

        public IEffect WithParameters(Dictionary<string, object> parameters) => FromParametersDictionary(TypeName, parameters);

        public void Initialize() { }

        /// <summary>
        /// Resolve a field value that may be a static value or a dynamic getter closure.
        /// </summary>
        private static int ResolveInt(Dictionary<string, object> parameters, string key)
        {
            if (parameters is not null && parameters.TryGetValue(key, out var raw) && raw is not null)
            {
                if (raw is Func<object> func) raw = func();
                if (EffectParamConvert.TryConvertToInt(raw, out var v)) return v;
            }
            return 0;
        }

        // ── IEffectArgumentField members ────────────────────────────────

        // IEffectArgumentField.Id: field id (used as the output field name)
        string IEffectArgumentField.Id => "Value";

        string IEffectArgumentField.TypeName => "int";

        string IEffectArgumentField.FromPlugin => InternalPluginBase.InternalPluginBaseID;

        EffectArgumentFieldType IEffectArgumentField.FieldType => EffectArgumentFieldType.Integer;

        bool IEffectArgumentField.IsDynamicAtRenderTime => true;

        string IEffectArgumentField.DefaultValue { get => "0"; set { } }
        string IEffectArgumentField.MinValue { get => "0"; set { } }
        string IEffectArgumentField.MaxValue { get => "0"; set { } }
        string[]? IEffectArgumentField.PresetOptions { get => null; set { } }
        string? IEffectArgumentField.Remarks { get => null; set { } }

        public Func<object> GetGetter() => () =>
        {
            var value = ResolveInt(Parameters, "Value");
            Log($"IntConstantValueProviderEffect {Id}/{Name}'s Value: {value} ({Parameters.GetValueOrDefault("Value")?.GetType()?.Name ?? "<null>"})");
            return value;

        };
    }

    /// <summary>
    /// The Render-side provider of the constant integer value source (<c>IntConstant</c>).
    /// </summary>
    public class IntConstantValueProviderProvider : EffectProviderBase
    {
        public IntConstantValueProviderProvider()
        {
            Name = "Int Constant";
            SetField("Value", 0);
        }

        public override string TypeName => "IntConstant";

        public override EffectType TypeOfEffect => EffectType.NonIPictureOutputValueProvider;

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
                TypeName = "int",
                FromPlugin = FromPlugin,
                FieldType = EffectArgumentFieldType.Integer,
                DefaultValue = "0",
            };
        }

        protected override IReadOnlyList<EffectArgumentFieldDescriptor> DefineFields()
        {
            return
            [
                Field("Value", EffectArgumentFieldType.Integer, "0", remarks: "The constant integer to provide. Can be bound to a value provider."),
            ];
        }

        protected override EffectImplementType[] SupportedImplementTypes() => [EffectImplementType.NotSpecified];

        protected override IEffect[] BuildEffects(EffectImplementType implementType, Dictionary<string, object> parameters)
        {
            return [IntConstantValueProviderEffect.FromParametersDictionary(TypeName, parameters)];
        }
    }
}
