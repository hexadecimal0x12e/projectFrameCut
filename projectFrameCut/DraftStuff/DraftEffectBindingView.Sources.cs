using projectFrameCut.Render.Compose;
using projectFrameCut.Render.Effect;
using projectFrameCut.Render.RenderAPIBase.ClipAndTrack;
using projectFrameCut.Render.RenderAPIBase.EffectAndMixture;
using projectFrameCut.Shared;
using static LocalizedResources.SimpleLocalizerBaseGeneratedHelper_PropertyPanel;

namespace projectFrameCut.DraftStuff;

public partial class DraftEffectBindingView
{
    private sealed record BuiltInSource(Guid Id, string Source, string Name);
    private sealed record SourceOutputEntry(string Key, string Name);

    private static BuiltInSource[] BuiltInSources =>
    [
        new(new("f151dd63-6821-4f7b-a71a-9bd8a035e763"), ValueProviderFrameContext.BuiltInAudioSampleProviderId, Localized.Effect_GlobalMix),
        new(new("f151dd63-6821-4f7b-a71a-9bd8a035e764"), ValueProviderFrameContext.BuiltInFrameProviderId, Localized.Effect_CurrentFrame),
        new(new("f151dd63-6821-4f7b-a71a-9bd8a035e765"), ValueProviderFrameContext.BuiltInClipFrameProviderId, Localized.Effect_ClipCurrentFrame),
        new(new("f151dd63-6821-4f7b-a71a-9bd8a035e766"), ValueProviderFrameContext.BuiltInProjectProgressProviderId, Localized.Effect_ProjectProgress),
        new(new("f151dd63-6821-4f7b-a71a-9bd8a035e767"), ValueProviderFrameContext.BuiltInProgressProviderId, Localized.Effect_ClipProgress),
    ];

    private bool HasContentPipeline => _clip?.ClipType != ClipMode.AudioClip
        && _pipeline is EffectStage.Render or EffectStage.Text or EffectStage.VectorContent or EffectStage.Composition;

    private string TerminalSourceOutputKey(string field) => _pipeline == EffectStage.Render
        ? field switch
        {
            RenderOutputProvider.AudioKey => "audio",
            RenderOutputProvider.PositionKey => "position",
            _ => "terminal:" + field,
        }
        : "terminal:" + field;

    private bool IsSourceOutputEnabled(string key)
    {
        if (bool.TryParse(_clip?.ExtraData.GetValueOrDefault(StagePositionKey(key, "Enabled"))?.ToString(), out var enabled)) return enabled;
        if (key.StartsWith("builtin://", StringComparison.Ordinal)
            && _clip?.EffectProviders?.Values.Any(p => p.Stage == _pipeline && p.AnchorsBindingState.Values.Contains(key)) == true) return true;
        // Earlier stages already exposed the global audio source before this panel existed.
        return !key.StartsWith("builtin://", StringComparison.Ordinal)
            || key == ValueProviderFrameContext.BuiltInAudioSampleProviderId && _pipeline != EffectStage.Render;
    }

    private IEnumerable<SourceOutputEntry> GetSourceOutputEntries()
    {
        if (_clip is null) yield break;
        if (HasContentPipeline)
            yield return new("content", _pipeline == EffectStage.Composition ? Localized.Effect_PictureOutput
                : $"{(_pipeline == EffectStage.Render ? PPLocalizedResources.EffectBind_SourcePicture : EffectStageUI.Name(_pipeline))} → {PPLocalizedResources.EffectBind_FinalResult}");
        if (_pipeline == EffectStage.Render)
        {
            if (HasClipAudio) yield return new("audio", $"{Localized.Effect_RenderSourceAudio} → {Localized.Effect_RenderFinalAudio}");
            if (_clip.ClipType != ClipMode.AudioClip)
                yield return new("position", $"{Localized.Effect_RenderSourcePosition} → {Localized.Effect_RenderFinalPosition}");
        }
        if (_clip.EffectProviders is { } providers)
            foreach (var terminal in EffectStages.GetTerminals(_pipeline, providers.Values))
            {
                if (_pipeline == EffectStage.Render && terminal.Id is RenderOutputProvider.AudioKey or RenderOutputProvider.PositionKey) continue;
                yield return new(TerminalSourceOutputKey(terminal.Id), terminal.Id == RenderOutputProvider.ShouldRenderKey
                    ? Localized.Effect_RenderShouldRender : HumanizePortName(terminal.Id));
            }
        foreach (var source in BuiltInSources)
            if (source.Source != ValueProviderFrameContext.BuiltInAudioSampleProviderId || ValueProviderFrameContext.SupportsAudioSample(_pipeline))
                yield return new(source.Source, source.Name);
    }

