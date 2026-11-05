using Microsoft.Maui.Controls.Shapes;
using projectFrameCut.ApplicationPluginBase.Effect;
using projectFrameCut.Render.Contracts;
using projectFrameCut.Render.Plugin;
using projectFrameCut.Render.RenderAPIBase.EffectAndMixture;
using projectFrameCut.Shared;
using Color = Microsoft.Maui.Graphics.Color;

namespace projectFrameCut.DraftStuff;

public partial class DraftEffectBindingView
{
    private sealed record StepValue(string Text, ImageSource? Image, bool Inactive);
    private sealed record StepPortRow(string Kind, string Name, string TypeName, string Value, string? Source, bool Inactive);

    /// <summary>管线单步执行时被激活的一个节点，以及该步骤各接口的取值。</summary>
    private sealed class StepRecord
    {
        public required NodeViewModel Node;
        public Guid ProviderId;
        public string Title = "";
        public string SubTitle = "";
        public bool Inactive;
        public Dictionary<string, StepValue> Outputs = new();
        public List<StepPortRow> Ports = [];
    }

    private readonly List<StepRecord> _steps = [];
    private readonly List<(BoxView Box, Color Color, double Scale)> _stepPortRestore = [];
    private readonly List<(View View, double Opacity)> _stepOpacityRestore = [];
    private (Border Frame, Brush? Stroke, double Thickness)? _stepFrameRestore;
    private string? _stepError;
    private bool _stepDebugEnabled;
    private int _stepIndex = -1, _stepRunVersion;

    private static string StepL(string key, string fallback) => PluginManager.GetLocalizationItem("Effect_StepDebug_" + key, fallback);

    private void OnStepDebugToggleClicked(object? sender, EventArgs e) => ToggleStepDebug();

    private void ToggleStepDebug()
    {
        try { ToggleStepDebugCore(); }
        catch (Exception ex) { Log(ex, "Toggle single-step mode", this); }
    }

    private void ToggleStepDebugCore()
    {
        if (_stepDebugEnabled)
        {
            StopStepDebug();
            return;
        }
        if (_clip is null || _page is null || _nodes.Count == 0) return;
        _stepDebugEnabled = true;
        _stepError = null;
        StepDebugBar.IsVisible = true;
        StepDebugPanel.IsVisible = true;
        StepDebugToggleButton.Text = "⏹";
        StepDebugDetailPanel.Children.Clear();
        StepDebugListPanel.Children.Clear();
        StepDebugDetailPanel.Children.Add(new Label
        {
            Text = StepL("Running", "正在执行管线…"), TextColor = Colors.Gray, FontSize = 12
        });
        SetStatusText(StepL("Hint", "单步执行：用 ◀ / ▶ 逐步查看被激活的节点和各接口的值。"));
        Log($"Entered single-step execution mode for clip {_clip.Id}, stage {_stage}.");
        _ = RunStepDebugAsync();
    }

    private async Task RunStepDebugAsync()
    {
        if (!_stepDebugEnabled || _clip is null) return;
        var clip = _clip;
        int version = ++_stepRunVersion;
        PreviewBusyIndicator.IsVisible = PreviewBusyIndicator.IsRunning = true;
        try
        {
            if (!_stepDebugEnabled || !ReferenceEquals(_clip, clip)) return;
            await GeneratePreviews();
        }
        catch (Exception ex)
        {
            if (version == _stepRunVersion)
            {
                _stepError = ex.Message;
                SetStatusText(ex.Message);
            }
        }
        finally
        {
            if (_previewCts is null) PreviewBusyIndicator.IsVisible = PreviewBusyIndicator.IsRunning = false;
            if (version == _stepRunVersion && _stepDebugEnabled && ReferenceEquals(_clip, clip))
            {
                _stepIndex = _steps.Count > 0 ? 0 : -1;
                UpdateStepDebugUI();
                if (_stepIndex >= 0) EnsureStepNodeVisible(_steps[0].Node);
                Log($"Captured {_steps.Count} single-step records for clip {clip.Id}.");
            }
        }
    }

