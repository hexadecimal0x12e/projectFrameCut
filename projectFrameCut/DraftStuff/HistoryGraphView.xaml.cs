using LocalizedResources;
using Color = Microsoft.Maui.Graphics.Color;
using Point = Microsoft.Maui.Graphics.Point;
using Rect = Microsoft.Maui.Graphics.Rect;

namespace projectFrameCut.DraftStuff;

public partial class HistoryGraphView : ContentView
{
    #region Node ViewModel & Drawables

    public sealed class HistoryGraphNodeViewModel
    {
        public Guid SnapshotID { get; init; }
        public Guid PreviousSnapshotID { get; set; }
        public List<Guid> NextSnapshotIDs { get; set; } = new();
        public DateTime SavedAt { get; init; }
        public string ChangeReason { get; init; } = string.Empty;
        public string ChangedByUserDisplayName { get; init; } = string.Empty;
        public Guid ChangedByUser { get; init; }
        public bool IsCurrentSnapshot { get; set; }
        public bool IsHead { get; set; }

        // Layout
        public double X { get; set; }
        public double Y { get; set; }
        public int Depth { get; set; }
        public View? View { get; set; }

        public double NodeWidth => 260;
        public double NodeHeight => 76;

        public string DisplayLabel => IsCurrentSnapshot ? $"* {ChangeReason}" : ChangeReason;

        public string TimeDisplay
        {
            get
            {
                if (SavedAt == DateTime.MinValue) return "?";
                var local = SavedAt.Kind == DateTimeKind.Utc ? SavedAt.ToLocalTime() : SavedAt;
                return local.ToString("MM-dd HH:mm");
            }
        }
    }

    public sealed class HistoryGraphConnectionsDrawable : IDrawable
    {
        public List<(HistoryGraphNodeViewModel From, HistoryGraphNodeViewModel To)> Connections = new();
        public double PanX, PanY, Scale = 1.0;

        static readonly Color BranchColor = Color.FromArgb("#666666");
        static readonly Color CurrentPathColor = Color.FromArgb("#66BB6A");

        public void Draw(ICanvas canvas, RectF dirtyRect)
        {
            canvas.SaveState();
            canvas.StrokeLineCap = LineCap.Round;
            canvas.Antialias = true;

            foreach (var (from, to) in Connections)
            {
                bool isCurrentPath = from.Depth == 0 && to.Depth == 0;

                canvas.StrokeColor = isCurrentPath ? CurrentPathColor : BranchColor;
                canvas.StrokeSize = (float)(isCurrentPath ? 3 * Scale : 2 * Scale);

                var start = new Point(
                    (from.X + from.NodeWidth) * Scale + PanX,
                    (from.Y + from.NodeHeight / 2) * Scale + PanY);
                var end = new Point(
                    (to.X) * Scale + PanX,
                    (to.Y + to.NodeHeight / 2) * Scale + PanY);

                DrawCurve(canvas, start, end);
            }

            canvas.RestoreState();
        }

        private static void DrawCurve(ICanvas canvas, Point start, Point end)
        {
            var path = new PathF();
            path.MoveTo((float)start.X, (float)start.Y);
            float cpOffset = Math.Min(60, (float)Math.Abs(end.X - start.X) * 0.4f);
            if (cpOffset < 10) cpOffset = 10;
            path.CurveTo(
                (float)(start.X + cpOffset), (float)start.Y,
                (float)(end.X - cpOffset), (float)end.Y,
                (float)end.X, (float)end.Y);
            canvas.DrawPath(path);
        }
    }

    #endregion

    #region Fields

    private IHistoryGraphProvider? _provider;
    private readonly List<HistoryGraphNodeViewModel> _nodeViewModels = new();
    private HistoryGraphConnectionsDrawable _connectionsDrawable = null!;
    private HistoryGraphNodeViewModel? _selectedNode;
    private Guid _selectedSnapshotId = Guid.Empty;
    private Guid _currentSnapshotId = Guid.Empty;

    // List view state
    private List<HistoryGraphNode> _currentNodes = new();
    private List<HistoryGraphRowDrawable> _rowDrawables = new();
    private List<GraphicsView> _rowGraphicsViews = new();
    private HistoryViewMode _viewMode = HistoryViewMode.Graph;

    // Canvas state
    private double _panStartX, _panStartY;
    private double _startScale = 1.0;
    private bool _pendingInitialFocus;
    private const double MinScale = 0.04;
    private const double MaxScale = 5.0;

