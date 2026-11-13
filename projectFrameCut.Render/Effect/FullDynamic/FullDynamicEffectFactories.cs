using projectFrameCut.Render.RenderAPIBase.EffectAndMixture;
using projectFrameCut.Shared;

namespace projectFrameCut.Render.Effect;

internal static class FullDynamicEffectFactories
{
    public static IReadOnlyDictionary<EffectImplementationKey, Func<IEffect>> Add(Dictionary<EffectImplementationKey, Func<IEffect>> factories)
    {
        string[] values = ["IntConstant", "IntOverlay", "SplitPictureChannels", "IntArithmeticAdd", "IntArithmeticSubtract", "IntArithmeticMultiply",
            "IntArithmeticDivide", "ValueTypeConversionNode", "NumericComparisonNode", "EqualityComparisonNode",
            "AdvancedMath1", "AdvancedMath2", "AdvancedMath3", "MergePictureChannels", "CreatePictureFP16",
            "CreateHDRPictureFP16", "ReadPictureFP16Pixel", "WritePictureFP16Pixel", "PictureChannelAverage",
            "IfConditionNode", "IsUpstreamAvailableOrDefault", "ConditionComputeNode", "ConditionalLoopNode",
            "EffectVariableRead", "EffectVariableWrite"];
        foreach (string name in values)
        {
            var entry = factories.First(p => p.Key.TypeName == name);
            factories[new(name, EffectImplementType.FullDynamicValueProvider)] = () => name switch
            {
                "IntConstant" => new FullDynamicIntegerEffect(entry.Value(), null),
                "IntArithmeticAdd" => new FullDynamicIntegerEffect(entry.Value(), IntArithmeticOperation.Add),
                "IntArithmeticSubtract" => new FullDynamicIntegerEffect(entry.Value(), IntArithmeticOperation.Subtract),
                "IntArithmeticMultiply" => new FullDynamicIntegerEffect(entry.Value(), IntArithmeticOperation.Multiply),
                "IntArithmeticDivide" => new FullDynamicIntegerEffect(entry.Value(), IntArithmeticOperation.Divide),
                "IfConditionNode" => new FullDynamicSelectionEffect(entry.Value(), false),
                "IsUpstreamAvailableOrDefault" => new FullDynamicSelectionEffect(entry.Value(), true),
                "ConditionComputeNode" => new FullDynamicConditionEffect(entry.Value(), false),
                "ConditionalLoopNode" => new FullDynamicConditionEffect(entry.Value(), true),
                "EffectVariableRead" => new FullDynamicVariableEffect(entry.Value(), false),
                "EffectVariableWrite" => new FullDynamicVariableEffect(entry.Value(), true),
                "NumericComparisonNode" or "AdvancedMath1" or "AdvancedMath2" or "AdvancedMath3" => new FullDynamicValueEffect(entry.Value(), ["A", "B", "C"]),
                "EqualityComparisonNode" => new FullDynamicValueEffect(entry.Value(), ["A", "B"]),
                _ => new FullDynamicValueEffect(entry.Value()),
            };
            if (name is "IntConstant" or "IntArithmeticAdd" or "IntArithmeticSubtract" or "IntArithmeticMultiply" or "IntArithmeticDivide")
                factories[new(name, EffectImplementType.StaticOrHalfDynamicValueProvider)] = () => new StaticValueEffect((IValueProviderEffect)entry.Value());
        }
        return factories;
    }

    private sealed class StaticValueEffect(IValueProviderEffect effect) : IValueProviderEffect, IDisposable
    {
        public EffectImplementType ImplementType => EffectImplementType.StaticOrHalfDynamicValueProvider;
        public string TypeName => ((IEffect)effect).TypeName;
        public string FromPlugin => ((IEffect)effect).FromPlugin;
        public string Id { get => ((IEffect)effect).Id; set => ((IEffect)effect).Id = value; }
        public string Name { get => effect.Name; set => effect.Name = value; }
        public bool Enabled { get => effect.Enabled; set => effect.Enabled = value; }
        public int Index { get => effect.Index; set => effect.Index = value; }
        public int RelativeWidth { get => effect.RelativeWidth; set => effect.RelativeWidth = value; }
        public int RelativeHeight { get => effect.RelativeHeight; set => effect.RelativeHeight = value; }
        public string? BindedEffectProvidingSystemID { get => effect.BindedEffectProvidingSystemID; set => effect.BindedEffectProvidingSystemID = value; }
        public Dictionary<string, object> Parameters => effect.Parameters;
        public bool IsDeterministic => effect.IsDeterministic;
        public bool IsReorderable => false;
        public IEffect WithParameters(Dictionary<string, object> parameters) => new StaticValueEffect((IValueProviderEffect)effect.WithParameters(parameters));
        public void Initialize() => effect.Initialize();
        public void Dispose() => (effect as IDisposable)?.Dispose();
        string IEffectArgumentField.Id => ((IEffectArgumentField)effect).Id;
        string IEffectArgumentField.TypeName => ((IEffectArgumentField)effect).TypeName;
        public EffectArgumentFieldType FieldType => effect.FieldType;
        public bool IsDynamicAtRenderTime => true;
        public string DefaultValue { get => effect.DefaultValue; set => effect.DefaultValue = value; }
        public string MinValue { get => effect.MinValue; set => effect.MinValue = value; }
        public string MaxValue { get => effect.MaxValue; set => effect.MaxValue = value; }
        public string[]? PresetOptions { get => effect.PresetOptions; set => effect.PresetOptions = value; }
        public string? Remarks { get => effect.Remarks; set => effect.Remarks = value; }
        public Func<object> GetGetter() => effect.GetGetter();
    }
}
