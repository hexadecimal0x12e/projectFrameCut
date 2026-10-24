using projectFrameCut.Render.RenderAPIBase.EffectAndMixture;
using projectFrameCut.Shared;
using System.Security.Cryptography;
using System.Text;

namespace projectFrameCut.DraftStuff;

public partial class DraftEffectBindingView
{
    private void AddStageTerminals()
    {
        if (_pipeline != Stage.ClipArguments || _clip?.EffectProviders is not { } providers) return;
        double x = Math.Max(600, _nodes.Values.Where(n => n.TerminalField is null).Select(n => n.X).DefaultIfEmpty(250).Max() + 200);
        foreach (var terminal in EffectStages.GetTerminals(_pipeline, providers.Values))
        {
            var id = new Guid(SHA256.HashData(Encoding.UTF8.GetBytes($"{_clip.Id}/{_pipeline}/{terminal.Id}"))[..16]);
            if (_nodes.ContainsKey(id)) continue;
            var node = new NodeViewModel
            {
                Id = id, Provider = providers[terminal.ProviderId!.Value], Kind = NodeKind.ClipArguments,
                TerminalField = terminal.Id, TerminalFields = terminal.Inputs.Select(f => f.Id).ToArray(),
                DisplayName = terminal.Id switch
                {
                    "Position" => Localized.Effect_ClipPosition,
                    "Size" => Localized.Effect_ClipSize,
                    _ => terminal.Name,
                },
                X = GetExtraDataDouble(StagePositionKey(terminal.Id, "X"), GetExtraDataDouble(StagePositionKey(terminal.Inputs[0].Id, "X"), x)),
                Y = GetExtraDataDouble(StagePositionKey(terminal.Id, "Y"), GetExtraDataDouble(StagePositionKey(terminal.Inputs[0].Id, "Y"), 60 + _nodes.Values.Count(n => n.TerminalField is not null) * 150)),
            };
            node.BuildPortsFromProvider();
            if (!_clip.ExtraData.ContainsKey(StagePositionKey(terminal.Id, "X")))
            {
                var position = FindNonOverlappingPosition(node, node.X, node.Y);
                node.X = position.X;
                node.Y = position.Y;
            }
            AddNode(node);
        }
    }

    private string StagePositionKey(string id, string axis) => $"__EffectStageLayout_{_pipeline}_{id}_{axis}__";

    private void SaveStageLayout()
    {
        if (_clip is null) return;
        foreach (var node in _nodes.Values)
        {
            if (node.TerminalField is { } field)
            {
                _clip.ExtraData[StagePositionKey(field, "X")] = node.X;
                _clip.ExtraData[StagePositionKey(field, "Y")] = node.Y;
            }
            else if (node.Provider is null && node.Kind is NodeKind.Input or NodeKind.Output or NodeKind.AudioSample) SaveSystemNodePosition(node);
        }
        SaveViewTransform();
    }
}
