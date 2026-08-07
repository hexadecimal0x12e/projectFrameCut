using projectFrameCut.Render.Plugin;
using projectFrameCut.Render.RenderAPIBase.Context;
using projectFrameCut.Render.RenderAPIBase.EffectAndMixture;
using projectFrameCut.Shared;
using System;
using System.Collections.Generic;

namespace projectFrameCut.Render.Effect
{
    /// <summary>
    /// The arithmetic operation performed by an <see cref="IntArithmeticValueProviderEffect"/>.
    /// </summary>
    public enum IntArithmeticOperation
    {
        Add,
        Subtract,
        Multiply,
        Divide,
    }

    /// <summary>
    /// A value provider effect that computes an integer arithmetic result from two int inputs
    /// (<c>A</c> and <c>B</c>). Each input can be a static value or a dynamic binding to another
    /// value provider; the result is exposed as an int output field (<c>Value</c>).
    /// This is meant for testing the <see cref="IEffectProvider"/> / <see cref="IValueProviderEffect"/>
    /// binding pipeline.
    /// </summary>
    public class IntArithmeticValueProviderEffect : IValueProviderEffect
    {
        public bool Enabled { get; set; } = true;
        public int Index { get; set; }
        public string Name { get; set; } = "Int Arithmetic";

        // IEffect.Id: effect instance id (provider bundle Guid)
        public string Id { get; set; } = Guid.NewGuid().ToString();

        public string TypeName { get; set; } = "IntArithmeticAdd";

        public string? BindedEffectProvidingSystemID { get; set; }

        public string FromPlugin => InternalPluginBase.InternalPluginBaseID;
        public EffectImplementType ImplementType => EffectImplementType.NotSpecified;
        public bool IsReorderable => false;
        public string? NeedComputer => null;
        public int RelativeWidth { get; set; }
        public int RelativeHeight { get; set; }

        /// <summary>
        /// The arithmetic operation to apply.
        /// </summary>
        public IntArithmeticOperation Operation { get; set; } = IntArithmeticOperation.Add;

        public Dictionary<string, object> Parameters { get; set; } = new();

        public static IEffect FromParametersDictionary(string typeName, IntArithmeticOperation operation, Dictionary<string, object> parameters)
        {
            return new IntArithmeticValueProviderEffect
            {
                TypeName = typeName,
                Operation = operation,
                Parameters = parameters ?? new Dictionary<string, object>(),
                Name = typeName,
            };
        }

        public IEffect WithParameters(Dictionary<string, object> parameters) => FromParametersDictionary(TypeName, Operation, parameters);
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
            int a = ResolveInt(Parameters, "A");
            int b = ResolveInt(Parameters, "B");
            Log($"IntArithmeticValueProviderEffect {Id}/{Name}'s A: {a} ({Parameters.GetValueOrDefault("A")?.GetType()?.Name ?? "<null>"})");
            Log($"IntArithmeticValueProviderEffect {Id}/{Name}'s B: {b} ({Parameters.GetValueOrDefault("B")?.GetType()?.Name ?? "<null>"})");
            Log($"IntArithmeticValueProviderEffect {Id}/{Name}'s Operation: {Operation} ");
            var result = Operation switch
            {
                IntArithmeticOperation.Add => a + b,
                IntArithmeticOperation.Subtract => a - b,
                IntArithmeticOperation.Multiply => a * b,
                IntArithmeticOperation.Divide => b == 0 ? 0 : a / b,
                _ => 0,
            };
            Log($"IntArithmeticValueProviderEffect {Id}/{Name}'s Result: {result}");
            return result;

        };
    }

    /// <summary>
    /// The Render-side provider of the integer arithmetic value sources.
    /// Each registered type (IntArithmeticAdd, IntArithmeticSubtract, IntArithmeticMultiply,
    /// IntArithmeticDivide) is an instance with the corresponding <see cref="Operation"/>.
    /// </summary>
    public class IntArithmeticValueProviderProvider : EffectProviderBase
    {
        public IntArithmeticValueProviderProvider()
        {
            Name = "Int Arithmetic";
        }

        /// <summary>
        /// The arithmetic operation of this provider instance.
        /// </summary>
        public IntArithmeticOperation Operation { get; init; } = IntArithmeticOperation.Add;

        public override string TypeName => "IntArithmetic" + Operation;

        public override EffectType TypeOfEffect => EffectType.NonIPictureOutputValueProvider;

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
                Field("A", EffectArgumentFieldType.Integer, "0"),
                Field("B", EffectArgumentFieldType.Integer, "0"),
            ];
        }

        protected override EffectImplementType[] SupportedImplementTypes() => [EffectImplementType.NotSpecified];

        protected override IEffect[] BuildEffects(EffectImplementType implementType, Dictionary<string, object> parameters)
        {
            return [IntArithmeticValueProviderEffect.FromParametersDictionary(TypeName, Operation, parameters)];
        }
    }
}
