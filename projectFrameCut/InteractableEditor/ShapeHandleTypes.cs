namespace projectFrameCut.InteractableEditor;

/// <summary>
/// Describes one interactive handle to be drawn on top of a clip overlay.
/// Positions are in normalized clip-local coordinates [0..1] within the clip's display rect.
/// </summary>
public readonly struct ShapeHandleDescriptor
{
    public ShapeHandleDescriptor(string id, float normalizedX, float normalizedY, Color fillColor, double size) : this()
    {
        Id = id;
        NormalizedX = normalizedX;
        NormalizedY = normalizedY;
        FillColor = fillColor;
        Size = size;
        HandleGetter = null;
    }

    public ShapeHandleDescriptor(string id, float normalizedX, float normalizedY, Color fillColor, double size, Func<View>? handleGetter) : this(id, normalizedX, normalizedY, fillColor, size)
    {
        HandleGetter = handleGetter;
    }

    /// <summary>Unique identifier within the clip (e.g. "p1", "cp2", "corner-r").</summary>
    public string Id { get; init; }

    /// <summary>Normalized X position [0..1] within the clip viewport.</summary>
    public float NormalizedX { get; init; }

    /// <summary>Normalized Y position [0..1] within the clip viewport.</summary>
    public float NormalizedY { get; init; }

    /// <summary>Handle fill color.</summary>
    public Color FillColor { get; init; }

    /// <summary>Display-pixel size of the handle (default 12).</summary>
    public double Size { get; init; }

    /// <summary>
    /// Optional: if provided, this function is called to get a custom view to display for the handle.
    /// </summary>
    public Func<View>? HandleGetter { get; init; } 
}

/// <summary>
/// Delegate: given a clip ID, returns the shape handles to display for that clip.
/// Return an empty list for clips that have no custom handles.
/// </summary>
public delegate IReadOnlyList<ShapeHandleDescriptor> ShapeHandleProvider(Guid clipId);

/// <summary>
/// Context passed to the shape handle drag callback.
/// Contains the display-to-logical mapping needed to convert
/// gesture deltas (in display pixels) to normalized parameter deltas.
/// </summary>
public readonly struct ShapeHandleDragContext
{
    public Guid ClipId { get; init; }
    public string HandleId { get; init; }

    /// <summary>Clip display width in pixels on the editor canvas.</summary>
    public double DisplayW { get; init; }

    /// <summary>Clip display height in pixels on the editor canvas.</summary>
    public double DisplayH { get; init; }

    /// <summary>Clip logical width in video coordinates.</summary>
    public double LogicalW { get; init; }

    /// <summary>Clip logical height in video coordinates.</summary>
    public double LogicalH { get; init; }

    /// <summary>Convert a display-pixel X delta to normalized [0..1] delta.</summary>
    public readonly float DeltaXToNormalized(double displayDeltaX)
        => DisplayW > 0 ? (float)(displayDeltaX / DisplayW) : 0f;

    /// <summary>Convert a display-pixel Y delta to normalized [0..1] delta.</summary>
    public readonly float DeltaYToNormalized(double displayDeltaY)
        => DisplayH > 0 ? (float)(displayDeltaY / DisplayH) : 0f;
}

/// <summary>
/// Delegate: called when a shape handle is panned/dragged.
/// <paramref name="e"/> provides the gesture status (Started/Running/Completed/Canceled)
/// and the cumulative delta since the gesture began.
/// </summary>
public delegate void ShapeHandleDragHandler(
    Guid clipId,
    string handleId,
    PanUpdatedEventArgs e,
    ShapeHandleDragContext context);
