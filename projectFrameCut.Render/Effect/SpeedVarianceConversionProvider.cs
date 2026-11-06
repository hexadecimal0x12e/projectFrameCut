using projectFrameCut.Render.Plugin;
using projectFrameCut.Render.RenderAPIBase.ClipAndTrack;
using projectFrameCut.Render.RenderAPIBase.EffectAndMixture;
using projectFrameCut.Shared;

namespace projectFrameCut.Render.Effect;

public sealed class CustomToSpeedVarianceProvider : CustomSpeedVarianceProvider
{
    public const string ConversionTypeName = "CustomToSpeedVariance";
    public override string TypeName => ConversionTypeName;
    public override EffectType TypeOfEffect => EffectType.NonIPictureOutputValueProvider;
    public override EffectTarget Target => EffectTarget.ValueProvider;
    protected override EffectArgumentFieldDescriptor DefineOutField() => Field("Speed", EffectArgumentFieldType.SpeedVariance, "");
    public CustomToSpeedVarianceProvider() => Name = "Custom to Speed Provider";
}

public sealed class SpeedVarianceToCustomProvider : EffectProviderBase, IMultipleOutputEffectProvider
{
    public const string ProviderTypeName = "SpeedVarianceToCustom";
    public const string SpeedKey = "Speed";
    public const string FrameKey = "Frame";
    public const string LengthKey = "Length";
    public const string SourceLengthKey = "SourceLength";
    public const string HasSourceLengthKey = "HasSourceLength";
    public const string OutputLengthKey = "OutputLength";
    public override string TypeName => ProviderTypeName;
    public override string FromPlugin => InternalPluginBase.InternalPluginBaseID;
    public override EffectType TypeOfEffect => EffectType.NonIPictureOutputValueProvider;
    public override EffectTarget Target => EffectTarget.ValueProvider;
    public override IReadOnlyList<EffectStage> SupportedStages => [EffectStage.Composition];
    public override bool IsDeterministic => true;

    public SpeedVarianceToCustomProvider()
    {
        Name = "Speed Provider to Custom";
        AnchorsBindingState[FrameKey] = ValueProviderFrameContext.SpeedFrameProviderId;
        AnchorsBindingState[LengthKey] = ValueProviderFrameContext.SpeedClipLengthProviderId;
        AnchorsBindingState[SourceLengthKey] = ValueProviderFrameContext.SpeedSourceLengthProviderId;
        AnchorsBindingState[HasSourceLengthKey] = ValueProviderFrameContext.SpeedHasSourceLengthProviderId;
    }

    protected override IReadOnlyDictionary<string, EffectArgumentFieldDescriptor> DefineInFields() => new Dictionary<string, EffectArgumentFieldDescriptor>();
    protected override IReadOnlyList<EffectArgumentFieldDescriptor> DefineFields() =>
    [
        Field(SpeedKey, EffectArgumentFieldType.SpeedVariance | EffectArgumentFieldType.Mandatory | EffectArgumentFieldType.CanNotBeStatic, ""),
        Field(FrameKey, EffectArgumentFieldType.UnsignedInteger, "0"),
        Field(LengthKey, EffectArgumentFieldType.UnsignedInteger, "0"),
        Field(SourceLengthKey, EffectArgumentFieldType.UnsignedInteger, "0"),
        Field(HasSourceLengthKey, EffectArgumentFieldType.Boolean, "false"),
    ];
    public IReadOnlyDictionary<string, EffectArgumentFieldDescriptor> OutFields => new Dictionary<string, EffectArgumentFieldDescriptor>
    {
        [CustomSpeedVarianceProvider.SourceFrameKey] = Field(CustomSpeedVarianceProvider.SourceFrameKey, EffectArgumentFieldType.UnsignedInteger, "0"),
        [CustomSpeedVarianceProvider.FinishedKey] = Field(CustomSpeedVarianceProvider.FinishedKey, EffectArgumentFieldType.Boolean, "false"),
        [OutputLengthKey] = Field(OutputLengthKey, EffectArgumentFieldType.UnsignedInteger, "0"),
    };
    protected override EffectArgumentFieldDescriptor DefineOutField() => OutFields[CustomSpeedVarianceProvider.SourceFrameKey];

