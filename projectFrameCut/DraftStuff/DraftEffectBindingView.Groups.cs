using Microsoft.Maui.Controls.Shapes;
using projectFrameCut.ApplicationAPIBase.Views.PropertyPanelBuilders;
using projectFrameCut.ApplicationPluginBase.Effect;
using projectFrameCut.Render.Effect;
using projectFrameCut.Render.RenderAPIBase.EffectAndMixture;
using projectFrameCut.Services;
using projectFrameCut.Shared;
using Rect = Microsoft.Maui.Graphics.Rect;

namespace projectFrameCut.DraftStuff;

public partial class DraftEffectBindingView
{
    private List<EffectGroup> _groups = [];
    private readonly HashSet<Guid> _groupSelection = [];
    private bool _groupMultiSelect;
    private Dictionary<Guid, Dictionary<string, string>> _groupBindingSnapshot = [];
    private VerticalStackLayout? _groupLibraryPanel;
    private int _groupLibraryVersion;

    private void InitializeGroups()
    {
        var select = new Button { Text = Localized.EffectGroup_MultiSelect, Margin = new Thickness(4, 0) };
        select.Clicked += (_, _) =>
        {
            _groupMultiSelect = !_groupMultiSelect;
            _groupSelection.Clear();
            select.BackgroundColor = _groupMultiSelect ? Colors.CornflowerBlue : Colors.Gray;
            RefreshGroupSelection();
        };
        var pack = new Button { Text = Localized.EffectGroup_Pack, Margin = new Thickness(4, 0) };
        pack.Clicked += (_, _) => BeginGroup();
        BottomCommandBar.Children.Insert(0, pack);
        BottomCommandBar.Children.Insert(0, select);
        Loaded += (_, _) =>
        {
            EffectGroupStore.Changed -= OnGroupLibraryChanged;
            EffectGroupStore.Changed += OnGroupLibraryChanged;
            RefreshGroupLibrary();
        };
        Unloaded += (_, _) => EffectGroupStore.Changed -= OnGroupLibraryChanged;
    }

    private void OnGroupLibraryChanged() => Dispatcher.Dispatch(RefreshGroupLibrary);

    private void LoadGroups(bool resetSelection = true)
    {
        _groups = [];
        if (resetSelection)
        {
            _groupSelection.Clear();
            _selectedNode = null;
        }
        _drawable.GroupBounds.Clear();
        if (_clip?.EffectProviders is not { } providers) return;
        try
        {
            _groups = EffectGroupHelper.Read(_clip.ExtraData);
            EffectGroupHelper.EnsureBoundaryPorts(_groups, providers);
            EffectGroupHelper.Validate(_groups, providers);
            EffectGroupHelper.Write(_clip.ExtraData, _groups);
        }
        catch (Exception ex)
        {
            Logger.Log(ex, "Load clip effect groups", this);
            SetStatusText(ex.Message);
            _groups = [];
        }
        CaptureGroupBindings();
    }

    private void CaptureGroupBindings()
    {
        _groupBindingSnapshot = _clip?.EffectProviders?
            .ToDictionary(p => p.Key, p => new Dictionary<string, string>(p.Value.AnchorsBindingState)) ?? [];
        if (_clip is not null && (_groups.Count > 0 || !_clip.ExtraData.ContainsKey(EffectGroupHelper.MetadataKey)))
            EffectGroupBindingGuard.Capture(_clip);
    }

    private bool SynchronizeGroupBindings()
    {
        if (_clip?.EffectProviders is not { } providers || _groups.Count == 0) return true;
        try
        {
            var candidate = EffectGroupHelper.Copy(_groups);
            EffectGroupHelper.EnsureBoundaryPorts(candidate, providers);
            EffectGroupHelper.Validate(candidate, providers);
            _groups = candidate;
            EffectGroupHelper.Write(_clip.ExtraData, _groups);
            CaptureGroupBindings();
            ApplyGroupProjection();
            return true;
        }
        catch (Exception ex)
        {
            foreach (var p in providers.Values)
                if (_groupBindingSnapshot.TryGetValue(p.Id, out var state)) p.AnchorsBindingState = new(state);
            EffectBindingHelper.MaterializeFields(providers.Values);
            Logger.Log(ex, "Reject invalid effect group binding", this);
            SetStatusText(ex.Message);
            RebuildConnections();
            return false;
        }
    }

