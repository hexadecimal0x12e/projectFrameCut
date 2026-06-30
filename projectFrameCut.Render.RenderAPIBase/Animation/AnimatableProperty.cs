namespace projectFrameCut.Render.RenderAPIBase.Animation;

/// <summary>
/// Identifies which property of a <see cref="Drawing.Vector.VectorCanvasElement"/> (or its
/// child <see cref="Drawing.Vector.VectorSegment"/> records) can be animated via
/// a <see cref="AnimationTrack"/>.
/// </summary>
public enum AnimatableProperty
{
    // ── Element-level transforms ──────────────────────────
    // These values are set directly on VectorCanvasElement.{get;set} properties.

    /// <summary>Horizontal position in normalized canvas space (0…1).</summary>
    RelativeX,

    /// <summary>Vertical position in normalized canvas space (0…1).</summary>
    RelativeY,

    /// <summary>Rotation angle in radians around the element's origin.</summary>
    Rotation,

    /// <summary>Canvas-space X origin for block-level positioning.</summary>
    BaseX,

    /// <summary>Canvas-space Y origin for block-level positioning.</summary>
    BaseY,

    // ── Segment-level appearance ───────────────────────────
    // These values are applied to every segment belonging to an element by
    // creating modified copies via the record's "with" expression.

    /// <summary>Fill opacity (0 = transparent, 1 = opaque) applied to all segments.</summary>
    FillColorA,

    /// <summary>Stroke opacity (0 = transparent, 1 = opaque) applied to all segments.</summary>
    StrokeColorA,
}