    internal static IReadOnlyDictionary<string, object?> Evaluate(Func<string, object?> read)
    {
        var speed = read(SpeedKey) as ISpeedVarianceProvider ?? throw new InvalidOperationException("A speed provider must be connected.");
        uint length = ClipTiming.RoundFrame(Convert.ToDouble(read(LengthKey)));
        uint? sourceLength = read(HasSourceLengthKey) is true ? ClipTiming.RoundFrame(Convert.ToDouble(read(SourceLengthKey))) : null;
        uint frame = ClipTiming.RoundFrame(Convert.ToDouble(read(FrameKey)));
        ValueProviderFrameContext.CancellationToken.ThrowIfCancellationRequested();
        uint outputLength = ClipTiming.EffectiveDuration(length, speed, sourceLength);
        bool finished = frame >= outputLength;
        uint source = finished ? 0 : ClipTiming.SourceOffset(frame, length, speed, outputLength, sourceLength);
        if (!finished && sourceLength.HasValue && source >= sourceLength.Value)
            throw new InvalidOperationException($"Speed source frame {source} exceeds available source length {sourceLength}.");
        return new Dictionary<string, object?>
        {
            [CustomSpeedVarianceProvider.SourceFrameKey] = source,
            [CustomSpeedVarianceProvider.FinishedKey] = finished,
            [OutputLengthKey] = outputLength,
        };
    }
}

public sealed class SpeedVarianceToCustomEffect : IMultipleOutputEffect
{
    public string TypeName => SpeedVarianceToCustomProvider.ProviderTypeName;
    public string FromPlugin => InternalPluginBase.InternalPluginBaseID;
    public EffectType TypeOfEffect => EffectType.NonIPictureOutputValueProvider;
    public EffectImplementType ImplementType => EffectImplementType.NotSpecified;
    public string Id { get; set; } = Guid.NewGuid().ToString();
    public string Name { get; set; } = "Speed Provider to Custom";
    public bool IsDeterministic => true;
    public bool Enabled { get; set; } = true;
    public int Index { get; set; }
    public int RelativeWidth { get; set; }
    public int RelativeHeight { get; set; }
    public bool IsReorderable => false;
    public string? BindedEffectProvidingSystemID { get; set; }
    public Dictionary<string, object> Parameters { get; set; } = new();
    public IEffect WithParameters(Dictionary<string, object> parameters) => new SpeedVarianceToCustomEffect { Parameters = parameters };
    public IReadOnlyDictionary<string, object?> ComputeOutputs(EffectExecutionContext context)
    {
        context.CancellationToken.ThrowIfCancellationRequested();
        return SpeedVarianceToCustomProvider.Evaluate(key => context.Parameters.GetValueOrDefault(key));
    }
}

internal static class SpeedVarianceBindings
{
    public static bool IsConversion(IEffectProvider p) => p is CustomToSpeedVarianceProvider or SpeedVarianceToCustomProvider;
    public static bool IsSpeed(IEffectProvider p) => p.TypeOfEffect == EffectType.SpeedVarianceProvider;
    public static bool IsConsumed(IEffectProvider p, IReadOnlyDictionary<Guid, IEffectProvider> providers) => IsSpeed(p)
        && providers.Values.Where(c => c.Enabled).Any(c => c.EnumerateFieldBindings().Any(b =>
            EffectProviderOutputExtensions.TryParseOutputSourceId(b.Value, out var id, out var output)
            && id == p.Id && p.TryGetOutputField(output, out var field) && field.FieldType.BaseType() == EffectArgumentFieldType.SpeedVariance));
}
