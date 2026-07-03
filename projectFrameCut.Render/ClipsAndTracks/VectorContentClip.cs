using projectFrameCut.Drawing.Base;
using projectFrameCut.Drawing.Vector;
using projectFrameCut.Drawing.Vector.ImportExport;
using projectFrameCut.Render.Effect;
using projectFrameCut.Render.Plugin;
using projectFrameCut.Render.RenderAPIBase.ClipAndTrack;
using projectFrameCut.Render.RenderAPIBase.EffectAndMixture;
using projectFrameCut.Render.RenderAPIBase.Plugins;
using projectFrameCut.Render.RenderAPIBase.VectorContent;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace projectFrameCut.Render.ClipsAndTracks
{
    /// <summary>
    /// A mutable vector-content clip whose <see cref="VectorPicture"/> can be
    /// animated per frame via an associated <see cref="Animation.vector animation"/>.
    /// The vector animation defines keyframe-driven animation of element-level
    /// properties (position, rotation) and segment-level appearance (fill/stroke
    /// opacity).
    /// </summary>
    public class VectorCanvasClip : IVectorContentClip
    {
        // ── IClip required properties ──────────────────────

        public string FromPlugin => projectFrameCut.Render.Plugin.InternalPluginBase.InternalPluginBaseID;

        public ClipMode ClipType => ClipMode.VectorCanvasClip;

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
        public float FrameTime { get; init; }

        [JsonIgnore]
        public ISpeedVarianceProvider? SpeedVarianceProviderInstance { get; set; }

        [JsonIgnore]
        public IMixture? MixtureInstance { get; set; }

        public bool ExtendToWholeDraft { get; set; }

        public EffectAndMixtureJSONStructure[]? Effects { get; init; }

        [JsonIgnore]
        public IEffect[]? EffectsInstances { get; set; }

        public string? FilePath { get; set; }
        public bool NeedFilePath => true;

        public Dictionary<string, object> ExtraData { get; set; } = new();

        public AntiAliasMode? ClipAntiAliasMode { get; set; }

        // ── Vector-specific state ──────────────────────────

        [JsonIgnore]
        public List<IVectorComponent> Components { get; set; } = new();

        /// <summary>
        /// Source SVG vector picture for legacy compatibility.
        /// </summary>
        [JsonIgnore]
        public VectorPicture? SourcePicture { get; set; }

        public ISourceReplacementEffect? AlternativeSource { get; set; }

        // ── Core: animated vector picture per frame ─────────

        /// <summary>
        /// Produces a <see cref="VectorPicture"/> for the given <paramref name="frameIndex"/>
        /// by evaluating the <see cref="AnimationPayload"/> (if SVG source present)
        /// and/or composing <see cref="Components"/> with their per-component vector animations.
        /// </summary>
        public VectorPicture GetVectorPictureRelativeToStartPointOfSource(
            uint frameIndex, int requiredWidth, int requiredHeight)
        {
            var result = new VectorPicture();
            float progress = CalculateProgress(frameIndex);
            foreach (var component in Components)
            {
                result.Elements.AddRange(component.ComputeAll(progress));
            }

            return result;
        }

        // ── IClip frame methods ────────────────────────────

        public IPicture GetFrameRelativeToStartPointOfSource(
            uint frameIndex, int requiredWidth, int requiredHeight,
            bool forceResize, IPicture.PicturePixelMode targetPPB)
        {
            var vectorPicture = GetVectorPictureRelativeToStartPointOfSource(
                frameIndex, requiredWidth, requiredHeight);

            var aa = ClipAntiAliasMode ?? IVectorContentClip.GlobalDefaultAntiAliasMode;
            var rasterizer = IVectorContentClip.GlobalDefaultRasterizer;

            IPicture raster = rasterizer.Convert(
                vectorPicture,
                requiredWidth,
                requiredHeight,
                transparentBackground: true,
                aaMode: aa);

            return raster.ToBitPerPixel(targetPPB);
        }

        // ── Lifecycle ──────────────────────────────────────

        public void ReInit(IPicture.PicturePixelMode targetPPB)
        {
            Components = DeserializeComponents();

            (EffectsInstances, SpeedVarianceProviderInstance, MixtureInstance, AlternativeSource) =
                EffectHelper.GetEffectsInstancesSpeedVarianceAndMixture(Effects);
        }

        public void Dispose()
        {
        }

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

        private static readonly JsonSerializerOptions _componentsJsonOptions = new()
        {
            PropertyNameCaseInsensitive = true,
            PropertyNamingPolicy = null, // Preserve PascalCase to match C# property names
        };

        private List<IVectorComponent> DeserializeComponents()
        {
            if (ExtraData is null || !ExtraData.TryGetValue(ComponentsDataKey, out var raw))
            {
                Log($"No ExtraData found for {Name}/{Id}'s Components. Returning empty list.", "warn");
                return new();
            }

            string? json = raw switch
            {
                string s when !string.IsNullOrEmpty(s) => s,
                JsonElement { ValueKind: JsonValueKind.String } je => je.GetString(),
                JsonElement je => je.GetRawText(),
                _ => null,
            };

            if (string.IsNullOrEmpty(json))
                return new();

            try
            {
                using var doc = JsonDocument.Parse(json);
                if (doc.RootElement.ValueKind != JsonValueKind.Array)
                {
                    return new();
                }

                var result = new List<IVectorComponent>();
                foreach (var element in doc.RootElement.EnumerateArray())
                {
                    var pluginId = element.TryGetProperty("FromPlugin", out var pluginElement)
                        ? pluginElement.GetString()
                        : Plugin.InternalPluginBase.InternalPluginBaseID;
                    var resolvedPluginId = string.IsNullOrWhiteSpace(pluginId)
                        ? InternalPluginBase.InternalPluginBaseID
                        : pluginId;
                    var plugin = PluginManager.LoadedPlugins.TryGetValue(resolvedPluginId, out var loaded)
                        ? loaded
                        : PluginManager.LoadedPlugins[InternalPluginBase.InternalPluginBaseID];
                    result.Add(plugin.VectComponentCreator(element));
                }

                return result;
            }
            catch (Exception ex)
            {
                Log(ex, $"deserialize Components from ExtraData", this);
                return new();
            }
        }

        /// <summary>
        /// Serialises the <see cref="Components"/> list into <see cref="ExtraData"/>
        /// so they persist with the clip's metadata.
        /// </summary>
        public void SerializeComponents(List<IVectorComponent> components)
        {
            ExtraData ??= new();
            ExtraData[ComponentsDataKey] = JsonSerializer.Serialize(components, _componentsJsonOptions);
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
        public float FrameTime { get; init; }
        public ISpeedVarianceProvider? SpeedVarianceProviderInstance { get; set; }
        public IMixture? MixtureInstance { get; set; }
        public bool ExtendToWholeDraft { get; set; }
        public EffectAndMixtureJSONStructure[]? Effects { get; init; }
        public IEffect[]? EffectsInstances { get; set; }
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
            (EffectsInstances, SpeedVarianceProviderInstance, MixtureInstance, AlternativeSource) = EffectHelper.GetEffectsInstancesSpeedVarianceAndMixture(Effects);
        }
    }
}
