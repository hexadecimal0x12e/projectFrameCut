using projectFrameCut.Render.RenderAPIBase.ClipAndTrack;

namespace projectFrameCut.ApplicationAPIBase.Interaction;

/// <summary>Logical coordinates used by the common interactive editor.</summary>
public readonly record struct InteractiveRect(double X, double Y, double Width, double Height)
{
    /// <summary>Gets whether the rectangle has a non-positive size.</summary>
    public bool IsEmpty => Width <= 0 || Height <= 0;
}

/// <summary>Capabilities that control which gestures an element accepts.</summary>
public readonly record struct InteractiveElementCapabilities(
    bool CanMove = true,
    bool CanResizeHorizontally = true,
    bool CanResizeVertically = true,
    bool AllowFreeScale = false,
    bool CanSnapWhileMoving = true,
    bool CanSnapWhileResizing = true,
    bool AllowOutOfBounds = false);

/// <summary>Small, timeline-independent description of an interactive element.</summary>
public interface IInteractableElement
{
    /// <summary>Gets the stable element identifier.</summary>
    Guid Id { get; }
    /// <summary>Gets the user-facing display name.</summary>
    string DisplayName { get; }
    /// <summary>Gets or sets the element rectangle in logical video coordinates.</summary>
    InteractiveRect LogicalRect { get; set; }
    /// <summary>Gets the gesture capabilities of this element.</summary>
    InteractiveElementCapabilities Capabilities { get; }
    /// <summary>Gets whether the element participates in the current editor view.</summary>
    bool IsVisible { get; }
    /// <summary>Gets the z-order layer used by the overlay.</summary>
    int Layer { get; }
    /// <summary>Gets or sets whether the element is selected.</summary>
    bool IsSelected { get; set; }
    /// <summary>Determines whether the element should be shown at the specified frame.</summary>
    /// <param name="frame">The current frame number.</param>
    bool IsVisibleAtFrame(uint frame) => IsVisible;
}

/// <summary>Snapshot of an element and the editor dimensions at a point in time.</summary>
public sealed record InteractiveElementState(
    Guid Id,
    string DisplayName,
    InteractiveRect LogicalRect,
    InteractiveElementCapabilities Capabilities,
    bool IsVisible,
    int Layer,
    bool IsSelected,
    uint CurrentFrame,
    double CanvasWidth,
    double CanvasHeight,
    double VideoWidth,
    double VideoHeight);

/// <summary>Serializable, timeline-independent element description.</summary>
public sealed record InteractiveElementDescriptor(
    Guid Id,
    string DisplayName,
    InteractiveRect LogicalRect,
    InteractiveElementCapabilities Capabilities = default,
    bool IsVisible = true,
    int Layer = 0,
    bool IsSelected = false);

/// <summary>Identifies which resize or move gesture is active.</summary>
public enum ResizeHandle
{
    TopLeft, TopRight, BottomLeft, BottomRight, ClipPan
}

/// <summary>Orientation of a reference line on the canvas.</summary>
public enum ReferenceLineOrientation { Horizontal, Vertical }

/// <summary>High-level operation performed by the editor.</summary>
public enum InteractiveOperation { None, Move, Resize, CustomHandle }

/// <summary>Lifecycle phase of an interactive change.</summary>
public enum InteractiveChangeKind { Started, Changed, Completed, Canceled }

/// <summary>Immutable reference line configuration.</summary>
public sealed record ReferenceLine(
    string Id,
    double Position,
    ReferenceLineOrientation Orientation,
    Color Color,
    double Thickness);

/// <summary>Describes a rectangle change produced by an interaction.</summary>
public sealed record InteractiveChange(
    Guid ElementId,
    InteractiveRect PreviousRect,
    InteractiveRect CurrentRect,
    InteractiveOperation Operation,
    InteractiveChangeKind Kind);

/// <summary>Candidate geometry captured for keyframe recording.</summary>
public sealed record KeyframeCandidate(
    Guid ElementId,
    uint Frame,
    InteractiveRect Rect,
    ResizeHandle Handle);

/// <summary>Describes a custom handle rendered inside an element rectangle.</summary>
public readonly record struct CustomHandleDescriptor(
    string Id,
    float NormalizedX,
    float NormalizedY,
    Color FillColor,
    double Size,
    Func<View>? ViewFactory = null);

/// <summary>Common handle descriptor. Kept under the historical name for plugin migration.</summary>
public readonly record struct ShapeHandleDescriptor(
    string Id,
    float NormalizedX,
    float NormalizedY,
    Color FillColor,
    double Size,
    Func<View>? HandleGetter = null);

/// <summary>Geometry context supplied while a custom handle is dragged.</summary>
public readonly record struct CustomHandleDragContext(
    Guid ElementId,
    string HandleId,
    double DisplayWidth,
    double DisplayHeight,
    double LogicalWidth,
    double LogicalHeight)
{
    /// <summary>Converts a display-pixel delta to a normalized X delta.</summary>
    public float DeltaXToNormalized(double delta) => DisplayWidth > 0 ? (float)(delta / DisplayWidth) : 0;
    /// <summary>Converts a display-pixel delta to a normalized Y delta.</summary>
    public float DeltaYToNormalized(double delta) => DisplayHeight > 0 ? (float)(delta / DisplayHeight) : 0;
}

