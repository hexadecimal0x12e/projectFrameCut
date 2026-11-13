using projectFrameCut.ApplicationAPIBase.Interaction;
using projectFrameCut.Render.Effect;
using projectFrameCut.Render.RenderAPIBase.EffectAndMixture;
using static LocalizedResources.SimpleLocalizerBaseGeneratedHelper_PropertyPanel;

namespace projectFrameCut.DraftStuff;

public partial class DraftEffectBindingView
{
    private NodeViewModel? _stepFocusNode
    {
        get => Graph.HighlightedNodeId is { } id ? _nodes.GetValueOrDefault(id) : null;
        set => Graph.HighlightedNodeId = value?.Id;
    }

    private void InitializeGraph()
    {
        Graph.SelectionChanged += () =>
        {
            if (!_groupMultiSelect && Graph.ActiveNodeId is { } id && _nodes.TryGetValue(id, out var node))
            {
                if (_selectedNode != node) SelectNode(node, true);
            }
            else RefreshGroupSelection();
        };
        Graph.BlankCanvasTapped += ShowSourcesAndOutputs;
        Graph.NodeContextMenuRequested += id =>
        {
            if (_nodes.TryGetValue(id, out var node)) ShowNodeContextMenu(node);
        };
        Graph.NodeActivated += id =>
        {
            if (!_nodes.TryGetValue(id, out var node)) return;
            if (node.Group is { } group)
            {
                OpenGroupGraph(group);
                return;
            }
#if WINDOWS || MACCATALYST
            DisconnectAllFromNode(node);
#else
            SelectNode(node);
#endif
        };
        Graph.ViewportChanged += _ =>
        {
            StaticInputTooltip.IsVisible = false;
            SaveViewTransform();
        };
        Graph.InteractionChanged += () =>
        {
            StaticInputTooltip.IsVisible = false;
            if (Graph.IsConnecting) SetStatusText(Localized.Effect_ClickConnectMode);
            else if (InfoLabel.Text == Localized.Effect_ClickConnectMode) SetStatusText(Localized.Effect_ClickConnectHint);
        };
        Graph.ConnectionRejected += () => SetStatusText(PPLocalizedResources.EffectBindView_ConnectedFail);
        Graph.ResolveNodePosition = (id, point) =>
        {
            var node = _nodes[id];
            TryMoveNodeWithoutOverlap(node, point.X, point.Y, out var x, out var y, out _, out _);
            return new Point(x, y);
        };
        Graph.NodeMoved += OnGraphNodeMoved;
        Graph.CanConnect = CanConnectGraphPorts;
        Graph.ConnectAsync = async connection =>
        {
            if (_closing || !TryResolveGraphPort(connection.Source, out var source, out _, out var sourceIndex)
                || !TryResolveGraphPort(connection.Target, out var target, out var kind, out var targetIndex)) return false;
            return await ConnectPortsAsync(source, sourceIndex, target, kind, targetIndex);
        };
        Graph.DisconnectAsync = port =>
        {
            if (_closing || !TryResolveGraphPort(port, out var node, out var kind, out var index)) return Task.FromResult(false);
            bool changed = TryDisconnectDroppedPort(node, kind, index);
            if (changed) OnBindingConfigurationChanged();
            SetStatusText(PPLocalizedResources.EffectBindView_ConnectedFail);
            RebuildConnections();
            return Task.FromResult(changed);
        };
        Graph.IsPortConnected = port => TryResolveGraphPort(port, out var node, out var kind, out var index)
            && IsPortConnected(node, kind, PortOf(node, kind, index)!);
    }

    private void PrepareGraphNode(NodeViewModel node)
    {
        node.CanMultiSelect = node.Kind is NodeKind.Effect or NodeKind.Group;
        node.ViewFactory = _ => CreateNodeView(node);
        node.Ports = node.InputPorts.Concat(node.OutputPorts).Concat(node.ParamPorts).Select(p => new GraphPort(
            GraphPortKey(p), (GraphPortKind)p.Kind, p.DisplayName, PortTypeHelper.GetTypeColor(p.FieldType),
            Tooltip: PortTooltip(node, p), CanConnect: NodePort.IsValidPort(p))).ToArray();
    }

