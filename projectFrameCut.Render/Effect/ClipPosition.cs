using projectFrameCut.Render.Plugin;
using projectFrameCut.Render.RenderAPIBase.ClipAndTrack;
using projectFrameCut.Render.RenderAPIBase.EffectAndMixture;

namespace projectFrameCut.Render.Effect;

public class ClipPositionEffect : IClipPositionProvider
{
    public bool IsDeterministic => true;
    public bool Enabled { get; set; } = true;
    public int Index { get; set; }
    public string Name { get; set; } = "ClipPosition";
    public string Id { get; set; } = string.Empty;
    public string? BindedEffectProvidingSystemID { get; set; }
    public string FromPlugin => InternalPluginBase.InternalPluginBaseID;
    public string TypeName => "ClipPosition";
    public bool IsReorderable => true;
    public bool PreserveAspectRatio => false;
    public Dictionary<string, object> Parameters { get; set; } = new();

    public IEffect WithParameters(Dictionary<string, object> parameters) => new ClipPositionEffect { Parameters = new(parameters) };

    public ClipPositionTuple GetPosition(IClip source, int targetWidth, int targetHeight)
    {
        float opacity = DynamicParam.Resolve(Parameters.GetValueOrDefault("Opacity"), 1f);
        return new(
            DynamicParam.Resolve(Parameters.GetValueOrDefault("TargetX"), 0),
            DynamicParam.Resolve(Parameters.GetValueOrDefault("TargetY"), 0),
            DynamicParam.Resolve(Parameters.GetValueOrDefault("TargetWidth"), 1920),
            DynamicParam.Resolve(Parameters.GetValueOrDefault("TargetHeight"), 1080),
            DynamicParam.Resolve(Parameters.GetValueOrDefault("IsDelta"), true),
            DynamicParam.Resolve(Parameters.GetValueOrDefault("Rotation"), 0f),
            float.IsNaN(opacity) ? 1 : Math.Clamp(opacity, 0, 1));
    }
}

public class ClipPositionProvider : EffectProviderBase
{
    public ClipPositionProvider() => Name = "ClipPosition";
    public override bool IsDeterministic => true;
    public override string TypeName => "ClipPosition";
    public override string FromPlugin => InternalPluginBase.InternalPluginBaseID;
    public override EffectType TypeOfEffect => EffectType.ClipPositionProvider;
    public override EffectTarget Target => EffectTarget.ClipPosition;
    public override IReadOnlyList<EffectStage> SupportedStages => [EffectStage.Render];

    protected override IReadOnlyList<EffectArgumentFieldDescriptor> DefineFields() =>
    [
        Field("TargetX", EffectArgumentFieldType.Integer, "0"),
        Field("TargetY", EffectArgumentFieldType.Integer, "0"),
        Field("TargetWidth", EffectArgumentFieldType.Integer, "1920"),
        Field("TargetHeight", EffectArgumentFieldType.Integer, "1080"),
        Field("Rotation", EffectArgumentFieldType.Numeric, "0"),
        Field("Opacity", EffectArgumentFieldType.Numeric, "1", min: "0", max: "1"),
        Field("IsDelta", EffectArgumentFieldType.Boolean, "true")
    ];
}

public sealed class SplitClipPositionEffect : IMultipleOutputEffect
{
    public bool IsDeterministic => true;
    public bool Enabled { get; set; } = true;
    public int Index { get; set; }
    public string Name { get; set; } = "Split Clip Position";
    public string Id { get; set; } = string.Empty;
    public string? BindedEffectProvidingSystemID { get; set; }
    public string FromPlugin => InternalPluginBase.InternalPluginBaseID;
    public string TypeName => "SplitClipPosition";
    public EffectType TypeOfEffect => EffectType.NormalEffect;
    public EffectImplementType ImplementType => EffectImplementType.NotSpecified;
    public bool IsReorderable => true;
    public int RelativeWidth { get; set; }
    public int RelativeHeight { get; set; }
    public Dictionary<string, object> Parameters { get; set; } = new();

    public IEffect WithParameters(Dictionary<string, object> parameters) => new SplitClipPositionEffect { Parameters = new(parameters) };

    public IReadOnlyDictionary<string, object?> ComputeOutputs(EffectExecutionContext context)
    {
        context.CancellationToken.ThrowIfCancellationRequested();
        if (context.Input is not ClipPositionTuple position)
            throw new ArgumentException("The primary input must be a ClipPositionTuple.");

        return new Dictionary<string, object?>
        {
            ["TargetX"] = position.TargetX,
            ["TargetY"] = position.TargetY,
            ["TargetWidth"] = position.TargetWidth,
            ["TargetHeight"] = position.TargetHeight,
            ["Rotation"] = position.Rotation,
            ["Opacity"] = position.Opacity,
            ["IsDelta"] = position.IsDelta,
        };
    }
}

public sealed class SplitClipPositionProvider : EffectProviderBase, IMultipleOutputEffectProvider
{
    public SplitClipPositionProvider() => Name = "Split Clip Position";
    public override bool IsDeterministic => true;
    public override string TypeName => "SplitClipPosition";
    public override string FromPlugin => InternalPluginBase.InternalPluginBaseID;
    public override EffectType TypeOfEffect => EffectType.NormalEffect;
    public override EffectTarget Target => EffectTarget.Pipeline | EffectTarget.ValueProvider;
    public override IReadOnlyList<EffectStage> SupportedStages => [EffectStage.Render];

    protected override IReadOnlyList<EffectArgumentFieldDescriptor> DefineFields() => [];

    protected override IReadOnlyDictionary<string, EffectArgumentFieldDescriptor> DefineInFields() =>
        new Dictionary<string, EffectArgumentFieldDescriptor>
        {
            [PrimaryInputAnchorKey] = Field(PrimaryInputAnchorKey, EffectArgumentFieldType.SizeAndPosition, ""),
        };

    public IReadOnlyDictionary<string, EffectArgumentFieldDescriptor> OutFields =>
        new Dictionary<string, EffectArgumentFieldDescriptor>
        {
            ["TargetX"] = Field("TargetX", EffectArgumentFieldType.Integer, "0"),
            ["TargetY"] = Field("TargetY", EffectArgumentFieldType.Integer, "0"),
            ["TargetWidth"] = Field("TargetWidth", EffectArgumentFieldType.Integer, "1920"),
            ["TargetHeight"] = Field("TargetHeight", EffectArgumentFieldType.Integer, "1080"),
            ["Rotation"] = Field("Rotation", EffectArgumentFieldType.Numeric, "0"),
            ["Opacity"] = Field("Opacity", EffectArgumentFieldType.Numeric, "1"),
            ["IsDelta"] = Field("IsDelta", EffectArgumentFieldType.Boolean, "true"),
        };
}
