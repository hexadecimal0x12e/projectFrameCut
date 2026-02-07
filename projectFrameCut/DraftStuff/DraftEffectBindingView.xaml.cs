using CommunityToolkit.Maui.Views;
using Microsoft.Maui.Controls.Shapes;
using projectFrameCut.ApplicationAPIBase.Effect;
using projectFrameCut.ApplicationAPIBase.Views.PropertyPanelBuilders;
using projectFrameCut.Render.RenderAPIBase.EffectAndMixture;
using projectFrameCut.Services;
using projectFrameCut.Render.Plugin;
using System.Diagnostics;
using Rect = Microsoft.Maui.Graphics.Rect;

namespace projectFrameCut.DraftStuff;

public enum NodeKind { Effect, Input, Output }

public partial class DraftEffectBindingView : ContentView
{
    private ClipElementUI? _clip;
    private DraftPage? _page;
    private List<NodeViewModel> _nodes = new();
    private ConnectionsDrawable _drawable;
    private NodeViewModel? _selectedNode;
    private NodePort? _pendingConnectionSource;

    private NodeViewModel? _inputNode;
    private NodeViewModel? _outputNode;

    public DraftEffectBindingView()
    {
        InitializeComponent();
        _drawable = new ConnectionsDrawable(_nodes);
        ConnectionsLayer.Drawable = _drawable;
    }

    public void LoadClip(ClipElementUI clip, DraftPage? page = null)
    {
        _clip = clip;
        _page = page;
        _nodes.Clear();
        NodesContainer.Children.Clear();
        PropertiesPanel.Children.Clear();

        // Create System Nodes
        _inputNode = new NodeViewModel { Kind = NodeKind.Input, X = 50, Y = 150, Data = null, Logic = null, AllowMultiOutput = true };
        AddNode(_inputNode);

        if (_clip.EffectBundles != null)
        {
            var factories = EffectServices.GetAvailableEffectBundles();

            foreach (var bundleData in _clip.EffectBundles.Values)
            {
                if (!factories.TryGetValue(bundleData.BundleTypeName, out var factory)) continue;

                try
                {
                    var bundleLogic = factory();
                    bundleLogic.Parameters = bundleData.Parameters;
                    bundleLogic.Id = bundleData.Id;
                    if (!string.IsNullOrEmpty(bundleData.Name)) bundleLogic.Name = bundleData.Name;
                    else bundleData.Name = bundleLogic.Name;

                    bool isBindable = bundleLogic.IsBindableEffect;

                    var createdEffects = bundleLogic.Create();
                    IBindableEffectFactory? bindableFactory = null;
                    bool allowMultiOutput = false;

                    if (createdEffects.Length > 0)
                    {
                        var eff = createdEffects[0];
                        if (eff is IBindableEffectFactory bef)
                        {
                            bindableFactory = bef;
                        }
                        
                        if (eff is IBindableArgumentEffectValueProvider ||
                            eff is IBindableArgumentEffectOneToOneValueProcesser ||
                            eff is IBindableArgumentEffectManyToOneValueProcesser)
                        {
                            allowMultiOutput = true;
                        }
                    }

                    var node = new NodeViewModel
                    {
                        Kind = NodeKind.Effect,
                        Data = bundleData,
                        Logic = bundleLogic,
                        IsBindable = isBindable,
                        BindableFactory = bindableFactory,
                        AllowMultiOutput = allowMultiOutput
                    };

                    // Recover Index if missing/default
                    if (node.Data.Index == int.MaxValue && _clip.Effects != null)
                    {
                        int minIndex = int.MaxValue;
                        foreach (var kvp in _clip.Effects)
                        {
                            var effect = kvp.Value;
                            if (effect.BindedEffectGroupID == bundleData.Id)
                            {
                                if (effect.Index < minIndex) minIndex = effect.Index;
                            }
                        }
                        if (minIndex != int.MaxValue)
                        {
                            node.Data.Index = minIndex;
                        }
                    }

                    // Initial Position
                    // Shift X to account for Input node
                    node.X = 250 + (node.Data.Index == int.MaxValue ? _nodes.Count * 50 : node.Data.Index * 200);
                    node.Y = 150 + (node.IsBindable ? 200 : 0) + (node.Data.Index == int.MaxValue ? 200 : 0);

                    AddNode(node);
                }
                catch (Exception ex)
                {
                    Debug.WriteLine($"Failed to load bundle {bundleData.BundleTypeName}: {ex.Message}");
                }
            }
        }

        // Create Output Node
        // Position it far right
        double maxX = _nodes.Max(n => n.X);
        _outputNode = new NodeViewModel { Kind = NodeKind.Output, X = Math.Max(maxX + 200, 600), Y = 150, Data = null, Logic = null };
        AddNode(_outputNode);

        RebuildConnections();
        ConnectionsLayer.Invalidate();
    }

