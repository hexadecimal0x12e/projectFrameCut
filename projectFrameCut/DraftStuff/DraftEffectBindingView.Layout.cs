using Microsoft.Maui;
using projectFrameCut.Render.Effect;
using Size = Microsoft.Maui.Graphics.Size;

namespace projectFrameCut.DraftStuff;

public partial class DraftEffectBindingView
{
    private const double LayoutColumnSpacing = 80;
    private const double LayoutRowSpacing = 40;

    private void OnAutoArrangeClicked(object? sender, EventArgs e)
    {
        if (_clip is null || _closing || _isDraggingNodeOrPort) return;
        try
        {
            RebuildConnections();
            var layouts = _nodes.Values.Where(n => n.View?.IsVisible == true).ToDictionary(n => n.Id, n =>
            {
                var bounds = GetNodeBounds(n, n.X, n.Y);
                if (n.View!.Width <= 0 || n.View.Height <= 0)
                {
                    var size = ((IView)n.View).Measure(double.PositiveInfinity, double.PositiveInfinity);
                    bounds.Width = Math.Max(NodeDefaultWidth, size.Width);
                    bounds.Height = Math.Max(NodeDefaultHeight, size.Height);
                }
                return new AutoLayoutNode { Node = n, Width = bounds.Width, Height = bounds.Height, NodeIds = [n.Id] };
            });
            if (layouts.Count == 0) return;

            // 展开的组作为整体参与外层布局，组内成员单独排列。
            var grouped = new HashSet<Guid>();
            foreach (var layout in layouts.Values.Where(n => n.Node.Group?.Collapsed == false))
                foreach (var id in layout.Node.Group!.ProviderIds.Concat(layout.Node.Group.ChildGroupIds))
                    if (layouts.TryGetValue(id, out var child))
                    {
                        layout.Children.Add(child);
                        grouped.Add(id);
                    }
            var roots = layouts.Values.Where(n => !grouped.Contains(n.Node.Id)).ToList();
            foreach (var root in roots) ArrangeGroupLayout(root);
            var contentSize = ArrangeNodeLayouts(roots);
            foreach (var root in roots) ApplyNodeLayout(root, 0, 0);

            foreach (var node in _nodes.Values)
            {
                SetNodePosition(node, commit: true);
                if (node.Provider is { } provider)
                {
                    provider.MetaData["__DraftEffectBindingView_InteractiveEditorX__"] = node.X;
                    provider.MetaData["__DraftEffectBindingView_InteractiveEditorY__"] = node.Y;
                }
            }
            if (_inputNode is not null) SaveSystemNodePosition(_inputNode);
            if (_outputNode is not null) SaveSystemNodePosition(_outputNode);
            EffectGroupHelper.Write(_clip.ExtraData, _groups);
            UpdateGroupBounds();
            FitArrangedNodes(contentSize);
            Log($"Auto-arranged {layouts.Count} visible nodes and {_drawable.Connections.Count} bindings for clip {_clip.Id}, pipeline {_pipeline}.");
        }
        catch (Exception ex)
        {
            Log(ex, "Auto-arrange effect binding nodes", this);
            SetStatusText(ex.Message);
        }
    }

    private void ArrangeGroupLayout(AutoLayoutNode layout)
    {
        if (layout.Children.Count == 0) return;
        foreach (var child in layout.Children)
        {
            ArrangeGroupLayout(child);
            layout.NodeIds.UnionWith(child.NodeIds);
        }
        var size = ArrangeNodeLayouts(layout.Children);
        foreach (var child in layout.Children)
        {
            child.X += NodeSpacing;
            child.Y += layout.Height + LayoutRowSpacing;
        }
        layout.Width = Math.Max(layout.Width, size.Width + NodeSpacing);
        layout.Height += LayoutRowSpacing + size.Height;
    }

