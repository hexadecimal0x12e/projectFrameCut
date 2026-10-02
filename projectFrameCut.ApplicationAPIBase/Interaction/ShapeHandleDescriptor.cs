namespace projectFrameCut.ApplicationAPIBase.Interaction;

public enum ShapeHandlePositionType
{
    Anchor,
    Control,
    Radius,
    Center,
    Angle,
    Corner,
}

/// <summary>Describes a component handle and its editor appearance.</summary>
public class ShapeHandleDescriptor
{
    public ShapeHandleDescriptor() { }

    public ShapeHandleDescriptor(string id, float normalizedX, float normalizedY, Color fillColor, double size, Func<View>? customHandleFactory = null)
    {
        Id = id;
        NormalizedX = normalizedX;
        NormalizedY = normalizedY;
        FillColor = fillColor;
        Size = size;
        CustomHandleFactory = customHandleFactory;
    }

    public string Id { get; init; } = string.Empty;
    public float NormalizedX { get; init; }
    public float NormalizedY { get; init; }
    public ShapeHandlePositionType PositionType { get; init; } = ShapeHandlePositionType.Anchor;
    public Color FillColor { get; init; } = Colors.Orange;
    public double Size { get; init; } = 12;
    public Func<View>? CustomHandleFactory { get; set; }
}
