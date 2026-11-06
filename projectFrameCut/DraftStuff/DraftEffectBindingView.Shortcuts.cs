using projectFrameCut.Render.Effect;
using projectFrameCut.Render.RenderAPIBase.EffectAndMixture;
using projectFrameCut.Shared;
using System.Globalization;
using System.Text.Json;

namespace projectFrameCut.DraftStuff;

public partial class DraftEffectBindingView
{
    private sealed record EditSnapshot(EffectProviderJSONStructure[] Providers, Dictionary<string, object> ExtraData);
    private sealed record NodeClipboard(Guid ClipId, CompositionPipeline Pipeline, EffectProviderJSONStructure[] Providers,
        List<EffectGroup> Groups, List<VariableDefinition> Variables);

    private static string? _nodeClipboard;
    private static int _nodePasteCount;
    private readonly List<string> _editHistory = [];
    private Guid _editHistoryClipId;
    private int _editHistoryIndex = -1;
    private bool _suspendEditHistory;

    private void OnEditCommandRequested(object? sender, DraftEditCommandEventArgs e)
    {
        if (e.Handled || _closing || _page is null || _clip is null || !IsLoaded || !IsVisible) return;
        if (_page.IsPopupShowing)
        {
            if (!ReferenceEquals(_page.Popup.Content, this)) return;
        }
        else if (_page.MainMultiWindowView.Windows.Count == 0
            || _page.MainMultiWindowView.ActiveWindow is not { IsMinimized: false, IsInStandaloneWindowMode: false } window
            || !ReferenceEquals(window.Content, this)) return;

        e.Handled = true;
        if (MermaidImportOverlay.IsVisible || GroupGraphOverlay.IsVisible) return;
        if (_isDraggingNodeOrPort || !_providerAddTask.IsCompleted) return;
#if WINDOWS
        if (Handler?.PlatformView is Microsoft.UI.Xaml.FrameworkElement element
            && element.XamlRoot is { } root && Microsoft.UI.Xaml.Input.FocusManager.GetFocusedElement(root) is
                Microsoft.UI.Xaml.Controls.TextBox or Microsoft.UI.Xaml.Controls.RichEditBox or Microsoft.UI.Xaml.Controls.PasswordBox) return;
#endif
        try
        {
            if (e.Command is DraftEditCommand.Undo or DraftEditCommand.Redo)
            {
                RestoreEditHistory(e.Command == DraftEditCommand.Undo ? -1 : 1);
                return;
            }
            var selection = GetShortcutSelection();
            if (e.Command == DraftEditCommand.Copy)
            {
                CopyNodes(selection);
                return;
            }
            _suspendEditHistory = true;
            switch (e.Command)
            {
                case DraftEditCommand.Delete:
                    DeleteNodes(selection);
                    break;
                case DraftEditCommand.Cut:
                    if (CopyNodes(selection)) DeleteNodes(selection);
                    break;
                case DraftEditCommand.Paste:
                    PasteNodes();
                    break;
            }
        }
        catch (Exception ex)
        {
            Logger.Log(ex, $"Effect editor command {e.Command}", this);
            SetStatusText(ex.Message);
        }
        finally
        {
            _suspendEditHistory = false;
            if (e.Command is DraftEditCommand.Delete or DraftEditCommand.Cut or DraftEditCommand.Paste) RecordEditHistory();
        }
    }

    private NodeViewModel[] GetShortcutSelection() => _groupMultiSelect
        ? _groupSelection.Where(_nodes.ContainsKey).Select(id => _nodes[id]).ToArray()
        : _selectedNode is { } node ? [node] : [];

    private void DeleteNodes(NodeViewModel[] selection)
    {
        foreach (var node in selection)
        {
            if (!_nodes.ContainsKey(node.Id)) continue;
            if (node.Group is { } group) DeleteGroup(group);
            else if (GetSourceOutputKey(node) is { } key) SetSourceOutputEnabled(key, false);
            else RemoveEffect(node);
        }
        Graph.SetSelection([]);
        Reload();
    }

