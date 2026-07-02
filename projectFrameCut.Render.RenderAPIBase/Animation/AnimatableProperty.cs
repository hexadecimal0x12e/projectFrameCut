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

    // ── Shape-specific dimensions ───────────────────────────
    // These values are applied to specific segment types via TransformSegments
    // to modify geometric properties (width, height, radius, control points, etc.).

    /// <summary>Width of Rectangle / RoundedRectangle segments.</summary>
    ShapeWidth,

    /// <summary>Height of Rectangle / RoundedRectangle segments.</summary>
    ShapeHeight,

    /// <summary>Corner radius of RoundedRectangle segments.</summary>
    ShapeCornerRadius,

    /// <summary>X-radius of Ellipse / Arc segments.</summary>
    ShapeRadiusX,

    /// <summary>Y-radius of Ellipse / Arc segments.</summary>
    ShapeRadiusY,

    /// <summary>Start angle (radians) of Arc segments.</summary>
    ShapeStartAngle,

    /// <summary>Sweep angle (radians) of Arc segments.</summary>
    ShapeSweepAngle,

    /// <summary>Center X of Arc segments.</summary>
    ShapeCenterX,

    /// <summary>Center Y of Arc segments.</summary>
    ShapeCenterY,

    /// <summary>First control-point X (Line, CubicBezier, QuadraticBezier).</summary>
    ShapePointX1,

    /// <summary>First control-point Y (Line, CubicBezier, QuadraticBezier).</summary>
    ShapePointY1,

    /// <summary>Second control-point X (Line, CubicBezier, QuadraticBezier).</summary>
    ShapePointX2,

    /// <summary>Second control-point Y (Line, CubicBezier, QuadraticBezier).</summary>
    ShapePointY2,

    /// <summary>Third control-point X (CubicBezier, QuadraticBezier).</summary>
    ShapePointX3,

    /// <summary>Third control-point Y (CubicBezier, QuadraticBezier).</summary>
    ShapePointY3,

    /// <summary>Fourth control-point X (CubicBezier only).</summary>
    ShapePointX4,

    /// <summary>Fourth control-point Y (CubicBezier only).</summary>
    ShapePointY4,
}
