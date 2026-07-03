namespace projectFrameCut.ApplicationAPIBase.VectorComponentHandler;

public enum ShapeHandlePositionType
{
    Anchor,
    Control,
    Radius,
    Center,
    Angle,
    Corner,
}

public class ShapeHandleDescriptor
{
    public string Id { get; init; } = string.Empty;
    public float NormalizedX { get; init; }
    public float NormalizedY { get; init; }
    public ShapeHandlePositionType PositionType { get; init; } = ShapeHandlePositionType.Anchor;
}