    private static string GraphPortKey(NodePort port) => $"{port.Id}:{port.Key}";

    private static GraphPortReference GraphPort(NodeViewModel node, PortKind kind, int index) =>
        new(node.Id, (GraphPortKind)kind, GraphPortKey(PortOf(node, kind, index)!));

    private bool TryResolveGraphPort(GraphPortReference port, out NodeViewModel node, out PortKind kind, out int index)
    {
        kind = (PortKind)port.Kind;
        index = -1;
        node = _nodes.GetValueOrDefault(port.NodeId)!;
        if (node is null) return false;
        var ports = kind switch
        {
            PortKind.AnchorInput => node.InputPorts,
            PortKind.AnchorOutput => node.OutputPorts,
            _ => node.ParamPorts,
        };
        index = ports.FindIndex(p => GraphPortKey(p) == port.Key);
        return index >= 0;
    }

    private bool CanConnectGraphPorts(GraphPortReference source, GraphPortReference target)
    {
        if (_closing || !TryResolveGraphPort(source, out var from, out _, out var sourceIndex)
            || !TryResolveGraphPort(target, out var to, out var kind, out var targetIndex)) return false;
        var sp = from.OutputPorts[sourceIndex];
        var tp = PortOf(to, kind, targetIndex)!;
        return NodePort.IsValidPort(sp) && NodePort.IsValidPort(tp)
            && !tp.DeclaredFieldType.HasFlag(EffectArgumentFieldType.CannotBeDynamic)
            && !(sp.FieldType.HasFlag(EffectArgumentFieldType.Any) && to.Kind == NodeKind.Output)
            && (sp.FieldType.HasFlag(EffectArgumentFieldType.Any) || tp.DeclaredFieldType.HasFlag(EffectArgumentFieldType.Any)
                || PortTypeHelper.IsPortTypeCompatible(sp.FieldType, tp.FieldType, sp.TypeContract, tp.TypeContract));
    }

    private bool IsPortConnected(NodeViewModel node, PortKind kind, NodePort port)
    {
        var id = PortProviderId(node, port);
        if (kind != PortKind.AnchorOutput
            && TryGetPortBindingSource(_clip?.EffectProviders?.GetValueOrDefault(id), port.Key, out _)) return true;
        return ReadBindings().Any(b => kind == PortKind.AnchorOutput
            ? b.Source == id && (b.SourcePortKey ?? node.OutputPorts.FirstOrDefault()?.Key) == port.Key
            : b.Target == id && (b.Kind == UIBindingKind.Picture ? EffectProviderAnchorExtensions.InputKey : b.TargetPortKey) == port.Key);
    }

    private void EndPortConnection() => Graph.CancelInteraction();

    private void OnGraphNodeMoved(GraphNodeMove change)
    {
        if (!_nodes.TryGetValue(change.NodeId, out var node)) return;
        MoveGroup(node, change.Current.X - change.Previous.X, change.Current.Y - change.Previous.Y);
        UpdateGroupBounds();
        if (change.Status is not (GestureStatus.Completed or GestureStatus.Canceled)) return;
        if (node.Group is { } group)
        {
            var members = EffectGroupHelper.Members(group, _groups);
            foreach (var child in _nodes.Values.Where(n => members.Contains(n.Id) || n.Group is not null && ContainsGroup(group, n.Id)))
                SetNodePosition(child, true);
            SaveGroupPositions();
            EffectGroupHelper.Write(_clip!.ExtraData, _groups);
        }
        else if (node.TerminalField is { } terminal)
        {
            _clip!.ExtraData[StagePositionKey(terminal, "X")] = node.X;
            _clip.ExtraData[StagePositionKey(terminal, "Y")] = node.Y;
        }
        else if (node.Provider is { } provider)
        {
            provider.MetaData ??= new Dictionary<string, object>();
            provider.MetaData[ProviderPositionKey("X")] = node.X;
            provider.MetaData[ProviderPositionKey("Y")] = node.Y;
        }
        else SaveSystemNodePosition(node);
        Graph.Invalidate();
        RecordEditHistory();
    }
}