    private string? GetSourceOutputKey(NodeViewModel node)
    {
        if (node.TerminalField is { } field) return TerminalSourceOutputKey(field);
        if (node.Kind is NodeKind.Input or NodeKind.Output && node.Group is null) return "content";
        if (node.Id == AudioSampleNodeId) return "audio";
        if (node.Id == PositionNodeId) return "position";
        return BuiltInSources.FirstOrDefault(s => s.Id == node.Id)?.Source;
    }

    private void AddSystemNodes()
    {
        var available = GetSourceOutputEntries().Select(e => e.Key).ToHashSet();
        foreach (var node in _nodes.Values.Where(n => GetSourceOutputKey(n) is { } key
            && (!available.Contains(key) || !IsSourceOutputEnabled(key)
                || n.TerminalField is not null && _clip?.EffectProviders?.ContainsKey(n.Provider!.Id) != true)).ToArray())
            RemoveSourceOutputNode(node);
        AddContentNodes();
        if (_pipeline == EffectStage.Render && HasClipAudio && IsSourceOutputEnabled("audio") && !_nodes.ContainsKey(AudioSampleNodeId))
            AddSourceNode(AudioSampleNodeId, ValueProviderFrameContext.ClipAudioProviderId, Localized.Effect_RenderSourceAudio, "audioSource", 350);
        AddRenderPositionSource();
        AddStageTerminals();
        foreach (var source in BuiltInSources)
            if (IsSourceOutputEnabled(source.Source) && !_nodes.ContainsKey(source.Id)
                && (source.Source != ValueProviderFrameContext.BuiltInAudioSampleProviderId || ValueProviderFrameContext.SupportsAudioSample(_pipeline)))
                AddSourceNode(source.Id, source.Source, source.Name, source.Source, 800);
    }

    private void AddSourceNode(Guid id, string source, string name, string layoutKey, double y)
    {
        var type = ValueProviderFrameContext.SourceType(source);
        bool legacyAudio = source == ValueProviderFrameContext.ClipAudioProviderId
            || source == ValueProviderFrameContext.BuiltInAudioSampleProviderId && _pipeline != EffectStage.Render;
        var node = new NodeViewModel
        {
            Id = id, Kind = NodeKind.AudioSample, Provider = null, DisplayName = name,
            X = GetExtraDataDouble(StagePositionKey(layoutKey, "X"), legacyAudio ? GetExtraDataDouble(StagePositionKey("audio", "X"), 50) : 50),
            Y = GetExtraDataDouble(StagePositionKey(layoutKey, "Y"), legacyAudio ? GetExtraDataDouble(StagePositionKey("audio", "Y"), 350) : y),
            OutputPorts = [new NodePort
            {
                Id = id, Kind = PortKind.AnchorOutput, Key = source,
                FieldType = type, DeclaredFieldType = type, DisplayName = name, Index = 0,
            }],
        };
        if (!_clip!.ExtraData.ContainsKey(StagePositionKey(layoutKey, "X"))
            && (!legacyAudio || !_clip.ExtraData.ContainsKey(StagePositionKey("audio", "X"))))
        {
            var position = FindNonOverlappingPosition(node, node.X, node.Y);
            node.X = position.X;
            node.Y = position.Y;
        }
        AddNode(node);
    }

    private void SetSourceOutputEnabled(string key, bool enabled)
    {
        if (_clip is null) return;
        SaveStageLayout();
        _clip.ExtraData[StagePositionKey(key, "Enabled")] = enabled;
        if (!enabled)
            foreach (var node in _nodes.Values.Where(n => GetSourceOutputKey(n) == key).ToArray())
            {
                if (node.TerminalField is not null)
                {
                    foreach (var field in node.TerminalFields) RemoveUIBindingsTo(BindingIdOf(node), field, UIBindingKind.Value);
                    if (node.Provider is RenderOutputProvider && node.TerminalField == RenderOutputProvider.ShouldRenderKey)
                        node.Provider.Fields = new()
                        {
                            [RenderOutputProvider.ShouldRenderKey] = new StaticEffectArgumentField
                            {
                                Id = RenderOutputProvider.ShouldRenderKey, FieldType = EffectArgumentFieldType.Boolean, Value = true,
                            },
                        };
                }
                else
                {
                    RemoveUIBindingsFrom(BindingIdOf(node), stage: _pipeline);
                    RemoveUIBindingsTo(BindingIdOf(node), null);
                }
                RemoveSourceOutputNode(node);
            }
        if (enabled && _clip.EffectProviders is { } providers)
        {
            if (key == "content") EffectBindingHelper.SetFinalOutput(providers, null, _pipeline);
            else if (key is "audio" or "position")
                RenderOutputProvider.Ensure(providers).SetFieldBinding(key == "audio" ? RenderOutputProvider.AudioKey : RenderOutputProvider.PositionKey,
                    key == "audio" ? ValueProviderFrameContext.ClipAudioProviderId : ValueProviderFrameContext.ClipPositionProviderId);
        }
        UpdateProviderNodes();
        OnBindingConfigurationChanged();
        RebuildConnections();
        RefreshSelectedNode();
        UpdateSourcesAndOutputsPanel();
    }

