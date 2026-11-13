using projectFrameCut.Render.RenderAPIBase.EffectAndMixture;
using projectFrameCut.Shared;
using static projectFrameCut.Render.RenderAPIBase.EffectAndMixture.IFullDynamicValueProviderEffect;

namespace projectFrameCut.Render.Effect;

/// <summary>Adapts the computation contract only; scheduling belongs to the new pipeline.</summary>
internal class FullDynamicValueEffect(IEffect effect, string[]? rawInputs = null) : IFullDynamicValueProviderEffect, IDisposable
{
    protected IEffect Inner { get; } = effect;
    public string TypeName => Inner.TypeName;
    public string FromPlugin => Inner.FromPlugin;
    public string Id { get => Inner.Id; set => Inner.Id = value; }
    public string Name { get => Inner.Name; set => Inner.Name = value; }
    public bool Enabled { get => Inner.Enabled; set => Inner.Enabled = value; }
    public int Index { get => Inner.Index; set => Inner.Index = value; }
    public int RelativeWidth { get => Inner.RelativeWidth; set => Inner.RelativeWidth = value; }
    public int RelativeHeight { get => Inner.RelativeHeight; set => Inner.RelativeHeight = value; }
    public string? BindedEffectProvidingSystemID { get => Inner.BindedEffectProvidingSystemID; set => Inner.BindedEffectProvidingSystemID = value; }
    public Dictionary<string, object> Parameters => Inner.Parameters;
    public EffectImplementType ImplementType => EffectImplementType.FullDynamicValueProvider;
    public bool IsDeterministic => Inner.IsDeterministic;
    public bool IsReorderable => false;
    public bool AcceptsHwAccelPictures => Inner.AcceptsHwAccelPictures;
    public void Initialize() => Inner.Initialize();
    public virtual IEffect WithParameters(Dictionary<string, object> parameters) => new FullDynamicValueEffect(Inner.WithParameters(parameters), rawInputs);
    public void Dispose() => (Inner as IDisposable)?.Dispose();

    public virtual IReadOnlyDictionary<string, EffectArgumentFieldType> EvaluateOutputPortType(FullDynamicEffectContext context) => context.OutputTypes;

    protected virtual IEnumerable<string> RequiredInputs(FullDynamicEffectContext context) => context.InputTypes.Keys;

    public virtual IReadOnlyDictionary<string, FullDynamicValueProviderEffectOutputResult> EvaluateState(FullDynamicEffectContext context)
    {
        var inputs = RequiredInputs(context).Select(context.InspectInput).ToArray();
        var state = inputs.Any(i => i.State == FullDynamicInputState.Failed) ? FullDynamicValueProviderEffectOutputAnchorState.CannotToCompute
            : inputs.Any(i => i.State == FullDynamicInputState.Inactive) ? FullDynamicValueProviderEffectOutputAnchorState.Inactive
            : inputs.Any(i => i.State == FullDynamicInputState.Waiting) ? FullDynamicValueProviderEffectOutputAnchorState.NotComputed
            : FullDynamicValueProviderEffectOutputAnchorState.Ready;
        return context.OutputTypes.Keys.ToDictionary(p => p, _ => new FullDynamicValueProviderEffectOutputResult(state,
            inputs.FirstOrDefault(i => i.Error is not null)?.Error?.Message ?? ""));
    }

    public virtual object? ComputeValue(FullDynamicEffectContext context, string portToCompute)
    {
        var result = context.GetOrCompute(this, () =>
        {
            var parameters = context.InputTypes.Keys.ToDictionary(key => key, key => rawInputs?.Contains(key) == true
                ? context.ReadInput(key) : EffectFieldTypes.ConvertInput(context.InputTypes[key], context.ReadInput(key), false));
            var f = context.Frame;
            return context.Services.ComputeEffect(Inner, new EffectExecutionContext
            {
                Input = parameters.GetValueOrDefault(context.PrimaryInput), Parameters = parameters,
                InputTypes = context.InputTypes, Clip = f.Clip, FrameIndex = f.FrameIndex,
                AudioSampleIndex = f.AudioSampleIndex, Progress = f.Progress, ClipProgress = f.ClipProgress,
                TargetWidth = f.TargetWidth, TargetHeight = f.TargetHeight, RelativeWidth = f.RelativeWidth,
                RelativeHeight = f.RelativeHeight, CancellationToken = f.CancellationToken,
            });
        });
        return result is IReadOnlyDictionary<string, object?> outputs ? outputs[portToCompute] : result;
    }
}

internal sealed class FullDynamicSelectionEffect(IEffect effect, bool fallback) : FullDynamicValueEffect(effect)
{
    public override IEffect WithParameters(Dictionary<string, object> parameters) => new FullDynamicSelectionEffect(Inner.WithParameters(parameters), fallback);

    private string Selected(FullDynamicEffectContext c) => fallback
        ? c.InspectInput(c.PrimaryInput) is { State: FullDynamicInputState.Available, Value: not null } ? c.PrimaryInput : "DefaultValue"
        : c.InspectInput("Condition").Value is true ? "TrueValue" : "FalseValue";