    private void StopStepDebug()
    {
        try
        {
            bool active = _stepDebugEnabled || _steps.Count > 0;
            _stepRunVersion++;
            _stepDebugEnabled = false;
            _steps.Clear();
            if (active) _previewCts?.Cancel();
            _stepError = null;
            _stepIndex = -1;
            ClearStepHighlightVisuals();
            if (!active) return;
            StepDebugBar.IsVisible = false;
            StepDebugPanel.IsVisible = false;
            StepDebugToggleButton.Text = "⏱";
            StepDebugListPanel.Children.Clear();
            StepDebugDetailPanel.Children.Clear();
            StepDebugCounterLabel.Text = "";
            StepDebugTitleLabel.Text = StepL("Title", "单步执行");
        }
        catch (Exception ex) { Log(ex, "Stop single-step mode", this); }
    }

    private void BeginStepRecording()
    {
        ClearStepHighlightVisuals();
        _stepError = null;
        _stepIndex = -1;
        _steps.Clear();
    }

    private static ImageSource? StepImage(byte[] png) => png.Length == 0 ? null
        : ImageSource.FromStream(() => new MemoryStream(png, writable: false));

    private static StepValue ReadStepValue(EffectBindingPreviewValue value) => new(
        value.Inactive ? StepL("Inactive", "∅ 未激活") : value.IsVector ? StepL("Vector", "矢量内容") : value.Text,
        StepImage(value.Png), value.Inactive);

    private void FinishStepRecording(EffectBindingPreviewResponse response)
    {
        _steps.Clear();
        _stepError = response.Error;
        if (_inputNode is { } input && response.Sources.TryGetValue("input", out var source))
            _steps.Add(new StepRecord
            {
                Node = input, ProviderId = input.Provider?.Id ?? IEffectProvider.InputAnchorGUID,
                Title = input.DisplayName, SubTitle = StepL("Source", "源画面"),
                Outputs = new() { [EffectProviderAnchorExtensions.InputKey] = ReadStepValue(source) },
            });
        foreach (var recorded in response.Steps)
        {
            var node = recorded.NodeId == Guid.Empty ? VisiblePortNode(recorded.ProviderId) : _nodes.GetValueOrDefault(recorded.NodeId);
            if (node is null || node.Kind is NodeKind.Input or NodeKind.Output) continue;
            var provider = _clip?.EffectProviders?.GetValueOrDefault(recorded.ProviderId) ?? node.Provider;
            var step = new StepRecord
            {
                Node = node, ProviderId = recorded.ProviderId, Title = node.DisplayName,
                SubTitle = $"{provider?.TypeName ?? node.Group?.Name ?? "?"} · {EffectStageUI.Name(_stage)}",
                Outputs = recorded.Outputs.ToDictionary(p => p.Key, p => ReadStepValue(p.Value)),
                Inactive = recorded.Outputs.Count > 0 && recorded.Outputs.Values.All(v => v.Inactive),
            };
            foreach (var (port, kind) in node.InputPorts.Select(p => (p, StepL("Input", "输入")))
                .Concat(node.ParamPorts.Select(p => (p, StepL("Param", "参数")))))
            {
                if (node.Group is not null && port.Id != recorded.ProviderId) continue;
                if (!recorded.Inputs.TryGetValue(port.Key, out var value)) continue;
                string? boundTo = provider is not null && TryGetPortBindingSource(provider, port.Key, out var sourceId)
                    ? GetBoundSourceDisplayName(provider, sourceId) : null;
                step.Ports.Add(new StepPortRow(kind, port.DisplayName,
                    PortTypeHelper.HumanizeTypeName(port.FieldType, port.TypeContract), ReadStepValue(value).Text, boundTo, value.Inactive));
            }
            _steps.Add(step);
        }
        if (_outputNode is { } output && response.Sources.TryGetValue("output", out var result))
            _steps.Add(new StepRecord
            {
                Node = output, ProviderId = output.Provider?.Id ?? IEffectProvider.OutputAnchorGUID,
                Title = output.DisplayName, SubTitle = StepL("Output", "最终输出"),
                Outputs = new() { ["content"] = ReadStepValue(result) },
            });
        _stepIndex = _steps.Count > 0 ? 0 : -1;
        UpdateStepDebugUI();
    }

    // ── Stepping UI ──────────────────────────────────────────────────────

    private void OnStepPrevClicked(object? sender, EventArgs e) => GoToStep(_stepIndex - 1);
    private void OnStepNextClicked(object? sender, EventArgs e) => GoToStep(_stepIndex + 1);
    private void OnStepLastClicked(object? sender, EventArgs e) => GoToStep(_steps.Count - 1);
    private void OnStepRunClicked(object? sender, EventArgs e) => _ = RunStepDebugAsync();
    private void OnStepStopClicked(object? sender, EventArgs e) => StopStepDebug();

