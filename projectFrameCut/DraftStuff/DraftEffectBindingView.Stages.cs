using projectFrameCut.Render.RenderAPIBase.EffectAndMixture;
using projectFrameCut.Shared;
using projectFrameCut.ApplicationPluginBase.Effect;
using System.Security.Cryptography;
using System.Text;

namespace projectFrameCut.DraftStuff;

public partial class DraftEffectBindingView
{
    private void AddContentNodes()
    {
        if (_clip is null || !HasContentStage || !IsSourceOutputEnabled("content")) return;
        if (_stage != EffectStage.Composition && !_nodes.ContainsKey(IEffectProvider.InputAnchorGUID))
        {
            bool inputHasPosition = _clip.ExtraData.ContainsKey(StagePositionKey("input", "X")) || _clip.ExtraData.ContainsKey(ExtraDataInputXKey);
            var inputX = GetExtraDataDouble(StagePositionKey("input", "X"), GetExtraDataDouble(ExtraDataInputXKey, 50));
            var inputY = GetExtraDataDouble(StagePositionKey("input", "Y"), GetExtraDataDouble(ExtraDataInputYKey, 150));
            var inputNode = new NodeViewModel
            {
                Kind = NodeKind.Input,
                X = inputX,
                Y = inputY,
                Id = IEffectProvider.InputAnchorGUID,
                Provider = null,
                DisplayName = ContentSourceName,
                OutputPort = new NodePort
                {
                    Kind = PortKind.AnchorOutput,
                    Key = EffectProviderAnchorExtensions.InputKey,
                    FieldType = _stage.GetContentType(),
                    DisplayName = ContentSourceName,
                    Index = 0,
                    Id = IEffectProvider.InputAnchorGUID
                }
            };
            if (!inputHasPosition)
            {
                var pos = FindNonOverlappingPosition(inputNode, inputX, inputY);
                inputNode.X = pos.X;
                inputNode.Y = pos.Y;
            }
            AddNode(inputNode);
            _inputNode = inputNode;
        }
        if (_nodes.TryGetValue(IEffectProvider.OutputAnchorGUID, out var existing))
        {
            _outputNode = existing;
            return;
        }
        double maxX = _nodes.Values.Select(n => n.X).DefaultIfEmpty(250).Max();
        bool outputHasPosition = _clip.ExtraData.ContainsKey(StagePositionKey("output", "X")) || _clip.ExtraData.ContainsKey(ExtraDataOutputXKey);
        var outputX = GetExtraDataDouble(StagePositionKey("output", "X"), GetExtraDataDouble(ExtraDataOutputXKey, Math.Max(maxX + 200, 600)));
        var outputY = GetExtraDataDouble(StagePositionKey("output", "Y"), GetExtraDataDouble(ExtraDataOutputYKey, 150));
        var outputNode = new NodeViewModel
        {
            Kind = NodeKind.Output,
            X = outputX,
            Y = outputY,
            Id = IEffectProvider.OutputAnchorGUID,
            Provider = null,
            DisplayName = ContentOutputName,
            InputPorts = [new NodePort
            {
                Kind = PortKind.AnchorInput,
                Key = EffectProviderAnchorExtensions.InputKey,
                FieldType = _stage.GetContentType(),
                DisplayName = ContentOutputName,
                Index = 0,
                Id = IEffectProvider.OutputAnchorGUID
            }]
        };
        if (!outputHasPosition)
        {
            var pos = FindNonOverlappingPosition(outputNode, outputX, outputY);
            outputNode.X = pos.X;
            outputNode.Y = pos.Y;
        }
        AddNode(outputNode);
        _outputNode = outputNode;
    }

    private static string StageTerminalName(StageTerminal terminal, IEffectProvider provider) => terminal.Id switch
    {
        RenderOutputProvider.AudioKey => Localized.Effect_RenderFinalAudio,
        RenderOutputProvider.PositionKey => Localized.Effect_RenderFinalPosition,
        RenderOutputProvider.ShouldRenderKey => Localized.Effect_RenderShouldRender,
        _ => EffectProviderUIHelper.FieldName(provider, terminal.Id, true, terminal.Name),
    };

