using projectFrameCut.Render.Effect;
using projectFrameCut.Render.Plugin;
using projectFrameCut.Render.RenderAPIBase.ClipAndTrack;
using projectFrameCut.Render.RenderAPIBase.EffectAndMixture;
using projectFrameCut.Render.RenderAPIBase.Project;
using projectFrameCut.Shared;
using System.Text.Json;

namespace projectFrameCut.DraftStuff;

public partial class DraftEffectBindingView
{
    private string _endpointName = "";
    private EffectArgumentFieldType _endpointType = EffectArgumentFieldType.Numeric;
    private static readonly EffectArgumentFieldType[] EndpointTypes =
    [
        EffectArgumentFieldType.Numeric, EffectArgumentFieldType.Integer, EffectArgumentFieldType.UnsignedInteger,
        EffectArgumentFieldType.Long, EffectArgumentFieldType.UnsignedLong, EffectArgumentFieldType.Boolean,
        EffectArgumentFieldType.String, EffectArgumentFieldType.Color, EffectArgumentFieldType.Size,
        EffectArgumentFieldType.Position, EffectArgumentFieldType.SizeAndPosition, EffectArgumentFieldType.IPicture,
        EffectArgumentFieldType.VectorPicture,
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
        ClipValuesPanel.Children.Clear();
        if (_clip is null || _page is null) return;
        var endpointTypes = EndpointTypes.Where(t => !t.IsContent() || t == _pipeline.GetContentType()).ToArray();
        if (!endpointTypes.Contains(_endpointType)) _endpointType = EffectArgumentFieldType.Numeric;
        ClipValuesPanel.Children.Add(new Label { Text = Localized.Effect_EndpointOutputs, FontAttributes = FontAttributes.Bold });
        var name = new Entry { Placeholder = Localized.Effect_EndpointName, Text = _endpointName };
        name.TextChanged += (_, e) => _endpointName = e.NewTextValue ?? "";
        ClipValuesPanel.Children.Add(name);
        var type = new Picker { Title = Localized.Effect_EndpointType, ItemsSource = endpointTypes.Select(PortTypeHelper.HumanizeTypeName).ToArray(), SelectedIndex = Array.IndexOf(endpointTypes, _endpointType) };
        type.SelectedIndexChanged += (_, _) =>
        {
            if (type.SelectedIndex >= 0) _endpointType = endpointTypes[type.SelectedIndex];
        };
        ClipValuesPanel.Children.Add(type);
        var create = new Button { Text = Localized.Effect_CreateEndpoint };
        create.Clicked += (_, _) => AddEndpointNode(new ClipValueExportProvider
        {
            Name = string.IsNullOrWhiteSpace(_endpointName) ? Localized.Effect_EndpointName : _endpointName.Trim(),
            ValueType = _endpointType,
        });
        ClipValuesPanel.Children.Add(create);
        foreach (var endpoint in _clip.EffectProviders?.Values.OfType<IClipValueEndpointProvider>().Where(p => p.IsExport && p.GetPipeline() == _pipeline) ?? [])
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
        ClipValuesPanel.Children.Add(new Label { Text = Localized.Effect_EndpointInputs, FontAttributes = FontAttributes.Bold });
        bool any = false;
        foreach (var source in _page.Clips.Values.Where(c => c.EffectProviders?.Values.Any(p => p is IClipValueEndpointProvider { IsExport: true }) == true))
        {
            if (source.Id != _clip.Id && !Precedes(source, _clip)) continue;
            foreach (var output in source.EffectProviders?.Values.OfType<IClipValueEndpointProvider>().Where(p => p.IsExport && p.CanPassTo(_pipeline, source.Id == _clip.Id)) ?? [])
            {
                any = true;
                ClipValuesPanel.Children.Add(new Label { Text = $"{source.DisplayName}: {output.Name} ({PortTypeHelper.HumanizeTypeName(output.ValueType)})" });
                var add = new Button { Text = Localized.Effect_AddEndpointInput, IsEnabled = output.Enabled };
                add.Clicked += (_, _) => AddEndpointNode(new ClipValueImportProvider
                {
                    Name = $"{source.DisplayName}: {output.Name}", SourceClipId = source.Id,
                    SourceEndpointId = output.Id, ValueType = output.ValueType,
                });
                ClipValuesPanel.Children.Add(add);
            }
        }
        if (!any) ClipValuesPanel.Children.Add(new Label { Text = Localized.Effect_NoAvailableEndpoints });
        Dispatcher.Dispatch(async () => await ClipValuesScrollView.ScrollToAsync(0, scroll, false));
    }

