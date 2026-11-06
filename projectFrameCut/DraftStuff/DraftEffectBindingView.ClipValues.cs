using projectFrameCut.Render.Effect;
using projectFrameCut.Render.Plugin;
using projectFrameCut.Render.RenderAPIBase.ClipAndTrack;
using projectFrameCut.Render.RenderAPIBase.EffectAndMixture;
using projectFrameCut.Shared;

namespace projectFrameCut.DraftStuff;

public partial class DraftEffectBindingView
{
    private string _endpointName = "";
    private EffectArgumentFieldType _endpointType = EffectArgumentFieldType.Numeric;
    private bool _creatingVariable = true;
    private static readonly EffectArgumentFieldType[] EndpointTypes =
    [
        EffectArgumentFieldType.Numeric, EffectArgumentFieldType.Integer, EffectArgumentFieldType.UnsignedInteger,
        EffectArgumentFieldType.Long, EffectArgumentFieldType.UnsignedLong, EffectArgumentFieldType.Boolean,
        EffectArgumentFieldType.String, EffectArgumentFieldType.Color, EffectArgumentFieldType.Size,
        EffectArgumentFieldType.Position, EffectArgumentFieldType.SizeAndPosition, EffectArgumentFieldType.IPicture,
        EffectArgumentFieldType.VectorPicture, EffectArgumentFieldType.AudioSample,
    ];

    private bool Precedes(ClipElementUI source, ClipElementUI target)
    {
        if (_page is null || source.Id == target.Id) return false;
        try
        {
            var s = DraftImportAndExportHelper.ExportClipElementFromDraftPage(_page, source, rebuildEffects: false);
            var t = DraftImportAndExportHelper.ExportClipElementFromDraftPage(_page, target, rebuildEffects: false);
            return ClipLayerOrder.Compare(s.LayerIndex, s.SubLayerIndex, t.LayerIndex, t.SubLayerIndex) < 0;
        }
        catch (KeyNotFoundException ex)
        {
            Log(ex, "Resolve endpoint clip order", this);
            return false;
        }
    }

    private void UpdateClipValuesPanel()
    {
        double scroll = ClipValuesScrollView.ScrollY;
        ClipValueCreationPanel.Children.Clear();
        ClipValuesPanel.Children.Clear();
        ClipValuesContent.IsVisible = _clip is not null && _page is not null;
        if (_clip is null || _page is null) return;
        var endpointTypes = EndpointTypes.Where(t => (!_creatingVariable || EffectVariableProvider.SupportsType(t))
            && (!t.IsContent() || t == _stage.GetContentType())
            && (t != EffectArgumentFieldType.AudioSample || ValueProviderFrameContext.SupportsAudioSample(_stage))).ToArray();
        if (!endpointTypes.Contains(_endpointType)) _endpointType = EffectArgumentFieldType.Numeric;
        var kind = new Picker { ItemsSource = new[] { Localized.Effect_Variables, Localized.Effect_ValuePassThrough }, SelectedIndex = _creatingVariable ? 0 : 1 };
        kind.SelectedIndexChanged += (_, _) =>
        {
            _creatingVariable = kind.SelectedIndex == 0;
            UpdateClipValuesPanel();
        };
        ClipValueCreationPanel.Children.Add(kind);
        EndpointCrossClipPanel.IsVisible = !_creatingVariable;
        CreateValueEndpointButton.Text = _creatingVariable ? Localized.Effect_CreateVariable : Localized.Effect_CreateEndpoint;
        CreateValueEndpointButton.IsEnabled = !_creatingVariable || CanManageVariables;
        if (_creatingVariable && !CanManageVariables)
            ClipValueCreationPanel.Children.Add(new Label { Text = Localized.Effect_VariableRequiresDynamic, FontSize = 12 });
        var name = new Entry { Placeholder = Localized.Effect_EndpointName, Text = _endpointName };
        name.TextChanged += (_, e) => _endpointName = e.NewTextValue ?? "";
        ClipValueCreationPanel.Children.Add(name);
        var type = new Picker { Title = Localized.Effect_EndpointType, ItemsSource = endpointTypes.Select(PortTypeHelper.HumanizeTypeName).ToArray(), SelectedIndex = Array.IndexOf(endpointTypes, _endpointType) };
        type.SelectedIndexChanged += (_, _) =>
        {
            if (type.SelectedIndex >= 0) _endpointType = endpointTypes[type.SelectedIndex];
        };
        ClipValueCreationPanel.Children.Add(type);
        ClipValuesPanel.Children.Add(new Label { Text = Localized.Effect_EndpointInputs, FontAttributes = FontAttributes.Bold });
        AddVariableRows(ClipValuesPanel);
        foreach (var endpoint in _clip.EffectProviders?.Values.OfType<IClipValueEndpointProvider>().Where(p => p.IsExport && IsCurrentPipelineProvider(p)) ?? [])
        {
            var row = new HorizontalStackLayout { Spacing = 4 };
            var select = new Button { Text = $"{endpoint.Name} ({PortTypeHelper.HumanizeTypeName(endpoint.ValueType)})", HorizontalOptions = LayoutOptions.Fill };
            select.Clicked += (_, _) =>
            {
                if (_nodes.TryGetValue(endpoint.Id, out var node))
                {
                    SelectNode(node);
                    RightTabView.SelectedIndex = 0;
                }
            };
            row.Children.Add(select);
            var remove = new Button { Text = Localized.DraftPage_ContextMenu_Delete };
            remove.Clicked += (_, _) =>
            {
                if (_nodes.TryGetValue(endpoint.Id, out var node)) RemoveEffect(node);
            };
            row.Children.Add(remove);
            ClipValuesPanel.Children.Add(row);
        }
        bool any = GetVariables().Count > 0 || _clip.EffectProviders?.Values.OfType<IClipValueEndpointProvider>().Any(p => p.IsExport && IsCurrentPipelineProvider(p)) == true;
        foreach (var (source, output) in GetAvailableClipValueInputs())
        {
            any = true;
            ClipValuesPanel.Children.Add(new Label { Text = $"{source.DisplayName} / {EffectStageUI.Name(output.Stage)}: {output.Name} ({PortTypeHelper.HumanizeTypeName(output.ValueType)})" });
            var add = new Button { Text = Localized.Effect_AddEndpointInput, IsEnabled = output.Enabled };
            add.Clicked += (_, _) => AddEndpointInput(source, output);
            ClipValuesPanel.Children.Add(add);
        }
        if (!any) ClipValuesPanel.Children.Add(new Label { Text = Localized.Effect_NoAvailableEndpoints });
        UpdateSourcesAndOutputsPanel();
        Dispatcher.Dispatch(async () => await ClipValuesScrollView.ScrollToAsync(0, scroll, false));
    }

