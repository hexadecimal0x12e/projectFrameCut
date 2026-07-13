namespace projectFrameCut.ApplicationAPIBase.VectorComponentHandler;

/// <summary>
/// A set of preset handle types.
/// </summary>
public enum ShapeHandlePositionType
{
    Anchor,
    Control,
    Radius,
    Center,
    Angle,
    Corner,
}

/// <summary>
/// A struct for describing a shape handle, including its position and type.
/// </summary>
public class ShapeHandleDescriptor
{
    /// <summary>
    /// The unique identifier for this handle. This is used to identify which handle is being dragged in ApplyHandleDrag.
    /// </summary>
    public string Id { get; init; } = string.Empty;
    /// <summary>
    /// The normalized X position of the handle, in the range [0, 1], relative to the shape's bounding box.
    /// </summary>
    public float NormalizedX { get; init; }
    /// <summary>
    /// The normalized Y position of the handle, in the range [0, 1], relative to the shape's bounding box.
    /// </summary>
    public float NormalizedY { get; init; }
    /// <summary>
    /// The type of the handle, which determines how it affects the shape when dragged.
    /// When <see cref="CustomHandleFactory"/> is set, this property is ignored.
    /// </summary>
    public ShapeHandlePositionType PositionType { get; init; } = ShapeHandlePositionType.Anchor;
    /// <summary>
    /// A factory function that creates a custom handle view for this handle. If set, this will be used instead of the default handle view.
    /// </summary>
    public Func<View>? CustomHandleFactory { get; set; } = null;
}

