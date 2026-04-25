using projectFrameCut.ApplicationAPIBase.DynamicPreviewProvider;
using projectFrameCut.ApplicationAPIBase.Helpers;
using projectFrameCut.ApplicationAPIBase.Plugins;
using projectFrameCut.DraftStuff;
using projectFrameCut.LivePreview;
using projectFrameCut.Render.ClipsAndTracks;
using projectFrameCut.Render.Plugin;
using projectFrameCut.Render.RenderAPIBase.ClipAndTrack;
using projectFrameCut.Render.RenderAPIBase.EffectAndMixture;
using projectFrameCut.Render.RenderAPIBase.Project;
using projectFrameCut.Render.Rendering;
using projectFrameCut.Shared;
using Microsoft.Maui.Controls.Shapes;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Text.Json;
using System.Threading;
using IPicture = projectFrameCut.Shared.IPicture;
using projectFrameCut.Asset;
using Path = System.IO.Path;

namespace projectFrameCut.InteractableEditor;

public sealed class DynamicPreview : ContentView, IDisposable
{
    private const double MinTextPreviewSize = 10d;

    public sealed record PreparedPreview(string ClipId, View? View, string? ErrorMessage, IClip? Source);

    private readonly ContentView _outputHost;
    private readonly Label _placeholder;
    private IClip[]? _clips;
    private LivePreviewer? _previewer;
    private uint _currentFrame;
    private long _renderVersion;
    private int _viewportWidth;
    private int _viewportHeight;
    private readonly object _clipsGate = new();
    private int _activePreviewOps;
    private readonly List<IClip[]> _pendingDisposeClipBatches = new();

    public DynamicPreview()
    {
        _placeholder = new Label
        {
            HorizontalOptions = LayoutOptions.Center,
            VerticalOptions = LayoutOptions.Center,
            HorizontalTextAlignment = TextAlignment.Center,
            VerticalTextAlignment = TextAlignment.Center,
            TextColor = Colors.White,
            BackgroundColor = Color.FromArgb("#66000000"),
            Padding = new Thickness(12, 8),
            IsVisible = false
        };

        _outputHost = new ContentView
        {
            HorizontalOptions = LayoutOptions.Fill,
            VerticalOptions = LayoutOptions.Fill,
            IsVisible = false
        };

        Content = new Grid
        {
            Children =
            {
                _outputHost,
                _placeholder,
            }
        };

        HorizontalOptions = LayoutOptions.Fill;
        VerticalOptions = LayoutOptions.Fill;
        InputTransparent = true;
        IsVisible = false;
    }

    public ContentView OutputView => _outputHost;

    public IClip[]? Clips => _clips;

    public uint CurrentFrame => _currentFrame;

    public async Task<IReadOnlyList<PreparedPreview>> PrepareFrameAsync(uint frameIndex, int targetWidth, int targetHeight)
    {
        _currentFrame = frameIndex;
        var clipsSnapshot = AcquireClipsSnapshot();
        try
        {
            var requests = ResolveRequests(clipsSnapshot, frameIndex);
            var canvasWidth = ResolveCanvasSize(_outputHost.Width, Width, _viewportWidth, targetWidth);
            var canvasHeight = ResolveCanvasSize(_outputHost.Height, Height, _viewportHeight, targetHeight);
            return await PrepareRequestsAsync(requests, canvasWidth, canvasHeight, targetWidth, targetHeight, frameIndex, applyClipTargetLayout: false).ConfigureAwait(false);
        }
        finally
        {
            ReleaseClipsSnapshot();
        }
    }

