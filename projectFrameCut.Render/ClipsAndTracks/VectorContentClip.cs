using projectFrameCut.Drawing.Base;
using projectFrameCut.Drawing.Vector;
using projectFrameCut.Drawing.Vector.ImportExport;
using projectFrameCut.Render.Effect;
using projectFrameCut.Render.Plugin;
using projectFrameCut.Render.RenderAPIBase.ClipAndTrack;
using projectFrameCut.Render.RenderAPIBase.EffectAndMixture;
using projectFrameCut.Render.RenderAPIBase.Plugins;
using projectFrameCut.Render.RenderAPIBase.VectorContent;
using projectFrameCut.Render.VectorContent.Components;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace projectFrameCut.Render.ClipsAndTracks
{
    public class VectorCanvasClip : IVectorContentClip
    {
        // ── IClip required properties ──────────────────────

        public string FromPlugin => projectFrameCut.Render.Plugin.InternalPluginBase.InternalPluginBaseID;

        public virtual ClipMode ClipType => ClipMode.VectorCanvasClip;

        public required Guid Id { get; init; }
        public required string Name { get; init; }
        public string BindedSoundTrack { get; init; } = "";
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

        [JsonIgnore]
        public ISpeedVarianceProvider? SpeedVarianceProviderInstance { get; set; }

        [JsonIgnore]
        public IMixture? MixtureInstance { get; set; }

        public bool ExtendToWholeDraft { get; set; }

        public EffectAndMixtureJSONStructure[]? Effects { get; init; }
        public EffectProviderJSONStructure[]? EffectProviders { get; init; }

        [JsonIgnore]
        public IEffect[]? EffectsInstances { get; set; }
        [JsonIgnore]
        public IEffectProvider[]? EffectProvidersInstances { get; set; }

        public string? FilePath { get; set; }
        public virtual bool NeedFilePath => false;

        public Dictionary<string, object> ExtraData { get; set; } = new();

        public AntiAliasMode? ClipAntiAliasMode { get; set; }

        // ── Vector-specific state ──────────────────────────

        /// <summary>
        /// The vector components that make up the canvas.
        /// </summary>
        [JsonIgnore]
        public List<IVectorComponent> Components { get; set; } = new();

        public ISourceReplacementEffect? AlternativeSource { get; set; }

        public virtual VectorPicture GetVectorPictureRelativeToStartPointOfSource(
            uint frameIndex, int requiredWidth, int requiredHeight)
        {
            var picture = BuildVectorPicture(frameIndex);
            int width = Math.Max(1, TargetWidth > 0 ? TargetWidth : requiredWidth);
            int height = Math.Max(1, TargetHeight > 0 ? TargetHeight : requiredHeight);
            // Resolve component coordinates in the clip's own canvas before output scaling.
            return new VectorPicture
            {
                Elements = picture.Elements.Select(e => (VectorCanvasElement)new VectorContent.VectorViewportElement(
                    e, width, height, 0, 0, width, height)).ToList()
            };
        }

        private VectorPicture BuildVectorPicture(uint frameIndex)
        {
            float progress = CalculateProgress(frameIndex);
            var result = new VectorPicture();
            foreach (var component in Components)
            {
                result.Elements.AddRange(VectorContent.VectorComponentProcessing.Compute(component,
                    (EffectsInstances ?? []).Where(e => e.Enabled).OrderBy(e => e.Index).OfType<IVectorComponentEffect>(),
                    frameIndex, progress));
            }
            return result;
        }

        // ── IClip frame methods ────────────────────────────

        public virtual IPicture GetFrameRelativeToStartPointOfSource(
            uint frameIndex, int requiredWidth, int requiredHeight,
            IPicture.PicturePixelMode targetPPB)
        {
            requiredWidth = Math.Max(1, requiredWidth);
            requiredHeight = Math.Max(1, requiredHeight);
            var vectorPicture = GetVectorPictureRelativeToStartPointOfSource(
                frameIndex, requiredWidth, requiredHeight);

            if (this is VectorComponentClip componentClip)
            {
                var viewport = componentClip.ReadViewport();
                vectorPicture = VectorPictureRasterization.ScaleStrokes(vectorPicture,
                    Math.Min((float)requiredWidth / viewport[2], (float)requiredHeight / viewport[3]));
            }

            var aa = ClipAntiAliasMode ?? IVectorContentClip.GlobalDefaultAntiAliasMode;
            var rasterizer = IVectorContentClip.GlobalDefaultRasterizer;

            IPicture raster = rasterizer.Convert(
                vectorPicture,
                requiredWidth,
                requiredHeight,
                transparentBackground: true,
                aaMode: aa);

            if (raster.BitPerPixel == targetPPB) return raster;
            try { return raster.ToBitPerPixel(targetPPB); }
            finally { raster.Dispose(); }
        }

        // ── Lifecycle ──────────────────────────────────────

        public virtual void ReInit(IPicture.PicturePixelMode targetPPB)
        {
            Components = DeserializeComponents();

            EffectHelper.ResolveClipEffects(this);
        }

        public virtual void Dispose() { }

        // ── Progress calculation ───────────────────────────

        /// <summary>
        /// Maps a zero-based source frame index to a normalised progress [0…1].
        /// When <see cref="Duration"/> ≤ 1 the result is always 0 (single-frame
        /// clip cannot animate).
        /// </summary>
        private float CalculateProgress(uint frameIndex)
        {
            if (Duration <= 1)
                return 0f;

            return Math.Clamp(frameIndex / (float)(Duration - 1), 0f, 1f);
        }

        // ── Component serialisation via ExtraData ────────────

        private const string ComponentsDataKey = "VectorCanvas.Components";

        private List<IVectorComponent> DeserializeComponents()
        {
            try { return VectorContent.VectorComponentSerializer.Read(ExtraData); }
            catch (Exception ex)
            {
                Log(ex, $"Restore vector components of {Id}", this);
                throw;
            }
        }

        /// <summary>
        /// Serialises the <see cref="Components"/> list into <see cref="ExtraData"/>
        /// so they persist with the clip's metadata.
        /// </summary>
        public void SerializeComponents(List<IVectorComponent> components)
        {
            ExtraData ??= new();
            ExtraData[ComponentsDataKey] = VectorContent.VectorComponentSerializer.Serialize(components);
        }

    }

    // ── VectorPhotoClip — immutable vector clip from SVG file ──

    public class VectorPhotoClip : IImmutableVectorContentClip
    {
        public AntiAliasMode? ClipAntiAliasMode { get; set; }

        public ClipMode ClipType => ClipMode.PhotoClip;
        public string FromPlugin => projectFrameCut.Render.Plugin.InternalPluginBase.InternalPluginBaseID;
        public bool IsVector => true;

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
        public bool ExtendToWholeDraft { get; set; }
        public EffectAndMixtureJSONStructure[]? Effects { get; init; }
        public EffectProviderJSONStructure[]? EffectProviders { get; init; }
        public IEffect[]? EffectsInstances { get; set; }
        [System.Text.Json.Serialization.JsonIgnore]
        public IEffectProvider[]? EffectProvidersInstances { get; set; }
        public string? FilePath { get; set; }

        public bool NeedFilePath => true;

        public Dictionary<string, object> ExtraData { get; set; } = new();

        [System.Text.Json.Serialization.JsonIgnore]
        public VectorPicture? Picture { get; set; }
        public ISourceReplacementEffect? AlternativeSource { get; set; }

        public void Dispose()
        {
            Picture = null;
        }

        public VectorPicture GetVectorPicture(int requiredWidth, int requiredHeight) => Picture ?? throw new InvalidOperationException("Vector picture is not initialized.");

        public void ReInit(IPicture.PicturePixelMode targetPPB)
        {
            if (FilePath is null) throw new NullReferenceException($"PhotoClip {Id}'s source path is null.");
            Picture = SVGToVectorElement.ImportFromFile(FilePath);
           EffectHelper.ResolveClipEffects(this);
        }
    }
}
