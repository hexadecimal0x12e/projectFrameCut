using Microsoft.Maui.Controls.Shapes;
using projectFrameCut.ApplicationAPIBase.Interaction;
using projectFrameCut.Services;
using projectFrameCut.Shared;

namespace projectFrameCut.Interaction;

public sealed class BindableGraphView : ContentView, IBindableGraph
{
    internal AbsoluteLayout NodesContainer { get; } = new() { AnchorX = 0, AnchorY = 0 };
    internal GraphicsView ConnectionsLayer { get; } = new() { InputTransparent = true };
    private readonly Dictionary<Guid, BindableGraphNode> _nodes = [];
    private readonly Dictionary<GraphPortReference, View> _ports = [];
    private readonly HashSet<Guid> _selection = [];
    private IReadOnlyList<GraphConnection> _connections = [];
    private GraphPortReference? _selectedPort, _dragPort;
    private Point? _dragPoint;
    private Guid? _movingNode;
    private Point _panStart, _moveStart, _moveTotal;
    private bool _multiSelect;
    private int? _submissionVersion;
    private bool IsSubmitting => _submissionVersion is not null;
    private int _generation;
    private Guid? _highlightedNodeId;
#if WINDOWS
    private Microsoft.UI.Xaml.UIElement? _wheelElement;
#endif

    public IReadOnlySet<Guid> SelectedNodeIds => _selection;
    public IReadOnlyList<GraphConnection> Connections => _connections;
    public Guid? ActiveNodeId { get; private set; }
    public bool IsInteracting => _movingNode is not null || _dragPort is not null;
    public bool IsConnecting => _selectedPort is not null;
    public int InteractionVersion => _generation;
    public GraphViewport Viewport => new(NodesContainer.Scale, NodesContainer.TranslationX, NodesContainer.TranslationY);
    public IList<RectF> GroupBounds { get; } = new List<RectF>();
    public Guid? HighlightedNodeId
    {
        get => _highlightedNodeId;
        set { _highlightedNodeId = value; Invalidate(); }
    }
    public bool MultiSelect
    {
        get => _multiSelect;
        set
        {
            if (_multiSelect == value) return;
            _multiSelect = value;
            SetSelection([], notify: true);
        }
    }
    public Func<GraphPortReference, GraphPortReference, bool>? CanConnect { get; set; }
    public Func<GraphConnection, Task<bool>>? ConnectAsync { get; set; }
    public Func<GraphPortReference, Task<bool>>? DisconnectAsync { get; set; }
    public Func<GraphPortReference, bool>? IsPortConnected { get; set; }
    public Func<Guid, Point, Point>? ResolveNodePosition { get; set; }
    public event Action? SelectionChanged;
    public event Action<GraphNodeMove>? NodeMoved;
    public event Action<GraphViewport>? ViewportChanged;
    public event Action<Guid>? NodeActivated;
    public event Action<Guid>? NodeContextMenuRequested;
    public event Action? BlankCanvasTapped;
    public event Action? InteractionChanged;
    public event Action? ConnectionRejected;

