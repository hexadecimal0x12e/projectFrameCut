using System.Collections.Concurrent;
using projectFrameCut.Drawing.Text.Entry;
using projectFrameCut.Drawing.Text.FontHelper;
using projectFrameCut.Drawing.Text.Typology;
using projectFrameCut.Drawing.Vector;
using projectFrameCut.Shared;
using TextAlignment = projectFrameCut.Drawing.Text.Entry.TextAlignment;

namespace projectFrameCut.Render.ClipsAndTracks.Text;

/// <summary>
/// Single-source-of-truth for all text typesetting in the render pipeline.
/// <para>
/// Callers feed in <see cref="TextEntry"/> values whose <c>X</c>, <c>Y</c>,
/// <c>FontSize</c>, <c>StrokeThickness</c>, <c>CharacterSpacing</c>,
/// <c>WordSpacing</c>, and the wrapping width stored in
/// <see cref="TextEntryExtraDataKeys.WrappingWidth"/> are expressed in the
/// pixels of the canvas described by <see cref="TextLayoutContext"/>.
/// </para>
/// <para>
/// Internally the pipeline performs exactly one conversion to the typesetting
/// engine's normalised space, runs <see cref="NormalTypesettingEngine"/>
/// (or <see cref="VerticalTypesettingEngine"/>) and returns either a
/// <see cref="TextBounds"/> (in canvas pixels) or a <see cref="VectorPicture"/>
/// ready for the rasterizer. There is exactly one place — <see cref="ToEngineSpace"/> —
/// where pixel→engine conversion happens.
/// </para>
/// </summary>
public static class TextLayoutPipeline
{
    private const float DefaultAscenderRatio = 0.8f;
    private static readonly ConcurrentDictionary<FontFace, float> AscenderCache = new();

    // ────────────────────────────────────────────────────────────────────
    //  Public measurement API
    // ────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Measure the bounding box (in canvas pixels) of <paramref name="pixelEntry"/>.
    /// The bounds include any wrapping, vertical-alignment shift, rotation,
    /// and stroke-thickness inflation.
    /// </summary>
    public static TextBounds Measure(TextEntry pixelEntry, TextLayoutContext ctx)
    {
        if (pixelEntry is null || string.IsNullOrEmpty(pixelEntry.Text))
            return TextBounds.Empty;

        var primaryFont = ResolveFont(pixelEntry);
        if (primaryFont is null)
            return TextBounds.Empty;

        var prepared = PrepareForTypesetting(pixelEntry, ctx, primaryFont);
        var engineEntry = ToEngineSpace(prepared.PixelEntry, ctx);
        engineEntry = ApplyLineBreakIfNeeded(engineEntry, primaryFont, prepared.PixelEntry, ctx);

        var unit = ctx.UnitLength;
        TextBounds bounds;
        if (!prepared.PixelEntry.GetUseVerticalLayout())
        {
            var inkBounds = MeasureHorizontalInkBoundsInEngineSpace(engineEntry, primaryFont);
            if (inkBounds.HasVisual)
            {
                var widthPx = (inkBounds.MaxX - inkBounds.MinX) * unit;
                var heightPx = (inkBounds.MaxY - inkBounds.MinY) * unit;
                var verticalAlignShift = ComputeVerticalAlignmentShiftPx(prepared.PixelEntry, heightPx, ctx);
                var baselineOffsetPx = ComputeBaselineOffsetPx(inkBounds, unit);
                var originX = prepared.PixelEntry.X;
                var topY = prepared.PixelEntry.Y + verticalAlignShift;
                var rotationOriginY = topY + baselineOffsetPx;
                var inkStrokeInflate = MathF.Max(0f, prepared.PixelEntry.StrokeThickness) * 0.5f;

                bounds = new TextBounds(
                    originX + inkBounds.MinX * unit - inkStrokeInflate,
                    topY - inkStrokeInflate,
                    MathF.Max(1f, widthPx + inkStrokeInflate * 2f),
                    MathF.Max(1f, heightPx + inkStrokeInflate * 2f));

                if (MathF.Abs(prepared.PixelEntry.Rotation) > 0.0001f)
                    bounds = RotateBoundsAround(bounds, originX, rotationOriginY, prepared.PixelEntry.Rotation);

                return bounds;
            }
        }

        var (widthEng, heightEng) = MeasureInEngineSpace(engineEntry, primaryFont);
        var widthFallbackPx = widthEng * unit;
        var heightFallbackPx = heightEng * unit;

        var verticalAlignShiftFallback = ComputeVerticalAlignmentShiftPx(prepared.PixelEntry, heightFallbackPx, ctx);

        // Horizontal alignment is applied by the engine (cursor X starts at 0 for Left,
        // shifted at render time for Center/Right). We mirror that here so the returned
        // bounds match what's actually painted.
        float originXFallback = prepared.PixelEntry.X;
        float originYFallback = prepared.PixelEntry.Y + verticalAlignShiftFallback;

        float left = originXFallback;
        switch (prepared.PixelEntry.Alignment)
        {
            case TextAlignment.Center: left -= widthFallbackPx / 2f; break;
            case TextAlignment.Right: left -= widthFallbackPx; break;
        }

        var strokeInflate = MathF.Max(0f, prepared.PixelEntry.StrokeThickness) * 0.5f;
        bounds = new TextBounds(
            left - strokeInflate,
            originYFallback - strokeInflate,
            MathF.Max(1f, widthFallbackPx + strokeInflate * 2f),
            MathF.Max(1f, heightFallbackPx + strokeInflate * 2f));

        if (MathF.Abs(prepared.PixelEntry.Rotation) > 0.0001f)
            bounds = RotateBoundsAround(bounds, originXFallback, originYFallback, prepared.PixelEntry.Rotation);

        return bounds;
    }

