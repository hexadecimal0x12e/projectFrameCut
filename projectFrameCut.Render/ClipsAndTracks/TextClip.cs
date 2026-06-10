using projectFrameCut.Drawing.Text.Entry;
using projectFrameCut.Drawing.Text.Typology;
using projectFrameCut.Drawing.Vector;
using projectFrameCut.Drawing.Vector.ImportExport;
using projectFrameCut.Render.RenderAPIBase.ClipAndTrack;
using projectFrameCut.Render.RenderAPIBase.EffectAndMixture;
using System.Text.Json;
using projectFrameCut.Render.Effect;

namespace projectFrameCut.Render.ClipsAndTracks
{
    public class TextClip : IVectorContentClip
    {
        public static bool DiagMode = false;

        public required string Id { get; init; }
        public Guid IdAsGUID
        {
            get;
            init
            {
                if (!Guid.TryParse(Id, out field))
                {
                    Log($"A clip's ID field should be a valid guid. The input field has an invalid data '{Id}'", "warn");
                    field = Guid.Empty;
                }
            }
        }
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
        public IEffect[]? EffectsInstances { get; set; }
        public bool NeedFilePath => false;
        public Dictionary<string, object> ExtraData { get; set; }
        public bool ExtendToWholeDraft { get; set; }

        public string BindedSoundTrack { get; init; } = "";


        string? IClip.FilePath { get => null; set => throw new InvalidOperationException("Set path is not supported by this type of clip."); }

        public List<TextClipEntry> TextEntries { get; set; } = new List<TextClipEntry>();

        public string FontPath { get; set; } = string.Empty;
        private const int MaxTextFrameCacheEntries = 16;
        private readonly object textFrameCacheLock = new();
        private readonly Dictionary<string, IPicture> textFrameCache = new(StringComparer.Ordinal);