    public BindableGraphView()
    {
        var canvas = new Grid { BackgroundColor = Color.FromArgb("#1e1e1e"), IsClippedToBounds = true };
        ConnectionsLayer.Drawable = new ConnectionsDrawable(this);
        canvas.Add(ConnectionsLayer);
        canvas.Add(NodesContainer);
        var pan = new PanGestureRecognizer();
        pan.PanUpdated += (_, e) =>
        {
            if (IsInteracting || IsSubmitting) return;
            if (e.StatusType == GestureStatus.Started) _panStart = new(Viewport.X, Viewport.Y);
            else if (e.StatusType == GestureStatus.Running)
                SetViewport(new(Viewport.Scale, _panStart.X + e.TotalX, _panStart.Y + e.TotalY), true);
        };
        canvas.GestureRecognizers.Add(pan);
        var pinch = new PinchGestureRecognizer();
        pinch.PinchUpdated += (_, e) =>
        {
            if (e.Status == GestureStatus.Running && !IsInteracting)
                Zoom(Viewport.Scale * e.Scale, new Point(e.ScaleOrigin.X * Width, e.ScaleOrigin.Y * Height));
        };
        canvas.GestureRecognizers.Add(pinch);
        var tap = new TapGestureRecognizer();
        tap.Tapped += (_, e) =>
        {
            if (IsInteracting || e.GetPosition(canvas) is not { } point) return;
            var p = ViewToCanvas(point);
            if (_nodes.Values.Any(n => n.View?.IsVisible == true && NodeBounds(n).Contains(p))) return;
            CancelInteraction();
            if (!MultiSelect) SetSelection([], notify: true);
            BlankCanvasTapped?.Invoke();
        };
        canvas.GestureRecognizers.Add(tap);
        Content = canvas;
        Unloaded += (_, _) => CancelInteraction();
    }

    protected override void OnHandlerChanged()
    {
        base.OnHandlerChanged();
#if WINDOWS
        if (_wheelElement is not null) _wheelElement.PointerWheelChanged -= OnWheel;
        _wheelElement = Handler?.PlatformView as Microsoft.UI.Xaml.UIElement;
        if (_wheelElement is not null) _wheelElement.PointerWheelChanged += OnWheel;
#endif
        if (Handler is null) CancelInteraction();
    }

#if WINDOWS
    private void OnWheel(object sender, Microsoft.UI.Xaml.Input.PointerRoutedEventArgs e)
    {
        var state = Microsoft.UI.Input.InputKeyboardSource.GetKeyStateForCurrentThread(Windows.System.VirtualKey.Control);
        if (!IsEnabled || (state & Windows.UI.Core.CoreVirtualKeyStates.Down) == 0) return;
        var p = e.GetCurrentPoint((Microsoft.UI.Xaml.UIElement)sender);
        if (p.Properties.MouseWheelDelta == 0) return;
        Zoom(Viewport.Scale * (p.Properties.MouseWheelDelta > 0 ? 1.1 : 0.9), new Point(p.Position.X, p.Position.Y));
        e.Handled = true;
    }
#endif

    public void AddNode(BindableGraphNode node)
    {
        _nodes.Add(node.Id, node);
        RefreshNode(node.Id);
    }

    public void RefreshNode(Guid id)
    {
        if (!_nodes.TryGetValue(id, out var node)) return;
        bool visible = node.View?.IsVisible != false;
        if (node.View is not null) NodesContainer.Children.Remove(node.View);
        foreach (var key in _ports.Keys.Where(p => p.NodeId == id).ToArray()) _ports.Remove(key);
        node.View = node.ViewFactory?.Invoke(this) ?? CreateNodeView(node);
        node.View.IsVisible = visible;
        node.View.SizeChanged += (_, _) => Invalidate();
        NodesContainer.Add(node.View);
        SetNodePosition(id, new Point(node.X, node.Y));
        if (_selectedPort is { } port && port.NodeId == id)
        {
            if (_ports.TryGetValue(port, out var surface)) surface.Scale = 1.6;
            else CancelInteraction();
        }
        RefreshSelection();
        Invalidate();
    }

    public void RemoveNode(Guid id)
    {
        if (!_nodes.Remove(id, out var node)) return;
        if (_selectedPort?.NodeId == id || _dragPort?.NodeId == id || _movingNode == id) CancelInteraction();
        if (node.View is not null) NodesContainer.Children.Remove(node.View);
        node.View = null;
        foreach (var key in _ports.Keys.Where(p => p.NodeId == id).ToArray()) _ports.Remove(key);
        _connections = _connections.Where(c => c.Source.NodeId != id && c.Target.NodeId != id).ToArray();
        bool changed = _selection.Remove(id);
        if (ActiveNodeId == id) ActiveNodeId = null;
        if (changed) SelectionChanged?.Invoke();
        Invalidate();
    }

