using projectFrameCut.ApplicationAPIBase.DynamicPreviewProvider;
using CommunityToolkit.Maui.Views;
using projectFrameCut.Render.ClipsAndTracks;
using projectFrameCut.Render.Plugin;
using projectFrameCut.Render.RenderAPIBase.ClipAndTrack;
using System.Linq;
using projectFrameCut.ApplicationAPIBase.Helpers;
using projectFrameCut.Render.Effect;
using projectFrameCut.Shared;
using System.Collections.Concurrent;
using System.Text.Json;
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

        var resolvedWidth = clip.TargetWidth > 0 ? clip.TargetWidth : clip.EffectiveOutputWidth;
        var resolvedHeight = clip.TargetHeight > 0 ? clip.TargetHeight : clip.EffectiveOutputHeight;

        if (targetWidth > 0)
        {
            resolvedWidth = Math.Min(resolvedWidth, targetWidth);
        }

        if (targetHeight > 0)
        {
            resolvedHeight = Math.Min(resolvedHeight, targetHeight);
        }

        var previewWidth = Math.Max(1, resolvedWidth > 0 ? resolvedWidth : (targetWidth > 0 ? targetWidth : canvasWidth));
        var previewHeight = Math.Max(1, resolvedHeight > 0 ? resolvedHeight : (targetHeight > 0 ? targetHeight : canvasHeight));
        var alpha = clip.A.HasValue ? Math.Clamp(clip.A.Value, 0f, 1f) : 1f;
        return new Grid
        {
            WidthRequest = previewWidth,
            HeightRequest = previewHeight,
            HorizontalOptions = LayoutOptions.Start,
            VerticalOptions = LayoutOptions.Start,
            Children =
            {
                new BoxView
                {
                    Color = Color.FromRgba(clip.R / 65535f, clip.G / 65535f, clip.B / 65535f, alpha),
                    HorizontalOptions = LayoutOptions.Fill,
                    VerticalOptions = LayoutOptions.Fill,
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
        if (target is not TextClip t || target.FromPlugin != InternalPluginBase.InternalPluginBaseID)
        {
            return false;
        }

        var entries = ResolveTextEntriesForPreview(t);
        if (entries.Count == 0)
        {
            return false;
        }

        return !entries.Any(c => c.UseVerticalLayout || (c.strokeWidth.HasValue && c.strokeWidth.Value > 0f));
    }

    public override View Generate(IClip target, int canvasWidth, int canvasHeight, int targetWidth, int targetHeight, uint targetFrame)
    {
        if (target is not TextClip clip)
        {
            return BuildFallbackLabel("Text clip is unavailable.");
        }

        var root = new AbsoluteLayout
        {
            WidthRequest = Math.Max(1, targetWidth),
            HeightRequest = Math.Max(1, targetHeight),
            HorizontalOptions = LayoutOptions.Fill,
            VerticalOptions = LayoutOptions.Fill,
            BackgroundColor = Colors.Transparent,
        };

        var entries = ResolveTextEntriesForPreview(clip);
        foreach (var e in entries)
        {
            var fontSize = Math.Max(1f, e.fontSize * ((e.dpi ?? 72f) / 72f));
            var label = new Label
            {
                Text = e.text,
                TextColor = Color.FromRgba(e.r / 257, e.g / 257, e.b / 257, (double)(e.a ?? 1d)),
                HorizontalTextAlignment = e.horizontalAlignment switch { SixLabors.Fonts.HorizontalAlignment.Left => TextAlignment.Start, SixLabors.Fonts.HorizontalAlignment.Right => TextAlignment.End, SixLabors.Fonts.HorizontalAlignment.Center => TextAlignment.Center, _ => TextAlignment.Start },
                VerticalTextAlignment = e.verticalAlignment switch { SixLabors.Fonts.VerticalAlignment.Top => TextAlignment.Start, SixLabors.Fonts.VerticalAlignment.Bottom => TextAlignment.End, SixLabors.Fonts.VerticalAlignment.Center => TextAlignment.Center, _ => TextAlignment.Start },
                HorizontalOptions = LayoutOptions.Start,
                VerticalOptions = LayoutOptions.Start,
                LineBreakMode = e.wrappingWidth.HasValue && e.wrappingWidth.Value > 0 ? LineBreakMode.WordWrap : LineBreakMode.NoWrap,
                FontAttributes = e.fontStyle switch
                {
                    SixLabors.Fonts.FontStyle.Regular => FontAttributes.None,
                    SixLabors.Fonts.FontStyle.Bold => FontAttributes.Bold,
                    SixLabors.Fonts.FontStyle.Italic => FontAttributes.Italic,
                    SixLabors.Fonts.FontStyle.BoldItalic => FontAttributes.Bold | FontAttributes.Italic,
                    _ => FontAttributes.None,
                },
                FontFamily = string.IsNullOrWhiteSpace(e.fontFamily) ? null : "UserFont_" + e.fontFamily,
                FontSize = fontSize,
                Margin = Thickness.Zero,
                Padding = Thickness.Zero,
                Rotation = e.rotation,
            };

            if (e.lineSpacing > 0)
            {
                label.LineHeight = e.lineSpacing;
            }

            if (e.wrappingWidth.HasValue && e.wrappingWidth.Value > 0)
            {
                label.WidthRequest = e.wrappingWidth.Value;
            }

            var (labelWidth, labelHeight) = MeasureLabelSizeWithFallback(label, e, fontSize);

            var x = e.x;
            var y = e.y;

            switch (e.horizontalAlignment)
            {
                case SixLabors.Fonts.HorizontalAlignment.Center:
                    x -= (int)(labelWidth / 2d);
                    break;
                case SixLabors.Fonts.HorizontalAlignment.Right:
                    x -= (int)labelWidth;
                    break;
            }

            switch (e.verticalAlignment)
            {
                case SixLabors.Fonts.VerticalAlignment.Center:
                    y -= (int)(labelHeight / 2d);
                    break;
                case SixLabors.Fonts.VerticalAlignment.Bottom:
                    y -= (int)labelHeight;
                    break;
            }

            AbsoluteLayout.SetLayoutBounds(label, new Rect(x, y, labelWidth, labelHeight));
            root.Children.Add(label);
        }

        return root;
    }

    private static (double width, double height) MeasureLabelSizeWithFallback(Label label, TextClipEntry entry, double fontSize)
    {
        var measureWidth = entry.wrappingWidth.HasValue && entry.wrappingWidth.Value > 0
            ? entry.wrappingWidth.Value
            : double.PositiveInfinity;

        double measuredWidth = 0d;
        double measuredHeight = 0d;

        try
        {
            var measuredSize = label.Measure(measureWidth, double.PositiveInfinity);
            measuredWidth = measuredSize.Width;
            measuredHeight = measuredSize.Height;
        }
        catch
        {
        }

        if (measuredWidth <= 1d || measuredHeight <= 1d)
        {
            EstimateTextSize(entry, fontSize, out measuredWidth, out measuredHeight);
        }

        return (Math.Max(1d, measuredWidth), Math.Max(1d, measuredHeight));
    }

    private static void EstimateTextSize(TextClipEntry entry, double fontSize, out double width, out double height)
    {
        var text = string.IsNullOrEmpty(entry.text) ? " " : entry.text;
        var strokeExtra = Math.Max(0d, entry.strokeWidth ?? 0f) * 2d;
        var lineHeight = Math.Max(1d, fontSize * Math.Max(0.8d, entry.lineSpacing));

        var lineCount = 1;
        foreach (var c in text)
        {
            if (c == '\n')
            {
                lineCount++;
            }
        }

        if (entry.wrappingWidth.HasValue && entry.wrappingWidth.Value > 0)
        {
            width = Math.Max(1d, entry.wrappingWidth.Value);

            var approxCharWidth = Math.Max(1d, fontSize * 0.56d);
            var maxCharsPerLine = Math.Max(1, (int)Math.Floor(width / approxCharWidth));
            var visualLines = 0;
            var charsInLine = 0;

            foreach (var c in text)
            {
                if (c == '\r')
                {
                    continue;
                }

                if (c == '\n')
                {
                    visualLines++;
                    charsInLine = 0;
                    continue;
                }

                charsInLine++;
                if (charsInLine >= maxCharsPerLine)
                {
                    visualLines++;
                    charsInLine = 0;
                }
            }

            if (charsInLine > 0 || visualLines == 0)
            {
                visualLines++;
            }

            height = Math.Max(lineCount, visualLines) * lineHeight;
        }
        else
        {
            var maxCharsInLine = 0;
            var charsInLine = 0;

            foreach (var c in text)
            {
                if (c == '\r')
                {
                    continue;
                }

                if (c == '\n')
                {
                    if (charsInLine > maxCharsInLine)
                    {
                        maxCharsInLine = charsInLine;
                    }

                    charsInLine = 0;
                    continue;
                }

                charsInLine++;
            }

            if (charsInLine > maxCharsInLine)
            {
                maxCharsInLine = charsInLine;
            }

            if (maxCharsInLine <= 0)
            {
                maxCharsInLine = 1;
            }

            width = maxCharsInLine * fontSize * 0.56d;
            height = lineCount * lineHeight;
        }

        width = Math.Max(1d, width + strokeExtra);
        height = Math.Max(1d, height + strokeExtra);
    }

    private static IReadOnlyList<TextClipEntry> ResolveTextEntriesForPreview(TextClip clip)
    {
        List<TextClipEntry>? extraEntries = null;

        if (clip.ExtraData?.TryGetValue("TextEntries", out var rawEntries) == true)
        {
            if (rawEntries is List<TextClipEntry> list && list.Count > 0)
            {
                extraEntries = list;
            }

            else if (rawEntries is JsonElement je)
            {
                try
                {
                    var parsed = je.Deserialize<List<TextClipEntry>>();
                    if (parsed is { Count: > 0 })
                    {
                        clip.ExtraData["TextEntries"] = parsed;
                        extraEntries = parsed;
                    }
                }
                catch
                {
                }
            }

            else if (rawEntries is string json && !string.IsNullOrWhiteSpace(json))
            {
                try
                {
                    var parsed = JsonSerializer.Deserialize<List<TextClipEntry>>(json);
                    if (parsed is { Count: > 0 })
                    {
                        clip.ExtraData["TextEntries"] = parsed;
                        extraEntries = parsed;
                    }
                }
                catch
                {
                }
            }
        }

        return PickBetterTextEntries(extraEntries, clip.TextEntries);
    }

    private static IReadOnlyList<TextClipEntry> PickBetterTextEntries(IReadOnlyList<TextClipEntry>? primary, IReadOnlyList<TextClipEntry>? fallback)
    {
        var p = primary ?? Array.Empty<TextClipEntry>();
        var f = fallback ?? Array.Empty<TextClipEntry>();

        var pHasVisibleText = p.Any(e => !string.IsNullOrWhiteSpace(e.text));
        var fHasVisibleText = f.Any(e => !string.IsNullOrWhiteSpace(e.text));

        if (pHasVisibleText && !fHasVisibleText)
        {
            return p;
        }

        if (fHasVisibleText && !pHasVisibleText)
        {
            return f;
        }

        return p.Count >= f.Count ? p : f;
    }
}