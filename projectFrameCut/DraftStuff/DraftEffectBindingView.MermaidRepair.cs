using projectFrameCut.ApplicationAPIBase.Interaction;
using projectFrameCut.Render.Effect;
using projectFrameCut.Render.RenderAPIBase.EffectAndMixture;
using projectFrameCut.Services;
using projectFrameCut.Shared;
using static LocalizedResources.SimpleLocalizerBaseGeneratedHelper_PropertyPanel;

namespace projectFrameCut.DraftStuff;

public partial class DraftEffectBindingView
{
    private EffectGroupTemplate? _mermaidRepairTemplate;
    private Dictionary<Guid, IEffectProvider> _mermaidRepairProviders = [];
    private IReadOnlyList<EffectBindingHelper.BindingDiagnostic> _mermaidRepairDiagnostics = [];
    private EffectFieldTypeResolution? _mermaidRepairTypes;
    private readonly Dictionary<Guid, BindableGraphNode> _mermaidRepairNodes = [];
    private readonly Dictionary<string, Guid> _mermaidRepairSources = [];
    private bool _mermaidRepairInitialized;
    private bool _mermaidRepairFitPending;

    private void ClearMermaidRepair()
    {
        _mermaidRepairTemplate = null;
        _mermaidRepairProviders.Clear();
        _mermaidRepairDiagnostics = [];
        _mermaidRepairTypes = null;
        _mermaidRepairNodes.Clear();
        _mermaidRepairSources.Clear();
        _mermaidRepairFitPending = false;
        MermaidRepairGraph.Clear();
        MermaidRepairBindings.Children.Clear();
        MermaidRepairPanel.IsVisible = false;
        MermaidRepairColumn.Width = new GridLength(0);
    }

    private void InitializeMermaidRepair()
    {
        if (_mermaidRepairInitialized) return;
        _mermaidRepairInitialized = true;
        MermaidRepairGraph.SelectionChanged += RefreshMermaidBindingActions;
        MermaidRepairGraph.CanConnect = CanConnectMermaidPorts;
        MermaidRepairGraph.ConnectAsync = c => Task.FromResult(EditMermaidRepair(t =>
        {
            string source = _mermaidRepairSources.FirstOrDefault(p => p.Value == c.Source.NodeId).Key
                ?? EffectProviderOutputExtensions.CreateOutputSourceId(c.Source.NodeId, c.Source.Key);
            t.Providers.Single(p => p.Id == c.Target.NodeId).AnchorsBindingState[c.Target.Key] = source;
            foreach (var group in t.Groups)
            {
                var members = EffectGroupHelper.Members(group, t.Groups);
                bool from = members.Contains(c.Source.NodeId), to = members.Contains(c.Target.NodeId);
                if (from && to)
                    group.Ports.RemoveAll(p => p.IsInput && p.ProviderId == c.Target.NodeId && p.FieldId == c.Target.Key);
                if (from == to) continue;
                var port = new EffectGroupPort
                {
                    ProviderId = to ? c.Target.NodeId : c.Source.NodeId, FieldId = to ? c.Target.Key : c.Source.Key,
                    IsInput = to, Name = to ? c.Target.Key : c.Source.Key,
                };
                if (!group.Ports.Any(p => EffectGroupHelper.SamePort(p, port))) group.Ports.Add(port);
            }
        }));
        MermaidRepairGraph.DisconnectAsync = port => Task.FromResult(EditMermaidRepair(t =>
        {
            foreach (var connection in MermaidRepairGraph.Connections.Where(c => c.Source == port || c.Target == port))
                t.Providers.Single(p => p.Id == connection.Target.NodeId).AnchorsBindingState.Remove(connection.Target.Key);
        }));
        MermaidRepairGraph.ConnectionRejected += () =>
        {
            MermaidRepairHintLabel.Text = PPLocalizedResources.EffectBindView_ConnectedFail;
            MermaidRepairHintLabel.TextColor = Colors.OrangeRed;
        };
        MermaidRepairGraph.SizeChanged += (_, _) =>
        {
            if (_mermaidRepairFitPending) Dispatcher.Dispatch(FitMermaidRepairWhenReady);
        };
    }

    private void AddMermaidRepairNode(BindableGraphNode node)
    {
        _mermaidRepairNodes.Add(node.Id, node);
        MermaidRepairGraph.AddNode(node);
        node.View!.SizeChanged += (_, _) =>
        {
            if (_mermaidRepairFitPending) Dispatcher.Dispatch(FitMermaidRepairWhenReady);
        };
    }