    public async Task UpdateDraft(DraftStructureJSON json)
    {
        ArgumentNullException.ThrowIfNull(json);
        var elements = (JsonSerializer.SerializeToElement(json).Deserialize<DraftStructureJSON>()?.Clips) ?? throw new NullReferenceException("Failed to cast ClipDraftDTOs to IClips."); //I don't want to write a lot of code to clone attributes from dto to IClip, it's too hard and may cause a lot of mystery bugs.

        var clipsList = new List<IClip>();

        foreach (var clip in elements.Cast<JsonElement>())
        {
            if (clip.TryGetProperty("ClipType", out var clipTypeProp)
                && clipTypeProp.ValueKind == JsonValueKind.Number
                && clipTypeProp.TryGetInt32(out var clipTypeValue)
                && (ClipMode)clipTypeValue == ClipMode.MarkingClip)
            {
                continue;
            }

            var clipInstance = PluginManager.CreateClip(clip);
            if (clipInstance.FilePath is not null)
            {
                if (clipInstance.FilePath.StartsWith('$'))
                {
                    var asset = AssetDatabase.Assets[clipInstance.FilePath.Substring(1)];
                    clipInstance.FilePath = asset.Path;
                    var proxyPath = Path.Combine(MauiProgram.DataPath, "My Assets", ".proxy", $"{asset.AssetId}.mp4");
                    if (Path.Exists(proxyPath))
                    {
                        clipInstance.FilePath = proxyPath;
                        Log($"The proxy for {clipInstance.Name} is used.");
                    }
                    else
                    {
                        Log($"The proxy for {clipInstance.Name} does not exist.");
                    }
                }
                else if (_previewer?.ProxyRoot is not null && clipInstance.FilePath is not null)
                {
                    var proxiedPath = Path.Combine(_previewer.ProxyRoot, $"{Path.GetFileNameWithoutExtension(clipInstance.FilePath)}.proxy.mp4");

                    if (Path.Exists(proxiedPath))
                    {
                        clipInstance.FilePath = proxiedPath;
                        Log($"The proxy for {clipInstance.Name} is used.");
                    }
                    else
                    {
                        Log($"The proxy for {clipInstance.Name} does not exist.");
                    }
                }
            }
            await Task.Run(() => clipInstance.ReInit(8));
            clipsList.Add(clipInstance);
        }
        
        var newClips = clipsList.ToArray();

        IClip[]? batchToDispose = null;
        lock (_clipsGate)
        {
            var oldClips = _clips;
            _clips = newClips;
            if (oldClips is not null)
            {
                if (_activePreviewOps > 0)
                {
                    _pendingDisposeClipBatches.Add(oldClips);
                }
                else
                {
                    batchToDispose = oldClips;
                }
            }
        }

        if (batchToDispose is not null)
        {
            DisposeClipBatch(batchToDispose);
        }
    }

    public void SetLivePreviewer(ref LivePreviewer? previewer)
    {
        _previewer = previewer;
    }

    public void UpdateCanvasSize(double width, double height)
    {
        if (width > 0)
        {
            _viewportWidth = Math.Max(1, (int)Math.Round(width, MidpointRounding.AwayFromZero));
        }

        if (height > 0)
        {
            _viewportHeight = Math.Max(1, (int)Math.Round(height, MidpointRounding.AwayFromZero));
        }
    }

    public async Task<bool> RenderFrame(uint frameIndex, int targetWidth, int targetHeight)
    {
        _currentFrame = frameIndex;
        var renderVersion = Interlocked.Increment(ref _renderVersion);
        var clipsSnapshot = AcquireClipsSnapshot();
        IReadOnlyList<PreparedPreview> prepared;
        var viewportWidth = ResolveCanvasSize(_outputHost.Width, Width, _viewportWidth, targetWidth);
        var viewportHeight = ResolveCanvasSize(_outputHost.Height, Height, _viewportHeight, targetHeight);
        try
        {
            var requests = ResolveRequests(clipsSnapshot, frameIndex);
            prepared = await PrepareRequestsAsync(requests, targetWidth, targetHeight, targetWidth, targetHeight, frameIndex, applyClipTargetLayout: true).ConfigureAwait(false);
        }
        finally
        {
            ReleaseClipsSnapshot();
        }

        if (Dispatcher.IsDispatchRequired)
        {
            return await Dispatcher.DispatchAsync(() => ApplyPreparedRequests(prepared, renderVersion, viewportWidth, viewportHeight, targetWidth, targetHeight));
        }

        return ApplyPreparedRequests(prepared, renderVersion, viewportWidth, viewportHeight, targetWidth, targetHeight);
    }

    public void Dispose()
    {
        Interlocked.Increment(ref _renderVersion);
        DisposeClips();
        _outputHost.Content = null;
    }

    private async Task<IReadOnlyList<PreparedPreview>> PrepareRequestsAsync(IReadOnlyList<PreviewRequest> requests, int canvasWidth, int canvasHeight, int targetWidth, int targetHeight, uint frameIndex, bool applyClipTargetLayout)
    {
        if (requests.Count == 0)
        {
            return [];
        }

        var preparationTasks = requests
            .Reverse()
            .Select(request => Task.Run(() => GenerateClipPreviewPrepared(request, canvasWidth, canvasHeight, targetWidth, targetHeight, frameIndex, applyClipTargetLayout)))
            .ToArray();

        return await Task.WhenAll(preparationTasks).ConfigureAwait(false);
    }