        public VectorPicture GetVectorPictureRelativeToStartPointOfSource(uint frameIndex, int targetWidth, int targetHeight)
        {
            var entriesToRender = ResolveTextEntriesForRender(frameIndex);
            var entriesForTarget = BuildEntriesForTargetSize(entriesToRender, targetWidth, targetHeight);

            var vectorCanvas = new VectorPicture();
            float uniformScale = Math.Min(targetWidth, targetHeight);

            foreach (var entry in entriesForTarget)
            {
                if (string.IsNullOrEmpty(entry.text))
                    continue;

                entry.text = entry.text.Replace("\r\n", "\n").Replace('\r', '\n');

                if (!TextClipFontRegistry.TryGetFont(entry.fontFamily, out var primaryFont) || primaryFont is null)
                {
                    var fallbackName = TextClipFontRegistry.FallbackFamilyName;
                    if (fallbackName is null || !TextClipFontRegistry.TryGetFont(fallbackName, out primaryFont) || primaryFont is null)
                        continue;
                }

                if (primaryFont is null || !entry.text.Any(c => !char.IsControl(c) && c != ' ' && primaryFont.CanDisplayTheChar(c)))
                {
                    Log($"TextClip {this.Name}: No valid font found (or no glyph is supported) for entry with text '{entry.text}' and font '{entry.fontFamily}'.", "warn");

                }


                // Compute baseline offset: in Y-down coordinates, the glyph body
                // starts at YMin (negative = above baseline) and ends at YMax (positive).
                // Without offset the baseline sits at entry.y and most glyphs get
                // clipped above the canvas.  Push the baseline down by the ascender
                // so the visible glyph body starts at the intended position.
                float unitsPerEm = primaryFont.UnitsPerEm;
                float ascenderRatio = 0.8f; // sensible default
                try
                {
                    ushort refGlyphIdx = primaryFont.GetGlyphIndex('A');
                    var ag = primaryFont.GetGlyph(refGlyphIdx);
                    if (ag is not null && !ag.IsEmpty && unitsPerEm > 0)
                    {
                        // Use YMax (the top of the glyph above the baseline) as the
                        // ascender. For most fonts this is the same as the cap height
                        // for capital letters like 'A'. YMin would give the descender
                        // depth (zero for 'A'), which is not what we want.
                        if (ag.YMax > 0)
                            ascenderRatio = (float)ag.YMax / unitsPerEm;
                    }
                }
                catch { }
                // entry.fontSize is in **pixels** at the source canvas resolution.
                // The render engine expects FontSize in normalised 0..1 canvas space
                // (see NormalTypesettingEngine docs), so we normalise by the
                // **target** height to keep the visual font size stable across
                // different render target dimensions.
                //
                // We additionally cap the normalised size at the canvas height. Without
                // this cap, a font larger than the canvas (e.g. 1081px on a 1080px
                // canvas) yields normFontSize > 1.0f, which pushes every glyph body
                // and every cursor advance outside [0,1] — the renderer then either
                // drops the glyph (if clamped) or rasterises it off-canvas. The 1.0
                // cap means the font is "no taller than the canvas" — visually the
                // user just sees the bottom of the text clipped, but the *layout*
                // (cursor positions, wrap decisions) stays inside the normalised
                // coordinate system.
                float normFontSizeRaw = entry.fontSize / (float)targetHeight;
                const float MaxNormFontSize = 1.0f;
                float normFontSize = MathF.Min(normFontSizeRaw, MaxNormFontSize);
                if (normFontSize < normFontSizeRaw)
                {
                    Log($"TextClip {this.Name}: fontSize {entry.fontSize}px exceeds targetHeight {targetHeight}px; clamping normFontSize from {normFontSizeRaw:F4} to {MaxNormFontSize:F4} to keep layout inside the normalised canvas.", "warn");
                }
                float baselineOffset = ascenderRatio * normFontSize;

                if (entry.UseVerticalLayout)
                {
                    var cleanText = entry.text.Replace("\r", "");
                    var verticalEngine = new VerticalTypesettingEngine();
                    var verticalLayout = verticalEngine.Layout(
                        new TextEntry
                        {
                            FontName = primaryFont.UniqueName,
                            Text = entry.text,
                            FontSize = normFontSize,
                            X = entry.x / (float)targetWidth,
                            Y = entry.y / (float)targetHeight + baselineOffset,
                            Rotation = entry.rotation * MathF.PI / 180f,
                            FillR = entry.r,
                            FillG = entry.g,
                            FillB = entry.b,
                            FillA = entry.a ?? 1f,
                            StrokeR = entry.strokeR,
                            StrokeG = entry.strokeG,
                            StrokeB = entry.strokeB,
                            StrokeThickness = entry.strokeWidth ?? 0f,
                            StrokeA = (entry.strokeWidth ?? 0f) > 0f ? 1f : 0f,
                            LineSpacing = entry.lineSpacing - 1f,
                            Alignment = entry.horizontalAlignment switch
                            {
                                ClipHorizontalAlignment.Center => Drawing.Text.Entry.TextAlignment.Center,
                                ClipHorizontalAlignment.Right => Drawing.Text.Entry.TextAlignment.Right,
                                _ => Drawing.Text.Entry.TextAlignment.Left,
                            },
                            ExtraData = new Dictionary<string, object> { { "keepNonCjkHorizontal", entry.KeepNonCJKTextAsHorizontal } }
                        }
                        , primaryFont);
                    //cleanText, primaryFont,
                    //normalizedFontSize: normFontSize,
                    //x: entry.x / (float)targetWidth,
                    //y: entry.y / (float)targetHeight + baselineOffset,
                    //lineSpacing: entry.lineSpacing,
                    //keepNonCjkHorizontal: entry.KeepNonCJKTextAsHorizontal,
                    //fillR: entry.r, fillG: entry.g, fillB: entry.b, fillA: entry.a ?? 1f,
                    //strokeR: entry.strokeR, strokeG: entry.strokeG, strokeB: entry.strokeB,
                    //strokeThickness: entry.strokeWidth ?? 0f);
                    if (verticalLayout.Elements.Count == 0)
                    {
                        verticalLayout.Elements.Add(ShapeCanvasElement.DrawRectangle(1f, 1f).WithPosition(entry.x, entry.y).WithFill(128 * 257, 0, 128 * 257));
                    }

                    // Apply overall rotation on the whole vertical block
                    if (Math.Abs(entry.rotation) > 0.0001f)
                    {
                        float rad = entry.rotation * MathF.PI / 180f;
                        foreach (var el in verticalLayout.Elements)
                            el.Rotation += rad;
                    }

                    // Compensate Y advances on portrait canvases: the typesetting engine
                    // computes advances in height-normalised space (1.0 = targetHeight)
                    // but VectorToIPicture maps RelativeY via uniformScale = min(w,h).
                    // On portrait canvases uniformScale == targetWidth < targetHeight,
                    // so cursor Y must be stretched to keep per-character advance correct.
                    float yCompensation = (float)targetHeight / uniformScale;
                    if (Math.Abs(yCompensation - 1f) > 0.0001f)
                    {
                        foreach (var el in verticalLayout.Elements)
                            el.RelativeY *= yCompensation;
                    }

                    foreach (var el in verticalLayout.Elements)
                        vectorCanvas.Elements.Add(el);
                }
                else
                {
                    var textEntry = new TextEntry
                    {
                        Text = entry.text,
                        FontName = primaryFont.UniqueName,
                        FontSize = normFontSize,
                        X = entry.x / (float)targetWidth,
                        Y = entry.y / (float)targetHeight + baselineOffset,
                        Rotation = entry.rotation * MathF.PI / 180f,
                        FillR = entry.r,
                        FillG = entry.g,
                        FillB = entry.b,
                        FillA = entry.a ?? 1f,
                        StrokeR = entry.strokeR,
                        StrokeG = entry.strokeG,
                        StrokeB = entry.strokeB,
                        StrokeThickness = entry.strokeWidth ?? 0f,
                        StrokeA = (entry.strokeWidth ?? 0f) > 0f ? 1f : 0f,
                        LineSpacing = entry.lineSpacing - 1f,
                        Alignment = entry.horizontalAlignment switch
                        {
                            ClipHorizontalAlignment.Center => Drawing.Text.Entry.TextAlignment.Center,
                            ClipHorizontalAlignment.Right => Drawing.Text.Entry.TextAlignment.Right,
                            _ => Drawing.Text.Entry.TextAlignment.Left,
                        },
                        LayerIndex = 0,
                    };

                    var engine = new NormalTypesettingEngine()
                    {
                        DebugMode = DiagMode,
                    };
                    (var measuredWidth, var measuredHeight) = engine.Measure(textEntry, primaryFont);
                    if (measuredWidth <= 0f || measuredHeight <= 0f)
                    {
                        Log($"TextClip {this.Name}: text {textEntry.Text} measured an invalid size {measuredWidth}*{measuredHeight}.", "warn");
                    }
                    // reported sizes are in normalised 0..1 space — multiply by the
                    // appropriate canvas dimension to compare against the
                    // "avg per char" values in targetWidth/targetHeight (pixels).
                    // NOTE: an earlier version of this log accidentally used
                    // `targetWidth` for both dimensions, which made the reported
                    // height nonsensical on non-square canvases.

                    var layout = engine.Layout(textEntry, primaryFont);
                    if (layout.Elements.Count == 0)
                    {
                        layout.Elements.Add(ShapeCanvasElement.DrawRectangle(measuredWidth, measuredHeight).WithPosition(entry.x, entry.y).WithFill(128 * 257, 0, 128 * 257));
                    }

                    // VectorToIPicture maps glyph position X via 'width' but glyph shape X
                    // via 'min(width,height)' (UseUniformScale). The engine computes
                    // advances in height-normalised space (1.0 = targetHeight) but the
                    // rasteriser maps RelativeX through uniformScale = min(w,h), so
                    // on non-square canvases the cursor must be adjusted to keep the
                    // per-character pixel advance correct.
                    float xCompensation = (float)targetHeight / uniformScale;
                    if (Math.Abs(xCompensation - 1f) > 0.0001f)
                    {
                        foreach (var el in layout.Elements)
                            el.RelativeX *= xCompensation;
                    }

                    foreach (var el in layout.Elements)
                        vectorCanvas.Elements.Add(el);
                }
            }

            return vectorCanvas;
        }

