using projectFrameCut.ApplicationAPIBase.Interaction;
using projectFrameCut.ApplicationPluginBase.Effect;
using projectFrameCut.Render.Effect;
using projectFrameCut.Render.RenderAPIBase.EffectAndMixture;
using projectFrameCut.Shared;
using static LocalizedResources.SimpleLocalizerBaseGeneratedHelper_PropertyPanel;

namespace projectFrameCut.DraftStuff;

public partial class DraftEffectBindingView
{
    private Guid? _groupGraphId;
    private readonly Dictionary<Guid, BindableGraphNode> _groupGraphNodes = [];
    private readonly Dictionary<Guid, EffectGroupPort> _groupGraphPorts = [];
    private EffectFieldTypeResolution? _groupGraphTypes;
    private bool _groupGraphFitPending, _groupGraphBusy;
    private int _groupGraphVersion;

    private void InitializeGroupGraph()
    {
        GroupGraph.CanConnect = CanConnectGroupGraphPorts;
        GroupGraph.ConnectAsync = ConnectGroupGraphPortsAsync;
        GroupGraph.DisconnectAsync = DisconnectGroupGraphPortAsync;
        GroupGraph.ConnectionRejected += () => ShowGroupGraphError(PPLocalizedResources.EffectBindView_ConnectedFail);
        GroupGraph.SizeChanged += (_, _) => FitGroupGraphWhenReady();
    }

    private void OpenGroupGraph(EffectGroup group)
    {
        if (_closing || _clip?.EffectProviders is null) return;
        CloseGroupGraph();
        EndPortConnection();
        StaticInputTooltip.IsVisible = false;
        _groupGraphId = group.Id;
        _groupGraphTypes = _fieldTypes;
        foreach (var port in group.Ports) _groupGraphPorts.Add(Guid.NewGuid(), EffectGroupHelper.Copy(port));
        GroupGraphTitle.Text = group.Name;
        GroupGraphHint.Text = Localized.Effect_ClickConnectHint;
        GroupGraphHint.TextColor = Colors.LightGray;
        GroupGraphOverlay.IsVisible = true;
        _groupGraphFitPending = true;
        RefreshGroupGraph();
    }

    private void CloseGroupGraph()
    {
        _groupGraphVersion++;
        _groupGraphId = null;
        _groupGraphBusy = _groupGraphFitPending = false;
        _groupGraphTypes = null;
        GroupGraphOverlay.IsVisible = false;
        GroupGraph.IsEnabled = true;
        GroupGraph.Clear();
        _groupGraphNodes.Clear();
        _groupGraphPorts.Clear();
    }

    private void OnCloseGroupGraphClicked(object sender, EventArgs e) => CloseGroupGraph();

    private GraphPortReference GroupGraphEndpoint(EffectGroupPort port) => new(port.ProviderId,
        !port.IsInput ? GraphPortKind.Output : IsNodeInput(_clip!.EffectProviders![port.ProviderId], port.FieldId)
            ? GraphPortKind.Input : GraphPortKind.Parameter, port.FieldId);

    private EffectArgumentFieldType GroupGraphPortType(GraphPortReference port)
    {
        if (_groupGraphPorts.TryGetValue(port.NodeId, out var boundary)) port = GroupGraphEndpoint(boundary);
        var p = _clip!.EffectProviders![port.NodeId];
        return port.Kind == GraphPortKind.Output
            ? _groupGraphTypes?.GetOutputType(p, port.Key) ?? p.GetOutputFields()[port.Key].FieldType
            : _groupGraphTypes?.GetInputType(p, port.Key) ?? DynamicEffectBindings.InputFields(p)[port.Key].FieldType;
    }