    private void RemoveSourceOutputNode(NodeViewModel node)
    {
        _nodes.Remove(node.Id);
        if (node.View is not null) NodesContainer.Children.Remove(node.View);
        if (_selectedNode == node) _selectedNode = null;
        if (_inputNode == node) _inputNode = null;
        if (_outputNode == node) _outputNode = null;
        if (_drawable.DragSourceNode == node)
        {
            _drawable.DragSourceNode = null;
            _drawable.DragPoint = null;
        }
    }

    private void OnCanvasTapped(object? sender, TappedEventArgs e)
    {
        if (_isDraggingNodeOrPort) return;
        if (e.GetPosition(NodesContainer) is { } point
            && _nodes.Values.Any(n => n.View?.IsVisible == true && GetNodeBounds(n, n.X, n.Y).Contains(point))) return;
        ShowSourcesAndOutputs();
    }

    private void ShowSourcesAndOutputs()
    {
        _selectedNode = null;
        RefreshGroupSelection();
        UpdateSourcesAndOutputsPanel();
        RightTabView.SelectedIndex = 0;
    }

    private void UpdateSourcesAndOutputsPanel()
    {
        bool selected = _selectedNode is not null;
        PropertiesTabHeader.Text = selected ? Localized.PropertyPanel : Localized.Effect_SourcesAndOutputs;
        SourcesAndOutputsPanel.IsVisible = !selected;
        PropertiesPanel.IsVisible = selected;
        AfterEffectBigPreview.IsVisible = selected;
        if (selected) return;
        PropertiesPanel.Children.Clear();
        AfterEffectBigPreview.Source = null;
        SourcesAndOutputsPanel.Children.Clear();
        foreach (var entry in GetSourceOutputEntries())
        {
            bool enabled = IsSourceOutputEnabled(entry.Key);
            AddSourceOutputRow(entry.Name, enabled, () => SetSourceOutputEnabled(entry.Key, !enabled));
        }
        if (_clip is null) return;
        SourcesAndOutputsPanel.Children.Add(new Label { Text = Localized.Effect_ValuePassThrough, FontAttributes = FontAttributes.Bold });
        foreach (var endpoint in _clip.EffectProviders?.Values.OfType<IClipValueEndpointProvider>().Where(p => p.Stage == _pipeline) ?? [])
            AddSourceOutputRow(endpoint.Name, true, () =>
            {
                if (_nodes.TryGetValue(endpoint.Id, out var node)) RemoveEffect(node);
            });
        foreach (var (source, output) in GetAvailableClipValueInputs())
            AddSourceOutputRow($"{source.DisplayName} / {EffectStageUI.Name(output.Stage)}: {output.Name} ({PortTypeHelper.HumanizeTypeName(output.ValueType)})",
                false, () => AddEndpointInput(source, output), output.Enabled);
        var create = new Button { Text = Localized.Effect_CreateEndpoint };
        create.Clicked += (_, _) => RightTabView.SelectedIndex = 2;
        SourcesAndOutputsPanel.Children.Add(create);
    }

    private void AddSourceOutputRow(string name, bool added, Action action, bool enabled = true)
    {
        var row = new Grid { ColumnDefinitions = new ColumnDefinitionCollection { new(GridLength.Star), new(GridLength.Auto) }, ColumnSpacing = 6 };
        row.Children.Add(new Label { Text = name, VerticalOptions = LayoutOptions.Center });
        var button = new Button { Text = added ? Localized.DraftPage_ContextMenu_Delete : Localized.Effect_AddSourceOutput, IsEnabled = enabled };
        button.Clicked += (_, _) => action();
        Grid.SetColumn(button, 1);
        row.Children.Add(button);
        SourcesAndOutputsPanel.Children.Add(row);
    }

    private void UpdateBuiltInPreviews(IClip clip, uint frame, CancellationToken token)
    {
        using var clipScope = ValueProviderFrameContext.PushClip(clip);
        using var frameScope = ValueProviderFrameContext.PushFrame(frame,
            Math.Clamp(((float)frame - clip.StartFrame) / Math.Max(1u, clip.GetEffectiveDuration()), 0, 1), false, token);
        foreach (var source in BuiltInSources)
            if (_nodes.TryGetValue(source.Id, out var node)) node.PreviewValue = RenderValueText(ValueProviderFrameContext.Get(source.Source));
    }
}
