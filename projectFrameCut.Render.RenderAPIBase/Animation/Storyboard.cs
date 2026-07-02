using projectFrameCut.Drawing.Vector;
using System;
using System.Collections.Generic;
using System.Linq;

namespace projectFrameCut.Render.RenderAPIBase.Animation;

/// <summary>
/// A complete animation definition: a collection of <see cref="AnimationTrack"/>s
/// that, together, describe how a <see cref="VectorPicture"/> changes over time.
/// </summary>
public class Storyboard
{
    /// <summary>
    /// Duration of the entire animation in source frames.
    /// Used to map frame indices to normalised progress [0…1].
    /// </summary>
    public uint DurationInFrames { get; set; } = 30;

    /// <summary>All animation tracks in this storyboard.</summary>
    public List<AnimationTrack> Tracks { get; set; } = new();

    /// <summary>
    /// Create an animated <see cref="VectorPicture"/> for the given normalised
    /// <paramref name="progress"/> [0…1] by cloning the <paramref name="source"/>
    /// picture and mutating only the elements that have active tracks.
    /// </summary>
    /// <param name="source">The base (static) vector picture. Not modified.</param>
    /// <param name="progress">Normalised time [0…1].</param>
    /// <returns>A new <see cref="VectorPicture"/> with animation applied.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="source"/> is null.</exception>
    /// <exception cref="NotSupportedException">
    /// An animated element is of a type that does not support deep-cloning.
    /// </exception>
    public VectorPicture Apply(VectorPicture source, float progress)
    {
        if (source is null)
            throw new ArgumentNullException(nameof(source));

        int count = source.Elements.Count;
        if (count == 0)
            return new VectorPicture();

        // Stage 1: shallow-clone the Elements array (shared references initially)
        var clonedElements = new VectorCanvasElement[count];
        for (int i = 0; i < count; i++)
            clonedElements[i] = source.Elements[i];

        // Stage 2: deep-clone only the elements touched by tracks and apply values
        foreach (var track in Tracks)
        {
            if (track is null || track.KeyFrames is null || track.KeyFrames.Count == 0)
                continue;

            int idx = track.ElementIndex;
            if (idx < 0 || idx >= count)
                continue;

            // Deep-clone on first touch for this element
            if (ReferenceEquals(clonedElements[idx], source.Elements[idx]))
                clonedElements[idx] = DeepCloneElement(source.Elements[idx]);

            float value = track.GetValue(progress);
            ApplyValue(clonedElements[idx], track.Property, value);
        }

        return new VectorPicture
        {
            Elements = clonedElements.ToList(),
        };
    }

    // ---------------------------------------------------------------
    // Private helpers
    // ---------------------------------------------------------------

    private static VectorCanvasElement DeepCloneElement(VectorCanvasElement original)
    {
        return original switch
        {
            ShapeCanvasElement shape => shape.Clone(),
            _ => throw new NotSupportedException(
                $"Animation does not support deep-cloning element type " +
                $"\"{original.GetType().Name}\". Only ShapeCanvasElement is supported.")
        };
    }