    private void RefreshGroupGraph()
    {
        if (_clip?.EffectProviders is not { } providers || _groups.FirstOrDefault(g => g.Id == _groupGraphId) is not { } group) return;
        var members = EffectGroupHelper.Members(group, _groups);
        double minX = members.Min(id => _nodes[id].X), minY = members.Min(id => _nodes[id].Y);
        var connections = new List<GraphConnection>();
        foreach (var id in members)
        {
            var p = providers[id];
            var ports = DynamicEffectBindings.InputFields(p).Select(f => new GraphPort(f.Key,
                IsNodeInput(p, f.Key) ? GraphPortKind.Input : GraphPortKind.Parameter,
                EffectProviderUIHelper.FieldName(p, f.Key, true, HumanizePortName(f.Key)),
                PortTypeHelper.GetTypeColor(_groupGraphTypes?.GetInputType(p, f.Key) ?? f.Value.FieldType),
                CanConnect: !f.Value.FieldType.HasFlag(EffectArgumentFieldType.CannotBeDynamic))).ToList();
            if (DynamicEffectBindings.CanBindOutput(p))
                ports.AddRange(p.GetOutputFields().Select(f => new GraphPort(f.Key, GraphPortKind.Output,
                    EffectProviderUIHelper.FieldName(p, f.Key, false, HumanizePortName(f.Key)),
                    PortTypeHelper.GetTypeColor(_groupGraphTypes?.GetOutputType(p, f.Key) ?? f.Value.FieldType))));
            SetGroupGraphNode(id, p.Name, ports, _nodes[id].X - minX + 340, _nodes[id].Y - minY + 30);
            foreach (var binding in p.AnchorsBindingState.Where(b => !EffectProviderAnchorExtensions.IsOutputBindingKey(b.Key)))
                if (EffectProviderOutputExtensions.TryParseOutputSourceId(binding.Value, out var sourceId, out var output)
                    && members.Contains(sourceId) && providers[sourceId].TryGetOutputField(output, out var field)
                    && DynamicEffectBindings.InputFields(p).ContainsKey(binding.Key))
                {
                    var source = new GraphPortReference(sourceId, GraphPortKind.Output, field.Id);
                    var target = new GraphPortReference(id, IsNodeInput(p, binding.Key) ? GraphPortKind.Input : GraphPortKind.Parameter, binding.Key);
                    connections.Add(new(source, target, PortTypeHelper.GetTypeColor(GroupGraphPortType(source)), target.Kind == GraphPortKind.Parameter));
                }
        }
        foreach (var port in group.Ports)
            if (!_groupGraphPorts.Values.Any(p => EffectGroupHelper.SamePort(p, port)))
                _groupGraphPorts.Add(Guid.NewGuid(), EffectGroupHelper.Copy(port));
        int inputIndex = 0, outputIndex = 0;
        double right = members.Max(id => _groupGraphNodes[id].X + Math.Max(240, _groupGraphNodes[id].View?.Width ?? 0)) + 100;
        foreach (var (id, port) in _groupGraphPorts)
        {
            var endpoint = GroupGraphEndpoint(port);
            var kind = port.IsInput ? GraphPortKind.Output : GraphPortKind.Input;
            var color = PortTypeHelper.GetTypeColor(GroupGraphPortType(endpoint));
            var reference = new GraphPortReference(id, kind, port.FieldId);
            string? tooltip = null;
            if (port.IsInput && providers[port.ProviderId].AnchorsBindingState.TryGetValue(port.FieldId, out var source)
                && source != IEffectProvider.NoConnectionGUID.ToString())
                tooltip = GetBoundSourceDisplayName(providers[port.ProviderId], source);
            else if (!port.IsInput)
            {
                var targets = providers.Values.Where(p => !members.Contains(p.Id)).SelectMany(p => p.AnchorsBindingState
                    .Where(b => !EffectProviderAnchorExtensions.IsOutputBindingKey(b.Key) && GroupGraphSourceMatches(b.Value, port))
                    .Select(b => $"{p.Name}: {EffectProviderUIHelper.FieldName(p, b.Key, true)}"));
                if (providers[port.ProviderId].IsFinalOutputSource()
                    && providers[port.ProviderId].TryGetOutputField(providers[port.ProviderId].GetFinalOutputFieldId(), out var final)
                    && final.Id == port.FieldId) targets = targets.Append(ContentOutputName);
                tooltip = string.Join("\n", targets);
            }
            SetGroupGraphNode(id, $"{Localized.EffectGroup_PublicPorts} · {(port.IsInput ? Localized.EffectGroup_Input : Localized.EffectGroup_Output)}",
                [new(port.FieldId, kind, port.Name, color, Tooltip: tooltip)], port.IsInput ? 20 : right,
                30 + (port.IsInput ? inputIndex++ : outputIndex++) * 130, Colors.CornflowerBlue);
            if (group.Ports.Any(p => EffectGroupHelper.SamePort(p, port)))
                connections.Add(port.IsInput ? new(reference, endpoint, color, endpoint.Kind == GraphPortKind.Parameter)
                    : new(endpoint, reference, color));
        }
        GroupGraph.SetConnections(connections);
        if (_groupGraphFitPending) Dispatcher.Dispatch(FitGroupGraphWhenReady);
    }

