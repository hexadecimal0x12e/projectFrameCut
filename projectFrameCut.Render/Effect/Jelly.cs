using projectFrameCut.Render.Plugin;
using projectFrameCut.Render.RenderAPIBase.ClipAndTrack;
using projectFrameCut.Render.RenderAPIBase.EffectAndMixture;

namespace projectFrameCut.Render.Effect;

public class JellyEffect : IContinuousClipPositionProvider
{
    private static readonly (double Progress, double Deformation)[] BounceSteps =
    [
        (0, 0), (0.12, 1), (0.30, -0.8), (0.48, 0.35),
        (0.65, -0.15), (0.82, 0.05), (1, 0)
    ];

    public string FromPlugin => InternalPluginBase.InternalPluginBaseID;
    public string TypeName => "Jelly";
    public string Name { get; set; } = "Jelly";
    public string Id { get; set; } = string.Empty;
    public int Index { get; set; }
    public bool Enabled { get; set; } = true;
    public bool IsReorderable => true;
    public string? BindedEffectProvidingSystemID { get; set; }
    public int RelativeWidth { get; set; } = -1;
    public int RelativeHeight { get; set; } = -1;
    public bool PreserveAspectRatio => false;
    public int PeriodFrames { get; init; } = 30;
    public float Strength { get; init; } = 0.3f;
    public Dictionary<string, object> Parameters { get; set; } = new();

    public IEffect WithParameters(Dictionary<string, object> parameters)
    {
        ArgumentNullException.ThrowIfNull(parameters);
        return new JellyEffect
        {
            PeriodFrames = PeriodFrames,
            Strength = Strength,
            RelativeWidth = RelativeWidth,
            RelativeHeight = RelativeHeight,
            Parameters = new(parameters)
        };
    }

    public void Initialize()
    {
    }

    public ClipPositionTuple GetPosition(IClip source, uint index, int targetWidth, int targetHeight)
        => GetPosition(source, index, targetWidth, targetHeight,
            RelativeWidth > 0 ? RelativeWidth : targetWidth,
            RelativeHeight > 0 ? RelativeHeight : targetHeight);

    public ClipPositionTuple GetPosition(IClip source, uint index, int targetWidth, int targetHeight, int relativeWidth, int relativeHeight)
    {
        ArgumentNullException.ThrowIfNull(source);
        int period = Math.Max(6, DynamicParam.Resolve(Parameters.GetValueOrDefault(nameof(PeriodFrames)), PeriodFrames));
        float strength = DynamicParam.Resolve(Parameters.GetValueOrDefault(nameof(Strength)), Strength);
        if (!float.IsFinite(strength)) strength = 0.3f;
        double progress = (index > source.StartFrame ? index - source.StartFrame : 0) % (uint)period / (double)period;
        double deformation = GetDeformation(progress) * Math.Clamp(strength, 0f, 0.95f);
        if (deformation == 0) return new ClipPositionTuple(0, 0, 0, 0, true);

        relativeWidth = Math.Max(1, relativeWidth);
        relativeHeight = Math.Max(1, relativeHeight);
        double scaleX = Math.Max(1, targetWidth) / (double)relativeWidth;
        double scaleY = Math.Max(1, targetHeight) / (double)relativeHeight;
        int width = Math.Max(1, (int)Math.Round((source.TargetWidth > 0 ? source.TargetWidth : relativeWidth) * scaleX, MidpointRounding.AwayFromZero));
        int height = Math.Max(1, (int)Math.Round((source.TargetHeight > 0 ? source.TargetHeight : relativeHeight) * scaleY, MidpointRounding.AwayFromZero));
        int dw = Math.Max(1, (int)Math.Round(width * (1 - deformation), MidpointRounding.AwayFromZero)) - width;
        int dh = Math.Max(1, (int)Math.Round(height * (1 + deformation), MidpointRounding.AwayFromZero)) - height;

        return new ClipPositionTuple(
            (int)Math.Round(-dw / (2 * scaleX), MidpointRounding.AwayFromZero),
            (int)Math.Round(-dh / (2 * scaleY), MidpointRounding.AwayFromZero),
            dw, dh, true);
    }

    private static double GetDeformation(double progress)
    {
        for (int i = 1; i < BounceSteps.Length; i++)
        {
            if (progress > BounceSteps[i].Progress) continue;
            var from = BounceSteps[i - 1];
            var to = BounceSteps[i];
            double t = (progress - from.Progress) / (to.Progress - from.Progress);
            t = t * t * (3 - 2 * t);
            return from.Deformation + (to.Deformation - from.Deformation) * t;
        }
        return 0;
    }
}

public class JellyEffectProvider : EffectProviderBase
{
    public JellyEffectProvider()
    {
        Name = "Jelly";
        SetField(nameof(JellyEffect.PeriodFrames), 30);
        SetField(nameof(JellyEffect.Strength), 0.3f);
    }

    public override string TypeName => "Jelly";
    public override EffectType TypeOfEffect => EffectType.ContinuousClipPositionProvider;
    public override EffectTarget Target => EffectTarget.Video;
    public override string FromPlugin => InternalPluginBase.InternalPluginBaseID;

    protected override IReadOnlyList<EffectArgumentFieldDescriptor> DefineFields()
    {
        return
        [
            Field(nameof(JellyEffect.PeriodFrames), EffectArgumentFieldType.Integer, "30", min: "6"),
            Field(nameof(JellyEffect.Strength), EffectArgumentFieldType.Numeric, "0.3", min: "0", max: "0.95")
        ];
    }
}
