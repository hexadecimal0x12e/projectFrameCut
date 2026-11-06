using projectFrameCut.ApplicationPluginBase.Effect;
using projectFrameCut.Render.Effect;
using projectFrameCut.Render.RenderAPIBase.EffectAndMixture;
using projectFrameCut.Shared;

namespace projectFrameCut.DraftStuff;

public partial class DraftEffectBindingView
{
    private IReadOnlyList<EffectBindingHelper.BindingDiagnostic> _previewDiagnostics = [];
    private readonly VerticalStackLayout _selectedNodeDiagnostics = new() { Spacing = 4 };

    private void UpdateSelectedNodeDiagnostics()
    {
        _selectedNodeDiagnostics.Children.Clear();
        if (_selectedNode is null) return;
        foreach (var diagnostic in GetBindingDiagnostics(_selectedNode))
            _selectedNodeDiagnostics.Children.Add(new Label { Text = FormatBindingDiagnostic(diagnostic), TextColor = Colors.OrangeRed, FontSize = 12 });
    }

    private IReadOnlyList<EffectBindingHelper.BindingDiagnostic> GetBindingDiagnostics(
        NodeViewModel node, IReadOnlyList<EffectBindingHelper.BindingDiagnostic>? diagnostics = null)
    {
        diagnostics ??= _bindingDiagnostics;
        if (node.Group is { } group)
        {
            var members = EffectGroupHelper.Members(group, _groups);
            return diagnostics.Where(d => d.ProviderId is { } id && members.Contains(id)).ToArray();
        }
        if (node.Provider is { } provider)
            return diagnostics.Where(d => d.ProviderId == provider.Id
                && (node.TerminalField is null || d.FieldId is null || node.TerminalFields.Contains(d.FieldId))).ToArray();
        return [];
    }

    private string DiagnosticNodeName(EffectBindingHelper.BindingDiagnostic diagnostic)
    {
        if (diagnostic.ProviderId is not { } id || _clip?.EffectProviders?.GetValueOrDefault(id) is not { } provider)
            return Localized.Effect_BindingsGraph;
        var name = $"{EffectStageUI.Name(provider.Stage)} / {ProviderNodeName(provider)}";
        return diagnostic.FieldId is { } field ? $"{name} / {EffectProviderUIHelper.FieldName(provider, field, true, field)}" : name;
    }

    private string BindingDiagnosticMessage(EffectBindingHelper.BindingDiagnostic diagnostic)
    {
        var message = diagnostic.Message;
        if (_clip?.EffectProviders is { } providers)
            foreach (var provider in providers.Values)
                message = message.Replace(provider.Id.ToString(), ProviderNodeName(provider), StringComparison.OrdinalIgnoreCase);
        return message;
    }

    private string FormatBindingDiagnostic(EffectBindingHelper.BindingDiagnostic diagnostic) =>
        $"{DiagnosticNodeName(diagnostic)}: {BindingDiagnosticMessage(diagnostic)}";

    private void OnBindingDiagnosticsClicked(object sender, EventArgs e) =>
        BindingDiagnosticsScroll.IsVisible = !BindingDiagnosticsScroll.IsVisible;

    private void UpdateBindingDiagnosticsPanel()
    {
        BindingDiagnosticsList.Children.Clear();
        BindingDiagnosticsPanel.IsVisible = _bindingDiagnostics.Count > 0;
        BindingDiagnosticsButton.Text = $"⚠ {Localized._Error} ({_bindingDiagnostics.Count})";
        foreach (var diagnostic in _bindingDiagnostics)
        {
            var title = new Button
            {
                Text = DiagnosticNodeName(diagnostic), TextColor = Colors.OrangeRed, FontSize = 12,
                HorizontalOptions = LayoutOptions.Start,
                IsEnabled = diagnostic.ProviderId is { } id && _clip?.EffectProviders?.ContainsKey(id) == true,
            };
            title.Clicked += (_, _) => FocusBindingDiagnostic(diagnostic);
            BindingDiagnosticsList.Children.Add(new VerticalStackLayout
            {
                Spacing = 2,
                Children =
                {
                    title,
                    new Label { Text = BindingDiagnosticMessage(diagnostic), TextColor = Colors.White, FontSize = 12 },
                },
            });
        }
    }

    private void ShowNodeDiagnostics(NodeViewModel node)
    {
        BindingDiagnosticsScroll.IsVisible = true;
        SelectNode(node, force: true);
        RightTabView.SelectedIndex = 0;
    }

    private void FocusBindingDiagnostic(EffectBindingHelper.BindingDiagnostic diagnostic)
    {
        if (_clip?.EffectProviders is not { } providers || diagnostic.ProviderId is not { } id
            || !providers.TryGetValue(id, out var provider)) return;
        try
        {
            var diagnostics = _bindingDiagnostics;
            var previewDiagnostics = _previewDiagnostics;
            if (!IsCurrentPipelineProvider(provider) || !_nodes.Values.Any(n => n.Provider?.Id == id))
            {
                SaveStageLayout();
                if (provider.Stage == EffectStage.Composition)
                    _clip.ExtraData[CompositionPipelineTabKey] = ProviderPipeline(provider).ToString();
                LoadStage(_clip, _page, true, provider.Stage);
                _previewDiagnostics = previewDiagnostics;
            }
            foreach (var group in _groups.Where(g => g.Collapsed && EffectGroupHelper.Members(g, _groups).Contains(id)))
                group.Collapsed = false;
            EffectGroupHelper.Write(_clip.ExtraData, _groups);
            ApplyGroupProjection();
            var terminal = EffectStages.GetTerminals(_stage, providers.Values).FirstOrDefault(t => t.ProviderId == id
                && (diagnostic.FieldId is null || t.Inputs.Any(f => f.Id == diagnostic.FieldId)));
            if (terminal is not null)
            {
                _clip.ExtraData[StagePositionKey(TerminalSourceOutputKey(terminal.Id), "Enabled")] = true;
                AddSystemNodes();
            }
            RefreshBindingDiagnosticIndicators(diagnostics);
            var node = _nodes.Values.FirstOrDefault(n => n.Provider?.Id == id
                && (n.TerminalField is null || diagnostic.FieldId is null || n.TerminalFields.Contains(diagnostic.FieldId)));
            if (node is null) return;
            ShowNodeDiagnostics(node);
            if (PanelCollapsed) OnTogglePanelClicked(this, EventArgs.Empty);
            RebuildConnections();
            Dispatcher.Dispatch(() =>
            {
                if (_closing || !_nodes.TryGetValue(node.Id, out var current) || current != node) return;
                double scale = NodesContainer.Scale;
                double top = TopCommandBar.Height + 20;
                double bottom = Math.Max(BottomCommandBar.Height, BindingDiagnosticsPanel.Height) + 50;
                NodesContainer.TranslationX = (ConnectionsLayer.Width - node.Width * scale) / 2 - node.X * scale;
                NodesContainer.TranslationY = top + Math.Max(0, ConnectionsLayer.Height - top - bottom - node.BodyHeight * scale) / 2 - node.Y * scale;
                UpdateDrawableScale();
                SaveViewTransform();
            });
        }
        catch (Exception ex) { Logger.Log(ex, "Focus effect binding diagnostic", this); }
    }
}