    private void SetGroupGraphNode(Guid id, string name, IReadOnlyList<GraphPort> ports, double x, double y, Color? border = null)
    {
        if (_groupGraphNodes.TryGetValue(id, out var node))
        {
            node.Ports = ports;
            GroupGraph.RefreshNode(id);
        }
        else
        {
            node = new() { Id = id, DisplayName = name, Ports = ports, X = x, Y = y, BorderColor = border ?? Colors.Gray };
            _groupGraphNodes.Add(id, node);
            GroupGraph.AddNode(node);
        }
        node.View!.SizeChanged += (_, _) =>
        {
            if (_groupGraphFitPending) Dispatcher.Dispatch(FitGroupGraphWhenReady);
        };
    }

    private void FitGroupGraphWhenReady()
    {
        if (!_groupGraphFitPending || !GroupGraphOverlay.IsVisible || GroupGraph.Width <= 40 || GroupGraph.Height <= 40
            || _groupGraphNodes.Values.Any(n => n.View is null || n.View.Width <= 0 || n.View.Height <= 0)) return;
        _groupGraphFitPending = false;
        GroupGraph.FitToContent();
    }

    private void OnArrangeGroupGraphClicked(object sender, EventArgs e)
    {
        var layers = _groupGraphNodes.Keys.ToDictionary(id => id, id => _groupGraphPorts.ContainsKey(id) ? 0 : 1);
        var remaining = _groupGraphNodes.Keys.ToHashSet();
        while (remaining.Count > 0)
        {
            var ready = remaining.Where(id => !GroupGraph.Connections.Any(c => c.Target.NodeId == id && remaining.Contains(c.Source.NodeId))).ToArray();
            if (ready.Length == 0) ready = [remaining.First()];
            foreach (var id in ready)
            {
                remaining.Remove(id);
                foreach (var c in GroupGraph.Connections.Where(c => c.Source.NodeId == id && remaining.Contains(c.Target.NodeId)))
                    layers[c.Target.NodeId] = Math.Max(layers[c.Target.NodeId], layers[id] + 1);
            }
        }
        int last = layers.Where(p => !_groupGraphPorts.ContainsKey(p.Key)).Select(p => p.Value).DefaultIfEmpty(1).Max() + 1;
        foreach (var (id, port) in _groupGraphPorts) layers[id] = port.IsInput ? 0 : last;
        double x = 20;
        foreach (var column in _groupGraphNodes.Values.GroupBy(n => layers[n.Id]).OrderBy(g => g.Key))
        {
            double y = 30;
            foreach (var node in column.OrderBy(n => n.Y))
            {
                GroupGraph.SetNodePosition(node.Id, new Point(x, y));
                y += Math.Max(80, node.View?.Height ?? 0) + 50;
            }
            x += column.Max(n => Math.Max(240, n.View?.Width ?? 0)) + 100;
        }
        GroupGraph.FitToContent();
    }

