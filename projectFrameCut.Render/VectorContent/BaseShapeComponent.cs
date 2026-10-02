using projectFrameCut.Drawing.Vector;
using projectFrameCut.Render.Plugin;
using projectFrameCut.Render.RenderAPIBase.VectorContent;
using System.Text.Json;

namespace projectFrameCut.Render.VectorContent;

public abstract class BaseShapeComponent : IVectorComponent
{
    public string FromPlugin => InternalPluginBase.InternalPluginBaseID;
    public abstract string TypeName { get; }
    public string Name { get; set; } = string.Empty;
    public Guid Id { get; set; } = Guid.NewGuid();
    public Dictionary<string, object> Parameters { get; set; } = new();
    public int Index { get; set; }

    public List<Point> GetPoints()
    {
        EnsureDefaultParameters();
        if (!Parameters.TryGetValue("Points", out var value)) return [];
        if (value is List<Point> points) return points;
        var restored = value switch
        {
            JsonElement e when e.ValueKind == JsonValueKind.Array => e.Deserialize<List<Point>>(),
            Point[] array => array.ToList(),
            _ => null
        } ?? [];
        Parameters["Points"] = restored;
        return restored;
    }

    protected abstract Dictionary<string, object> GetDefaultParameters();
    protected abstract ShapeCanvasElement BuildBaseShape();

    protected BaseShapeComponent()
    {
        EnsureDefaultParameters();
    }

    private void EnsureDefaultParameters()
    {
        Parameters ??= new();

        foreach (var (key, value) in GetDefaultParameters())
        {
            Parameters.TryAdd(key, value);
        }

        Parameters.TryAdd("RelativeX", 0.5f);
        Parameters.TryAdd("RelativeY", 0.5f);
        Parameters.TryAdd("Rotation", 0f);
        Parameters.TryAdd("BaseX", 0f);
        Parameters.TryAdd("BaseY", 0f);
        Parameters.TryAdd("LayerIndex", 0);
        Parameters.TryAdd("StrokeR", (float)ushort.MaxValue);
        Parameters.TryAdd("StrokeG", (float)ushort.MaxValue);
        Parameters.TryAdd("StrokeB", (float)ushort.MaxValue);
        Parameters.TryAdd("StrokeA", 1f);
        Parameters.TryAdd("FillR", 0f);
        Parameters.TryAdd("FillG", 0f);
        Parameters.TryAdd("FillB", 0f);
        Parameters.TryAdd("FillA", 1f);
        Parameters.TryAdd("Thickness", 2f);
    }

    public VectorCanvasElement Compute()
    {
        EnsureDefaultParameters();

        var shape = BuildBaseShape();
        shape = ApplyVisualProperties(shape);
        return shape;
    }

    protected ShapeCanvasElement ApplyVisualProperties(ShapeCanvasElement shape)
    {
        shape = shape.WithStroke(
            Parameters.GetUShort("StrokeR", ushort.MaxValue),
            Parameters.GetUShort("StrokeG", ushort.MaxValue),
            Parameters.GetUShort("StrokeB", ushort.MaxValue),
            Parameters.GetFloat("StrokeA", 1f),
            Parameters.GetFloat("Thickness", 2f));

        shape = shape.WithFill(
            Parameters.GetUShort("FillR", 0),
            Parameters.GetUShort("FillG", 0),
            Parameters.GetUShort("FillB", 0),
            Parameters.GetFloat("FillA", 1f));

        shape.RelativeX = Parameters.GetFloat("RelativeX", 0.5f);
        shape.RelativeY = Parameters.GetFloat("RelativeY", 0.5f);
        shape.Rotation = Parameters.GetFloat("Rotation", 0f);
        shape.BaseX = Parameters.GetFloat("BaseX", 0f);
        shape.BaseY = Parameters.GetFloat("BaseY", 0f);
        shape.LayerIndex = (int)Parameters.GetFloat("LayerIndex", Index);
        return shape;
    }


}
