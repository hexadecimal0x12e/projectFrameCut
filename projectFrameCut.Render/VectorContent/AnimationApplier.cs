using projectFrameCut.Drawing.Vector;

namespace projectFrameCut.Render.VectorContent;

public static class AnimationApplier
{
    public static void ApplyFieldValue(VectorCanvasElement element, string fieldId, float value)
    {
        switch (fieldId)
        {
            case "RelativeX":
                element.RelativeX = value;
                return;
            case "RelativeY":
                element.RelativeY = value;
                return;
            case "Rotation":
                element.Rotation = value;
                return;
            case "BaseX":
                element.BaseX = value;
                return;
            case "BaseY":
                element.BaseY = value;
                return;
            case "LayerIndex":
                element.LayerIndex = (int)Math.Round(value);
                return;
        }

        if (element is not ShapeCanvasElement shape)
        {
            return;
        }

        switch (fieldId)
        {
            case "FillA":
                shape.TransformSegments(s => s with { FillA = Math.Clamp(value, 0f, 1f) });
                return;
            case "StrokeA":
                shape.TransformSegments(s => s with { StrokeA = Math.Clamp(value, 0f, 1f) });
                return;
            case "Thickness":
                shape.TransformSegments(s => s with { Thickness = Math.Max(0f, value) });
                return;
            case "FillR":
                shape.TransformSegments(s => s with { FillR = ToUShort(value) });
                return;
            case "FillG":
                shape.TransformSegments(s => s with { FillG = ToUShort(value) });
                return;
            case "FillB":
                shape.TransformSegments(s => s with { FillB = ToUShort(value) });
                return;
            case "StrokeR":
                shape.TransformSegments(s => s with { StrokeR = ToUShort(value) });
                return;
            case "StrokeG":
                shape.TransformSegments(s => s with { StrokeG = ToUShort(value) });
                return;
            case "StrokeB":
                shape.TransformSegments(s => s with { StrokeB = ToUShort(value) });
                return;
            case "Width":
                shape.TransformSegments(s => s switch
                {
                    RectangleVectorSegment r => r with { Width = Math.Max(0.001f, value) },
                    _ => s,
                });
                return;
            case "Height":
                shape.TransformSegments(s => s switch
                {
                    RectangleVectorSegment r => r with { Height = Math.Max(0.001f, value) },
                    _ => s,
                });
                return;
            case "CornerRadius":
                shape.TransformSegments(s => s switch
                {
                    RoundedRectangleVectorSegment rr => rr with { CornerRadius = Math.Max(0f, value) },
                    _ => s,
                });
                return;
            case "RadiusX":
                shape.TransformSegments(s => s switch
                {
                    EllipseVectorSegment e => e with { RadiusX = Math.Max(0.001f, value) },
                    ArcVectorSegment a => a with { RadiusX = Math.Max(0.001f, value) },
                    _ => s,
                });
                return;
            case "RadiusY":
                shape.TransformSegments(s => s switch
                {
                    EllipseVectorSegment e => e with { RadiusY = Math.Max(0.001f, value) },
                    ArcVectorSegment a => a with { RadiusY = Math.Max(0.001f, value) },
                    _ => s,
                });
                return;
            case "CenterX":
                shape.TransformSegments(s => s switch
                {
                    ArcVectorSegment a => a with { X = value },
                    _ => s,
                });
                return;
            case "CenterY":
                shape.TransformSegments(s => s switch
                {
                    ArcVectorSegment a => a with { Y = value },
                    _ => s,
                });
                return;
            case "StartAngle":
                shape.TransformSegments(s => s switch
                {
                    ArcVectorSegment a => a with { StartAngle = value },
                    _ => s,
                });
                return;
            case "SweepAngle":
                shape.TransformSegments(s => s switch
                {
                    ArcVectorSegment a => a with { SweepAngle = value },
                    _ => s,
                });
                return;
            case "X1":
                shape.TransformSegments(s => s switch
                {
                    StraightLineVectorSegment l => l with { X1 = value },
                    CubicBezierVectorSegment b => b with { X1 = value },
                    QuadraticBezierVectorSegment q => q with { X1 = value },
                    _ => s,
                });
                return;
            case "Y1":
                shape.TransformSegments(s => s switch
                {
                    StraightLineVectorSegment l => l with { Y1 = value },
                    CubicBezierVectorSegment b => b with { Y1 = value },
                    QuadraticBezierVectorSegment q => q with { Y1 = value },
                    _ => s,
                });
                return;
            case "X2":
                shape.TransformSegments(s => s switch
                {
                    StraightLineVectorSegment l => l with { X2 = value },
                    CubicBezierVectorSegment b => b with { X2 = value },
                    QuadraticBezierVectorSegment q => q with { X2 = value },
                    _ => s,
                });
                return;
            case "Y2":
                shape.TransformSegments(s => s switch
                {
                    StraightLineVectorSegment l => l with { Y2 = value },
                    CubicBezierVectorSegment b => b with { Y2 = value },
                    QuadraticBezierVectorSegment q => q with { Y2 = value },
                    _ => s,
                });
                return;
            case "X3":
                shape.TransformSegments(s => s switch
                {
                    CubicBezierVectorSegment b => b with { X3 = value },
                    QuadraticBezierVectorSegment q => q with { X3 = value },
                    _ => s,
                });
                return;
            case "Y3":
                shape.TransformSegments(s => s switch
                {
                    CubicBezierVectorSegment b => b with { Y3 = value },
                    QuadraticBezierVectorSegment q => q with { Y3 = value },
                    _ => s,
                });
                return;
            case "X4":
                shape.TransformSegments(s => s switch
                {
                    CubicBezierVectorSegment b => b with { X4 = value },
                    _ => s,
                });
                return;
            case "Y4":
                shape.TransformSegments(s => s switch
                {
                    CubicBezierVectorSegment b => b with { Y4 = value },
                    _ => s,
                });
                return;
        }
    }

    private static ushort ToUShort(float value) =>
        (ushort)Math.Clamp((int)Math.Round(value), ushort.MinValue, ushort.MaxValue);
}

