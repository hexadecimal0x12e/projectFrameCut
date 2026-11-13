using projectFrameCut.Shared;

namespace projectFrameCut.Render.RenderAPIBase.EffectAndMixture;

public sealed class RenderOutputProvider : EffectProviderBase
{
    public RenderOutputProvider() => Name = "Render";
    public const string ProviderTypeName = "RenderOutput";
    public const string AudioKey = "Audio";
    public const string PositionKey = "Position";
    public const string ShouldRenderKey = "ShouldRender";
    public override string TypeName => ProviderTypeName;
    public override string FromPlugin => "projectFrameCut.Render.Plugins.InternalPluginBase";
    public override EffectType TypeOfEffect => EffectType.NotSpecified;
    public override bool IsDeterministic => true;
    public override EffectTarget Target => EffectTarget.InternalUse | EffectTarget.IsNotVisibleInEffectEditor | EffectTarget.IsNotVisibleInNewEffectSelector;
    public override IReadOnlyList<EffectStage> SupportedStages => [EffectStage.Render];
    protected override IReadOnlyDictionary<string, EffectArgumentFieldDescriptor> DefineInFields() => new Dictionary<string, EffectArgumentFieldDescriptor>
    {
        [AudioKey] = Field(AudioKey, EffectArgumentFieldType.AudioSample, ""),
        [PositionKey] = Field(PositionKey, EffectArgumentFieldType.SizeAndPosition, ""),
        [ShouldRenderKey] = Field(ShouldRenderKey, EffectArgumentFieldType.Boolean, "true"),
    };
    protected override IReadOnlyList<EffectArgumentFieldDescriptor> DefineFields() => [];
    protected override EffectArgumentFieldDescriptor DefineOutField() => new();
    public override IEffect[] Build() => [];
    public override IEffect[] Build<TPicture>() => [];

    public static RenderOutputProvider Ensure(IDictionary<Guid, IEffectProvider> providers)
    {
        if (providers.Values.OfType<RenderOutputProvider>().SingleOrDefault() is { } existing) return existing;
        var output = new RenderOutputProvider();
        output.SetFieldBinding(AudioKey, ValueProviderFrameContext.ClipAudioProviderId);
        output.SetFieldBinding(PositionKey, ValueProviderFrameContext.ClipPositionProviderId);
        providers.Add(output.Id, output);
        return output;
    }
}