    private void GoToStep(int index)
    {
        try
        {
            if (_steps.Count == 0) return;
            _stepIndex = Math.Clamp(index, 0, _steps.Count - 1);
            UpdateStepDebugUI();
            EnsureStepNodeVisible(_steps[_stepIndex].Node);
        }
        catch (Exception ex) { Log(ex, "Move to single-step index", this); }
    }

    private void UpdateStepDebugUI()
    {
        if (!_stepDebugEnabled) return;
        try { UpdateStepDebugUICore(); }
        catch (Exception ex) { Log(ex, "Update single-step panel", this); }
    }

    private void UpdateStepDebugUICore()
    {
        StepDebugListPanel.Children.Clear();
        StepDebugDetailPanel.Children.Clear();
        StepDebugHintLabel.Text = StepL("Hint", "单步执行：用 ◀ / ▶ 逐步查看被激活的节点和各接口的值。");
        bool has = _stepIndex >= 0 && _stepIndex < _steps.Count;
        StepDebugCounterLabel.Text = has ? $"{_stepIndex + 1}/{_steps.Count}" : $"0/{_steps.Count}";
        StepPrevButton.IsEnabled = has && _stepIndex > 0;
        StepNextButton.IsEnabled = has && _stepIndex < _steps.Count - 1;
        StepLastButton.IsEnabled = has && _stepIndex < _steps.Count - 1;
        StepDebugTitleLabel.Text = has
            ? $"{StepL("Title", "单步执行")} · {_stepIndex + 1}/{_steps.Count} · {_steps[_stepIndex].Title}"
            : StepL("Title", "单步执行");
        if (_stepError is { Length: > 0 } error)
            StepDebugDetailPanel.Children.Add(new Label
            {
                Text = error, TextColor = Color.FromArgb("#FF8A80"), FontSize = 11, LineBreakMode = LineBreakMode.WordWrap
            });
        if (!has)
        {
            StepDebugDetailPanel.Children.Add(new Label
            {
                Text = StepL("Empty", "没有捕获到节点执行。请点击 ⟳ 重新执行，或确认当前阶段的管线存在有效绑定。"),
                TextColor = Colors.Gray, FontSize = 12, LineBreakMode = LineBreakMode.WordWrap
            });
            ClearStepHighlightVisuals();
            return;
        }
        BuildStepList();
        BuildStepDetail(_steps[_stepIndex]);
        ApplyStepHighlight(_steps[_stepIndex]);
    }

    private void BuildStepList()
    {
        Border? selectedRow = null;
        for (int i = 0; i < _steps.Count; i++)
        {
            int index = i;
            var step = _steps[i];
            bool selected = i == _stepIndex;
            var summary = (step.Outputs.Values.FirstOrDefault()?.Text ?? "").ReplaceLineEndings(" ");
            if (summary.Length > 26) summary = summary[..26] + "…";
            var row = new Border
            {
                BackgroundColor = selected ? Color.FromArgb("#233640") : Colors.Transparent,
                Stroke = Colors.Transparent, StrokeThickness = 0, Padding = new Thickness(4, 2),
                Content = new Label
                {
                    Text = string.IsNullOrWhiteSpace(summary) ? $"{i + 1}. {step.Title}" : $"{i + 1}. {step.Title} · {summary}",
                    FontSize = 11,
                    TextColor = selected ? Color.FromArgb("#00E5FF") : step.Inactive ? Colors.OrangeRed : Colors.LightGray,
                    LineBreakMode = LineBreakMode.TailTruncation,
                },
            };
            var tap = new TapGestureRecognizer();
            tap.Tapped += (_, _) => GoToStep(index);
            row.GestureRecognizers.Add(tap);
            StepDebugListPanel.Children.Add(row);
            if (selected) selectedRow = row;
        }
        if (selectedRow is not null)
            Dispatcher.Dispatch(() => ScrollStepListTo(selectedRow));
    }