    /// <summary>
    /// Measure the combined bounds of multiple entries.
    /// </summary>
    public static TextBounds Measure(IReadOnlyList<TextEntry> pixelEntries, TextLayoutContext ctx)
    {
        var result = TextBounds.Empty;
        bool any = false;
        foreach (var entry in pixelEntries)
        {
            var b = Measure(entry, ctx);
            if (b.Width <= 0f && b.Height <= 0f) continue;
            result = any ? result.Union(b) : b;
            any = true;
        }
        return any ? result : TextBounds.Empty;
    }

    // ────────────────────────────────────────────────────────────────────
    //  Public layout API
    // ────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Lay out <paramref name="pixelEntries"/> into a <see cref="VectorPicture"/>
    /// suitable for the rasterizer at <paramref name="targetWidth"/> ×
    /// <paramref name="targetHeight"/>.
    /// <para>
    /// The target dimensions should normally match the canvas aspect described
    /// by <paramref name="ctx"/>; the rasterizer will stretch the picture if
    /// they do not. Glyph positions remain stable as long as
    /// <c>targetW / targetH ≈ ctx.CanvasWidth / ctx.CanvasHeight</c>.
    /// </para>
    /// </summary>
    public static VectorPicture LayoutForRender(
        IReadOnlyList<TextEntry> pixelEntries,
        TextLayoutContext ctx,
        int targetWidth,
        int targetHeight)
    {
        var canvas = new VectorPicture();
        if (pixelEntries.Count == 0)
            return canvas;

        foreach (var pixelEntry in pixelEntries)
        {
            if (pixelEntry is null || string.IsNullOrEmpty(pixelEntry.Text))
                continue;

            var primaryFont = ResolveFont(pixelEntry);
            if (primaryFont is null) continue;

            var prepared = PrepareForTypesetting(pixelEntry, ctx, primaryFont);
            var engineEntry = ToEngineSpace(prepared.PixelEntry, ctx);
            engineEntry = ApplyLineBreakIfNeeded(engineEntry, primaryFont, prepared.PixelEntry, ctx);

            float baselineOffsetPx = 0f;
            float verticalShiftPx = 0f;
            if (!prepared.PixelEntry.GetUseVerticalLayout())
            {
                var inkBounds = MeasureHorizontalInkBoundsInEngineSpace(engineEntry, primaryFont);
                if (inkBounds.HasVisual)
                {
                    var measuredHeightPx = (inkBounds.MaxY - inkBounds.MinY) * ctx.UnitLength;
                    baselineOffsetPx = ComputeBaselineOffsetPx(inkBounds, ctx.UnitLength);
                    verticalShiftPx = ComputeVerticalAlignmentShiftPx(prepared.PixelEntry, measuredHeightPx, ctx);
                }
            }

            if (baselineOffsetPx <= 0f)
                baselineOffsetPx = GetAscenderRatio(primaryFont) * engineEntry.FontSize * ctx.CanvasHeight;

            if (prepared.PixelEntry.GetVerticalAlignment() != ClipVerticalAlignment.Top && verticalShiftPx <= 0f)
            {
                var (_, heightEng) = MeasureInEngineSpace(engineEntry, primaryFont);
                var measuredHeightPx = heightEng * ctx.UnitLength;
                verticalShiftPx = ComputeVerticalAlignmentShiftPx(prepared.PixelEntry, measuredHeightPx, ctx);
            }

            var baselineOffsetEng = baselineOffsetPx / ctx.CanvasHeight;
            var verticalShiftEng = verticalShiftPx / ctx.CanvasHeight;
            engineEntry = engineEntry with { Y = engineEntry.Y + baselineOffsetEng + verticalShiftEng };

            VectorPicture layout;
            if (prepared.PixelEntry.GetUseVerticalLayout())
            {
                var verticalEngine = new VerticalTypesettingEngine
                {
                    FallbackFonts = TextClipFontRegistry.FallbackFonts,
                };
                var verticalEngineEntry = engineEntry with
                {
                    ExtraData = new Dictionary<string, object>(engineEntry.ExtraData)
                    {
                        ["keepNonCjkHorizontal"] = prepared.PixelEntry.GetKeepNonCJKTextAsHorizontal()
                    }
                };
                layout = verticalEngine.Layout(verticalEngineEntry, primaryFont);
            }
            else
            {
                var horizontalEngine = new NormalTypesettingEngine
                {
                    FallbackFonts = TextClipFontRegistry.FallbackFonts,
                    DebugMode = TextClip.DiagMode,
                };
                layout = horizontalEngine.Layout(engineEntry, primaryFont);
            }

            // Aspect compensation: when the target's min axis differs from the
            // canvas's min axis (e.g., portrait canvas rendered to a square
            // preview), per-glyph Relative offsets get mapped through the
            // rasterizer's min(target) instead of min(canvas). We scale them
            // proportionally so glyph positions stay invariant to that
            // mismatch.
            ApplyAspectCompensation(layout, ctx, targetWidth, targetHeight);

            foreach (var element in layout.Elements)
                canvas.Elements.Add(element);
        }

        return canvas;
    }