    private IEnumerable<(ClipElementUI Source, IClipValueEndpointProvider Output)> GetAvailableClipValueInputs()
    {
        if (_clip is null || _page is null) yield break;
        foreach (var source in _page.Clips.Values.Where(c => c.EffectProviders?.Values.OfType<IClipValueEndpointProvider>().Any(p => p.IsExport) == true))
        {
            if (source.Id != _clip.Id && !Precedes(source, _clip)) continue;
            foreach (var output in source.EffectProviders?.Values.OfType<IClipValueEndpointProvider>()
                .Where(p => p.IsExport && p.CanPassTo(_stage, source.Id == _clip.Id, _clip.EffectProviders?.Values)) ?? [])
                yield return (source, output);
        }
    }

    private void AddEndpointInput(ClipElementUI source, IClipValueEndpointProvider output) => AddEndpointNode(new ClipValueImportProvider
    {
        Name = $"{source.DisplayName}: {output.Name}", SourceClipId = source.Id,
        SourceEndpointId = output.Id, ValueType = output.ValueType,
    });

    private void OnCreateClipValueEndpointClicked(object? sender, EventArgs e)
    {
        if (_creatingVariable) CreateVariable();
        else AddEndpointNode(new ClipValueExportProvider
        {
            Name = string.IsNullOrWhiteSpace(_endpointName) ? Localized.Effect_EndpointName : _endpointName.Trim(),
            ValueType = _endpointType,
            AllowCrossClip = EndpointAllowCrossClipCheckBox.IsChecked,
        });
    }

    private void AddEndpointNode(IEffectProvider provider)
    {
        if (_clip is null) return;
        provider.Stage = _stage;
        AssignCurrentPipeline(provider);
        _clip.EffectProviders ??= new();
        _clip.EffectProviders[provider.Id] = provider;
        var node = new NodeViewModel { Id = provider.Id, Kind = NodeKind.Effect, Provider = provider, DisplayName = ProviderNodeName(provider) };
        node.BuildPortsFromProvider();
        var position = FindNonOverlappingPosition(node, 250 + _nodes.Count * 30, 150);
        node.X = position.X;
        node.Y = position.Y;
        provider.MetaData[ProviderPositionKey("X")] = node.X;
        provider.MetaData[ProviderPositionKey("Y")] = node.Y;
        AddNode(node);
        OnBindingConfigurationChanged();
        RebuildConnections();
        UpdateClipValuesPanel();
        Log($"Added value node {provider.Id} ({provider.TypeName}) on {_clip.Id}.");
    }

