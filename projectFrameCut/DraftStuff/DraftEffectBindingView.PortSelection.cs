using projectFrameCut.Render.Effect;
using projectFrameCut.Render.RenderAPIBase.EffectAndMixture;
using static LocalizedResources.SimpleLocalizerBaseGeneratedHelper_PropertyPanel;

namespace projectFrameCut.DraftStuff;

public partial class DraftEffectBindingView
{
    private sealed record SelectedPort(NodeViewModel Node, PortKind Kind, int Index, string Key, Guid ProviderId);
    private SelectedPort? _connectionPort;
    private bool _connectingPorts;

    private void AddPortTapGestures(View view, NodeViewModel node, PortKind kind, int index)
    {
        var tap = new TapGestureRecognizer();
        tap.Tapped += async (_, _) => await OnPortTappedAsync(node, kind, index);
        view.GestureRecognizers.Add(tap);
        var doubleTap = new TapGestureRecognizer { NumberOfTapsRequired = 2 };
        doubleTap.Tapped += (_, _) =>
        {
            if (_closing || _isDraggingNodeOrPort || _connectionPort is not null || _connectingPorts) return;
            var port = PortOf(node, kind, index);
            if (port is null || IsPortConnected(node, kind, port)) return;
            _connectionPort = new(node, kind, index, port.Key, PortProviderId(node, port));
            view.Scale = 1.6;
            SetStatusText(Localized.Effect_ClickConnectMode);
        };
        view.GestureRecognizers.Add(doubleTap);
        if (_connectionPort is { } selected && selected.Node == node && selected.Kind == kind && selected.Index == index)
            view.Scale = 1.6;
    }

    private bool IsPortConnected(NodeViewModel node, PortKind kind, NodePort port)
    {
        var id = PortProviderId(node, port);
        if (kind != PortKind.AnchorOutput
            && TryGetPortBindingSource(_clip?.EffectProviders?.GetValueOrDefault(id), port.Key, out _)) return true;
        return ReadBindings().Any(b => kind == PortKind.AnchorOutput
            ? b.Source == id && (b.SourcePortKey ?? node.OutputPorts.FirstOrDefault()?.Key) == port.Key
            : b.Target == id && (b.Kind == UIBindingKind.Picture ? EffectProviderAnchorExtensions.InputKey : b.TargetPortKey) == port.Key);
    }

    private async Task OnPortTappedAsync(NodeViewModel node, PortKind kind, int index)
    {
        if (_closing || _isDraggingNodeOrPort || _connectingPorts || _connectionPort is not { } selected) return;
        var port = PortOf(selected.Node, selected.Kind, selected.Index);
        if (!_nodes.TryGetValue(selected.Node.Id, out var current) || current != selected.Node
            || port is null || port.Key != selected.Key || PortProviderId(current, port) != selected.ProviderId)
        {
            EndPortConnection();
            return;
        }
        if (selected.Node == node || (selected.Kind == PortKind.AnchorOutput) == (kind == PortKind.AnchorOutput))
        {
            SetStatusText(PPLocalizedResources.EffectBindView_ConnectedFail);
            return;
        }
        _connectingPorts = true;
        try
        {
            bool input = selected.Kind != PortKind.AnchorOutput;
            if (await ConnectPortsAsync(input ? node : selected.Node, input ? index : selected.Index,
                input ? selected.Node : node, input ? selected.Kind : kind, input ? selected.Index : index, selected))
                EndPortConnection(false);
        }
        finally
        {
            _connectingPorts = false;
        }
    }

    private void EndPortConnection(bool resetHint = true)
    {
        if (_connectionPort is not { } selected) return;
        var views = selected.Kind switch
        {
            PortKind.AnchorInput => selected.Node.InputPortViews,
            PortKind.AnchorOutput => selected.Node.OutputPortViews,
            _ => selected.Node.ParamPortViews,
        };
        if (views.ElementAtOrDefault(selected.Index) is { } view) view.Scale = 1;
        _connectionPort = null;
        if (resetHint) SetStatusText(Localized.Effect_ClickConnectHint);
    }
}
