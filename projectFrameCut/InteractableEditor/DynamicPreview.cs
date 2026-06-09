using projectFrameCut.ApplicationAPIBase.DynamicPreviewProvider;
using projectFrameCut.ApplicationAPIBase.Helpers;
using projectFrameCut.ApplicationAPIBase.Plugins;
using projectFrameCut.ApplicationAPIBase.Text;
using projectFrameCut.DraftStuff;
using projectFrameCut.LivePreview;
using projectFrameCut.Render.ClipsAndTracks;
using projectFrameCut.Render.Plugin;
using projectFrameCut.Render.RenderAPIBase.ClipAndTrack;
using projectFrameCut.Render.RenderAPIBase.EffectAndMixture;
using projectFrameCut.Render.RenderAPIBase.Project;
using projectFrameCut.Render.Rendering;
using projectFrameCut.Shared;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Text.Json;
using System.Threading;
using IPicture = projectFrameCut.Drawing.Base.IPicture;
using projectFrameCut.Asset;
using Path = System.IO.Path;
using RenderITransform = projectFrameCut.Render.RenderAPIBase.ClipAndTrack.ITransform;
using projectFrameCut.ApplicationPluginBase.DynamicPreviewProvider;
using projectFrameCut.Drawing.Base.Picture;
using projectFrameCut.Drawing.Base;
using projectFrameCut.Drawing.Processing.Resizing;
using projectFrameCut.Drawing.Vector;
using Microsoft.Maui.Controls.Shapes;
using ShapesPath = Microsoft.Maui.Controls.Shapes.Path;
using MauiPoint = Microsoft.Maui.Graphics.Point;

namespace projectFrameCut.InteractableEditor;

public sealed class DynamicPreview : IDisposable
{
    private const int ParallelPreparationThreshold = 2;
    private const int MaxCachedFallbackFrames = 120;
    private const int MaxDiskCachedFallbackFrames = 1500;
    private const string TextStyleParametersKey = "TextStyleProvider_Parameters";
    private const string TextStyleProviderFromKey = "TextStyleProvider_FromPlugin";
    private const string TextStyleProviderTypeKey = "TextStyleProvider_TypeName";
    private static readonly int s_maxPreparationParallelism = Math.Max(1, Environment.ProcessorCount / 2);
    private static readonly IComparer<IClip> s_clipLayerComparer = Comparer<IClip>.Create(static (left, right) =>
    {
        var layerCompare = right.LayerIndex.CompareTo(left.LayerIndex);
        if (layerCompare != 0)
        {
            return layerCompare;
        }

        return right.SubLayerIndex.CompareTo(left.SubLayerIndex);
    });
    private static readonly ConcurrentDictionary<FallbackFrameCacheKey, CachedFallbackFrame> s_fallbackFrameCache = new();
    private static readonly ConcurrentDictionary<string, long> s_fallbackDiskFrameAccess = new(StringComparer.Ordinal);
    public static string DiskCacheRoot { get; set { if (Directory.Exists(value)) field = value; } } = Path.Combine(MauiProgram.DataPath, "RenderCache", "clipLocalFallback");


    public sealed class PreparedPreview
    {
        private readonly Func<View>? _viewFactory;
        private View? _materializedView;

        public PreparedPreview(string clipId, Func<View>? viewFactory, string? errorMessage, IClip? source)
        {
            ClipId = clipId;
            _viewFactory = viewFactory;
            ErrorMessage = errorMessage;
            Source = source;
        }

        public string ClipId { get; }
        public string? ErrorMessage { get; }
        public IClip? Source { get; }

        public View? View
        {
            get
            {
                if (_viewFactory is null) return null;
                if (_materializedView is null)
                    _materializedView = _viewFactory();
                return _materializedView;
            }
        }
    }

    private IClip[]? _clips;
    private LivePreviewer? _previewer;
    private long _renderVersion;
    private int _viewportWidth;
    private int _viewportHeight;
    private readonly object _clipsGate = new();
    private int _activePreviewOps;
    private readonly List<IClip[]> _pendingDisposeClipBatches = new();
    private readonly ConcurrentDictionary<string, byte> _sourceColorFallbackLogKeys = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, byte> _missingProviderFallbackLogKeys = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, byte> _effectFallbackLogKeys = new(StringComparer.Ordinal);
    private long _prepareVersion;
    private readonly object _preparedOverlayCacheGate = new();
    private IReadOnlyList<PreparedPreview> _lastPreparedOverlayPreviews = [];

    public DynamicPreview()
    {

    }

    public IClip[]? Clips => _clips;

    /// <summary>
    /// When true, all effects will be rendered in the Rendering pipeline 
    /// and then the clip will be placed in canvas.
    /// </summary>
    public static bool DisableEffectDynamicPreview { get; set; } = false;

    /// <summary>
    /// When true, IVectorContentClip clips fall back to bitmap rasterization
    /// instead of being converted to MAUI Path elements. Default false (vector Path mode enabled).
    /// </summary>
    public static bool DisableVectorPreviewPaths { get; set; } = false;

    /// <summary>
    /// Divisor for preview resolution. 1 = full resolution, 2 = half, etc.
    /// Reduces rendering load by scaling down canvas dimensions.
    /// </summary>
    public int PreviewResolutionDivisor { get; set; } = 1;

    public async Task<IReadOnlyList<PreparedPreview>> PrepareFrameAsync(uint frameIndex, int targetWidth, int targetHeight, double CanvasWidth, double CanvasHeight, CancellationToken token)
    {
        var prepareVersion = Interlocked.Increment(ref _prepareVersion);
        try
        {
            var dimensions = ResolveDimensions(targetWidth, targetHeight, CanvasWidth, CanvasHeight);
            return await GetFinalRequests(frameIndex, targetWidth, targetHeight, prepareVersion, dimensions.canvasWidth, dimensions.canvasHeight, token).ConfigureAwait(false);
        }
        finally
        {
            ReleaseClipsSnapshot();
        }
    }

    public (int canvasWidth, int canvasHeight) ResolveDimensions(int targetWidth, int targetHeight, double CanvasWidth, double CanvasHeight)
    {
        var canvasWidth = ResolveCanvasSize(CanvasWidth, _viewportWidth, targetWidth);
        var canvasHeight = ResolveCanvasSize(CanvasHeight, _viewportHeight, targetHeight);
        var divisor = Math.Max(1, PreviewResolutionDivisor);
        if (divisor > 1)
        {
            canvasWidth = Math.Max(1, canvasWidth / divisor);
            canvasHeight = Math.Max(1, canvasHeight / divisor);
        }

        return (canvasWidth, canvasHeight);
    }

    public async Task<IReadOnlyList<PreparedPreview>> GetFinalRequests(uint frameIndex, int targetWidth, int targetHeight, long prepareVersion, int canvasWidth, int canvasHeight, CancellationToken token)
    {
        var clipsSnapshot = AcquireClipsSnapshot();
        var requests = ResolveRequests(clipsSnapshot, frameIndex, canvasWidth, canvasHeight);
        var prepared = await PrepareRequestsAsync(requests, canvasWidth, canvasHeight, targetWidth, targetHeight, frameIndex, applyClipTargetLayout: false, checkVersion: true, prepareVersion, token).ConfigureAwait(false);
        if (prepared is not null)
        {
            return prepared;
        }
        if (token.IsCancellationRequested) return null!;
        return GetCachedOverlayPreparedPreviews();
    }

    public async Task UpdateDraft(DraftStructureJSON json)
    {
        ArgumentNullException.ThrowIfNull(json);
        var elements = (JsonSerializer.SerializeToElement(json).Deserialize<DraftStructureJSON>()?.Clips) ?? throw new NullReferenceException("Failed to cast ClipDraftDTOs to IClips."); //I don't want to write a lot of code to clone attributes from dto to IClip, it's too hard and may cause a lot of mystery bugs.

        var clipsList = new List<IClip>();
        var reinitTasks = new List<Task>();

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
            clipsList.Add(clipInstance);
            reinitTasks.Add(Task.Run(() => clipInstance.ReInit(8)));
        }

        await Task.WhenAll(reinitTasks);

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