/// <summary>Provides custom handles for an element.</summary>
public delegate IReadOnlyList<CustomHandleDescriptor> CustomHandleProvider(Guid elementId);
/// <summary>Receives custom-handle gesture updates.</summary>
public delegate void CustomHandleDragHandler(Guid elementId, string handleId, PanUpdatedEventArgs args, CustomHandleDragContext context);
/// <summary>Legacy-name custom handle provider retained for migration.</summary>
public delegate IReadOnlyList<ShapeHandleDescriptor> ShapeHandleProvider(Guid elementId);
/// <summary>Legacy-name custom handle gesture context.</summary>
public readonly record struct ShapeHandleDragContext(
    Guid ElementId, string HandleId, double DisplayW, double DisplayH, double LogicalW, double LogicalH)
{
    /// <summary>Converts a display-pixel delta to a normalized X delta.</summary>
    public float DeltaXToNormalized(double delta) => DisplayW > 0 ? (float)(delta / DisplayW) : 0;
    /// <summary>Converts a display-pixel delta to a normalized Y delta.</summary>
    public float DeltaYToNormalized(double delta) => DisplayH > 0 ? (float)(delta / DisplayH) : 0;
}
/// <summary>Receives legacy-name custom-handle gesture updates.</summary>
public delegate void ShapeHandleDragHandler(Guid elementId, string handleId, PanUpdatedEventArgs args, ShapeHandleDragContext context);

/// <summary>Receives a rectangle change from the editor.</summary>
public delegate Task InteractiveElementChangedHandler(InteractiveChange change);
/// <summary>Receives an element click.</summary>
public delegate Task InteractiveElementClickedHandler(Guid elementId);

/// <summary>Preview data prepared off the UI thread. The view is materialized at most once.</summary>
public sealed class PreparedPreview
{
    private readonly Func<View>? _viewFactory;
    private View? _materializedView;

    public PreparedPreview(Guid clipId, Func<View>? viewFactory, string? errorMessage, IClip? source)
    {
        ClipId = clipId;
        _viewFactory = viewFactory;
        ErrorMessage = errorMessage;
        Source = source;
    }

    /// <summary>Gets the element or clip identifier associated with the preview.</summary>
    public Guid ClipId { get; }
    /// <summary>Gets the deferred view factory, if one was supplied.</summary>
    public Func<View>? ViewFactory => _viewFactory;
    /// <summary>Gets the cached view, creating it once on first access.</summary>
    public View? View => _materializedView ??= _viewFactory?.Invoke();
    /// <summary>Gets the preview error, if preparation failed.</summary>
    public string? ErrorMessage { get; }
    /// <summary>Gets the optional source model used by timeline hosts.</summary>
    public IClip? Source { get; }
}

/// <summary>A view providing interactive editing capabilities for a set of elements on the UI.</summary>
/// <remarks>To create a new instance of the editor, use the <see cref="Create"/> method.</remarks>
public interface IInteractableEditor : IView, IContentView, ICrossPlatformLayout, IElement, IPadding, Microsoft.Maui.ITransform
{
    internal static Func<IInteractableEditor>? creator { get; set; }

    /// <summary>Creates a new instance of the editor using the implementation provided by the application. </summary>
    public static IInteractableEditor Create() => creator?.Invoke() ?? throw new InvalidOperationException("Creator is not available now.");

    /// <summary>Replaces the elements displayed by the editor.</summary>
    /// <param name="elements">Elements to display.</param>
    void SetInteractiveElements(IReadOnlyCollection<IInteractableElement> elements);
    /// <summary>Sets the selected element.</summary>
    /// <param name="elementId">Selected identifier, or <see langword="null"/> to clear selection.</param>
    void SetSelectedElement(Guid? elementId);
    /// <summary>Updates the display canvas size in device-independent pixels.</summary>
    void SetCanvasSize(double width, double height);
    /// <summary>Updates the logical video size.</summary>
    void SetVideoSize(double width, double height);
    /// <summary>Sets the current frame used for visibility filtering.</summary>
    void SetCurrentFrame(uint frame);
    /// <summary>Applies prepared previews on the UI thread.</summary>
    void ApplyPreparedPreviews(IReadOnlyList<PreparedPreview> previews);
    /// <summary>Dispatches preview application to the UI thread when necessary.</summary>
    Task<bool> ApplyPreparedPreviewsAsync(IReadOnlyList<PreparedPreview> previews);
    /// <summary>Starts or cancels reference-line placement.</summary>
    void AddReferenceLine(ReferenceLineOrientation? orientation);
    /// <summary>Removes a reference line by identifier.</summary>
    void RemoveReferenceLine(string id);
    /// <summary>Removes all reference lines.</summary>
    void ClearReferenceLines();
    /// <summary>Serializes reference lines using the host-compatible JSON format.</summary>
    string SerializeReferenceLines();
    /// <summary>Restores reference lines from JSON.</summary>
    void RestoreReferenceLines(string? json);
    /// <summary>Configures the preview refresh callback.</summary>
    IInteractableEditor ConfigurePreviewRefresh(Func<Task>? callback);
    /// <summary>Configures the element click callback.</summary>
    IInteractableEditor ConfigureElementClicked(InteractiveElementClickedHandler? callback);
    /// <summary>Configures the blank-canvas click callback.</summary>
    IInteractableEditor ConfigureBlankAreaClicked(Func<Task>? callback);
    /// <summary>Configures the rectangle-change callback.</summary>
    IInteractableEditor ConfigureElementChanged(InteractiveElementChangedHandler? callback);
    /// <summary>Configures custom handle rendering and drag callbacks.</summary>
    IInteractableEditor ConfigureCustomHandles(CustomHandleProvider? provider, CustomHandleDragHandler? dragHandler);
}
