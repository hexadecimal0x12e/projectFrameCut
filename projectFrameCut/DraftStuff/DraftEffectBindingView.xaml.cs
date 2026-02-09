using CommunityToolkit.Maui.Views;
using Microsoft.Maui.Controls.Shapes;
using Microsoft.Maui.Layouts;
using projectFrameCut.ApplicationAPIBase.Effect;
using projectFrameCut.ApplicationAPIBase.Views.PropertyPanelBuilders;
using projectFrameCut.Render.Plugin;
using projectFrameCut.Render.RenderAPIBase.EffectAndMixture;
using projectFrameCut.Services;
using projectFrameCut.Shared;
using System.Diagnostics;
using static LocalizedResources.SimpleLocalizerBaseGeneratedHelper_PropertyPanel;
using Rect = Microsoft.Maui.Graphics.Rect;

namespace projectFrameCut.DraftStuff;

public enum NodeKind { Effect, Input, Output }

public partial class DraftEffectBindingView : ContentView
{
    private ClipElementUI? _clip;
    private DraftPage? _page;
    private Dictionary<Guid, NodeViewModel> _nodes = new();
    private ConnectionsDrawable _drawable;
    private NodeViewModel? _selectedNode;
    private NodePort? _pendingConnectionSource;

    private NodeViewModel? _inputNode;
    private NodeViewModel? _outputNode;

    private double _panStartX, _panStartY;
    private bool _isDraggingNodeOrPort;
    private double _startScale = 1.0;