    private bool ApplyPreparedRequests(IReadOnlyList<PreparedPreview> prepared, long renderVersion, int viewportWidth, int viewportHeight, int targetWidth, int targetHeight)
    {
        if (renderVersion != Interlocked.Read(ref _renderVersion))
        {
            return false;
        }

        if (prepared.Count == 0)
        {
            _outputHost.Content = null;
            _outputHost.IsVisible = false;
            _placeholder.Text = string.Empty;
            _placeholder.IsVisible = false;
            IsVisible = false;
            return false;
        }

        Microsoft.Maui.Controls.Grid composite = new()
        {
            HorizontalOptions = LayoutOptions.Fill,
            VerticalOptions = LayoutOptions.Fill,
            InputTransparent = true
        };

        var renderedCount = 0;
        string? lastErrorMessage = null;

        foreach (var result in prepared)
        {
            if (!string.IsNullOrWhiteSpace(result.ErrorMessage))
            {
                lastErrorMessage = result.ErrorMessage;
            }

            if (result.View is not Microsoft.Maui.Controls.View generatedView)
            {
                continue;
            }

            generatedView.ZIndex = (int)((result.Source?.LayerIndex ?? 1) * 100);
            composite.Children.Add(generatedView);
            renderedCount++;
        }

        Microsoft.Maui.Controls.View? finalView = null;
        if (renderedCount == 1)
        {
            if (composite.Children[0] is Microsoft.Maui.Controls.View singleView)
            {
                finalView = singleView;
            }
        }
        else if (renderedCount > 1)
        {
            finalView = composite as Microsoft.Maui.Controls.View;
        }

        var alignedView = BuildViewportAlignedView(finalView, viewportWidth, viewportHeight, targetWidth, targetHeight);
        _outputHost.Content = alignedView;
        _outputHost.IsVisible = alignedView is not null;
        _placeholder.Text = lastErrorMessage ?? string.Empty;
        _placeholder.IsVisible = alignedView is null && !string.IsNullOrWhiteSpace(lastErrorMessage);
        IsVisible = alignedView is not null || _placeholder.IsVisible;

        return alignedView is not null;
    }

    private static int ResolveCanvasSize(double hostSize, double selfSize, int cachedSize, int fallbackSize)
    {
        if (hostSize > 0)
        {
            return Math.Max(1, (int)Math.Round(hostSize, MidpointRounding.AwayFromZero));
        }

        if (selfSize > 0)
        {
            return Math.Max(1, (int)Math.Round(selfSize, MidpointRounding.AwayFromZero));
        }

        if (cachedSize > 0)
        {
            return cachedSize;
        }

        return Math.Max(1, fallbackSize);
    }

    private static View? BuildViewportAlignedView(View? view, int viewportWidth, int viewportHeight, int targetWidth, int targetHeight)
    {
        if (view is null)
        {
            return null;
        }

        var logicalWidth = Math.Max(1, targetWidth);
        var logicalHeight = Math.Max(1, targetHeight);
        var viewportRect = CalculateAspectFitRect(Math.Max(1, viewportWidth), Math.Max(1, viewportHeight), logicalWidth, logicalHeight);
        var scale = viewportRect.Width / logicalWidth;
        if (double.IsNaN(scale) || double.IsInfinity(scale) || scale <= 0)
        {
            scale = 1d;
        }

        var logicalCanvas = new Grid
        {
            WidthRequest = logicalWidth,
            HeightRequest = logicalHeight,
            HorizontalOptions = LayoutOptions.Start,
            VerticalOptions = LayoutOptions.Start,
            InputTransparent = true
        };
        logicalCanvas.Children.Add(view);

        return new ContentView
        {
            Content = logicalCanvas,
            WidthRequest = logicalWidth,
            HeightRequest = logicalHeight,
            HorizontalOptions = LayoutOptions.Start,
            VerticalOptions = LayoutOptions.Start,
            InputTransparent = true,
            AnchorX = 0,
            AnchorY = 0,
            Scale = scale,
            TranslationX = viewportRect.X,
            TranslationY = viewportRect.Y
        };
    }

    private static Rect CalculateAspectFitRect(int viewportWidth, int viewportHeight, int targetWidth, int targetHeight)
    {
        if (viewportWidth <= 0 || viewportHeight <= 0 || targetWidth <= 0 || targetHeight <= 0)
        {
            return new Rect(0, 0, Math.Max(1, viewportWidth), Math.Max(1, viewportHeight));
        }

        double ratioViewport = (double)viewportWidth / viewportHeight;
        double ratioTarget = (double)targetWidth / targetHeight;
        double drawW;
        double drawH;
        double offX;
        double offY;

        if (ratioTarget > ratioViewport)
        {
            drawW = viewportWidth;
            drawH = drawW / ratioTarget;
            offX = 0;
            offY = (viewportHeight - drawH) / 2d;
        }
        else
        {
            drawH = viewportHeight;
            drawW = drawH * ratioTarget;
            offX = (viewportWidth - drawW) / 2d;
            offY = 0;
        }

        return new Rect(offX, offY, drawW, drawH);
    }

    private PreparedPreview GenerateClipPreviewPrepared(PreviewRequest request, int canvasWidth, int canvasHeight, int targetWidth, int targetHeight, uint frameIndex, bool applyClipTargetLayout)
    {
        var generatedView = GenerateClipPreview(request, canvasWidth, canvasHeight, targetWidth, targetHeight, frameIndex, out var message, applyClipTargetLayout);
        return new PreparedPreview(request.Clip.Id, generatedView, message, request.Clip);
    }

