using projectFrameCut.Render.Plugin;
using projectFrameCut.Render.RenderAPIBase.EffectAndMixture;
using projectFrameCut.Shared;

namespace projectFrameCut.Render.Effect;

public sealed class ConditionalLoopNodeEffectProvider : EffectProviderBase, IMultipleOutputEffectProvider
{
    public const string StartValueKey = "StartValue";
    public const string LoopInputKey = "LoopInput";
    public const string LoopOutputKey = "LoopOutput";
    public const string ExitOutputKey = "ExitOutput";
    public const string ConditionKey = "Condition";

    public ConditionalLoopNodeEffectProvider() => Name = "ConditionalLoopNode";
    public override string TypeName => "ConditionalLoopNode";
    public override string FromPlugin => InternalPluginBase.InternalPluginBaseID;
    public override bool IsDeterministic => true;
    public override EffectType TypeOfEffect => EffectType.NormalEffect;
    public override EffectTarget Target => EffectTarget.Pipeline | EffectTarget.ValueProvider;
    public override IReadOnlyList<EffectStage> SupportedStages => [EffectStage.Render, EffectStage.VectorContent, EffectStage.Composition];

    protected override IReadOnlyList<EffectArgumentFieldDescriptor> DefineFields() =>
    [
        Field(ConditionKey, EffectArgumentFieldType.Boolean | EffectArgumentFieldType.Mandatory, "false"),
    ];

    protected override IReadOnlyDictionary<string, EffectArgumentFieldDescriptor> DefineInFields() =>
        new Dictionary<string, EffectArgumentFieldDescriptor>
        {
            [StartValueKey] = Field(StartValueKey, EffectArgumentFieldType.Any | EffectArgumentFieldType.Mandatory | EffectArgumentFieldType.CanNotBeStatic, ""),
            [LoopInputKey] = Field(LoopInputKey, EffectArgumentFieldType.Any | EffectArgumentFieldType.Mandatory | EffectArgumentFieldType.CanNotBeStatic, ""),
        };

    public IReadOnlyDictionary<string, EffectArgumentFieldDescriptor> OutFields =>
        new Dictionary<string, EffectArgumentFieldDescriptor>
        {
            [LoopOutputKey] = Field(LoopOutputKey, EffectArgumentFieldType.Any, ""),
            [ExitOutputKey] = Field(ExitOutputKey, EffectArgumentFieldType.Any, ""),
        };

    public override EffectArgumentFieldType ResolveFieldType(string fieldId, bool isInput, EffectFieldTypeContext context)
    {
        if (isInput && fieldId == ConditionKey) return EffectArgumentFieldType.Boolean;
        var inputs = new[] { StartValueKey, LoopInputKey }.Select(key => context.InputTypes.GetValueOrDefault(key, EffectArgumentFieldType.Any).BaseType())
            .Where(type => type is not (EffectArgumentFieldType.Any or EffectArgumentFieldType.Unknown)).Distinct().ToArray();
        if (inputs.Length > 1) throw new InvalidOperationException("Conditional loop data ports must use the same type.");
        return inputs.Length == 0 ? EffectArgumentFieldType.Any : inputs[0];
    }

    public override object? ResolveDataFormat(string fieldId, bool isInput, EffectFieldTypeContext context) =>
        fieldId == ConditionKey && isInput ? null
            : context.InputDataFormats.GetValueOrDefault(StartValueKey) ?? context.InputDataFormats.GetValueOrDefault(LoopInputKey);
}

public sealed class ConditionalLoopNodeEffect : IEffect, IMultipleOutputEffect
{
    public bool IsDeterministic => true;
    public bool AcceptsHwAccelPictures => true;
    public string TypeName => "ConditionalLoopNode";
    public string FromPlugin => InternalPluginBase.InternalPluginBaseID;
    public EffectType TypeOfEffect => EffectType.NormalEffect;
    public EffectImplementType ImplementType => EffectImplementType.NotSpecified;
    public string Id { get; set; } = Guid.NewGuid().ToString();
    public string Name { get; set; } = "ConditionalLoopNode";
    public bool Enabled { get; set; } = true;
    public int Index { get; set; }
    public int RelativeWidth { get; set; }
    public int RelativeHeight { get; set; }
    public bool IsReorderable => false;
    public string? BindedEffectProvidingSystemID { get; set; }
    public Dictionary<string, object> Parameters { get; set; } = new();
    public IEffect WithParameters(Dictionary<string, object> parameters) => new ConditionalLoopNodeEffect { Parameters = parameters };

    public IReadOnlyDictionary<string, object?> ComputeOutputs(EffectExecutionContext context)
    {
        context.CancellationToken.ThrowIfCancellationRequested();
        if (context.Input is null) throw new ArgumentException("ConditionalLoopNode requires a data input.");
        if (context.Parameters.GetValueOrDefault(ConditionalLoopNodeEffectProvider.ConditionKey, false) is not bool condition)
            throw new ArgumentException("ConditionalLoopNode requires a bool condition.");
        return new Dictionary<string, object?>
        {
            [ConditionalLoopNodeEffectProvider.LoopOutputKey] = condition ? context.Input : EffectBranchState.Inactive,
            [ConditionalLoopNodeEffectProvider.ExitOutputKey] = condition ? EffectBranchState.Inactive : context.Input,
        };
    }
}
