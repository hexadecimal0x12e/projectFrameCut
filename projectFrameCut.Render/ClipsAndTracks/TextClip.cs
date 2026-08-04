using projectFrameCut.Drawing.Text.Entry;
using projectFrameCut.Drawing.Vector;
using projectFrameCut.Drawing.Vector.ImportExport;
using projectFrameCut.Render.ClipsAndTracks.Text;
using projectFrameCut.Render.RenderAPIBase.ClipAndTrack;
using projectFrameCut.Render.RenderAPIBase.EffectAndMixture;
using System.Text.Json;
using System.Text.Json.Serialization;
using projectFrameCut.Render.Effect;
using projectFrameCut.Shared;

namespace projectFrameCut.Render.ClipsAndTracks
{
    public class TextClip : IVectorContentClip
    {
        public static bool DiagMode = false;

        public required Guid Id { get; init; }
        public required string Name { get; init; }
        public uint LayerIndex { get; init; } = 0;
        public uint SubLayerIndex { get; init; }
        public uint StartFrame { get; init; }
        public uint RelativeStartFrame { get; init; }
        public uint Duration { get; set; }
        public float FrameTime { get; init; }
        public float SecondPerFrameRatio { get => 1; init { } }

        public string? filePath { get; } = null;
        public ClipMode ClipType => ClipMode.TextClip;
        public string FromPlugin => projectFrameCut.Render.Plugin.InternalPluginBase.InternalPluginBaseID;

        public EffectAndMixtureJSONStructure[]? Effects { get; init; }
        public EffectProviderJSONStructure[]? EffectProviders { get; init; }
        public IEffect[]? EffectsInstances { get; set; }
        [System.Text.Json.Serialization.JsonIgnore]
        public IEffectProvider[]? EffectProvidersInstances { get; set; }
        public bool NeedFilePath => false;
        public Dictionary<string, object> ExtraData { get; set; }
        public bool ExtendToWholeDraft { get; set; }

        public string BindedSoundTrack { get; init; } = "";

        string? IClip.FilePath { get => null; set => throw new InvalidOperationException("Set path is not supported by this type of clip."); }

        [JsonIgnore]
        public List<TextEntry> TextEntries { get; set; } = new List<TextEntry>();

        [JsonPropertyName("TextEntries")]
        public List<object> TextEntriesJson
        {
            get => TextEntries.Cast<object>().ToList();
            set
            {
                TextEntries = new List<TextEntry>(value.Count);
                foreach (var item in value)
                {
                    if (item is TextEntry te)
                    {
                        TextEntries.Add(te);
                    }
                    else if (item is JsonElement je)
                    {
                        try
                        {
                            TextEntries.Add(je.Deserialize<TextEntry>()!);
                        }
                        catch
                        {
                            try
                            {
                                var old = je.Deserialize<TextClipEntry>();
                                if (old is not null)
                                    TextEntries.Add(TextEntryMigration.MigrateFromTextClipEntry(old));
                            }
                            catch { }
                        }
                    }
                }
            }
        }

        public string FontPath { get; set; } = string.Empty;
        private const int MaxTextFrameCacheEntries = 1024;
        private readonly object textFrameCacheLock = new();
        private readonly Dictionary<long, IPicture> textFrameCache = new();

        /// <summary>
        /// Build a <see cref="VectorPicture"/> for the given target dimensions.
        /// The clip is laid out against its own bounding box
        /// (<see cref="TargetWidth"/> × <see cref="TargetHeight"/>) — when those
        /// are unset the requested target dims are used.
        /// </summary>
        public VectorPicture GetVectorPictureRelativeToStartPointOfSource(uint frameIndex, int targetWidth, int targetHeight)
        {
            var entriesToRender = ResolveTextEntriesForRender(frameIndex);
            if (entriesToRender.Count == 0)
                return new VectorPicture();

            // The layout canvas is the clip's own bounding box. This way the
            // pixel values stored in each TextEntry (FontSize, X, Y, WrappingWidth, ...)
            // map directly to pixels inside the clip box, regardless of the
            // final raster target dimensions.
            float canvasW = TargetWidth > 0 ? TargetWidth : targetWidth;
            float canvasH = TargetHeight > 0 ? TargetHeight : targetHeight;
            if (canvasW <= 0) canvasW = targetWidth;
            if (canvasH <= 0) canvasH = targetHeight;
            if (canvasW <= 0) canvasW = 1f;
            if (canvasH <= 0) canvasH = 1f;

            var ctx = TextLayoutContext.FromCanvas(canvasW, canvasH);
            return TextLayoutPipeline.LayoutForRender(entriesToRender, ctx, targetWidth, targetHeight);
        }

