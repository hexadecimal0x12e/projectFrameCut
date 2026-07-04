using projectFrameCut.Render.RenderAPIBase.VectorContent;

namespace projectFrameCut.Render.VectorContent;

public static class AnimatableFieldMap
{
    public static readonly IReadOnlyDictionary<string, AnimatableField> CommonFields =
        new Dictionary<string, AnimatableField>
        {
            ["RelativeX"] = Create("RelativeX", "Relative X", 0f, 1f),
            ["RelativeY"] = Create("RelativeY", "Relative Y", 0f, 1f),
            ["Rotation"] = Create("Rotation", "Rotation", -MathF.PI, MathF.PI),
            ["BaseX"] = Create("BaseX", "Base X", 0f, 1f),
            ["BaseY"] = Create("BaseY", "Base Y", 0f, 1f),
            ["LayerIndex"] = Create("LayerIndex", "Layer Index", 0f, 100f),
            ["StrokeR"] = Create("StrokeR", "Stroke R", 0f, ushort.MaxValue),
            ["StrokeG"] = Create("StrokeG", "Stroke G", 0f, ushort.MaxValue),
            ["StrokeB"] = Create("StrokeB", "Stroke B", 0f, ushort.MaxValue),
            ["StrokeA"] = Create("StrokeA", "Stroke Opacity", 0f, 1f),
            ["FillR"] = Create("FillR", "Fill R", 0f, ushort.MaxValue),
            ["FillG"] = Create("FillG", "Fill G", 0f, ushort.MaxValue),
            ["FillB"] = Create("FillB", "Fill B", 0f, ushort.MaxValue),
            ["FillA"] = Create("FillA", "Fill Opacity", 0f, 1f),
            ["Thickness"] = Create("Thickness", "Thickness", 0f, 0.1f),
        };

    public static readonly IReadOnlyDictionary<string, AnimatableField> ShapeFields =
        new Dictionary<string, AnimatableField>
        {
            ["Width"] = Create("Width", "Width", 0.001f, 1f),
            ["Height"] = Create("Height", "Height", 0.001f, 1f),
            ["CornerRadius"] = Create("CornerRadius", "Corner Radius", 0f, 0.5f),
            ["RadiusX"] = Create("RadiusX", "Radius X", 0.001f, 1f),
            ["RadiusY"] = Create("RadiusY", "Radius Y", 0.001f, 1f),
            ["CenterX"] = Create("CenterX", "Center X", 0f, 1f),
            ["CenterY"] = Create("CenterY", "Center Y", 0f, 1f),
            ["StartAngle"] = Create("StartAngle", "Start Angle", -MathF.PI * 2f, MathF.PI * 2f),
            ["SweepAngle"] = Create("SweepAngle", "Sweep Angle", -MathF.PI * 2f, MathF.PI * 2f),
            ["X1"] = Create("X1", "X1", 0f, 1f),
            ["Y1"] = Create("Y1", "Y1", 0f, 1f),
            ["X2"] = Create("X2", "X2", 0f, 1f),
            ["Y2"] = Create("Y2", "Y2", 0f, 1f),
            ["X3"] = Create("X3", "X3", 0f, 1f),
            ["Y3"] = Create("Y3", "Y3", 0f, 1f),
            ["X4"] = Create("X4", "X4", 0f, 1f),
            ["Y4"] = Create("Y4", "Y4", 0f, 1f),

            ["FontSize"] = Create("FontSize", "Font Size", 8f, 500f),
            ["CharacterSpacing"] = Create("CharacterSpacing", "Char Spacing", -20f, 100f),
            ["LineSpacing"] = Create("LineSpacing", "Line Spacing", 0f, 2f),
        };

    private static AnimatableField Create(string id, string name, float min, float max) =>
        new AnimatableField
        {
            Id = id,
            DisplayName = name,
            Description = name,
            MinimumValue = min,
            MaximumValue = max,
        };
}