    #endregion

    #region Constructor

    public HistoryGraphView()
    {
        InitializeComponent();
        _connectionsDrawable = new HistoryGraphConnectionsDrawable();
        ConnectionsLayer.Drawable = _connectionsDrawable;
    }

    public HistoryGraphView(IHistoryGraphProvider provider) : this()
    {
        _provider = provider ?? throw new ArgumentNullException(nameof(provider));
        _provider.CurrentSnapshotChanged += OnProviderCurrentSnapshotChanged;
    }

    [Obsolete("Use HistoryGraphView(IHistoryGraphProvider) instead")]
    public HistoryGraphView(DraftPage page) : this(new DraftHistoryGraphProvider(page))
    {
    }

    protected override void OnHandlerChanged()
    {
        base.OnHandlerChanged();
#if WINDOWS
        if (Handler?.PlatformView is Microsoft.UI.Xaml.UIElement platformView)
        {
            platformView.PointerWheelChanged -= OnWindowsPointerWheelChanged;
            platformView.PointerWheelChanged += OnWindowsPointerWheelChanged;
        }
#endif
    }

    #endregion

    #region Public API

    public HistoryViewMode ViewMode
    {
        get => _viewMode;
        set
        {
            if (_viewMode == value) return;
            _viewMode = value;
            UpdateViewMode();
        }
    }

    public void LoadHistory(
        List<HistoryGraphNode> nodes,
        List<HistoryGraphEdge> edges,
        Guid currentSnapshotId)
    {
        _currentSnapshotId = currentSnapshotId;
        _selectedNode = null;
        _selectedSnapshotId = Guid.Empty;
        DetailsPanel.IsVisible = false;

        // Clear existing
        NodesContainer.Children.Clear();
        _nodeViewModels.Clear();
        _connectionsDrawable.Connections.Clear();
        ListContent.Children.Clear();
        _currentNodes.Clear();
        _rowDrawables.Clear();
        _rowGraphicsViews.Clear();

        if (nodes.Count == 0)
        {
            EmptyHint.IsVisible = true;
            InfoLabel.Text = "";
            GraphViewContainer.IsVisible = _viewMode == HistoryViewMode.Graph;
            ListViewContainer.IsVisible = _viewMode == HistoryViewMode.List;
            ConnectionsLayer.Invalidate();
            return;
        }

        EmptyHint.IsVisible = false;

        // Store raw nodes for list view
        _currentNodes = nodes;

        // ── Build graph view ──
        BuildGraphView(nodes, currentSnapshotId);

        // ── Build list view ──
        BuildListView(nodes, edges);

        InfoLabel.Text = _provider is not null
            ? $"{_provider.ProviderName}: {nodes.Count} snapshots"
            : $"{nodes.Count} snapshots";

        UpdateViewMode();

        // Keep nodes readable on first open and start around the active snapshot.
        _pendingInitialFocus = true;
        Dispatcher.Dispatch(TryApplyInitialView);
    }

    public void RefreshSelection()
    {
        if (_provider is null) return;
        _currentSnapshotId = _provider.CurrentSnapshotID;

        // Update graph view selection
        foreach (var vm in _nodeViewModels)
        {
            vm.IsCurrentSnapshot = vm.SnapshotID == _currentSnapshotId;
            if (vm.View is Border border)
            {
                UpdateNodeBorderStyle(border, vm);
            }
        }
        ConnectionsLayer.Invalidate();

        // Update list view selection
        for (int i = 0; i < _rowDrawables.Count; i++)
        {
            bool isCurrent = i < _currentNodes.Count && _currentNodes[i].SnapshotID == _currentSnapshotId;
            _rowDrawables[i].IsCurrentSnapshot = isCurrent;
            _rowDrawables[i].IsSelected = _currentNodes.Count > i && _currentNodes[i].SnapshotID == _selectedSnapshotId;
            if (i < _rowGraphicsViews.Count)
                _rowGraphicsViews[i].Invalidate();
        }

        // Update current highlighting in rows
        for (int i = 0; i < ListContent.Children.Count; i++)
        {
            if (ListContent.Children[i] is Grid row && i < _currentNodes.Count)
            {
                row.BackgroundColor = _currentNodes[i].SnapshotID == _currentSnapshotId
                    ? Color.FromArgb("#1A4A9EFF")
                    : Colors.Transparent;
            }
        }
    }