    private void AddNode(NodeViewModel node)
    {
        var view = CreateNodeView(node);
        node.View = view;
        NodesContainer.Add(view);
        AbsoluteLayout.SetLayoutBounds(view, new Rect(node.X, node.Y, 150, 80));
        _nodes.Add(node);
    }

    private View CreateNodeView(NodeViewModel node)
    {
        var frame = new Border
        {
            Stroke = node.Kind == NodeKind.Effect ? Colors.Gray : Colors.White,
            StrokeThickness = node.Kind == NodeKind.Effect ? 2 : 4,
            StrokeShape = new RoundRectangle { CornerRadius = 5 },
            BackgroundColor = Color.FromArgb("#2d2d2d"),
            Padding = 5,
            WidthRequest = 150,
            HeightRequest = 80
        };

        string title = node.Kind switch
        {
            NodeKind.Input => "Input",
            NodeKind.Output => "Output",
            _ => node.Data?.Name ?? node.Data?.BundleTypeName ?? "Unknown"
        };

        var label = new Label
        {
            Text = title,
            TextColor = Colors.White,
            HorizontalOptions = LayoutOptions.Center,
            VerticalOptions = LayoutOptions.Center,
            FontSize = 14,
            FontAttributes = node.Kind == NodeKind.Effect ? FontAttributes.None : FontAttributes.Bold,
            LineBreakMode = LineBreakMode.TailTruncation
        };

        // Ports
        var inputPort = new BoxView { Color = Colors.Green, WidthRequest = 15, HeightRequest = 15, VerticalOptions = LayoutOptions.Center };
        var outputPort = new BoxView { Color = Colors.Red, WidthRequest = 15, HeightRequest = 15, VerticalOptions = LayoutOptions.Center };

        // Handle visibility for System Nodes
        if (node.Kind == NodeKind.Input) inputPort.Color = Colors.Transparent; // Hide Input on Input Node
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

        // Port Interactions
        var inputTap = new TapGestureRecognizer();
        inputTap.Tapped += (s, e) => OnPortClicked(node, true);
        inputPort.GestureRecognizers.Add(inputTap);

        var inputPan = new PanGestureRecognizer();
        inputPan.PanUpdated += (s, e) => OnPortPan(node, e, true);
        inputPort.GestureRecognizers.Add(inputPan);

        var outputTap = new TapGestureRecognizer();
        outputTap.Tapped += (s, e) => OnPortClicked(node, false);
        outputPort.GestureRecognizers.Add(outputTap);

        var outputPan = new PanGestureRecognizer();
        outputPan.PanUpdated += (s, e) => OnPortPan(node, e, false);
        outputPort.GestureRecognizers.Add(outputPan);

        // Layout
        var layout = new Grid { ColumnDefinitions = new ColumnDefinitionCollection { new ColumnDefinition { Width = 20 }, new ColumnDefinition { Width = GridLength.Star }, new ColumnDefinition { Width = 20 } } };
        
        bodyActionContainer.Add(label);
        
        layout.Add(inputPort, 0, 0);
        layout.Add(bodyActionContainer, 1, 0);
        layout.Add(outputPort, 2, 0);

        frame.Content = layout;
        return frame;
    }

    private void OnNodePan(NodeViewModel node, PanUpdatedEventArgs e)
    {
        switch (e.StatusType)
        {
            case GestureStatus.Started:
                node.DragStartX = node.X;
                node.DragStartY = node.Y;
                break;
            case GestureStatus.Running:
                node.X = node.DragStartX + e.TotalX;
                node.Y = node.DragStartY + e.TotalY;
                AbsoluteLayout.SetLayoutBounds(node.View, new Rect(node.X, node.Y, 150, 80));
                ConnectionsLayer.Invalidate();
                break;
        }
    }