    // ────────────────────────────────────────────────────────────────────
    //  Engine-space conversion
    // ────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Convert a pixel-space <paramref name="pixelEntry"/> to an entry the
    /// <see cref="NormalTypesettingEngine"/> can consume directly. The
    /// conversion follows the engine's documented unit conventions:
    /// <list type="bullet">
    ///   <item><c>X</c> ÷ canvas width</item>
    ///   <item><c>Y</c> ÷ canvas height</item>
    ///   <item>All "length" fields (FontSize, StrokeThickness, CharacterSpacing,
    ///         WordSpacing) ÷ <see cref="TextLayoutContext.UnitLength"/>
    ///         (= <c>min(canvasW, canvasH)</c>)</item>
    ///   <item>WrappingWidth in <see cref="TextEntryExtraDataKeys.WrappingWidth"/>
    ///         likewise ÷ UnitLength</item>
    /// </list>
    /// The engine then reads positions in width/height fractions and length
    /// fields in <c>min(W,H)</c> fractions — matching the rasterizer's
    /// <c>UseUniformScale</c> mapping.
    /// </summary>
    public static TextEntry ToEngineSpace(TextEntry pixelEntry, TextLayoutContext ctx)
    {
        var w = ctx.CanvasWidth > 0 ? ctx.CanvasWidth : 1920f;
        var h = ctx.CanvasHeight > 0 ? ctx.CanvasHeight : 1080f;
        var unit = ctx.UnitLength;

        var converted = pixelEntry with
        {
            X = pixelEntry.X / w,
            Y = pixelEntry.Y / h,
            FontSize = pixelEntry.FontSize / unit,
            StrokeThickness = pixelEntry.StrokeThickness / unit,
            CharacterSpacing = pixelEntry.CharacterSpacing / unit,
            WordSpacing = pixelEntry.WordSpacing / unit,
            ExtraData = new Dictionary<string, object>(pixelEntry.ExtraData),
        };

        var wwPx = pixelEntry.GetWrappingWidth();
        if (wwPx.HasValue && wwPx.Value > 0f)
            converted.SetWrappingWidth(wwPx.Value / unit);

        return converted;
    }