    #endregion

    #region Graph View Building

    private void BuildGraphView(
        List<HistoryGraphNode> nodes,
        Guid currentSnapshotId)
    {
        // Convert to viewmodels
        var nodeDict = new Dictionary<Guid, HistoryGraphNodeViewModel>();
        foreach (var node in nodes)
        {
            var vm = new HistoryGraphNodeViewModel
            {
                SnapshotID = node.SnapshotID,
                PreviousSnapshotID = node.PreviousSnapshotID,
                NextSnapshotIDs = node.NextSnapshotIDs,
                SavedAt = node.SavedAt,
                ChangeReason = node.ChangeReason,
                ChangedByUserDisplayName = node.ChangedByUserDisplayName,
                ChangedByUser = node.ChangedByUser,
                IsCurrentSnapshot = node.SnapshotID == currentSnapshotId,
                IsHead = node.IsHead,
            };
            _nodeViewModels.Add(vm);
            nodeDict[vm.SnapshotID] = vm;
        }

        var parentByNode = BuildParentMap(_nodeViewModels, nodeDict);
        ComputeLayout(_nodeViewModels, nodeDict, parentByNode, currentSnapshotId);

        // Create MAUI views
        foreach (var vm in _nodeViewModels)
        {
            var view = CreateNodeView(vm);
            vm.View = view;
            NodesContainer.Children.Add(view);
            AbsoluteLayout.SetLayoutBounds(view, new Rect(vm.X, vm.Y, vm.NodeWidth, vm.NodeHeight));
        }

        // Draw the same validated parent links used by the layout. Snapshot Previous links
        // are authoritative; stale or differently ordered Next lists must not move edges.
        foreach (var (childId, parentId) in parentByNode)
        {
            _connectionsDrawable.Connections.Add((nodeDict[parentId], nodeDict[childId]));
        }

        ConnectionsLayer.Invalidate();
    }

    #endregion

    #region List View Building

    private void BuildListView(List<HistoryGraphNode> nodes, List<HistoryGraphEdge> edges)
    {
        var edgeSet = new HashSet<(Guid from, Guid to)>();
        foreach (var edge in edges)
            edgeSet.Add((edge.FromSnapshotID, edge.ToSnapshotID));

        for (int i = 0; i < nodes.Count; i++)
        {
            var node = nodes[i];
            Guid prevId = node.PreviousSnapshotID;
            Guid nextId = node.NextSnapshotID;

            bool hasPredecessor = edgeSet.Contains((prevId, node.SnapshotID)) || prevId != Guid.Empty;
            bool hasSuccessor = edgeSet.Contains((node.SnapshotID, nextId)) || nextId != Guid.Empty;
            bool isFirst = i == 0;
            bool isLast = i == nodes.Count - 1;

            var row = BuildListRow(node, hasPredecessor || !isFirst, hasSuccessor || !isLast);

            var capturedNode = node;
            var tapGesture = new TapGestureRecognizer();
            tapGesture.Tapped += (_, _) => OnListItemTapped(capturedNode);
            row.GestureRecognizers.Add(tapGesture);

            ListContent.Children.Add(row);
        }
    }

    private View BuildListRow(HistoryGraphNode node, bool hasPredecessor, bool hasSuccessor)
    {
        var drawable = new HistoryGraphRowDrawable
        {
            HasPredecessor = hasPredecessor,
            HasSuccessor = hasSuccessor,
            IsCurrentSnapshot = node.SnapshotID == _currentSnapshotId,
            IsSelected = node.SnapshotID == _selectedSnapshotId
        };

        var graphView = new GraphicsView
        {
            WidthRequest = 50,
            HeightRequest = 56,
            Drawable = drawable,
            BackgroundColor = Colors.Transparent
        };

        _rowDrawables.Add(drawable);
        _rowGraphicsViews.Add(graphView);

        string reasonText = string.IsNullOrWhiteSpace(node.ChangeReason)
            ? "(no description)"
            : node.ChangeReason.Trim();
        if (node.IsCurrentSnapshot) reasonText = "* " + reasonText;

        bool isCurrent = node.IsCurrentSnapshot;

        var reasonLabel = new Label
        {
            Text = reasonText,
            TextColor = isCurrent ? Colors.White : Color.FromArgb("#ccc"),
            FontSize = 13,
            FontAttributes = isCurrent ? FontAttributes.Bold : FontAttributes.None,
            LineBreakMode = LineBreakMode.TailTruncation,
            VerticalOptions = LayoutOptions.Center
        };

        var timeLabel = new Label
        {
            Text = node.RelativeTimeDisplay,
            TextColor = Color.FromArgb("#999"),
            FontSize = 11,
            VerticalOptions = LayoutOptions.Center
        };

        var textStack = new VerticalStackLayout
        {
            Spacing = 2,
            VerticalOptions = LayoutOptions.Center,
            Children = { reasonLabel, timeLabel }
        };

        var row = new Grid
        {
            ColumnDefinitions =
            {
                new ColumnDefinition { Width = 50 },
                new ColumnDefinition { Width = GridLength.Star }
            },
            Padding = new Thickness(0, 0),
            BackgroundColor = isCurrent ? Color.FromArgb("#1A4A9EFF") : Colors.Transparent
        };

        row.Add(graphView, 0);
        row.Add(textStack, 1);

        return row;
    }

