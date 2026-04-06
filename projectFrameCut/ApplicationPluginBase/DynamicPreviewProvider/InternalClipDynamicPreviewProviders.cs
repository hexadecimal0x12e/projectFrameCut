using projectFrameCut.ApplicationAPIBase.DynamicPreviewProvider;
using CommunityToolkit.Maui.Views;
using projectFrameCut.Render.ClipsAndTracks;
using projectFrameCut.Render.Plugin;
using projectFrameCut.Render.RenderAPIBase.ClipAndTrack;
using System.Linq;
using projectFrameCut.ApplicationAPIBase.Helpers;
using projectFrameCut.Render.Effect;
using System.Collections.Concurrent;
using System.Threading;

namespace projectFrameCut.ApplicationPluginBase.DynamicPreviewProvider;

internal abstract class InternalClipDynamicPreviewProviderBase : IClipDynamicPreviewProvider
{
    public abstract string TypeName { get; }

    public abstract bool IsAvailable(IClip target);

    public abstract View Generate(IClip target, int canvasWidth, int canvasHeight, int targetWidth, int targetHeight, uint targetFrame);

    protected static Label BuildFallbackLabel(string text)
    {
        return new Label
        {
            Text = text,
            HorizontalOptions = LayoutOptions.Center,
            VerticalOptions = LayoutOptions.Center,
            HorizontalTextAlignment = TextAlignment.Center,
            VerticalTextAlignment = TextAlignment.Center,
            TextColor = Colors.White,
            BackgroundColor = Color.FromArgb("#55000000"),
            Padding = new Thickness(8)
        };
    }
}

internal sealed class VideoClipDynamicPreviewProvider : InternalClipDynamicPreviewProviderBase
{
    private const int MaxCachedVideoFrames = 160;
    private const int PrefetchForwardCount = 8;
    private const int PrefetchBackwardCount = 1;

    private static readonly ConcurrentDictionary<VideoFrameCacheKey, CachedVideoFrame> _frameCache = new();
    private static readonly ConcurrentDictionary<VideoPrefetchContextKey, VideoPrefetchContext> _prefetchContexts = new();

    public override string TypeName => "VideoClip";

    public override bool IsAvailable(IClip target)
    {
        return target is VideoClip
            && target.FromPlugin == InternalPluginBase.InternalPluginBaseID
            && !string.IsNullOrWhiteSpace(target.FilePath)
            && File.Exists(target.FilePath);
    }

    public override View Generate(IClip target, int canvasWidth, int canvasHeight, int targetWidth, int targetHeight, uint targetFrame)
    {
        if (target is not VideoClip clip || string.IsNullOrWhiteSpace(clip.FilePath) || !File.Exists(clip.FilePath))
        {
            return BuildFallbackLabel("Video source is unavailable.");
        }
        clip.Decoder?.EnableLock = true;

        var contextKey = new VideoPrefetchContextKey(clip.Id, canvasWidth, canvasHeight);
        var frameKey = new VideoFrameCacheKey(clip.Id, canvasWidth, canvasHeight, targetFrame);

        if (TryGetCachedFrame(frameKey, out var cachedSource))
        {
            EnqueuePrefetch(clip, contextKey, targetFrame);
            return BuildImage(cachedSource);
        }

        var source = RenderFrameAsImageSource(clip, canvasWidth, canvasHeight, targetFrame, contextKey);
        if (source is null)
        {
            return BuildFallbackLabel("Video source is unavailable.");
        }

        CacheFrame(frameKey, source);
        EnqueuePrefetch(clip, contextKey, targetFrame);

        return BuildImage(source);
    }

    private static Image BuildImage(ImageSource source)
    {
        return new Image
        {
            Source = source,
            Aspect = Aspect.Fill,
            HorizontalOptions = LayoutOptions.Fill,
            VerticalOptions = LayoutOptions.Fill,
        };
    }

    private static bool TryGetCachedFrame(VideoFrameCacheKey frameKey, out ImageSource source)
    {
        if (_frameCache.TryGetValue(frameKey, out var cached))
        {
            cached.Touch();
            source = cached.Source;
            return true;
        }

        source = null!;
        return false;
    }

    private static void CacheFrame(VideoFrameCacheKey frameKey, ImageSource source)
    {
        _frameCache[frameKey] = new CachedVideoFrame(source);
        TrimCacheIfNeeded();
    }

    private static void TrimCacheIfNeeded()
    {
        if (_frameCache.Count <= MaxCachedVideoFrames)
        {
            return;
        }

        var removeCount = _frameCache.Count - MaxCachedVideoFrames;
        foreach (var stale in _frameCache
            .OrderBy(x => x.Value.LastAccessTicks)
            .Take(removeCount))
        {
            _frameCache.TryRemove(stale.Key, out _);
        }
    }

