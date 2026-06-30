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
        }
    }

    private static float ClampAlpha(float value) => Math.Clamp(value, 0f, 1f);
}