    private void OnListItemTapped(HistoryGraphNode node)
    {
        _selectedSnapshotId = node.SnapshotID;
        _selectedNode = null;

        for (int i = 0; i < _rowDrawables.Count; i++)
        {
            _rowDrawables[i].IsSelected = i < _currentNodes.Count && _currentNodes[i].SnapshotID == _selectedSnapshotId;
            if (i < _rowGraphicsViews.Count)
                _rowGraphicsViews[i].Invalidate();
        }

        _provider?.OnNodeSelected(node);

        // Update details panel (same shared panel)
        ShowDetailsPanel(node);
    }

    #endregion

    #region Layout Algorithm

    private void ComputeLayout(
        List<HistoryGraphNodeViewModel> nodes,
        Dictionary<Guid, HistoryGraphNodeViewModel> nodeDict,
        Dictionary<Guid, Guid> parentByNode,
        Guid currentSnapshotId)
    {
        if (nodes.Count == 0) return;

        const double xPadding = 80;
        const double yPadding = 40;
        const double columnSpacing = 340;
        const double rowSpacing = 124;

        var currentPath = new HashSet<Guid>();
        for (var id = currentSnapshotId; id != Guid.Empty && currentPath.Add(id)
             && parentByNode.TryGetValue(id, out var parentId); id = parentId) { }

        var children = nodes.ToDictionary(n => n.SnapshotID, _ => new List<HistoryGraphNodeViewModel>());
        foreach (var (childId, parentId) in parentByNode)
            children[parentId].Add(nodeDict[childId]);

        static int CompareNodes(HistoryGraphNodeViewModel a, HistoryGraphNodeViewModel b)
        {
            int byTime = a.SavedAt.CompareTo(b.SavedAt);
            return byTime != 0 ? byTime : a.SnapshotID.CompareTo(b.SnapshotID);
        }

        foreach (var list in children.Values)
            list.Sort((a, b) =>
            {
                int byPath = currentPath.Contains(b.SnapshotID).CompareTo(currentPath.Contains(a.SnapshotID));
                return byPath != 0 ? byPath : CompareNodes(a, b);
            });

        var roots = nodes.Where(n => !parentByNode.ContainsKey(n.SnapshotID)).ToList();
        roots.Sort((a, b) =>
        {
            int byPath = currentPath.Contains(b.SnapshotID).CompareTo(currentPath.Contains(a.SnapshotID));
            return byPath != 0 ? byPath : CompareNodes(a, b);
        });

        int lane = 0;
        var positioned = new HashSet<Guid>();

        double LayoutNode(HistoryGraphNodeViewModel node, int column)
        {
            positioned.Add(node.SnapshotID);
            node.X = xPadding + column * columnSpacing;
            node.Depth = currentPath.Contains(node.SnapshotID) ? 0 : 1;

            var childList = children[node.SnapshotID];
            if (childList.Count == 0)
            {
                node.Y = yPadding + lane++ * rowSpacing;
                return node.Y;
            }

            // The current path (or the first stable child) stays on the parent's lane;
            // every additional branch receives its own non-overlapping subtree lanes.
            node.Y = LayoutNode(childList[0], column + 1);
            for (int i = 1; i < childList.Count; i++)
                LayoutNode(childList[i], column + 1);
            return node.Y;
        }

        foreach (var root in roots)
            LayoutNode(root, 0);

        // Corrupt cyclic data has no root. Keep those records visible without drawing
        // misleading cyclic links.
        foreach (var node in nodes.OrderBy(n => n.SavedAt).ThenBy(n => n.SnapshotID))
            if (!positioned.Contains(node.SnapshotID))
                LayoutNode(node, 0);
    }