        public IPicture GetFrameRelativeToStartPointOfSource(uint frameIndex, int targetWidth, int targetHeight, bool forceResize, IPicture.PicturePixelMode targetPPB)
        {
            // Guard against unreasonable render dimensions
            if (targetWidth > 0 && targetHeight > 0)
            {
                const int maxRatio = 10;
                if (targetWidth / targetHeight > maxRatio)
                {
                    Log($"TextClip {Name}: targetWidth {targetWidth} is {targetWidth / (float)targetHeight:F1}x targetHeight {targetHeight}. Clamping width to {targetHeight * maxRatio}.", "warn");
                    targetWidth = targetHeight * maxRatio;
                }
                else if (targetHeight / targetWidth > maxRatio)
                {
                    Log($"TextClip {Name}: targetHeight {targetHeight} is {targetHeight / (float)targetWidth:F1}x targetWidth {targetWidth}. Clamping height to {targetWidth * maxRatio}.", "warn");
                    targetHeight = targetWidth * maxRatio;
                }
            }

            var rawEntries = ResolveTextEntriesForRender(frameIndex);
            long cacheKey = BuildFrameCacheKey(targetWidth, targetHeight, forceResize, targetPPB, rawEntries);

            if (TryGetFrameFromCache(cacheKey, out var cachedFrame))
            {
                if (cachedFrame.BitPerPixel != targetPPB)
                    return cachedFrame.ToBitPerPixel(targetPPB);
                return cachedFrame;
            }

            var vectorCanvas = GetVectorPictureRelativeToStartPointOfSource(frameIndex, targetWidth, targetHeight);

            var sourcePicture = IVectorContentClip.GlobalDefaultRasterizer.Convert(vectorCanvas, targetWidth, targetHeight, transparentBackground: true, aaMode: ClipAntiAliasMode ?? IVectorContentClip.GlobalDefaultAntiAliasMode);
            sourcePicture.CanBeDisposed = false;
            sourcePicture.ProcessStack = new List<PictureProcessStack>
            {
                new PictureProcessStack
                {
                    OperationDisplayName = "TextClip Render",
                    Operator = typeof(TextClip),
                    ProcessingFuncStackTrace = new System.Diagnostics.StackTrace(true),
                    Properties = new Dictionary<string, object>
                    {
                        { "TextEntriesHash", cacheKey },
                        { "FontPath", FontPath }
                    }
                }
            };
            var resultPicture = sourcePicture.BitPerPixel != targetPPB
                ? sourcePicture.ToBitPerPixel(targetPPB)
                : sourcePicture;
            resultPicture.CanBeDisposed = false;
            CacheRenderedFrame(cacheKey, resultPicture);
            return resultPicture;
        }

        public TextClip()
        {
            (EffectsInstances, SpeedVarianceProviderInstance, MixtureInstance, AlternativeSource) = EffectHelper.GetEffectsInstancesSpeedVarianceAndMixture(Effects);
        }

        public void ReInit(IPicture.PicturePixelMode targetPPB)
        {
            ClearFrameCache();
            if (!string.IsNullOrWhiteSpace(FontPath))
                TextClipFontRegistry.AddFont(FontPath);
            (EffectsInstances, SpeedVarianceProviderInstance, MixtureInstance, AlternativeSource) = EffectHelper.GetEffectsInstancesSpeedVarianceAndMixture(Effects);
        }

        public void Dispose()
        {
            ClearFrameCache();
        }

        public uint? GetClipLength() => Duration;

        public int TargetWidth { get; set; }
        public int TargetHeight { get; set; }
        public int TargetX { get; set; }
        public int TargetY { get; set; }
        public ISpeedVarianceProvider? SpeedVarianceProviderInstance { get; set; }
        public IMixture? MixtureInstance { get; set; }
        public AntiAliasMode? ClipAntiAliasMode { get; set; }
        public ISourceReplacementEffect? AlternativeSource { get; set; }

        public static void GetFont(bool force = false) => TextClipFontRegistry.Initialize();

        /// <summary>
        /// Build a stable hash for the frame cache from the rendering parameters
        /// and a structural fingerprint of the entries. Avoids serialising the
        /// entire TextEntry payload to JSON for every cache lookup.
        /// </summary>
        private long BuildFrameCacheKey(int targetWidth, int targetHeight, bool forceResize, IPicture.PicturePixelMode targetPPB, IReadOnlyList<TextEntry> entries)
        {
            var hash = new HashCode();
            hash.Add(targetWidth);
            hash.Add(targetHeight);
            hash.Add(forceResize);
            hash.Add(targetPPB.Value);
            hash.Add(FontPath);
            hash.Add(TargetWidth);
            hash.Add(TargetHeight);
            foreach (var e in entries)
            {
                if (e is null) { hash.Add(0); continue; }
                hash.Add(e.Text ?? string.Empty);
                hash.Add(e.FontName ?? string.Empty);
                hash.Add(e.FontStyle ?? string.Empty);
                hash.Add(e.FontSize);
                hash.Add(e.X);
                hash.Add(e.Y);
                hash.Add(e.Rotation);
                hash.Add(e.FillR); hash.Add(e.FillG); hash.Add(e.FillB); hash.Add(e.FillA);
                hash.Add(e.StrokeR); hash.Add(e.StrokeG); hash.Add(e.StrokeB); hash.Add(e.StrokeA);
                hash.Add(e.StrokeThickness);
                hash.Add(e.CharacterSpacing);
                hash.Add(e.WordSpacing);
                hash.Add(e.LineSpacing);
                hash.Add((int)e.Alignment);
                hash.Add((int)e.Decoration);
                hash.Add((int)e.FlowDirection);
                hash.Add(e.GetWrappingWidth() ?? float.NaN);
                hash.Add((int)e.GetVerticalAlignment());
                hash.Add((int)e.GetLayoutMode());
                hash.Add(e.GetFixedHeightValue() ?? float.NaN);
                hash.Add(e.GetUseVerticalLayout());
                hash.Add(e.GetKeepNonCJKTextAsHorizontal());
            }
            return hash.ToHashCode();
        }

