using projectFrameCut.Drawing.Vector;
using System.Collections.Generic;

namespace projectFrameCut.Render.RenderAPIBase.Animation;

/// <summary>
/// Serializable blueprint for a user-created vector shape.
/// Stores shape type, element-level transforms, visual properties,
/// and shape-specific creation parameters.
/// </summary>
public class VectorComponentDefinition
{
    /// <summary>Unique identifier for this component.</summary>
    public System.Guid Id { get; set; } = System.Guid.NewGuid();

    /// <summary>User-visible name.</summary>
    public string DisplayName { get; set; } = "";

    /// <summary>Type of shape to create.</summary>
    public VectorShapeType ShapeType { get; set; }

    // ── Element-level transform (mirrors VectorCanvasElement) ──

    /// <summary>Normalised X position [0..1] within the canvas.</summary>
    public float RelativeX { get; set; } = 0.5f;

    /// <summary>Normalised Y position [0..1] within the canvas.</summary>
    public float RelativeY { get; set; } = 0.5f;

    /// <summary>Block-level X origin in canvas space.</summary>
    public float BaseX { get; set; }

    /// <summary>Block-level Y origin in canvas space.</summary>
    public float BaseY { get; set; }

    /// <summary>Rotation angle in radians.</summary>
    public float Rotation { get; set; }

    /// <summary>Z-order within the picture.</summary>
    public int LayerIndex { get; set; }

    // ── Visual defaults for all segments ──

    /// <summary>Stroke red channel [0..65535].</summary>
    public ushort StrokeR { get; set; } = ushort.MaxValue;

    /// <summary>Stroke green channel [0..65535].</summary>
    public ushort StrokeG { get; set; } = ushort.MaxValue;

    /// <summary>Stroke blue channel [0..65535].</summary>
    public ushort StrokeB { get; set; } = ushort.MaxValue;

    /// <summary>Stroke opacity [0..1].</summary>
    public float StrokeA { get; set; } = 1f;

    /// <summary>Stroke thickness in canvas units.</summary>
    public float Thickness { get; set; } = 2f;

    /// <summary>Fill red channel [0..65535].</summary>
    public ushort FillR { get; set; }

    /// <summary>Fill green channel [0..65535].</summary>
    public ushort FillG { get; set; }

    /// <summary>Fill blue channel [0..65535].</summary>
    public ushort FillB { get; set; }

    /// <summary>Fill opacity [0..1].</summary>
    public float FillA { get; set; } = 1f;

    // ── Shape-specific dimensions ──

    /// <summary>
    /// Shape-specific parameters keyed by name.
    /// Keys depend on <see cref="ShapeType"/> — see <c>ShapeDefaults</c> for the schema.
    /// </summary>
    public Dictionary<string, float> ShapeParameters { get; set; } = new();

    // ── Variable-length vertices (Polygon / Polyline only) ──

    /// <summary>Vertex positions for Polygon and Polyline shapes.</summary>
    public List<Point> Points { get; set; } = new();

    // ── SVG import ────────────────────────────────────────

    /// <summary>
    /// File path to the source SVG file. Only set when <see cref="ShapeType"/>
    /// is <see cref="VectorShapeType.ImportedSvg"/>. Used to reload the SVG
    /// elements on deserialization.
    /// </summary>
    public string? SourceFilePath { get; set; }
}