    private static Dictionary<Guid, Guid> BuildParentMap(
        List<HistoryGraphNodeViewModel> nodes,
        Dictionary<Guid, HistoryGraphNodeViewModel> nodeDict)
    {
        var result = nodes
            .Where(n => n.PreviousSnapshotID != Guid.Empty
                        && n.PreviousSnapshotID != n.SnapshotID
                        && nodeDict.ContainsKey(n.PreviousSnapshotID))
            .ToDictionary(n => n.SnapshotID, n => n.PreviousSnapshotID);

        var cyclic = new HashSet<Guid>();
        foreach (var node in nodes)
        {
            var path = new List<Guid>();
            var indices = new Dictionary<Guid, int>();
            var id = node.SnapshotID;
            while (result.TryGetValue(id, out var parentId))
            {
                if (indices.TryGetValue(id, out int cycleStart))
                {
                    for (int i = cycleStart; i < path.Count; i++)
                        cyclic.Add(path[i]);
                    break;
                }
                indices[id] = path.Count;
                path.Add(id);
                id = parentId;
            }
        }

        foreach (var id in cyclic)
            result.Remove(id);

        foreach (var node in nodes)
        {
            node.NextSnapshotIDs = [];
            node.IsHead = true;
        }
        foreach (var (childId, parentId) in result)
        {
            nodeDict[parentId].NextSnapshotIDs.Add(childId);
            nodeDict[parentId].IsHead = false;
        }

        return result;
    }

    #endregion

    #region Node View Creation

    private View CreateNodeView(HistoryGraphNodeViewModel vm)
    {
        var frame = new Border
        {
            Stroke = Colors.Gray,
            StrokeThickness = 2,
            StrokeShape = new Microsoft.Maui.Controls.Shapes.RoundRectangle { CornerRadius = 8 },
            BackgroundColor = Color.FromArgb("#2d2d2d"),
            Padding = new Thickness(12, 8),
            HeightRequest = vm.NodeHeight,
            WidthRequest = vm.NodeWidth,
            ZIndex = 2
        };

        var titleLabel = new Label
        {
            Text = vm.DisplayLabel,
            TextColor = Colors.White,
            FontSize = 13,
            LineBreakMode = LineBreakMode.TailTruncation,
            MaxLines = 1,
            VerticalOptions = LayoutOptions.Center
        };

        var timeLabel = new Label
        {
            Text = vm.TimeDisplay,
            TextColor = Color.FromArgb("#999"),
            FontSize = 11,
            HorizontalOptions = LayoutOptions.End,
            VerticalOptions = LayoutOptions.Center
        };

        var bodyGrid = new Grid
        {
            ColumnDefinitions =
            {
                new ColumnDefinition { Width = GridLength.Star },
                new ColumnDefinition { Width = GridLength.Auto }
            }
        };
        bodyGrid.Add(titleLabel, 0, 0);
        bodyGrid.Add(timeLabel, 1, 0);

        frame.Content = bodyGrid;
        UpdateNodeBorderStyle(frame, vm);

        var tapGesture = new TapGestureRecognizer();
        var capturedVm = vm;
        tapGesture.Tapped += (_, _) => OnGraphNodeTapped(capturedVm);
        frame.GestureRecognizers.Add(tapGesture);

        ToolTipProperties.SetText(frame, $"{vm.DisplayLabel}\n{vm.TimeDisplay}");

        return frame;
    }

    private static void UpdateNodeBorderStyle(Border border, HistoryGraphNodeViewModel vm)
    {
        if (vm.IsCurrentSnapshot)
        {
            border.Stroke = Color.FromArgb("#4A9EFF");
            border.StrokeThickness = 3;
            border.BackgroundColor = Color.FromArgb("#1A304050");
        }
        else if (vm.IsHead)
        {
            border.Stroke = Color.FromArgb("#66BB6A");
            border.StrokeThickness = 2;
            border.BackgroundColor = Color.FromArgb("#2d2d2d");
        }
        else if (vm.Depth > 0)
        {
            border.Stroke = Color.FromArgb("#FF8C00");
            border.StrokeThickness = 1.5;
            border.BackgroundColor = Color.FromArgb("#2d2d2d");
        }
        else
        {
            border.Stroke = Color.FromArgb("#555555");
            border.StrokeThickness = 1.5;
            border.BackgroundColor = Color.FromArgb("#2d2d2d");
        }
    }

