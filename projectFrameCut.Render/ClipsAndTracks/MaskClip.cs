using projectFrameCut.Render.Effect;
using projectFrameCut.Render.Plugin;
using projectFrameCut.Render.RenderAPIBase.ClipAndTrack;
using projectFrameCut.Render.RenderAPIBase.EffectAndMixture;
using projectFrameCut.Render.Rendering;
using projectFrameCut.Render.Sources;
using System.Text.Json.Serialization;

namespace projectFrameCut.Render.ClipsAndTracks;

public sealed class MaskClip : IRotatableClip
{
    public const string SourceTypeKey = "MaskSourceType";
    public const string ArgumentPrefix = "Source.";

    public required Guid Id { get; init; }
    public required string Name { get; init; }
    public uint LayerIndex { get; init; }
    public uint SubLayerIndex { get; init; }
    public uint StartFrame { get; init; }
    public uint RelativeStartFrame { get; init; }
    public uint Duration { get; set; }
    public float FrameTime { get; init; }
    public string BindedSoundTrack { get; init; } = "";
    public ClipMode ClipType => ClipMode.MaskClip;
    public string TypeName => nameof(MaskClip);
    public string FromPlugin => InternalPluginBase.InternalPluginBaseID;
    public bool NeedFilePath => false;
    public string? FilePath { get; set; }
    public int TargetWidth { get; set; }
    public int TargetHeight { get; set; }
    public int TargetX { get; set; }
    public int TargetY { get; set; }
    public float Rotation { get; set; }
    public int StartingX { get; set; }
    public int StartingY { get; set; }
    public bool ExtendToWholeDraft { get; set; }
    public Dictionary<string, object> ExtraData { get; set; } = new();
    public EffectAndMixtureJSONStructure[]? Effects { get; init; }
    public EffectProviderJSONStructure[]? EffectProviders { get; init; }
    [JsonIgnore] public IEffect[]? EffectsInstances { get; set; }
    [JsonIgnore] public IEffectProvider[]? EffectProvidersInstances { get; set; }
    [JsonIgnore] public ISpeedVarianceProvider? SpeedVarianceProviderInstance { get; set; }
    [JsonIgnore] public IMixture? MixtureInstance { get; set; }
    [JsonIgnore] public ISourceReplacementEffect? AlternativeSource { get; set; }
    [JsonIgnore] public Dictionary<string, object> SourceArguments { get; private set; } = new();
    [JsonIgnore] public string SourceType => ExtraData.GetValueOrDefault(SourceTypeKey)?.ToString() ?? "Blur";

    [JsonIgnore]
    public IReadOnlyDictionary<string, ClipArgumentFieldDescriptor> ArgumentFields
    {
        get
        {
            var fields = new Dictionary<string, ClipArgumentFieldDescriptor>(ClipArgumentHandler.DefaultFields);
            foreach (var f in MaskSources.Create(SourceType).Fields)
            {
                string id = f.Id;
                fields.Add(ArgumentPrefix + id, new()
                {
                    Id = ArgumentPrefix + id, FieldType = f.FieldType, DefaultValue = f.DefaultValue,
                    MinValue = f.MinValue, MaxValue = f.MaxValue, PresetOptions = f.PresetOptions,
                    Remarks = f.Remarks,
                    ReadValue = c => ((MaskClip)c).SourceArguments.GetValueOrDefault(id) ??
                        MaskSources.Create(((MaskClip)c).SourceType).ReadArgument(id, null)
                });
            }
            return fields;
        }
    }

    public IClip HandleArguments(IReadOnlyDictionary<string, object> arguments)
    {
        var copy = (MaskClip)ClipArgumentHandler.Handle(this,
            arguments.Where(p => !p.Key.StartsWith(ArgumentPrefix, StringComparison.Ordinal)).ToDictionary());
        copy.SourceArguments = new(SourceArguments);
        var source = MaskSources.Create(SourceType);
        foreach (var (key, value) in arguments.Where(p => p.Key.StartsWith(ArgumentPrefix, StringComparison.Ordinal)))
            copy.SourceArguments[key[ArgumentPrefix.Length..]] = source.ReadArgument(key[ArgumentPrefix.Length..], value);
        return copy;
    }

    public IPicture GetFrameRelativeToStartPointOfSource(uint frameIndex, int requiredWidth, int requiredHeight,
        IPicture.PicturePixelMode targetPPB) => GetUnrotatedFrame(frameIndex, requiredWidth, requiredHeight, targetPPB);

    public IPicture GetUnrotatedFrame(uint frameIndex, int width, int height, IPicture.PicturePixelMode pixelMode)
    {
        var context = MaskFrameContext.Current ?? throw new InvalidOperationException("A mask source requires a timeline render scope.");
        uint frame = ClipArgumentBinding.GetFrameIndex(this,
            ValueProviderFrameContext.Get(ValueProviderFrameContext.BuiltInFrameProviderId) is float f && f >= 0 && f < uint.MaxValue
                ? (uint)f : context.Frame);
        using var values = ValueProviderFrameContext.PushFrame(frame,
            Math.Clamp(((float)frame - StartFrame) / Math.Max(1u, ((IClip)this).GetEffectiveDuration()), 0, 1), true, context.Token);
        using var backdrop = context.ReadBelow(this);
        var processed = MaskSources.Create(SourceType).Process(backdrop, SourceArguments,
            Math.Min((float)context.Width / context.ProjectWidth, (float)context.Height / context.ProjectHeight), context.Token);
        using var ownedProcessed = ReferenceEquals(processed, backdrop) ? null : processed;
        var result = MaskPictureSampler.Extract(processed, this, context, frame, width, height);
        try
        {
            var converted = result.ToBitPerPixel(pixelMode);
            if (!ReferenceEquals(result, converted)) result.Dispose();
            return converted;
        }
        catch { result.Dispose(); throw; }
    }

    public void ReInit(IPicture.PicturePixelMode pixelMode)
    {
        _ = MaskSources.Create(SourceType);
        EffectHelper.ResolveClipEffects(this);
    }

    public void Dispose() => EffectHelper.ReleaseClipEffects(this);
}