    private IReadOnlyList<PreviewRequest> ResolveRequests(IReadOnlyList<IClip>? clips, uint frameIndex)
    {
        if (clips is null || clips.Count == 0)
        {
            return [];
        }

        return clips
            .Where(c => c.ClipType != ClipMode.AudioClip && c.ClipType != ClipMode.MarkingClip)
            .Where(c => c.ContainsFrame(frameIndex))
            .OrderByDescending(c => c.LayerIndex)
            .ThenByDescending(c => c.SubLayerIndex)
            .Select(clip => new PreviewRequest(clip, ResolveProvider(clip)))
            .ToArray();
    }

    private View? GenerateClipPreview(PreviewRequest request, int canvasWidth, int canvasHeight, int targetWidth, int targetHeight, uint frameIndex, out string? message, bool applyClipTargetLayout)
    {
        message = null;
        var clip = request.Clip;
        if (clip is null)
        {
            return null;
        }

        var sourceColorAdjustEffects = clip.EffectsInstances?
            .Where(e => e.Enabled)
            .OfType<IColorAdjustEffect>()
            .OrderBy(e => e.Index)
            .ToArray() ?? Array.Empty<IColorAdjustEffect>();

        View? generatedView = null;
        var usedFullRenderFallback = false;
        if (sourceColorAdjustEffects.Length == 0 && request.Provider is not null && request.Provider.IsAvailable(clip))
        {
            try
            {
                generatedView = request.Provider.Generate(clip, canvasWidth, canvasHeight, targetWidth, targetHeight, frameIndex);
            }
            catch (Exception ex)
            {
                message = $"Failed to generate dynamic preview: {ex.Message}";
            }
        }
        else
        {
            if (sourceColorAdjustEffects.Length > 0)
            {
                Log($"Clip {clip.Id}/{clip.Name} has source color-adjust effects, using frame fallback for source rendering.");
            }
            else
            {
                Log($"Clip {clip.Id}/{clip.Name} does not have a dynamic preview provider, using frame fallback.");
            }

            var useFullRenderFallback = _previewer is not null && sourceColorAdjustEffects.Length == 0;
            generatedView = GenerateFrameFallbackView(clip, canvasWidth, canvasHeight, frameIndex, useFullRenderFallback, sourceColorAdjustEffects);
            usedFullRenderFallback = useFullRenderFallback;
        }

        if (generatedView is null)
        {
            try
            {
                generatedView = GenerateFrameFallbackView(clip, canvasWidth, canvasHeight, frameIndex);
            }
            catch (Exception ex)
            {
                message = $"Failed to render fallback frame: {ex.Message}";
                return null;
            }
        }

        if (generatedView is null)
        {
            return null;
        }

        if (clip.EffectsInstances?.Any() == true && !usedFullRenderFallback)
        {
            foreach (var effect in clip.EffectsInstances
                .Where(e => e.Enabled)
                .OrderBy(e => e.Index))
            {
                if (effect is IColorAdjustEffect)
                {
                    continue;
                }

                var isLegacyLayoutEffect = IsLegacyInternalLayoutEffect(effect);
                if (isLegacyLayoutEffect)
                {
                    // In prepared-preview mode, InteractableEditor owns clip placement/size.
                    // Applying legacy internal place/resize here causes double layout scaling.
                    if (!applyClipTargetLayout)
                    {
                        continue;
                    }

                    if (HasExplicitTargetRect(clip))
                    {
                        continue;
                    }
                }
                var provider = ResolveEffectProvider(effect, generatedView.GetType());
                if (provider?.IsAvailable(effect, generatedView.GetType()) ?? false)
                {
                    generatedView = ApplyEffectPreview(generatedView, effect, canvasWidth, canvasHeight, frameIndex);
                }
                else
                {
                    Log($"Clip {clip.Id}/{clip.Name}'s Effect {effect.TypeName}/{effect.Name} does not support dynamic preview, using frame fallback.");
                    var useFullRenderFallback = _previewer is not null;
                    generatedView = GenerateFrameFallbackView(clip, canvasWidth, canvasHeight, frameIndex, useFullRenderFallback);
                    usedFullRenderFallback = useFullRenderFallback;
                    break;
                }
            }
        }

        if (generatedView is null)
        {
            return null;
        }

        generatedView.AutomationId ??= $"clip={clip.ClipType},id={clip.Id}";

        if (!applyClipTargetLayout)
        {
            if (clip.ClipType == ClipMode.TextClip)
            {
                return NormalizeTextPreparedPreviewToClipLocal(generatedView, clip, targetWidth, targetHeight);
            }

            return generatedView;
        }

        if (usedFullRenderFallback)
        {
            return generatedView;
        }

        return ApplyClipTargetLayoutPreview(generatedView, clip, canvasWidth, canvasHeight);
    }