    #endregion

    #region Node Interaction

    private void OnGraphNodeTapped(HistoryGraphNodeViewModel vm)
    {
        _selectedSnapshotId = vm.SnapshotID;

        // Deselect previous graph node
        if (_selectedNode != null && _selectedNode.View is Border prevBorder)
        {
            prevBorder.StrokeThickness = _selectedNode.IsCurrentSnapshot ? 3 : 2;
            UpdateNodeBorderStyle(prevBorder, _selectedNode);
        }

        // Select new graph node
        _selectedNode = vm;
        if (vm.View is Border border)
        {
            border.Stroke = Color.FromArgb("#FFB74D");
            border.StrokeThickness = 3;
        }

        // Also deselect list view
        for (int i = 0; i < _rowDrawables.Count; i++)
        {
            _rowDrawables[i].IsSelected = false;
            if (i < _rowGraphicsViews.Count)
                _rowGraphicsViews[i].Invalidate();
        }

        _provider?.OnNodeSelected(
            new HistoryGraphNode
            {
                SnapshotID = vm.SnapshotID,
                PreviousSnapshotID = vm.PreviousSnapshotID,
                NextSnapshotIDs = vm.NextSnapshotIDs,
                SavedAt = vm.SavedAt,
                ChangeReason = vm.ChangeReason,
                ChangedByUserDisplayName = vm.ChangedByUserDisplayName,
                ChangedByUser = vm.ChangedByUser,
                IsCurrentSnapshot = vm.IsCurrentSnapshot,
                IsHead = vm.IsHead,
            });

        ShowDetailsPanel(vm);
    }

    private void ShowDetailsPanel(HistoryGraphNodeViewModel vm)
    {
        DetailsPanel.IsVisible = true;
        DetailsSavedAtLabel.Text = vm.SavedAt == DateTime.MinValue
            ? "Unknown time"
            : vm.SavedAt.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss");
        DetailsAuthorLabel.Text = $"By: {vm.ChangedByUserDisplayName}";
        DetailsChangeReasonLabel.Text = vm.ChangeReason;
        DetailsRestoreButton.IsEnabled = !vm.IsCurrentSnapshot;
        DetailsRestoreButton.Text = vm.IsCurrentSnapshot ? "(Current)" : Localized._Apply;

        // Append provider extension content
        ClearDetailsPanelExtension();
        var extension = _provider?.GetDetailsPanelExtension(
            new HistoryGraphNode
            {
                SnapshotID = vm.SnapshotID,
                PreviousSnapshotID = vm.PreviousSnapshotID,
                NextSnapshotIDs = vm.NextSnapshotIDs,
                SavedAt = vm.SavedAt,
                ChangeReason = vm.ChangeReason,
                ChangedByUserDisplayName = vm.ChangedByUserDisplayName,
                ChangedByUser = vm.ChangedByUser,
                IsCurrentSnapshot = vm.IsCurrentSnapshot,
                IsHead = vm.IsHead,
            });
        if (extension is not null)
        {
            DetailsExtensionContainer.Children.Add(extension);
        }
    }

    private void ShowDetailsPanel(HistoryGraphNode node)
    {
        DetailsPanel.IsVisible = true;
        DetailsSavedAtLabel.Text = node.SavedAt == DateTime.MinValue
            ? "Unknown time"
            : node.SavedAt.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss");
        DetailsAuthorLabel.Text = $"By: {node.ChangedByUserDisplayName}";
        DetailsChangeReasonLabel.Text = node.ChangeReason;
        DetailsRestoreButton.IsEnabled = !node.IsCurrentSnapshot;
        DetailsRestoreButton.Text = node.IsCurrentSnapshot ? "(Current)" : Localized._Apply;

        ClearDetailsPanelExtension();
        var extension = _provider?.GetDetailsPanelExtension(node);
        if (extension is not null)
        {
            DetailsExtensionContainer.Children.Add(extension);
        }
    }