    /// <summary>
    /// The inverse of <see cref="ToEngineSpace"/> — useful for migration.
    /// </summary>
    public static TextEntry FromEngineSpace(TextEntry engineEntry, TextLayoutContext ctx)
    {
        var w = ctx.CanvasWidth > 0 ? ctx.CanvasWidth : 1920f;
        var h = ctx.CanvasHeight > 0 ? ctx.CanvasHeight : 1080f;
        var unit = ctx.UnitLength;

        var converted = engineEntry with
        {
            X = engineEntry.X * w,
            Y = engineEntry.Y * h,
            FontSize = engineEntry.FontSize * unit,
            StrokeThickness = engineEntry.StrokeThickness * unit,
            CharacterSpacing = engineEntry.CharacterSpacing * unit,
            WordSpacing = engineEntry.WordSpacing * unit,
            ExtraData = new Dictionary<string, object>(engineEntry.ExtraData),
        };

        var wwEng = engineEntry.GetWrappingWidth();
        if (wwEng.HasValue && wwEng.Value > 0f)
            converted.SetWrappingWidth(wwEng.Value * unit);

        return converted;
    }

    // ────────────────────────────────────────────────────────────────────
    //  Internals
    // ────────────────────────────────────────────────────────────────────

    private readonly record struct PreparedEntry(TextEntry PixelEntry);

    /// <summary>
    /// Apply layout-mode logic (e.g., FixedHeight fits the font size to the
    /// canvas height). Returns a possibly-modified pixel-space entry.
    /// </summary>
    private static PreparedEntry PrepareForTypesetting(TextEntry pixelEntry, TextLayoutContext ctx, FontFace primaryFont)
    {
        pixelEntry.Text = pixelEntry.Text?.Replace("\r\n", "\n").Replace('\r', '\n') ?? string.Empty;

        var layoutMode = pixelEntry.GetLayoutMode();
        if (layoutMode == TextClipLayoutMode.FixedHeight)
        {
            var fixedHPx = pixelEntry.GetFixedHeightValue() ?? ctx.CanvasHeight;
            if (fixedHPx > 0f)
            {
                var adjusted = TextLayoutModeResolver.FitFontSizeToHeight(pixelEntry, primaryFont, ctx, fixedHPx);
                return new PreparedEntry(adjusted);
            }
        }

        return new PreparedEntry(pixelEntry);
    }

