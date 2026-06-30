using projectFrameCut.Drawing.Vector;
using System;
using System.Collections.Generic;
using System.Linq;

namespace projectFrameCut.Render.RenderAPIBase.Animation;

/// <summary>
/// Converts a <see cref="VectorComponentDefinition"/> into a concrete
/// <see cref="ShapeCanvasElement"/> using the static factory methods
/// on <see cref="ShapeCanvasElement"/>.
/// </summary>
public static class VectorComponentFactory
{
    /// <summary>
    /// Create a <see cref="ShapeCanvasElement"/> from the given definition.
    /// </summary>
    public static ShapeCanvasElement CreateFromDefinition(VectorComponentDefinition def)
    {
        var shape = def.ShapeType switch
        {
            VectorShapeType.Rectangle => CreateRectangle(def),
            VectorShapeType.RoundedRectangle => CreateRoundedRectangle(def),
            VectorShapeType.Ellipse => CreateEllipse(def),
            VectorShapeType.Line => CreateLine(def),
            VectorShapeType.CubicBezier => CreateCubicBezier(def),
            VectorShapeType.QuadraticBezier => CreateQuadraticBezier(def),
            VectorShapeType.Arc => CreateArc(def),
            VectorShapeType.Polygon => CreatePolygon(def),
            VectorShapeType.Polyline => CreatePolyline(def),
            VectorShapeType.ImportedSvg => throw new InvalidOperationException(
                "ImportedSvg components cannot be built as a single ShapeCanvasElement. " +
                "Use VectorComponent.BuildElements() instead."),
            _ => throw new ArgumentOutOfRangeException(nameof(def.ShapeType), def.ShapeType, "Unknown shape type."),
        };

        // Apply visual properties
        if (def.Thickness > 0)
            shape = shape.WithStroke(def.StrokeR, def.StrokeG, def.StrokeB, def.StrokeA, def.Thickness);

        shape = shape.WithFill(def.FillR, def.FillG, def.FillB, def.FillA);

        // Apply element-level transform
        shape.WithPosition(def.RelativeX, def.RelativeY)
             .WithLayer(def.LayerIndex);

        shape.Rotation = def.Rotation;
        shape.BaseX = def.BaseX;
        shape.BaseY = def.BaseY;

        return shape;
    }

    // ── Shape-specific creators ─────────────────────────────

    private static ShapeCanvasElement CreateRectangle(VectorComponentDefinition def)
    {
        float w = GetParam(def, "Width", 0.3f);
        float h = GetParam(def, "Height", 0.3f);
        return ShapeCanvasElement.DrawRectangle(w, h);
    }

    private static ShapeCanvasElement CreateRoundedRectangle(VectorComponentDefinition def)
    {
        float w = GetParam(def, "Width", 0.3f);
        float h = GetParam(def, "Height", 0.3f);
        float r = GetParam(def, "CornerRadius", 0.05f);
        return ShapeCanvasElement.DrawRoundedRectangle(w, h, r);
    }

    private static ShapeCanvasElement CreateEllipse(VectorComponentDefinition def)
    {
        float rx = GetParam(def, "RadiusX", 0.15f);
        float ry = GetParam(def, "RadiusY", 0.15f);
        return ShapeCanvasElement.DrawEllipse(rx, ry);
    }

    private static ShapeCanvasElement CreateLine(VectorComponentDefinition def)
    {
        float x1 = GetParam(def, "X1", 0.1f);
        float y1 = GetParam(def, "Y1", 0.1f);
        float x2 = GetParam(def, "X2", 0.9f);
        float y2 = GetParam(def, "Y2", 0.9f);
        return ShapeCanvasElement.DrawLine(x1, y1, x2, y2);
    }

    private static ShapeCanvasElement CreateCubicBezier(VectorComponentDefinition def)
    {
        float x1 = GetParam(def, "X1", 0.1f);
        float y1 = GetParam(def, "Y1", 0.3f);
        float x2 = GetParam(def, "X2", 0.3f);
        float y2 = GetParam(def, "Y2", 0.7f);
        float x3 = GetParam(def, "X3", 0.7f);
        float y3 = GetParam(def, "Y3", 0.3f);
        float x4 = GetParam(def, "X4", 0.9f);
        float y4 = GetParam(def, "Y4", 0.7f);
        return ShapeCanvasElement.DrawCubicBezier(x1, y1, x2, y2, x3, y3, x4, y4);
    }

    private static ShapeCanvasElement CreateQuadraticBezier(VectorComponentDefinition def)
    {
        float x1 = GetParam(def, "X1", 0.1f);
        float y1 = GetParam(def, "Y1", 0.1f);
        float x2 = GetParam(def, "X2", 0.5f);
        float y2 = GetParam(def, "Y2", 0.9f);
        float x3 = GetParam(def, "X3", 0.9f);
        float y3 = GetParam(def, "Y3", 0.1f);
        return ShapeCanvasElement.DrawQuadraticBezier(x1, y1, x2, y2, x3, y3);
    }

    private static ShapeCanvasElement CreateArc(VectorComponentDefinition def)
    {
        float cx = GetParam(def, "CenterX", 0.5f);
        float cy = GetParam(def, "CenterY", 0.5f);
        float rx = GetParam(def, "RadiusX", 0.3f);
        float ry = GetParam(def, "RadiusY", 0.3f);
        float start = GetParam(def, "StartAngle", 0f);
        float sweep = GetParam(def, "SweepAngle", MathF.PI);
        return ShapeCanvasElement.DrawArc(cx, cy, rx, ry, start, sweep);
    }