    private static View ApplyClipTargetLayoutPreview(View input, IClip clip, int canvasWidth, int canvasHeight)
    {
        if (!HasExplicitTargetRect(clip))
        {
            return ApplyImplicitClipAutoCenterPreview(input, clip, canvasHeight);
        }

        var width = clip.TargetWidth > 0 ? clip.TargetWidth : Math.Max(1, canvasWidth);
        var height = clip.TargetHeight > 0 ? clip.TargetHeight : Math.Max(1, canvasHeight);

        input.WidthRequest = Math.Max(1, width);
        input.HeightRequest = Math.Max(1, height);
        input.HorizontalOptions = LayoutOptions.Start;
        input.VerticalOptions = LayoutOptions.Start;
        input.TranslationX = clip.TargetX;
        input.TranslationY = clip.TargetY;
        return input;
    }

    private static View ApplyImplicitClipAutoCenterPreview(View input, IClip clip, int canvasHeight)
    {
        if (HasExplicitTargetRect(clip) || HasLegacyInternalPlaceResizeEffects(clip))
        {
            return input;
        }

        if (Math.Abs(input.TranslationY) > 0.01d)
        {
            return input;
        }

        var requestedHeight = input.HeightRequest;
        if (requestedHeight <= 0 || requestedHeight >= canvasHeight)
        {
            return input;
        }

        input.TranslationY += (canvasHeight - requestedHeight) / 2d;
        return input;
    }

    private static View NormalizeTextPreparedPreviewToClipLocal(View input, IClip clip, int targetWidth, int targetHeight)
    {
        var viewportWidth = Math.Max(1d, targetWidth);
        var viewportHeight = Math.Max(1d, targetHeight);
        var hasMeasuredClipRect = TryResolveTextClipRectForPreparedPreview(clip, out var clipRect);

        if (!hasMeasuredClipRect)
        {
            clipRect = new Rect(0, 0, viewportWidth, viewportHeight);
        }

        // Keep clamp behavior identical to InteractableEditor: preserve size, shift origin in-bounds.
        var clippedW = Math.Clamp(clipRect.Width, MinTextPreviewSize, viewportWidth);
        var clippedH = Math.Clamp(clipRect.Height, MinTextPreviewSize, viewportHeight);
        var clippedX = Math.Clamp(clipRect.X, 0, viewportWidth - clippedW);
        var clippedY = Math.Clamp(clipRect.Y, 0, viewportHeight - clippedH);
        var clippedRect = new Rect(clippedX, clippedY, clippedW, clippedH);

        var worldHost = new Grid
        {
            WidthRequest = viewportWidth,
            HeightRequest = viewportHeight,
            HorizontalOptions = LayoutOptions.Start,
            VerticalOptions = LayoutOptions.Start,
            InputTransparent = true,
            TranslationX = -clippedRect.X,
            TranslationY = -clippedRect.Y
        };
        worldHost.Children.Add(input);

        var debugTag = $"txtLocal src={(hasMeasuredClipRect ? "measured" : "fallback")},crop={Math.Round(clippedRect.X)},{Math.Round(clippedRect.Y)},{Math.Round(clippedRect.Width)}x{Math.Round(clippedRect.Height)}";
        return new ContentView
        {
            Content = worldHost,
            WidthRequest = clippedRect.Width,
            HeightRequest = clippedRect.Height,
            HorizontalOptions = LayoutOptions.Start,
            VerticalOptions = LayoutOptions.Start,
            InputTransparent = true,
            AutomationId = debugTag,
            Clip = new RectangleGeometry
            {
                Rect = new Rect(0, 0, clippedRect.Width, clippedRect.Height)
            }
        };
    }

    private static bool TryResolveTextClipRectForPreparedPreview(IClip clip, out Rect rect)
    {
        rect = default;
        if (clip is not TextClip textClip)
        {
            return false;
        }

        if (!TryResolveTextEntriesForPreparedPreview(textClip, out var entries))
        {
            return false;
        }

        var hasBounds = false;
        double minX = 0;
        double minY = 0;
        double maxX = 0;
        double maxY = 0;

        foreach (var entry in entries)
        {
            if (!TryMeasureTextEntryRectForPreparedPreview(entry, out var x, out var y, out var w, out var h))
            {
                continue;
            }

            var left = x;
            var top = y;
            var right = x + w;
            var bottom = y + h;

            if (!hasBounds)
            {
                minX = left;
                minY = top;
                maxX = right;
                maxY = bottom;
                hasBounds = true;
            }
            else
            {
                minX = Math.Min(minX, left);
                minY = Math.Min(minY, top);
                maxX = Math.Max(maxX, right);
                maxY = Math.Max(maxY, bottom);
            }
        }

        if (!hasBounds)
        {
            return false;
        }

        rect = new Rect(
            minX,
            minY,
            Math.Max(MinTextPreviewSize, maxX - minX),
            Math.Max(MinTextPreviewSize, maxY - minY));
        return true;
    }