    public void Clear()
    {
        CancelInteraction();
        NodesContainer.Children.Clear();
        _nodes.Clear();
        _ports.Clear();
        _connections = [];
        GroupBounds.Clear();
        HighlightedNodeId = null;
        SetSelection([]);
        Invalidate();
    }

    public void SetConnections(IReadOnlyList<GraphConnection> connections)
    {
        _connections = connections.ToArray();
        Invalidate();
    }

    public void SetSelection(IEnumerable<Guid> ids, Guid? activeNodeId = null, bool notify = false)
    {
        var selected = ids.Where(_nodes.ContainsKey).ToArray();
        _selection.Clear();
        _selection.UnionWith(selected);
        ActiveNodeId = activeNodeId is { } id && _nodes.ContainsKey(id) ? id : selected.Cast<Guid?>().FirstOrDefault();
        RefreshSelection();
        if (notify) SelectionChanged?.Invoke();
    }

    public void SelectNode(Guid id)
    {
        if (!_nodes.TryGetValue(id, out var node) || IsInteracting) return;
        if (MultiSelect)
        {
            if (!node.CanMultiSelect) return;
            if (!_selection.Add(id)) _selection.Remove(id);
            ActiveNodeId = id;
            RefreshSelection();
            SelectionChanged?.Invoke();
        }
        else SetSelection([id], id, true);
    }

    private void RefreshSelection()
    {
        foreach (var node in _nodes.Values)
            if (node.Frame is { } frame) frame.Stroke = _selection.Contains(node.Id) ? Colors.Yellow : node.BorderColor;
    }

    public void SetNodePosition(Guid id, Point position, bool commit = true)
    {
        if (!_nodes.TryGetValue(id, out var node)) return;
        node.X = position.X;
        node.Y = position.Y;
        if (node.View is not { } view) return;
        if (commit)
        {
            AbsoluteLayout.SetLayoutBounds(view, new Rect(position.X, position.Y, AbsoluteLayout.AutoSize, AbsoluteLayout.AutoSize));
            view.TranslationX = view.TranslationY = 0;
        }
        else
        {
            var bounds = AbsoluteLayout.GetLayoutBounds(view);
            view.TranslationX = position.X - bounds.X;
            view.TranslationY = position.Y - bounds.Y;
        }
        Invalidate();
    }

    public void SetViewport(GraphViewport viewport, bool notify = false)
    {
        if (!double.IsFinite(viewport.Scale) || !double.IsFinite(viewport.X) || !double.IsFinite(viewport.Y)) return;
        NodesContainer.Scale = Math.Clamp(viewport.Scale, 0.2, 5);
        NodesContainer.TranslationX = viewport.X;
        NodesContainer.TranslationY = viewport.Y;
        Invalidate();
        if (notify) ViewportChanged?.Invoke(Viewport);
    }

    public void Zoom(double scale, Point? focalPoint = null)
    {
        if (!double.IsFinite(scale)) return;
        var focal = focalPoint ?? new Point(Width / 2, Height / 2);
        var logical = ViewToCanvas(focal);
        scale = Math.Clamp(scale, 0.2, 5);
        SetViewport(new(scale, focal.X - logical.X * scale, focal.Y - logical.Y * scale), true);
    }

    public void FitToContent()
    {
        var bounds = _nodes.Values.Where(n => n.View?.IsVisible == true).Select(NodeBounds).ToArray();
        if (bounds.Length == 0 || Width <= 40 || Height <= 40) return;
        double x = bounds.Min(b => b.X), y = bounds.Min(b => b.Y);
        double w = bounds.Max(b => b.Right) - x, h = bounds.Max(b => b.Bottom) - y;
        double scale = Math.Clamp(Math.Min((Width - 40) / Math.Max(1, w), (Height - 40) / Math.Max(1, h)), 0.2, 5);
        SetViewport(new(scale, (Width - w * scale) / 2 - x * scale, (Height - h * scale) / 2 - y * scale), true);
    }