    public DraftEffectBindingView()
    {
        InitializeComponent();
        _drawable = new ConnectionsDrawable(_nodes);
        ConnectionsLayer.Drawable = _drawable;

        ZoomInButton.Clicked += OnZoomIn;
        ZoomOutButton.Clicked += OnZoomOut;
        ResetButton.Clicked += OnReset;

        InfoLabel.Text = PPLocalizedResources.EffectBindView_Hint;
        
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

#if WINDOWS
    private void OnWindowsPointerWheelChanged(object sender, Microsoft.UI.Xaml.Input.PointerRoutedEventArgs e)
    {
        var properties = e.GetCurrentPoint(null).Properties;
        if (properties.MouseWheelDelta != 0)
        {
            // Check for Ctrl key state
            var state = Microsoft.UI.Input.InputKeyboardSource.GetKeyStateForCurrentThread(Windows.System.VirtualKey.Control);
            if ((state & Windows.UI.Core.CoreVirtualKeyStates.Down) == Windows.UI.Core.CoreVirtualKeyStates.Down)
            {
                int delta = properties.MouseWheelDelta;
                // Standard mouse wheel delta is 120
                double zoomFactor = delta > 0 ? 1.1 : 0.9;

                double currentScale = NodesContainer.Scale;
                double targetScale = Math.Clamp(currentScale * zoomFactor, 0.2, 5.0);

                // Get mouse position relative to the view
                var point = e.GetCurrentPoint((Microsoft.UI.Xaml.UIElement)sender).Position;

                ApplyZoom(targetScale, point.X, point.Y);

                e.Handled = true;
            }
        }
    }
#endif

    private void OnZoomIn(object? sender, EventArgs e)
    {
        double targetScale = Math.Min(NodesContainer.Scale * 1.2, 5.0);
        // Zoom to center of the view
        ApplyZoom(targetScale, this.Width / 2, this.Height / 2);
    }

    private void OnZoomOut(object? sender, EventArgs e)
    {
        double targetScale = Math.Max(NodesContainer.Scale / 1.2, 0.2);
        // Zoom to center of the view
        ApplyZoom(targetScale, this.Width / 2, this.Height / 2);
    }

    private void OnCanvasPinchUpdated(object sender, PinchGestureUpdatedEventArgs e)
    {
        if (e.Status == GestureStatus.Started)
        {
            _startScale = NodesContainer.Scale;
        }
        else if (e.Status == GestureStatus.Running)
        {
            double targetScale = Math.Clamp(_startScale * e.Scale, 0.2, 5.0);

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

        // Calculate the content point under the focal point relative to standard 0,0 anchor
        // Screen = Content * Scale + Trans
        // Content = (Screen - Trans) / Scale

        double contentX = (focalX - oldTransX) / oldScale;
        double contentY = (focalY - oldTransY) / oldScale;

        // NewTrans = Screen - Content * NewScale
        double newTransX = focalX - (contentX * targetScale);
        double newTransY = focalY - (contentY * targetScale);

        NodesContainer.Scale = targetScale;
        NodesContainer.TranslationX = newTransX;
        NodesContainer.TranslationY = newTransY;

        _drawable.PanX = newTransX;
        _drawable.PanY = newTransY;
        UpdateDrawableScale();
    }

    private void OnReset(object? sender, EventArgs e)
    {
        NodesContainer.AnchorX = 0;
        NodesContainer.AnchorY = 0;

        NodesContainer.Scale = 1.0;
        NodesContainer.TranslationX = 0;
        NodesContainer.TranslationY = 0;

        _drawable.PanX = 0;
        _drawable.PanY = 0;
        UpdateDrawableScale();
        ConnectionsLayer.Invalidate();
    }

    private void UpdateDrawableScale()
    {
        // Ensure Anchors are 0,0 so our math (x * scale + pan) holds true
        NodesContainer.AnchorX = 0;
        NodesContainer.AnchorY = 0;

        _drawable.Scale = NodesContainer.Scale;
        ConnectionsLayer.Invalidate();
    }

    public void LoadClip(ClipElementUI clip, DraftPage? page = null)
    {
        _clip = clip;
        _page = page;
        UpdateAddEffectsPanel();
        _nodes.Clear();
        NodesContainer.Children.Clear();
        PropertiesPanel.Children.Clear();

        // Reset View Transform
        NodesContainer.Scale = 1.0;
        NodesContainer.TranslationX = 0;
        NodesContainer.TranslationY = 0;
        NodesContainer.AnchorX = 0;
        NodesContainer.AnchorY = 0;
        _drawable.PanX = 0;
        _drawable.PanY = 0;
        _drawable.Scale = 1.0;

        // Create System Nodes
        _inputNode = new NodeViewModel { Kind = NodeKind.Input, X = 50, Y = 150, Id = IEffectBundle.InputAnchorGUID, Bundle = null };
        AddNode(_inputNode);

        if (_clip.EffectBundles != null)
        {
            // The logic to load factories and instantiate IEffectBundle has been removed.
            // We iterate bundleData directly to create visual nodes.

            foreach (var bundle in _clip.EffectBundles.Values)
            {
                // Placeholder Node Creation
                // We assume 1 input port "Input" because we cannot inspect the real effect logic anymore.
                var node = new NodeViewModel
                {
                    Id = bundle.Id,
                    Kind = NodeKind.Effect,
                    Bundle = bundle,
                    InputPortNames = bundle.InputAnchorsDisplayName ?? [string.IsNullOrWhiteSpace(bundle.InputAnchorDisplayName) ? "Frame" : bundle.InputAnchorDisplayName],
                    DisplayName = bundle.Name
                };

                if (bundle.Parameters.TryGetValue("__DraftEffectBindingView_InteractiveEditorX__", out var xObj) && bundle.Parameters.TryGetValue("__DraftEffectBindingView_InteractiveEditorY__", out var yObj) && xObj is double x && yObj is double y)
                {
                    node.X = x;
                    node.Y = y;
                }
                else
                {
                    node.X = 250 + (_nodes.Count * 50);
                    node.Y = 150 + (_nodes.Count * 20);
                }

                AddNode(node);
            }
        }

        // Create Output Node
        // Position it far right
        double maxX = _nodes.Max(kvp => kvp.Value.X);
        _outputNode = new NodeViewModel { Kind = NodeKind.Output, X = Math.Max(maxX + 200, 600), Y = 150, Id = IEffectBundle.OutputAnchorGUID, Bundle = null };
        AddNode(_outputNode);

        RebuildConnections();
        ConnectionsLayer.Invalidate();
    }

    private void AddNode(NodeViewModel node)
    {
        var view = CreateNodeView(node);
        node.View = view;
        NodesContainer.Add(view);
        AbsoluteLayout.SetLayoutBounds(view, new Rect(node.X, node.Y, -1, 80));
        _nodes.Add(node.Id, node);
    }

    private View CreateNodeView(NodeViewModel node)
    {
        ArgumentNullException.ThrowIfNull(node);
        var frame = new Border
        {
            Stroke = node.Kind == NodeKind.Effect ? Colors.Gray : Colors.White,
            StrokeThickness = node.Kind == NodeKind.Effect ? 2 : 4,
            StrokeShape = new RoundRectangle { CornerRadius = 5 },
            BackgroundColor = Color.FromArgb("#2d2d2d"),
            Padding = 5,
            HeightRequest = 80,
            MinimumWidthRequest = 150
        };

        var title = node.Kind switch
        {
            NodeKind.Input => PPLocalizedResources.EffectBind_SourcePicture,
            NodeKind.Output => PPLocalizedResources.EffectBind_FinalResult,
            _ => node?.DisplayName ?? node?.Bundle?.TypeName ?? "?"
        };

        var label = new Label
        {
            Text = title,
            TextColor = Colors.White,
            HorizontalOptions = LayoutOptions.Center,
            VerticalOptions = LayoutOptions.Center,
            FontSize = 14,
            FontAttributes = node?.Kind == NodeKind.Effect ? FontAttributes.None : FontAttributes.Bold,
            LineBreakMode = LineBreakMode.NoWrap
        };

        ToolTipProperties.SetText(label, title);

        // Ports
        View inputPortView;

        if (node.InputPortNames != null && node.InputPortNames.Length > 0)
        {
            var stack = new VerticalStackLayout { Spacing = 5, VerticalOptions = LayoutOptions.Center };
            for (int i = 0; i < node.InputPortNames.Length; i++)
            {
                var portName = node.InputPortNames[i];
                var portBox = new BoxView { Color = Colors.Green, WidthRequest = 15, HeightRequest = 15 };

                int portIndex = i; // Capture for lambda

                var pTap = new TapGestureRecognizer();
                pTap.Tapped += (s, e) => OnPortClicked(node, true, portIndex);
                portBox.GestureRecognizers.Add(pTap);

                var pPan = new PanGestureRecognizer();
                pPan.PanUpdated += (s, e) => OnPortPan(node, e, true, portIndex);
                portBox.GestureRecognizers.Add(pPan);

                var row = new HorizontalStackLayout { Spacing = 3, VerticalOptions = LayoutOptions.Center };
                row.Add(portBox);
                if (!string.IsNullOrEmpty(portName))
                {
                    var l = new Label
                    {
                        Text = portName,
                        TextColor = Colors.LightGray,
                        FontSize = 10,
                        VerticalOptions = LayoutOptions.Center
                    };
                    ToolTipProperties.SetText(l, portName);
                    row.Add(l);
                }

                stack.Add(row);
            }
            inputPortView = stack;
        }
        else
        {
            var box = new BoxView { Color = Colors.Green, WidthRequest = 15, HeightRequest = 15, VerticalOptions = LayoutOptions.Center };
            // Original single port logic
            var inputTap = new TapGestureRecognizer();
            inputTap.Tapped += (s, e) => OnPortClicked(node, true);
            box.GestureRecognizers.Add(inputTap);

            var inputPan = new PanGestureRecognizer();
            inputPan.PanUpdated += (s, e) => OnPortPan(node, e, true);
            box.GestureRecognizers.Add(inputPan);
            ToolTipProperties.SetText(box, PPLocalizedResources.EffectBind_InputAnchor);

            inputPortView = box;
        }

        var outputPort = new BoxView { Color = Colors.Red, WidthRequest = 15, HeightRequest = 15, VerticalOptions = LayoutOptions.Center };
        ToolTipProperties.SetText(outputPort, node?.Bundle?.OutputAnchorDisplayName ?? PPLocalizedResources.EffectBind_OutputAnchor);

        // Handle visibility for System Nodes
        if (node.Kind == NodeKind.Input) inputPortView.IsVisible = false; // Hide Input on Input Node
        if (node.Kind == NodeKind.Output) outputPort.Color = Colors.Transparent; // Hide Output on Output Node

        // Interaction
        var tap = new TapGestureRecognizer();
        tap.Tapped += (s, e) => SelectNode(node);
        tap.NumberOfTapsRequired = 2;
        tap.Tapped += (s, e) => DisconnectNode(node);

        var singleTap = new TapGestureRecognizer();
        singleTap.Tapped += (s, e) => SelectNode(node);

        var bodyActionContainer = new Grid();

        bodyActionContainer.GestureRecognizers.Add(singleTap);
        if (node.Kind == NodeKind.Effect) bodyActionContainer.GestureRecognizers.Add(tap); // Only disconnect effects

        var pan = new PanGestureRecognizer();
        pan.PanUpdated += (s, e) => OnNodePan(node, e);
        bodyActionContainer.GestureRecognizers.Add(pan);

        // Output Port Interaction (Single Output mostly)
        var outputTap = new TapGestureRecognizer();
        outputTap.Tapped += (s, e) => OnPortClicked(node, false);
        outputPort.GestureRecognizers.Add(outputTap);

        var outputPan = new PanGestureRecognizer();
        outputPan.PanUpdated += (s, e) => OnPortPan(node, e, false);
        outputPort.GestureRecognizers.Add(outputPan);

        // Layout
        var layout = new Grid { ColumnDefinitions = new ColumnDefinitionCollection { new ColumnDefinition { Width = GridLength.Auto }, new ColumnDefinition { Width = GridLength.Star }, new ColumnDefinition { Width = 20 } } };

        bodyActionContainer.Add(label);

        layout.Add(inputPortView, 0, 0);
        layout.Add(bodyActionContainer, 1, 0);
        layout.Add(outputPort, 2, 0);

        if (node.OutputAnchorID == IEffectBundle.NoConnectionGUID && node.InputAnchorID != IEffectBundle.NoConnectionGUID)
        {
            frame.Opacity = 0.8;
        }

        frame.SizeChanged += (s, e) => ConnectionsLayer.Invalidate();

        frame.Content = layout;
        return frame;
    }

    private void OnNodePan(NodeViewModel node, PanUpdatedEventArgs e)
    {
        switch (e.StatusType)
        {
            case GestureStatus.Started:
                _isDraggingNodeOrPort = true;
                node.DragStartX = node.X;
                node.DragStartY = node.Y;
                ConnectionsLayer.Invalidate(); // Redraw connections while dragging
                break;
            case GestureStatus.Running:
                // Adjust movement delta by scale factor to keep mouse sync
                double scale = NodesContainer.Scale;
                if (scale <= 0) scale = 0.1;

                node.X = node.DragStartX + (e.TotalX / scale);
                node.Y = node.DragStartY + (e.TotalY / scale);

                AbsoluteLayout.SetLayoutBounds(node.View, new Rect(node.X, node.Y, -1, 80));
                ConnectionsLayer.Invalidate();
                break;
            case GestureStatus.Completed:
            case GestureStatus.Canceled:
                _isDraggingNodeOrPort = false;
                node?.Bundle?.Parameters?["__DraftEffectBindingView_InteractiveEditorX__"] = node.X;
                node?.Bundle?.Parameters?["__DraftEffectBindingView_InteractiveEditorY__"] = node.Y;
                ConnectionsLayer.Invalidate();
                break;
        }
    }

    private void OnPortPan(NodeViewModel node, PanUpdatedEventArgs e, bool isInput, int portIndex = 0)
    {
        switch (e.StatusType)
        {
            case GestureStatus.Started:
                _isDraggingNodeOrPort = true;
                _drawable.DragSourceNode = node;
                _drawable.IsDraggingFromInput = isInput;
                _drawable.DragSourcePortIndex = portIndex;
                // Initialize drag point at the port location (Screen Space)
                // Use current scale for initial position calculation
                double s = NodesContainer.Scale;

                double portY = 40;
                if (isInput && node.InputPortNames != null && node.InputPortNames.Length > 1)
                {
                    double portSize = 15;
                    double spacing = 5;
                    int n = node.InputPortNames.Length;
                    double stackHeight = n * portSize + (n - 1) * spacing;
                    double startY = (80 - stackHeight) / 2;
                    portY = startY + portIndex * (portSize + spacing) + (portSize / 2);
                }

                if (isInput)
                    _drawable.DragPoint = new Point((node.X * s) + _drawable.PanX, (node.Y * s) + _drawable.PanY + portY * s);
                else
                    _drawable.DragPoint = new Point((node.X * s) + _drawable.PanX + node.Width * s, (node.Y * s) + _drawable.PanY + 40 * s);
                break;

            case GestureStatus.Running:
                // Calculate new drag point based on start + delta
                // Note: e.TotalX/Y are in screen coords, so we add them directly to screen pos

                // Re-calculate Start Point in Screen Space
                double scale = NodesContainer.Scale;

                double startPortY = 40;
                if (isInput && node.InputPortNames != null && node.InputPortNames.Length > 1)
                {
                    double portSize = 15;
                    double spacing = 5;
                    int n = node.InputPortNames.Length;
                    double stackHeight = n * portSize + (n - 1) * spacing;
                    double startY = (80 - stackHeight) / 2;
                    startPortY = startY + portIndex * (portSize + spacing) + (portSize / 2);
                }

                double baseX = (isInput ? node.X : node.X + node.Width) * scale + _drawable.PanX;
                double baseY = (node.Y + startPortY) * scale + _drawable.PanY;

                _drawable.DragPoint = new Point(baseX + e.TotalX, baseY + e.TotalY);
                ConnectionsLayer.Invalidate();
                break;

            case GestureStatus.Completed:
            case GestureStatus.Canceled:
                _isDraggingNodeOrPort = false;
                if (_drawable.DragPoint.HasValue)
                {
                    var dropPoint = _drawable.DragPoint.Value;
                    NodeViewModel? match = null;
                    int matchPortIndex = 0;
                    double finalScale = NodesContainer.Scale;

                    // Hit Test
                    foreach (var kvp in _nodes)
                    {
                        var candidate = kvp.Value;
                        if (candidate == node) continue;

                        if (isInput)
                        {
                            // Dragging FROM Input, looking for Output
                            if (candidate.Kind == NodeKind.Input) continue;

                            // Target in Screen Space (Output Port)
                            double targetX = (candidate.X + candidate.Width) * finalScale + _drawable.PanX;
                            double targetY = (candidate.Y + 40) * finalScale + _drawable.PanY;

                            if (Math.Abs(targetX - dropPoint.X) < 40 && Math.Abs(targetY - dropPoint.Y) < 40)
                            {
                                match = candidate;
                                matchPortIndex = 0;
                                break;
                            }
                        }
                        else
                        {
                            // Dragging FROM Output, looking for Input
                            if (candidate.Kind == NodeKind.Output) continue;

                            // If Candidate has Multiple Inputs, Check which one we hit
                            if (candidate.InputPortNames != null && candidate.InputPortNames.Length > 1)
                            {
                                double portSize = 15;
                                double spacing = 5;
                                int n = candidate.InputPortNames.Length;
                                double stackHeight = n * portSize + (n - 1) * spacing;
                                double startY = (80 - stackHeight) / 2;

                                for (int i = 0; i < n; i++)
                                {
                                    double thisPortY = startY + i * (portSize + spacing) + (portSize / 2);

                                    double targetX = candidate.X * finalScale + _drawable.PanX;
                                    double targetY = (candidate.Y + thisPortY) * finalScale + _drawable.PanY;

                                    if (Math.Abs(targetX - dropPoint.X) < 30 && Math.Abs(targetY - dropPoint.Y) < 30)
                                    {
                                        match = candidate;
                                        matchPortIndex = i;
                                        break;
                                    }
                                }
                                if (match != null) break;
                            }
                            else
                            {
                                double targetX = candidate.X * finalScale + _drawable.PanX;
                                double targetY = (candidate.Y + 40) * finalScale + _drawable.PanY;
                                if (Math.Abs(targetX - dropPoint.X) < 40 && Math.Abs(targetY - dropPoint.Y) < 40)
                                {
                                    match = candidate;
                                    matchPortIndex = 0;
                                    break;
                                }
                            }
                        }
                    }

                    if (match != null)
                    {
                        if (isInput)
                        {
                            // Dragged from Input (Target) -> Found Output (Source)
                            // node is Target, match is Source
                            ConnectNodes(match, node, portIndex); // portIndex is from drag start (Target Input Port)
                        }
                        else
                        {
                            // Dragged from Output (Source) -> Found Input (Target)
                            // node is Source, match is Target
                            ConnectNodes(node, match, matchPortIndex);
                        }
                    }
                    else
                    {
                        SetStatusText(PPLocalizedResources.EffectBindView_ConnectedFail);
                    }
                }

                _drawable.DragSourceNode = null;
                _drawable.DragPoint = null;
                ConnectionsLayer.Invalidate();
                RebuildConnections();
                break;
        }
    }

    private void OnCanvasPanUpdated(object sender, PanUpdatedEventArgs e)
    {
        if (_isDraggingNodeOrPort) return;

        switch (e.StatusType)
        {
            case GestureStatus.Started:
                _panStartX = NodesContainer.TranslationX;
                _panStartY = NodesContainer.TranslationY;
                break;
            case GestureStatus.Running:
                NodesContainer.TranslationX = _panStartX + e.TotalX;
                NodesContainer.TranslationY = _panStartY + e.TotalY;

                _drawable.PanX = NodesContainer.TranslationX;
                _drawable.PanY = NodesContainer.TranslationY;
                ConnectionsLayer.Invalidate();
                break;
        }
    }

    private void SelectNode(NodeViewModel node)
    {
        if (_selectedNode != null && _selectedNode.View is Border b) b.Stroke = Colors.Gray;
        _selectedNode = node;
        if (node.View is Border b2) b2.Stroke = Colors.Yellow;

        PropertiesPanel.Children.Clear();
        PropertiesPanel.Children.Add(new Label { Text = node.DisplayName, FontAttributes = FontAttributes.Bold });

        if (node.Kind != NodeKind.Effect)
        {
            return;
        }

        try
        {
            ArgumentNullException.ThrowIfNull(node.Bundle);
            var ppb = node.Bundle.CreateUI();
            ArgumentNullException.ThrowIfNull(ppb, $"CreateUI() for {node.Bundle?.TypeName}");
            ppb.PropertyChanged += (s, args) =>
            {
                node.Bundle.Parameters = node.Bundle.HandlePropertyPanelChange(args);
                RebuildConnections();
                ConnectionsLayer.Invalidate();
            };

            PropertiesPanel.Children.Add(ppb.Build());
        }
        catch (Exception ex)
        {
            PropertiesPanel.Children.Add(new Label { Text = "Error loading properties: " + ex.Message });
        }

    }

    private void OnPortClicked(NodeViewModel node, bool isInput, int portIndex = 0)
    {
        if (_pendingConnectionSource == null)
        {
            if (!isInput)
            {
                _pendingConnectionSource = new NodePort { Node = node, IsInput = false };
            }
        }
        else
        {
            if (isInput)
            {
                if (_pendingConnectionSource.Value.Node != node)
                {
                    ConnectNodes(_pendingConnectionSource.Value.Node, node, portIndex);
                }
                _pendingConnectionSource = null;
            }
            else
            {
                _pendingConnectionSource = new NodePort { Node = node, IsInput = false };
            }
        }
    }

    private void ConnectNodes(NodeViewModel source, NodeViewModel target, int targetPortIndex = 0)
    {
        if (target.InputPortNames is null) ConnectNodesForOneinOneout(source, target);
        else ConnectNodesForOneinMultiout(source, target, targetPortIndex);

        SetStatusText(PPLocalizedResources.EffectBindView_Connected(source?.DisplayName ?? "Unknown",target?.DisplayName ?? "Unknown"));
    }

    private void ConnectNodesForOneinOneout(NodeViewModel source, NodeViewModel target)
    {
        Dictionary<Guid, NodeViewModel> newList = new(_nodes);
        foreach (var item in _nodes)
        {
            if (item.Key == source.Id)
            {
                var n = item.Value;
                n.OutputAnchorID = target.Id;
                newList[item.Key] = n;

            }
            else if (item.Key == target.Id)
            {
                var n = item.Value;
                n.InputAnchorID = source.Id;
                newList[item.Key] = n;

            }
            else if (item.Value.OutputAnchorID == target.Id)
            {
                var n = item.Value;
                n.OutputAnchorID = IEffectBundle.NoConnectionGUID;
                newList[item.Key] = n;
            }
        }
        _nodes = newList;
    }

    private void ConnectNodesForOneinMultiout(NodeViewModel source, NodeViewModel target, int targetPortIndex)
    {
        Dictionary<Guid, NodeViewModel> newList = new(_nodes);
        foreach (var item in _nodes)
        {
            if (item.Key == source.Id)
            {
                var n = item.Value;
                n.OutputAnchorID = target.Id;
                newList[item.Key] = n;

            }
            else if (item.Key == target.Id)
            {
                var n = item.Value;
                ArgumentNullException.ThrowIfNull(n.InputPortNames);
                if (n.InputAnchorIDs is null) n.InputAnchorIDs = Enumerable.Repeat(IEffectBundle.NoConnectionGUID, n.InputPortNames.Length).ToList();
                n.InputAnchorIDs[targetPortIndex] = source.Id;
                newList[item.Key] = n;

            }
            else if (item.Value.OutputAnchorID == target.Id)
            {
                var n = item.Value;
                n.OutputAnchorID = IEffectBundle.NoConnectionGUID;
                newList[item.Key] = n;
            }
        }
        _nodes = newList;
    }


    private void DisconnectNode(NodeViewModel node)
    {
        Dictionary<Guid, NodeViewModel> newList = new(_nodes);

        switch (node.Kind)
        {
            case NodeKind.Input:
                {
                    foreach (var item in _nodes)
                    {
                        if (item.Value.InputAnchorID == IEffectBundle.InputAnchorGUID)
                        {
                            item.Value.InputAnchorID = IEffectBundle.NoConnectionGUID;
                            newList[item.Key] = item.Value;
                        }
                    }

                    break;
                }
            case NodeKind.Output:
                {
                    foreach (var item in _nodes)
                    {
                        if (item.Value.OutputAnchorID == IEffectBundle.OutputAnchorGUID)
                        {
                            item.Value.OutputAnchorID = IEffectBundle.NoConnectionGUID;
                            newList[item.Key] = item.Value;
                        }

                    }

                    break;
                }
            default:
                {
                    node.InputAnchorID = IEffectBundle.NoConnectionGUID;
                    node.OutputAnchorID = IEffectBundle.NoConnectionGUID;
                    if (node.InputAnchorIDs is not null) node.InputAnchorIDs = Enumerable.Repeat(IEffectBundle.NoConnectionGUID, node.InputAnchorIDs.Count).ToList();
                    newList[node.Id] = node;

                    break;
                }
        }

        _nodes = newList;

        SetStatusText(PPLocalizedResources.EffectBindView_Disconnected(node?.DisplayName ?? "Unknown"));

        RebuildConnections();
        ConnectionsLayer.Invalidate();
    }

    private void RebuildConnections()
    {
        _drawable.Connections.Clear();

        if (!_nodes.Any(n => n.Value.Kind == NodeKind.Effect && n.Value.OutputAnchorID == IEffectBundle.OutputAnchorGUID)) //no any effect connected to output
        {
            _drawable.Connections.Add((_inputNode ?? throw new NullReferenceException(), _outputNode ?? throw new NullReferenceException(), 0));

        }

        foreach (var kvp in _nodes)
        {
            if (kvp.Key == IEffectBundle.InputAnchorGUID) continue;
            var item = kvp.Value;
            if (item.InputAnchorIDs.ListAny())
            {
                for (int i = 0; i < item.InputAnchorIDs.Count; i++)
                {
                    if (item.InputAnchorIDs[i] != IEffectBundle.NoConnectionGUID) _drawable.Connections.Add((_nodes[item.InputAnchorIDs[i]], item, i));
                }
            }
            else
            {
                if (item.InputAnchorID != IEffectBundle.NoConnectionGUID) _drawable.Connections.Add((_nodes[item.InputAnchorID], item, 0));

            }

            if (kvp.Key == IEffectBundle.OutputAnchorGUID) continue;

            if (item.OutputAnchorID == IEffectBundle.NoConnectionGUID || item.InputAnchorID == IEffectBundle.NoConnectionGUID)
            {
                item.View?.Opacity = 0.8;
            }
            else
            {
                item.View?.Opacity = 1;

            }
        }

    }

    public void SetStatusText(string text)
    {
        Dispatcher.Dispatch(() =>
        {
            InfoLabel.Text = text;
        });
        _page?.SetStatusText(text);

    }

    [DebuggerDisplay("{Id}, {Bundle?.TypeName}")]
    class NodeViewModel
    {
        public required Guid Id;
        public required NodeKind Kind;
        public required IEffectBundle? Bundle;
        public View? View;
        public double X, Y;
        public bool IsBindable => Bundle?.IsBindableEffect ?? false;
        public string[]? InputPortNames;

        public Guid InputAnchorID { get => Bundle?.BindedInputId ?? field; set { if (Bundle != null) Bundle.BindedInputId = value; else field = value; } }
        public Guid OutputAnchorID { get => Bundle?.BindedOutputId ?? field; set { if (Bundle != null) Bundle.BindedOutputId = value; else field = value; } }
        public List<Guid>? InputAnchorIDs { get => Bundle?.BindedInputIds; set { if (Bundle != null) Bundle.BindedInputIds = value; } }

        public double DragStartX, DragStartY;

        public double Width => View?.Width > 0 ? View.Width : 150;

        public string DisplayName { get; set; }


    }

    struct NodePort
    {
        public NodeViewModel? Node;
        public bool IsInput;
        public int PortIndex;
    }

    private void UpdateAddEffectsPanel()
    {
        AddEffectsPanel.Children.Clear();

        if (_clip is null || _page is null) return;


        AddEffectsPanel.Children.Add(ClipInfoBuilder.BuildAddEffectPanel(
            _page,
            EffectServices.GetAvailableEffectBundles(),
            new(),
            (s, e) =>
            {
                if (e.Id == "AddBundle")
                {
                    // ´¦ÀíÌí¼ÓÂß¼­
                    var BundleType = e.Value?.ToString();
                    if (!string.IsNullOrWhiteSpace(BundleType)) AddBundle(BundleType);
                }
            }
        ));
    }

    private void AddBundle(string bundleTypeName)
    {
        if (_clip == null) return;

        var bundlesFactories = EffectServices.GetAvailableEffectBundles();

        if (bundlesFactories.TryGetValue(bundleTypeName, out var factory))
        {
            var instance = factory();
            instance.Id = Guid.NewGuid();
            instance.BindedInputId = IEffectBundle.NoConnectionGUID;
            instance.BindedOutputId = IEffectBundle.NoConnectionGUID;
            _clip.EffectBundles ??= new Dictionary<Guid, IEffectBundle>();
            _clip.EffectBundles[instance.Id] = instance;
            ClipInfoBuilder.RebuildAllEffects(_clip);

            LoadClip(_clip, _page);
        }

        SetStatusText(Localized._Done);
    }


    class ConnectionsDrawable : IDrawable
    {
        private List<NodeViewModel> _nodes;
        public List<(NodeViewModel From, NodeViewModel To, int ToPortIndex)> Connections = new();
        public double PanX, PanY;
        public double Scale = 1.0;

        // Dragging State
        public NodeViewModel? DragSourceNode;
        public bool IsDraggingFromInput;
        public int DragSourcePortIndex; // 0 for output usually
        public Point? DragPoint;

        public ConnectionsDrawable(Dictionary<Guid, NodeViewModel> nodes)
        {
            _nodes = nodes.Values.ToList();
        }

        public void Draw(ICanvas canvas, RectF dirtyRect)
        {
            canvas.SaveState();

            canvas.StrokeColor = Colors.White;
            canvas.StrokeSize = (float)(2 * Scale);

            foreach (var (from, to, toIdx) in Connections)
            {
                var start = Transform(from.X + from.Width, from.Y + 40);

                double portY = 40;
                if (to.InputPortNames != null && to.InputPortNames.Length > 1)
                {
                    double portSize = 15;
                    double spacing = 5;
                    int n = to.InputPortNames.Length;
                    double stackHeight = n * portSize + (n - 1) * spacing;
                    double startY = (80 - stackHeight) / 2;
                    portY = startY + toIdx * (portSize + spacing) + (portSize / 2);
                }

                var end = Transform(to.X, to.Y + portY);

                DrawCurve(canvas, start, end);
            }

            if (DragSourceNode != null && DragPoint.HasValue)
            {
                Point start, end;
                if (IsDraggingFromInput)
                {
                    // Dragging FROM Input (Port) -> Mouse
                    // DragSourcePortIndex matters here
                    start = DragPoint.Value; // This point is already calculated in OnPortPan
                                             // But wait, IsDraggingFromInput here means we started dragging ON an input port.
                                             // Usually we draw a line FROM that port TO mouse.

                    double portY = 40;
                    if (DragSourceNode.InputPortNames != null && DragSourceNode.InputPortNames.Length > 1)
                    {
                        double portSize = 15;
                        double spacing = 5;
                        int n = DragSourceNode.InputPortNames.Length;
                        double stackHeight = n * portSize + (n - 1) * spacing;
                        double startY = (80 - stackHeight) / 2;
                        portY = startY + DragSourcePortIndex * (portSize + spacing) + (portSize / 2);
                    }

                    // Logic was reversed in previous code?
                    // "Dragging INTO input. Line starts at Mouse, ends at Input"
                    // No, IsDraggingFromInput means we grabbed the input handle.
                    // Usually that means we are pulling a wire OUT of it to connect to an output?
                    // Or we are dragging an EXISTING connection away?
                    // The UI intent in OnPortClick seems to be: Tap input -> Wait for tap on node.
                    // Drag intent: Drag from Input -> Hit Output.
                    // Visuals: Start at Input Port, End at Mouse.

                    start = Transform(DragSourceNode.X, DragSourceNode.Y + portY);
                    end = DragPoint.Value;
                }
                else
                {
                    // Dragging FROM Output -> Mouse
                    start = Transform(DragSourceNode.X + DragSourceNode.Width, DragSourceNode.Y + 40);
                    end = DragPoint.Value;
                }

                canvas.StrokeColor = Colors.Yellow;
                DrawCurve(canvas, start, end);
            }

            canvas.RestoreState();
        }

        private Point Transform(double x, double y)
        {
            // We need to match how NodesContainer renders.
            // If we Set AnchorPoints to 0,0 in OnReset, this simple math works:
            // return new Point(x * Scale + PanX, y * Scale + PanY);

            // But if Anchor is Center (default), it expands from center.
            // Width/Height of NodesContainer matters.

            // Let's strictly use (X * Scale) + PanX for now and ensure Anchor is handled in View.
            return new Point(x * Scale + PanX, y * Scale + PanY);
        }


        private void DrawCurve(ICanvas canvas, Point start, Point end)
        {
            var path = new PathF();
            path.MoveTo((float)start.X, (float)start.Y);
            float controlPointOffset = 50;
            path.CurveTo((float)(start.X + controlPointOffset), (float)start.Y, (float)(end.X - controlPointOffset), (float)end.Y, (float)end.X, (float)end.Y);
            canvas.DrawPath(path);
        }
    }
}