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

        public double NodeWidth => 180;
        public double NodeHeight => 56;

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

        static readonly Color MainChainColor = Color.FromArgb("#4A9EFF");
        static readonly Color BranchColor = Color.FromArgb("#666666");
        static readonly Color CurrentPathColor = Color.FromArgb("#66BB6A");

        public void Draw(ICanvas canvas, RectF dirtyRect)
        {
            canvas.SaveState();
            canvas.StrokeLineCap = LineCap.Round;
            canvas.Antialias = true;

            foreach (var (from, to) in Connections)
            {
                bool isCurrentPath = from.IsCurrentSnapshot || to.IsCurrentSnapshot;
                bool isMainChain = from.Depth == 0 && to.Depth == 0;

                canvas.StrokeColor = isCurrentPath ? CurrentPathColor
                                    : isMainChain ? MainChainColor
                                    : BranchColor;
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

    private DraftPage? _page;
    private readonly List<HistoryGraphNodeViewModel> _nodeViewModels = new();
    private HistoryGraphConnectionsDrawable _connectionsDrawable = null!;
    private HistoryGraphNodeViewModel? _selectedNode;
    private Guid _selectedSnapshotId = Guid.Empty;
    private Guid _currentSnapshotId = Guid.Empty;

    // Canvas state
    private double _panStartX, _panStartY;
    private double _startScale = 1.0;
    private const double MinScale = 0.1;
    private const double MaxScale = 5.0;
    private const double CanvasWidth = 5000;
    private const double CanvasHeight = 3000;

    #endregion

    #region Constructor

    public HistoryGraphView()
    {
        InitializeComponent();
        _connectionsDrawable = new HistoryGraphConnectionsDrawable();
        ConnectionsLayer.Drawable = _connectionsDrawable;
    }

    public HistoryGraphView(DraftPage page) : this()
    {
        _page = page;
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

        if (nodes.Count == 0)
        {
            EmptyHint.IsVisible = true;
            InfoLabel.Text = "";
            ConnectionsLayer.Invalidate();
            return;
        }

        EmptyHint.IsVisible = false;

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

        // Compute 2D layout
        ComputeLayout(_nodeViewModels, nodeDict);

        // Create MAUI views
        foreach (var vm in _nodeViewModels)
        {
            var view = CreateNodeView(vm);
            vm.View = view;
            NodesContainer.Children.Add(view);
            AbsoluteLayout.SetLayoutBounds(view, new Rect(vm.X, vm.Y, vm.NodeWidth, vm.NodeHeight));
        }

        // Build connections
        foreach (var edge in edges)
        {
            if (nodeDict.TryGetValue(edge.FromSnapshotID, out var fromVm)
                && nodeDict.TryGetValue(edge.ToSnapshotID, out var toVm))
            {
                _connectionsDrawable.Connections.Add((fromVm, toVm));
            }
        }

        InfoLabel.Text = $"{nodes.Count} snapshots";

        ConnectionsLayer.Invalidate();

        // Fit all after layout settles
        Dispatcher.Dispatch(() => OnFitAll(null, EventArgs.Empty));
    }

    public void RefreshSelection()
    {
        if (_page is null) return;
        _currentSnapshotId = _page.CurrentSnapshotID;
        foreach (var vm in _nodeViewModels)
        {
            vm.IsCurrentSnapshot = vm.SnapshotID == _currentSnapshotId;
            if (vm.View is Border border)
            {
                UpdateNodeBorderStyle(border, vm);
            }
        }
        ConnectionsLayer.Invalidate();
    }

    #endregion

    #region Layout Algorithm

    private void ComputeLayout(
        List<HistoryGraphNodeViewModel> nodes,
        Dictionary<Guid, HistoryGraphNodeViewModel> nodeDict)
    {
        if (nodes.Count == 0) return;

        // Identify main chain (follow PrimaryNext from root to last head)
        var mainChain = new HashSet<Guid>();
        var chainNodes = new List<Guid>();

        // Find root (node with no parent in the graph)
        var rootCandidates = nodes.Where(n =>
            n.PreviousSnapshotID == Guid.Empty
            || !nodeDict.ContainsKey(n.PreviousSnapshotID)).ToList();

        if (rootCandidates.Count == 0) return;

        // Build the main chain from root following first next
        var cursor = rootCandidates[0].SnapshotID;
        while (cursor != Guid.Empty && nodeDict.TryGetValue(cursor, out var node) && mainChain.Add(cursor))
        {
            chainNodes.Add(cursor);
            node.Depth = 0;
            node.IsHead = node.NextSnapshotIDs.Count == 0;
            var nextId = node.NextSnapshotIDs.FirstOrDefault();
            cursor = nextId != Guid.Empty ? nextId : Guid.Empty;
        }

        // Assign time-based X positions
        var times = nodes.Where(n => n.SavedAt != DateTime.MinValue).Select(n => n.SavedAt.Ticks).ToList();
        double minTime = times.Count > 0 ? times.Min() : DateTime.MinValue.Ticks;
        double maxTime = times.Count > 0 ? times.Max() : DateTime.MaxValue.Ticks;
        double timeRange = Math.Max(maxTime - minTime, 1);
        const double XPadding = 80;
        double usableWidth = CanvasWidth - XPadding * 2;

        // Assign positions for main chain
        for (int i = 0; i < chainNodes.Count; i++)
        {
            if (nodeDict.TryGetValue(chainNodes[i], out var vm))
            {
                double t = vm.SavedAt == DateTime.MinValue ? minTime : vm.SavedAt.Ticks;
                vm.X = XPadding + ((t - minTime) / timeRange) * usableWidth;
                vm.Y = 40;
                vm.Depth = 0;
            }
        }

        // Assign positions for branch nodes
        var remaining = nodes.Where(n => !mainChain.Contains(n.SnapshotID)).ToList();
        var branchDepthCounter = new Dictionary<Guid, int>(); // Previous -> next branch index

        foreach (var node in remaining)
        {
            double t = node.SavedAt == DateTime.MinValue ? minTime : node.SavedAt.Ticks;
            node.X = XPadding + ((t - minTime) / timeRange) * usableWidth;

            // Determine depth from parent
            if (node.PreviousSnapshotID != Guid.Empty && nodeDict.ContainsKey(node.PreviousSnapshotID))
            {
                var parent = nodeDict[node.PreviousSnapshotID];
                int parentDepth = parent.Depth;
                if (!branchDepthCounter.ContainsKey(node.PreviousSnapshotID))
                    branchDepthCounter[node.PreviousSnapshotID] = 1;
                int branchIdx = branchDepthCounter[node.PreviousSnapshotID]++;
                node.Depth = parentDepth + branchIdx;
                node.Y = 40 + node.Depth * 130;
            }
            else
            {
                // Orphan node
                node.Depth = 1;
                node.Y = 40 + 130;
            }
        }

        // Adjust Y spacing for main chain to avoid branch overlap
        int maxDepth = nodes.Max(n => n.Depth);
        double totalHeight = 40 + (maxDepth + 1) * 130 + 80;

        // Ensure canvas dimensions are set
        foreach (var n in nodes)
        {
            if (n.X < 0) n.X = XPadding;
            if (n.Y < 0) n.Y = 40;
        }
    }

    #endregion

    #region Node View Creation

    private View CreateNodeView(HistoryGraphNodeViewModel vm)
    {
        var container = new VerticalStackLayout
        {
            Spacing = 0,
            HorizontalOptions = LayoutOptions.Start,
            VerticalOptions = LayoutOptions.Start,
            InputTransparent = false
        };

        var frame = new Border
        {
            Stroke = Colors.Gray,
            StrokeThickness = 2,
            StrokeShape = new Microsoft.Maui.Controls.Shapes.RoundRectangle { CornerRadius = 6 },
            BackgroundColor = Color.FromArgb("#2d2d2d"),
            Padding = new Thickness(8, 4),
            HeightRequest = vm.NodeHeight,
            MinimumWidthRequest = vm.NodeWidth,
            ZIndex = 2
        };

        var titleLabel = new Label
        {
            Text = vm.DisplayLabel,
            TextColor = Colors.White,
            FontSize = 12,
            LineBreakMode = LineBreakMode.TailTruncation,
            MaxLines = 1,
            VerticalOptions = LayoutOptions.Center
        };

        var timeLabel = new Label
        {
            Text = vm.TimeDisplay,
            TextColor = Color.FromArgb("#999"),
            FontSize = 10,
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
        container.Add(frame);

        UpdateNodeBorderStyle(frame, vm);

        var tapGesture = new TapGestureRecognizer();
        var capturedVm = vm;
        tapGesture.Tapped += (_, _) => OnNodeTapped(capturedVm);
        frame.GestureRecognizers.Add(tapGesture);

        ToolTipProperties.SetText(frame, $"{vm.DisplayLabel}\n{vm.TimeDisplay}");

        return container;
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

    private void OnNodeTapped(HistoryGraphNodeViewModel vm)
    {
        // Deselect previous
        if (_selectedNode != null && _selectedNode.View is Border prevBorder)
        {
            prevBorder.StrokeThickness = _selectedNode.IsCurrentSnapshot ? 3 : 2;
            UpdateNodeBorderStyle(prevBorder, _selectedNode);
        }

        // Select new
        _selectedNode = vm;
        _selectedSnapshotId = vm.SnapshotID;

        if (vm.View is Border border)
        {
            border.Stroke = Color.FromArgb("#FFB74D");
            border.StrokeThickness = 3;
        }

        // Update details panel
        DetailsPanel.IsVisible = true;
        DetailsSavedAtLabel.Text = vm.SavedAt == DateTime.MinValue
            ? "Unknown time"
            : vm.SavedAt.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss");
        DetailsAuthorLabel.Text = $"By: {vm.ChangedByUserDisplayName}";
        DetailsChangeReasonLabel.Text = vm.ChangeReason;
        DetailsRestoreButton.IsEnabled = !vm.IsCurrentSnapshot;
        DetailsRestoreButton.Text = vm.IsCurrentSnapshot ? "(Current)" : Localized._Apply;
    }

    private async void OnRestoreClicked(object? sender, EventArgs e)
    {
        if (_selectedSnapshotId == Guid.Empty || _page is null) return;

        try
        {
            _page.SetStateBusy();
            _page.ApplySlot(_selectedSnapshotId);
            RefreshSelection();
        }
        catch (Exception ex)
        {
            _page.SetStateFail();
            _page.SetStatusText($"Restore failed: {ex.Message}");
        }
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
        double focalX = Width / 2;
        double focalY = Height / 2;

        ApplyZoom(targetScale, focalX, focalY);

        e.Handled = true;
    }
#endif

    #endregion

    #region Zoom Controls

    private void OnZoomIn(object? sender, EventArgs e)
    {
        double targetScale = Math.Clamp(NodesContainer.Scale * 1.25, MinScale, MaxScale);
        ApplyZoom(targetScale, Width / 2, Height / 2);
    }

    private void OnZoomOut(object? sender, EventArgs e)
    {
        double targetScale = Math.Clamp(NodesContainer.Scale / 1.25, MinScale, MaxScale);
        ApplyZoom(targetScale, Width / 2, Height / 2);
    }

    private void OnResetView(object? sender, EventArgs e)
    {
        NodesContainer.Scale = 1.0;
        NodesContainer.TranslationX = 0;
        NodesContainer.TranslationY = 0;
        NodesContainer.AnchorX = 0;
        NodesContainer.AnchorY = 0;
        SyncDrawableTransform();
        ConnectionsLayer.Invalidate();
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

        double viewW = Width > 0 ? Width : 800;
        double viewH = Height > 0 ? Height : 400;

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

    #endregion
}