    private static void EnqueuePrefetch(VideoClip clip, VideoPrefetchContextKey contextKey, uint targetFrame)
    {
        var context = _prefetchContexts.GetOrAdd(contextKey, static _ => new VideoPrefetchContext());

        foreach (var frameIndex in EnumeratePrefetchFrames(targetFrame))
        {
            if (_frameCache.ContainsKey(new VideoFrameCacheKey(contextKey.ClipId, contextKey.CanvasWidth, contextKey.CanvasHeight, frameIndex)))
            {
                continue;
            }

            if (context.PendingFrames.TryAdd(frameIndex, 0))
            {
                context.Queue.Enqueue(frameIndex);
            }
        }

        StartPrefetchWorkerIfNeeded(clip, contextKey, context);
    }

    private static IEnumerable<uint> EnumeratePrefetchFrames(uint centerFrame)
    {
        for (var i = 1; i <= PrefetchForwardCount; i++)
        {
            if (centerFrame <= uint.MaxValue - (uint)i)
            {
                yield return centerFrame + (uint)i;
            }
        }

        for (var i = 1; i <= PrefetchBackwardCount; i++)
        {
            if (centerFrame >= (uint)i)
            {
                yield return centerFrame - (uint)i;
            }
        }
    }

    private static void StartPrefetchWorkerIfNeeded(VideoClip clip, VideoPrefetchContextKey contextKey, VideoPrefetchContext context)
    {
        if (Interlocked.CompareExchange(ref context.WorkerRunning, 1, 0) != 0)
        {
            return;
        }

        _ = Task.Run(() =>
        {
            while (true)
            {
                while (context.Queue.TryDequeue(out var frameIndex))
                {
                    context.PendingFrames.TryRemove(frameIndex, out _);

                    var frameKey = new VideoFrameCacheKey(contextKey.ClipId, contextKey.CanvasWidth, contextKey.CanvasHeight, frameIndex);
                    if (_frameCache.ContainsKey(frameKey))
                    {
                        continue;
                    }

                    var source = RenderFrameAsImageSource(clip, contextKey.CanvasWidth, contextKey.CanvasHeight, frameIndex, contextKey);
                    if (source is not null)
                    {
                        CacheFrame(frameKey, source);
                    }
                }

                Interlocked.Exchange(ref context.WorkerRunning, 0);

                if (context.Queue.IsEmpty || Interlocked.CompareExchange(ref context.WorkerRunning, 1, 0) != 0)
                {
                    break;
                }
            }
        });
    }

    private static ImageSource? RenderFrameAsImageSource(VideoClip clip, int canvasWidth, int canvasHeight, uint targetFrame, VideoPrefetchContextKey contextKey)
    {
        var context = _prefetchContexts.GetOrAdd(contextKey, static _ => new VideoPrefetchContext());
        context.DecodeGate.Wait();

        try
        {
            using var frame = ((IClip)clip).GetFrame(targetFrame, canvasWidth, canvasHeight, true, 8);
            return frame.ToImageSource();
        }
        catch
        {
            return null;
        }
        finally
        {
            context.DecodeGate.Release();
        }
    }

    private sealed class VideoPrefetchContext
    {
        public SemaphoreSlim DecodeGate { get; } = new(1, 1);
        public ConcurrentQueue<uint> Queue { get; } = new();
        public ConcurrentDictionary<uint, byte> PendingFrames { get; } = new();
        public int WorkerRunning;
    }

    private sealed class CachedVideoFrame
    {
        public CachedVideoFrame(ImageSource source)
        {
            Source = source;
            Touch();
        }

        public ImageSource Source { get; }
        public long LastAccessTicks { get; private set; }

        public void Touch()
        {
            LastAccessTicks = DateTime.UtcNow.Ticks;
        }
    }

    private sealed record VideoFrameCacheKey(string ClipId, int CanvasWidth, int CanvasHeight, uint FrameIndex);
    private sealed record VideoPrefetchContextKey(string ClipId, int CanvasWidth, int CanvasHeight);
}

internal sealed class PhotoClipDynamicPreviewProvider : InternalClipDynamicPreviewProviderBase
{
    public override string TypeName => "PhotoClip";

    public override bool IsAvailable(IClip target)
    {
        return target is PhotoClip
            && target.FromPlugin == InternalPluginBase.InternalPluginBaseID
            && !string.IsNullOrWhiteSpace(target.FilePath)
            && File.Exists(target.FilePath);
    }

