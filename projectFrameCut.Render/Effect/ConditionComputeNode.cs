using projectFrameCut.Render.Plugin;
using projectFrameCut.Render.RenderAPIBase.EffectAndMixture;
using projectFrameCut.Shared;

namespace projectFrameCut.Render.Effect;

public sealed class ConditionComputeNodeEffectProvider : EffectProviderBase, IMultipleOutputEffectProvider
{
    public override IReadOnlyList<Stage> SupportedStages => EffectStages.Values;

    public override bool IsDeterministic => true;

    public const string ConditionKey = "Condition";
    public const string TrueOutputKey = "True";
    public const string FalseOutputKey = "False";

    public ConditionComputeNodeEffectProvider() => Name = "ConditionComputeNode";
    public override string TypeName => "ConditionComputeNode";
    public override string FromPlugin => InternalPluginBase.InternalPluginBaseID;
    public override EffectType TypeOfEffect => EffectType.NormalEffect;
    public override EffectTarget Target => EffectTarget.Video | EffectTarget.ValueProvider;

    protected override IReadOnlyList<EffectArgumentFieldDescriptor> DefineFields() =>
    [
        Field(ConditionKey, EffectArgumentFieldType.Boolean, "false"),
    ];

    protected override IReadOnlyDictionary<string, EffectArgumentFieldDescriptor> DefineInFields() =>
        new Dictionary<string, EffectArgumentFieldDescriptor>
        {
            [PrimaryInputAnchorKey] = Field(PrimaryInputAnchorKey,
                EffectArgumentFieldType.Any | EffectArgumentFieldType.Mandatory | EffectArgumentFieldType.CanNotBeStatic, ""),
        };

    public IReadOnlyDictionary<string, EffectArgumentFieldDescriptor> OutFields =>
        new Dictionary<string, EffectArgumentFieldDescriptor>
        {
            [TrueOutputKey] = Field(TrueOutputKey, EffectArgumentFieldType.Any, ""),
            [FalseOutputKey] = Field(FalseOutputKey, EffectArgumentFieldType.Any, ""),
        };

    public override object? ResolveDataFormat(string fieldId, bool isInput, EffectFieldTypeContext context) =>
        isInput ? base.ResolveDataFormat(fieldId, true, context) : context.InputDataFormats.GetValueOrDefault(PrimaryInputAnchorKey);

    public override EffectArgumentFieldType ResolveFieldType(string fieldId, bool isInput, EffectFieldTypeContext context) =>
        isInput && fieldId == ConditionKey ? EffectArgumentFieldType.Boolean
            : context.InputTypes.GetValueOrDefault(PrimaryInputAnchorKey, EffectArgumentFieldType.Any);
}

internal static class EffectBranchState
{
    public static object Inactive { get; } = new();
}

public sealed class ConditionComputeNodeEffect : IEffect, IMultipleOutputEffect
{
    public bool IsDeterministic => true;
    public bool AcceptsHwAccelPictures => true;

    public string TypeName => "ConditionComputeNode";
    public string FromPlugin => InternalPluginBase.InternalPluginBaseID;
    public EffectType TypeOfEffect => EffectType.NormalEffect;
    public EffectImplementType ImplementType => EffectImplementType.NotSpecified;
    public string Id { get; set; } = Guid.NewGuid().ToString();
    public string Name { get; set; } = "ConditionComputeNode";
    public bool Enabled { get; set; } = true;
    public int Index { get; set; }
    public int RelativeWidth { get; set; }
    public int RelativeHeight { get; set; }
    public bool IsReorderable => false;
    public string? BindedEffectProvidingSystemID { get; set; }
    public Dictionary<string, object> Parameters { get; set; } = new();
    public IEffect WithParameters(Dictionary<string, object> parameters) => new ConditionComputeNodeEffect { Parameters = parameters };

    public IReadOnlyDictionary<string, object?> ComputeOutputs(EffectExecutionContext context)
    {
        context.CancellationToken.ThrowIfCancellationRequested();
        if (context.Input is null) throw new ArgumentException("ConditionComputeNode requires a primary input.");
        if (context.Parameters.GetValueOrDefault(ConditionComputeNodeEffectProvider.ConditionKey, false) is not bool condition)
            throw new ArgumentException("ConditionComputeNode requires a bool condition.");
        return new Dictionary<string, object?>
        {
            [ConditionComputeNodeEffectProvider.TrueOutputKey] = condition ? context.Input : EffectBranchState.Inactive,
            [ConditionComputeNodeEffectProvider.FalseOutputKey] = condition ? EffectBranchState.Inactive : context.Input,
        };
    }
}
