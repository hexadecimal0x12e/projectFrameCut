using projectFrameCut.Render.RenderAPIBase.ClipAndTrack;
using projectFrameCut.Render.RenderAPIBase.EffectAndMixture;
using projectFrameCut.Render.Effect;
using projectFrameCut.Drawing.Processing.Resizing;
using projectFrameCut.Drawing.Vector.ImportExport;
using projectFrameCut.Drawing.Vector;

namespace projectFrameCut.Render.ClipsAndTracks
{
    public class PhotoClip : IImmutableContentClip
    {
        public required Guid Id { get; init; }
        public required string Name { get; init; }
        public uint LayerIndex { get; init; } = 0;
        public uint SubLayerIndex { get; init; }
        public uint StartFrame { get; init; }
        public uint RelativeStartFrame { get; init; }
        public uint Duration { get; set; }
        public float FrameTime { get; init; }
        public float SecondPerFrameRatio { get => 1; init { } }

        public string? FilePath { get; set; } = string.Empty;
        public bool NeedFilePath => true;
        public Dictionary<string, object> ExtraData { get; set; }
        public bool ExtendToWholeDraft { get; set; }

        public bool Use16bpp = false;


        [System.Text.Json.Serialization.JsonIgnore]
        public IPicture? source { get; set; } = null;

        public ClipMode ClipType => ClipMode.PhotoClip;
        public string FromPlugin => projectFrameCut.Render.Plugin.InternalPluginBase.InternalPluginBaseID;
        public bool IsVector => false;

        public string BindedSoundTrack { get; init; } = "";



        public EffectAndMixtureJSONStructure[]? Effects { get; init; }
        public EffectProviderJSONStructure[]? EffectProviders { get; init; }
        public IEffect[]? EffectsInstances { get; set; }
        [System.Text.Json.Serialization.JsonIgnore]
        public IEffectProvider[]? EffectProvidersInstances { get; set; }
        public int TargetWidth { get; set; }
        public int TargetHeight { get; set; }
        public int TargetX { get; set; }
        public int TargetY { get; set; }
        public ISpeedVarianceProvider? SpeedVarianceProviderInstance { get; set; }
        public IMixture? MixtureInstance { get; set; }
        public ISourceReplacementEffect? AlternativeSource { get; set; }

        public PhotoClip()
        {
            (EffectsInstances, SpeedVarianceProviderInstance, MixtureInstance, AlternativeSource) = EffectHelper.GetEffectsInstancesSpeedVarianceAndMixture(Effects);
        }
        public IPicture GetContent(int targetWidth, int targetHeight, bool forceResize, IPicture.PicturePixelMode targetPPB) => source?.Resize(targetWidth, targetHeight, forceResize).ToBitPerPixel(targetPPB) ?? throw new NullReferenceException("Source is null. Please init it.");

        void IClip.ReInit(IPicture.PicturePixelMode targetPPB)
        {
            if (FilePath is null) throw new NullReferenceException($"PhotoClip {Id}'s source path is null.");
            source = targetPPB == 16 ? new Picture16bpp(FilePath) : new Picture8bpp(FilePath);
            source.CanBeDisposed = false;
            source.ProcessStack = new List<PictureProcessStack>
            {
                new PictureProcessStack
                {
                    Operator = GetType(),
                    OperationDisplayName = $"Created for PhotoClip {Name} ({Id})",
                    ProcessingFuncStackTrace = null,
                    Properties = new Dictionary<string, object>
                    {
                        { "Path", FilePath }
                    }
                }
            };
            (EffectsInstances, SpeedVarianceProviderInstance, MixtureInstance, AlternativeSource) = EffectHelper.GetEffectsInstancesSpeedVarianceAndMixture(Effects);

        }


        void IDisposable.Dispose()
        {
            source?.CanBeDisposed = false;
            source?.Dispose(true);
        }

    }

}