    internal static (float widthEng, float heightEng) MeasureInEngineSpace(TextEntry engineEntry, FontFace primaryFont)
    {
        var engine = new NormalTypesettingEngine
        {
            FallbackFonts = TextClipFontRegistry.FallbackFonts,
        };
        return engine.Measure(engineEntry, primaryFont);
    }

    private readonly record struct HorizontalInkBounds(float MinX, float MinY, float MaxX, float MaxY)
    {
        public bool HasVisual => MaxX > MinX && MaxY > MinY;
    }

    private static HorizontalInkBounds MeasureHorizontalInkBoundsInEngineSpace(TextEntry engineEntry, FontFace primaryFont)
    {
        if (string.IsNullOrEmpty(engineEntry.Text))
            return default;

        float lineHeight = engineEntry.FontSize * (1f + engineEntry.LineSpacing);
        ushort spaceGlyphIndex = primaryFont.GetGlyphIndex(' ');
        float spaceAdvanceWidth = primaryFont.GetVariedAdvanceWidth(spaceGlyphIndex) *
                                  (engineEntry.FontSize / primaryFont.UnitsPerEm);
        bool isRtl = engineEntry.FlowDirection == TextFlowDirection.RightToLeft;

        float? minX = null;
        float? minY = null;
        float? maxX = null;
        float? maxY = null;

        var lines = engineEntry.Text.Split('\n');
        for (int lineIdx = 0; lineIdx < lines.Length; lineIdx++)
        {
            var line = lines[lineIdx];
            if (line.Length == 0)
                continue;

            float baselineY = lineIdx * lineHeight;
            var advances = new float[line.Length];
            var resolvedFonts = new FontFace?[line.Length];
            var resolvedIndices = new ushort[line.Length];
            float totalWidth = 0f;

            for (int i = 0; i < line.Length; i++)
            {
                char c = line[i];
                float advance;
                if (c == ' ')
                {
                    advance = spaceAdvanceWidth + engineEntry.WordSpacing + engineEntry.CharacterSpacing;
                }
                else
                {
                    var (resolvedFont, resolvedGlyphIndex) = ResolveCharForMeasurement(c, primaryFont);
                    resolvedFonts[i] = resolvedFont;
                    resolvedIndices[i] = resolvedGlyphIndex;
                    advance = ComputeCharacterAdvanceForMeasurement(
                        resolvedFont,
                        resolvedGlyphIndex,
                        engineEntry.FontSize,
                        engineEntry.CharacterSpacing);
                }

                advances[i] = advance;
                totalWidth += advance;
            }

            float xOffset = isRtl
                ? engineEntry.Alignment switch
                {
                    TextAlignment.Center => totalWidth * 0.5f,
                    TextAlignment.Right => 0f,
                    _ => totalWidth,
                }
                : engineEntry.Alignment switch
                {
                    TextAlignment.Center => -totalWidth * 0.5f,
                    TextAlignment.Right => -totalWidth,
                    _ => 0f,
                };

            float cursorX = xOffset;
            for (int i = 0; i < line.Length; i++)
            {
                if (isRtl)
                    cursorX -= advances[i];

                var resolvedFont = resolvedFonts[i];
                if (resolvedFont is not null &&
                    resolvedFont.TryGetGlyphBounds(resolvedIndices[i], out short gxMin, out short gyMin, out short gxMax, out short gyMax))
                {
                    float scale = engineEntry.FontSize / resolvedFont.UnitsPerEm;
                    float visualLeft = cursorX + gxMin * scale;
                    float visualRight = cursorX + gxMax * scale;
                    float visualTop = baselineY + gyMin * scale;
                    float visualBottom = baselineY + gyMax * scale;

                    minX = !minX.HasValue || visualLeft < minX.Value ? visualLeft : minX.Value;
                    minY = !minY.HasValue || visualTop < minY.Value ? visualTop : minY.Value;
                    maxX = !maxX.HasValue || visualRight > maxX.Value ? visualRight : maxX.Value;
                    maxY = !maxY.HasValue || visualBottom > maxY.Value ? visualBottom : maxY.Value;
                }

                if (!isRtl)
                    cursorX += advances[i];
            }
        }

        return minX.HasValue && minY.HasValue && maxX.HasValue && maxY.HasValue
            ? new HorizontalInkBounds(minX.Value, minY.Value, maxX.Value, maxY.Value)
            : default;
    }