    private void RefreshGroupSelection()
    {
        foreach (var node in _nodes.Values)
            if (node.View is VerticalStackLayout stack && stack.Children.OfType<Border>().FirstOrDefault() is { } border)
                border.Stroke = _groupSelection.Contains(node.Id) || !_groupMultiSelect && node == _selectedNode ? Colors.Yellow
                    : node.BindingDiagnosticSignature.Length > 0 ? Colors.OrangeRed
                    : node.Kind == NodeKind.Group ? Colors.CornflowerBlue : node.Kind == NodeKind.Effect ? Colors.Gray : Colors.White;
    }

    private bool TrySelectGroupMember(NodeViewModel node)
    {
        if (!_groupMultiSelect) return false;
        if (node.Kind is not (NodeKind.Effect or NodeKind.Group)) return true;
        if (!_groupSelection.Add(node.Id)) _groupSelection.Remove(node.Id);
        RefreshGroupSelection();
        return true;
    }

    private void BeginGroup()
    {
        if (_clip?.EffectProviders is not { } providers || _groupSelection.Count == 0) return;
        try
        {
            var selected = _groupSelection.Select(id => _nodes[id]).ToArray();
            var parents = selected.Select(n => _groups.FirstOrDefault(g => g.ProviderIds.Contains(n.Id) || g.ChildGroupIds.Contains(n.Id))?.Id).Distinct().ToArray();
            if (parents.Length != 1) throw new InvalidOperationException(Localized.EffectGroup_SameParent);
            var candidate = EffectGroupHelper.Copy(_groups);
            var group = new EffectGroup { Name = Localized.EffectGroup_Title,
                X = selected.Min(n => n.X), Y = selected.Min(n => n.Y) - 70,
                ProviderIds = selected.Where(n => n.Kind == NodeKind.Effect).Select(n => n.Id).ToList(),
                ChildGroupIds = selected.Where(n => n.Kind == NodeKind.Group).Select(n => n.Id).ToList() };
            candidate.Add(group);
            if (EffectGroupHelper.Members(group, candidate).Any(id => !EffectGroupHelper.CanGroup(providers[id], providers)))
                throw new InvalidOperationException(Localized.EffectGroup_ForbiddenNode);
            if (parents[0] is { } parentId)
            {
                var parent = candidate.Single(g => g.Id == parentId);
                parent.ProviderIds.RemoveAll(group.ProviderIds.Contains);
                parent.ChildGroupIds.RemoveAll(group.ChildGroupIds.Contains);
                parent.ChildGroupIds.Add(group.Id);
            }
            EffectGroupHelper.EnsureBoundaryPorts(candidate, providers);
            EffectGroupHelper.Validate(candidate, providers);
            ShowGroupEditor(group, candidate);
        }
        catch (Exception ex) { GroupError(ex); }
    }

    private List<EffectGroupPort> GroupPortCandidates(EffectGroup group, List<EffectGroup> groups)
    {
        if (_clip?.EffectProviders is not { } providers) return [];
        var members = EffectGroupHelper.Members(group, groups);
        var ports = new List<EffectGroupPort>();
        foreach (var id in group.ProviderIds)
        {
            var p = providers[id];
            ports.AddRange(DynamicEffectBindings.InputFields(p).Select(f => new EffectGroupPort
                { ProviderId = id, FieldId = f.Key, Name = $"{p.Name}: {f.Key}", IsInput = true }));
            ports.AddRange(p.GetOutputFields().Select(f => new EffectGroupPort
                { ProviderId = id, FieldId = f.Key, Name = $"{p.Name}: {f.Key}" }));
        }
        foreach (var id in group.ChildGroupIds) ports.AddRange(EffectGroupHelper.Copy(groups.Single(g => g.Id == id).Ports));
        return ports.Where(port => port.IsInput
            ? !providers[port.ProviderId].AnchorsBindingState.TryGetValue(port.FieldId, out var source)
                || !EffectProviderOutputExtensions.TryParseOutputSourceId(source, out var id, out _) || !members.Contains(id)
            : DynamicEffectBindings.CanBindOutput(providers[port.ProviderId])).ToList();
    }