    private void FitMermaidRepairWhenReady()
    {
        if (!_mermaidRepairFitPending || !MermaidRepairPanel.IsVisible || MermaidRepairGraph.Width <= 40 || MermaidRepairGraph.Height <= 40
            || _mermaidRepairNodes.Count == 0 || _mermaidRepairNodes.Values.Any(n => n.View is null || n.View.Width <= 0 || n.View.Height <= 0)) return;
        _mermaidRepairFitPending = false;
        MermaidRepairGraph.FitToContent();
    }

    private bool EditMermaidRepair(Action<EffectGroupTemplate> edit)
    {
        if (_mermaidRepairTemplate is null || _mermaidImportBusy || _closing) return false;
        try
        {
            var template = EffectGroupHelper.Copy(_mermaidRepairTemplate);
            edit(template);
            RefreshMermaidRepair(template);
            return true;
        }
        catch (Exception ex)
        {
            Logger.Log(ex, "Repair imported Mermaid bindings", this);
            MermaidImportErrors.Children.Clear();
            ShowMermaidImportError(ex);
            return false;
        }
    }

    private bool CanConnectMermaidPorts(GraphPortReference source, GraphPortReference target)
    {
        if (_mermaidImportBusy || _mermaidRepairTypes is null
            || !_mermaidRepairProviders.TryGetValue(target.NodeId, out var to)
            || !DynamicEffectBindings.InputFields(to).TryGetValue(target.Key, out var input)
            || input.FieldType.HasFlag(EffectArgumentFieldType.CannotBeDynamic)) return false;
        var builtin = _mermaidRepairSources.FirstOrDefault(p => p.Value == source.NodeId).Key;
        if (builtin is not null)
            return input.FieldType.HasFlag(EffectArgumentFieldType.Any)
                || EffectFieldTypes.AreCompatible(ValueProviderFrameContext.SourceType(builtin), _mermaidRepairTypes.GetInputType(to, target.Key));
        if (!_mermaidRepairProviders.TryGetValue(source.NodeId, out var from) || from.Stage != to.Stage
            || !DynamicEffectBindings.CanBindOutput(from) || !from.GetOutputFields().ContainsKey(source.Key)) return false;
        var type = _mermaidRepairTypes.GetOutputType(from, source.Key);
        return type.HasFlag(EffectArgumentFieldType.Any) || input.FieldType.HasFlag(EffectArgumentFieldType.Any) || EffectFieldTypes.AreCompatible(type,
            _mermaidRepairTypes.GetInputType(to, target.Key), _mermaidRepairTypes.GetOutputContract(from, source.Key),
            _mermaidRepairTypes.GetInputContract(to, target.Key));
    }

