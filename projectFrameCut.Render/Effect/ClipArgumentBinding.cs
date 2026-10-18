using projectFrameCut.Render.ClipsAndTracks;
using projectFrameCut.Render.RenderAPIBase.ClipAndTrack;
using projectFrameCut.Render.RenderAPIBase.EffectAndMixture;
using System.Collections.Concurrent;
using System.Runtime.CompilerServices;

namespace projectFrameCut.Render.Effect;

public static class ClipArgumentBinding
{
    private sealed record FrameCopy(IClip Source, uint Frame, bool ShouldRender);
    private static readonly ConditionalWeakTable<IClip, FrameCopy> Copies = new();

    public static bool IsFrameCopy(IClip clip) => Copies.TryGetValue(clip, out _);

    internal static uint GetFrameIndex(IClip clip, uint fallback) => Copies.TryGetValue(clip, out var copy) ? copy.Frame : fallback;

    public static bool ShouldRender(IClip frameClip) => !Copies.TryGetValue(frameClip, out var copy) || copy.ShouldRender;

    internal static bool TryGetFrame(IClip clip, uint frame, out IClip? result)
    {
        result = null;
        return ClipValueFrameContext.Current?.TryGetComputed((typeof(ClipArgumentBinding), clip.Id, frame), out result) == true;
    }

    public static IClip InitializeFrame(IClip clip, uint frame)
    {
        if (Rendering.FrameHashContext.Current is { } context)
            return context.Evaluate((typeof(ClipArgumentBinding), clip.Id, frame), () => InitializeFrameCore(clip, frame), c => c, _ => { });
        return InitializeFrameCore(clip, frame);
    }

    private static IClip InitializeFrameCore(IClip clip, uint frame)
    {
        if (Copies.TryGetValue(clip, out var copy))
        {
            if (copy.Frame == frame)
            {
                ClipValueFrameContext.Current?.SetComputed((typeof(ClipArgumentBinding), clip.Id, frame), clip);
                return clip;
            }
            clip = copy.Source;
        }
        if (TryGetFrame(clip, frame, out var cached)) return cached!;
        try
        {
            var provider = clip.EffectProvidersInstances?.OfType<ClipArgumentProvider>().SingleOrDefault();
            if (provider is not { Enabled: true }) return clip;
            using var context = ValueProviderFrameContext.PushFrame(frame, clip.GetEffectiveDuration() > 0
                ? Math.Clamp((float)((long)frame - clip.StartFrame) / clip.GetEffectiveDuration(), 0, 1) : 0);
            bool shouldRender = provider.EvaluateShouldRender();
            var result = shouldRender ? clip.HandleArguments(provider.Evaluate(clip)) : clip.CopyForFrame();
            if (ReferenceEquals(result, clip) || result.Id != clip.Id)
                throw new InvalidOperationException("The clip argument handler must return a frame copy with the same id.");
            Copies.Add(result, new(clip, frame, shouldRender));
            ClipValueFrameContext.Current?.SetComputed((typeof(ClipArgumentBinding), clip.Id, frame), result);
            return result;
        }
        catch (OperationCanceledException) { throw; }
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

    public IClip Get(IClip clip, uint frame, Action<IClip>? initialize = null)
    {
        if (Rendering.FrameHashContext.Current is { } context)
            return context.Evaluate((typeof(ClipArgumentFrameCache), clip.Id, frame), () =>
            {
                if (!ClipArgumentBinding.IsFrameCopy(clip)) initialize?.Invoke(clip);
                return ClipArgumentBinding.InitializeFrame(clip, frame);
            }, c => c, _ => { });
        var result = clips.GetOrAdd((clip.Id, frame), _ => new(() =>
        {
            if (ClipArgumentBinding.TryGetFrame(clip, frame, out var cached)) return cached!;
            if (!ClipArgumentBinding.IsFrameCopy(clip)) initialize?.Invoke(clip);
            return ClipArgumentBinding.InitializeFrame(clip, frame);
        })).Value;
        ClipValueFrameContext.Current?.SetComputed((typeof(ClipArgumentBinding), clip.Id, frame), result);
        return result;
    }

    public void RemoveFrame(uint frame)
    {
        foreach (var key in clips.Keys)
            if (key.Frame == frame) clips.TryRemove(key, out _);
    }

    public void Clear() => clips.Clear();
}