    protected override IEnumerable<string> RequiredInputs(FullDynamicEffectContext c)
    {
        string first = fallback ? c.PrimaryInput : "Condition";
        yield return first;
        if (c.InspectInput(first).State != FullDynamicInputState.Waiting)
        {
            string selected = Selected(c);
            if (selected != first) yield return selected;
        }
    }

    public override IReadOnlyDictionary<string, FullDynamicValueProviderEffectOutputResult> EvaluateState(FullDynamicEffectContext c)
    {
        if (fallback && c.InspectInput(c.PrimaryInput).State != FullDynamicInputState.Waiting)
        {
            var input = c.InspectInput(Selected(c));
            var state = input.State switch
            {
                FullDynamicInputState.Waiting => FullDynamicValueProviderEffectOutputAnchorState.NotComputed,
                FullDynamicInputState.Failed => FullDynamicValueProviderEffectOutputAnchorState.CannotToCompute,
                FullDynamicInputState.Inactive => FullDynamicValueProviderEffectOutputAnchorState.Inactive,
                _ => FullDynamicValueProviderEffectOutputAnchorState.Ready,
            };
            return c.OutputTypes.Keys.ToDictionary(k => k, _ => new FullDynamicValueProviderEffectOutputResult(state, input.Error?.Message ?? ""));
        }
        var states = base.EvaluateState(c);
        if (!fallback && states.Values.All(s => s.State == FullDynamicValueProviderEffectOutputAnchorState.Ready)
            && c.InspectInput(Selected(c)).Value is null)
            return c.OutputTypes.Keys.ToDictionary(k => k, _ => new FullDynamicValueProviderEffectOutputResult(FullDynamicValueProviderEffectOutputAnchorState.Inactive, ""));
        return states;
    }

    public override object? ComputeValue(FullDynamicEffectContext c, string portToCompute) => c.ReadInput(Selected(c));
}

internal sealed class FullDynamicConditionEffect(IEffect effect, bool loop) : FullDynamicValueEffect(effect), IFullDynamicSchedulingEffect
{
    public FullDynamicFeedback? Feedback => loop ? new("StartValue", "LoopInput", "Condition", "LoopOutput", "ExitOutput") : null;
    public override IEffect WithParameters(Dictionary<string, object> parameters) => new FullDynamicConditionEffect(Inner.WithParameters(parameters), loop);
    protected override IEnumerable<string> RequiredInputs(FullDynamicEffectContext c) => ["Condition", c.PrimaryInput];
    public override IReadOnlyDictionary<string, FullDynamicValueProviderEffectOutputResult> EvaluateState(FullDynamicEffectContext c)
    {
        var states = base.EvaluateState(c);
        if (states.Values.Any(s => s.State != FullDynamicValueProviderEffectOutputAnchorState.Ready)) return states;
        bool condition = c.InspectInput("Condition").Value is true;
        string selected = loop ? condition ? "LoopOutput" : "ExitOutput" : condition ? "True" : "False";
        return c.OutputTypes.Keys.ToDictionary(k => k, k => new FullDynamicValueProviderEffectOutputResult(
            k == selected ? FullDynamicValueProviderEffectOutputAnchorState.Ready : FullDynamicValueProviderEffectOutputAnchorState.Inactive, ""));
    }
    public override object? ComputeValue(FullDynamicEffectContext c, string portToCompute) => c.Input;
}

internal sealed class FullDynamicVariableEffect(IEffect effect, bool write) : FullDynamicValueEffect(effect), IFullDynamicSchedulingEffect
{
    public bool HasSideEffects => write;
    public override IEffect WithParameters(Dictionary<string, object> parameters) => new FullDynamicVariableEffect(Inner.WithParameters(parameters), write);
    protected override IEnumerable<string> RequiredInputs(FullDynamicEffectContext c) => write ? [c.PrimaryInput] : ["InitialValue"];
    public override object? ComputeValue(FullDynamicEffectContext c, string portToCompute)
    {
        if (!Guid.TryParse(c.Metadata.GetValueOrDefault("VariableId")?.ToString(), out var id)) id = Guid.Parse(BindedEffectProvidingSystemID!);
        if (!write) return c.Services.ReadVariable(id, () => EffectVariableProvider.ConvertValue(c.OutputTypes[portToCompute], c.ReadInput("InitialValue")));
        return c.GetOrCompute(this, () =>
        {
            var value = EffectVariableProvider.ConvertValue(c.OutputTypes[portToCompute], c.Input);
            c.Services.WriteVariable(id, value);
            return value;
        });
    }
}

internal sealed class FullDynamicIntegerEffect(IEffect effect, IntArithmeticOperation? operation) : FullDynamicValueEffect(effect)
{
    public override IEffect WithParameters(Dictionary<string, object> parameters) => new FullDynamicIntegerEffect(Inner.WithParameters(parameters), operation);
    public override object? ComputeValue(FullDynamicEffectContext c, string portToCompute)
    {
        int Read(string key) => EffectParamConvert.TryConvertToInt(c.ReadInput(key), out var value) ? value : 0;
        return operation is { } op ? IntArithmeticValueProviderEffect.Calculate(Read("A"), Read("B"), op) : Read("Value");
    }
}