    private void ShowGroupEditor(EffectGroup group, List<EffectGroup> candidate)
    {
        if (_clip?.EffectProviders is not { } providers) return;
        var owner = _clip;
        PropertiesPanel.Children.Clear();
        RightTabView.SelectedIndex = 0;
        PropertiesPanel.Children.Add(new Label { Text = Localized.EffectGroup_PublicPorts, FontAttributes = FontAttributes.Bold });
        var name = new Entry { Text = group.Name, Placeholder = Localized.EffectGroup_Name };
        PropertiesPanel.Children.Add(name);
        var required = EffectGroupHelper.RequiredPorts(group, candidate, providers);
        // A parent interface is another boundary that cannot be removed from a child.
        required.AddRange(candidate.Where(g => g.ChildGroupIds.Contains(group.Id)).SelectMany(g => g.Ports)
            .Where(p => EffectGroupHelper.Members(group, candidate).Contains(p.ProviderId)));
        var options = GroupPortCandidates(group, candidate);
        options = options.OrderBy(p => group.Ports.FindIndex(c => EffectGroupHelper.SamePort(c, p)) is var i && i >= 0 ? i : int.MaxValue).ToList();
        var list = new VerticalStackLayout { Spacing = 6 };
        var rows = new List<(EffectGroupPort Port, CheckBox Check, Entry Name, View Row)>();
        foreach (var port in options)
        {
            var stored = group.Ports.FirstOrDefault(p => EffectGroupHelper.SamePort(p, port));
            var check = new CheckBox { IsChecked = stored is not null, IsEnabled = !required.Any(p => EffectGroupHelper.SamePort(p, port)) };
            var entry = new Entry { Text = stored?.Name ?? port.Name, HorizontalOptions = LayoutOptions.Fill };
            var row = new Grid { ColumnDefinitions = { new(GridLength.Auto), new(GridLength.Star), new(GridLength.Auto), new(GridLength.Auto) } };
            row.Add(check, 0);
            row.Add(new VerticalStackLayout { Children = { new Label { Text = $"{(port.IsInput ? Localized.EffectGroup_Input : Localized.EffectGroup_Output)} · {port.Name}", FontSize = 11 }, entry } }, 1);
            var up = new Button { Text = "↑", Padding = 4 };
            var down = new Button { Text = "↓", Padding = 4 };
            void Move(int delta)
            {
                int i = rows.FindIndex(r => r.Row == row), target = i + delta;
                if (target < 0 || target >= rows.Count) return;
                var r = rows[i];
                rows.RemoveAt(i);
                rows.Insert(target, r);
                list.Children.Remove(row);
                list.Children.Insert(target, row);
            }
            up.Clicked += (_, _) => Move(-1);
            down.Clicked += (_, _) => Move(1);
            row.Add(up, 2);
            row.Add(down, 3);
            rows.Add((port, check, entry, row));
            list.Add(row);
        }
        PropertiesPanel.Children.Add(list);
        var save = new Button { Text = Localized._OK };
        save.Clicked += (_, _) =>
        {
            if (!ReferenceEquals(_clip, owner)) return;
            try
            {
                group.Name = string.IsNullOrWhiteSpace(name.Text) ? Localized.EffectGroup_Title : name.Text.Trim();
                group.Ports = rows.Where(r => r.Check.IsChecked).Select(r => new EffectGroupPort
                { ProviderId = r.Port.ProviderId, FieldId = r.Port.FieldId, IsInput = r.Port.IsInput,
                    Name = string.IsNullOrWhiteSpace(r.Name.Text) ? r.Port.Name : r.Name.Text.Trim() }).ToList();
                EffectGroupHelper.EnsureBoundaryPorts(candidate, providers);
                EffectGroupHelper.Validate(candidate, providers);
                _groups = candidate;
                _groupSelection.Clear();
                CommitGroups();
                Logger.Log($"Configured effect group {group.Id} ({group.Name}).", "info");
            }
            catch (Exception ex) { GroupError(ex); }
        };
        PropertiesPanel.Children.Add(save);
        var cancel = new Button { Text = Localized._Cancel };
        cancel.Clicked += (_, _) => PropertiesPanel.Children.Clear();
        PropertiesPanel.Children.Add(cancel);
    }

    private void CommitGroups(bool rebuild = false)
    {
        if (_clip is null) return;
        SaveGroupPositions();
        EffectGroupHelper.Write(_clip.ExtraData, _groups);
        CaptureGroupBindings();
        UpdateProviderNodes();
        ApplyGroupProjection();
        RebuildConnections();
        UpdateClipValuesPanel();
        ConnectionsLayer.Invalidate();
        if (rebuild) OnBindingConfigurationChanged();
    }