    private static Rect NodeBounds(BindableGraphNode node) => new(node.X, node.Y,
        node.View?.Width > 0 ? node.View.Width : 150, node.View?.Height > 0 ? node.View.Height : 80);
    public Point CanvasToView(Point p) => new(p.X * Viewport.Scale + Viewport.X, p.Y * Viewport.Scale + Viewport.Y);
    public Point ViewToCanvas(Point p) => new((p.X - Viewport.X) / Viewport.Scale, (p.Y - Viewport.Y) / Viewport.Scale);
    public void Invalidate() => ConnectionsLayer.Invalidate();

    public Point? GetPortPosition(GraphPortReference port)
    {
        if (!_ports.TryGetValue(port, out var surface) || !_nodes.TryGetValue(port.NodeId, out var node)
            || node.View?.IsVisible != true || !surface.IsVisible || surface.Width <= 0 || surface.Height <= 0) return null;
        double x = surface.Width / 2, y = surface.Height / 2;
        for (View? v = surface; v is not null; v = v.Parent as View)
        {
            if (ReferenceEquals(v, node.View)) return new Point(node.X + x, node.Y + y);
            if (!v.IsVisible) return null;
            x += v.X + v.TranslationX;
            y += v.Y + v.TranslationY;
        }
        return null;
    }

    public void AttachNodeSurface(Guid id, View selectionSurface, View dragSurface)
    {
        if (_nodes.TryGetValue(id, out var node) && selectionSurface is Border frame) node.Frame = frame;
#if LINUX
        var tap = new TapGestureRecognizer();
        tap.Tapped += (_, _) => SelectNode(id);
        selectionSurface.GestureRecognizers.Add(tap);
        var doubleTap = new TapGestureRecognizer { NumberOfTapsRequired = 2 };
        doubleTap.Tapped += (_, _) => { if (!MultiSelect && !IsInteracting) NodeActivated?.Invoke(id); };
        selectionSurface.GestureRecognizers.Add(doubleTap);
        var contextTap = new TapGestureRecognizer { Buttons = ButtonsMask.Secondary };
        contextTap.Tapped += (_, _) => NodeContextMenuRequested?.Invoke(id);
        selectionSurface.GestureRecognizers.Add(contextTap);
#else
        UIServices.RegisterSelectOrContextMenu(selectionSurface,
            OnSelected: () => SelectNode(id),
            OnClicked: () =>
            {
#if ANDROID || IOS
                SelectNode(id);
#else
                if (!MultiSelect && !IsInteracting) NodeActivated?.Invoke(id);
#endif
            },
            OnContextMenuClick: () => NodeContextMenuRequested?.Invoke(id));
#endif
        var pan = new PanGestureRecognizer();
        pan.PanUpdated += (_, e) => OnNodePan(id, e);
        dragSurface.GestureRecognizers.Add(pan);
    }

    private void OnNodePan(Guid id, PanUpdatedEventArgs e)
    {
        if (!_nodes.TryGetValue(id, out var node) || !node.CanMove || IsSubmitting) return;
        var previous = new Point(node.X, node.Y);
        switch (e.StatusType)
        {
            case GestureStatus.Started:
                _movingNode = id;
                _moveStart = previous;
                _moveTotal = Point.Zero;
                InteractionChanged?.Invoke();
                break;
            case GestureStatus.Running when _movingNode == id:
                var proposed = new Point(node.X + (e.TotalX - _moveTotal.X) / Viewport.Scale,
                    node.Y + (e.TotalY - _moveTotal.Y) / Viewport.Scale);
                var resolved = ResolveNodePosition?.Invoke(id, proposed) ?? proposed;
                _moveTotal = new Point(resolved.X == proposed.X ? e.TotalX : _moveTotal.X,
                    resolved.Y == proposed.Y ? e.TotalY : _moveTotal.Y);
                SetNodePosition(id, resolved, false);
                break;
            case GestureStatus.Completed:
            case GestureStatus.Canceled:
                if (_movingNode != id) return;
                SetNodePosition(id, e.StatusType == GestureStatus.Canceled ? _moveStart : previous);
                _movingNode = null;
                InteractionChanged?.Invoke();
                break;
            default: return;
        }
        NodeMoved?.Invoke(new(id, previous, new Point(node.X, node.Y), e.StatusType));
    }