    private void OnPortPan(NodeViewModel node, PanUpdatedEventArgs e, bool isInput)
    {
        switch (e.StatusType)
        {
            case GestureStatus.Started:
                _drawable.DragSourceNode = node;
                _drawable.IsDraggingFromInput = isInput;
                // Initialize drag point at the port location
                if (isInput)
                    _drawable.DragPoint = new Point(node.X, node.Y + 40); // Input port edge
                else
                    _drawable.DragPoint = new Point(node.X + 150, node.Y + 40); // Output port edge
                break;

            case GestureStatus.Running:
                // Calculate new drag point based on start + delta
                double startX = isInput ? node.X : node.X + 150;
                double startY = node.Y + 40;
                _drawable.DragPoint = new Point(startX + e.TotalX, startY + e.TotalY);
                ConnectionsLayer.Invalidate();
                break;

            case GestureStatus.Completed:
            case GestureStatus.Canceled:
                if (_drawable.DragPoint.HasValue)
                {
                    var dropPoint = _drawable.DragPoint.Value;
                    NodeViewModel? match = null;

                    // Hit Test
                    foreach (var candidate in _nodes)
                    {
                        if (candidate == node) continue;

                        if (isInput)
                        {
                            // Dragging FROM Input, looking for Output
                            // Output port is at candidate.X + 150
                            if (candidate.Kind == NodeKind.Input) continue; // Input node usually has output, but check validity in ConnectNodes

                            double targetX = candidate.X + 150;
                            double targetY = candidate.Y + 40;
                            if (Math.Abs(targetX - dropPoint.X) < 40 && Math.Abs(targetY - dropPoint.Y) < 40)
                            {
                                match = candidate;
                                break;
                            }
                        }
                        else
                        {
                            // Dragging FROM Output, looking for Input
                            // Input port is at candidate.X
                            if (candidate.Kind == NodeKind.Output) continue; // Output node has no output? Wait, Output Node HAS Input.

                            double targetX = candidate.X;
                            double targetY = candidate.Y + 40;
                            if (Math.Abs(targetX - dropPoint.X) < 40 && Math.Abs(targetY - dropPoint.Y) < 40)
                            {
                                match = candidate;
                                break;
                            }
                        }
                    }

                    if (match != null)
                    {
                        if (isInput)
                        {
                            // Dragged from Input (Target) -> Found Output (Source)
                            ConnectNodes(match, node);
                        }
                        else
                        {
                            // Dragged from Output (Source) -> Found Input (Target)
                            ConnectNodes(node, match);
                        }
                    }
                }

                _drawable.DragSourceNode = null;
                _drawable.DragPoint = null;
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

        if (node.Kind != NodeKind.Effect)
        {
            PropertiesPanel.Children.Add(new Label { Text = $"{node.Kind} Node", FontAttributes = FontAttributes.Bold });
            return;
        }

        try
        {
            var ppb = node.Logic.CreateUI();

            ppb.PropertyChanged += (s, args) =>
            {
                node.Data.Parameters = node.Logic.HandlePropertyPanelChange(args);
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

    private void OnPortClicked(NodeViewModel node, bool isInput)
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
                    ConnectNodes(_pendingConnectionSource.Value.Node, node);
                }
                _pendingConnectionSource = null;
            }
            else
            {
                _pendingConnectionSource = new NodePort { Node = node, IsInput = false };
            }
        }
    }

    private void ConnectNodes(NodeViewModel source, NodeViewModel target)
    {
        EnsureSingleOutput(source, target);

        if (target.IsBindable && target.Kind == NodeKind.Effect)
        {
            // Bindable Effect Binding
            if (target.BindableFactory != null)
            {
                if (source.Kind == NodeKind.Input)
                {
                    // Input node cannot be bound to bindable effect input directly unless special ID is supported?
                    // Assuming bindable input takes Effect ID. System Nodes don't have IDs like effects.
                    // For now, allow binding but we must provide an ID for InputNode or handle it.
                    // Input logic for bindable is tricky. Usually "Original Image".ID?
                    // Let's assume standard sequential is what the user wants if drawing from Input.
                    _page?.SetStatusText("Cannot bind Input directly to parameter yet.");
                    return;
                }

                target.Data.Parameters["BindedInputID"] = source.Data.Id;
                target.Data.Parameters.Remove("BindedInputIDs");
                target.Logic.Parameters = target.Data.Parameters;
            }
        }
        else if (target.Kind == NodeKind.Effect || target.Kind == NodeKind.Output)
        {
            // Sequential Binding

            // 1. Handle Input -> Effect
            if (source.Kind == NodeKind.Input)
            {
                // Target becomes Index 0
                // If Target is already 0, do nothing.
                if (target.Kind == NodeKind.Effect)
                {
                    InsertNodeAt(target, 0);
                }
                RebuildConnections();
                ConnectionsLayer.Invalidate();
                _page?.SetStatusText($"Binded.");

                return;
            }

            // 2. Handle Effect -> Output
            if (target.Kind == NodeKind.Output)
            {
                // Source becomes Last in Chain
                // If Source is floating, append.
                if (source.Kind == NodeKind.Effect)
                {
                    if (source.Data.Index == int.MaxValue)
                    {
                        AppendNodeToEnd(source);
                    }
                    // If source is already in chain, connection to Output is implicit, just visual confirmation.
                    // But maybe we want to cut the chain after source?
                    // Not implementing cut for now to avoid accidental data loss.
                }
                RebuildConnections();
                ConnectionsLayer.Invalidate();
                _page?.SetStatusText($"Binded.");
                return;
            }

            // 3. Effect -> Effect (Sequential)
            if (source.Kind == NodeKind.Effect && target.Kind == NodeKind.Effect)
            {
                if (source.Data.Index == int.MaxValue)
                {
                    AppendNodeToEnd(source);
                }

                var list = _nodes.Where(n => n.Kind == NodeKind.Effect && !n.IsBindable && n.Data.Index != int.MaxValue && n != target).OrderBy(n => n.Data.Index).ToList();
                int insertIdx = list.IndexOf(source);

                if (insertIdx >= 0)
                    list.Insert(insertIdx + 1, target);
                else
                    list.Add(target);

                Reindex(list);
            }
        }
        RebuildConnections();
        ConnectionsLayer.Invalidate();
        _page?.SetStatusText($"Binded.");

    }