    private void SaveGroupPositions()
    {
        foreach (var id in _groups.SelectMany(g => g.ProviderIds))
            if (_nodes.TryGetValue(id, out var node) && node.Provider is { } provider)
            {
                provider.MetaData["__DraftEffectBindingView_InteractiveEditorX__"] = node.X;
                provider.MetaData["__DraftEffectBindingView_InteractiveEditorY__"] = node.Y;
            }
    }

    private void GroupError(Exception ex)
    {
        Logger.Log(ex, "Effect group operation", this);
        SetStatusText(ex.Message);
        PropertiesPanel.Children.Add(new Label { Text = ex.Message, TextColor = Colors.OrangeRed });
    }

    private void AddGroupActions(NodeViewModel node)
    {
        if (node.Group is not { } group) return;
        AddNodeActionButton(group.Collapsed ? Localized.EffectGroup_Expand : Localized.EffectGroup_Collapse, () =>
        {
            group.Collapsed = !group.Collapsed;
            CommitGroups();
        });
        AddNodeActionButton(Localized.EffectGroup_PublicPorts, () =>
        {
            var candidate = EffectGroupHelper.Copy(_groups);
            ShowGroupEditor(candidate.Single(g => g.Id == group.Id), candidate);
        });
        AddNodeActionButton(Localized.EffectGroup_Save, () => SaveGroup(group));
        AddNodeActionButton(Localized.EffectGroup_Export, () => ExportGroup(group));
        AddNodeActionButton(Localized.EffectGroup_Ungroup, () =>
        {
            foreach (var parent in _groups.Where(g => g.ChildGroupIds.Contains(group.Id)))
            {
                parent.ChildGroupIds.Remove(group.Id);
                parent.ChildGroupIds.AddRange(group.ChildGroupIds);
                parent.ProviderIds.AddRange(group.ProviderIds);
            }
            _groups.Remove(group);
            CommitGroups();
        });
        AddNodeActionButton(Localized.DraftPage_ContextMenu_Delete, () => DeleteGroup(group));
    }

    private async void SaveGroup(EffectGroup group)
    {
        if (_clip?.EffectProviders is not { } providers) return;
        try
        {
            SaveGroupPositions();
            var json = EffectGroupStore.Serialize(EffectGroupHelper.Export(group, _groups, providers));
            var hash = _page is null ? await EffectGroupStore.SaveAsync(json) : await EffectGroupStore.SaveWithChoiceAsync(json, _page);
            if (hash is not null) SetStatusText($"{Localized._Done} · {hash[..8]}");
        }
        catch (Exception ex) { GroupError(ex); }
    }

    private async void ExportGroup(EffectGroup group)
    {
        if (_clip?.EffectProviders is not { } providers) return;
        try
        {
            SaveGroupPositions();
            var template = EffectGroupHelper.Export(group, _groups, providers);
            var json = EffectGroupStore.Serialize(template);
            await EffectGroupStore.ExportAsync(new(EffectGroupStore.Hash(json), json, template, null));
        }
        catch (Exception ex) { GroupError(ex); }
    }

    private void DeleteGroup(EffectGroup group)
    {
        if (_clip?.EffectProviders is not { } providers) return;
        var members = EffectGroupHelper.Members(group, _groups);
        var removed = new HashSet<Guid>();
        void Visit(EffectGroup g)
        {
            removed.Add(g.Id);
            foreach (var id in g.ChildGroupIds) Visit(_groups.Single(c => c.Id == id));
        }
        Visit(group);
        foreach (var id in members) EffectBindingHelper.RemoveProvider(providers, id);
        _groups.RemoveAll(g => removed.Contains(g.Id));
        foreach (var g in _groups)
        {
            g.ChildGroupIds.RemoveAll(removed.Contains);
            g.Ports.RemoveAll(p => members.Contains(p.ProviderId));
        }
        RemoveEmptyGroups();
        CommitGroups(rebuild: true);
    }

    private void RemoveEmptyGroups()
    {
        if (_clip?.EffectProviders is not { } providers) return;
        foreach (var g in _groups)
        {
            g.ProviderIds.RemoveAll(id => !providers.ContainsKey(id));
            g.Ports.RemoveAll(p => !providers.ContainsKey(p.ProviderId));
        }
        bool changed;
        do
        {
            var empty = _groups.Where(g => g.ProviderIds.Count == 0 && g.ChildGroupIds.Count == 0).Select(g => g.Id).ToHashSet();
            changed = empty.Count > 0;
            _groups.RemoveAll(g => empty.Contains(g.Id));
            foreach (var g in _groups) g.ChildGroupIds.RemoveAll(empty.Contains);
        } while (changed);
    }