    private void AddStageTerminals()
    {
        if (_stage is not (EffectStage.Text or EffectStage.Render) || _clip?.EffectProviders is not { } providers) return;
        double x = _stage == EffectStage.Render && _outputNode is { } output ? output.X
            : Math.Max(600, _nodes.Values.Where(n => n.TerminalField is null).Select(n => n.X).DefaultIfEmpty(250).Max() + 200);
        foreach (var terminal in EffectStages.GetTerminals(_stage, providers.Values))
        {
            if (!IsSourceOutputEnabled(TerminalSourceOutputKey(terminal.Id))) continue;
            if (_stage == EffectStage.Render && (terminal.Id == RenderOutputProvider.AudioKey && !HasClipAudio
                || terminal.Id == RenderOutputProvider.PositionKey && _clip.ClipType == ClipMode.AudioClip)) continue;
            var id = new Guid(SHA256.HashData(Encoding.UTF8.GetBytes($"{_clip.Id}/{_stage}/{terminal.Id}"))[..16]);
            if (_nodes.TryGetValue(id, out var existing))
            {
                existing.Provider = providers[terminal.ProviderId!.Value];
                existing.DisplayName = StageTerminalName(terminal, existing.Provider);
                existing.BuildPortsFromProvider();
                if (existing.VisualSignature != GetNodeVisualSignature(existing)) RecreateNodeView(existing);
                continue;
            }
            var node = new NodeViewModel
            {
                Id = id, Provider = providers[terminal.ProviderId!.Value], Kind = NodeKind.ClipArguments,
                TerminalField = terminal.Id, TerminalFields = terminal.Inputs.Select(f => f.Id).ToArray(),
                DisplayName = StageTerminalName(terminal, providers[terminal.ProviderId!.Value]),
                X = GetExtraDataDouble(StagePositionKey(terminal.Id, "X"), GetExtraDataDouble(StagePositionKey(terminal.Inputs[0].Id, "X"), x)),
                Y = GetExtraDataDouble(StagePositionKey(terminal.Id, "Y"), GetExtraDataDouble(StagePositionKey(terminal.Inputs[0].Id, "Y"), _stage == EffectStage.Render ? terminal.Id switch
                    {
                        RenderOutputProvider.AudioKey => 350,
                        RenderOutputProvider.PositionKey => 550,
                        _ => 700,
                    } : 60 + _nodes.Values.Count(n => n.TerminalField is not null) * 150)),
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

    private bool HasClipAudio => _clip is { } clip && (clip.ClipType == ClipMode.AudioClip || !string.IsNullOrWhiteSpace(clip.BindedSoundTrack));

    private void AddRenderPositionSource()
    {
        if (_stage != EffectStage.Render || _clip?.ClipType == ClipMode.AudioClip
            || !IsSourceOutputEnabled("position") || _nodes.ContainsKey(PositionNodeId)) return;
        AddNode(new NodeViewModel
        {
            Id = PositionNodeId, Kind = NodeKind.AudioSample, Provider = null,
            DisplayName = Localized.Effect_RenderSourcePosition,
            X = GetExtraDataDouble(StagePositionKey("positionSource", "X"), 50),
            Y = GetExtraDataDouble(StagePositionKey("positionSource", "Y"), 550),
            OutputPorts = [new NodePort
            {
                Id = PositionNodeId, Kind = PortKind.AnchorOutput, Key = "Position",
                FieldType = EffectArgumentFieldType.SizeAndPosition, DeclaredFieldType = EffectArgumentFieldType.SizeAndPosition,
                DisplayName = Localized.Effect_RenderSourcePosition, Index = 0,
            }],
        });
    }

    private string StagePositionKey(string id, string axis) => $"__EffectStageLayout_{_stage}_{PipelineLayoutPrefix}{id}_{axis}__";

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
