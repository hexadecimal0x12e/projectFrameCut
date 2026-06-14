using projectFrameCut.Drawing.Text.Entry;
using projectFrameCut.Drawing.Text.FontHelper;
using projectFrameCut.Drawing.Text.Typology;
using projectFrameCut.Shared;

namespace projectFrameCut.Render.ClipsAndTracks.Text;

/// <summary>
/// Layout-mode specific helpers used by <see cref="TextLayoutPipeline"/>.
/// Currently the only non-trivial mode is <see cref="TextClipLayoutMode.FixedHeight"/>,
/// which auto-fits the font size so the rendered text block matches a given height.
/// </summary>
internal static class TextLayoutModeResolver
{
    /// <summary>
    /// Return a copy of <paramref name="pixelEntry"/> with its <c>FontSize</c>
    /// (and proportionally its <c>StrokeThickness</c>) scaled so the
    /// measured pixel height equals <paramref name="targetHeightPx"/>.
    /// </summary>
    public static TextEntry FitFontSizeToHeight(TextEntry pixelEntry, FontFace primaryFont, TextLayoutContext ctx, float targetHeightPx)
    {
        if (string.IsNullOrEmpty(pixelEntry.Text) || targetHeightPx <= 0f || pixelEntry.FontSize <= 0f)
            return pixelEntry;

        // The engine's measured height scales linearly with FontSize when the
        // text doesn't reflow (which is true here: FixedHeight may still have
        // a separately-set WrappingWidth, but a single line's height likewise
        // scales linearly). Single measurement + scalar adjustment suffices
        // for the no-wrapping case; if the entry has wrapping, an extra pass
        // refines the size after the new wrapping has been applied.
        var measureEngineEntry = TextLayoutPipeline.ToEngineSpace(pixelEntry, ctx);
        var (_, heightEng) = TextLayoutPipeline.MeasureInEngineSpace(measureEngineEntry, primaryFont);

        var measuredHeightPx = heightEng * ctx.UnitLength;
        if (measuredHeightPx <= 0.0001f)
            return pixelEntry;

        var scale = targetHeightPx / measuredHeightPx;
        if (float.IsNaN(scale) || float.IsInfinity(scale) || scale <= 0f)
            return pixelEntry;

        var adjusted = pixelEntry with
        {
            FontSize = pixelEntry.FontSize * scale,
            StrokeThickness = pixelEntry.StrokeThickness * scale,
            CharacterSpacing = pixelEntry.CharacterSpacing * scale,
            WordSpacing = pixelEntry.WordSpacing * scale,
            ExtraData = new Dictionary<string, object>(pixelEntry.ExtraData),
        };

        // If the entry has a wrapping width, the linear assumption breaks: the
        // bigger font wraps more, height grows, our scale undershoots. Refine
        // once with the new font size; this is enough for typical text.
        var ww = adjusted.GetWrappingWidth();
        if (ww.HasValue && ww.Value > 0f)
        {
            var refineEngineEntry = TextLayoutPipeline.ToEngineSpace(adjusted, ctx);
            var (_, refinedHeightEng) = TextLayoutPipeline.MeasureInEngineSpace(refineEngineEntry, primaryFont);
            var refinedHeightPx = refinedHeightEng * ctx.UnitLength;
            if (refinedHeightPx > 0.0001f)
            {
                var refinementScale = targetHeightPx / refinedHeightPx;
                if (!float.IsNaN(refinementScale) && !float.IsInfinity(refinementScale) && refinementScale > 0f)
                {
                    adjusted = adjusted with
                    {
                        FontSize = adjusted.FontSize * refinementScale,
                        StrokeThickness = adjusted.StrokeThickness * refinementScale,
                        CharacterSpacing = adjusted.CharacterSpacing * refinementScale,
                        WordSpacing = adjusted.WordSpacing * refinementScale,
                    };
                }
            }
        }

        return adjusted;
    }
}