    private bool CopyNodes(NodeViewModel[] selection)
    {
        if (_clip?.EffectProviders is not { } providers || selection.Length == 0
            || selection.Any(n => n.Kind is not (NodeKind.Effect or NodeKind.Group) || n.Provider?.TypeOfEffect == EffectType.Transform)) return false;
        SaveStageLayout();
        SaveGroupPositions();
        var ids = selection.Where(n => n.Provider is not null).Select(n => n.Id).ToHashSet();
        var groups = new List<EffectGroup>();
        void AddGroup(EffectGroup g)
        {
            if (groups.Any(c => c.Id == g.Id)) return;
            groups.Add(g);
            ids.UnionWith(g.ProviderIds);
            foreach (var id in g.ChildGroupIds) AddGroup(_groups.Single(c => c.Id == id));
        }
        foreach (var node in selection)
            if (node.Group is { } group) AddGroup(group);
        if (ids.Count == 0) return false;
        var variableIds = ids.Select(id => providers[id]).OfType<EffectVariableProvider>().Select(p => p.VariableId).ToHashSet();
        _nodeClipboard = JsonSerializer.Serialize(new NodeClipboard(_clip.Id, IsTransformPipeline ? _compositionPipeline : CompositionPipeline.Mixture,
            ids.Select(id => EffectBindingHelper.SerializeProvider(providers[id])).ToArray(), groups,
            variableIds.Count == 0 ? [] : GetVariables().Where(v => variableIds.Contains(v.Id)).ToList()));
        _nodePasteCount = 0;
        SetStatusText(Localized._Done);
        return true;
    }

    private void PasteNodes()
    {
        if (_clip is null || _nodeClipboard is null) return;
        var data = JsonSerializer.Deserialize<NodeClipboard>(_nodeClipboard)!;
        var ids = data.Providers.Select(p => p.Id).Concat(data.Groups.Select(g => g.Id)).ToDictionary(id => id, _ => Guid.NewGuid());
        var variableIds = data.Variables.ToDictionary(v => v.Id, _ => Guid.NewGuid());
        var existing = _clip.EffectProviders ?? [];
        double offset = 40 * (_nodePasteCount + 1);
        foreach (var p in data.Providers)
        {
            var oldId = p.Id;
            p.Id = ids[p.Id];
            p.Stage = _stage;
            foreach (var key in p.AnchorsBindingState.Keys.ToArray())
            {
                if (EffectProviderAnchorExtensions.IsOutputBindingKey(key))
                {
                    p.AnchorsBindingState.Remove(key);
                    continue;
                }
                if (!EffectProviderOutputExtensions.TryParseOutputSourceId(p.AnchorsBindingState[key], out var source, out var output)) continue;
                if (ids.TryGetValue(source, out var mapped))
                    p.AnchorsBindingState[key] = output is null ? mapped.ToString() : EffectProviderOutputExtensions.CreateOutputSourceId(mapped, output);
                else if (source != IEffectProvider.InputAnchorGUID && (data.ClipId != _clip.Id || !existing.ContainsKey(source)))
                    p.AnchorsBindingState[key] = IEffectProvider.NoConnectionGUID.ToString();
            }
            p.MetaData ??= new();
            if (p.TypeName is "EffectVariableRead" or "EffectVariableWrite")
            {
                var variable = Guid.TryParse(p.MetaData.GetValueOrDefault(nameof(EffectVariableProvider.VariableId))?.ToString(), out var id) ? id : oldId;
                p.MetaData[nameof(EffectVariableProvider.VariableId)] = variableIds[variable].ToString();
            }
            if (Guid.TryParse(p.MetaData.GetValueOrDefault(nameof(ClipValueEndpointProvider.SourceClipId))?.ToString(), out var sourceClip)
                && sourceClip == data.ClipId
                && Guid.TryParse(p.MetaData.GetValueOrDefault(nameof(ClipValueEndpointProvider.SourceEndpointId))?.ToString(), out var endpoint)
                && ids.TryGetValue(endpoint, out var newEndpoint))
            {
                p.MetaData[nameof(ClipValueEndpointProvider.SourceClipId)] = _clip.Id.ToString();
                p.MetaData[nameof(ClipValueEndpointProvider.SourceEndpointId)] = newEndpoint.ToString();
            }
            foreach (var axis in new[] { "X", "Y" })
            {
                double position = double.TryParse(p.MetaData.GetValueOrDefault(ProviderPositionKey(axis, data.Pipeline))?.ToString(),
                    NumberStyles.Float, CultureInfo.InvariantCulture, out var n) ? n : 150;
                p.MetaData[ProviderPositionKey(axis)] = position + offset;
            }
        }
        foreach (var g in data.Groups)
        {
            g.Id = ids[g.Id];
            g.ProviderIds = g.ProviderIds.Select(id => ids[id]).ToList();
            g.ChildGroupIds = g.ChildGroupIds.Select(id => ids[id]).ToList();
            foreach (var port in g.Ports) port.ProviderId = ids[port.ProviderId];
            g.X += offset;
            g.Y += offset;
        }
        var combined = EffectBindingHelper.MigrateToEffectProviders(
            [.. existing.Values.Select(EffectBindingHelper.SerializeProvider), .. data.Providers], null);
        if (combined.Count != existing.Count + data.Providers.Length) throw new InvalidOperationException(Localized.Effect_IncompatiblePipeline);
        foreach (var id in data.Providers.Select(p => p.Id))
        {
            var p = combined[id];
            if (!p.SupportedStages.Contains(_stage) || !CanAddToPipeline(p, _stage, _compositionPipeline)
                || !p.Target.HasFlag(EffectTarget.Pipeline)
                    && !EffectBindingHelper.AreTargetsCompatible(p.Target, _clip.GetEffectSelectionTarget(_stage)))
                throw new InvalidOperationException(Localized.Effect_IncompatiblePipeline);
            AssignCurrentPipeline(p);
        }
        var groups = _groups.Concat(data.Groups).ToList();
        EffectGroupHelper.Validate(groups, combined);
        var variables = data.Variables.Count > 0 ? GetVariables() : [];
        _clip.EffectProviders = combined;
        EffectGroupHelper.Write(_clip.ExtraData, groups);
        if (data.Variables.Count > 0)
            _clip.ExtraData[VariablesKey] = variables.Concat(data.Variables.Select(v => v with { Id = variableIds[v.Id] })).ToList();
        _nodePasteCount++;
        LoadStage(_clip, _page, _showIsNotVisibleInEffectEditorEffect, _stage);
        OnBindingConfigurationChanged();
        var selected = data.Groups.FirstOrDefault(g => !data.Groups.Any(parent => parent.ChildGroupIds.Contains(g.Id)))?.Id
            ?? data.Providers[0].Id;
        if (_nodes.TryGetValue(selected, out var node)) SelectNode(node, force: true);
        SetStatusText(Localized._Done);
    }