    private void ClearDetailsPanelExtension()
    {
        DetailsExtensionContainer.Children.Clear();
    }

    private async void OnRestoreClicked(object? sender, EventArgs e)
    {
        if (_selectedSnapshotId == Guid.Empty || _provider is null) return;

        bool success = await _provider.ApplySnapshotAsync(_selectedSnapshotId);
        if (success)
        {
            RefreshSelection();
        }
    }

    #endregion

    #region View Mode Toggle

    private void OnToggleViewClicked(object? sender, EventArgs e)
    {
        _viewMode = _viewMode == HistoryViewMode.Graph
            ? HistoryViewMode.List
            : HistoryViewMode.Graph;
        UpdateViewMode();
    }

    private void UpdateViewMode()
    {
        bool isEmpty = _nodeViewModels.Count == 0 && _currentNodes.Count == 0;
        bool isGraph = _viewMode == HistoryViewMode.Graph;

        GraphViewContainer.IsVisible = isGraph && !isEmpty;
        ListViewContainer.IsVisible = !isGraph && !isEmpty;

        ToggleViewButton.Text = isGraph ? "☰" : "⬡";

        if (isGraph && !isEmpty)
        {
            ConnectionsLayer.Invalidate();
        }
    }

    private void OnProviderCurrentSnapshotChanged(object? sender, EventArgs e)
    {
        MainThread.BeginInvokeOnMainThread(() => RefreshSelection());
    }

    #endregion

    #region Pan and Zoom

    private void OnCanvasPanUpdated(object? sender, PanUpdatedEventArgs e)
    {
        switch (e.StatusType)
        {
            case GestureStatus.Started:
                _panStartX = NodesContainer.TranslationX;
                _panStartY = NodesContainer.TranslationY;
                break;
            case GestureStatus.Running:
                NodesContainer.TranslationX = _panStartX + e.TotalX;
                NodesContainer.TranslationY = _panStartY + e.TotalY;
                SyncDrawableTransform();
                ConnectionsLayer.Invalidate();
                break;
            case GestureStatus.Completed:
            case GestureStatus.Canceled:
                break;
        }
    }

    private void OnCanvasPinchUpdated(object? sender, PinchGestureUpdatedEventArgs e)
    {
        if (e.Status == GestureStatus.Started)
        {
            _startScale = NodesContainer.Scale;
        }
        else if (e.Status == GestureStatus.Running)
        {
            double targetScale = Math.Clamp(_startScale * e.Scale, MinScale, MaxScale);

            if (sender is View v)
            {
                double focalX = e.ScaleOrigin.X * v.Width;
                double focalY = e.ScaleOrigin.Y * v.Height;
                ApplyZoom(targetScale, focalX, focalY);
            }
        }
    }

    private void ApplyZoom(double targetScale, double focalX, double focalY)
    {
        double oldScale = NodesContainer.Scale;
        double oldTransX = NodesContainer.TranslationX;
        double oldTransY = NodesContainer.TranslationY;

        if (oldScale <= 0) oldScale = 0.1;

        double contentX = (focalX - oldTransX) / oldScale;
        double contentY = (focalY - oldTransY) / oldScale;

        double newTransX = focalX - (contentX * targetScale);
        double newTransY = focalY - (contentY * targetScale);

        NodesContainer.Scale = targetScale;
        NodesContainer.TranslationX = newTransX;
        NodesContainer.TranslationY = newTransY;

        SyncDrawableTransform();
        ConnectionsLayer.Invalidate();
    }

    private void SyncDrawableTransform()
    {
        _connectionsDrawable.PanX = NodesContainer.TranslationX;
        _connectionsDrawable.PanY = NodesContainer.TranslationY;
        _connectionsDrawable.Scale = NodesContainer.Scale;
    }

#if WINDOWS
    private void OnWindowsPointerWheelChanged(object sender, Microsoft.UI.Xaml.Input.PointerRoutedEventArgs e)
    {
        var ctrlState = Microsoft.UI.Input.InputKeyboardSource.GetKeyStateForCurrentThread(
            Windows.System.VirtualKey.Control);
        bool ctrlPressed = (ctrlState & Windows.UI.Core.CoreVirtualKeyStates.Down)
                          == Windows.UI.Core.CoreVirtualKeyStates.Down;

        if (!ctrlPressed) return;

        var properties = e.GetCurrentPoint(null).Properties;
        int delta = properties.MouseWheelDelta;

        double scaleFactor = delta > 0 ? 1.15 : 1.0 / 1.15;
        double targetScale = Math.Clamp(NodesContainer.Scale * scaleFactor, MinScale, MaxScale);

        // Use center of canvas as focal point for Ctrl+Scroll zoom
        double focalX = GraphViewContainer.Width / 2;
        double focalY = GraphViewContainer.Height / 2;

        ApplyZoom(targetScale, focalX, focalY);

        e.Handled = true;
    }
#endif

