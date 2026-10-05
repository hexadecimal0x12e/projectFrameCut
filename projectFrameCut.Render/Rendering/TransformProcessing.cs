using projectFrameCut.Render.ClipsAndTracks;
using projectFrameCut.Render.Effect;
using projectFrameCut.Render.RenderAPIBase.ClipAndTrack;
using projectFrameCut.Render.RenderAPIBase.EffectAndMixture;
using projectFrameCut.Shared;
using System.Runtime.CompilerServices;

namespace projectFrameCut.Render.Rendering;

public static class TransformProcessing
{
    private static readonly ConditionalWeakTable<IPicture, object> CanvasFrames = new();
    private static readonly ConditionalWeakTable<Dictionary<string, object>, RuntimeCache> Runtimes = new();
    private sealed class RuntimeCache
    {
        public Dictionary<Guid, (string Json, ITransform Transform)> Items { get; } = new();
    }
    public static void Release(Dictionary<string, object> metadata)
    {
        if (!Runtimes.TryGetValue(metadata, out var cache)) return;
        lock (cache)
        {
            foreach (var entry in cache.Items.Values)
                if (entry.Transform is IDisposable disposable) disposable.Dispose();
            cache.Items.Clear();
            Runtimes.Remove(metadata);
        }
    }

    public static bool IsCanvasFrame(IPicture frame) => CanvasFrames.TryGetValue(frame, out _);
    public static void MarkCanvasFrame(IPicture frame) => CanvasFrames.GetValue(frame, static _ => new object());

    public static bool HasActiveTransform(IClip clip, IReadOnlyList<IClip> clips, uint frame)
    {
        var infos = clips.Select(TransformClipInfo.FromClip).ToArray();
        return Enum.GetValues<TransformSide>().Any(side => ClipTransforms.Resolve(infos, clip.Id, side)?.Contains(frame) == true);
    }

    public static IPicture RenderCanvas(IClip clip, IReadOnlyList<IClip> clips, uint frame, int width, int height,
        int projectWidth, int projectHeight, IPicture.PicturePixelMode pixelMode)
    {
        if (TryRender(clip, clips, frame, width, height, projectWidth, projectHeight, pixelMode, out var result)) return result!;
        return RenderInput(clip, frame, width, height, projectWidth, projectHeight, pixelMode);
    }

    public static bool TryRender(IClip clip, IReadOnlyList<IClip> clips, uint frameIndex, int width, int height,
        int projectWidth, int projectHeight, IPicture.PicturePixelMode pixelMode, out IPicture? result, int sdrBrightness = 203, bool autoCenterImplicitClip = true)
    {
        result = null;
        var infos = clips.Select(TransformClipInfo.FromClip).ToArray();
        foreach (var side in Enum.GetValues<TransformSide>())
        {
            var resolved = ClipTransforms.Resolve(infos, clip.Id, side);
            if (resolved is null || !resolved.Contains(frameIndex)) continue;
            try
            {
                var binding = resolved.Binding;
                var cache = Runtimes.GetValue(resolved.Owner.Metadata, static _ => new RuntimeCache());
                lock (cache)
                {
                    var activeIds = Enum.GetValues<TransformSide>().Select(edge => TransformBinding.Read(resolved.Owner.Metadata, edge)?.Id).ToArray();
                    foreach (var id in cache.Items.Keys.Where(id => !activeIds.Contains(id)).ToArray())
                    {
                        if (cache.Items[id].Transform is IDisposable retired) retired.Dispose();
                        cache.Items.Remove(id);
                    }
                    string json = System.Text.Json.JsonSerializer.Serialize(binding);
                    if (!cache.Items.TryGetValue(binding.Id, out var cached) || cached.Json != json)
                    {
                        if (cached.Transform is IDisposable disposable) disposable.Dispose();
                        cached = (json, ClipTransforms.Create(binding));
                        cache.Items[binding.Id] = cached;
                    }
                    var transform = cached.Transform;
                    var mode = binding.InputMode == TransformInputMode.OneInput ? TransformDefinition.SupportOneInput : TransformDefinition.SupportTwoInput;
                    if (!transform.Definition.HasFlag(TransformDefinition.Clip) || !transform.Definition.HasFlag(mode))
                        throw new NotSupportedException($"Transform {transform.TypeName} does not support {binding.InputMode} pictures.");
                    transform.Duration = resolved.Duration;
                    using var left = RenderInput(clips.First(c => c.Id == resolved.Left.Id), frameIndex, width, height, projectWidth, projectHeight, pixelMode, sdrBrightness, autoCenterImplicitClip);
                    using var right = resolved.Right is { } r ? RenderInput(clips.First(c => c.Id == r.Id), frameIndex, width, height, projectWidth, projectHeight, pixelMode, sdrBrightness, autoCenterImplicitClip) : null;
                    result = ProcessFrames(left, right, transform, binding.InputMode, Progress(frameIndex, resolved.Start, resolved.Duration), width, height);
                    // Plugins may return an input buffer owned by this call.
                    if (ReferenceEquals(result, left) || ReferenceEquals(result, right)) result = result.Clone();
                    if (binding.IsAI) result = EffectProcessing.ProcessAIWatermark(result, frameIndex);
                    MarkCanvasFrame(result);
                    return true;
                }
            }
            catch (Exception ex)
            {
                Log(ex, $"Transform {resolved.Binding.Id} on clip {clip.Id}, frame {frameIndex}; using normal picture", "TransformProcessing");
                return false;
            }
        }
        return false;
    }