    private Size ArrangeNodeLayouts(List<AutoLayoutNode> nodes)
    {
        var owners = new Dictionary<Guid, AutoLayoutNode>();
        foreach (var node in nodes)
            foreach (var id in node.NodeIds) owners[id] = node;
        var incoming = nodes.ToDictionary(n => n, _ => new HashSet<AutoLayoutNode>());
        var outgoing = nodes.ToDictionary(n => n, _ => new HashSet<AutoLayoutNode>());
        foreach (var connection in _drawable.Connections)
            if (owners.TryGetValue(connection.From.Id, out var from) && owners.TryGetValue(connection.To.Id, out var to)
                && from != to && outgoing[from].Add(to))
                incoming[to].Add(from);

        var pending = nodes.ToDictionary(n => n, n => incoming[n].Count);
        var remaining = nodes.ToHashSet();
        int cycleBreaks = 0;
        while (remaining.Count > 0)
        {
            var ready = nodes.Where(n => remaining.Contains(n) && pending[n] == 0).ToArray();
            if (ready.Length == 0)
            {
                // 组投影或异常绑定可能形成环，按稳定顺序打破布局依赖。
                ready = [nodes.Where(remaining.Contains).OrderBy(n => pending[n]).First()];
                cycleBreaks++;
            }
            foreach (var node in ready)
            {
                remaining.Remove(node);
                foreach (var target in outgoing[node].Where(remaining.Contains))
                {
                    target.Layer = Math.Max(target.Layer, node.Layer + 1);
                    pending[target]--;
                }
            }
        }
        if (cycleBreaks > 0) Log($"Resolved {cycleBreaks} cyclic layout dependencies among {nodes.Count} effect binding nodes.", "warning");
        if (nodes.FirstOrDefault(n => n.Node.Kind == NodeKind.Output) is { } output)
            output.Layer = nodes.Where(n => n != output).Select(n => n.Layer).DefaultIfEmpty(-1).Max() + 1;

        var columns = nodes.GroupBy(n => n.Layer).OrderBy(g => g.Key).Select(g => g.ToList()).ToList();
        var order = nodes.Select((n, i) => (n, i)).ToDictionary(p => p.n, p => p.i);
        var rows = new Dictionary<AutoLayoutNode, double>();
        foreach (var column in columns)
            for (int i = 0; i < column.Count; i++) rows[column[i]] = i;

        // 双向按相邻节点的平均行号排序，减少分支和参数连线交叉。
        for (int pass = 0; pass < 4; pass++)
        {
            bool forward = pass % 2 == 0;
            foreach (var column in forward ? columns.AsEnumerable() : columns.AsEnumerable().Reverse())
            {
                var sorted = column.OrderBy(n => n.Node.Kind == NodeKind.Input ? 0 : incoming[n].Count + outgoing[n].Count == 0 ? 2 : 1)
                    .ThenBy(n => (forward ? incoming[n] : outgoing[n])
                        .Where(other => forward ? other.Layer < n.Layer : other.Layer > n.Layer)
                        .Select(other => rows[other]).DefaultIfEmpty(rows[n]).Average())
                    .ThenBy(n => order[n]).ToArray();
                column.Clear();
                column.AddRange(sorted);
                for (int i = 0; i < column.Count; i++) rows[column[i]] = i;
            }
        }

        double height = columns.Max(c => c.Sum(n => n.Height) + (c.Count - 1) * LayoutRowSpacing);
        double x = 0;
        foreach (var column in columns)
        {
            double y = (height - column.Sum(n => n.Height) - (column.Count - 1) * LayoutRowSpacing) / 2;
            foreach (var node in column)
            {
                node.X = x;
                node.Y = y;
                y += node.Height + LayoutRowSpacing;
            }
            x += column.Max(n => n.Width) + LayoutColumnSpacing;
        }
        return new Size(x - LayoutColumnSpacing, height);
    }

    private void ApplyNodeLayout(AutoLayoutNode layout, double x, double y)
    {
        var node = layout.Node;
        double dx = x + layout.X - node.X, dy = y + layout.Y - node.Y;
        node.X = x + layout.X;
        node.Y = y + layout.Y;
        if (node.Group is { } group)
        {
            if (group.Collapsed) MoveGroup(node, dx, dy);
            else
            {
                group.X = node.X;
                group.Y = node.Y;
            }
        }
        foreach (var child in layout.Children) ApplyNodeLayout(child, node.X, node.Y);
    }

    private void FitArrangedNodes(Size size)
    {
        double top = PipelinePicker.IsVisible ? Math.Max(40, PipelinePicker.Height) + 20 : 20;
        double width = ConnectionsLayer.Width - 40;
        double height = ConnectionsLayer.Height - top - Math.Max(BottomCommandBar.Height, InfoLabel.Height) - 20;
        double scale = width > 0 && height > 0 ? Math.Clamp(Math.Min(width / size.Width, height / size.Height), 0.2, 1.0) : 1.0;
        NodesContainer.AnchorX = NodesContainer.AnchorY = 0;
        NodesContainer.Scale = scale;
        NodesContainer.TranslationX = 20 + Math.Max(0, (width - size.Width * scale) / 2);
        NodesContainer.TranslationY = top + Math.Max(0, (height - size.Height * scale) / 2);
        UpdateDrawableScale();
        SaveViewTransform();
    }

    private sealed class AutoLayoutNode
    {
        public required NodeViewModel Node;
        public List<AutoLayoutNode> Children = [];
        public HashSet<Guid> NodeIds = [];
        public double X, Y, Width, Height;
        public int Layer;
    }
}