    private NodePort GroupNodePort(EffectGroupPort port, int index)
    {
        var provider = _clip!.EffectProviders![port.ProviderId];
        var field = port.IsInput ? DynamicEffectBindings.InputFields(provider)[port.FieldId] : provider.GetOutputFields()[port.FieldId];
        return new() { Id = port.ProviderId, Key = port.FieldId, DisplayName = port.Name, Index = index,
            Kind = port.IsInput ? IsNodeInput(provider, port.FieldId) ? PortKind.AnchorInput : PortKind.ParamBind
                : PortKind.AnchorOutput, DeclaredFieldType = field.FieldType,
            FieldType = port.IsInput ? _fieldTypes?.GetInputType(provider, port.FieldId) ?? field.FieldType
                : _fieldTypes?.GetOutputType(provider, port.FieldId) ?? field.FieldType };
    }

    private void ApplyGroupProjection()
    {
        var visibleGroups = new HashSet<Guid>();
        var hiddenProviders = new HashSet<Guid>();
        _drawable.GroupBounds.Clear();
        if (_clip?.EffectProviders is not { } providers) return;
        bool Hidden(EffectGroup group) => _groups.Any(g => g.Collapsed && g.Id != group.Id && ContainsGroup(g, group.Id));
        foreach (var group in _groups)
        {
            var members = EffectGroupHelper.Members(group, _groups);
            if (!members.All(id => _nodes.ContainsKey(id))) continue;
            if (group.Collapsed) hiddenProviders.UnionWith(members);
            if (Hidden(group)) continue;
            visibleGroups.Add(group.Id);
            bool added = !_nodes.TryGetValue(group.Id, out var node);
            node ??= new NodeViewModel { Id = group.Id, Kind = NodeKind.Group, Provider = null };
            node.Group = group;
            node.DisplayName = group.Name;
            node.X = group.X;
            node.Y = group.Y;
            node.InputPorts = group.Collapsed ? group.Ports.Where(p => p.IsInput && IsNodeInput(providers[p.ProviderId], p.FieldId))
                .Select(GroupNodePort).ToList() : [];
            node.ParamPorts = group.Collapsed ? group.Ports.Where(p => p.IsInput && !IsNodeInput(providers[p.ProviderId], p.FieldId))
                .Select(GroupNodePort).ToList() : [];
            node.OutputPorts = group.Collapsed ? group.Ports.Where(p => !p.IsInput && DynamicEffectBindings.CanBindOutput(providers[p.ProviderId]))
                .Select(GroupNodePort).ToList() : [];
            if (added) AddNode(node);
            else
            {
                if (node.VisualSignature != GetNodeVisualSignature(node)) RecreateNodeView(node);
                SetNodePosition(node, commit: !_isDraggingNodeOrPort);
            }
        }
        foreach (var node in _nodes.Values.Where(n => n.Group is not null && !visibleGroups.Contains(n.Id)).ToArray())
        {
            NodesContainer.Children.Remove(node.View!);
            _nodes.Remove(node.Id);
            if (_selectedNode == node)
            {
                _selectedNode = null;
                PropertiesPanel.Children.Clear();
            }
        }
        foreach (var node in _nodes.Values)
            if (node.View is not null) node.View.IsVisible = !hiddenProviders.Contains(node.Id);
        UpdateGroupBounds();
        RefreshGroupSelection();
    }

    private bool ContainsGroup(EffectGroup group, Guid id) => group.ChildGroupIds.Contains(id)
        || group.ChildGroupIds.Any(child => ContainsGroup(_groups.Single(g => g.Id == child), id));