    public void AttachPortSurface(GraphPortReference port, View surface)
    {
        _ports[port] = surface;
        surface.SizeChanged += (_, _) => Invalidate();
        var pan = new PanGestureRecognizer();
        pan.PanUpdated += async (_, e) => await OnPortPanAsync(port, e);
        surface.GestureRecognizers.Add(pan);
        var tap = new TapGestureRecognizer();
        tap.Tapped += async (_, _) =>
        {
            if (IsInteracting || IsSubmitting || _selectedPort is not { } selected) return;
            await TryConnectAsync(selected, port);
        };
        surface.GestureRecognizers.Add(tap);
        var doubleTap = new TapGestureRecognizer { NumberOfTapsRequired = 2 };
        doubleTap.Tapped += (_, _) =>
        {
            if (IsInteracting || IsSubmitting || _selectedPort is not null || PortOf(port)?.CanConnect != true
                || (IsPortConnected?.Invoke(port) ?? _connections.Any(c => c.Source == port || c.Target == port))) return;
            _selectedPort = port;
            surface.Scale = 1.6;
            InteractionChanged?.Invoke();
        };
        surface.GestureRecognizers.Add(doubleTap);
    }

    private GraphPort? PortOf(GraphPortReference p) => _nodes.GetValueOrDefault(p.NodeId)?.Ports
        .FirstOrDefault(x => x.Kind == p.Kind && x.Key == p.Key);
    private bool CanJoin(GraphPortReference a, GraphPortReference b)
    {
        if (a.NodeId == b.NodeId || (a.Kind == GraphPortKind.Output) == (b.Kind == GraphPortKind.Output)
            || PortOf(a)?.CanConnect != true || PortOf(b)?.CanConnect != true) return false;
        return a.Kind == GraphPortKind.Output ? CanConnect?.Invoke(a, b) != false : CanConnect?.Invoke(b, a) != false;
    }

    private GraphPortReference? FindPortTarget(GraphPortReference source, Point point, double radius)
    {
        GraphPortReference? hit = null;
        double best = radius * radius;
        foreach (var port in _ports.Keys)
        {
            if (!CanJoin(source, port) || GetPortPosition(port) is not { } position) continue;
            var p = CanvasToView(position);
            double distance = (p.X - point.X) * (p.X - point.X) + (p.Y - point.Y) * (p.Y - point.Y);
            if (distance >= best) continue;
            best = distance;
            hit = port;
        }
        return hit;
    }

    private async Task OnPortPanAsync(GraphPortReference port, PanUpdatedEventArgs e)
    {
        if (IsSubmitting || PortOf(port)?.CanConnect != true) return;
        try
        {
            if (e.StatusType == GestureStatus.Started)
            {
                CancelInteraction();
                _dragPort = port;
                _dragPoint = GetPortPosition(port) is { } p ? CanvasToView(p) : null;
                InteractionChanged?.Invoke();
            }
            else if (_dragPort == port && e.StatusType == GestureStatus.Running && GetPortPosition(port) is { } p)
            {
                var start = CanvasToView(p);
                var point = new Point(start.X + e.TotalX, start.Y + e.TotalY);
                _dragPoint = FindPortTarget(port, point, 50) is { } target && GetPortPosition(target) is { } end
                    ? CanvasToView(end) : point;
            }
            else if (_dragPort == port && e.StatusType is GestureStatus.Completed or GestureStatus.Canceled)
            {
                var point = _dragPoint;
                _dragPort = null;
                _dragPoint = null;
                InteractionChanged?.Invoke();
                if (e.StatusType == GestureStatus.Completed && point is { } drop)
                {
                    if (FindPortTarget(port, drop, 40) is { } target) await TryConnectAsync(port, target);
                    else await DisconnectPortAsync(port);
                }
            }
        }
        catch (Exception ex) { Logger.Log(ex, "Interact with graph port", this); }
        finally { Invalidate(); }
    }

