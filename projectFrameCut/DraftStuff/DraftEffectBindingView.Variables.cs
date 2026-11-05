using projectFrameCut.Render.Effect;
using projectFrameCut.Render.RenderAPIBase.EffectAndMixture;
using projectFrameCut.Shared;
using System.Globalization;
using System.Text.Json;

namespace projectFrameCut.DraftStuff;

public partial class DraftEffectBindingView
{
    private sealed record VariableDefinition(Guid Id, string Name, EffectArgumentFieldType ValueType);
    private bool CanManageVariables => _clip is not null && DynamicEffectCalculation.Enabled(_clip.ExtraData) && !IsTransformPipeline
        && _stage is EffectStage.Render or EffectStage.VectorContent or EffectStage.Composition;
    private string VariablesKey => $"__EffectVariables_{_stage}_{PipelineLayoutPrefix}__";

    private List<VariableDefinition> GetVariables()
    {
        if (_clip is null) return [];
        var variables = _clip.ExtraData.GetValueOrDefault(VariablesKey) switch
        {
            List<VariableDefinition> saved => saved,
            JsonElement json => json.Deserialize<List<VariableDefinition>>() ?? [],
            _ => new List<VariableDefinition>(),
        };
        foreach (var node in _clip.EffectProviders?.Values.OfType<EffectVariableProvider>().Where(IsCurrentPipelineProvider) ?? [])
            if (variables.All(v => v.Id != node.VariableId)) variables.Add(new(node.VariableId, node.Name, node.ValueType));
        _clip.ExtraData[VariablesKey] = variables;
        return variables;
    }

    private void CreateVariable()
    {
        if (_clip is null || !CanManageVariables) return;
        var variables = GetVariables();
        variables.Add(new(Guid.NewGuid(), string.IsNullOrWhiteSpace(_endpointName) ? Localized.Effect_VariableName : _endpointName.Trim(), _endpointType));
        _clip.ExtraData[VariablesKey] = variables;
        OnBindingConfigurationChanged();
        UpdateClipValuesPanel();
    }

    private void AddVariableRows(VerticalStackLayout panel)
    {
        foreach (var variable in GetVariables())
        {
            var row = new Grid { ColumnDefinitions = new ColumnDefinitionCollection { new(GridLength.Star), new(GridLength.Auto), new(GridLength.Auto), new(GridLength.Auto) }, ColumnSpacing = 4 };
            row.Children.Add(new Label { Text = $"{variable.Name} ({PortTypeHelper.HumanizeTypeName(variable.ValueType)})", VerticalOptions = LayoutOptions.Center, LineBreakMode = LineBreakMode.TailTruncation });
            var read = new Button { Text = Localized.Effect_AddVariableRead, IsEnabled = CanManageVariables };
            read.Clicked += (_, _) => AddVariableNode(variable, false);
            var write = new Button { Text = Localized.Effect_AddVariableWrite, IsEnabled = CanManageVariables };
            write.Clicked += (_, _) => AddVariableNode(variable, true);
            var remove = new Button { Text = Localized.DraftPage_ContextMenu_Delete };
            remove.Clicked += (_, _) => RemoveVariable(variable.Id);
            row.Add(read, 1);
            row.Add(write, 2);
            row.Add(remove, 3);
            panel.Children.Add(row);
        }
    }

    private void AddVariableNode(VariableDefinition variable, bool write)
    {
        EffectVariableProvider provider = write ? new EffectVariableWriteProvider() : new EffectVariableReadProvider();
        provider.VariableId = variable.Id;
        provider.Name = variable.Name;
        provider.ValueType = variable.ValueType;
        AddEndpointNode(provider);
    }

    private void RemoveVariable(Guid id)
    {
        if (_clip is null) return;
        _clip.ExtraData[VariablesKey] = GetVariables().Where(v => v.Id != id).ToList();
        foreach (var node in _clip.EffectProviders?.Values.OfType<EffectVariableProvider>().Where(p => p.VariableId == id).ToArray() ?? [])
        {
            EffectBindingHelper.RemoveProvider(_clip.EffectProviders!, node.Id);
            RemoveUIBindingsFrom(node.Id);
            RemoveUIBindingsTo(node.Id, null);
            if (_nodes.TryGetValue(node.Id, out var view)) RemoveSourceOutputNode(view);
        }
        RemoveEmptyGroups();
        CommitGroups(rebuild: true);
        UpdateSourcesAndOutputsPanel();
    }

    private void BuildVariableProperties(NodeViewModel node, EffectVariableProvider variable)
    {
        var name = new Entry { Placeholder = Localized.Effect_VariableName, Text = variable.Name };
        name.Unfocused += (_, _) =>
        {
            if (string.IsNullOrWhiteSpace(name.Text) || name.Text.Trim() == variable.Name) return;
            var variables = GetVariables();
            _clip!.ExtraData[VariablesKey] = variables.Select(v => v.Id == variable.VariableId ? v with { Name = name.Text.Trim() } : v).ToList();
            foreach (var provider in _clip.EffectProviders!.Values.OfType<EffectVariableProvider>().Where(p => p.VariableId == variable.VariableId))
            {
                provider.Name = name.Text.Trim();
                if (_nodes.TryGetValue(provider.Id, out var view))
                {
                    view.DisplayName = ProviderNodeName(provider);
                    RecreateNodeView(view);
                }
            }
            OnBindingConfigurationChanged();
            UpdateClipValuesPanel();
        };
        PropertiesPanel.Children.Add(name);
        var enabled = new Switch { IsToggled = variable.Enabled };
        enabled.Toggled += (_, e) =>
        {
            variable.Enabled = e.Value;
            UpdateEndpointNode(node);
        };
        PropertiesPanel.Children.Add(new Label { Text = Localized.DraftSettingPage_Advanced_Enabled });
        PropertiesPanel.Children.Add(enabled);
        PropertiesPanel.Children.Add(new Label { Text = Localized.Effect_VariableDescription, FontSize = 12 });
        if (variable is not EffectVariableReadProvider) return;
        var field = variable.Fields[EffectVariableReadProvider.InitialValueKey];
        var value = field.GetGetter()();
        var initial = new Entry
        {
            Text = value is string text ? text : value is IFormattable formatted
                ? formatted.ToString(null, CultureInfo.InvariantCulture) : JsonSerializer.Serialize(value),
        };
        PropertiesPanel.Children.Add(new Label { Text = Localized.Effect_VariableInitialValue });
        PropertiesPanel.Children.Add(initial);
        var error = new Label { TextColor = Colors.OrangeRed, IsVisible = false, FontSize = 12 };
        PropertiesPanel.Children.Add(error);
        initial.Unfocused += (_, _) =>
        {
            try
            {
                var updated = EffectVariableProvider.ConvertValue(variable.ValueType, initial.Text ?? "");
                if (updated is null || !EffectFieldTypes.Accepts(variable.ValueType, updated))
                    throw new FormatException(Localized.Effect_VariableInvalidInitialValue);
                var fields = variable.Fields;
                fields[EffectVariableReadProvider.InitialValueKey] = new StaticEffectArgumentField
                {
                    Id = EffectVariableReadProvider.InitialValueKey, FieldType = field.FieldType,
                    Value = updated, DefaultValue = field.DefaultValue,
                };
                variable.Fields = fields;
                error.IsVisible = false;
                OnBindingConfigurationChanged();
                RefreshNodeParams(node);
            }
            catch (Exception ex) when (ex is FormatException or OverflowException or JsonException or InvalidCastException)
            {
                error.Text = Localized.Effect_VariableInvalidInitialValue;
                error.IsVisible = true;
            }
        };
    }
}
