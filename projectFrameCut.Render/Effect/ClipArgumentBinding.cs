using projectFrameCut.Render.ClipsAndTracks;
using projectFrameCut.Render.RenderAPIBase.ClipAndTrack;
using projectFrameCut.Render.RenderAPIBase.EffectAndMixture;
using System.Collections.Concurrent;
using System.Runtime.CompilerServices;

namespace projectFrameCut.Render.Effect;

public static class ClipArgumentBinding
{
    private sealed record FrameCopy(IClip Source, uint Frame);
    private static readonly ConditionalWeakTable<IClip, FrameCopy> Copies = new();

    public static bool IsFrameCopy(IClip clip) => Copies.TryGetValue(clip, out _);

    public static IClip InitializeFrame(IClip clip, uint frame)
    {
        if (Copies.TryGetValue(clip, out var copy))
        {
            if (copy.Frame == frame) return clip;
            clip = copy.Source;
        }
        try
        {
            var provider = clip.EffectProvidersInstances?.OfType<ClipArgumentProvider>().SingleOrDefault();
            if (provider is not { Enabled: true }) return clip;
            using var context = ValueProviderFrameContext.PushFrame(frame, clip.GetEffectiveDuration() > 0
                ? Math.Clamp((float)((long)frame - clip.StartFrame) / clip.GetEffectiveDuration(), 0, 1) : 0);
            var result = clip.HandleArguments(provider.Evaluate(clip));
            if (ReferenceEquals(result, clip) || result.Id != clip.Id)
                throw new InvalidOperationException("The clip argument handler must return a frame copy with the same id.");
            Copies.Add(result, new(clip, frame));
            Logger.LogDiagnostic($"Initialized clip arguments for {clip.Id}, frame {frame}.");
            return result;
        }
        catch (Exception ex)
        {
            ClipInitializationFailure.Mark(clip, "ClipArguments", ex);
            Logger.Log(ex, $"Initialize arguments for clip {clip.Id}, frame {frame}", nameof(ClipArgumentBinding));
            return clip;
        }
    }
}

public sealed class ClipArgumentFrameCache
{
    private readonly ConcurrentDictionary<(Guid Clip, uint Frame), Lazy<IClip>> clips = new();

    public IClip Get(IClip clip, uint frame, Action<IClip>? initialize = null) => clips.GetOrAdd((clip.Id, frame),
        _ => new(() =>
        {
            if (!ClipArgumentBinding.IsFrameCopy(clip)) initialize?.Invoke(clip);
            return ClipArgumentBinding.InitializeFrame(clip, frame);
        })).Value;

    public void RemoveFrame(uint frame)
    {
        foreach (var key in clips.Keys)
            if (key.Frame == frame) clips.TryRemove(key, out _);
    }

    public void Clear() => clips.Clear();
}