    private void BuildEndpointProperties(NodeViewModel node, IClipValueEndpointProvider endpoint)
    {
        var name = new Entry { Placeholder = Localized.Effect_EndpointName, Text = endpoint.Name };
        name.Unfocused += (_, _) =>
        {
            if (string.IsNullOrWhiteSpace(name.Text) || name.Text == endpoint.Name) return;
            endpoint.Name = name.Text.Trim();
            UpdateEndpointNode(node);
        };
        PropertiesPanel.Children.Add(name);
        PropertiesPanel.Children.Add(new Label { Text = Localized.DraftSettingPage_Advanced_Enabled });
        var enabled = new Switch { IsToggled = endpoint.Enabled };
        enabled.Toggled += (_, e) =>
        {
            endpoint.Enabled = e.Value;
            UpdateEndpointNode(node);
        };
        PropertiesPanel.Children.Add(enabled);
        if (endpoint.IsExport)
        {
            var allowCrossClip = new CheckBox { IsChecked = endpoint.AllowCrossClip };
            allowCrossClip.CheckedChanged += (_, e) =>
            {
                endpoint.AllowCrossClip = e.Value;
                Log($"Changed clip endpoint {endpoint.Id} cross-clip pass-through to {e.Value} on {_clip?.Id}.");
                UpdateEndpointNode(node);
            };
            PropertiesPanel.Children.Add(new HorizontalStackLayout
            {
                Spacing = 6,
                Children = { allowCrossClip, new Label { Text = Localized.Effect_EndpointAllowCrossClip, VerticalOptions = LayoutOptions.Center } },
            });
            var endpointTypes = EndpointTypes.Where(t => !t.IsContent() || t == endpoint.Stage.GetContentType()).ToArray();
            var type = new Picker { Title = Localized.Effect_EndpointType, ItemsSource = endpointTypes.Select(PortTypeHelper.HumanizeTypeName).ToArray(), SelectedIndex = Array.IndexOf(endpointTypes, endpoint.ValueType) };
            type.SelectedIndexChanged += (_, _) =>
            {
                if (type.SelectedIndex < 0) return;
                endpoint.ValueType = endpointTypes[type.SelectedIndex];
                UpdateEndpointNode(node);
            };
            PropertiesPanel.Children.Add(type);
        }
        else
        {
            var source = _page?.Clips.GetValueOrDefault(endpoint.SourceClipId);
            var output = source?.EffectProviders?.GetValueOrDefault(endpoint.SourceEndpointId);
            PropertiesPanel.Children.Add(new Label
            {
                Text = $"{Localized.Effect_EndpointSource}: {(source is null ? Localized.Effect_EndpointSourceMissing : $"{source.DisplayName} / {output?.Name ?? Localized.Effect_EndpointMissing}")}\n{Localized.Effect_EndpointType}: {PortTypeHelper.HumanizeTypeName(endpoint.ValueType)}",
            });
        }
    }

    private void UpdateEndpointNode(NodeViewModel node)
    {
        node.DisplayName = ProviderNodeName(node.Provider!);
        node.BuildPortsFromProvider();
        RecreateNodeView(node);
        RebuildConnections();
        OnBindingConfigurationChanged();
        UpdateClipValuesPanel();
    }

    private IReadOnlyList<EffectBindingHelper.BindingDiagnostic> GetAllBindingDiagnostics()
    {
        if (_clip?.EffectProviders is not { } providers) return [];
        var diagnostics = _fieldTypes is null ? new List<EffectBindingHelper.BindingDiagnostic>()
            : EffectBindingHelper.ValidateBindings(providers, _fieldTypes, DynamicEffectCalculation.Enabled(_clip.ExtraData)).ToList();
        diagnostics.AddRange(_previewDiagnostics);
        foreach (var endpoint in providers.Values.OfType<IClipValueEndpointProvider>())
        {
            if (endpoint.IsExport)
            {
                if (endpoint.GetMainInputSource() == IEffectProvider.NoConnectionGUID.ToString())
                    diagnostics.Add(new(endpoint.Id, "UnboundClipEndpoint", Localized.Effect_EndpointOutputDisconnected));
            }
            else if (GetEndpointInputStatus(endpoint) is { } status)
                diagnostics.Add(new(endpoint.Id, "InvalidClipEndpoint", status));
        }
        return diagnostics;
    }

    private string? GetEndpointInputStatus(IClipValueEndpointProvider input)
    {
        var source = _page?.Clips.GetValueOrDefault(input.SourceClipId);
        if (source is null) return Localized.Effect_EndpointSourceMissing;
        var output = source.EffectProviders?.GetValueOrDefault(input.SourceEndpointId) as IClipValueEndpointProvider;
        string? status;
        if (output is not { IsExport: true }) status = Localized.Effect_EndpointMissing;
        else if (!output.Enabled) status = Localized.Effect_EndpointDisabled;
        else if (source.Id != _clip?.Id && !output.AllowCrossClip) status = Localized.Effect_EndpointCrossClipDisabled;
        else if (output.GetMainInputSource() == IEffectProvider.NoConnectionGUID.ToString()) status = Localized.Effect_EndpointDisconnected;
        else if (output.ValueType != input.ValueType || !output.CanPassTo(input.Stage, source.Id == _clip?.Id, _clip?.EffectProviders?.Values)) status = Localized.Effect_EndpointTypeChanged;
        else if (_clip is not null && source.Id != _clip.Id && !Precedes(source, _clip)) status = Localized.Effect_EndpointOrderInvalid;
        else return null;
        return $"「{(output is { IsExport: true } ? $"{source.DisplayName} / {output.Name}" : source.DisplayName)}」：{status}";
    }
}
