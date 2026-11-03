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
    [JsonIgnore] public IReadOnlyList<EffectStage> SupportedStages => EffectStages.Picture;
    [JsonIgnore] public IEffectStagePlan? EffectStagePlan { get; set; }
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
    public float Opacity { get; set; } = 1;
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

    [JsonIgnore] public IReadOnlyDictionary<string, ClipArgumentFieldDescriptor> ArgumentFields => SourceFields(ExtraData);

    public IClip HandleArguments(IReadOnlyDictionary<string, object> arguments)
    {
        var copy = (MaskClip)ClipArgumentHandler.Copy(this);
        copy.SourceArguments = new(SourceArguments);
        var source = MaskSources.Create(SourceType);
        foreach (var (key, value) in arguments)
        {
            if (!key.StartsWith(ArgumentPrefix, StringComparison.Ordinal)) throw new ArgumentException($"Unknown source property '{key}'.");
            copy.SourceArguments[key[ArgumentPrefix.Length..]] = source.ReadArgument(key[ArgumentPrefix.Length..], value);
        }
        return copy;
    }

    public const string SourceArgumentKeyPrefix = "__MaskSource_";

    public static IReadOnlyDictionary<string, ClipArgumentFieldDescriptor> SourceFields(Dictionary<string, object> data)
    {
        var source = MaskSources.Create(data.GetValueOrDefault(SourceTypeKey)?.ToString() ?? "Blur");
        return source.Fields.ToDictionary(f => ArgumentPrefix + f.Id, f => new ClipArgumentFieldDescriptor
        {
            Id = ArgumentPrefix + f.Id, FieldType = f.FieldType | EffectArgumentFieldType.CannotBeDynamic,
            DefaultValue = f.DefaultValue, MinValue = f.MinValue, MaxValue = f.MaxValue,
            PresetOptions = f.PresetOptions, Remarks = f.Remarks,
            ReadValue = _ => source.ReadArgument(f.Id, data.GetValueOrDefault(SourceArgumentKeyPrefix + f.Id)),
        });
    }

    public IPicture GetFrameRelativeToStartPointOfSource(uint frameIndex, int requiredWidth, int requiredHeight,
        IPicture.PicturePixelMode targetPPB) => GetUnrotatedFrame(frameIndex, requiredWidth, requiredHeight, targetPPB);

    public IPicture GetUnrotatedFrame(uint frameIndex, int width, int height, IPicture.PicturePixelMode pixelMode)
    {
        if (FrameHashContext.Current is { } hashes)
            return hashes.ReadPicture((typeof(MaskClip), Id, frameIndex, width, height, pixelMode.Value,
                ClipArgumentBinding.GetFrameIndex(this, MaskFrameContext.Current?.Frame ?? frameIndex)),
                () => RenderMask(frameIndex, width, height, pixelMode))!;
        return RenderMask(frameIndex, width, height, pixelMode);
    }

    private IPicture RenderMask(uint frameIndex, int width, int height, IPicture.PicturePixelMode pixelMode)
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
        var source = MaskSources.Create(SourceType);
        SourceArguments = source.Fields.ToDictionary(f => f.Id, f => source.ReadArgument(f.Id,
            ExtraData.GetValueOrDefault(SourceArgumentKeyPrefix + f.Id)));
        EffectHelper.ResolveClipEffects(this);
    }

    public void Dispose() => EffectHelper.ReleaseClipEffects(this);
}
