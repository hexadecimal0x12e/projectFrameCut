using projectFrameCut.ApplicationAPIBase.Views.PropertyPanelBuilders;
using projectFrameCut.Render.RenderAPIBase.VectorContent;

namespace projectFrameCut.ApplicationAPIBase.VectorComponentHandler;

public interface IVectorComponentHandler
{
    /// <summary>
    /// Define the type name of the component. This is used to identify the component type in serialization and deserialization processes.
    /// </summary>
    public string TypeName { get; }
    /// <summary>
    /// Indicates which plugin this component handler comes from. 
    /// </summary>
    public string FromPlugin { get; }
    /// <summary>
    /// Get a user-friendly display name for this component type
    /// </summary>
    public string DisplayName { get; }
    /// <summary>
    /// A icon in Google Material Icons codepoint.
    /// </summary>
    public string Icon { get; }

    /// <summary>
    /// Indicates whether this component type has default handles in the UI that can be used for user interaction. 
    /// </summary>
    public bool HasDefaultHandles { get; }

    /// <summary>
    /// Creates a new instance of the vector component with the specified parameters. If no parameters are provided, default values will be used.
    /// </summary>
    /// <param name="parameters"></param>
    /// <returns></returns>
    public IVectorComponent Create(Dictionary<string, object>? parameters = null);
    /// <summary>
    /// Creates a list of shape handle descriptors for the given vector component. These handles can be used for user interaction, such as dragging or resizing the component.
    /// </summary>
    /// <param name="component"></param>
    /// <returns></returns>
    public IReadOnlyList<ShapeHandleDescriptor> CreateHandles(IVectorComponent component);
    /// <summary>
    /// Applies the changes made by dragging a handle of the vector component. This method updates the component's parameters based on the new position of the handle.
    /// </summary>
    /// <param name="component">the source vector component</param>
    /// <param name="handleId">the ID of the handle being dragged</param>
    /// <param name="newX">the new X position of the handle</param>
    /// <param name="newY">the new Y position of the handle</param>
    /// <param name="isLive">indicates whether the drag is in live(dragging) or final(completed) state.</param>
    public void ApplyHandleDrag(IVectorComponent component, string handleId, float newX, float newY, bool isLive);
    /// <summary>
    /// Creates a property panel UI for the given vector component. This UI allows users to view and edit the properties of the component.
    /// </summary>
    /// <param name="component"></param>
    /// <returns></returns>
    public PropertyPanelBuilder CreatePropertyUI(IVectorComponent component);
    /// <summary>
    /// Handles changes made to the properties of the vector component through the property panel UI. This method updates the component's parameters based on the new values provided in the event arguments.
    /// </summary>
    /// <param name="component"></param>
    /// <param name="args"></param>
    public void HandlePropertyChange(IVectorComponent component, PropertyPanelPropertyChangedEventArgs args);
    /// <summary>
    /// Gets a display item that contains user-friendly information about the component type, such as its display name, icon, and description. The information can be localized based on the provided locale.
    /// </summary>
    /// <param name="locale"></param>
    /// <returns></returns>
    public VectorComponentHandlerDisplayItem GetDisplayItem(string? locale = null);
    /// <summary>
    /// Gets the default parameters for a new instance of the vector component. These parameters are used when creating a new component without any user-specified values.
    /// </summary>
    /// <returns></returns>
    public Dictionary<string, object> DefaultParameters { get; }
}