    private void UpdateGroupBounds()
    {
        _drawable.GroupBounds.Clear();
        foreach (var g in _groups.Where(g => !g.Collapsed && _nodes.ContainsKey(g.Id)))
        {
            var nodes = EffectGroupHelper.Members(g, _groups).Where(_nodes.ContainsKey).Select(id => _nodes[id])
                .Concat(_nodes.Values.Where(n => n.Group is not null && (n.Id == g.Id || ContainsGroup(g, n.Id))))
                .Where(n => n.View?.IsVisible == true).ToArray();
            if (nodes.Length == 0) continue;
            double x = nodes.Min(n => n.X) - 12, y = nodes.Min(n => n.Y) - 12;
            _drawable.GroupBounds.Add(new RectF((float)x, (float)y, (float)(nodes.Max(n => n.X + n.Width) - x + 12),
                (float)(nodes.Max(n => n.Y + (n.View?.Height > 0 ? n.View.Height : n.BodyHeight)) - y + 12)));
        }
    }

    private void MoveGroup(NodeViewModel node, double dx, double dy)
    {
        if (node.Group is not { } group) return;
        group.X = node.X;
        group.Y = node.Y;
        var members = EffectGroupHelper.Members(group, _groups);
        foreach (var child in _groups.Where(g => g.Id != group.Id && ContainsGroup(group, g.Id)))
        {
            child.X += dx;
            child.Y += dy;
            if (_nodes.TryGetValue(child.Id, out var view)) { view.X = child.X; view.Y = child.Y; }
        }
        foreach (var id in members)
        {
            if (!_nodes.TryGetValue(id, out var child)) continue;
            child.X += dx;
            child.Y += dy;
        }
        foreach (var child in _nodes.Values.Where(n => members.Contains(n.Id) || n.Group is not null && ContainsGroup(group, n.Id)))
            SetNodePosition(child, commit: false);
        UpdateGroupBounds();
    }

    private void BuildGroupProperties(NodeViewModel node)
    {
        var group = node.Group!;
        foreach (var port in group.Ports.Where(p => p.IsInput))
        {
            var provider = _clip!.EffectProviders![port.ProviderId];
            PropertiesPanel.Children.Add(new Label { Text = port.Name, FontAttributes = FontAttributes.Bold });
            var host = new ClipBindingHost(_clip, provider, _page, () => { OnBindingConfigurationChanged(); BuildGroupPropertiesPanel(group.Id); });
            var panel = new PropertyPanelBuilder();
            EffectProviderUIHelper.BuildUI(provider, panel, host, [port.FieldId]);
            panel.PropertyChanged += (_, args) =>
            {
                var update = EffectProviderUIHelper.HandleChange(provider, args);
                if (update.newFields is not null) provider.Fields = update.newFields;
                OnBindingConfigurationChanged();
            };
            PropertiesPanel.Children.Add(panel.Build());
            var bind = new Button { Text = Localized.EffectGroup_Bind };
            bind.Clicked += async (_, _) => await host.EditBinding(port.FieldId);
            PropertiesPanel.Children.Add(bind);
        }
        var edit = new Button { Text = Localized.EffectGroup_PublicPorts };
        edit.Clicked += (_, _) =>
        {
            var candidate = EffectGroupHelper.Copy(_groups);
            ShowGroupEditor(candidate.Single(g => g.Id == group.Id), candidate);
        };
        PropertiesPanel.Children.Add(edit);
    }

    private void BuildGroupPropertiesPanel(Guid id)
    {
        if (!_nodes.TryGetValue(id, out var node)) return;
        PropertiesPanel.Children.Clear();
        BuildGroupProperties(node);
    }

    private bool DisconnectGroupOutput(NodeViewModel node, NodePort port)
    {
        if (_clip?.EffectProviders is not { } providers || node.Group is null) return false;
        var members = EffectGroupHelper.Members(node.Group, _groups);
        bool changed = false;
        foreach (var target in providers.Values.Where(p => !members.Contains(p.Id)))
            foreach (var binding in target.AnchorsBindingState.ToArray())
                if (!EffectProviderAnchorExtensions.IsOutputBindingKey(binding.Key)
                    && EffectProviderOutputExtensions.TryParseOutputSourceId(binding.Value, out var id, out var output)
                    && id == port.Id && (output is null || output == port.Key))
                {
                    if (binding.Key == EffectProviderAnchorExtensions.InputKey) target.DisconnectMainInput();
                    else target.ClearFieldBinding(binding.Key);
                    changed = true;
                }
        var source = providers[port.Id];
        if (source.IsFinalOutputSource() && (source.GetFinalOutputFieldId() is null || source.GetFinalOutputFieldId() == port.Key))
        {
            source.SetFinalOutputSource(false);
            changed = true;
        }
        return changed;
    }

