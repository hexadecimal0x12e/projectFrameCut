namespace projectFrameCut.Render.ClipsAndTracks.Text;

/// <summary>
/// The project-pixel canvas a <see cref="TextLayoutPipeline"/> operation works against.
///
/// In the new text system every public <see cref="projectFrameCut.Drawing.Text.Entry.TextEntry"/>
/// field (<c>X</c>, <c>Y</c>, <c>FontSize</c>, <c>StrokeThickness</c>, <c>CharacterSpacing</c>,
/// <c>WordSpacing</c>, and the <c>WrappingWidth</c> stored in
/// <see cref="projectFrameCut.Shared.TextEntryExtraDataKeys.WrappingWidth"/>) is expressed in
/// the pixels of the canvas described by this context. The pipeline performs a single,
/// well-defined normalisation when feeding the typesetting engine and converts measured
/// outputs back to canvas pixels.
/// </summary>
public readonly record struct TextLayoutContext(float CanvasWidth, float CanvasHeight)
{
    /// <summary>
    /// Pick the smaller axis. This is the value used to convert "length" fields
    /// (FontSize, WrappingWidth, ...) to and from engine space, mirroring the
    /// rasterizer's <c>min(W,H)</c> uniform-square mapping for glyph paths.
    /// </summary>
    public float UnitLength => CanvasWidth > 0 && CanvasHeight > 0
        ? MathF.Min(CanvasWidth, CanvasHeight)
        : 1f;

    public static TextLayoutContext FromCanvas(float w, float h)
        => new(w > 0 ? w : 1920f, h > 0 ? h : 1080f);

    public static TextLayoutContext FromCanvas(int w, int h)
        => FromCanvas((float)w, (float)h);
}