    private async Task DisconnectPortAsync(GraphPortReference port)
    {
        int generation = _generation;
        _submissionVersion = generation;
        try
        {
            if (DisconnectAsync is { } disconnect) await disconnect(port);
            else SetConnections(_connections.Where(c => c.Source != port && c.Target != port).ToArray());
        }
        finally
        {
            if (_submissionVersion == generation) _submissionVersion = null;
        }
    }

    private async Task TryConnectAsync(GraphPortReference a, GraphPortReference b)
    {
        if (!CanJoin(a, b)) { ConnectionRejected?.Invoke(); return; }
        var source = a.Kind == GraphPortKind.Output ? a : b;
        var target = a.Kind == GraphPortKind.Output ? b : a;
        var connection = new GraphConnection(source, target, PortOf(source)!.Color, target.Kind == GraphPortKind.Parameter);
        int generation = _generation;
        _submissionVersion = generation;
        try
        {
            bool accepted;
            if (ConnectAsync is { } connect) accepted = await connect(connection);
            else
            {
                SetConnections(_connections.Where(c => c.Target != target).Append(connection).ToArray());
                accepted = true;
            }
            if (generation != _generation) return;
            if (accepted) CancelInteraction();
            else ConnectionRejected?.Invoke();
        }
        catch (Exception ex) { Logger.Log(ex, "Connect graph ports", this); ConnectionRejected?.Invoke(); }
        finally
        {
            if (_submissionVersion == generation) _submissionVersion = null;
        }
    }

    public void CancelInteraction()
    {
        _generation++;
        _submissionVersion = null;
        if (_selectedPort is { } selected && _ports.TryGetValue(selected, out var surface)) surface.Scale = 1;
        _selectedPort = _dragPort = null;
        _dragPoint = null;
        if (_movingNode is { } id && _nodes.TryGetValue(id, out var node))
        {
            var previous = new Point(node.X, node.Y);
            SetNodePosition(id, _moveStart);
            _movingNode = null;
            NodeMoved?.Invoke(new(id, previous, _moveStart, GestureStatus.Canceled));
        }
        InteractionChanged?.Invoke();
        Invalidate();
    }