    #endregion

    #region Zoom Controls

    private void OnZoomIn(object? sender, EventArgs e)
    {
        double targetScale = Math.Clamp(NodesContainer.Scale * 1.25, MinScale, MaxScale);
        ApplyZoom(targetScale, GraphViewContainer.Width / 2, GraphViewContainer.Height / 2);
    }

    private void OnZoomOut(object? sender, EventArgs e)
    {
        double targetScale = Math.Clamp(NodesContainer.Scale / 1.25, MinScale, MaxScale);
        ApplyZoom(targetScale, GraphViewContainer.Width / 2, GraphViewContainer.Height / 2);
    }

    private void OnResetView(object? sender, EventArgs e)
    {
        FocusSnapshot(_nodeViewModels.FirstOrDefault(n => n.IsCurrentSnapshot) ?? _nodeViewModels.FirstOrDefault());
    }

    private void OnFitAll(object? sender, EventArgs e)
    {
        if (_nodeViewModels.Count == 0) return;

        double minX = _nodeViewModels.Min(n => n.X) - 50;
        double minY = _nodeViewModels.Min(n => n.Y) - 30;
        double maxX = _nodeViewModels.Max(n => n.X + n.NodeWidth) + 50;
        double maxY = _nodeViewModels.Max(n => n.Y + n.NodeHeight) + 30;

        double contentW = maxX - minX;
        double contentH = maxY - minY;

        if (contentW <= 0 || contentH <= 0) return;

        double viewW = GraphViewContainer.Width > 0 ? GraphViewContainer.Width : 800;
        double viewH = GraphViewContainer.Height > 0 ? GraphViewContainer.Height : 400;

        double scaleX = viewW / contentW;
        double scaleY = viewH / contentH;
        double scale = Math.Min(scaleX, scaleY) * 0.9;
        scale = Math.Clamp(scale, MinScale, MaxScale);

        double transX = -minX * scale + (viewW - contentW * scale) / 2;
        double transY = -minY * scale + (viewH - contentH * scale) / 2;

        NodesContainer.Scale = scale;
        NodesContainer.TranslationX = transX;
        NodesContainer.TranslationY = transY;
        NodesContainer.AnchorX = 0;
        NodesContainer.AnchorY = 0;

        SyncDrawableTransform();
        ConnectionsLayer.Invalidate();
    }

    private void OnGraphViewSizeChanged(object? sender, EventArgs e)
    {
        if (_pendingInitialFocus)
            TryApplyInitialView();
    }

    private void TryApplyInitialView()
    {
        if (!_pendingInitialFocus || GraphViewContainer.Width <= 0 || GraphViewContainer.Height <= 0)
            return;

        _pendingInitialFocus = false;
        FocusSnapshot(_nodeViewModels.FirstOrDefault(n => n.IsCurrentSnapshot) ?? _nodeViewModels.FirstOrDefault());
    }

    private void FocusSnapshot(HistoryGraphNodeViewModel? node)
    {
        if (node is null) return;

        const double scale = 1.0;
        double viewW = GraphViewContainer.Width > 0 ? GraphViewContainer.Width : 800;
        double viewH = GraphViewContainer.Height > 0 ? GraphViewContainer.Height : 400;
        NodesContainer.Scale = scale;
        NodesContainer.TranslationX = viewW / 2 - (node.X + node.NodeWidth / 2) * scale;
        NodesContainer.TranslationY = viewH / 2 - (node.Y + node.NodeHeight / 2) * scale;
        SyncDrawableTransform();
        ConnectionsLayer.Invalidate();
    }

    #endregion
}

/// <summary>View mode for the history panel.</summary>
public enum HistoryViewMode
{
    /// <summary>DAG-style graph view with nodes and curved connections.</summary>
    Graph,
    /// <summary>Compact vertical list view with timeline-style dots.</summary>
    List
}