    private static (FontFace font, ushort glyphIndex) ResolveCharForMeasurement(char c, FontFace primaryFont)
    {
        ushort idx = primaryFont.GetGlyphIndex(c);
        if (idx != 0)
            return (primaryFont, idx);

        foreach (var fallbackFont in TextClipFontRegistry.FallbackFonts)
        {
            if (fallbackFont is null || ReferenceEquals(fallbackFont, primaryFont))
                continue;

            idx = fallbackFont.GetGlyphIndex(c);
            if (idx != 0)
                return (fallbackFont, idx);
        }

        return (primaryFont, primaryFont.GetGlyphIndex(c));
    }

    private static float ComputeCharacterAdvanceForMeasurement(
        FontFace font,
        ushort glyphIndex,
        float charFontSize,
        float charCharacterSpacing)
    {
        if (charFontSize <= 0f || !float.IsFinite(charFontSize))
            return charCharacterSpacing;

        ushort unitsPerEm = font.UnitsPerEm;
        if (unitsPerEm == 0)
            return charCharacterSpacing;

        float advance = font.GetVariedAdvanceWidth(glyphIndex) * (charFontSize / unitsPerEm) + charCharacterSpacing;
        if (advance < charFontSize * 0.1f)
            advance = charFontSize * 0.5f;

        return advance;
    }

    private static TextEntry ApplyLineBreakIfNeeded(
        TextEntry engineEntry,
        FontFace primaryFont,
        TextEntry pixelEntryForWrapping,
        TextLayoutContext ctx)
    {
        if (engineEntry.GetUseVerticalLayout())
            return engineEntry;

        var pixelWW = pixelEntryForWrapping.GetWrappingWidth();
        if (!pixelWW.HasValue || pixelWW.Value <= 0.0001f)
            return engineEntry;

        var unit = ctx.UnitLength;
        var engineWW = pixelWW.Value / unit;

        var broken = LineBreakHandler.BreakLine(engineEntry, primaryFont, engineWW);
        return engineEntry with { Text = broken };
    }

    private static float ComputeVerticalAlignmentShiftPx(TextEntry pixelEntry, float measuredHeightPx, TextLayoutContext ctx)
    {
        var va = pixelEntry.GetVerticalAlignment();
        if (va == ClipVerticalAlignment.Top || measuredHeightPx <= 0f)
            return 0f;

        var clipH = ctx.CanvasHeight;
        if (clipH <= 0f) return 0f;

        return va switch
        {
            ClipVerticalAlignment.Center => MathF.Max(0f, (clipH - measuredHeightPx) / 2f),
            ClipVerticalAlignment.Bottom => MathF.Max(0f, clipH - measuredHeightPx),
            _ => 0f,
        };
    }

    private static float ComputeBaselineOffsetPx(HorizontalInkBounds inkBounds, float unit)
    {
        return MathF.Max(0f, -inkBounds.MinY * unit);
    }

