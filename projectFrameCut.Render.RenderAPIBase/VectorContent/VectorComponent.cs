using projectFrameCut.Drawing.Vector;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json.Serialization;

namespace projectFrameCut.Render.RenderAPIBase.VectorContent;

/// <summary>
/// Runtime aggregation of a component definition and its vector animation.
/// Combines shape creation (from the definition) with per-component
/// animation (from the vector animation) to produce animated elements
/// for any given clip frame.
/// </summary>
public class VectorComponent
{
    /// <summary>Shape blueprint — type, position, colours, dimensions.</summary>
    public VectorComponentDefinition Definition { get; set; } = new();

    /// <summary>Per-component animation timeline.</summary>
    public ComponentAnimations Timeline { get; set; } = new();

    // ── SVG import cache ───────────────────────────────────

    /// <summary>
    /// Runtime cache of parsed SVG elements. Populated when the component
    /// is an <see cref="VectorShapeType.ImportedSvg"/> type and the source
    /// file is loaded. Not serialized — rebuilt from <see cref="VectorComponentDefinition.SourceFilePath"/>
    /// on deserialization.
    /// </summary>
    [JsonIgnore]
    public List<VectorCanvasElement>? CachedElements { get; set; }

    // ── Single-element methods (backward compat) ───────────

    /// <summary>
    /// Build a fresh <see cref="ShapeCanvasElement"/> from the definition
    /// using the appropriate factory method. Throws for <see cref="VectorShapeType.ImportedSvg"/>
    /// — use <see cref="BuildElements"/> for multi-element components.
    /// </summary>
    public ShapeCanvasElement BuildElement()
    {
        return VectorComponentFactory.CreateFromDefinition(Definition);
    }

    /// <summary>
    /// Build the element and apply this component's vector animation for the
    /// given clip frame. For SVG components, returns the first element.
    /// Prefer <see cref="GetAnimatedElements"/> for multi-element components.
    /// </summary>
    public VectorCanvasElement GetAnimatedElement(uint clipFrame, uint clipDuration)
    {
        var results = GetAnimatedElements(clipFrame, clipDuration);
        return results.Count > 0 ? results[0] : BuildElement();
    }

    // ── Multi-element methods ──────────────────────────────

    /// <summary>
    /// Builds all visual elements from this component.
    /// For manual shapes: returns a single ShapeCanvasElement.
    /// For SVG imports: returns cloned cached SVG elements, or an empty list
    /// if the SVG could not be loaded.
    /// </summary>
    public List<VectorCanvasElement> BuildElements()
    {
        if (Definition.ShapeType == VectorShapeType.ImportedSvg)
        {
            if (CachedElements is null || CachedElements.Count == 0)
                return new();

            // Deep clone each element so the cache remains pristine
            return CachedElements.Select(DeepCloneElement).ToList();
        }

        return new List<VectorCanvasElement>
        {
            VectorComponentFactory.CreateFromDefinition(Definition)
        };
    }

    /// <summary>
    /// Builds all elements and applies per-element animation.
    /// For SVG imports, each <see cref="AnimationTrack.ElementIndex"/> references
    /// the index within the cached SVG elements.
    /// For manual shapes, all tracks are applied to the single element.
    /// </summary>
    public List<VectorCanvasElement> GetAnimatedElements(uint clipFrame, uint clipDuration)
    {
        var elements = BuildElements();
        if (elements.Count == 0)
            return elements;

        if (Definition.ShapeType == VectorShapeType.ImportedSvg)
        {
            ApplySvgAnimation(elements, clipFrame, clipDuration);
        }
        else
        {
            ApplySingleAnimation(elements, clipFrame, clipDuration);
        }

        return elements;
    }

    // ── Animation helpers ──────────────────────────────────

    private void ApplySvgAnimation(List<VectorCanvasElement> elements, uint clipFrame, uint clipDuration)
    {
        if (Timeline.Tracks is null || Timeline.Tracks.Count == 0)
            return;

        float localProgress = Timeline.CalculateLocalProgress(clipFrame, clipDuration);

        foreach (var track in Timeline.Tracks)
        {
            if (track is null || track.KeyFrames is null || track.KeyFrames.Count == 0)
                continue;

            int idx = track.ElementIndex;
            if (idx < 0 || idx >= elements.Count)
                continue;

            if (elements[idx] is not ShapeCanvasElement shape)
                continue;

            var cloned = shape.Clone();
            float value = track.GetValue(localProgress);
            VectorAnimations.ApplyValue(cloned, track.Property, value);
            elements[idx] = cloned;
        }
    }

    private void ApplySingleAnimation(List<VectorCanvasElement> elements, uint clipFrame, uint clipDuration)
    {
        if (elements[0] is ShapeCanvasElement shape)
        {
            var animated = Timeline.Apply(shape, clipFrame, clipDuration);
            elements[0] = animated;
        }
    }

    // ── Helpers ────────────────────────────────────────────

    private static VectorCanvasElement DeepCloneElement(VectorCanvasElement original)
    {
        return original switch
        {
            ShapeCanvasElement shape => shape.Clone(),
            _ => original, // Non-shape elements: return as-is (best effort)
        };
    }
}