    /// <summary>
    /// 用坐标滚动把当前步骤滚到列表中间。不用 ScrollToAsync(Element, ScrollToPosition, bool)：
    /// 该方法在 Windows 上会对元素归属做校验并同步抛 ArgumentException，异常从 Dispatcher 回调逃逸会直接终止进程。
    /// </summary>
    private void ScrollStepListTo(Border row)
    {
        try
        {
            double viewport = StepDebugListScroll.Height;
            double y = row.Y;
            if (viewport <= 0 || y <= 0) return;
            double rowHeight = row.Height > 0 ? row.Height : 20;
            double target = Math.Max(0, y - Math.Max(0, (viewport - rowHeight) / 2));
            var task = StepDebugListScroll.ScrollToAsync(0, target, false);
            if (task is not null)
                _ = task.ContinueWith(t => Log(t.Exception!.GetBaseException(), "Scroll single-step list", this),
                    CancellationToken.None, TaskContinuationOptions.OnlyOnFaulted, TaskScheduler.Default);
        }
        catch (Exception ex) { Log(ex, "Scroll single-step list", this); }
    }

    private void BuildStepDetail(StepRecord step)
    {
        StepDebugDetailPanel.Children.Add(new Label
        {
            Text = step.Title, FontAttributes = FontAttributes.Bold, TextColor = Colors.White, FontSize = 13,
            LineBreakMode = LineBreakMode.WordWrap
        });
        StepDebugDetailPanel.Children.Add(new Label { Text = step.SubTitle, TextColor = Colors.Gray, FontSize = 10 });
        if (step.Inactive)
            StepDebugDetailPanel.Children.Add(new Label
            {
                Text = StepL("InactiveNode", "该节点在当前帧未激活（分支未选中），其接口未参与计算。"),
                TextColor = Colors.OrangeRed, FontSize = 11, LineBreakMode = LineBreakMode.WordWrap
            });

        var table = new Grid
        {
            ColumnSpacing = 6,
            RowSpacing = 3,
            ColumnDefinitions =
            {
                new ColumnDefinition { Width = GridLength.Auto },
                new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) },
                new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) },
            },
        };
        int row = 0;
        void AddPortRow(string kind, string name, string type, string value, bool inactive, string? tooltip)
        {
            table.Add(new Label { Text = kind, FontSize = 10, TextColor = Colors.Gray, VerticalOptions = LayoutOptions.Start }, 0, row);
            var nameLabel = new Label
            {
                Text = string.IsNullOrWhiteSpace(type) ? name : $"{name} · {type}", FontSize = 10,
                TextColor = Colors.LightGray, LineBreakMode = LineBreakMode.WordWrap
            };
            table.Add(nameLabel, 1, row);
            var valueLabel = new Label
            {
                Text = value, FontSize = 10,
                TextColor = inactive ? Colors.OrangeRed : Color.FromArgb("#f2c94c"),
                LineBreakMode = LineBreakMode.WordWrap
            };
            if (!string.IsNullOrWhiteSpace(tooltip)) ToolTipProperties.SetText(valueLabel, tooltip);
            table.Add(valueLabel, 2, row);
            row++;
        }
        foreach (var port in step.Ports)
            AddPortRow(port.Kind, port.Name, port.TypeName, port.Value, port.Inactive,
                string.IsNullOrWhiteSpace(port.Source) ? null : $"{StepL("BoundTo", "来源")}: {port.Source}");
        var provider = _clip?.EffectProviders?.GetValueOrDefault(step.ProviderId) ?? step.Node.Provider;
        foreach (var (field, value) in step.Outputs)
            AddPortRow(StepL("Output", "输出"),
                provider is null ? StepL("Content", "画面") : EffectProviderUIHelper.FieldName(provider, field, false, field),
                "", value.Text, value.Inactive, null);
        StepDebugDetailPanel.Children.Add(table);

        var image = step.Outputs.Values.Select(v => v.Image).FirstOrDefault(i => i is not null);
        if (image is not null)
        {
            StepDebugDetailPanel.Children.Add(new Border
            {
                Stroke = Color.FromArgb("#3a3a3a"), StrokeThickness = 1, StrokeShape = new RoundRectangle { CornerRadius = 4 },
                BackgroundColor = Colors.Black, Padding = 0, HeightRequest = 130,
                Content = new Microsoft.Maui.Controls.Image { Source = image, Aspect = Aspect.AspectFit },
            });
        }
    }

    // ── Highlighting ─────────────────────────────────────────────────────

    private void ApplyStepHighlight(StepRecord step)
    {
        ClearStepHighlightVisuals();
        var node = step.Node;
        if (node.View is null || _nodes.GetValueOrDefault(node.Id) != node) return;
        var accent = step.Inactive ? Color.FromArgb("#FF5252") : Color.FromArgb("#00E5FF");
        foreach (var other in _nodes.Values)
        {
            if (other.View is null) continue;
            _stepOpacityRestore.Add((other.View, other.View.Opacity));
            other.View.Opacity = other == node ? 1 : 0.4;
        }
        if (node.Frame is { } frame)
        {
            _stepFrameRestore = (frame, frame.Stroke, frame.StrokeThickness);
            frame.Stroke = accent;
            frame.StrokeThickness = 4;
        }
        HighlightStepPorts(node, node.InputPorts, node.InputPortViews, step.ProviderId, step.Inactive ? accent : Color.FromArgb("#FFD54F"));
        HighlightStepPorts(node, node.ParamPorts, node.ParamPortViews, step.ProviderId, step.Inactive ? accent : Color.FromArgb("#FFD54F"));
        HighlightStepPorts(node, node.OutputPorts, node.OutputPortViews, step.ProviderId, accent);
        _drawable.StepFocusNode = node;
        ConnectionsLayer.Invalidate();
    }

    private void HighlightStepPorts(NodeViewModel node, List<NodePort> ports, List<View> views, Guid providerId, Color color)
    {
        for (int i = 0; i < views.Count && i < ports.Count; i++)
        {
            if (node.Group is not null && ports[i].Id != providerId) continue;
            if (views[i] is not BoxView box) continue;
            _stepPortRestore.Add((box, box.Color, box.Scale));
            box.Color = color;
            box.Scale = 1.6;
        }
    }

    private void ClearStepHighlightVisuals()
    {
        foreach (var (view, opacity) in _stepOpacityRestore) view.Opacity = opacity;
        _stepOpacityRestore.Clear();
        if (_stepFrameRestore is { } frame && frame.Frame is { } border)
        {
            border.Stroke = frame.Stroke;
            border.StrokeThickness = frame.Thickness;
        }
        _stepFrameRestore = null;
        foreach (var (box, color, scale) in _stepPortRestore)
        {
            box.Color = color;
            box.Scale = scale;
        }
        _stepPortRestore.Clear();
        if (_drawable.StepFocusNode is not null)
        {
            _drawable.StepFocusNode = null;
            ConnectionsLayer.Invalidate();
        }
    }

    /// <summary>把执行焦点节点移到画布可见区域（避开单步面板），方便逐步观察。</summary>
    private void EnsureStepNodeVisible(NodeViewModel node)
    {
        try
        {
            if (_closing || _isDraggingNodeOrPort || node.View is null) return;
            double width = ConnectionsLayer.Width, height = ConnectionsLayer.Height;
            if (width <= 0 || height <= 0) return;
            double panelWidth = StepDebugPanel.WidthRequest > 0 ? StepDebugPanel.WidthRequest : 520;
            double panelHeight = StepDebugPanel.HeightRequest > 0 ? StepDebugPanel.HeightRequest : 330;
            double leftInset = StepDebugPanel.IsVisible ? Math.Min(panelWidth + 20, width / 2) : 0;
            double bottomInset = StepDebugPanel.IsVisible ? Math.Min(panelHeight + 20, height / 2) : 0;
            double scale = NodesContainer.Scale;
            double left = node.X * scale + NodesContainer.TranslationX;
            double top = node.Y * scale + NodesContainer.TranslationY;
            double w = Math.Max(node.Width, NodeDefaultWidth) * scale;
            double h = Math.Max(node.BodyHeight, NodeDefaultHeight) * scale;
            if (left >= leftInset + 12 && top >= 44 && left + w <= width - 12 && top + h <= height - bottomInset - 12) return;
            NodesContainer.TranslationX = leftInset + (width - leftInset) / 2 - (node.X + node.Width / 2) * scale;
            NodesContainer.TranslationY = (height - bottomInset) / 2 - (node.Y + node.BodyHeight / 2) * scale;
            _drawable.PanX = NodesContainer.TranslationX;
            _drawable.PanY = NodesContainer.TranslationY;
            ConnectionsLayer.Invalidate();
            SaveViewTransform();
        }
        catch (Exception ex) { Log(ex, "Focus single-step node", this); }
    }
}