        public IPicture GetFrameRelativeToStartPointOfSource(uint frameIndex, int targetWidth, int targetHeight, bool forceResize, IPicture.PicturePixelMode targetPPB)
        {
            var entriesToRender = ResolveTextEntriesForRender(frameIndex);
            var entriesForTarget = BuildEntriesForTargetSize(entriesToRender, targetWidth, targetHeight);
            string serializedEntries = JsonSerializer.Serialize(entriesForTarget, JsonSerializerOptions.Web);
            string cacheKey = BuildFrameCacheKey(targetWidth, targetHeight, forceResize, targetPPB, serializedEntries);

            if (TryGetFrameFromCache(cacheKey, out var cachedFrame))
            {
                return cachedFrame;
            }

            var vectorCanvas = GetVectorPictureRelativeToStartPointOfSource(frameIndex, targetWidth, targetHeight);

            var sourcePicture = VectorToIPicture.Convert(vectorCanvas, targetWidth, targetHeight, transparentBackground: true, aaMode: ClipAntiAliasMode ?? IVectorContentClip.GlobalDefaultAntiAliasMode);
            sourcePicture.ProcessStack = new List<PictureProcessStack>
                {
                    new PictureProcessStack
                    {
                        OperationDisplayName = "TextClip Render",
                        Operator = typeof(TextClip),
                        ProcessingFuncStackTrace = new System.Diagnostics.StackTrace(true),
                        Properties = new Dictionary<string, object>
                        {
                            { "TextEntries", serializedEntries },
                            { "FontPath", FontPath }
                        }
                    }
                };
            IPicture rendered = sourcePicture;

            if (targetPPB.Value != 16)
            {
                rendered = rendered.ToBitPerPixel(targetPPB);
            }

            CacheRenderedFrame(cacheKey, rendered);
            return rendered.Clone();
        }