    private void EnsureSingleOutput(NodeViewModel source, NodeViewModel newTarget)
    {
        if (source.AllowMultiOutput) return;

        // Disconnect from Bindable Nodes (Check by Id)
        foreach (var node in _nodes.Where(n => n.Kind == NodeKind.Effect && n.IsBindable && n != newTarget))
        {
            if (node.Data.Parameters.TryGetValue("BindedInputID", out var objId) && objId is string id && id == source.Data?.Id)
            {
                 node.Data.Parameters.Remove("BindedInputID");
                 // Refresh params
                 node.Logic.Parameters = node.Data.Parameters; 
            }
            // Handle array IDs if supported (ManyInput)
            if (node.Data.Parameters.TryGetValue("BindedInputIDs", out var objIds) && objIds is string[] ids)
            {
                if (ids.Contains(source.Data?.Id))
                {
                    node.Data.Parameters["BindedInputIDs"] = ids.Where(x => x != source.Data?.Id).ToArray();
                    node.Logic.Parameters = node.Data.Parameters;
                }
            }
        }

        // Sequential Check
        // If source is in sequence and pointing to something else, break it.
        // A sequential source points to (Index + 1).
        // If NewTarget IS the (Index+1), we follow normal path.
        // If NewTarget is Bindable, Source feeds Bindable. Source CANNOT feed (Index+1).
        // If NewTarget is Sequential (Rearrangement), the Reindex logic usually handles it, but we need to ensure clarity.
        
        // Specifically: If connecting to a Bindable Target, break the Sequential Chain.
        if (newTarget.IsBindable)
        {
             // Source is being used as data. If it was part of a main chain (outputting to next effect), that link is broken.
             // We need to detach the node that WAS after Source.
             if (source.Data != null && source.Data.Index != int.MaxValue)
             {
                 var nextNode = _nodes.FirstOrDefault(n => n.Kind == NodeKind.Effect && !n.IsBindable && n.Data.Index == source.Data.Index + 1);
                 if (nextNode != null)
                 {
                     DisconnectNode(nextNode); // Detach next node to break flow
                 }
                 // Also if Source points to Output (Last Node -> Output).
                 // Implicit connection to OutputNode is not "Data", but logical end.
                 // If Source is used as Data, does it stop being logical end?
                 // Probably yes, if strict 1-to-1.
             }
        }
        else
        {
             // Connecting to Sequential Target.
             // The Re-Index logic will handle replacing the flow.
        }
    }

    private void InsertNodeAt(NodeViewModel node, int index)
    {
        var list = _nodes.Where(n => n.Kind == NodeKind.Effect && !n.IsBindable && n.Data.Index != int.MaxValue && n != node).OrderBy(n => n.Data.Index).ToList();
        if (index > list.Count) index = list.Count;
        list.Insert(index, node);
        Reindex(list);
    }

    private void AppendNodeToEnd(NodeViewModel node)
    {
        var list = _nodes.Where(n => n.Kind == NodeKind.Effect && !n.IsBindable && n.Data.Index != int.MaxValue && n != node).OrderBy(n => n.Data.Index).ToList();
        list.Add(node);
        Reindex(list);
    }