    private string CaptureEditSnapshot()
    {
        SaveStageLayout();
        SaveGroupPositions();
        EffectGroupHelper.Write(_clip!.ExtraData, _groups);
        return JsonSerializer.Serialize(new EditSnapshot(
            _clip.EffectProviders?.Values.OrderBy(p => p.Id).Select(EffectBindingHelper.SerializeProvider).ToArray() ?? [],
            _clip.ExtraData.OrderBy(p => p.Key).ToDictionary()));
    }

    private void InitializeEditHistory(bool reset = false)
    {
        if (_clip is null || _suspendEditHistory) return;
        if (reset || _editHistoryClipId != _clip.Id)
        {
            _editHistory.Clear();
            _editHistoryIndex = -1;
            _editHistoryClipId = _clip.Id;
        }
        if (_editHistoryIndex < 0) RecordEditHistory();
    }

    private void RecordEditHistory()
    {
        if (_clip is null || _closing || _suspendEditHistory || _loadingStage) return;
        try
        {
            var snapshot = CaptureEditSnapshot();
            if (_editHistoryIndex >= 0 && _editHistory[_editHistoryIndex] == snapshot) return;
            _editHistory.RemoveRange(_editHistoryIndex + 1, _editHistory.Count - _editHistoryIndex - 1);
            _editHistory.Add(snapshot);
            if (_editHistory.Count > 100) _editHistory.RemoveAt(0);
            _editHistoryIndex = _editHistory.Count - 1;
        }
        catch (Exception ex) { Logger.Log(ex, "Record effect editor history", this); }
    }

    private void RestoreEditHistory(int direction)
    {
        int index = _editHistoryIndex + direction;
        if (_clip is null || index < 0 || index >= _editHistory.Count) return;
        var snapshot = JsonSerializer.Deserialize<EditSnapshot>(_editHistory[index])!;
        var providers = EffectBindingHelper.MigrateToEffectProviders(snapshot.Providers, null);
        if (providers.Count != snapshot.Providers.Length) throw new InvalidOperationException(Localized.Effect_IncompatiblePipeline);
        _suspendEditHistory = true;
        try
        {
            StopStepDebug();
            _clip.EffectProviders = providers;
            _clip.ExtraData = snapshot.ExtraData;
            LoadStage(_clip, _page, _showIsNotVisibleInEffectEditorEffect);
            ClipInfoBuilder.SaveTextStyleClipArguments(_clip);
            _editHistoryIndex = index;
            _editHistory[index] = CaptureEditSnapshot();
            SetStatusText(Localized._Done);
        }
        finally { _suspendEditHistory = false; }
    }
}