        public TextClip()
        {
            (EffectsInstances, SpeedVarianceProviderInstance, MixtureInstance) = EffectHelper.GetEffectsInstancesSpeedVarianceAndMixture(Effects);

        }

        public void ReInit(IPicture.PicturePixelMode targetPPB)
        {
            ClearFrameCache();

            if (!string.IsNullOrWhiteSpace(FontPath))
            {
                TextClipFontRegistry.AddFont(FontPath);
            }
            (EffectsInstances, SpeedVarianceProviderInstance, MixtureInstance) = EffectHelper.GetEffectsInstancesSpeedVarianceAndMixture(Effects);

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

        /// <summary>
        /// Initializes the font registry. Kept for backward compatibility.
        /// </summary>
        public static void GetFont(bool force = false) => TextClipFontRegistry.Initialize();

        private string BuildFrameCacheKey(int targetWidth, int targetHeight, bool forceResize, IPicture.PicturePixelMode targetPPB, string serializedEntries)
            => $"{targetWidth}x{targetHeight}|forceResize={forceResize}|ppb={targetPPB.Value}|font={FontPath}|entries={serializedEntries}";

        private bool TryGetFrameFromCache(string cacheKey, out IPicture picture)
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
                    try { cachedFrame.Dispose(true); } catch { }
                }
            }

