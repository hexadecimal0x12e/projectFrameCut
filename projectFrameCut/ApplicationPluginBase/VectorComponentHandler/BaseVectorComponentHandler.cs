using projectFrameCut.ApplicationAPIBase.VectorComponentHandler;
using projectFrameCut.ApplicationAPIBase.Views.PropertyPanelBuilders;
using projectFrameCut.Render.RenderAPIBase.VectorContent;
using projectFrameCut.Render.VectorContent;

namespace projectFrameCut.ApplicationPluginBase.VectorComponentHandler;

public abstract class BaseVectorComponentHandler : IVectorComponentHandler
{
    public abstract string TypeName { get; }
    public string FromPlugin => InternalApplicationPluginBase.InternalPluginBaseID;
    public abstract string DisplayName { get; }
    public abstract string Icon { get; }
    public abstract bool HasDefaultHandles { get; }

    Dictionary<string, object> IVectorComponentHandler.DefaultParameters => GetDefaultParameters();

    protected abstract IVectorComponent CreateComponent();
    protected abstract Dictionary<string, object> GetDefaultParameters();

    public virtual IVectorComponent Create(Dictionary<string, object>? parameters = null)
    {
        var component = CreateComponent();
        var defaults = GetDefaultParameters();
        foreach (var (key, value) in defaults)
        {
            component.Parameters[key] = value;
        }

        if (parameters is not null)
        {
            foreach (var (key, value) in parameters)
            {
                component.Parameters[key] = value;
            }
        }

        component.Name = $"{DisplayName}";
        return component;
    }

    public virtual IReadOnlyList<ShapeHandleDescriptor> CreateHandles(IVectorComponent component) => [];

    public virtual void ApplyHandleDrag(IVectorComponent component, string handleId, float newX, float newY, bool isLive)
    {
    }

    /// <summary>
    /// Creates the full property panel UI for the given component.
    /// Builds from <see cref="AddCommonProperties"/> + <see cref="AddShapeSpecificProperties"/>.
    /// </summary>
    public PropertyPanelBuilder CreatePropertyUI(IVectorComponent component)
    {
        var builder = new PropertyPanelBuilder();
        AddCommonProperties(builder, component);
        AddShapeSpecificProperties(builder, component);
        return builder;
    }

    /// <summary>
    /// Adds common property sections (Position and Appearance) that apply to all shape types.
    /// </summary>
    protected static void AddCommonProperties(PropertyPanelBuilder builder, IVectorComponent component)
    {
        // ── Position section ──
        builder.AddCollapsibleSection("位置", b =>
        {
            b.AddSlider("RelativeX", "X:", 0.0, 1.0, GetParam(component, "RelativeX", 0.5f),
                eventCallMode: SliderUpdateEventCallMode.OnValueChanged);
            b.AddSlider("RelativeY", "Y:", 0.0, 1.0, GetParam(component, "RelativeY", 0.5f),
                eventCallMode: SliderUpdateEventCallMode.OnValueChanged);
            b.AddSlider("Rotation", "旋转:", -3.1416, 3.1416, GetParam(component, "Rotation", 0.0f),
                eventCallMode: SliderUpdateEventCallMode.OnValueChanged);
        }, defaultExpanded: true);

        // ── Appearance section ──
        builder.AddCollapsibleSection("外观", b =>
        {
            b.AddSlider("Thickness", "描边:", 0.0, 20.0, GetParam(component, "Thickness", 2.0f),
                eventCallMode: SliderUpdateEventCallMode.OnValueChanged);

            // Stroke RGBA
            b.AddSlider("StrokeR", "描边 R:", 0.0, 255.0, GetParam(component, "StrokeR", 255.0f),
                eventCallMode: SliderUpdateEventCallMode.OnValueChanged);
            b.AddSlider("StrokeG", "描边 G:", 0.0, 255.0, GetParam(component, "StrokeG", 255.0f),
                eventCallMode: SliderUpdateEventCallMode.OnValueChanged);
            b.AddSlider("StrokeB", "描边 B:", 0.0, 255.0, GetParam(component, "StrokeB", 255.0f),
                eventCallMode: SliderUpdateEventCallMode.OnValueChanged);
            b.AddSlider("StrokeA", "描边 A:", 0.0, 1.0, GetParam(component, "StrokeA", 1.0f),
                eventCallMode: SliderUpdateEventCallMode.OnValueChanged);
            b.AddSeparator();

            // Fill RGBA
            b.AddSlider("FillR", "填充 R:", 0.0, 255.0, GetParam(component, "FillR", 0.0f),
                eventCallMode: SliderUpdateEventCallMode.OnValueChanged);
            b.AddSlider("FillG", "填充 G:", 0.0, 255.0, GetParam(component, "FillG", 0.0f),
                eventCallMode: SliderUpdateEventCallMode.OnValueChanged);
            b.AddSlider("FillB", "填充 B:", 0.0, 255.0, GetParam(component, "FillB", 0.0f),
                eventCallMode: SliderUpdateEventCallMode.OnValueChanged);
            b.AddSlider("FillA", "填充 A:", 0.0, 1.0, GetParam(component, "FillA", 1.0f),
                eventCallMode: SliderUpdateEventCallMode.OnValueChanged);
        }, defaultExpanded: true);
    }

    /// <summary>
    /// Override to add shape-specific property sliders/controls.
    /// Called after <see cref="AddCommonProperties"/> so shape properties appear below common ones.
    /// </summary>
    protected abstract void AddShapeSpecificProperties(PropertyPanelBuilder builder, IVectorComponent component);

    public virtual void HandlePropertyChange(IVectorComponent component, PropertyPanelPropertyChangedEventArgs args)
    {
        component.Parameters[args.Id] = args.Value ?? 0f;
    }

    public virtual VectorComponentHandlerDisplayItem GetDisplayItem(string? locale = null) =>
        new()
        {
            DisplayName = DisplayName,
            Icon = Icon,
            Description = DisplayName,
        };

    protected static float GetParam(IVectorComponent component, string key, float fallback = 0f) =>
        component.Parameters.GetFloat(key, fallback);
}