    public override View Generate(IClip target, int canvasWidth, int canvasHeight, int targetWidth, int targetHeight, uint targetFrame)
    {
        if (target is not PhotoClip clip || string.IsNullOrWhiteSpace(clip.FilePath) || !File.Exists(clip.FilePath))
        {
            return BuildFallbackLabel("Image source is unavailable.");
        }

        return new Image
        {
            Source = ImageSource.FromFile(clip.FilePath),
            Aspect = Aspect.Fill,
            HorizontalOptions = LayoutOptions.Fill,
            VerticalOptions = LayoutOptions.Fill,
        };
    }
}

internal sealed class SolidColorClipDynamicPreviewProvider : InternalClipDynamicPreviewProviderBase
{
    public override string TypeName => "SolidColorClip";

    public override bool IsAvailable(IClip target)
    {
        return target is SolidColorClip
            && target.FromPlugin == InternalPluginBase.InternalPluginBaseID;
    }

    public override View Generate(IClip target, int canvasWidth, int canvasHeight, int targetWidth, int targetHeight, uint targetFrame)
    {
        if (target is not SolidColorClip clip)
        {
            return BuildFallbackLabel("Solid color clip is unavailable.");
        }

        var previewWidth = Math.Min(canvasWidth, Math.Max(1, clip.EffectiveOutputWidth));
        var previewHeight = Math.Min(canvasHeight, Math.Max(1, clip.EffectiveOutputHeight));
        var alpha = clip.A.HasValue ? Math.Clamp(clip.A.Value, 0f, 1f) : 1f;
        return new Grid
        {
            HorizontalOptions = LayoutOptions.Fill,
            VerticalOptions = LayoutOptions.Fill,
            Children =
            {
                new BoxView
                {
                    Color = Color.FromRgba((byte)(clip.R / 257), (byte)(clip.G / 257), (byte)(clip.B / 257), alpha),
                    WidthRequest = previewWidth,
                    HeightRequest = previewHeight,
                    HorizontalOptions = LayoutOptions.Center,
                    VerticalOptions = LayoutOptions.Center,
                }
            }
        };
    }
}

internal sealed class TextClipDynamicPreviewProvider : InternalClipDynamicPreviewProviderBase
{
    public override string TypeName => "TextClip";

    public override bool IsAvailable(IClip target)
    {
        return target is TextClip t
            && target.FromPlugin == InternalPluginBase.InternalPluginBaseID
            && !t.TextEntries.Any(c => c.UseVerticalLayout || c.applyKerning || c.strokeWidth > 0 || c.dpi is not null);
    }

    public override View Generate(IClip target, int canvasWidth, int canvasHeight, int targetWidth, int targetHeight, uint targetFrame)
    {
        if (target is not TextClip clip)
        {
            return BuildFallbackLabel("Text clip is unavailable.");
        }

        var entries = clip.TextEntries.Select(e => new Label
        {
            Text = e.text,
            TextColor = Color.FromRgba(e.r / 257, e.g / 257, e.b / 257, (double)(e.a ?? 1d)),
            HorizontalTextAlignment = e.horizontalAlignment switch { SixLabors.Fonts.HorizontalAlignment.Left => TextAlignment.Start, SixLabors.Fonts.HorizontalAlignment.Right => TextAlignment.End, SixLabors.Fonts.HorizontalAlignment.Center => TextAlignment.Center, _ => TextAlignment.Center },
            VerticalTextAlignment = e.verticalAlignment switch { SixLabors.Fonts.VerticalAlignment.Top => TextAlignment.Start, SixLabors.Fonts.VerticalAlignment.Bottom => TextAlignment.End, SixLabors.Fonts.VerticalAlignment.Center => TextAlignment.Center, _ => TextAlignment.Center },
            HorizontalOptions = LayoutOptions.Fill,
            VerticalOptions = LayoutOptions.Fill,
            LineBreakMode = LineBreakMode.WordWrap,
            FontAttributes = e.fontStyle switch
            {
                SixLabors.Fonts.FontStyle.Regular => FontAttributes.None,
                SixLabors.Fonts.FontStyle.Bold => FontAttributes.Bold,
                SixLabors.Fonts.FontStyle.Italic => FontAttributes.Italic,
                SixLabors.Fonts.FontStyle.BoldItalic => FontAttributes.Bold | FontAttributes.Italic,
                _ => FontAttributes.None,
            },
            Margin = new Thickness(12),
            FontFamily = "UserFont_" + e.fontFamily,
            CharacterSpacing = e.lineSpacing,
            TranslationX = e.x,
            TranslationY = e.y,
            Rotation = e.rotation,

        });
        var g = new Grid();
        foreach (var item in entries)
        {
            g.Add(item);
        }
        return g;
    }
}