    private void RefreshMermaidRepair(EffectGroupTemplate template)
    {
        var providers = EffectGroupStore.InspectForEditing(template, out var diagnostics);
        if (_mermaidRepairTemplate is null) _mermaidRepairFitPending = true;
        var selected = MermaidRepairGraph.ActiveNodeId;
        var positions = _mermaidRepairNodes.ToDictionary(p => p.Key, p => new Point(p.Value.X, p.Value.Y));
        _mermaidRepairTemplate = template;
        _mermaidRepairProviders = providers;
        _mermaidRepairDiagnostics = diagnostics;
        _mermaidRepairTypes = new(providers);
        InitializeMermaidRepair();
        MermaidRepairGraph.Clear();
        _mermaidRepairNodes.Clear();
        MermaidRepairColumn.Width = new GridLength(2, GridUnitType.Star);
        MermaidRepairPanel.IsVisible = true;
        MermaidRepairHintLabel.Text = Localized.Effect_MermaidRepairHint;
        MermaidRepairHintLabel.TextColor = Colors.LightGray;

        var ports = template.DeclaredPorts.Concat(providers.Values.SelectMany(EffectGroupHelper.DeclaredPorts))
            .Concat(template.Groups.SelectMany(g => g.Ports))
            .Select(p => new GraphPortReference(p.ProviderId, p.IsInput ? GraphPortKind.Input : GraphPortKind.Output, p.FieldId)).ToHashSet();
        var connections = new List<GraphConnection>();
        foreach (var p in template.Providers)
            foreach (var binding in p.AnchorsBindingState.Where(b => !EffectProviderAnchorExtensions.IsOutputBindingKey(b.Key)
                && b.Value != IEffectProvider.NoConnectionGUID.ToString()))
            {
                var target = new GraphPortReference(p.Id, GraphPortKind.Input, binding.Key);
                GraphPortReference source;
                if (ValueProviderFrameContext.IsBuiltInSource(binding.Value))
                {
                    if (!_mermaidRepairSources.TryGetValue(binding.Value, out var builtinId))
                        _mermaidRepairSources[binding.Value] = builtinId = Guid.NewGuid();
                    source = new(builtinId, GraphPortKind.Output, EffectProviderAnchorExtensions.OutputKey);
                }
                else if (EffectProviderOutputExtensions.TryParseOutputSourceId(binding.Value, out var id, out var field))
                {
                    if (field is null && providers.TryGetValue(id, out var provider) && provider.TryGetOutputField(null, out var output)) field = output.Id;
                    source = new(id, GraphPortKind.Output, field ?? EffectProviderAnchorExtensions.OutputKey);
                }
                else continue;
                ports.Add(source);
                ports.Add(target);
                bool invalid = diagnostics.Any(d => d.ProviderId is null || d.ProviderId == p.Id && (d.FieldId is null || d.FieldId == binding.Key)
                    || d.ProviderId == source.NodeId && (d.FieldId is null || d.FieldId == source.Key));
                connections.Add(new(source, target, invalid ? Colors.OrangeRed : PortTypeHelper.GetTypeColor(MermaidPortType(source)), Thickness: invalid ? 3.5 : 2.5));
            }
        var problemNodes = diagnostics.Where(d => d.ProviderId.HasValue).Select(d => d.ProviderId!.Value).ToHashSet();
        foreach (var connection in connections.Where(c => c.Color == Colors.OrangeRed))
        {
            problemNodes.Add(connection.Source.NodeId);
            problemNodes.Add(connection.Target.NodeId);
        }
        int index = 0;
        double rowY = 30, rowHeight = 0;
        foreach (var p in template.Providers)
        {
            var nodePorts = ports.Where(port => port.NodeId == p.Id).OrderBy(port => port.Kind).ThenBy(port => port.Key, StringComparer.Ordinal).Select(port =>
            {
                bool actual = providers.TryGetValue(p.Id, out var provider) && (port.Kind == GraphPortKind.Output
                    ? provider.GetOutputFields().ContainsKey(port.Key) : DynamicEffectBindings.InputFields(provider).ContainsKey(port.Key));
                return new GraphPort(port.Key, port.Kind, port.Key, actual ? PortTypeHelper.GetTypeColor(MermaidPortType(port)) : Colors.OrangeRed);
            }).ToArray();
            bool invalid = problemNodes.Contains(p.Id) || diagnostics.Any(d => d.ProviderId is null);
            var node = new BindableGraphNode
            {
                Id = p.Id, DisplayName = (invalid ? "⚠ " : "") + (string.IsNullOrWhiteSpace(p.Name) ? p.TypeName : p.Name),
                X = positions.TryGetValue(p.Id, out var point) ? point.X : 340 + index % 3 * 420,
                Y = positions.ContainsKey(p.Id) ? point.Y : rowY, Ports = nodePorts,
                BorderColor = invalid ? Colors.OrangeRed : Colors.Gray,
            };
            AddMermaidRepairNode(node);
            rowHeight = Math.Max(rowHeight, 100 + Math.Max(nodePorts.Count(port => port.Kind == GraphPortKind.Input), nodePorts.Count(port => port.Kind == GraphPortKind.Output)) * 28);
            if (++index % 3 == 0) { rowY += rowHeight + 40; rowHeight = 0; }
        }
        index = 0;
        foreach (var source in _mermaidRepairSources)
        {
            var node = new BindableGraphNode
            {
                Id = source.Value, DisplayName = source.Key, X = positions.TryGetValue(source.Value, out var point) ? point.X : 20,
                Y = positions.ContainsKey(source.Value) ? point.Y : 30 + index++ * 150,
                Ports = [new(EffectProviderAnchorExtensions.OutputKey, GraphPortKind.Output, "", PortTypeHelper.GetTypeColor(ValueProviderFrameContext.SourceType(source.Key)))],
                BorderColor = problemNodes.Contains(source.Value) ? Colors.OrangeRed : Colors.Gray,
            };
            AddMermaidRepairNode(node);
        }
        MermaidRepairGraph.SetConnections(connections);
        if (selected is { } active) MermaidRepairGraph.SetSelection([active], active);
        RefreshMermaidBindingActions();
        MermaidImportErrors.Children.Clear();
        MermaidImportErrorsScroll.IsVisible = diagnostics.Count != 0;
        if (diagnostics.Count != 0) ShowMermaidImportError(new EffectGroupValidationException(template, diagnostics));
        if (_mermaidRepairFitPending) Dispatcher.Dispatch(FitMermaidRepairWhenReady);
    }

    private EffectArgumentFieldType MermaidPortType(GraphPortReference port)
    {
        if (_mermaidRepairProviders.TryGetValue(port.NodeId, out var provider))
            return port.Kind == GraphPortKind.Output ? _mermaidRepairTypes!.GetOutputType(provider, port.Key)
                : _mermaidRepairTypes!.GetInputType(provider, port.Key);
        var source = _mermaidRepairSources.FirstOrDefault(s => s.Value == port.NodeId).Key;
        return source is null ? EffectArgumentFieldType.Unknown : ValueProviderFrameContext.SourceType(source);
    }

