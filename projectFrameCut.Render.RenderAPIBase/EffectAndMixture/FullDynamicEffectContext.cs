namespace projectFrameCut.Render.RenderAPIBase.EffectAndMixture;

public enum FullDynamicInputState { Waiting, Available, Inactive, Failed }

public sealed record FullDynamicInput(FullDynamicInputState State, object? Value = null, Exception? Error = null);

public sealed record FullDynamicFeedback(string StartInput, string FeedbackInput, string ConditionInput,
    string BodyOutput, string ExitOutput);

/// <summary>Capabilities interpreted by the full dynamic scheduler, independent of provider types.</summary>
public interface IFullDynamicSchedulingEffect
{
    FullDynamicFeedback? Feedback => null;
    bool HasSideEffects => false;
}

public interface IFullDynamicExecutionServices
{
    FullDynamicInput InspectInput(string port, bool request = true);
    IReadOnlyDictionary<Guid, object?> Variables { get; }
    void OnCompleted(Action release);
    object? ComputeEffect(IEffect effect, EffectExecutionContext context);
    object? ReadInput(string port);
    object? GetOrCompute(object key, Func<object?> compute);
    object? ReadVariable(Guid id, Func<object?> initialValue);
    void WriteVariable(Guid id, object? value);
}

/// <summary>One node invocation. Inspection never executes upstream nodes; reads are demand driven.</summary>
public sealed class FullDynamicEffectContext
{
    public Guid InvocationId { get; init; } = Guid.NewGuid();
    public required EffectExecutionContext Frame { get; init; }
    public required IFullDynamicExecutionServices Services { get; init; }
    public required IReadOnlyDictionary<string, EffectArgumentFieldType> InputTypes { get; init; }
    public required IReadOnlyDictionary<string, EffectArgumentFieldType> OutputTypes { get; init; }
    public IReadOnlyDictionary<string, EffectPortTypeContract?> OutputContracts { get; init; } = new Dictionary<string, EffectPortTypeContract?>();
    public IReadOnlyDictionary<string, object> Metadata { get; init; } = new Dictionary<string, object>();
    public string PrimaryInput { get; init; } = EffectProviderAnchorExtensions.InputKey;
    public FullDynamicInput PeekInput(string port) => Services.InspectInput(port, false);
    public object? Input => ReadInput(PrimaryInput);
    public FullDynamicInput InspectInput(string port) => Services.InspectInput(port);
    public object? ReadInput(string port) => Services.ReadInput(port);
    public object? GetOrCompute(object key, Func<object?> compute) => Services.GetOrCompute(key, compute);
}
