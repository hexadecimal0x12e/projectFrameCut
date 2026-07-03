using projectFrameCut.Drawing.Vector;
using projectFrameCut.Render.Plugin;
using projectFrameCut.Render.RenderAPIBase.VectorContent;

namespace projectFrameCut.Render.VectorContent;

public abstract class BaseShapeComponent : IVectorComponent
{
    public string FromPlugin => InternalPluginBase.InternalPluginBaseID;
    public abstract string TypeName { get; }
    public string Name { get; set; } = string.Empty;
    public Guid Id { get; set; } = Guid.NewGuid();
    public Dictionary<string, object> Parameters { get; } = new();
    public int Index { get; set; }
    public List<VectorAnimationKeyFrame> AnimationFrames { get; set; } = new();
    public IReadOnlyDictionary<string, IAnimatableField> AnimatableFields { get; }

    protected abstract string[] ShapeFieldIds { get; }
    protected abstract Dictionary<string, object> GetDefaultParameters();
    protected abstract ShapeCanvasElement BuildBaseShape();

    protected BaseShapeComponent()
    {
        foreach (var (key, value) in GetDefaultParameters())
        {
            Parameters[key] = value;
        }

        if (!Parameters.ContainsKey("RelativeX")) Parameters["RelativeX"] = 0.5f;
        if (!Parameters.ContainsKey("RelativeY")) Parameters["RelativeY"] = 0.5f;
        if (!Parameters.ContainsKey("Rotation")) Parameters["Rotation"] = 0f;
        if (!Parameters.ContainsKey("BaseX")) Parameters["BaseX"] = 0f;
        if (!Parameters.ContainsKey("BaseY")) Parameters["BaseY"] = 0f;
        if (!Parameters.ContainsKey("LayerIndex")) Parameters["LayerIndex"] = 0;
        if (!Parameters.ContainsKey("StrokeR")) Parameters["StrokeR"] = (float)ushort.MaxValue;
        if (!Parameters.ContainsKey("StrokeG")) Parameters["StrokeG"] = (float)ushort.MaxValue;
        if (!Parameters.ContainsKey("StrokeB")) Parameters["StrokeB"] = (float)ushort.MaxValue;
        if (!Parameters.ContainsKey("StrokeA")) Parameters["StrokeA"] = 1f;
        if (!Parameters.ContainsKey("FillR")) Parameters["FillR"] = 0f;
        if (!Parameters.ContainsKey("FillG")) Parameters["FillG"] = 0f;
        if (!Parameters.ContainsKey("FillB")) Parameters["FillB"] = 0f;
        if (!Parameters.ContainsKey("FillA")) Parameters["FillA"] = 1f;
        if (!Parameters.ContainsKey("Thickness")) Parameters["Thickness"] = 0.01f;

        var map = new Dictionary<string, IAnimatableField>(AnimatableFieldMap.CommonFields);
        foreach (var fieldId in ShapeFieldIds)
        {
            if (AnimatableFieldMap.ShapeFields.TryGetValue(fieldId, out var field))
            {
                map[fieldId] = field;
            }
        }
        AnimatableFields = map;
    }

    public VectorCanvasElement Compute(float normalizedProgress)
    {
        var shape = BuildBaseShape();
        shape = ApplyVisualProperties(shape);
        shape = ApplyAnimation(shape, normalizedProgress);
        return shape;
    }

    protected ShapeCanvasElement ApplyVisualProperties(ShapeCanvasElement shape)
    {
        shape = shape.WithStroke(
            Parameters.GetUShort("StrokeR", ushort.MaxValue),
            Parameters.GetUShort("StrokeG", ushort.MaxValue),
            Parameters.GetUShort("StrokeB", ushort.MaxValue),
            Parameters.GetFloat("StrokeA", 1f),
            Parameters.GetFloat("Thickness", 0.01f));

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

    protected ShapeCanvasElement ApplyAnimation(ShapeCanvasElement shape, float progress)
    {
        if (AnimationFrames.Count == 0)
        {
            return shape;
        }

        var cloned = shape.Clone();
        var groups = AnimationFrames
            .Where(k => !string.IsNullOrWhiteSpace(k.TargetFieldId))
            .GroupBy(k => k.TargetFieldId);

        foreach (var group in groups)
        {
            var ordered = group.OrderBy(k => k.Time).ToList();
            if (ordered.Count == 0)
            {
                continue;
            }

            float value;
            if (ordered.Count == 1 || progress <= ordered[0].Time)
            {
                value = ordered[0].Value;
            }
            else if (progress >= ordered[^1].Time)
            {
                value = ordered[^1].Value;
            }
            else
            {
                value = ordered[^1].Value;
                for (int i = 1; i < ordered.Count; i++)
                {
                    var prev = ordered[i - 1];
                    var next = ordered[i];
                    if (progress > next.Time)
                    {
                        continue;
                    }

                    var span = next.Time - prev.Time;
                    if (span <= 0f)
                    {
                        value = next.Value;
                    }
                    else
                    {
                        var t = (progress - prev.Time) / span;
                        var eased = EasingFunctions.Apply(prev.Easing, t);
                        value = prev.Value + (next.Value - prev.Value) * eased;
                    }
                    break;
                }
            }

            AnimationApplier.ApplyFieldValue(cloned, group.Key, value);
        }

        return cloned;
    }
}