    private static IPicture RenderInput(IClip clip, uint frame, int width, int height, int projectWidth, int projectHeight, IPicture.PicturePixelMode pixelMode, int sdrBrightness = 203, bool autoCenterImplicitClip = true)
    {
        uint clamped = (uint)Math.Clamp((ulong)frame, clip.StartFrame, (ulong)clip.StartFrame + clip.GetEffectiveDuration() - 1);
        var frames = Timeline.GetFramesInOneFrame([clip], clamped, width, height, pixelMode,
            projectWidth, projectHeight, applyTransforms: false).ToArray();
        if (pixelMode == IPicture.PicturePixelMode.UShortPicture)
        {
            for (int i = 0; i < frames.Length; i++)
            {
                if (frames[i].Clip is IHDRPicture<ushort>) continue;
                int brightness = clip.ExtraData.TryGetValue("HDRBrightness", out var value) && int.TryParse(value.ToString(), out var parsed) ? parsed : sdrBrightness;
                frames[i] = new OneFrame(clamped, clip, HDRPicture16bpp.ToHDRPictureBySignal(frames[i].Clip, brightness));
            }
        }
        return Timeline.MixtureLayers(frames, clamped, width, height, (int)pixelMode, autoCenterImplicitClip: autoCenterImplicitClip,
            projectRelativeWidth: projectWidth, projectRelativeHeight: projectHeight, transparentBackground: true);
    }

    private static double Progress(uint frame, ulong start, uint duration) => duration <= 1 ? 0 : Math.Clamp(((double)frame - start) / (duration - 1), 0, 1);

    public static IPicture ProcessFrames(IPicture left, IPicture? right, ITransform source, TransformInputMode mode, double progress, int width, int height)
    {
        var flag = mode == TransformInputMode.OneInput ? TransformDefinition.SupportOneInput : TransformDefinition.SupportTwoInput;
        if (!source.Definition.HasFlag(TransformDefinition.Clip) || !source.Definition.HasFlag(flag))
            throw new NotSupportedException($"Transform {source.TypeName} does not support {mode} pictures.");
        if (mode == TransformInputMode.OneInput)
            return (source as IOneInputSingleFrameTransform ?? throw new NotSupportedException("Missing one-input transform implementation."))
                .GetFrame(left, progress, width, height);
        ArgumentNullException.ThrowIfNull(right);
        return source.TransformType == TransformType.ContinuousTransform
            ? (source as IContinuousTransform ?? throw new NotSupportedException("Missing continuous transform implementation.")).GetFrame(left, right, progress, width, height)
            : (source as ISingleFrameTransform ?? throw new NotSupportedException("Missing two-input transform implementation.")).GetFrame(left, right, width, height);
    }

    public static IPicture ProcessTransform(IClip left, IClip? right, ITransform source, int width, int height, uint frameIndex, IPicture.PicturePixelMode pixelMode)
    {
        using var l = left.GetFrame((uint)Math.Clamp((ulong)frameIndex, left.StartFrame, (ulong)left.StartFrame + left.GetEffectiveDuration() - 1), width, height, pixelMode);
        using var r = right?.GetFrame((uint)Math.Clamp((ulong)frameIndex, right.StartFrame, (ulong)right.StartFrame + right.GetEffectiveDuration() - 1), width, height, pixelMode);
        ulong start = right is null ? left.StartFrame : (ulong)right.StartFrame - Math.Min((ulong)right.StartFrame, source.Duration / 2);
        var result = ProcessFrames(l, r, source, right is null ? TransformInputMode.OneInput : TransformInputMode.TwoInput, Progress(frameIndex, start, source.Duration), width, height);
        return ReferenceEquals(result, l) || ReferenceEquals(result, r) ? result.Clone() : result;
    }
}