        ResetFallbackLogs();
        CacheOverlayPreparedPreviews([]);
    }

    public void SetClips(IClip[]? clips)
    {
        if (clips is null)
        {
            return;
        }

        IClip[]? batchToDispose = null;
        lock (_clipsGate)
        {
            var oldClips = _clips;
            _clips = clips;
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

        ResetFallbackLogs();
        CacheOverlayPreparedPreviews([]);
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

    public void Dispose()
    {
        Interlocked.Increment(ref _renderVersion);
        Interlocked.Increment(ref _prepareVersion);
        DisposeClips();
        ResetFallbackLogs();
        CacheOverlayPreparedPreviews([]);
    }

    public async Task<IReadOnlyList<PreparedPreview>?> PrepareRequestsAsync(IReadOnlyList<PreviewRequest> requests, int canvasWidth, int canvasHeight, int targetWidth, int targetHeight, uint frameIndex, bool applyClipTargetLayout, bool checkVersion, long prepareVersion, CancellationToken token)
    {
        if (requests.Count == 0)
        {
            return [];
        }

        var orderedRequests = new PreviewRequest[requests.Count];
        for (var i = 0; i < requests.Count; i++)
        {
            orderedRequests[i] = requests[requests.Count - 1 - i];
        }

        var prepared = new PreparedPreview[orderedRequests.Length];
        if (orderedRequests.Length < ParallelPreparationThreshold)
        {
            for (var i = 0; i < orderedRequests.Length; i++)
            {
                prepared[i] = GenerateClipPreviewPrepared(orderedRequests[i], canvasWidth, canvasHeight, targetWidth, targetHeight, frameIndex, applyClipTargetLayout, token);
            }

            return prepared;
        }

        using var semaphore = new SemaphoreSlim(s_maxPreparationParallelism);
        var tasks = new Task[orderedRequests.Length];

        for (var i = 0; i < orderedRequests.Length; i++)
        {
            var index = i;
            tasks[i] = ThrottledPrepareAsync(index);
        }

        await Task.WhenAll(tasks).ConfigureAwait(false);

        if (checkVersion && prepareVersion != Interlocked.Read(ref _prepareVersion))
        {
            return null;
        }
        if (prepared.Length > 0) CacheOverlayPreparedPreviews(prepared);
        return prepared;

        async Task ThrottledPrepareAsync(int index)
        {
            await semaphore.WaitAsync(token).ConfigureAwait(false);
            try
            {
                if (token.IsCancellationRequested) return;
                if (checkVersion && prepareVersion != Volatile.Read(ref _prepareVersion)) return;

                prepared[index] = await Task.Run(() =>
                    GenerateClipPreviewPrepared(orderedRequests[index], canvasWidth, canvasHeight, targetWidth, targetHeight, frameIndex, applyClipTargetLayout, token),
                    token).ConfigureAwait(false);
            }
            finally
            {
                semaphore.Release();
            }
        }
    }

    private static int ResolveCanvasSize(double canvasSize, int cachedSize, int fallbackSize)
    {
        if (canvasSize > 0)
        {
            return Math.Max(1, (int)Math.Round(canvasSize, MidpointRounding.AwayFromZero));
        }

        if (cachedSize > 0)
        {
            return cachedSize;
        }

        return Math.Max(1, fallbackSize);
    }

    private static bool TryUpdatePreviewTreeInPlace(View existing, View incoming)
    {
        if (ReferenceEquals(existing, incoming))
        {
            return true;
        }

        if (existing.GetType() != incoming.GetType())
        {
            return false;
        }

        ApplySharedViewState(existing, incoming);

        switch (existing)
        {
            case Image existingImage when incoming is Image incomingImage:
                existingImage.Aspect = incomingImage.Aspect;
                existingImage.Source = incomingImage.Source;
                return true;
            case ContentView existingContent when incoming is ContentView incomingContent:
                if (incomingContent.Content is null)
                {
                    existingContent.Content = null;
                    return true;
                }

                if (existingContent.Content is not View existingContentChild
                    || incomingContent.Content is not View incomingContentChild)
                {
                    return false;
                }

                return TryUpdatePreviewTreeInPlace(existingContentChild, incomingContentChild);
            case Grid existingGrid when incoming is Grid incomingGrid:
                if (existingGrid.Children.Count != incomingGrid.Children.Count)
                {
                    return false;
                }

                for (var i = 0; i < existingGrid.Children.Count; i++)
                {
                    if (existingGrid.Children[i] is not View existingGridChild
                        || incomingGrid.Children[i] is not View incomingGridChild
                        || !TryUpdatePreviewTreeInPlace(existingGridChild, incomingGridChild))
                    {
                        return false;
                    }
                }

                return true;
            default:
                return false;
        }
    }

    private static void ApplySharedViewState(View target, View source)
    {
        target.WidthRequest = source.WidthRequest;
        target.HeightRequest = source.HeightRequest;
        target.MinimumWidthRequest = source.MinimumWidthRequest;
        target.MinimumHeightRequest = source.MinimumHeightRequest;
        target.MaximumWidthRequest = source.MaximumWidthRequest;
        target.MaximumHeightRequest = source.MaximumHeightRequest;
        target.HorizontalOptions = source.HorizontalOptions;
        target.VerticalOptions = source.VerticalOptions;
        target.Margin = source.Margin;
        target.InputTransparent = source.InputTransparent;
        target.AnchorX = source.AnchorX;
        target.AnchorY = source.AnchorY;
        target.Scale = source.Scale;
        target.ScaleX = source.ScaleX;
        target.ScaleY = source.ScaleY;
        target.TranslationX = source.TranslationX;
        target.TranslationY = source.TranslationY;
        target.Rotation = source.Rotation;
        target.RotationX = source.RotationX;
        target.RotationY = source.RotationY;
        target.Opacity = source.Opacity;
        target.IsVisible = source.IsVisible;
        target.ZIndex = source.ZIndex;
        target.AutomationId = source.AutomationId;
    }

    public void CacheOverlayPreparedPreviews(IReadOnlyList<PreparedPreview> prepared)
    {
        lock (_preparedOverlayCacheGate)
        {
            _lastPreparedOverlayPreviews = prepared.Count == 0 ? [] : prepared.ToArray();
        }
    }

    private IReadOnlyList<PreparedPreview> GetCachedOverlayPreparedPreviews()
    {
        lock (_preparedOverlayCacheGate)
        {
            return _lastPreparedOverlayPreviews;
        }
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

    private PreparedPreview GenerateClipPreviewPrepared(PreviewRequest request, int canvasWidth, int canvasHeight, int targetWidth, int targetHeight, uint frameIndex, bool applyClipTargetLayout, CancellationToken token)
    {
        var generatedView = GenerateClipPreview(request, canvasWidth, canvasHeight, targetWidth, targetHeight, frameIndex, out var message, applyClipTargetLayout, token);
        Func<View>? viewFactory = generatedView is not null ? () => generatedView : null;
        return new PreparedPreview(request.Clip.Id, viewFactory, message, request.Clip);
    }

    public IReadOnlyList<PreviewRequest> ResolveRequests(IReadOnlyList<IClip>? clips, uint frameIndex, int canvasWidth = 0, int canvasHeight = 0)
    {
        if (clips is null || clips.Count == 0)
        {
            return [];
        }

        var visibleClips = new List<IClip>(clips.Count);
        for (var i = 0; i < clips.Count; i++)
        {
            var clip = clips[i];
            if (clip.ClipType == ClipMode.AudioClip || clip.ClipType == ClipMode.MarkingClip)
            {
                continue;
            }

            if (!clip.ContainsFrame(frameIndex))
            {
                continue;
            }

            visibleClips.Add(clip);
        }

        if (visibleClips.Count == 0)
        {
            return [];
        }

        var clipIndex = new Dictionary<string, IClip>(clips.Count, StringComparer.Ordinal);
        for (var i = 0; i < clips.Count; i++)
        {
            var sourceClip = clips[i];
            if (!string.IsNullOrWhiteSpace(sourceClip.Id))
            {
                clipIndex[sourceClip.Id] = sourceClip;
            }
        }

        visibleClips.Sort(s_clipLayerComparer);
        var requests = new PreviewRequest[visibleClips.Count];
        for (var i = 0; i < visibleClips.Count; i++)
        {
            var clip = visibleClips[i];
            if (clip is TransformContainer transformClip)
            {
                BindTransformRuntimeSources(transformClip, clipIndex);
            }

            if (clip is TextClip textClip)
            {
                var styleProvider = ResolveTextClipStyleProvider(clip);
                if (styleProvider is not null)
                {
                    var savedParams = ReadTextStyleParameters(clip.ExtraData);
                    if (savedParams is not null)
                    {
                        styleProvider.Parameters = savedParams;
                    }

                    clip.ExtraData ??= new Dictionary<string, object>(StringComparer.Ordinal);
                    clip.ExtraData[TextStyleParametersKey] = new Dictionary<string, string>(styleProvider.Parameters);
                    var resolvedEntries = TextMeasureHelper.ResolveEntries(textClip);
                    if (resolvedEntries.Count == 0)
                    {
                        var entries = styleProvider.BuildEntries();
                        if (entries.Length > 0)
                        {
                            var rebuiltEntries = new List<TextClipEntry>(entries);
                            clip.ExtraData["TextEntries"] = rebuiltEntries;
                            resolvedEntries = rebuiltEntries;
                        }
                    }

                    if (textClip.TargetWidth <= 0 && textClip.TargetHeight <= 0
                        && canvasWidth > 0 && canvasHeight > 0)
                    {
                        if (resolvedEntries.Count > 0)
                        {
                            var bounds = TextMeasureHelper.MeasureBounds(resolvedEntries);
                            textClip.TargetX = (int)Math.Round(bounds.X);
                            textClip.TargetY = (int)Math.Round(bounds.Y);
                            textClip.TargetWidth = Math.Max(1, (int)Math.Ceiling(bounds.Width));
                            textClip.TargetHeight = Math.Max(1, (int)Math.Ceiling(bounds.Height));
                        }
                        else
                        {
                            var rect = styleProvider.GetViewRect(canvasWidth, canvasHeight);
                            if (!rect.IsDelta)
                            {
                                textClip.TargetX = rect.TargetX;
                                textClip.TargetY = rect.TargetY;
                                textClip.TargetWidth = Math.Max(1, rect.TargetWidth);
                                textClip.TargetHeight = Math.Max(1, rect.TargetHeight);
                            }
                        }
                    }
                }
                else if (textClip.TargetWidth <= 0 && textClip.TargetHeight <= 0)
                {
                    var bounds = TextMeasureHelper.MeasureBounds(textClip);
                    if (bounds.Width > 0 && bounds.Height > 0)
                    {
                        textClip.TargetX = (int)bounds.X;
                        textClip.TargetY = (int)bounds.Y;
                        textClip.TargetWidth = (int)Math.Ceiling(bounds.Width);
                        textClip.TargetHeight = (int)Math.Ceiling(bounds.Height);
                    }
                }
            }

            requests[i] = new PreviewRequest(clip, ResolveProvider(clip));
        }

        return requests;
    }

    private static void BindTransformRuntimeSources(TransformContainer transformClip, IReadOnlyDictionary<string, IClip> clipIndex)
    {
        if (transformClip.ExtraData is null || transformClip.Transform is not RenderITransform transform)
        {
            return;
        }

        if (clipIndex.TryGetValue(transform.BindedLeftClip.ToString(), out var leftClip))
        {
            transformClip.ExtraData[TransformClipDynamicPreviewRuntimeKeys.LeftClip] = leftClip;
        }
        else
        {
            transformClip.ExtraData.Remove(TransformClipDynamicPreviewRuntimeKeys.LeftClip);
        }

        if (clipIndex.TryGetValue(transform.BindedRightClip.ToString(), out var rightClip))
        {
            transformClip.ExtraData[TransformClipDynamicPreviewRuntimeKeys.RightClip] = rightClip;
        }
        else
        {
            transformClip.ExtraData.Remove(TransformClipDynamicPreviewRuntimeKeys.RightClip);
        }
    }

    private View? GenerateClipPreview(PreviewRequest request, int canvasWidth, int canvasHeight, int targetWidth, int targetHeight, uint frameIndex, out string? message, bool applyClipTargetLayout, CancellationToken token)
    {
        Stopwatch diagSW = Stopwatch.StartNew();
        message = null;
        var clip = request.Clip;
        if (clip is null)
        {
            return null;
        }

        var enabledEffects = GetEnabledEffectsSorted(clip.EffectsInstances);
        List<IColorAdjustEffect>? sourceColorAdjustEffects = null;
        for (var i = 0; i < enabledEffects.Length; i++)
        {
            if (token.IsCancellationRequested) return null;
            if (enabledEffects[i] is IColorAdjustEffect colorAdjustEffect)
            {
                sourceColorAdjustEffects ??= new List<IColorAdjustEffect>(enabledEffects.Length);
                sourceColorAdjustEffects.Add(colorAdjustEffect);
            }
        }
        if (token.IsCancellationRequested) return null;

        var willUseEffectFallback = DisableEffectDynamicPreview && enabledEffects.Length > 0;

        View? generatedView = null;
        var usedFullRenderFallback = false;
        if (willUseEffectFallback)
        {
            LogOnce(_effectFallbackLogKeys, clip.Id, $"Clip {clip.Id}/{clip.Name} has {enabledEffects.Length} effect(s), using clip-local fallback (DisableEffectDynamicPreview=true).");
        }
        else if ((sourceColorAdjustEffects?.Count ?? 0) == 0 && request.Provider is not null)
        {
            if (clip is IVectorContentClip vectorClip && !DisableVectorPreviewPaths)
            {
                try
                {
                    generatedView = BuildVectorPreviewView(vectorClip, canvasWidth, canvasHeight, targetWidth, targetHeight, frameIndex);
                }
                catch (Exception ex)
                {
                    message = $"Failed to generate vector preview: {ex.Message}";
                }
            }
            else
            {
                try
                {
                    var projectRelativeWidth = _previewer?.ProjectRelativeWidth > 0 ? _previewer.ProjectRelativeWidth : canvasWidth;
                    var projectRelativeHeight = _previewer?.ProjectRelativeHeight > 0 ? _previewer.ProjectRelativeHeight : canvasHeight;
                    DynamicPreviewRenderContext.Set(new DynamicPreviewRenderContext.State(
                        Math.Max(1, projectRelativeWidth),
                        Math.Max(1, projectRelativeHeight),
                        Math.Max(1, targetWidth),
                        Math.Max(1, targetHeight)));
                    generatedView = request.Provider.Generate(clip, canvasWidth, canvasHeight, targetWidth, targetHeight, frameIndex);
                }
                catch (Exception ex)
                {
                    message = $"Failed to generate dynamic preview: {ex.Message}";
                }
                finally
                {
                    DynamicPreviewRenderContext.Set(null);
                }
            }
        }
        else
        {
            if (sourceColorAdjustEffects is { Count: > 0 })
            {
                LogOnce(_sourceColorFallbackLogKeys, clip.Id, $"Clip {clip.Id}/{clip.Name} has source color-adjust effects, using frame fallback for source rendering.");
            }
            else
            {
                LogOnce(_missingProviderFallbackLogKeys, clip.Id, $"Clip {clip.Id}/{clip.Name} does not have a dynamic preview provider, using frame fallback.");
            }

            generatedView = GenerateFrameFallbackView(clip, canvasWidth, canvasHeight, frameIndex, fullRender: false, sourceColorAdjustEffects, token);
            usedFullRenderFallback = false;
        }
        //LogDiagnostic($"[GenerateClipPreview] The request {request.Clip.Name}'s Stage 1 - preview generation took {diagSW.ElapsedMilliseconds} ms.");
        if (token.IsCancellationRequested) return null;

        if (willUseEffectFallback)
        {
            generatedView = GenerateClipEffectFallbackView(clip, enabledEffects, targetWidth, targetHeight, frameIndex, token);
            usedFullRenderFallback = true;
        }
        else if (generatedView is null)
        {
            try
            {
                generatedView = GenerateFrameFallbackView(clip, canvasWidth, canvasHeight, frameIndex, token: token);
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

        if (enabledEffects.Length > 0 && !usedFullRenderFallback && !DisableEffectDynamicPreview)
        {
            for (var i = 0; i < enabledEffects.Length; i++)
            {
                var effect = enabledEffects[i];
                if (effect is IColorAdjustEffect || effect is IClipPositionProvider || effect is IContinuousClipPositionProvider)
                {
                    continue;
                }

                var isLegacyLayoutEffect = IsLegacyInternalLayoutEffect(effect);
                if (isLegacyLayoutEffect)
                {
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
                if (provider is not null && IsEffectProviderAvailable(provider, effect, generatedView.GetType()))
                {
                    float previewProgress;
                    if (effect is IContinuousEffect c && c.IsScoped)
                    {
                        int span = c.EndPoint - c.StartPoint;
                        previewProgress = span > 0 ? Math.Clamp(((float)frameIndex - c.StartPoint) / span, 0f, 1f) : 1f;
                    }
                    else
                    {
                        float effectDuration = clip.GetEffectiveDuration();
                        previewProgress = effectDuration > 0
                            ? Math.Clamp(((float)frameIndex - clip.StartFrame) / effectDuration, 0f, 1f)
                            : 1f;
                    }
                    generatedView = ApplyEffectPreview(generatedView, effect, provider, canvasWidth, canvasHeight, frameIndex, previewProgress);
                }
                else
                {
                    var effectKey = $"{clip.Id}:{effect.TypeName}:{effect.Name}";
                    LogOnce(_effectFallbackLogKeys, effectKey, $"Clip {clip.Id}/{clip.Name}'s Effect {effect.TypeName}/{effect.Name} does not support dynamic preview, using clip-local fallback.");
                    generatedView = GenerateClipEffectFallbackView(clip, enabledEffects, targetWidth, targetHeight, frameIndex, token);
                    usedFullRenderFallback = true;
                    break;
                }
            }
        }
        //LogDiagnostic($"[GenerateClipPreview] The request {request.Clip.Name}'s Stage 2 - process effect took {diagSW.ElapsedMilliseconds} ms.");

        if (generatedView is null)
        {
            return null;
        }

        generatedView.AutomationId ??= $"clip={clip.ClipType},id={clip.Id}";

        if (!applyClipTargetLayout)
        {
            return generatedView;
        }

        if (usedFullRenderFallback)
        {
            return generatedView;
        }

        var layout = ApplyClipTargetLayoutPreview(generatedView, clip, enabledEffects, canvasWidth, canvasHeight, frameIndex);

        //LogDiagnostic($"[GenerateClipPreview] The request {request.Clip.Name}'s Stage 3 - apply layout {diagSW.ElapsedMilliseconds} ms.");
        return layout;

    }

    private static View ApplyClipTargetLayoutPreview(View input, IClip clip, IReadOnlyList<IEffect> enabledEffects, int canvasWidth, int canvasHeight, uint frameIndex)
    {
        var baseW = clip.TargetWidth > 0 ? clip.TargetWidth : Math.Max(1, canvasWidth);
        var baseH = clip.TargetHeight > 0 ? clip.TargetHeight : Math.Max(1, canvasHeight);
        double x = clip.TargetX;
        double y = clip.TargetY;
        double w = baseW;
        double h = baseH;

        ApplyPositionProvidersToClip(clip, enabledEffects, frameIndex, canvasWidth, canvasHeight, ref x, ref y, ref w, ref h);

        if (!HasExplicitTargetRect(clip))
        {
            input.WidthRequest = Math.Max(1, w);
            input.HeightRequest = Math.Max(1, h);
            input.HorizontalOptions = LayoutOptions.Start;
            input.VerticalOptions = LayoutOptions.Start;
            input.TranslationX = x;
            input.TranslationY = y;
            return ApplyImplicitClipAutoCenterPreview(input, clip, canvasHeight);
        }

        input.WidthRequest = Math.Max(1, w);
        input.HeightRequest = Math.Max(1, h);
        input.HorizontalOptions = LayoutOptions.Start;
        input.VerticalOptions = LayoutOptions.Start;
        input.TranslationX = x;
        input.TranslationY = y;
        return input;
    }

    private static void ApplyPositionProvidersToClip(IClip clip, IReadOnlyList<IEffect> enabledEffects, uint frameIndex, int targetWidth, int targetHeight, ref double x, ref double y, ref double w, ref double h)
    {
        if (enabledEffects.Count == 0)
        {
            return;
        }

        for (var i = 0; i < enabledEffects.Count; i++)
        {
            var effect = enabledEffects[i];
            ClipPositionTuple pos;
            if (effect is IContinuousClipPositionProvider cp)
            {
                pos = cp.GetPosition(clip, frameIndex, targetWidth, targetHeight);
            }
            else if (effect is IClipPositionProvider p)
            {
                pos = p.GetPosition(clip, targetWidth, targetHeight);
            }
            else
            {
                continue;
            }

            if (pos.IsDelta)
            {
                x += pos.TargetX;
                y += pos.TargetY;
                w += pos.TargetWidth;
                h += pos.TargetHeight;
            }
            else
            {
                x = pos.TargetX;
                y = pos.TargetY;
                if (pos.TargetWidth > 0)
                {
                    w = pos.TargetWidth;
                }

                if (pos.TargetHeight > 0)
                {
                    h = pos.TargetHeight;
                }
            }
        }
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

    [DebuggerStepThrough()]
    private static ITextClipStyleProvider? ResolveTextClipStyleProvider(IClip clip)
    {
        var providerFrom = ReadExtraDataString(clip.ExtraData, TextStyleProviderFromKey);
        var providerType = ReadExtraDataString(clip.ExtraData, TextStyleProviderTypeKey);
        if (!string.IsNullOrWhiteSpace(providerFrom)
            && !string.IsNullOrWhiteSpace(providerType)
            && PluginManager.LoadedPlugins.TryGetValue(providerFrom, out var ownerPlugin)
            && ownerPlugin is IApplicationPluginBase appPlugin
            && appPlugin.TextClipStyleProvider.TryGetValue(providerType, out var factory))
        {
            return factory();
        }

        if (PluginManager.LoadedPlugins.TryGetValue(clip.FromPlugin, out var clipOwner)
            && clipOwner is IApplicationPluginBase clipPlugin)
        {
            return ResolveTextClipStyleProviderFromDictionary(clipPlugin.TextClipStyleProvider, clip);
        }

        return null;
    }

    private static ITextClipStyleProvider? ResolveTextClipStyleProviderFromDictionary(
        IReadOnlyDictionary<string, Func<ITextClipStyleProvider>> providers, IClip clip)
    {
        if (providers.Count == 0)
        {
            return null;
        }

        if (!string.IsNullOrWhiteSpace(clip.TypeName)
            && providers.TryGetValue(clip.TypeName, out var typedProvider))
        {
            return typedProvider();
        }

        var clipModeName = clip.ClipType.ToString();
        if (providers.TryGetValue(clipModeName, out var modeProvider))
        {
            return modeProvider();
        }

        var fallback = providers.Values.FirstOrDefault();
        return fallback is null ? null : fallback();
    }

    private static string? ReadExtraDataString(Dictionary<string, object>? data, string key)
    {
        if (data == null || !data.TryGetValue(key, out var raw) || raw is null)
        {
            return null;
        }

        if (raw is string s)
        {
            return s;
        }

        if (raw is JsonElement je)
        {
            if (je.ValueKind == JsonValueKind.String)
            {
                return je.GetString();
            }
            return je.ToString();
        }

        return raw.ToString();
    }

    private static Dictionary<string, string>? ReadTextStyleParameters(Dictionary<string, object>? data)
    {
        if (TryReadStringDictionary(data, TextStyleParametersKey, out var parameters))
        {
            return parameters;
        }

        if (TryReadStringDictionary(data, TextStyleParametersKey, out var providerParameters))
        {
            return providerParameters;
        }

        return null;
    }

    private static bool TryReadStringDictionary(Dictionary<string, object>? data, string key, out Dictionary<string, string> values)
    {
        values = null!;
        if (data == null || !data.TryGetValue(key, out var raw) || raw is null)
        {
            return false;
        }

        if (raw is Dictionary<string, string> stringDict)
        {
            values = new Dictionary<string, string>(stringDict);
            return true;
        }

        if (raw is Dictionary<string, object> objDict)
        {
            values = objDict.ToDictionary(k => k.Key, v => v.Value?.ToString() ?? string.Empty, StringComparer.Ordinal);
            return true;
        }

        if (raw is JsonElement je && je.ValueKind == JsonValueKind.Object)
        {
            var dict = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (var prop in je.EnumerateObject())
            {
                dict[prop.Name] = prop.Value.ValueKind == JsonValueKind.String
                    ? (prop.Value.GetString() ?? string.Empty)
                    : prop.Value.ToString();
            }
            values = dict;
            return true;
        }

        return false;
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

    private View GenerateFrameFallbackView(IClip clip, int targetWidth, int targetHeight, uint frameIndex, bool fullRender = false, IReadOnlyList<IColorAdjustEffect>? sourceColorAdjustEffects = null, CancellationToken token = default)
    {
        if (token.IsCancellationRequested) return null!;
        IPicture frame = null!;
        if (!fullRender)
        {
            var cacheKey = new FallbackFrameCacheKey(clip.Id, targetWidth, targetHeight, frameIndex, ResolveFallbackSourceFingerprint(clip));
            var needsClone = sourceColorAdjustEffects is { Count: > 0 };
            if (!TryGetCachedFallbackFrame(cacheKey, out frame, deepCopy: needsClone))
            {
                if (!TryGetResizedFallbackFrame(cacheKey, out frame)
                    && !TryGetResizedFallbackFromDisk(cacheKey, out frame))
                {
                    frame = clip.GetFrame(frameIndex, targetWidth, targetHeight, true, IPicture.PicturePixelMode.BytePicture);
                }

                if (frame is not null)
                {
                    if (frame.Disposed) Debugger.Break();
                    CacheFallbackFrame(cacheKey, frame);
                    EnqueueFallbackDiskPersist(cacheKey, frame);
                }
            }
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
                var cacheKey = new FallbackFrameCacheKey(clip.Id, targetWidth, targetHeight, frameIndex, ResolveFallbackSourceFingerprint(clip));
                if (!TryGetCachedFallbackFrame(cacheKey, out frame, deepCopy: false))
                {
                    if (!TryGetResizedFallbackFrame(cacheKey, out frame)
                        && !TryGetResizedFallbackFromDisk(cacheKey, out frame))
                    {
                        frame = clip.GetFrame(frameIndex, targetWidth, targetHeight, true, IPicture.PicturePixelMode.BytePicture);
                    }

                    if (frame is not null)
                    {
                        if (frame.Disposed) Debugger.Break();
                        CacheFallbackFrame(cacheKey, frame);
                        EnqueueFallbackDiskPersist(cacheKey, frame);
                    }
                }
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

        if (token.IsCancellationRequested) return null!;

        return new Image
        {
            Source = frame.ToImageSource(),
            Aspect = Aspect.Fill,
            HorizontalOptions = LayoutOptions.Fill,
            VerticalOptions = LayoutOptions.Fill,
        };
    }

    private static View GenerateClipEffectFallbackView(IClip clip, IReadOnlyList<IEffect> enabledEffects, int targetWidth, int targetHeight, uint frameIndex, CancellationToken token)
    {
        var cacheKey = new FallbackFrameCacheKey(clip.Id, targetWidth, targetHeight, frameIndex, ResolveFallbackSourceFingerprint(clip));
        if (!TryGetCachedFallbackFrame(cacheKey, out var frame))
        {
            if (!TryGetResizedFallbackFrame(cacheKey, out frame)
                && !TryGetResizedFallbackFromDisk(cacheKey, out frame))
            {
                frame = clip.GetFrame(frameIndex, targetWidth, targetHeight, true, IPicture.PicturePixelMode.BytePicture);
            }

            if (frame is not null)
            {
                if (frame.Disposed) Debugger.Break();
                CacheFallbackFrame(cacheKey, frame);
                EnqueueFallbackDiskPersist(cacheKey, frame);
            }
        }
        if (token.IsCancellationRequested) return null;

        if (frame is null)
        {
            return new Image
            {
                Source = null,
                Aspect = Aspect.Fill,
                HorizontalOptions = LayoutOptions.Fill,
                VerticalOptions = LayoutOptions.Fill,
            };
        }

        for (var i = 0; i < enabledEffects.Count; i++)
        {
            if (token.IsCancellationRequested) return null;
            var effect = enabledEffects[i];
            if (effect is IClipPositionProvider or IContinuousClipPositionProvider)
            {
                continue;
            }

            if (IsLegacyInternalLayoutEffect(effect))
            {
                continue;
            }

            try
            {
                if (effect is INormalEffect normalEffect)
                {
                    frame = normalEffect.Render(frame, PluginManager.CreateComputer(effect.NeedComputer), targetWidth, targetHeight);
                }
            }
            catch (Exception ex)
            {
                Log($"Clip {clip.Id}/{clip.Name}'s effect {effect.TypeName}/{effect.Name} failed during clip-local fallback: {ex.Message}");
            }
        }
        if (token.IsCancellationRequested) return null;

        return new Image
        {
            Source = frame.ToImageSource(),
            Aspect = Aspect.Fill,
            HorizontalOptions = LayoutOptions.Fill,
            VerticalOptions = LayoutOptions.Fill,
        };
    }

    private static View ApplyEffectPreview(View input, IEffect effect, IEffectDynamicPreviewProvider provider, int targetWidth, int targetHeight, uint frameIndex, float progress)
    {
        try
        {
            return provider.Generate(effect, input, input.GetType(), targetWidth, targetHeight, frameIndex, progress) ?? input;
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

    private static IEffect[] GetEnabledEffectsSorted(IEffect[]? effects)
    {
        if (effects is null || effects.Length == 0)
        {
            return Array.Empty<IEffect>();
        }

        var enabledEffects = new List<IEffect>(effects.Length);
        for (var i = 0; i < effects.Length; i++)
        {
            var effect = effects[i];
            if (effect.Enabled)
            {
                enabledEffects.Add(effect);
            }
        }

        if (enabledEffects.Count <= 1)
        {
            return enabledEffects.ToArray();
        }

        enabledEffects.Sort(static (left, right) => left.Index.CompareTo(right.Index));
        return enabledEffects.ToArray();
    }

    private static void LogOnce(ConcurrentDictionary<string, byte> gate, string key, string message)
    {
        if (gate.TryAdd(key, 0))
        {
            Log(message);
        }
    }

    private void ResetFallbackLogs()
    {
        _sourceColorFallbackLogKeys.Clear();
        _missingProviderFallbackLogKeys.Clear();
        _effectFallbackLogKeys.Clear();
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

    private static bool TryGetCachedFallbackFrame(FallbackFrameCacheKey key, out IPicture frame, bool deepCopy = true)
    {
        if (s_fallbackFrameCache.TryGetValue(key, out var cached))
        {
            cached.Touch();
            frame = deepCopy ? cached.Frame.Clone() : cached.Frame;
            return true;
        }

        var diskPath = ResolveFallbackDiskCachePath(key);
        if (File.Exists(diskPath))
        {
            try
            {
                frame = new Picture8bpp(diskPath);
                CacheFallbackFrame(key, frame);
                TouchFallbackDiskEntry(diskPath);
                return true;
            }
            catch
            {
            }
        }

        frame = null!;
        return false;
    }

    private static bool TryGetResizedFallbackFrame(FallbackFrameCacheKey targetKey, out IPicture resizedFrame)
    {
        int bestDelta = int.MaxValue;
        int bestArea = -1;
        FallbackFrameCacheKey? bestKey = null;

        foreach (var kvp in s_fallbackFrameCache)
        {
            var k = kvp.Key;
            if (k.ClipId == targetKey.ClipId && k.SourceFingerprint == targetKey.SourceFingerprint && k.FrameIndex == targetKey.FrameIndex)
            {
                if (k.TargetWidth == targetKey.TargetWidth && k.TargetHeight == targetKey.TargetHeight)
                    continue;

                int delta = Math.Abs(k.TargetWidth - targetKey.TargetWidth) + Math.Abs(k.TargetHeight - targetKey.TargetHeight);
                int area = k.TargetWidth * k.TargetHeight;

                if (delta < bestDelta || (delta == bestDelta && area > bestArea))
                {
                    bestDelta = delta;
                    bestArea = area;
                    bestKey = k;
                }
            }
        }

        if (bestKey is null || !s_fallbackFrameCache.TryGetValue(bestKey.Value, out var cached))
        {
            resizedFrame = null!;
            return false;
        }

        cached.Touch();
        var source = cached.Frame.Clone();
        var resized = source.Resize(targetKey.TargetWidth, targetKey.TargetHeight, preserveAspect: true);
        if (ReferenceEquals(resized, source))
        {
            resizedFrame = source;
        }
        else
        {
            resizedFrame = resized.BitPerPixel == IPicture.PicturePixelMode.BytePicture
                ? resized
                : resized.ToBitPerPixel(IPicture.PicturePixelMode.BytePicture);
        }
        return true;
    }

    private static List<(int width, int height)> ResolveFallbackDiskCacheSizes(string clipId, long sourceFingerprint)
    {
        var sizes = new List<(int width, int height)>();
        var baseDir = Path.Combine(DiskCacheRoot, SanitizePathSegment(clipId));
        if (!Directory.Exists(baseDir))
            return sizes;

        var fingerprintStr = sourceFingerprint.ToString("X16");
        try
        {
            foreach (var subDir in Directory.EnumerateDirectories(baseDir))
            {
                var name = Path.GetFileName(subDir);
                var parts = name.Split('x');
                if (parts.Length == 2
                    && int.TryParse(parts[0], out var w)
                    && int.TryParse(parts[1], out var h)
                    && w > 0 && h > 0
                    && Directory.Exists(Path.Combine(subDir, fingerprintStr)))
                {
                    sizes.Add((w, h));
                }
            }
        }
        catch
        {
        }

        return sizes;
    }

    private static bool TryGetResizedFallbackFromDisk(FallbackFrameCacheKey targetKey, out IPicture resizedFrame)
    {
        var sizes = ResolveFallbackDiskCacheSizes(targetKey.ClipId, targetKey.SourceFingerprint);
        if (sizes.Count == 0)
        {
            resizedFrame = null!;
            return false;
        }

        int bestDelta = int.MaxValue;
        int bestArea = -1;
        (int width, int height) bestSize = default;

        foreach (var (w, h) in sizes)
        {
            if (w == targetKey.TargetWidth && h == targetKey.TargetHeight)
                continue;

            int delta = Math.Abs(w - targetKey.TargetWidth) + Math.Abs(h - targetKey.TargetHeight);
            int area = w * h;

            if (delta < bestDelta || (delta == bestDelta && area > bestArea))
            {
                bestDelta = delta;
                bestArea = area;
                bestSize = (w, h);
            }
        }

        if (bestDelta == int.MaxValue)
        {
            resizedFrame = null!;
            return false;
        }

        var diskKey = new FallbackFrameCacheKey(targetKey.ClipId, bestSize.width, bestSize.height, targetKey.FrameIndex, targetKey.SourceFingerprint);
        var diskPath = ResolveFallbackDiskCachePath(diskKey);
        if (!File.Exists(diskPath))
        {
            resizedFrame = null!;
            return false;
        }

        try
        {
            resizedFrame = new Picture8bpp(diskPath).Resize(targetKey.TargetWidth, targetKey.TargetHeight, preserveAspect: true);
            TouchFallbackDiskEntry(diskPath);
            return true;
        }
        catch
        {
            resizedFrame = null!;
            return false;
        }
    }

    private static void CacheFallbackFrame(FallbackFrameCacheKey key, IPicture frame)
    {
        s_fallbackFrameCache[key] = new CachedFallbackFrame(frame);
        TrimFallbackCacheIfNeeded();
    }

    private static void TrimFallbackCacheIfNeeded()
    {
        if (s_fallbackFrameCache.Count <= MaxCachedFallbackFrames)
        {
            return;
        }

        var removeCount = s_fallbackFrameCache.Count - MaxCachedFallbackFrames;
        foreach (var stale in s_fallbackFrameCache.OrderBy(x => x.Value.LastAccessTicks).Take(removeCount).ToArray())
        {
            if (s_fallbackFrameCache.TryRemove(stale.Key, out var removed))
            {
                try
                {
                    removed.Frame.Dispose();
                }
                catch
                {
                }
            }
        }
    }

    private static void EnqueueFallbackDiskPersist(FallbackFrameCacheKey key, IPicture frame)
    {
        var diskPath = ResolveFallbackDiskCachePath(key);
        Task.Run(() =>
        {
            try
            {
                var dir = Path.GetDirectoryName(diskPath);
                if (!string.IsNullOrWhiteSpace(dir))
                {
                    Directory.CreateDirectory(dir);
                }

                frame.SaveToPng(diskPath);
                TouchFallbackDiskEntry(diskPath);
                TrimFallbackDiskCacheIfNeeded();
            }
            catch
            {
            }
        });
    }

    private static long ResolveFallbackSourceFingerprint(IClip clip)
    {
        var sourcePath = clip.FilePath;
        if (string.IsNullOrWhiteSpace(sourcePath))
        {
            return StringComparer.Ordinal.GetHashCode(clip.Id ?? string.Empty);
        }

        try
        {
            var info = new FileInfo(sourcePath);
            if (!info.Exists)
            {
                return StringComparer.Ordinal.GetHashCode(sourcePath);
            }

            unchecked
            {
                return (info.Length * 397L) ^ info.LastWriteTimeUtc.Ticks;
            }
        }
        catch
        {
            return StringComparer.Ordinal.GetHashCode(sourcePath);
        }
    }

    private static string ResolveFallbackDiskCachePath(FallbackFrameCacheKey key)
    {
        var clipId = SanitizePathSegment(key.ClipId);
        var dimension = $"{key.TargetWidth}x{key.TargetHeight}";
        var fingerprint = key.SourceFingerprint.ToString("X16");
        return Path.Combine(DiskCacheRoot, clipId, dimension, fingerprint, $"{key.FrameIndex}.png");
    }

    [DebuggerStepThrough()]
    private static string SanitizePathSegment(string raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
        {
            return "_";
        }

        var chars = raw.ToCharArray();
        var invalids = Path.GetInvalidFileNameChars();
        for (var i = 0; i < chars.Length; i++)
        {
            if (invalids.Contains(chars[i]))
            {
                chars[i] = '_';
            }
        }

        return new string(chars);
    }

    private static void TouchFallbackDiskEntry(string? diskPath)
    {
        if (!string.IsNullOrWhiteSpace(diskPath))
        {
            s_fallbackDiskFrameAccess[diskPath] = DateTime.UtcNow.Ticks;
        }
    }

    private static void TrimFallbackDiskCacheIfNeeded()
    {
        if (s_fallbackDiskFrameAccess.Count <= MaxDiskCachedFallbackFrames)
        {
            return;
        }

        var removeCount = s_fallbackDiskFrameAccess.Count - MaxDiskCachedFallbackFrames;
        foreach (var stale in s_fallbackDiskFrameAccess.OrderBy(entry => entry.Value).Take(removeCount))
        {
            if (s_fallbackDiskFrameAccess.TryRemove(stale.Key, out _))
            {
                try
                {
                    if (File.Exists(stale.Key))
                    {
                        File.Delete(stale.Key);
                    }
                }
                catch
                {
                }
            }
        }
    }

    #region Vector to MAUI Path conversion

    private static View BuildVectorPreviewView(
        IVectorContentClip vectorClip,
        int canvasWidth, int canvasHeight,
        int targetWidth, int targetHeight,
        uint frameIndex)
    {
        var clipW = vectorClip.TargetWidth > 0 ? vectorClip.TargetWidth : Math.Max(1, canvasWidth);
        var clipH = vectorClip.TargetHeight > 0 ? vectorClip.TargetHeight : Math.Max(1, canvasHeight);

        var vectorPicture = vectorClip.GetVectorPictureRelativeToStartPointOfSource(vectorClip.GetRelativeFrameIndex(frameIndex) ?? vectorClip.GetEffectiveDuration(), clipW, clipH);

        var container = new AbsoluteLayout
        {
            HorizontalOptions = LayoutOptions.Fill,
            VerticalOptions = LayoutOptions.Fill,
        };

        var elements = vectorPicture.Elements;
        if (elements is null || elements.Count == 0)
            return container;

        var sortedElements = elements.OrderBy(e => e.LayerIndex);

        foreach (var element in sortedElements)
        {
            var segments = element.Draw();
            if (segments is null || segments.Length == 0)
                continue;

            float scaleX, scaleY, originX, originY;
            if (element.UseUniformScale)
            {
                float us = Math.Min(clipW, clipH);
                scaleX = us;
                scaleY = us;
                originX = element.BaseX * clipW + element.RelativeX * us;
                originY = element.BaseY * clipH + element.RelativeY * us;
            }
            else
            {
                scaleX = clipW;
                scaleY = clipH;
                originX = element.RelativeX * clipW;
                originY = element.RelativeY * clipH;
            }

            float cosA = 0, sinA = 0;
            bool hasRotation = MathF.Abs(element.Rotation) > 0.0001f;
            if (hasRotation)
            {
                cosA = MathF.Cos(element.Rotation);
                sinA = MathF.Sin(element.Rotation);
            }

            foreach (var segment in segments)
            {
                if (segment is null) continue;
#if WINDOWS
                var device = new Microsoft.Graphics.Canvas.CanvasDevice();
                var maxSize = device.MaximumBitmapSizeInPixels;
                if (scaleX > maxSize || scaleY > maxSize)
                {
                    Log($"Skipping vector segment with scaleX={scaleX} and scaleY={scaleY} exceeding device limit of {maxSize}", "error");
                    throw new ArgumentOutOfRangeException($"Cannot create CanvasImageSource sized {scaleX} x {scaleY}; MaximumBitmapSizeInPixels for this device is {maxSize}"); //we need to handle it here, as the exception thrown from inside the geometry creation code is not catchable and will crash the app without any logs otherwise
                }
#endif
                var path = CreatePathFromSegment(segment, scaleX, scaleY, originX, originY, hasRotation, cosA, sinA);
                if (path is not null)
                    container.Children.Add(path);
            }
        }

        return container;
    }

    private static ShapesPath? CreatePathFromSegment(
        VectorSegment segment,
        float scaleX, float scaleY,
        float originX, float originY,
        bool hasRotation, float cosA, float sinA)
    {
        Geometry? geometry = null;

        switch (segment)
        {
            case StraightLineVectorSegment s:
                geometry = CreateLineGeometry(s, scaleX, scaleY, originX, originY, hasRotation, cosA, sinA);
                break;
            case RoundedRectangleVectorSegment s:
                geometry = CreateRoundedRectGeometry(s, scaleX, scaleY, originX, originY, hasRotation, cosA, sinA);
                break;
            case RectangleVectorSegment s:
                geometry = CreateRectGeometry(s, scaleX, scaleY, originX, originY, hasRotation, cosA, sinA);
                break;
            case EllipseVectorSegment s:
                geometry = CreateEllipseGeometry(s, scaleX, scaleY, originX, originY, hasRotation, cosA, sinA);
                break;
            case CubicBezierVectorSegment s:
                geometry = CreateCubicBezierGeometry(s, scaleX, scaleY, originX, originY, hasRotation, cosA, sinA);
                break;
            case QuadraticBezierVectorSegment s:
                geometry = CreateQuadraticBezierGeometry(s, scaleX, scaleY, originX, originY, hasRotation, cosA, sinA);
                break;
            case ArcVectorSegment s:
                geometry = CreateArcGeometry(s, scaleX, scaleY, originX, originY, hasRotation, cosA, sinA);
                break;
            case PolygonVectorSegment s:
                geometry = CreatePolygonGeometry(s.Points, s.Holes, scaleX, scaleY, originX, originY, hasRotation, cosA, sinA);
                break;
            case PolylineVectorSegment s:
                geometry = CreatePolylineGeometry(s.Points, scaleX, scaleY, originX, originY, hasRotation, cosA, sinA);
                break;
        }

        if (geometry is null) return null;

        bool hasFill = segment.FillA > 0;
        bool hasStroke = segment.Thickness > 0 && segment.StrokeA > 0;

        return new ShapesPath
        {
            Data = geometry,
            Fill = hasFill
                ? new SolidColorBrush(Color.FromRgba(segment.FillR / 65535f, segment.FillG / 65535f, segment.FillB / 65535f, segment.FillA))
                : null,
            Stroke = hasStroke
                ? new SolidColorBrush(Color.FromRgba(segment.StrokeR / 65535f, segment.StrokeG / 65535f, segment.StrokeB / 65535f, segment.StrokeA))
                : null,
            StrokeThickness = hasStroke ? segment.Thickness : 0,
        };
    }

    private static MauiPoint NToP(float nx, float ny, float scaleX, float scaleY, float originX, float originY, bool rot, float cosA, float sinA)
    {
        if (rot)
        {
            float rx = nx * cosA - ny * sinA;
            float ry = nx * sinA + ny * cosA;
            return new MauiPoint(rx * scaleX + originX, ry * scaleY + originY);
        }
        return new MauiPoint(nx * scaleX + originX, ny * scaleY + originY);
    }

    private static Geometry CreateLineGeometry(StraightLineVectorSegment s,
        float sx, float sy, float ox, float oy, bool rot, float cosA, float sinA)
    {
        var start = NToP(s.X1, s.Y1, sx, sy, ox, oy, rot, cosA, sinA);
        var end = NToP(s.X2, s.Y2, sx, sy, ox, oy, rot, cosA, sinA);

        var figure = new PathFigure { StartPoint = start, IsClosed = false, IsFilled = false };
        figure.Segments.Add(new LineSegment { Point = end });

        var pg = new PathGeometry();
        pg.Figures.Add(figure);
        return pg;
    }

    private static Geometry CreateRectGeometry(RectangleVectorSegment s,
        float sx, float sy, float ox, float oy, bool rot, float cosA, float sinA)
    {
        if (!rot)
        {
            var topLeft = NToP(s.X, s.Y, sx, sy, ox, oy, false, 0, 0);
            return new RectangleGeometry
            {
                Rect = new Rect(topLeft.X, topLeft.Y, s.Width * sx, s.Height * sy)
            };
        }

        var p0 = NToP(s.X, s.Y, sx, sy, ox, oy, true, cosA, sinA);
        var p1 = NToP(s.X + s.Width, s.Y, sx, sy, ox, oy, true, cosA, sinA);
        var p2 = NToP(s.X + s.Width, s.Y + s.Height, sx, sy, ox, oy, true, cosA, sinA);
        var p3 = NToP(s.X, s.Y + s.Height, sx, sy, ox, oy, true, cosA, sinA);

        return BuildPolygonPathGeometry([p0, p1, p2, p3], closeFigure: true);
    }

    private static Geometry CreateRoundedRectGeometry(RoundedRectangleVectorSegment s,
        float sx, float sy, float ox, float oy, bool rot, float cosA, float sinA)
    {
        float nX = s.X, nY = s.Y, nW = s.Width, nH = s.Height;
        float r = Math.Min(s.CornerRadius, Math.Min(nW, nH) * 0.5f);

        if (!rot)
        {
            var pg = new PathGeometry();
            var figure = new PathFigure { IsClosed = true, IsFilled = true };

            float pxR = r * sx, pyR = r * sy;
            var c0 = NToP(nX + r, nY, sx, sy, ox, oy, false, 0, 0);
            var c1 = NToP(nX + nW - r, nY, sx, sy, ox, oy, false, 0, 0);
            var c2 = NToP(nX + nW, nY + r, sx, sy, ox, oy, false, 0, 0);
            var c3 = NToP(nX + nW, nY + nH - r, sx, sy, ox, oy, false, 0, 0);
            var c4 = NToP(nX + nW - r, nY + nH, sx, sy, ox, oy, false, 0, 0);
            var c5 = NToP(nX + r, nY + nH, sx, sy, ox, oy, false, 0, 0);
            var c6 = NToP(nX, nY + nH - r, sx, sy, ox, oy, false, 0, 0);
            var c7 = NToP(nX, nY + r, sx, sy, ox, oy, false, 0, 0);

            figure.StartPoint = c0;
            figure.Segments.Add(new LineSegment { Point = c1 });
            figure.Segments.Add(new ArcSegment { Point = c2, Size = new Size(pxR, pyR), IsLargeArc = false, SweepDirection = SweepDirection.Clockwise });
            figure.Segments.Add(new LineSegment { Point = c3 });
            figure.Segments.Add(new ArcSegment { Point = c4, Size = new Size(pxR, pyR), IsLargeArc = false, SweepDirection = SweepDirection.Clockwise });
            figure.Segments.Add(new LineSegment { Point = c5 });
            figure.Segments.Add(new ArcSegment { Point = c6, Size = new Size(pxR, pyR), IsLargeArc = false, SweepDirection = SweepDirection.Clockwise });
            figure.Segments.Add(new LineSegment { Point = c7 });
            figure.Segments.Add(new ArcSegment { Point = c0, Size = new Size(pxR, pyR), IsLargeArc = false, SweepDirection = SweepDirection.Clockwise });

            pg.Figures.Add(figure);
            return pg;
        }

        // Rotated: approximate as 4-corner polygon (corner rounding is secondary for preview)
        var p0 = NToP(nX, nY, sx, sy, ox, oy, true, cosA, sinA);
        var p1 = NToP(nX + nW, nY, sx, sy, ox, oy, true, cosA, sinA);
        var p2 = NToP(nX + nW, nY + nH, sx, sy, ox, oy, true, cosA, sinA);
        var p3 = NToP(nX, nY + nH, sx, sy, ox, oy, true, cosA, sinA);
        return BuildPolygonPathGeometry([p0, p1, p2, p3], closeFigure: true);
    }

    private static Geometry CreateEllipseGeometry(EllipseVectorSegment s,
        float sx, float sy, float ox, float oy, bool rot, float cosA, float sinA)
    {
        var center = NToP(s.X, s.Y, sx, sy, ox, oy, rot, cosA, sinA);
        return new EllipseGeometry
        {
            Center = center,
            RadiusX = s.RadiusX * sx,
            RadiusY = s.RadiusY * sy,
        };
    }

    private static Geometry CreateCubicBezierGeometry(CubicBezierVectorSegment s,
        float sx, float sy, float ox, float oy, bool rot, float cosA, float sinA)
    {
        var p1 = NToP(s.X1, s.Y1, sx, sy, ox, oy, rot, cosA, sinA);
        var p2 = NToP(s.X2, s.Y2, sx, sy, ox, oy, rot, cosA, sinA);
        var p3 = NToP(s.X3, s.Y3, sx, sy, ox, oy, rot, cosA, sinA);
        var p4 = NToP(s.X4, s.Y4, sx, sy, ox, oy, rot, cosA, sinA);

        var figure = new PathFigure { StartPoint = p1, IsClosed = false, IsFilled = false };
        figure.Segments.Add(new BezierSegment { Point1 = p2, Point2 = p3, Point3 = p4 });

        var pg = new PathGeometry();
        pg.Figures.Add(figure);
        return pg;
    }

    private static Geometry CreateQuadraticBezierGeometry(QuadraticBezierVectorSegment s,
        float sx, float sy, float ox, float oy, bool rot, float cosA, float sinA)
    {
        var p1 = NToP(s.X1, s.Y1, sx, sy, ox, oy, rot, cosA, sinA);
        var p2 = NToP(s.X2, s.Y2, sx, sy, ox, oy, rot, cosA, sinA);
        var p3 = NToP(s.X3, s.Y3, sx, sy, ox, oy, rot, cosA, sinA);

        var figure = new PathFigure { StartPoint = p1, IsClosed = false, IsFilled = false };
        figure.Segments.Add(new QuadraticBezierSegment { Point1 = p2, Point2 = p3 });

        var pg = new PathGeometry();
        pg.Figures.Add(figure);
        return pg;
    }

    private static Geometry CreateArcGeometry(ArcVectorSegment s,
        float sx, float sy, float ox, float oy, bool rot, float cosA, float sinA)
    {
        float startAngle = s.StartAngle;
        float endAngle = s.StartAngle + s.SweepAngle;

        // Compute start/end in normalized space
        float startNX = s.X + s.RadiusX * MathF.Cos(startAngle);
        float startNY = s.Y + s.RadiusY * MathF.Sin(startAngle);
        float endNX = s.X + s.RadiusX * MathF.Cos(endAngle);
        float endNY = s.Y + s.RadiusY * MathF.Sin(endAngle);

        var startPt = NToP(startNX, startNY, sx, sy, ox, oy, rot, cosA, sinA);
        var endPt = NToP(endNX, endNY, sx, sy, ox, oy, rot, cosA, sinA);

        bool isLargeArc = MathF.Abs(s.SweepAngle) > MathF.PI;
        var sweepDir = s.SweepAngle >= 0 ? SweepDirection.Clockwise : SweepDirection.CounterClockwise;

        var figure = new PathFigure { StartPoint = startPt, IsClosed = false };
        figure.Segments.Add(new ArcSegment
        {
            Point = endPt,
            Size = new Size(s.RadiusX * sx, s.RadiusY * sy),
            RotationAngle = 0,
            IsLargeArc = isLargeArc,
            SweepDirection = sweepDir,
        });

        var pg = new PathGeometry();
        pg.Figures.Add(figure);
        return pg;
    }

    private static Geometry CreatePolygonGeometry(Drawing.Vector.Point[] points, Drawing.Vector.Point[][]? holes,
        float sx, float sy, float ox, float oy, bool rot, float cosA, float sinA)
    {
        if (points is null || points.Length == 0)
            return null!;

        var pg = new PathGeometry();
        if (holes is { Length: > 0 })
            pg.FillRule = FillRule.EvenOdd;

        var pts = points.Select(p => NToP(p.X, p.Y, sx, sy, ox, oy, rot, cosA, sinA)).ToArray();
        var mainFigure = new PathFigure { StartPoint = pts[0], IsClosed = true, IsFilled = true };
        if (pts.Length > 1)
        {
            var segPoints = new PointCollection();
            for (int i = 1; i < pts.Length; i++) segPoints.Add(pts[i]);
            mainFigure.Segments.Add(new PolyLineSegment { Points = segPoints });
        }
        pg.Figures.Add(mainFigure);

        if (holes is not null)
        {
            foreach (var hole in holes)
            {
                if (hole is null || hole.Length == 0) continue;
                var holePts = hole.Select(p => NToP(p.X, p.Y, sx, sy, ox, oy, rot, cosA, sinA)).ToArray();
                var holeFigure = new PathFigure { StartPoint = holePts[0], IsClosed = true, IsFilled = true };
                if (holePts.Length > 1)
                {
                    var holeSegPoints = new PointCollection();
                    for (int i = 1; i < holePts.Length; i++) holeSegPoints.Add(holePts[i]);
                    holeFigure.Segments.Add(new PolyLineSegment { Points = holeSegPoints });
                }
                pg.Figures.Add(holeFigure);
            }
        }

        return pg;
    }

    private static Geometry CreatePolylineGeometry(Drawing.Vector.Point[] points,
        float sx, float sy, float ox, float oy, bool rot, float cosA, float sinA)
    {
        if (points is null || points.Length < 2)
            return null!;

        var pts = points.Select(p => NToP(p.X, p.Y, sx, sy, ox, oy, rot, cosA, sinA)).ToArray();
        var figure = new PathFigure { StartPoint = pts[0], IsClosed = false, IsFilled = false };
        var segPoints = new PointCollection();
        for (int i = 1; i < pts.Length; i++) segPoints.Add(pts[i]);
        figure.Segments.Add(new PolyLineSegment { Points = segPoints });

        var pg = new PathGeometry();
        pg.Figures.Add(figure);
        return pg;
    }

    private static Geometry BuildPolygonPathGeometry(IReadOnlyList<MauiPoint> pts, bool closeFigure)
    {
        if (pts.Count == 0) return null!;

        var figure = new PathFigure { StartPoint = pts[0], IsClosed = closeFigure, IsFilled = closeFigure };
        if (pts.Count > 1)
        {
            var segPoints = new PointCollection();
            for (int i = 1; i < pts.Count; i++) segPoints.Add(pts[i]);
            figure.Segments.Add(new PolyLineSegment { Points = segPoints });
        }

        var pg = new PathGeometry();
        pg.Figures.Add(figure);
        return pg;
    }

    #endregion

    public sealed record PreviewRequest(IClip Clip, IClipDynamicPreviewProvider? Provider);

    private sealed class CachedFallbackFrame
    {
        public CachedFallbackFrame(IPicture frame)
        {
            Frame = frame.Clone();
            Frame.CanBeDisposed = false;
            Touch();
        }

        public IPicture Frame { get; }
        public long LastAccessTicks { get; private set; }

        public void Touch() => LastAccessTicks = DateTime.UtcNow.Ticks;
    }

    private readonly record struct FallbackFrameCacheKey(string ClipId, int TargetWidth, int TargetHeight, uint FrameIndex, long SourceFingerprint);

}