    private void AddGroupLibraryPanel()
    {
        _groupLibraryPanel = new VerticalStackLayout { Spacing = 6 };
        AddEffectsPanel.Children.Add(_groupLibraryPanel);
        RefreshGroupLibrary();
    }

    private async void RefreshGroupLibrary()
    {
        var panel = _groupLibraryPanel;
        if (panel is null) return;
        int version = ++_groupLibraryVersion;
        try
        {
            var items = await EffectGroupStore.LoadAsync();
            if (version != _groupLibraryVersion || panel != _groupLibraryPanel) return;
            panel.Children.Clear();
            panel.Children.Add(new Label { Text = Localized.EffectGroup_Library, FontAttributes = FontAttributes.Bold });
            foreach (var item in items)
            {
                var add = new Button { Text = $"{item.Name} · {item.Hash[..Math.Min(8, item.Hash.Length)]}", IsEnabled = item.Template is not null && item.Error is null };
                add.Clicked += (_, _) => AddGroupTemplate(item);
                panel.Children.Add(add);
                if (item.Error is not null) panel.Children.Add(new Label { Text = item.Error, FontSize = 11, TextColor = Colors.OrangeRed });
            }
        }
        catch (Exception ex) { GroupError(ex); }
    }

    private void AddGroupTemplate(EffectGroupLibraryItem item)
    {
        if (_clip is null || item.Template is null) return;
        try
        {
            var template = EffectGroupHelper.Instantiate(item.Template);
            var providers = EffectGroupStore.Restore(template);
            var target = _clip.SupportsNativeEffects ? _clip.GetEffectSelectionTarget(_pipeline) : _clip.GetEffectSelectionTarget();
            if (providers.Values.Any(p => !ClipInfoBuilder.CanSelectEffectProvider(p, target, hideKeyFramedProviders: true)
                || p.Stage != _pipeline))
                throw new InvalidOperationException(Localized.Effect_IncompatiblePipeline);
            var combined = new Dictionary<Guid, IEffectProvider>(_clip.EffectProviders ?? []);
            foreach (var p in providers) combined.Add(p.Key, p.Value);
            var groups = _groups.Concat(template.Groups).ToList();
            EffectGroupHelper.Validate(groups, combined);
            var root = template.Groups.Single(g => g.Id == template.RootGroupId);
            var position = FindNonOverlappingPosition(new NodeViewModel { Id = root.Id, Kind = NodeKind.Group, Provider = null }, 250, 150);
            double dx = position.X - root.X, dy = position.Y - root.Y;
            foreach (var g in template.Groups) { g.X += dx; g.Y += dy; }
            foreach (var p in providers.Values)
            {
                double Read(string key) => p.MetaData.TryGetValue(key, out var v) && double.TryParse(v.ToString(), System.Globalization.NumberStyles.Float,
                    System.Globalization.CultureInfo.InvariantCulture, out var n) ? n : 250;
                p.MetaData["__DraftEffectBindingView_InteractiveEditorX__"] = Read("__DraftEffectBindingView_InteractiveEditorX__") + dx;
                p.MetaData["__DraftEffectBindingView_InteractiveEditorY__"] = Read("__DraftEffectBindingView_InteractiveEditorY__") + dy;
            }
            EffectGroupHelper.Validate(groups, combined);
            _clip.EffectProviders = combined;
            _groups = groups;
            CommitGroups(rebuild: true);
            Logger.Log($"Added effect group template {item.Hash} to clip {_clip.Id}.", "info");
        }
        catch (Exception ex) { GroupError(ex); }
    }

    private NodeViewModel? VisiblePortNode(Guid providerId)
    {
        var group = _nodes.Values.FirstOrDefault(n => n.Group?.Collapsed == true
            && EffectGroupHelper.Members(n.Group, _groups).Contains(providerId));
        if (group is not null) return group;
        return GetNodeByBindingId(providerId);
    }

    private static NodePort? PortOf(NodeViewModel node, PortKind kind, int index) => kind switch
    {
        PortKind.AnchorInput => node.InputPorts.ElementAtOrDefault(index),
        PortKind.AnchorOutput => node.OutputPorts.ElementAtOrDefault(index),
        _ => node.ParamPorts.ElementAtOrDefault(index),
    };

    private static Guid PortProviderId(NodeViewModel node, NodePort? port) => node.Group is null ? BindingIdOf(node) : port!.Id;
}