    private View CreateNodeView(BindableGraphNode node)
    {
        var container = new VerticalStackLayout { Spacing = 2, MinimumWidthRequest = 150 };
        var body = new Grid { ColumnDefinitions = new ColumnDefinitionCollection
            { new(GridLength.Auto), new(GridLength.Star), new(GridLength.Auto) }, ColumnSpacing = 6 };
        var content = new VerticalStackLayout { Spacing = 4, Padding = 6 };
        var title = new Label { Text = node.DisplayName, TextColor = Colors.White, HorizontalTextAlignment = TextAlignment.Center };
        content.Add(title);
        if (node.ContentFactory?.Invoke() is { } custom) content.Add(custom);
        body.Add(content, 1, 0);
        var inputs = new VerticalStackLayout { Spacing = 4, VerticalOptions = LayoutOptions.Center };
        var outputs = new VerticalStackLayout { Spacing = 4, VerticalOptions = LayoutOptions.Center };
        var parameters = new VerticalStackLayout { Spacing = 2 };
        foreach (var port in node.Ports)
        {
            var row = new HorizontalStackLayout { Spacing = 4, HeightRequest = 24 };
            var dot = new BoxView { Color = port.Color, WidthRequest = port.Kind == GraphPortKind.Parameter ? 9 : 15,
                HeightRequest = port.Kind == GraphPortKind.Parameter ? 9 : 15, VerticalOptions = LayoutOptions.Center };
            AttachPortSurface(new(node.Id, port.Kind, port.Key), dot);
            ToolTipProperties.SetText(dot, port.Tooltip ?? port.DisplayName);
            var label = new Label { Text = port.DisplayName, TextColor = Colors.LightGray, FontSize = 10, VerticalOptions = LayoutOptions.Center };
            if (port.Kind == GraphPortKind.Output) { row.Add(label); row.Add(dot); outputs.Add(row); }
            else
            {
                row.Add(dot);
                row.Add(label);
                if (port.Value is not null) row.Add(new Label { Text = port.Value, TextColor = Colors.White, FontSize = 10, VerticalOptions = LayoutOptions.Center });
                if (port.Kind == GraphPortKind.Parameter) parameters.Add(row);
                else inputs.Add(row);
            }
        }
        body.Add(inputs);
        body.Add(outputs, 2, 0);
        var frame = new Border { Content = body, Stroke = node.BorderColor, StrokeThickness = 2,
            StrokeShape = new RoundRectangle { CornerRadius = 5 }, BackgroundColor = Color.FromArgb("#2d2d2d"), Padding = 5 };
        node.Frame = frame;
        AttachNodeSurface(node.Id, frame, title);
        if (node.ParametersOnTop) container.Add(parameters);
        container.Add(frame);
        if (!node.ParametersOnTop) container.Add(parameters);
        return container;
    }

    private sealed class ConnectionsDrawable(BindableGraphView graph) : IDrawable
    {
        public void Draw(ICanvas canvas, RectF dirtyRect)
        {
            canvas.SaveState();
            double scale = graph.Viewport.Scale;
            canvas.StrokeColor = Colors.CornflowerBlue;
            canvas.StrokeSize = (float)(2 * scale);
            foreach (var bounds in graph.GroupBounds)
            {
                var p = graph.CanvasToView(new Point(bounds.X, bounds.Y));
                canvas.DrawRoundedRectangle((float)p.X, (float)p.Y, (float)(bounds.Width * scale), (float)(bounds.Height * scale), 8);
            }
            foreach (var c in graph._connections)
            {
                if (graph.GetPortPosition(c.Source) is not { } start || graph.GetPortPosition(c.Target) is not { } end) continue;
                float emphasis = 1;
                var color = c.Color;
                if (graph.HighlightedNodeId is { } id)
                {
                    if (c.Target.NodeId == id) { color = Color.FromArgb("#00E5FF"); emphasis = 1.8f; }
                    else if (c.Source.NodeId == id) { color = color.WithAlpha(0.75f); emphasis = 1.4f; }
                    else color = color.WithAlpha(0.12f);
                }
                canvas.StrokeColor = color;
                canvas.StrokeSize = (float)(c.Thickness * scale * emphasis);
                canvas.StrokeDashPattern = c.IsDashed ? [4, 3] : null;
                DrawCurve(canvas, graph.CanvasToView(start), graph.CanvasToView(end));
            }
            canvas.StrokeDashPattern = null;
            if (graph._dragPort is { } port && graph._dragPoint is { } drop && graph.GetPortPosition(port) is { } position)
            {
                canvas.StrokeColor = graph.PortOf(port)?.Color ?? Colors.Gray;
                canvas.StrokeSize = (float)(2 * scale);
                DrawCurve(canvas, graph.CanvasToView(position), drop);
            }
            canvas.RestoreState();
        }

        private static void DrawCurve(ICanvas canvas, Point start, Point end)
        {
            var path = new PathF();
            path.MoveTo((float)start.X, (float)start.Y);
            path.CurveTo((float)start.X + 50, (float)start.Y, (float)end.X - 50, (float)end.Y, (float)end.X, (float)end.Y);
            canvas.DrawPath(path);
        }
    }
}
