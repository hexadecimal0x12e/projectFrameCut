using projectFrameCut.Drawing.Base;
using projectFrameCut.Render.Contracts;
using projectFrameCut.Render.PluginIsolation;
using projectFrameCut.Render.RenderAPIBase.ClipAndTrack;
using projectFrameCut.Render.RenderAPIBase.EffectAndMixture;
using projectFrameCut.Shared;

namespace projectFrameCut.Render.PluginIsolation;

internal sealed class SnapshotClip : IClip
{
    public SnapshotClip(IsolationClipSnapshot source)
    {
        FromPlugin = source.FromPlugin;
        ClipType = (ClipMode)source.ClipType;
        TypeName = source.TypeName;
        Id = Guid.Parse(source.Id);
        Name = source.Name;
        BindedSoundTrack = source.BoundSoundTrack;
        LayerIndex = source.LayerIndex;
        SubLayerIndex = source.SubLayerIndex;
        StartFrame = source.StartFrame;
        RelativeStartFrame = source.RelativeStartFrame;
        Duration = source.Duration;
        TargetWidth = source.TargetWidth;
        TargetHeight = source.TargetHeight;
        TargetX = source.TargetX;
        TargetY = source.TargetY;
        StartingX = source.StartingX;
        StartingY = source.StartingY;
        FrameTime = source.FrameTime;
        ExtendToWholeDraft = source.ExtendToWholeDraft;
        FilePath = string.IsNullOrWhiteSpace(source.FileName) ? null : source.FileName;
        NeedFilePath = source.NeedFilePath;
        ExtraData = source.Metadata.ToDictionary(x => x.Key, x => IsolationValueConverter.ToObject(x.Value)!);
    }

    public string FromPlugin { get; }
    public ClipMode ClipType { get; }
    public string TypeName { get; }
    public Guid Id { get; init; }
    public string Name { get; init; }
    public string BindedSoundTrack { get; init; }
    public uint LayerIndex { get; init; }
    public uint SubLayerIndex { get; init; }
    public uint StartFrame { get; init; }
    public uint RelativeStartFrame { get; init; }
    public uint Duration { get; set; }
    public int TargetWidth { get; set; }
    public int TargetHeight { get; set; }
    public int TargetX { get; set; }
    public int TargetY { get; set; }
    public int StartingX { get; set; }
    public int StartingY { get; set; }
    public float FrameTime { get; init; }
    public ISpeedVarianceProvider? SpeedVarianceProviderInstance { get; set; }
    public IMixture? MixtureInstance { get; set; }
    public ISourceReplacementEffect? AlternativeSource { get; set; }
    public bool ExtendToWholeDraft { get; set; }
    public EffectAndMixtureJSONStructure[]? Effects { get; init; }
    public EffectProviderJSONStructure[]? EffectProviders { get; init; }
    public IEffect[]? EffectsInstances { get; set; }
    public IEffectProvider[]? EffectProvidersInstances { get; set; }
    public string? FilePath { get; set; }
    public bool NeedFilePath { get; }
    public Dictionary<string, object> ExtraData { get; set; }

    public IPicture GetFrameRelativeToStartPointOfSource(uint frameIndex, int requiredWidth, int requiredHeight, IPicture.PicturePixelMode targetPPB)
        => throw new NotSupportedException("A sandbox clip snapshot cannot decode frames. The source picture is transferred separately.");

    public void ReInit(IPicture.PicturePixelMode targetPPB) { }
    public void Dispose() { }
}