    private static void ApplyAspectCompensation(VectorPicture layout, TextLayoutContext ctx, int targetW, int targetH)
    {
        if (targetW <= 0 || targetH <= 0) return;
        var canvasMin = ctx.UnitLength;
        var targetMin = MathF.Min(targetW, targetH);
        if (canvasMin <= 0f || targetMin <= 0f) return;

        // Pixel positions for per-glyph offsets are RelativeXY * min(target). We
        // want pixel positions to behave as if min was canvas's min instead, so
        // we scale by (canvasMin / targetMin) on the *expected target* side —
        // i.e., multiply Relative by (canvasMin / targetMin) * (targetMin / canvasMin) = 1
        // when target aspect matches canvas aspect. When they diverge we
        // compensate by scaling axes independently.
        var scaleX = (targetW / ctx.CanvasWidth) / (targetMin / canvasMin);
        var scaleY = (targetH / ctx.CanvasHeight) / (targetMin / canvasMin);

        if (MathF.Abs(scaleX - 1f) < 0.0001f && MathF.Abs(scaleY - 1f) < 0.0001f)
            return;

        foreach (var element in layout.Elements)
        {
            element.RelativeX *= scaleX;
            element.RelativeY *= scaleY;
        }
    }

    private static TextBounds RotateBoundsAround(TextBounds bounds, float centerX, float centerY, float angleRad)
    {
        var cos = MathF.Cos(angleRad);
        var sin = MathF.Sin(angleRad);

        static (float rx, float ry) Rot(float px, float py, float c, float s)
            => (px * c - py * s, px * s + py * c);

        var p0 = Rot(bounds.X - centerX, bounds.Y - centerY, cos, sin);
        var p1 = Rot(bounds.Right - centerX, bounds.Y - centerY, cos, sin);
        var p2 = Rot(bounds.X - centerX, bounds.Bottom - centerY, cos, sin);
        var p3 = Rot(bounds.Right - centerX, bounds.Bottom - centerY, cos, sin);

        var rMinX = MathF.Min(MathF.Min(p0.rx, p1.rx), MathF.Min(p2.rx, p3.rx));
        var rMinY = MathF.Min(MathF.Min(p0.ry, p1.ry), MathF.Min(p2.ry, p3.ry));
        var rMaxX = MathF.Max(MathF.Max(p0.rx, p1.rx), MathF.Max(p2.rx, p3.rx));
        var rMaxY = MathF.Max(MathF.Max(p0.ry, p1.ry), MathF.Max(p2.ry, p3.ry));

        return new TextBounds(
            centerX + rMinX,
            centerY + rMinY,
            MathF.Max(1f, rMaxX - rMinX),
            MathF.Max(1f, rMaxY - rMinY));
    }

    internal static FontFace? ResolveFont(TextEntry entry)
    {
        if (TextClipFontRegistry.TryGetFont(entry.FontName, out var primaryFont) && primaryFont is not null)
            return primaryFont;
        var fallbackName = TextClipFontRegistry.FallbackFamilyName;
        if (fallbackName is null) return null;
        if (TextClipFontRegistry.TryGetFont(fallbackName, out var fallbackFont))
            return fallbackFont;
        return null;
    }

    internal static float GetAscenderRatio(FontFace font)
    {
        if (font is null) return DefaultAscenderRatio;
        return AscenderCache.GetOrAdd(font, ComputeAscenderRatio);
    }

    private static float ComputeAscenderRatio(FontFace font)
    {
        try
        {
            if (font.UnitsPerEm <= 0)
                return DefaultAscenderRatio;

            // Sample a few representative glyphs (uppercase Latin + CJK 中) to
            // get a robust ascender estimate. We use the maximum yMax across
            // samples so the baseline never clips ascending characters.
            char[] samples = { 'A', 'H', 'M', '中', '日' };
            short maxYMax = 0;
            bool any = false;
            foreach (var ch in samples)
            {
                ushort idx = font.GetGlyphIndex(ch);
                if (idx == 0) continue;
                if (!font.TryGetGlyphBounds(idx, out _, out _, out _, out var yMax)) continue;
                if (yMax > maxYMax) maxYMax = yMax;
                any = true;
            }
            if (!any || maxYMax <= 0)
                return DefaultAscenderRatio;
            return (float)maxYMax / font.UnitsPerEm;
        }
        catch
        {
            return DefaultAscenderRatio;
        }
    }
}