    private bool CanConnectGroupGraphPorts(GraphPortReference source, GraphPortReference target)
    {
        if (_closing || _groupGraphBusy || _groupGraphId is null || _clip?.EffectProviders is not { } providers) return false;
        bool boundarySource = _groupGraphPorts.TryGetValue(source.NodeId, out var input);
        bool boundaryTarget = _groupGraphPorts.TryGetValue(target.NodeId, out var output);
        if (boundarySource && boundaryTarget || boundarySource && !input!.IsInput || boundaryTarget && output!.IsInput) return false;
        var from = boundarySource ? GroupGraphEndpoint(input!) : source;
        var to = boundaryTarget ? GroupGraphEndpoint(output!) : target;
        if (!providers.TryGetValue(from.NodeId, out var sp) || !providers.TryGetValue(to.NodeId, out var tp)) return false;
        var declared = boundaryTarget ? tp.GetOutputFields().GetValueOrDefault(to.Key) : DynamicEffectBindings.InputFields(tp).GetValueOrDefault(to.Key);
        if (declared is null || !boundaryTarget && declared.FieldType.HasFlag(EffectArgumentFieldType.CannotBeDynamic)) return false;
        var sourceType = GroupGraphPortType(source);
        return sourceType.HasFlag(EffectArgumentFieldType.Any) || declared.FieldType.HasFlag(EffectArgumentFieldType.Any)
            || PortTypeHelper.IsPortTypeCompatible(sourceType, GroupGraphPortType(target),
                boundarySource ? _groupGraphTypes?.GetInputContract(sp, from.Key) : _groupGraphTypes?.GetOutputContract(sp, from.Key),
                boundaryTarget ? _groupGraphTypes?.GetOutputContract(tp, to.Key) : _groupGraphTypes?.GetInputContract(tp, to.Key));
    }

    private Task<bool> ConnectGroupGraphPortsAsync(GraphConnection connection)
    {
        Guid? proxy = _groupGraphPorts.ContainsKey(connection.Source.NodeId) ? connection.Source.NodeId
            : _groupGraphPorts.ContainsKey(connection.Target.NodeId) ? connection.Target.NodeId : null;
        EffectGroupPort? replacement = null;
        if (proxy is { } proxyId)
        {
            var old = _groupGraphPorts[proxyId];
            var endpoint = old.IsInput ? connection.Target : connection.Source;
            replacement = new() { ProviderId = endpoint.NodeId, FieldId = endpoint.Key, IsInput = old.IsInput, Name = old.Name };
        }
        return EditGroupGraphAsync((groups, bindings) =>
        {
            if (proxy is { } id) RemapGroupGraphPort(groups, bindings, _groupGraphPorts[id], replacement);
            else bindings[connection.Target.NodeId][connection.Target.Key] = GroupGraphSource(connection.Source.NodeId, connection.Source.Key);
        }, proxy, replacement);
    }

    private Task<bool> DisconnectGroupGraphPortAsync(GraphPortReference port)
    {
        var connections = GroupGraph.Connections.Where(c => c.Source == port || c.Target == port).ToArray();
        if (connections.Length == 0) return Task.FromResult(false);
        return EditGroupGraphAsync((groups, bindings) =>
        {
            foreach (var c in connections)
                if (_groupGraphPorts.TryGetValue(c.Source.NodeId, out var input)) RemapGroupGraphPort(groups, bindings, input, null);
                else if (_groupGraphPorts.TryGetValue(c.Target.NodeId, out var output)) RemapGroupGraphPort(groups, bindings, output, null);
                else ClearGroupGraphBinding(bindings[c.Target.NodeId], c.Target.Key);
        }, validateTypes: false);
    }

    private string GroupGraphSource(Guid id, string key) => _clip!.EffectProviders![id] is IMultipleOutputEffectProvider
        ? EffectProviderOutputExtensions.CreateOutputSourceId(id, key) : id.ToString();

    private bool GroupGraphSourceMatches(string source, EffectGroupPort port) =>
        EffectProviderOutputExtensions.TryParseOutputSourceId(source, out var id, out var output) && id == port.ProviderId
        && _clip!.EffectProviders![id].TryGetOutputField(output, out var field) && field.Id == port.FieldId;

    private static void ClearGroupGraphBinding(Dictionary<string, string> state, string key)
    {
        if (key == EffectProviderAnchorExtensions.InputKey) state[key] = IEffectProvider.NoConnectionGUID.ToString();
        else state.Remove(key);
    }

