using projectFrameCut.Drawing.Vector;

namespace projectFrameCut.ApplicationAPIBase.VectorComponentHandler;

/// <summary>Geometry transformed into the current clip viewport, with its size in canvas pixels.</summary>
public sealed record VectorHandlePreviewContext(IReadOnlyList<VectorCanvasElement> Elements, float Width, float Height);
