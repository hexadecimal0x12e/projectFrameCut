using projectFrameCut.Drawing.Vector;
using projectFrameCut.Render.Plugin;
using projectFrameCut.Render.RenderAPIBase.VectorContent;
using projectFrameCut.Shared;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace projectFrameCut.Render.VectorContent.Components;

/// <summary>
/// A persistent group of <see cref="IVectorComponent"/>s that behaves as a single component.
/// The group has its own position, size and rotation, which are composed on top of each child component's transform.
/// </summary>
public class ComponentGroup : IVectorComponent
{
    private const string ChildrenKey = "Children";
    private const string InitialRelativeXKey = "InitialRelativeX";
    private const string InitialRelativeYKey = "InitialRelativeY";
    private const string InitialWidthKey = "InitialWidth";
    private const string InitialHeightKey = "InitialHeight";

    private static readonly JsonSerializerOptions s_serializerOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        PropertyNamingPolicy = null,
        NumberHandling = JsonNumberHandling.AllowReadingFromString | JsonNumberHandling.AllowNamedFloatingPointLiterals
    };

    private List<IVectorComponent>? _cachedChildren;

    public string FromPlugin => InternalPluginBase.InternalPluginBaseID;

    public string TypeName => "ComponentGroup";

    public string Name { get; set; } = "Group";

    public Guid Id { get; set; } = Guid.NewGuid();

    public Dictionary<string, object> Parameters { get; set; } = new();

    public int Index { get; set; }

    public List<VectorAnimationKeyFrame> AnimationFrames { get; set; } = new();

    public IReadOnlyDictionary<string, AnimatableField> AnimatableFields { get; }

    /// <summary>
    /// Whether this group was imported from an SVG file.
    /// Stored in <see cref="Parameters"/> because <see cref="IVectorComponent"/>
    /// is an interface — in .NET 7+ System.Text.Json uses the declared type
    /// for serialisation, so properties unique to <see cref="ComponentGroup"/>
    /// would be lost during the round-trip.  The Parameters dictionary, being
    /// declared on IVectorComponent itself, survives serialisation correctly.
    /// </summary>
    public bool IsSVG
    {
        get => GetBoolParam("IsSVG", false);
        set => Parameters["IsSVG"] = value ? "True" : "False";
    }

    /// <inheritdoc cref="IsSVG"/>
    public bool IsImportedGroup
    {
        get => GetBoolParam("IsImportedGroup", false);
        set => Parameters["IsImportedGroup"] = value ? "True" : "False";
    }

    /// <inheritdoc cref="IsSVG"/>
    public string SourceFile
    {
        get => GetStringParam("SourceFile", "imported.svg");
        set => Parameters["SourceFile"] = value ?? "imported.svg";
    }

    public ComponentGroup()
    {
        EnsureDefaultParameters();

        AnimatableFields = new Dictionary<string, AnimatableField>
        {
            ["RelativeX"] = AnimatableFieldMap.CommonFields["RelativeX"],
            ["RelativeY"] = AnimatableFieldMap.CommonFields["RelativeY"],
            ["Width"] = AnimatableFieldMap.ShapeFields["Width"],
            ["Height"] = AnimatableFieldMap.ShapeFields["Height"],
            ["Rotation"] = AnimatableFieldMap.CommonFields["Rotation"],
        };
    }

    /// <summary>
    /// The child components contained in this group.
    /// Deserialized lazily from <see cref="Parameters"/> and cached.
    /// </summary>
    [JsonIgnore]
    public List<IVectorComponent> Children
    {
        get
        {
            EnsureDefaultParameters();
            if (_cachedChildren is null)
            {
                _cachedChildren = DeserializeChildren();
            }
            return _cachedChildren;
        }
    }

    /// <summary>
    /// Replaces the children list and updates the serialized cache.
    /// </summary>
    public void SetChildren(IEnumerable<IVectorComponent> children)
    {
        EnsureDefaultParameters();
        _cachedChildren = children.ToList();
        Parameters[ChildrenKey] = JsonSerializer.Serialize(_cachedChildren, typeof(List<IVectorComponent>), s_serializerOptions);
    }

    /// <summary>
    /// Captures the initial group bounds used as the reference frame for scaling.
    /// </summary>
    public void SetInitialBounds(float relativeX, float relativeY, float width, float height)
    {
        EnsureDefaultParameters();
        Parameters[InitialRelativeXKey] = relativeX;
        Parameters[InitialRelativeYKey] = relativeY;
        Parameters[InitialWidthKey] = width;
        Parameters[InitialHeightKey] = height;
    }

    public float InitialRelativeX => GetFloatParam(InitialRelativeXKey, 0.5f);
    public float InitialRelativeY => GetFloatParam(InitialRelativeYKey, 0.5f);
    public float InitialWidth => GetFloatParam(InitialWidthKey, 0.3f);
    public float InitialHeight => GetFloatParam(InitialHeightKey, 0.3f);

    /// <summary>
    /// A group does not produce a single element. Use <see cref="ComputeAll"/> instead.
    /// </summary>
    public VectorCanvasElement Compute(float index) =>
        throw new InvalidOperationException($"{nameof(ComponentGroup)} does not produce a single element. Use {nameof(ComputeAll)}.");

    /// <summary>
    /// Computes all child elements with the group's position, scale and rotation applied.
    /// Uses separate X/Y scales to support non-uniform resizing from the interactive editor.
    /// </summary>
    public IEnumerable<VectorCanvasElement> ComputeAll(float progress)
    {
        float groupRelX = EvaluateField("RelativeX", progress, 0.5f);
        float groupRelY = EvaluateField("RelativeY", progress, 0.5f);
        float groupWidth = EvaluateField("Width", progress, 0.3f);
        float groupHeight = EvaluateField("Height", progress, 0.3f);
        float groupRot = EvaluateField("Rotation", progress, 0f);

        float initialWidth = Math.Max(0.0001f, InitialWidth);
        float initialHeight = Math.Max(0.0001f, InitialHeight);

        // Non-uniform scale: Width and Height now directly control X and Y scaling independently.
        // This eliminates the feedback loop that occurred when averaging scaleX and scaleY into
        // a single uniform scale (the old approach caused the clip rect to shrink back toward
        // the initial size over multiple sync cycles).
        float scaleX = groupWidth / initialWidth;
        float scaleY = groupHeight / initialHeight;

        float cos = MathF.Cos(groupRot);
        float sin = MathF.Sin(groupRot);

        foreach (var child in Children)
        {
            if (child is ComponentGroup)
            {
                // Nested groups are not supported in this version.
                continue;
            }

            var element = child.Compute(progress);
            if (element is null)
            {
                continue;
            }

            float childRelX = element.RelativeX;
            float childRelY = element.RelativeY;
            float childRot = element.Rotation;

            float localX = childRelX - InitialRelativeX;
            float localY = childRelY - InitialRelativeY;

            // Apply the group transform: rotate then non-uniform scale the local offset,
            // then translate by the current group center.
            // Transform order: T(center) * R(rotation) * S(scaleX, scaleY) * localOffset
            element.RelativeX = groupRelX + scaleX * localX * cos - scaleY * localY * sin;
            element.RelativeY = groupRelY + scaleX * localX * sin + scaleY * localY * cos;
            element.Rotation = groupRot;

            if (element is ShapeCanvasElement shape)
            {
                float childCos = MathF.Cos(childRot);
                float childSin = MathF.Sin(childRot);
                shape.TransformSegments(s => TransformSegment(s, scaleX, scaleY, childCos, childSin));
            }

            yield return element;
        }
    }

    private static VectorSegment TransformSegment(VectorSegment segment, float scaleX, float scaleY, float cos, float sin)
    {
        // Pre-rotate segment coordinates by the child's own rotation, then apply the group's
        // non-uniform scale (scaleX for X-components, scaleY for Y-components).
        // Transform order: R(childRot) * S(scaleX, scaleY) — rotate first, then scale.
        return segment switch
        {
            StraightLineVectorSegment l => l with
            {
                X1 = scaleX * (l.X1 * cos - l.Y1 * sin),
                Y1 = scaleY * (l.X1 * sin + l.Y1 * cos),
                X2 = scaleX * (l.X2 * cos - l.Y2 * sin),
                Y2 = scaleY * (l.X2 * sin + l.Y2 * cos),
            },
            RoundedRectangleVectorSegment rr => rr with
            {
                X = scaleX * (rr.X * cos - rr.Y * sin),
                Y = scaleY * (rr.X * sin + rr.Y * cos),
                Width = scaleX * rr.Width,
                Height = scaleY * rr.Height,
                CornerRadius = (scaleX + scaleY) / 2f * rr.CornerRadius,
            },
            RectangleVectorSegment r => r with
            {
                X = scaleX * (r.X * cos - r.Y * sin),
                Y = scaleY * (r.X * sin + r.Y * cos),
                Width = scaleX * r.Width,
                Height = scaleY * r.Height,
            },
            EllipseVectorSegment e => e with
            {
                X = scaleX * (e.X * cos - e.Y * sin),
                Y = scaleY * (e.X * sin + e.Y * cos),
                RadiusX = scaleX * e.RadiusX,
                RadiusY = scaleY * e.RadiusY,
            },
            ArcVectorSegment a => a with
            {
                X = scaleX * (a.X * cos - a.Y * sin),
                Y = scaleY * (a.X * sin + a.Y * cos),
                RadiusX = scaleX * a.RadiusX,
                RadiusY = scaleY * a.RadiusY,
            },
            CubicBezierVectorSegment b => b with
            {
                X1 = scaleX * (b.X1 * cos - b.Y1 * sin),
                Y1 = scaleY * (b.X1 * sin + b.Y1 * cos),
                X2 = scaleX * (b.X2 * cos - b.Y2 * sin),
                Y2 = scaleY * (b.X2 * sin + b.Y2 * cos),
                X3 = scaleX * (b.X3 * cos - b.Y3 * sin),
                Y3 = scaleY * (b.X3 * sin + b.Y3 * cos),
                X4 = scaleX * (b.X4 * cos - b.Y4 * sin),
                Y4 = scaleY * (b.X4 * sin + b.Y4 * cos),
            },
            QuadraticBezierVectorSegment q => q with
            {
                X1 = scaleX * (q.X1 * cos - q.Y1 * sin),
                Y1 = scaleY * (q.X1 * sin + q.Y1 * cos),
                X2 = scaleX * (q.X2 * cos - q.Y2 * sin),
                Y2 = scaleY * (q.X2 * sin + q.Y2 * cos),
                X3 = scaleX * (q.X3 * cos - q.Y3 * sin),
                Y3 = scaleY * (q.X3 * sin + q.Y3 * cos),
            },
            PolygonVectorSegment p => p with
            {
                Points = p.Points.Select(pt => RotateScalePoint(pt, scaleX, scaleY, cos, sin)).ToArray(),
            },
            PolylineVectorSegment p => p with
            {
                Points = p.Points.Select(pt => RotateScalePoint(pt, scaleX, scaleY, cos, sin)).ToArray(),
            },
            _ => segment,
        };
    }

    private static Point RotateScalePoint(Point point, float scaleX, float scaleY, float cos, float sin) =>
        new(scaleX * (point.X * cos - point.Y * sin), scaleY * (point.X * sin + point.Y * cos));

    private float EvaluateField(string fieldId, float progress, float defaultValue)
    {
        return AnimationFrames.EvaluateField(fieldId, progress, GetFloatParam(fieldId, defaultValue));
    }

    private float GetFloatParam(string key, float defaultValue)
    {
        EnsureDefaultParameters();

        if (!Parameters.TryGetValue(key, out var val) || val is null)
        {
            return defaultValue;
        }

        return val switch
        {
            float f => f,
            double d => (float)d,
            int i => i,
            uint u => u,
            long l => l,
            ushort us => us,
            decimal m => (float)m,
            JsonElement { ValueKind: JsonValueKind.Number } je => je.GetSingle(),
            JsonElement { ValueKind: JsonValueKind.String } je when float.TryParse(je.GetString(), out var parsed) => parsed,
            _ => defaultValue,
        };
    }

    private bool GetBoolParam(string key, bool defaultValue)
    {
        EnsureDefaultParameters();

        if (!Parameters.TryGetValue(key, out var val) || val is null)
            return defaultValue;

        return val switch
        {
            bool b => b,
            string s when bool.TryParse(s, out var parsed) => parsed,
            JsonElement { ValueKind: JsonValueKind.True } => true,
            JsonElement { ValueKind: JsonValueKind.False } => false,
            JsonElement { ValueKind: JsonValueKind.String } je
                when bool.TryParse(je.GetString(), out var parsed) => parsed,
            _ => defaultValue,
        };
    }

    private string GetStringParam(string key, string defaultValue)
    {
        EnsureDefaultParameters();

        if (!Parameters.TryGetValue(key, out var val) || val is null)
            return defaultValue;

        return val switch
        {
            string s => s,
            JsonElement { ValueKind: JsonValueKind.String } je => je.GetString() ?? defaultValue,
            _ => defaultValue,
        };
    }

    private List<IVectorComponent> DeserializeChildren()
    {
        EnsureDefaultParameters();

        var result = new List<IVectorComponent>();

        if (!Parameters.TryGetValue(ChildrenKey, out var raw))
        {
            return result;
        }

        string? json = raw switch
        {
            string s when !string.IsNullOrEmpty(s) => s,
            JsonElement { ValueKind: JsonValueKind.String } je => je.GetString(),
            JsonElement je => je.GetRawText(),
            _ => null,
        };

        if (string.IsNullOrEmpty(json))
        {
            return result;
        }

        try
        {
            using var doc = JsonDocument.Parse(json);
            if (doc.RootElement.ValueKind != JsonValueKind.Array)
            {
                return result;
            }

            foreach (var element in doc.RootElement.EnumerateArray())
            {
                var pluginId = element.TryGetProperty("FromPlugin", out var pluginElement)
                    ? pluginElement.GetString()
                    : InternalPluginBase.InternalPluginBaseID;
                var resolvedPluginId = string.IsNullOrWhiteSpace(pluginId)
                    ? InternalPluginBase.InternalPluginBaseID
                    : pluginId;
                var plugin = PluginManager.LoadedPlugins.TryGetValue(resolvedPluginId, out var loaded)
                    ? loaded
                    : PluginManager.LoadedPlugins[InternalPluginBase.InternalPluginBaseID];
                result.Add(plugin.VectComponentCreator(element));
            }
        }
        catch (Exception ex)
        {
            // Log the error to aid debugging while falling back to an empty children list.
            Logger.Log(ex, $"ComponentGroup.DeserializeChildren for '{Name}' ({Id})", this);
        }

        return result;
    }

    private void EnsureDefaultParameters()
    {
        Parameters ??= new();

        Parameters.TryAdd("RelativeX", 0.5f);
        Parameters.TryAdd("RelativeY", 0.5f);
        Parameters.TryAdd("Width", 0.3f);
        Parameters.TryAdd("Height", 0.3f);
        Parameters.TryAdd("Rotation", 0f);
        Parameters.TryAdd("BaseX", 0f);
        Parameters.TryAdd("BaseY", 0f);
        Parameters.TryAdd("LayerIndex", 0);
        Parameters.TryAdd("IsSVG", "False");
        Parameters.TryAdd("IsImportedGroup", "False");
        Parameters.TryAdd("SourceFile", "imported.svg");
        Parameters.TryAdd(InitialRelativeXKey, 0.5f);
        Parameters.TryAdd(InitialRelativeYKey, 0.5f);
        Parameters.TryAdd(InitialWidthKey, 0.3f);
        Parameters.TryAdd(InitialHeightKey, 0.3f);
        Parameters.TryAdd(ChildrenKey, "[]");
    }
}
