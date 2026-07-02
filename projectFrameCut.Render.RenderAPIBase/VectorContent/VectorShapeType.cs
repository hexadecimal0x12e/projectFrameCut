namespace projectFrameCut.Render.RenderAPIBase.VectorContent;

/// <summary>
/// Types of vector shapes that users can add to a composition.
/// </summary>
public enum VectorShapeType
{
    Rectangle,
    RoundedRectangle,
    Ellipse,
    Line,
    CubicBezier,
    QuadraticBezier,
    Arc,
    Polygon,
    Polyline,
    /// <summary>Imported SVG file — each file is one component containing all elements.</summary>
    ImportedSvg,
}
