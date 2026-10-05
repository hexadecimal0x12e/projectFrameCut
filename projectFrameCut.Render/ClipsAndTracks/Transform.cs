using System.Text.Json;
using projectFrameCut.Render.Plugin;
using projectFrameCut.Render.RenderAPIBase.ClipAndTrack;
using projectFrameCut.Shared;

namespace projectFrameCut.Render.ClipsAndTracks;

public readonly record struct TransformClipInfo(Guid Id, uint Start, uint Duration, uint Layer, uint SubLayer, Dictionary<string, object> Metadata)
{
    public ulong End => (ulong)Start + Duration;
    public static TransformClipInfo FromClip(IClip clip) => new(clip.Id, clip.StartFrame, clip.GetEffectiveDuration(), clip.LayerIndex, clip.SubLayerIndex, clip.ExtraData);
}

public sealed record ResolvedTransform(TransformClipInfo Owner, TransformBinding Binding, TransformClipInfo Left, TransformClipInfo? Right, ulong Start, uint Duration)
{
    public bool Contains(uint frame) => Duration > 0 && frame >= Start && frame < Start + Duration;
}

public static class ClipTransforms
{
    public static (TransformClipInfo Owner, TransformBinding Binding)? Find(IReadOnlyList<TransformClipInfo> clips, Guid clipId, TransformSide side)
    {
        var clip = clips.FirstOrDefault(c => c.Id == clipId);
        if (clip.Id == Guid.Empty) return null;
        var own = TransformBinding.Read(clip.Metadata, side);
        if (own is not null) return (clip, own);
        if (side == TransformSide.Left)
        {
            foreach (var candidate in clips)
            {
                var incoming = TransformBinding.Read(candidate.Metadata, TransformSide.Right);
                if (incoming?.InputMode == TransformInputMode.TwoInput && incoming.RightClipId == clipId)
                    return (candidate, incoming);
            }
        }
        return null;
    }

    public static ResolvedTransform? Resolve(IReadOnlyList<TransformClipInfo> clips, Guid clipId, TransformSide side)
    {
        var found = Find(clips, clipId, side);
        if (found is null) return null;
        var (owner, binding) = found.Value;
        var left = clips.FirstOrDefault(c => c.Id == binding.LeftClipId);
        if (left.Id == Guid.Empty || left.Duration == 0 || binding.Duration == 0) return null;
        if (binding.InputMode == TransformInputMode.OneInput)
        {
            var duration = Math.Min(binding.Duration, Capacity(clips, left, binding.Side));
            return new(owner, binding, left, null, binding.Side == TransformSide.Left ? left.Start : left.End - duration, duration);
        }
        var right = clips.FirstOrDefault(c => c.Id == binding.RightClipId);
        if (right.Id == Guid.Empty || right.Duration == 0 || left.Layer != right.Layer || left.SubLayer != right.SubLayer || left.End != right.Start)
            return null;
        uint length = (uint)Math.Min(binding.Duration, Math.Min((ulong)Capacity(clips, left, TransformSide.Right) * 2 + 1,
            (ulong)Capacity(clips, right, TransformSide.Left) * 2));
        return new(owner, binding, left, right, (ulong)right.Start - length / 2, length);
    }

    private static uint Capacity(IReadOnlyList<TransformClipInfo> clips, TransformClipInfo clip, TransformSide side)
    {
        var otherSide = side == TransformSide.Left ? TransformSide.Right : TransformSide.Left;
        var other = Find(clips, clip.Id, otherSide);
        if (other is null) return clip.Duration;
        var binding = other.Value.Binding;
        if (binding.InputMode == TransformInputMode.TwoInput)
        {
            var l = clips.FirstOrDefault(c => c.Id == binding.LeftClipId);
            var r = clips.FirstOrDefault(c => c.Id == binding.RightClipId);
            if (l.Id == Guid.Empty || r.Id == Guid.Empty || l.End != r.Start || l.Layer != r.Layer || l.SubLayer != r.SubLayer)
                return clip.Duration;
        }
        uint reserved = binding.InputMode == TransformInputMode.OneInput ? binding.Duration
            : otherSide == TransformSide.Left ? binding.Duration / 2 + binding.Duration % 2 : binding.Duration / 2;
        return clip.Duration - Math.Min(reserved, otherSide == TransformSide.Left ? clip.Duration / 2 : clip.Duration / 2 + clip.Duration % 2);
    }

    private static object ReadParameter(JsonElement value, string type) => value.ValueKind switch
    {
        JsonValueKind.String => value.GetString()!,
        JsonValueKind.True => true,
        JsonValueKind.False => false,
        JsonValueKind.Number when type is "float" or "single" or "System.Single" => value.GetSingle(),
        JsonValueKind.Number when type is "double" or "System.Double" => value.GetDouble(),
        JsonValueKind.Number when type is "uint" or "System.UInt32" => value.GetUInt32(),
        JsonValueKind.Number when value.TryGetInt32(out var number) => number,
        JsonValueKind.Number when value.TryGetInt64(out var number) => number,
        JsonValueKind.Number => value.GetDouble(),
        _ => value.Clone()
    };

    public static ITransform Create(TransformBinding binding)
    {
        var transform = PluginManager.CreateTransform(binding.TransformElement);
        transform.BindedLeftClip = binding.LeftClipId;
        transform.BindedRightClip = binding.RightClipId;
        if (binding.TransformElement.TryGetProperty("Parameters", out var parameters))
            transform.Parameters = parameters.EnumerateObject().ToDictionary(p => p.Name,
                p => ReadParameter(p.Value, transform.ParametersType.GetValueOrDefault(p.Name, "")));
        transform.Side = binding.Side;
        transform.Duration = binding.Duration;
        transform.Init();
        return transform;
    }
}