    private static bool TryResolveTextEntriesForPreparedPreview(TextClip clip, out List<TextClipEntry> entries)
    {
        entries = null!;

        List<TextClipEntry>? extraEntries = null;

        if (clip.ExtraData?.TryGetValue("TextEntries", out var rawEntries) == true)
        {
            if (rawEntries is List<TextClipEntry> list && list.Count > 0)
            {
                extraEntries = list;
            }

            else if (rawEntries is JsonElement element)
            {
                try
                {
                    var parsed = element.Deserialize<List<TextClipEntry>>();
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

        var selected = PickBetterTextEntries(extraEntries, clip.TextEntries);
        if (selected.Count > 0)
        {
            entries = selected is List<TextClipEntry> selectedList
                ? selectedList
                : selected.ToList();
            return true;
        }

        return false;
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

    private static bool TryMeasureTextEntryRectForPreparedPreview(TextClipEntry entry, out double x, out double y, out double w, out double h)
    {
        x = entry.x;
        y = entry.y;
        w = MinTextPreviewSize;
        h = MinTextPreviewSize;

        var rawText = entry.text ?? string.Empty;
        var textForMeasure = string.IsNullOrEmpty(rawText) ? " " : rawText;
        var dpi = entry.dpi ?? 72d;
        var fontSize = Math.Max(1d, entry.fontSize * (dpi / 72d));
        var strokeExtra = Math.Max(0d, entry.strokeWidth ?? 0f) * 2d;

        if (entry.UseVerticalLayout)
        {
            var glyphCount = rawText.Count(c => c != '\n' && c != '\r');
            if (glyphCount <= 0)
            {
                glyphCount = 1;
            }

            var lineAdvance = fontSize * Math.Max(0.1d, entry.lineSpacing);
            w = Math.Max(MinTextPreviewSize, fontSize + strokeExtra);
            h = Math.Max(MinTextPreviewSize, glyphCount * lineAdvance + strokeExtra);
        }
        else
        {
            var measurementLabel = new Label();
            try
            {
                measurementLabel.Text = textForMeasure;
                measurementLabel.FontSize = fontSize;
                measurementLabel.FontFamily = string.IsNullOrWhiteSpace(entry.fontFamily) ? null : entry.fontFamily;
                measurementLabel.FontAttributes = entry.fontStyle switch
                {
                    SixLabors.Fonts.FontStyle.Bold => FontAttributes.Bold,
                    SixLabors.Fonts.FontStyle.Italic => FontAttributes.Italic,
                    SixLabors.Fonts.FontStyle.BoldItalic => FontAttributes.Bold | FontAttributes.Italic,
                    _ => FontAttributes.None
                };

                var wrappingWidth = entry.wrappingWidth.HasValue && entry.wrappingWidth.Value > 0
                    ? entry.wrappingWidth.Value
                    : 0f;

                if (wrappingWidth > 0)
                {
                    measurementLabel.WidthRequest = wrappingWidth;
                    measurementLabel.LineBreakMode = LineBreakMode.WordWrap;
                    var wrappedSize = measurementLabel.Measure(wrappingWidth, double.PositiveInfinity);
                    w = wrappedSize.Width;
                    h = wrappedSize.Height;
                }
                else
                {
                    measurementLabel.WidthRequest = -1;
                    measurementLabel.LineBreakMode = LineBreakMode.NoWrap;
                    var size = measurementLabel.Measure(double.PositiveInfinity, double.PositiveInfinity);
                    w = size.Width;
                    h = size.Height;
                }
            }
            catch
            {
                var fallbackWidth = Math.Max(1, textForMeasure.Length) * fontSize * 0.6d;
                var fallbackHeight = fontSize * 1.2d;
                w = fallbackWidth;
                h = fallbackHeight;
            }

            if (w <= 1d || h <= 1d)
            {
                EstimateTextEntryRectSize(entry, textForMeasure, fontSize, out var fallbackW, out var fallbackH);
                w = fallbackW;
                h = fallbackH;
            }

            w = Math.Max(MinTextPreviewSize, w + strokeExtra);
            h = Math.Max(MinTextPreviewSize, h + strokeExtra);
        }

        switch (entry.horizontalAlignment)
        {
            case SixLabors.Fonts.HorizontalAlignment.Center:
                x -= w / 2d;
                break;
            case SixLabors.Fonts.HorizontalAlignment.Right:
                x -= w;
                break;
        }

        switch (entry.verticalAlignment)
        {
            case SixLabors.Fonts.VerticalAlignment.Center:
                y -= h / 2d;
                break;
            case SixLabors.Fonts.VerticalAlignment.Bottom:
                y -= h;
                break;
        }

        if (Math.Abs(entry.rotation) > 0.0001f)
        {
            var radians = entry.rotation * Math.PI / 180d;
            var cos = Math.Cos(radians);
            var sin = Math.Sin(radians);

            static (double rx, double ry) Rotate(double px, double py, double cosV, double sinV)
                => (px * cosV - py * sinV, px * sinV + py * cosV);

            var p0 = Rotate(0, 0, cos, sin);
            var p1 = Rotate(w, 0, cos, sin);
            var p2 = Rotate(0, h, cos, sin);
            var p3 = Rotate(w, h, cos, sin);

            var minRx = Math.Min(Math.Min(p0.rx, p1.rx), Math.Min(p2.rx, p3.rx));
            var minRy = Math.Min(Math.Min(p0.ry, p1.ry), Math.Min(p2.ry, p3.ry));
            var maxRx = Math.Max(Math.Max(p0.rx, p1.rx), Math.Max(p2.rx, p3.rx));
            var maxRy = Math.Max(Math.Max(p0.ry, p1.ry), Math.Max(p2.ry, p3.ry));

            x = entry.x + minRx;
            y = entry.y + minRy;
            w = Math.Max(MinTextPreviewSize, maxRx - minRx);
            h = Math.Max(MinTextPreviewSize, maxRy - minRy);
        }

        return true;
    }

    private static void EstimateTextEntryRectSize(TextClipEntry entry, string textForMeasure, double fontSize, out double width, out double height)
    {
        var lineHeight = Math.Max(1d, fontSize * Math.Max(0.8d, entry.lineSpacing));
        var lineCount = 1;
        foreach (var c in textForMeasure)
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

            foreach (var c in textForMeasure)
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
            return;
        }

        var maxCharsInLine = 0;
        var currentChars = 0;
        foreach (var c in textForMeasure)
        {
            if (c == '\r')
            {
                continue;
            }

            if (c == '\n')
            {
                if (currentChars > maxCharsInLine)
                {
                    maxCharsInLine = currentChars;
                }

                currentChars = 0;
                continue;
            }

            currentChars++;
        }

        if (currentChars > maxCharsInLine)
        {
            maxCharsInLine = currentChars;
        }

        if (maxCharsInLine <= 0)
        {
            maxCharsInLine = 1;
        }

        width = maxCharsInLine * fontSize * 0.56d;
        height = lineCount * lineHeight;
    }

    private static bool HasExplicitTargetRect(IClip clip)
        => clip.TargetX != 0 || clip.TargetY != 0 || clip.TargetWidth > 0 || clip.TargetHeight > 0;

    private static bool HasLegacyInternalPlaceResizeEffects(IClip clip)
    {
        if (clip.EffectsInstances?.Any() != true)
        {
            return false;
        }

        return clip.EffectsInstances.Any(IsLegacyInternalLayoutEffect);
    }

    private static bool IsLegacyInternalLayoutEffect(IEffect effect)
    {
        if (string.Equals(effect.Name, "__Internal_Place__", StringComparison.Ordinal)
            || string.Equals(effect.Name, "__Internal_Resize__", StringComparison.Ordinal))
        {
            return true;
        }

        if (!string.Equals(effect.FromPlugin, InternalPluginBase.InternalPluginBaseID, StringComparison.Ordinal))
        {
            return false;
        }

        if (string.Equals(effect.TypeName, "Place", StringComparison.OrdinalIgnoreCase)
            || string.Equals(effect.TypeName, "Resize", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        return false;
    }

    [DebuggerStepThrough()]
    private static IClipDynamicPreviewProvider? ResolveProvider(IClip clip)
    {
        if (PluginManager.LoadedPlugins.TryGetValue(clip.FromPlugin, out var ownerPlugin)
            && ownerPlugin is IApplicationPluginBase appPlugin)
        {
            var provider = ResolveProviderFromDictionary(appPlugin.ClipDynamicPreviewProvider, clip);
            if (provider is not null)
            {
                return provider;
            }
        }

        return null;
    }

    private static IClipDynamicPreviewProvider? ResolveProviderFromDictionary(IReadOnlyDictionary<string, IClipDynamicPreviewProvider> providers, IClip clip)
    {
        if (providers.Count == 0)
        {
            return null;
        }

        if (!string.IsNullOrWhiteSpace(clip.TypeName)
            && providers.TryGetValue(clip.TypeName, out var typedProvider)
            && IsProviderAvailable(typedProvider, clip))
        {
            return typedProvider;
        }

        var clipModeName = clip.ClipType.ToString();
        if (providers.TryGetValue(clipModeName, out var modeProvider) && IsProviderAvailable(modeProvider, clip))
        {
            return modeProvider;
        }

        return providers.Values.FirstOrDefault(provider => IsProviderAvailable(provider, clip));
    }

    private static bool IsProviderAvailable(IClipDynamicPreviewProvider provider, IClip clip)
    {
        try
        {
            return provider.IsAvailable(clip);
        }
        catch
        {
            return false;
        }
    }

    private View GenerateFrameFallbackView(IClip clip, int targetWidth, int targetHeight, uint frameIndex, bool fullRender = false, IReadOnlyList<IColorAdjustEffect>? sourceColorAdjustEffects = null)
    {
        IPicture frame = null!;
        if (!fullRender)
        {
            frame = clip.GetFrame(frameIndex, targetWidth, targetHeight, true, IPicture.PicturePixelMode.BytePicture);
        }
        else
        {
            if (_previewer is not null)
            {
                frame = _previewer.GetFrame(frameIndex, targetWidth, targetHeight);
            }
            else
            {
                // Full-render fallback needs LivePreviewer; when unavailable, degrade to clip-local frame fallback.
                frame = clip.GetFrame(frameIndex, targetWidth, targetHeight, true, IPicture.PicturePixelMode.BytePicture);
            }
        }

        if (sourceColorAdjustEffects is { Count: > 0 })
        {
            foreach (var effect in sourceColorAdjustEffects)
            {
                try
                {
                    frame = effect.Process(frame, PluginManager.CreateComputer(effect.NeedComputer));
                }
                catch (Exception ex)
                {
                    Log($"Clip {clip.Id}/{clip.Name}'s color adjust effect {effect.TypeName}/{effect.Name} failed during source rendering: {ex.Message}");
                }
            }
        }

        return new Image
        {
            Source = frame.ToImageSource(),
            Aspect = Aspect.Fill,
            HorizontalOptions = LayoutOptions.Fill,
            VerticalOptions = LayoutOptions.Fill,
        };
    }

    private static View ApplyEffectPreview(View input, IEffect effect, int targetWidth, int targetHeight, uint frameIndex)
    {
        var provider = ResolveEffectProvider(effect, input.GetType());
        if (provider is null)
        {
            return input;
        }

        try
        {
            return provider.Generate(effect, input, input.GetType(), targetWidth, targetHeight, frameIndex) ?? input;
        }
        catch
        {
            return input;
        }
    }

    private static IEffectDynamicPreviewProvider? ResolveEffectProvider(IEffect effect, Type typeOfInput)
    {
        if (PluginManager.LoadedPlugins.TryGetValue(effect.FromPlugin, out var ownerPlugin)
            && ownerPlugin is IApplicationPluginBase appPlugin)
        {
            var provider = ResolveEffectProviderFromDictionary(appPlugin.EffectDynamicPreviewProvider, effect, typeOfInput);
            if (provider is not null)
            {
                return provider;
            }
        }

        return null;
    }

    private static IEffectDynamicPreviewProvider? ResolveEffectProviderFromDictionary(IReadOnlyDictionary<string, IEffectDynamicPreviewProvider> providers, IEffect effect, Type typeOfInput)
    {
        if (providers.Count == 0)
        {
            return null;
        }

        if (!string.IsNullOrWhiteSpace(effect.TypeName)
            && providers.TryGetValue(effect.TypeName, out var typedProvider)
            && IsEffectProviderAvailable(typedProvider, effect, typeOfInput))
        {
            return typedProvider;
        }

        return providers.Values.FirstOrDefault(provider => IsEffectProviderAvailable(provider, effect, typeOfInput));
    }

    private static bool IsEffectProviderAvailable(IEffectDynamicPreviewProvider provider, IEffect effect, Type typeOfInput)
    {
        try
        {
            return provider.IsAvailable(effect, typeOfInput);
        }
        catch
        {
            return false;
        }
    }

    private void DisposeClips()
    {
        List<IClip[]>? batchesToDispose = null;
        lock (_clipsGate)
        {
            if (_clips is not null)
            {
                if (_activePreviewOps > 0)
                {
                    _pendingDisposeClipBatches.Add(_clips);
                }
                else
                {
                    batchesToDispose ??= new List<IClip[]>();
                    batchesToDispose.Add(_clips);
                }
            }

            _clips = null;

            if (_activePreviewOps == 0 && _pendingDisposeClipBatches.Count > 0)
            {
                batchesToDispose ??= new List<IClip[]>();
                batchesToDispose.AddRange(_pendingDisposeClipBatches);
                _pendingDisposeClipBatches.Clear();
            }
        }

        if (batchesToDispose is null)
        {
            return;
        }

        foreach (var batch in batchesToDispose)
        {
            DisposeClipBatch(batch);
        }
    }

    private IClip[] AcquireClipsSnapshot()
    {
        lock (_clipsGate)
        {
            _activePreviewOps++;
            return _clips ?? Array.Empty<IClip>();
        }
    }

    private void ReleaseClipsSnapshot()
    {
        List<IClip[]>? batchesToDispose = null;
        lock (_clipsGate)
        {
            if (_activePreviewOps > 0)
            {
                _activePreviewOps--;
            }

            if (_activePreviewOps == 0 && _pendingDisposeClipBatches.Count > 0)
            {
                batchesToDispose = new List<IClip[]>(_pendingDisposeClipBatches);
                _pendingDisposeClipBatches.Clear();
            }
        }

        if (batchesToDispose is null)
        {
            return;
        }

        foreach (var batch in batchesToDispose)
        {
            DisposeClipBatch(batch);
        }
    }

    private static void DisposeClipBatch(IReadOnlyList<IClip> clips)
    {
        foreach (var clip in clips)
        {
            try
            {
                clip.Dispose();
            }
            catch
            {
            }
        }
    }

    private sealed record PreviewRequest(IClip Clip, IClipDynamicPreviewProvider? Provider);

}