    private void RemapGroupGraphPort(List<EffectGroup> groups, Dictionary<Guid, Dictionary<string, string>> bindings,
        EffectGroupPort old, EffectGroupPort? replacement)
    {
        var root = groups.Single(g => g.Id == _groupGraphId);
        var members = EffectGroupHelper.Members(root, groups);
        if (root.Ports.Any(p => EffectGroupHelper.SamePort(p, old)))
        {
            if (old.IsInput)
            {
                var source = bindings[old.ProviderId].GetValueOrDefault(old.FieldId);
                ClearGroupGraphBinding(bindings[old.ProviderId], old.FieldId);
                if (replacement is not null)
                {
                    ClearGroupGraphBinding(bindings[replacement.ProviderId], replacement.FieldId);
                    if (source is not null && source != IEffectProvider.NoConnectionGUID.ToString())
                        bindings[replacement.ProviderId][replacement.FieldId] = source;
                }
            }
            else
            {
                foreach (var state in bindings.Where(b => !members.Contains(b.Key)).Select(b => b.Value))
                    foreach (var b in state.Where(b => !EffectProviderAnchorExtensions.IsOutputBindingKey(b.Key) && GroupGraphSourceMatches(b.Value, old)).ToArray())
                        if (replacement is null) ClearGroupGraphBinding(state, b.Key);
                        else state[b.Key] = GroupGraphSource(replacement.ProviderId, replacement.FieldId);
                var original = bindings[old.ProviderId];
                if (original.GetValueOrDefault(EffectProviderAnchorExtensions.OutputKey) == IEffectProvider.OutputAnchorGUID.ToString()
                    && _clip!.EffectProviders![old.ProviderId].TryGetOutputField(
                        original.GetValueOrDefault(EffectProviderAnchorExtensions.OutputFieldKey), out var field) && field.Id == old.FieldId)
                {
                    original[EffectProviderAnchorExtensions.OutputKey] = IEffectProvider.NoConnectionGUID.ToString();
                    original.Remove(EffectProviderAnchorExtensions.OutputFieldKey);
                    if (replacement is not null)
                    {
                        bindings[replacement.ProviderId][EffectProviderAnchorExtensions.OutputKey] = IEffectProvider.OutputAnchorGUID.ToString();
                        bindings[replacement.ProviderId][EffectProviderAnchorExtensions.OutputFieldKey] = replacement.FieldId;
                    }
                }
            }
        }
        else if (replacement is { IsInput: true }) ClearGroupGraphBinding(bindings[replacement.ProviderId], replacement.FieldId);
        // Preserve the enclosing interfaces and all their external consumers when an endpoint moves.
        foreach (var group in groups.Where(g => EffectGroupHelper.Members(g, groups).IsSupersetOf(members)))
            for (int i = group.Ports.Count - 1; i >= 0; i--)
                if (EffectGroupHelper.SamePort(group.Ports[i], old))
                {
                    if (replacement is null) group.Ports.RemoveAt(i);
                    else group.Ports[i] = new() { ProviderId = replacement.ProviderId, FieldId = replacement.FieldId,
                        IsInput = old.IsInput, Name = group.Ports[i].Name };
                }
        if (replacement is not null && !root.Ports.Any(p => EffectGroupHelper.SamePort(p, replacement))) root.Ports.Add(replacement);
    }