            picture = null!;
            return false;
        }

        private void CacheRenderedFrame(string cacheKey, IPicture picture)
        {
            lock (textFrameCacheLock)
            {
                if (!textFrameCache.ContainsKey(cacheKey) && textFrameCache.Count >= MaxTextFrameCacheEntries)
                {
                    ClearFrameCacheUnsafe();
                }

                if (textFrameCache.TryGetValue(cacheKey, out var oldFrame))
                {
                    try { oldFrame.Dispose(true); } catch { }
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
                try { frame.Dispose(true); } catch { }
            }
            textFrameCache.Clear();
        }

        private IReadOnlyList<TextClipEntry> ResolveTextEntriesForRender(uint targetFrame)
        {
            var raw = GetRawEntries();
            foreach (var item in EffectsInstances?.Where(c => c is ITextEffect or IContinuousTextEffect) ?? [])
            {
                if (item is ITextEffect)
                {
                    raw = ((ITextEffect)item).Process(raw.ToArray());

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

        private IReadOnlyList<TextClipEntry> GetRawEntries()
        {
            if (ExtraData?.TryGetValue("TextEntries", out var rawEntries) == true)
            {
                if (rawEntries is List<TextClipEntry> list && list.Count > 0)
                    return list;

                if (rawEntries is JsonElement je)
                {
                    try
                    {
                        var parsed = je.Deserialize<List<TextClipEntry>>();
                        if (parsed is { Count: > 0 })
                            return parsed;
                    }
                    catch
                    {
                        // fall back to TextEntries
                    }
                }

                if (rawEntries is string json && !string.IsNullOrWhiteSpace(json))
                {
                    try
                    {
                        var parsed = JsonSerializer.Deserialize<List<TextClipEntry>>(json);
                        if (parsed is { Count: > 0 })
                            return parsed;
                    }
                    catch
                    {
                        // fall back to TextEntries
                    }
                }
            }

            return TextEntries;
        }

        private IReadOnlyList<TextClipEntry> BuildEntriesForTargetSize(IReadOnlyList<TextClipEntry> entries, int targetWidth, int targetHeight)
        {
            if (entries.Count == 0 || targetWidth <= 0 || targetHeight <= 0)
            {
                return entries;
            }

            var sourceWidth = TargetWidth > 0 ? TargetWidth : targetWidth;
            var sourceHeight = TargetHeight > 0 ? TargetHeight : targetHeight;
            if (sourceWidth <= 0 || sourceHeight <= 0)
            {
                return entries;
            }

            var scaleX = (float)targetWidth / sourceWidth;
            var scaleY = (float)targetHeight / sourceHeight;
            if (Math.Abs(scaleX - 1f) < 0.0001f && Math.Abs(scaleY - 1f) < 0.0001f)
            {
                return entries;
            }

            var textScale = MathF.Max(0.0001f, MathF.Min(scaleX, scaleY));
            var scaled = new List<TextClipEntry>(entries.Count);
            foreach (var entry in entries)
            {
                scaled.Add(entry with
                {
                    x = (int)Math.Round(entry.x * scaleX, MidpointRounding.AwayFromZero),
                    y = (int)Math.Round(entry.y * scaleY, MidpointRounding.AwayFromZero),
                    // fontSize is NOT scaled here — the render loop normalizes it
                    // via entry.fontSize / targetHeight.  Pre-scaling would compound
                    // with that normalization, causing advances near 1.0 (full canvas
                    // width per glyph) when TargetWidth/TargetHeight differ from the
                    // render target dimensions.
                    wrappingWidth = entry.wrappingWidth.HasValue
                        ? Math.Max(0.1f, entry.wrappingWidth.Value * scaleX)
                        : null,
                });
            }

            return scaled;
        }


    }

    public class ImmutableContentTextClip : TextClip, IImmutableVectorContentClip
    {
        public VectorPicture GetVectorPicture(int requiredWidth, int requiredHeight)
            => base.GetVectorPictureRelativeToStartPointOfSource(0U, requiredHeight, requiredHeight);
    }
}