    private void Reindex(List<NodeViewModel> list)
    {
        for (int i = 0; i < list.Count; i++) list[i].Data.Index = i;
    }

    private void DisconnectNode(NodeViewModel node)
    {
        if (node.Kind != NodeKind.Effect) return;

        node.Data.Index = int.MaxValue;
        if (node.IsBindable)
        {
            node.Data.Parameters.Remove("BindedInputID");
            node.Data.Parameters.Remove("BindedInputIDs");
            node.Logic.Parameters = node.Data.Parameters;
        }
        RebuildConnections();
        ConnectionsLayer.Invalidate();
    }

    private void RebuildConnections()
    {
        _drawable.Connections.Clear();

        // 1. Sequential
        var seqNodes = _nodes.Where(n => n.Kind == NodeKind.Effect && n.Data.Index != int.MaxValue).OrderBy(n => n.Data.Index).ToList();

        // Input -> First
        if (seqNodes.Count > 0)
        {
            if (_inputNode != null) _drawable.Connections.Add((_inputNode, seqNodes[0]));
        }
        else
        {
            // Input -> Output (Empty chain)
            if (_inputNode != null && _outputNode != null) _drawable.Connections.Add((_inputNode, _outputNode));
        }

        // Chain
        for (int i = 0; i < seqNodes.Count - 1; i++)
        {
            _drawable.Connections.Add((seqNodes[i], seqNodes[i + 1]));
        }

        // Last -> Output
        if (seqNodes.Count > 0)
        {
            if (_outputNode != null) _drawable.Connections.Add((seqNodes.Last(), _outputNode));
        }

        // 2. Bindable
        foreach (var node in _nodes.Where(n => n.Kind == NodeKind.Effect && n.IsBindable))
        {
            if (node.Data.Parameters.TryGetValue("BindedInputID", out var idObj) && idObj is string id && !string.IsNullOrEmpty(id))
            {
                var src = _nodes.FirstOrDefault(n => n.Kind == NodeKind.Effect && n.Data.Id == id);
                if (src != null) _drawable.Connections.Add((src, node));
            }
            if (node.Data.Parameters.TryGetValue("BindedInputIDs", out var idsObj) && idsObj is string[] ids)
            {
                foreach (var pid in ids)
                {
                    var src = _nodes.FirstOrDefault(n => n.Kind == NodeKind.Effect && n.Data.Id == pid);
                    if (src != null) _drawable.Connections.Add((src, node));
                }
            }
        }
    }

    class NodeViewModel
    {
        public NodeKind Kind;
        public EffectBundleData? Data;
        public IEffectBundle? Logic;
        public View? View;
        public double X, Y;
        public bool IsBindable;
        public IBindableEffectFactory? BindableFactory;
        public bool AllowMultiOutput;

        public double DragStartX, DragStartY;
    }

    struct NodePort
    {
        public NodeViewModel? Node;
        public bool IsInput;
    }

    class ConnectionsDrawable : IDrawable
    {
        private List<NodeViewModel> _nodes;
        public List<(NodeViewModel From, NodeViewModel To)> Connections = new();

        // Dragging State
        public NodeViewModel? DragSourceNode;
        public bool IsDraggingFromInput;
        public Point? DragPoint;

        public ConnectionsDrawable(List<NodeViewModel> nodes)
        {
            _nodes = nodes;
        }

        public void Draw(ICanvas canvas, RectF dirtyRect)
        {
            canvas.StrokeColor = Colors.White;
            canvas.StrokeSize = 2;

            foreach (var (from, to) in Connections)
            {
                var start = new Point(from.X + 150, from.Y + 40);
                var end = new Point(to.X, to.Y + 40);

                DrawCurve(canvas, start, end);
            }

            if (DragSourceNode != null && DragPoint.HasValue)
            {
                Point start, end;
                if (IsDraggingFromInput)
                {
                    // Dragging INTO input. Line starts at Mouse, ends at Input
                    start = DragPoint.Value;
                    end = new Point(DragSourceNode.X, DragSourceNode.Y + 40);
                }
                else
                {
                    // Dragging FROM output. Line starts at Output, ends at Mouse
                    start = new Point(DragSourceNode.X + 150, DragSourceNode.Y + 40);
                    end = DragPoint.Value;
                }

                canvas.StrokeColor = Colors.Yellow;
                DrawCurve(canvas, start, end);
            }
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