    private View MermaidDiagnosticLocation(EffectGroupValidationException validation, EffectBindingHelper.BindingDiagnostic diagnostic)
    {
        var button = new Button { Text = validation.NodeName(diagnostic), TextColor = Colors.OrangeRed,
            FontAttributes = FontAttributes.Bold, HorizontalOptions = LayoutOptions.Start, Padding = 0 };
        button.Clicked += (_, _) =>
        {
            if (diagnostic.ProviderId is not { } id || !_mermaidRepairNodes.TryGetValue(id, out var node)) return;
            MermaidRepairGraph.SetSelection([id], id, true);
            var viewport = MermaidRepairGraph.Viewport;
            MermaidRepairGraph.SetViewport(new(viewport.Scale, MermaidRepairGraph.Width / 2 - (node.X + 100) * viewport.Scale,
                MermaidRepairGraph.Height / 2 - (node.Y + 40) * viewport.Scale));
        };
        return button;
    }

    private void RefreshMermaidBindingActions()
    {
        MermaidRepairBindings.Children.Clear();
        if (MermaidRepairGraph.ActiveNodeId is not { } id || _mermaidRepairTemplate is null) return;
        foreach (var c in MermaidRepairGraph.Connections.Where(c => c.Source.NodeId == id || c.Target.NodeId == id))
        {
            var disconnect = new Button
            {
                Text = $"{PPLocalizedResources.EffectBindView_BoundSource_Disconnect} · {_mermaidRepairNodes.GetValueOrDefault(c.Source.NodeId)?.DisplayName} · {c.Source.Key} → {_mermaidRepairNodes.GetValueOrDefault(c.Target.NodeId)?.DisplayName} · {c.Target.Key}",
                FontSize = 11, TextColor = c.Color,
            };
            disconnect.Clicked += (_, _) => EditMermaidRepair(t =>
                t.Providers.Single(p => p.Id == c.Target.NodeId).AnchorsBindingState.Remove(c.Target.Key));
            MermaidRepairBindings.Children.Add(disconnect);
        }
        foreach (var port in _mermaidRepairTemplate.Groups.SelectMany(g => g.Ports).Where(p => p.ProviderId == id)
            .DistinctBy(p => (p.FieldId, p.IsInput)))
        {
            List<string> fields = _mermaidRepairProviders.TryGetValue(id, out var provider)
                ? EffectGroupHelper.DeclaredPorts(provider).Where(p => p.IsInput == port.IsInput).Select(p => p.FieldId).ToList() : [];
            var picker = new Picker { Title = $"{Localized.EffectGroup_PublicPorts} · {port.Name}", ItemsSource = fields,
                SelectedIndex = fields.IndexOf(port.FieldId) };
            picker.SelectedIndexChanged += (_, _) =>
            {
                if (picker.SelectedItem is not string field) return;
                EditMermaidRepair(t =>
                {
                    foreach (var p in t.Groups.SelectMany(g => g.Ports).Where(p => EffectGroupHelper.SamePort(p, port))) p.FieldId = field;
                });
            };
            var remove = new Button { Text = $"{Localized.DraftPage_ContextMenu_Delete} · {port.Name}", FontSize = 11 };
            remove.Clicked += (_, _) => EditMermaidRepair(t =>
            {
                foreach (var group in t.Groups) group.Ports.RemoveAll(p => EffectGroupHelper.SamePort(p, port));
            });
            var row = new Grid { ColumnDefinitions = new ColumnDefinitionCollection { new(GridLength.Star), new(GridLength.Auto) }, ColumnSpacing = 6 };
            row.Add(picker);
            row.Add(remove, 1, 0);
            MermaidRepairBindings.Children.Add(row);
        }
    }

    private void OnSyncMermaidPortsClicked(object sender, EventArgs e) => EditMermaidRepair(t =>
    {
        t.DeclaredPorts.RemoveAll(p => _mermaidRepairProviders.ContainsKey(p.ProviderId));
        t.DeclaredPorts.AddRange(_mermaidRepairProviders.Values.SelectMany(EffectGroupHelper.DeclaredPorts));
    });

    private void OnArrangeMermaidClicked(object sender, EventArgs e)
    {
        if (_mermaidRepairTemplate is null) return;
        _mermaidRepairNodes.Clear();
        _mermaidRepairFitPending = true;
        RefreshMermaidRepair(_mermaidRepairTemplate);
    }
}