    private async Task<bool> EditGroupGraphAsync(Action<List<EffectGroup>, Dictionary<Guid, Dictionary<string, string>>> edit,
        Guid? proxy = null, EffectGroupPort? replacement = null, bool validateTypes = true)
    {
        if (_groupGraphBusy || _closing || _groupGraphId is null || _clip?.EffectProviders is not { } providers) return false;
        var owner = _clip;
        int version = _groupGraphVersion, fieldVersion = _fieldTypeVersion;
        var previousGroups = _groups;
        var previous = providers.ToDictionary(p => p.Key, p => p.Value.AnchorsBindingState);
        bool applied = false;
        _groupGraphBusy = true;
        GroupGraph.IsEnabled = false;
        try
        {
            var groups = EffectGroupHelper.Copy(_groups);
            var bindings = previous.ToDictionary(p => p.Key, p => new Dictionary<string, string>(p.Value));
            edit(groups, bindings);
            foreach (var group in groups)
            {
                var members = EffectGroupHelper.Members(group, groups);
                group.Ports.RemoveAll(p => p.IsInput && EffectProviderOutputExtensions.TryParseOutputSourceId(
                    bindings[p.ProviderId].GetValueOrDefault(p.FieldId), out var id, out _) && members.Contains(id));
            }
            var changed = providers.Values.Where(p => bindings[p.Id].Count != previous[p.Id].Count
                || bindings[p.Id].Any(b => previous[p.Id].GetValueOrDefault(b.Key) != b.Value)).ToArray();
            var types = await EffectFieldTypeResolution.ResolveAsync(providers,
                bindingOverrides: bindings.ToDictionary(p => p.Key, p => (IReadOnlyDictionary<string, string>)p.Value));
            if (_closing || !ReferenceEquals(_clip, owner) || version != _groupGraphVersion || fieldVersion != _fieldTypeVersion) return false;
            if (validateTypes && (DynamicEffectCalculation.HasTypeErrors(types, owner.ExtraData)
                || changed.Any(p => !types.AreInputsCompatible(p))))
                throw new InvalidOperationException(PPLocalizedResources.EffectBindView_ConnectedFail);
            if (validateTypes)
                foreach (var p in changed)
                {
                    var source = bindings[p.Id].GetValueOrDefault(EffectProviderAnchorExtensions.InputKey);
                    if (source != previous[p.Id].GetValueOrDefault(EffectProviderAnchorExtensions.InputKey)
                        && EffectProviderOutputExtensions.TryParseOutputSourceId(source, out var id, out var output)
                        && providers.TryGetValue(id, out var from) && !from.CanConnectContent(p, output, types))
                        throw new InvalidOperationException(Localized.Effect_IncompatiblePipeline);
                }
            applied = true;
            foreach (var p in changed) p.AnchorsBindingState = bindings[p.Id];
            EffectGroupHelper.EnsureBoundaryPorts(groups, providers);
            EffectGroupHelper.Validate(groups, providers);
            if (validateTypes)
                foreach (var p in changed.Where(p => p.IsFinalOutputSource()))
                    p.SetFinalOutputSource(true, p.GetFinalOutputFieldId(), types);
            _groups = groups;
            if (!OnBindingConfigurationChanged()) throw new InvalidOperationException(PPLocalizedResources.EffectBindView_ConnectedFail);
            UpdateProviderNodes();
            ApplyGroupProjection();
            RebuildConnections();
            RefreshSelectedNode();
            if (version == _groupGraphVersion)
            {
                if (proxy is { } id && replacement is not null) _groupGraphPorts[id] = replacement;
                _groupGraphTypes = types;
                GroupGraphHint.Text = Localized.Effect_ClickConnectHint;
                GroupGraphHint.TextColor = Colors.LightGray;
                RefreshGroupGraph();
            }
            return true;
        }
        catch (Exception ex)
        {
            if (applied)
            {
                foreach (var p in providers.Values) p.AnchorsBindingState = previous[p.Id];
                _groups = previousGroups;
                EffectGroupHelper.Write(owner.ExtraData, _groups);
                CaptureGroupBindings();
                EffectBindingHelper.MaterializeFields(providers.Values);
                ApplyGroupProjection();
                RebuildConnections();
            }
            Logger.Log(ex, "Edit effect group graph", this);
            if (version == _groupGraphVersion) ShowGroupGraphError(ex.Message);
            return false;
        }
        finally
        {
            if (version == _groupGraphVersion)
            {
                _groupGraphBusy = false;
                GroupGraph.IsEnabled = true;
            }
        }
    }

    private void ShowGroupGraphError(string message)
    {
        GroupGraphHint.Text = message;
        GroupGraphHint.TextColor = Colors.OrangeRed;
    }
}