    /// <summary>
    /// Apply an animated value to a specific property of an element.
    /// Made public so <see cref="ComponentStoryboard"/> can reuse this logic
    /// for per-component animation.
    /// </summary>
    public static void ApplyValue(
        VectorCanvasElement element, AnimatableProperty property, float value)
    {
        switch (property)
        {
            case AnimatableProperty.RelativeX:
                element.RelativeX = value;
                break;
            case AnimatableProperty.RelativeY:
                element.RelativeY = value;
                break;
            case AnimatableProperty.Rotation:
                element.Rotation = value;
                break;
            case AnimatableProperty.BaseX:
                element.BaseX = value;
                break;
            case AnimatableProperty.BaseY:
                element.BaseY = value;
                break;

            case AnimatableProperty.FillColorA:
                if (element is ShapeCanvasElement fillShape)
                    fillShape.TransformSegments(s => s with { FillA = ClampAlpha(value) });
                break;

            case AnimatableProperty.StrokeColorA:
                if (element is ShapeCanvasElement strokeShape)
                    strokeShape.TransformSegments(s => s with { StrokeA = ClampAlpha(value) });
                break;

            // ── Shape-specific dimensions ────────────────────

            case AnimatableProperty.ShapeWidth:
                if (element is ShapeCanvasElement sShape)
                    sShape.TransformSegments(s => s switch
                    {
                        RectangleVectorSegment r => r with { Width = Math.Max(0.0001f, value) },
                        _ => s,
                    });
                break;

            case AnimatableProperty.ShapeHeight:
                if (element is ShapeCanvasElement sShape2)
                    sShape2.TransformSegments(s => s switch
                    {
                        RectangleVectorSegment r => r with { Height = Math.Max(0.0001f, value) },
                        _ => s,
                    });
                break;

            case AnimatableProperty.ShapeCornerRadius:
                if (element is ShapeCanvasElement sShape3)
                    sShape3.TransformSegments(s => s switch
                    {
                        RoundedRectangleVectorSegment rr => rr with { CornerRadius = Math.Max(0f, value) },
                        _ => s,
                    });
                break;

            case AnimatableProperty.ShapeRadiusX:
                if (element is ShapeCanvasElement sShape4)
                    sShape4.TransformSegments(s => s switch
                    {
                        EllipseVectorSegment e => e with { RadiusX = Math.Max(0.0001f, value) },
                        ArcVectorSegment a => a with { RadiusX = Math.Max(0.0001f, value) },
                        _ => s,
                    });
                break;

            case AnimatableProperty.ShapeRadiusY:
                if (element is ShapeCanvasElement sShape5)
                    sShape5.TransformSegments(s => s switch
                    {
                        EllipseVectorSegment e => e with { RadiusY = Math.Max(0.0001f, value) },
                        ArcVectorSegment a => a with { RadiusY = Math.Max(0.0001f, value) },
                        _ => s,
                    });
                break;

            case AnimatableProperty.ShapeStartAngle:
                if (element is ShapeCanvasElement sShape6)
                    sShape6.TransformSegments(s => s switch
                    {
                        ArcVectorSegment a => a with { StartAngle = value },
                        _ => s,
                    });
                break;

            case AnimatableProperty.ShapeSweepAngle:
                if (element is ShapeCanvasElement sShape7)
                    sShape7.TransformSegments(s => s switch
                    {
                        ArcVectorSegment a => a with { SweepAngle = value },
                        _ => s,
                    });
                break;

            case AnimatableProperty.ShapeCenterX:
                if (element is ShapeCanvasElement sShape8)
                    sShape8.TransformSegments(s => s switch
                    {
                        ArcVectorSegment a => a with { X = value },
                        _ => s,
                    });
                break;

            case AnimatableProperty.ShapeCenterY:
                if (element is ShapeCanvasElement sShape9)
                    sShape9.TransformSegments(s => s switch
                    {
                        ArcVectorSegment a => a with { Y = value },
                        _ => s,
                    });
                break;

            case AnimatableProperty.ShapePointX1:
                if (element is ShapeCanvasElement sShape10)
                    sShape10.TransformSegments(s => s switch
                    {
                        StraightLineVectorSegment l => l with { X1 = value },
                        CubicBezierVectorSegment b => b with { X1 = value },
                        QuadraticBezierVectorSegment q => q with { X1 = value },
                        _ => s,
                    });
                break;

            case AnimatableProperty.ShapePointY1:
                if (element is ShapeCanvasElement sShape11)
                    sShape11.TransformSegments(s => s switch
                    {
                        StraightLineVectorSegment l => l with { Y1 = value },
                        CubicBezierVectorSegment b => b with { Y1 = value },
                        QuadraticBezierVectorSegment q => q with { Y1 = value },
                        _ => s,
                    });
                break;

            case AnimatableProperty.ShapePointX2:
                if (element is ShapeCanvasElement sShape12)
                    sShape12.TransformSegments(s => s switch
                    {
                        StraightLineVectorSegment l => l with { X2 = value },
                        CubicBezierVectorSegment b => b with { X2 = value },
                        QuadraticBezierVectorSegment q => q with { X2 = value },
                        _ => s,
                    });
                break;

            case AnimatableProperty.ShapePointY2:
                if (element is ShapeCanvasElement sShape13)
                    sShape13.TransformSegments(s => s switch
                    {
                        StraightLineVectorSegment l => l with { Y2 = value },
                        CubicBezierVectorSegment b => b with { Y2 = value },
                        QuadraticBezierVectorSegment q => q with { Y2 = value },
                        _ => s,
                    });
                break;

            case AnimatableProperty.ShapePointX3:
                if (element is ShapeCanvasElement sShape14)
                    sShape14.TransformSegments(s => s switch
                    {
                        CubicBezierVectorSegment b => b with { X3 = value },
                        QuadraticBezierVectorSegment q => q with { X3 = value },
                        _ => s,
                    });
                break;

            case AnimatableProperty.ShapePointY3:
                if (element is ShapeCanvasElement sShape15)
                    sShape15.TransformSegments(s => s switch
                    {
                        CubicBezierVectorSegment b => b with { Y3 = value },
                        QuadraticBezierVectorSegment q => q with { Y3 = value },
                        _ => s,
                    });
                break;

            case AnimatableProperty.ShapePointX4:
                if (element is ShapeCanvasElement sShape16)
                    sShape16.TransformSegments(s => s switch
                    {
                        CubicBezierVectorSegment b => b with { X4 = value },
                        _ => s,
                    });
                break;

            case AnimatableProperty.ShapePointY4:
                if (element is ShapeCanvasElement sShape17)
                    sShape17.TransformSegments(s => s switch
                    {
                        CubicBezierVectorSegment b => b with { Y4 = value },
                        _ => s,
                    });
                break;
        }
    }

    private static float ClampAlpha(float value) => Math.Clamp(value, 0f, 1f);
}