        private bool TryGetFrameFromCache(long cacheKey, out IPicture picture)
        {
            lock (textFrameCacheLock)
            {
                if (textFrameCache.TryGetValue(cacheKey, out var cachedFrame))
                {
                    if (!cachedFrame.Disposed)
                    {
                        picture = cachedFrame.Clone();
                        return true;
                    }
                    textFrameCache.Remove(cacheKey);
                    try { cachedFrame.Dispose(force: false); } catch { }
                }
            }
            picture = null!;
            return false;
        }

        private void CacheRenderedFrame(long cacheKey, IPicture picture)
        {
            lock (textFrameCacheLock)
            {
                if (!textFrameCache.ContainsKey(cacheKey) && textFrameCache.Count >= MaxTextFrameCacheEntries)
                    ClearFrameCacheUnsafe();

                if (textFrameCache.TryGetValue(cacheKey, out var oldFrame))
                {
                    try { oldFrame.Dispose(force: false); } catch { }
                }

                picture.CanBeDisposed = false;
                textFrameCache[cacheKey] = picture;
            }
        }

        private void ClearFrameCache()
        {
            lock (textFrameCacheLock)
            {
                ClearFrameCacheUnsafe();
            }
        }

        private void ClearFrameCacheUnsafe()
        {
            foreach (var frame in textFrameCache.Values)
            {
                try { frame.Dispose(force: false); } catch { }
            }
            textFrameCache.Clear();
        }

        private IReadOnlyList<TextEntry> ResolveTextEntriesForRender(uint targetFrame)
        {
            var raw = GetRawEntries();
            foreach (var item in EffectsInstances?.Where(c => c is ITextEffect or IContinuousTextEffect) ?? [])
            {
                if (item is ITextEffect textEffect)
                {
                    raw = textEffect.Process(raw.ToArray());
                }
                else if (item is IContinuousTextEffect cte)
                {
                    if (cte.IsScoped)
                    {
                        if (targetFrame < cte.StartPoint || targetFrame > cte.EndPoint)
                            continue;
                    }
                    raw = cte.Process(raw.ToArray(), targetFrame / ((IClip)this).GetEffectiveDuration());
                }
            }
            return raw;
        }

        private IReadOnlyList<TextEntry> GetRawEntries()
        {
            if (ExtraData?.TryGetValue("TextEntries", out var rawEntries) == true)
            {
                // Try new format (TextEntry) first
                if (rawEntries is List<TextEntry> list && list.Count > 0)
                    return list;

                // Handle old format (List<TextClipEntry>) stored directly by migration paths
                if (rawEntries is List<TextClipEntry> oldList && oldList.Count > 0)
                {
                    var migrated = TextEntryMigration.MigrateFromTextClipEntries(oldList);
                    ExtraData["TextEntries"] = migrated;
                    return migrated;
                }

                if (rawEntries is JsonElement je)
                {
                    try
                    {
                        var parsed = je.Deserialize<List<TextEntry>>();
                        if (parsed is { Count: > 0 }) { ExtraData["TextEntries"] = parsed; return parsed; }
                    }
                    catch
                    {
                        // Fall back to old TextClipEntry format
                        try
                        {
                            var oldParsed = je.Deserialize<List<TextClipEntry>>();
                            if (oldParsed is { Count: > 0 })
                            {
                                var migrated = TextEntryMigration.MigrateFromTextClipEntries(oldParsed);
                                ExtraData["TextEntries"] = migrated;
                                return migrated;
                            }
                        }
                        catch { }
                    }
                }

                if (rawEntries is string json && !string.IsNullOrWhiteSpace(json))
                {
                    try
                    {
                        var parsed = JsonSerializer.Deserialize<List<TextEntry>>(json);
                        if (parsed is { Count: > 0 }) { ExtraData["TextEntries"] = parsed; return parsed; }
                    }
                    catch
                    {
                        // Fall back to old TextClipEntry format
                        try
                        {
                            var oldParsed = JsonSerializer.Deserialize<List<TextClipEntry>>(json);
                            if (oldParsed is { Count: > 0 })
                            {
                                var migrated = TextEntryMigration.MigrateFromTextClipEntries(oldParsed);
                                ExtraData["TextEntries"] = migrated;
                                return migrated;
                            }
                        }
                        catch { }
                    }
                }
            }

            return TextEntries;
        }
    }

    public class ImmutableContentTextClip : TextClip, IImmutableVectorContentClip
    {
        public VectorPicture GetVectorPicture(int requiredWidth, int requiredHeight)
            => base.GetVectorPictureRelativeToStartPointOfSource(0U, requiredWidth, requiredHeight);
    }
}