    private void AddEndpointNode(ClipValueEndpointProvider provider)
    {
        if (_clip is null) return;
        provider.SetPipeline(_pipeline);
        _clip.EffectProviders ??= new();
        _clip.EffectProviders[provider.Id] = provider;
        var node = new NodeViewModel { Id = provider.Id, Kind = NodeKind.Effect, Provider = provider, DisplayName = provider.Name };
        node.BuildPortsFromProvider();
        var position = FindNonOverlappingPosition(node, 250 + _nodes.Count * 30, 150);
        node.X = position.X;
        node.Y = position.Y;
        provider.MetaData["__DraftEffectBindingView_InteractiveEditorX__"] = node.X;
        provider.MetaData["__DraftEffectBindingView_InteractiveEditorY__"] = node.Y;
        AddNode(node);
        OnBindingConfigurationChanged();
        RebuildConnections();
        UpdateClipValuesPanel();
        Log($"Added clip endpoint {provider.Id} ({provider.TypeName}) on {_clip.Id}.");
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
            var endpointTypes = EndpointTypes.Where(t => !t.IsContent() || t == endpoint.GetPipeline().GetContentType()).ToArray();
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
                Text = $"{Localized.Effect_EndpointSource}: {(source is null ? Localized.Effect_EndpointSourceMissing : $"{source.DisplayName} / {output?.Name ?? Localized.Effect_EndpointMissing}")}\n{Localized.Effect_EndpointType}: {endpoint.ValueType}",
            });
        }
    }

    private void UpdateEndpointNode(NodeViewModel node)
    {
        node.DisplayName = node.Provider!.Name;
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
            : EffectBindingHelper.ValidateBindings(providers, _fieldTypes).ToList();
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
        else if (output.GetMainInputSource() == IEffectProvider.NoConnectionGUID.ToString()) status = Localized.Effect_EndpointDisconnected;
        else if (output.ValueType != input.ValueType || !output.CanPassTo(input.GetPipeline(), source.Id == _clip?.Id)) status = Localized.Effect_EndpointTypeChanged;
        else if (_clip is not null && source.Id != _clip.Id && !Precedes(source, _clip)) status = Localized.Effect_EndpointOrderInvalid;
        else return null;
        return $"「{(output is { IsExport: true } ? $"{source.DisplayName} / {output.Name}" : source.DisplayName)}」：{status}";
    }

    private IClip[] CreateEndpointPreviewClips()
    {
        if (_clip is null || _page is null) return [];
        var clips = new List<IClip>();
        try
        {
            foreach (var element in _page.Clips.Values.Where(c => c.ClipType is not (ClipMode.AudioClip or ClipMode.MarkingClip)))
            {
                var dto = DraftImportAndExportHelper.ExportClipElementFromDraftPage(_page, element, rebuildEffects: false);
                clips.Add(PluginManager.CreateClip(JsonSerializer.SerializeToElement(dto)));
            }
            return clips.ToArray();
        }
        catch
        {
            foreach (var clip in clips)
            {
                try { clip.Dispose(); }
                catch (Exception ex) { Log(ex, "Release endpoint preview clip", this); }
            }
            throw;
        }
    }

    private sealed class EndpointPreviewClips(IClip[] clips) : IDisposable
    {
        public IClip[] Clips => clips;
        public void Dispose()
        {
            foreach (var clip in clips)
            {
                try { clip.Dispose(); }
                catch (Exception ex) { Log(ex, "Release endpoint preview clip", this); }
            }
        }
    }
}