    private static ShapeCanvasElement CreatePolygon(VectorComponentDefinition def)
    {
        var points = def.Points is { Count: >= 3 }
            ? def.Points.ToArray()
            : ShapeDefaults.GetDefaultPoints(VectorShapeType.Polygon);
        return ShapeCanvasElement.DrawPolygon(points);
    }

    private static ShapeCanvasElement CreatePolyline(VectorComponentDefinition def)
    {
        var points = def.Points is { Count: >= 2 }
            ? def.Points.ToArray()
            : ShapeDefaults.GetDefaultPoints(VectorShapeType.Polyline);
        return ShapeCanvasElement.DrawPolyline(points);
    }

    // ── Helpers ─────────────────────────────────────────────

    private static float GetParam(VectorComponentDefinition def, string key, float defaultValue)
    {
        if (def.ShapeParameters is not null && def.ShapeParameters.TryGetValue(key, out float value))
            return value;
        return defaultValue;
    }
}

// ═══════════════════════════════════════════════════════════
// ShapeDefaults — default parameter values per shape type
// ═══════════════════════════════════════════════════════════

/// <summary>
/// Provides sensible default shape parameters for each <see cref="VectorShapeType"/>.
/// </summary>
public static class ShapeDefaults
{
    public static Dictionary<string, float> GetDefaults(VectorShapeType type) => type switch
    {
        VectorShapeType.Rectangle => new() { ["Width"] = 0.3f, ["Height"] = 0.3f },
        VectorShapeType.RoundedRectangle => new() { ["Width"] = 0.3f, ["Height"] = 0.3f, ["CornerRadius"] = 0.05f },
        VectorShapeType.Ellipse => new() { ["RadiusX"] = 0.15f, ["RadiusY"] = 0.15f },
        VectorShapeType.Line => new() { ["X1"] = 0.1f, ["Y1"] = 0.1f, ["X2"] = 0.9f, ["Y2"] = 0.9f },
        VectorShapeType.CubicBezier => new()
        {
            ["X1"] = 0.1f, ["Y1"] = 0.3f, ["X2"] = 0.3f, ["Y2"] = 0.7f,
            ["X3"] = 0.7f, ["Y3"] = 0.3f, ["X4"] = 0.9f, ["Y4"] = 0.7f,
        },
        VectorShapeType.QuadraticBezier => new()
        {
            ["X1"] = 0.1f, ["Y1"] = 0.1f, ["X2"] = 0.5f, ["Y2"] = 0.9f,
            ["X3"] = 0.9f, ["Y3"] = 0.1f,
        },
        VectorShapeType.Arc => new()
        {
            ["CenterX"] = 0.5f, ["CenterY"] = 0.5f,
            ["RadiusX"] = 0.3f, ["RadiusY"] = 0.3f,
            ["StartAngle"] = 0f, ["SweepAngle"] = MathF.PI,
        },
        VectorShapeType.Polygon => new(),
        VectorShapeType.Polyline => new(),
        VectorShapeType.ImportedSvg => new(),
        _ => new(),
    };

    public static Point[] GetDefaultPoints(VectorShapeType type) => type switch
    {
        VectorShapeType.Polygon => new Point[]
        {
            new(0.3f, 0.3f), new(0.5f, 0.7f), new(0.7f, 0.3f),
        },
        VectorShapeType.Polyline => new Point[]
        {
            new(0.1f, 0.5f), new(0.3f, 0.3f), new(0.5f, 0.7f),
            new(0.7f, 0.3f), new(0.9f, 0.5f),
        },
        VectorShapeType.ImportedSvg => Array.Empty<Point>(),
        _ => Array.Empty<Point>(),
    };

    /// <summary>Human-readable names for each shape type.</summary>
    public static string GetDisplayName(VectorShapeType type) => type switch
    {
        VectorShapeType.Rectangle => "Rectangle",
        VectorShapeType.RoundedRectangle => "Rounded Rect",
        VectorShapeType.Ellipse => "Ellipse",
        VectorShapeType.Line => "Line",
        VectorShapeType.CubicBezier => "Cubic Bezier",
        VectorShapeType.QuadraticBezier => "Quad Bezier",
        VectorShapeType.Arc => "Arc",
        VectorShapeType.Polygon => "Polygon",
        VectorShapeType.Polyline => "Polyline",
        VectorShapeType.ImportedSvg => "SVG Import",
        _ => type.ToString(),
    };

    /// <summary>Unicode icon characters for each shape type.</summary>
    public static string GetIcon(VectorShapeType type) => type switch
    {
        VectorShapeType.Rectangle => "▭",
        VectorShapeType.RoundedRectangle => "▢",
        VectorShapeType.Ellipse => "⬭",
        VectorShapeType.Line => "╱",
        VectorShapeType.CubicBezier => "∿",
        VectorShapeType.QuadraticBezier => "⌈",
        VectorShapeType.Arc => "⌒",
        VectorShapeType.Polygon => "⬣",
        VectorShapeType.Polyline => "⦚",
        VectorShapeType.ImportedSvg => "📄",
        _ => "□",
    };
}
