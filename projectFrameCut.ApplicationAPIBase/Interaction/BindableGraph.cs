namespace projectFrameCut.ApplicationAPIBase.Interaction;

public enum GraphPortKind { Input, Output, Parameter }

public readonly record struct GraphPortReference(Guid NodeId, GraphPortKind Kind, string Key);

public sealed record GraphPort(string Key, GraphPortKind Kind, string DisplayName,
    Color Color, string? Value = null, string? Tooltip = null, bool CanConnect = true);

public sealed record GraphConnection(GraphPortReference Source, GraphPortReference Target,
    Color Color, bool IsDashed = false, double Thickness = 2.5);

public readonly record struct GraphViewport(double Scale = 1, double X = 0, double Y = 0)
{
    public GraphViewport() : this(1, 0, 0) { }
}

public sealed record GraphNodeMove(Guid NodeId, Point Previous, Point Current, GestureStatus Status);

/// <summary>Node data in logical canvas coordinates. RefreshNode applies changes to its appearance.</summary>
public class BindableGraphNode
{
    public required Guid Id { get; init; }
    public string DisplayName { get; set; } = "";
    public double X { get; set; }
    public double Y { get; set; }
    public bool CanMove { get; set; } = true;
    public bool CanMultiSelect { get; set; } = true;
    public bool ParametersOnTop { get; set; } = true;
    public Color BorderColor { get; set; } = Colors.Gray;
    public IReadOnlyList<GraphPort> Ports { get; set; } = [];
    public Func<View>? ContentFactory { get; set; }
    public Func<IBindableGraph, View>? ViewFactory { get; set; }
    public View? View { get; internal set; }
    public Border? Frame { get; internal set; }
}

/// <summary>Reusable interactive graph. The host supplies connection rules and optional persistence.</summary>
public interface IBindableGraph : IView, IContentView, ICrossPlatformLayout, IElement, IPadding, Microsoft.Maui.ITransform
{
    internal static Func<IBindableGraph>? Creator { get; set; }
    public static IBindableGraph Create() => Creator?.Invoke()
        ?? throw new InvalidOperationException("Creator is not available now.");

    IReadOnlySet<Guid> SelectedNodeIds { get; }
    IReadOnlyList<GraphConnection> Connections { get; }
    Guid? ActiveNodeId { get; }
    bool MultiSelect { get; set; }
    bool IsInteracting { get; }
    bool IsConnecting { get; }
    int InteractionVersion { get; }
    GraphViewport Viewport { get; }
    Guid? HighlightedNodeId { get; set; }
    IList<RectF> GroupBounds { get; }
    Func<GraphPortReference, GraphPortReference, bool>? CanConnect { get; set; }
    /// <summary>When supplied, the host commits the change and refreshes connections before returning true.</summary>
    Func<GraphConnection, Task<bool>>? ConnectAsync { get; set; }
    /// <summary>When supplied, the host removes bindings for this port and refreshes connections.</summary>
    Func<GraphPortReference, Task<bool>>? DisconnectAsync { get; set; }
    Func<GraphPortReference, bool>? IsPortConnected { get; set; }
    Func<Guid, Point, Point>? ResolveNodePosition { get; set; }

    event Action? SelectionChanged;
    event Action<GraphNodeMove>? NodeMoved;
    event Action<GraphViewport>? ViewportChanged;
    event Action<Guid>? NodeActivated;
    event Action<Guid>? NodeContextMenuRequested;
    event Action? BlankCanvasTapped;
    event Action? InteractionChanged;
    event Action? ConnectionRejected;

    void AddNode(BindableGraphNode node);
    void RefreshNode(Guid id);
    void RemoveNode(Guid id);
    void Clear();
    void SetConnections(IReadOnlyList<GraphConnection> connections);
    void SetSelection(IEnumerable<Guid> ids, Guid? activeNodeId = null, bool notify = false);
    void SelectNode(Guid id);
    void SetNodePosition(Guid id, Point position, bool commit = true);
    void SetViewport(GraphViewport viewport, bool notify = false);
    void Zoom(double scale, Point? focalPoint = null);
    void FitToContent();
    void Invalidate();
    void CancelInteraction();
    Point CanvasToView(Point point);
    Point ViewToCanvas(Point point);
    Point? GetPortPosition(GraphPortReference port);
    void AttachNodeSurface(Guid id, View selectionSurface, View dragSurface);
    void AttachPortSurface(GraphPortReference port, View surface);
}
