using projectFrameCut.ApplicationAPIBase.VectorComponentHandler;
using projectFrameCut.ApplicationAPIBase.Views.PropertyPanelBuilders;
using projectFrameCut.ApplicationAPIBase.Views.Pickers;
using projectFrameCut.Render.RenderAPIBase.VectorContent;
using projectFrameCut.ApplicationAPIBase.Interaction;
using projectFrameCut.Render.VectorContent;
using static LocalizedResources.SimpleLocalizerBaseGeneratedHelper_PropertyPanel;

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

    public virtual View? GetHandlePreview(IVectorComponent component, VectorHandlePreviewContext context, View? currentView = null)
    {
        var view = currentView as VectorHandlePreviewView ?? new VectorHandlePreviewView();
        return view.Update(context) ? view : null;
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
        builder.AddCollapsibleSection(PPLocalizedResources.VectorContentHandler_Section_Position, b =>
        {
            b.AddSlider("Rotation", PPLocalizedResources.VectorContentHandler_Rotation, -3.1416, 3.1416, GetParam(component, "Rotation", 0.0f),
                eventCallMode: SliderUpdateEventCallMode.OnValueChanged);
        }, defaultExpanded: true);

        // ── Appearance section ──
        builder.AddCollapsibleSection(PPLocalizedResources.VectorContentHandler_Section_Appearance, b =>
        {
            b.AddSlider("Thickness", Localized.VectorContentHandler_StrokeWidth, 0.0, 20.0, GetParam(component, component is projectFrameCut.Render.VectorContent.Components.TextComponent ? "StrokeThickness" : "Thickness", 2.0f),
                eventCallMode: SliderUpdateEventCallMode.OnValueChanged);

            AddColorPicker(b, component, "Stroke", Localized.VectorContentHandler_StrokeColor);
            AddColorPicker(b, component, "Fill", Localized.VectorContentHandler_FillColor);
        }, defaultExpanded: true);
    }

    private static void AddColorPicker(PropertyPanelBuilder builder, IVectorComponent component, string prefix, PropertyPanelItemLabel title)
    {
        var color = Color.FromRgba(
            component.Parameters.GetUShort(prefix + "R") / 65535.0,
            component.Parameters.GetUShort(prefix + "G") / 65535.0,
            component.Parameters.GetUShort(prefix + "B") / 65535.0,
            Math.Clamp(GetParam(component, prefix + "A", 1f), 0f, 1f));
        builder.AddCustomChild(title, invoker =>
        {
            var swatch = new BoxView { Color = color, WidthRequest = 30, HeightRequest = 30, CornerRadius = 5, VerticalOptions = LayoutOptions.Center };
            var label = new Label { Text = color.ToArgbHex(), VerticalOptions = LayoutOptions.Center };
            var layout = new HorizontalStackLayout { Spacing = 8, Children = { swatch, label } };
            bool opening = false;
            var tap = new TapGestureRecognizer();
            tap.Tapped += async (_, _) =>
            {
                if (opening || App.GetCurrentPage() is not DraftPage page) return;
                opening = true;
                try
                {
                    var picker = new ColorPicker { SelectedColor = swatch.Color };
                    picker.SelectedColorChanged += (_, selected) =>
                    {
                        swatch.Color = selected;
                        label.Text = selected.ToArgbHex();
                        invoker(selected);
                    };
                    await page.ShowAPopup(new ScrollView
                    {
                        Content = new VerticalStackLayout
                        {
                            Spacing = 10,
                            Padding = new Thickness(10, 0),
                            Children =
                            {
                                new Button { Text = Localized._Hide, Command = new Command(async () => await page.HidePopup(true)) },
                                picker
                            }
                        }
                    }, mode: "dialog");
                }
                catch (Exception ex) { projectFrameCut.Shared.Logger.Log(ex, $"Open vector {prefix} color picker for {component.Id}", component); }
                finally { opening = false; }
            };
            layout.GestureRecognizers.Add(tap);
            return layout;
        }, "Vector" + prefix + "Color", color);
    }

    /// <summary>
    /// Override to add shape-specific property sliders/controls.
    /// Called after <see cref="AddCommonProperties"/> so shape properties appear below common ones.
    /// </summary>
    protected abstract void AddShapeSpecificProperties(PropertyPanelBuilder builder, IVectorComponent component);

    public virtual void HandlePropertyChange(IVectorComponent component, PropertyPanelPropertyChangedEventArgs args)
    {
        if ((args.Id is "VectorStrokeColor" or "VectorFillColor") && args.Value is Color color)
        {
            string prefix = args.Id == "VectorStrokeColor" ? "Stroke" : "Fill";
            component.Parameters[prefix + "R"] = (float)Math.Round(Math.Clamp(color.Red, 0f, 1f) * ushort.MaxValue);
            component.Parameters[prefix + "G"] = (float)Math.Round(Math.Clamp(color.Green, 0f, 1f) * ushort.MaxValue);
            component.Parameters[prefix + "B"] = (float)Math.Round(Math.Clamp(color.Blue, 0f, 1f) * ushort.MaxValue);
            component.Parameters[prefix + "A"] = Math.Clamp(color.Alpha, 0f, 1f);
        }
        else
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
