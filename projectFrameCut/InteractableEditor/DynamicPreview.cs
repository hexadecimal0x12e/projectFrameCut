using Microsoft.Maui.Controls.Shapes;
using projectFrameCut.ApplicationAPIBase.DynamicPreviewProvider;
using projectFrameCut.ApplicationAPIBase.Helpers;
using projectFrameCut.ApplicationAPIBase.Plugins;
using projectFrameCut.ApplicationAPIBase.Text;
using projectFrameCut.ApplicationPluginBase.DynamicPreviewProvider;
using projectFrameCut.Asset;
using projectFrameCut.DraftStuff;
using projectFrameCut.Drawing.Base;
using projectFrameCut.Drawing.Base.Picture;
using projectFrameCut.Drawing.Processing.Resizing;
using projectFrameCut.Drawing.Text.Entry;
using projectFrameCut.Drawing.Vector;
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
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Threading;
using IPicture = projectFrameCut.Drawing.Base.IPicture;
using MauiPoint = Microsoft.Maui.Graphics.Point;
using Path = System.IO.Path;
using RenderITransform = projectFrameCut.Render.RenderAPIBase.ClipAndTrack.ITransform;
using ShapesPath = Microsoft.Maui.Controls.Shapes.Path;

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

        public PreparedPreview(Guid clipId, Func<View>? viewFactory, string? errorMessage, IClip? source)
        {
            ClipId = clipId;
            _viewFactory = viewFactory;
            ErrorMessage = errorMessage;
            Source = source;
        }

        public Guid ClipId { get; }
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

    /// <summary>
    /// Intermediate result from Phase 1 (background source preparation), consumed by Phase 2 (UI-thread View building).
    /// </summary>
    private sealed class PreviewSourceData
    {
        public IClip Clip { get; init; } = null!;
        public string? ErrorMessage { get; init; }

        // Provider 直接返回的 View（SolidColorClip 创建的 BoxView、错误 Label 等无法提取 ImageSource 的情况）
        public View? PreservedView { get; init; }
        // dispatchable provider 制准备的图片源，Phase 2 传给 Generate 使用
        public Drawing.Base.IPicture? PreparedPicture { get; init; }
        // 从帧解码或 ImageSource.FromFile 获得的 ImageSource（最常用）
        public ImageSource? FrameSource { get; init; }
        // VectorClip 标记（实际数据在 Phase 2 从 clip 读取）
        public bool HasVectorData { get; init; }
        // 是否已执行全渲染回退（跳过 effect 叠加逻辑）
        public bool UsedFullRenderFallback { get; init; }
        // 已启用效果列表（Phase 2 叠加效果时需要）
        public IReadOnlyList<IEffect>? EnabledEffects { get; init; }
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
    /// When a <see cref="Render.VectorContent.Components.ComponentGroup"/> has more child components
    /// than this threshold, its preview is rasterized to a bitmap instead of being split into
    /// individual MAUI Path objects. This prevents GPU overload or platform path-count limits
    /// when a group contains a very large number of sub-components (e.g. an imported SVG with
    /// hundreds of paths). Set to <c>int.MaxValue</c> to disable early rasterization entirely.
    /// Default is 50.
    /// </summary>
    public static int GroupRasterizationChildThreshold { get; set; } = 128;

    /// <summary>
    /// Divisor for preview resolution. 1 = full resolution, 2 = half, etc.
    /// Reduces rendering load by scaling down canvas dimensions.
    /// </summary>
    public int PreviewResolutionDivisor { get; set; } = 1;

    public async Task<IReadOnlyList<PreparedPreview>> PrepareFrameAsync(uint frameIndex, int targetWidth, int targetHeight, double CanvasWidth, double CanvasHeight, CancellationToken token, bool applyClipTargetLayout = true)
    {
        var prepareVersion = Interlocked.Increment(ref _prepareVersion);
        try
        {
            var dimensions = ResolveDimensions(targetWidth, targetHeight, CanvasWidth, CanvasHeight);
            return await GetFinalRequests(frameIndex, targetWidth, targetHeight, prepareVersion, dimensions.canvasWidth, dimensions.canvasHeight, token, applyClipTargetLayout).ConfigureAwait(false);
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

    public async Task<IReadOnlyList<PreparedPreview>> GetFinalRequests(uint frameIndex, int targetWidth, int targetHeight, long prepareVersion, int canvasWidth, int canvasHeight, CancellationToken token, bool applyClipTargetLayout = true)
    {
        var clipsSnapshot = AcquireClipsSnapshot();
        var requests = ResolveRequests(clipsSnapshot, frameIndex, canvasWidth, canvasHeight);
        var prepared = await PrepareRequestsAsync(requests, canvasWidth, canvasHeight, targetWidth, targetHeight, frameIndex, applyClipTargetLayout, checkVersion: true, prepareVersion, token).ConfigureAwait(false);
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
        var clips = json.Clips;
        if (clips is null || clips.Length == 0) return;

        var clipsList = new List<IClip>();
        var reinitTasks = new List<Task>();

        foreach (var clip in clips)
        {
            if (clip.ClipType == ClipMode.MarkingClip)
            {
                continue;
            }

            var clipJson = JsonSerializer.SerializeToElement(clip);
            var clipInstance = PluginManager.CreateClip(clipJson);
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
    [MethodImpl(MethodImplOptions.AggressiveOptimization)]
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
            catch (OperationCanceledException)
            {
                // Cancellation is silent — the slot stays null, semaphore released in finally.
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
        var sourceData = GenerateClipPreviewSource(request, canvasWidth, canvasHeight, targetWidth, targetHeight, frameIndex, token);
        if (sourceData is null)
        {
            return new PreparedPreview(request.Clip.Id, null, "Failed to generate preview source.", request.Clip);
        }
        return new PreparedPreview(request.Clip.Id, () =>
            BuildClipPreviewView(sourceData, request, canvasWidth, canvasHeight, targetWidth, targetHeight, frameIndex, applyClipTargetLayout, token),
            sourceData.ErrorMessage, request.Clip);
    }

    [MethodImpl(MethodImplOptions.AggressiveOptimization)]
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

        var clipIndex = new Dictionary<Guid, IClip>(clips.Count);
        for (var i = 0; i < clips.Count; i++)
        {
            var sourceClip = clips[i];
            if (sourceClip.Id != Guid.Empty)
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
                            var rebuiltEntries = new List<TextEntry>(entries);
                            clip.ExtraData["TextEntries"] = rebuiltEntries;
                            resolvedEntries = rebuiltEntries;
                        }
                    }

                    if (textClip.TargetWidth <= 0 && textClip.TargetHeight <= 0
                        && canvasWidth > 0 && canvasHeight > 0)
                    {
                        if (resolvedEntries.Count > 0)
                        {
                            var bounds = TextMeasureHelper.MeasureBounds(textClip);
                            textClip.TargetX = (int)Math.Round(bounds.X);
                            textClip.TargetY = (int)Math.Round(bounds.Y);
                            textClip.TargetWidth = Math.Max(1, (int)Math.Ceiling(bounds.Width));
                            textClip.TargetHeight = Math.Max(1, (int)Math.Ceiling(bounds.Height));
                            // Shift entries to clip-local space so the layout canvas
                            // (TargetWidth × TargetHeight) matches the coordinate system.
                            ShiftEntriesToClipLocal(resolvedEntries, textClip.TargetX, textClip.TargetY);
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
                        // Shift entries to clip-local space so the layout canvas
                        // (TargetWidth × TargetHeight) matches the coordinate system.
                        var rawEntries = TextMeasureHelper.ResolveEntries(textClip);
                        if (rawEntries.Count > 0)
                            ShiftEntriesToClipLocal(rawEntries, textClip.TargetX, textClip.TargetY);
                    }
                }
            }

            requests[i] = new PreviewRequest(clip, ResolveProvider(clip));
        }

        return requests;
    }

    private static void BindTransformRuntimeSources(TransformContainer transformClip, IReadOnlyDictionary<Guid, IClip> clipIndex)
    {
        if (transformClip.ExtraData is null || transformClip.Transform is not RenderITransform transform)
        {
            return;
        }

        if (clipIndex.TryGetValue(transform.BindedLeftClip, out var leftClip))
        {
            transformClip.ExtraData[TransformClipDynamicPreviewRuntimeKeys.LeftClip] = leftClip;
        }
        else
        {
            transformClip.ExtraData.Remove(TransformClipDynamicPreviewRuntimeKeys.LeftClip);
        }

        if (clipIndex.TryGetValue(transform.BindedRightClip, out var rightClip))
        {
            transformClip.ExtraData[TransformClipDynamicPreviewRuntimeKeys.RightClip] = rightClip;
        }
        else
        {
            transformClip.ExtraData.Remove(TransformClipDynamicPreviewRuntimeKeys.RightClip);
        }
    }

    private PreviewSourceData? GenerateClipPreviewSource(PreviewRequest request, int canvasWidth, int canvasHeight, int targetWidth, int targetHeight, uint frameIndex, CancellationToken token)
    {
        var clip = request.Clip;
        if (clip is null) return null;

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

        View? preservedView = null;
        ImageSource? frameSource = null;
        IPicture? preparedPicture = null;
        bool hasVectorData = false;
        bool usedFullRenderFallback = false;
        string? errorMessage = null;

        if (willUseEffectFallback)
        {
            LogOnce(_effectFallbackLogKeys, clip.Id.ToString(), $"Clip {clip.Id}/{clip.Name} has {enabledEffects.Length} effect(s), using clip-local fallback (DisableEffectDynamicPreview=true).");
        }
        else if ((sourceColorAdjustEffects?.Count ?? 0) == 0 && request.Provider is not null)
        {
            if (clip is IVectorContentClip vectorClip && !DisableVectorPreviewPaths)
            {
                // Vector 路径：Phase 1 只读数据，Phase 2 创建 MAUI Path
                hasVectorData = true;
            }
            else if (request.Provider.IsPrepareGenerateDispatchable)
            {
                try
                {
                    preparedPicture = request.Provider.PrepareSource(clip, canvasWidth, canvasHeight, targetWidth, targetHeight, frameIndex, token).GetAwaiter().GetResult();
                }
                catch (Exception ex)
                {
                    errorMessage = $"Failed to prepare preview source: {ex.Message}";
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
                    var providerView = request.Provider.Generate(clip, null, canvasWidth, canvasHeight, targetWidth, targetHeight, frameIndex);
                    if (providerView is Image providerImage && providerImage.Source is not null)
                    {
                        // 提取 ImageSource，在 Phase 2 于 UI 线程重新创建 Image
                        frameSource = providerImage.Source;
                    }
                    else
                    {
                        // 非 Image 控件（BoxView、Label 等）保留原 View
                        preservedView = providerView;
                    }
                }
                catch (Exception ex)
                {
                    errorMessage = $"Failed to generate dynamic preview: {ex.Message}";
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
                LogOnce(_sourceColorFallbackLogKeys, clip.Id.ToString(), $"Clip {clip.Id}/{clip.Name} has source color-adjust effects, using frame fallback for source rendering.");
            }
            else
            {
                LogOnce(_missingProviderFallbackLogKeys, clip.Id.ToString(), $"Clip {clip.Id}/{clip.Name} does not have a dynamic preview provider, using frame fallback.");
            }

            // GenerateFrameFallbackView 返回的是 Image View，这里只取 ImageSource
            var fallbackView = GenerateFrameFallbackView(clip, canvasWidth, canvasHeight, frameIndex, fullRender: false, sourceColorAdjustEffects, token);
            if (fallbackView is Image fallbackImage && fallbackImage.Source is not null)
            {
                frameSource = fallbackImage.Source;
            }
            else
            {
                preservedView = fallbackView;
            }
        }
        if (token.IsCancellationRequested) return null;

        // willUseEffectFallback 路径：在后台线程做全部渲染（包含帧读取），只保留 ImageSource
        if (willUseEffectFallback)
        {
            var fallbackView = GenerateClipEffectFallbackView(clip, enabledEffects, targetWidth, targetHeight, frameIndex, token);
            if (fallbackView is Image fbImage && fbImage.Source is not null)
            {
                frameSource = fbImage.Source;
            }
            else
            {
                preservedView = fallbackView;
            }
            usedFullRenderFallback = true;
        }
        else if (preservedView is null && frameSource is null && !hasVectorData)
        {
            // 没有任何结果时尝试 fallback
            try
            {
                var fallbackView = GenerateFrameFallbackView(clip, canvasWidth, canvasHeight, frameIndex, token: token);
                if (fallbackView is Image fbImage && fbImage.Source is not null)
                {
                    frameSource = fbImage.Source;
                }
                else
                {
                    preservedView = fallbackView;
                }
            }
            catch (Exception ex)
            {
                errorMessage = $"Failed to render fallback frame: {ex.Message}";
                return new PreviewSourceData
                {
                    Clip = clip,
                    ErrorMessage = errorMessage,
                    EnabledEffects = enabledEffects,
                };
            }
        }

        return new PreviewSourceData
        {
            Clip = clip,
            ErrorMessage = errorMessage,
            PreservedView = preservedView,
            PreparedPicture = preparedPicture,
            FrameSource = frameSource,
            HasVectorData = hasVectorData,
            EnabledEffects = enabledEffects,
            UsedFullRenderFallback = usedFullRenderFallback,
        };
    }

    /// <summary>
    /// Phase 2: 在 UI 线程上从 PreviewSourceData 创建 MAUI View，叠加效果并应用布局。
    /// </summary>
    private View BuildClipPreviewView(PreviewSourceData source, PreviewRequest request, int canvasWidth, int canvasHeight, int targetWidth, int targetHeight, uint frameIndex, bool applyClipTargetLayout, CancellationToken token)
    {
        var clip = source.Clip;
        var enabledEffects = source.EnabledEffects ?? [];

        View generatedView;
        if (source.PreparedPicture is not null && request.Provider is not null)
        {
            generatedView = request.Provider.Generate(clip, source.PreparedPicture, canvasWidth, canvasHeight, targetWidth, targetHeight, frameIndex);
        }
        else if (source.FrameSource is not null)
        {
            generatedView = new Image
            {
                Source = source.FrameSource,
                Aspect = Aspect.Fill,
                HorizontalOptions = LayoutOptions.Fill,
                VerticalOptions = LayoutOptions.Fill,
            };
        }
        else if (source.PreservedView is not null)
        {
            generatedView = source.PreservedView;
        }
        else if (source.HasVectorData && clip is IVectorContentClip vectorClip && !DisableVectorPreviewPaths)
        {
            generatedView = BuildVectorPreviewView(vectorClip, canvasWidth, canvasHeight, targetWidth, targetHeight, frameIndex);
        }
        else
        {
            // No data to build a view from
            return null!;
        }

        if (token.IsCancellationRequested) return null!;

        // 叠加效果（effect stacking）
        bool usedFullRenderFallback = source.UsedFullRenderFallback;
        if (enabledEffects.Count > 0 && !usedFullRenderFallback && !DisableEffectDynamicPreview)
        {
            for (var i = 0; i < enabledEffects.Count; i++)
            {
                var effect = enabledEffects[i];
                if (effect is IColorAdjustEffect || effect is IClipPositionProvider || effect is IContinuousClipPositionProvider)
                {
                    continue;
                }

                var isLegacyLayoutEffect = IsLegacyInternalLayoutEffect(effect);
                if (isLegacyLayoutEffect)
                {
                    if (!applyClipTargetLayout) continue;
                    if (HasExplicitTargetRect(clip)) continue;
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
                    LogDiagnostic($"Applied dynamic preview for clip {clip.Id}/{clip.Name}'s effect {effect.TypeName}/{effect.Name}.");
                }
                else
                {
                    var effectKey = $"{clip.Id}:{effect.TypeName}:{effect.Name}";
                    LogDiagnostic($"Clip {clip.Id}/{clip.Name}'s Effect {effect.TypeName}/{effect.Name} does not support dynamic preview, using clip-local fallback.");
                    generatedView = GenerateClipEffectFallbackView(clip, enabledEffects, targetWidth, targetHeight, frameIndex, token);
                    usedFullRenderFallback = true;
                    break;
                }
            }
        }

        if (token.IsCancellationRequested) return null!;

        generatedView.AutomationId ??= $"clip={clip.ClipType},id={clip.Id}";

        // 应用布局
        if (!applyClipTargetLayout || usedFullRenderFallback)
        {
            return generatedView;
        }

        return ApplyClipTargetLayoutPreview(generatedView, clip, enabledEffects, canvasWidth, canvasHeight, frameIndex);
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
        LogDiagnostic($"Placed clip {clip.Id}/{clip.Name} at ({x},{y}) with size ({w}×{h}) after applying position providers.");
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

    private static void ShiftEntriesToClipLocal(IReadOnlyList<TextEntry> entries, int offsetX, int offsetY)
    {
        if (offsetX == 0 && offsetY == 0) return;
        for (var i = 0; i < entries.Count; i++)
        {
            var e = entries[i];
            if (e is not null)
            {
                e.X -= offsetX;
                e.Y -= offsetY;
            }
        }
    }

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

    private static List<(int width, int height)> ResolveFallbackDiskCacheSizes(Guid clipId, long sourceFingerprint)
    {
        var sizes = new List<(int width, int height)>();
        var baseDir = Path.Combine(DiskCacheRoot, SanitizePathSegment(clipId.ToString()));
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
            return clip.Id.GetHashCode();
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
        var clipId = SanitizePathSegment(key.ClipId.ToString());
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

    static int MaxVectorPathSize = 65535; // This is a safeguard limit to prevent creating excessively large paths that could crash the app. The actual maximum size may depend on the platform and device capabilities.
    private static int s_deviceMaxSize = 0; // Cached device limit, set once from CanvasDevice on Windows

    private static int GetDeviceMaxSize()
    {
        if (s_deviceMaxSize == 0)
        {
#if WINDOWS
            try
            {
                using var device = new Microsoft.Graphics.Canvas.CanvasDevice();
                s_deviceMaxSize = device.MaximumBitmapSizeInPixels;
            }
            catch
            {
                s_deviceMaxSize = MaxVectorPathSize;
            }
#else
            s_deviceMaxSize = MaxVectorPathSize;
#endif
        }
        return s_deviceMaxSize;
    }

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
        var deviceLimit = GetDeviceMaxSize();
        var skippedSegmentCount = 0;

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

            // Skip this entire element if the base scale alone already exceeds device limits
            if (scaleX > deviceLimit || scaleY > deviceLimit)
            {
                skippedSegmentCount++;
                continue;
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
                try
                {
                    var path = CreatePathFromSegment(segment, scaleX, scaleY, originX, originY, hasRotation, cosA, sinA, deviceLimit);
                    if (path is not null)
                        container.Children.Add(path);
                }
                catch (ArgumentOutOfRangeException)
                {
                    skippedSegmentCount++;
                    break;
                }
                catch (Exception ex) when (ex.Message.Contains("CanvasImageSource") || ex.Message.Contains("MaximumBitmapSize"))
                {
                    break;
                }
            }
        }

        if (skippedSegmentCount > 0)
        {
            Log($"Vector preview: skipped {skippedSegmentCount} oversize segment(s) exceeding device limit of {deviceLimit}.","error");
        }

        if(skippedSegmentCount >= sortedElements.Count())
        {
            throw new ArgumentOutOfRangeException($"all vector segment exceeds device limit of {deviceLimit}. Please check your source, or disable Vector dynamic previewing function.");
        }

        return container;
    }

    internal static View BuildViewportVectorPreviewView(
        IReadOnlyList<VectorCanvasElement> elements,
        int canvasWidth,
        int canvasHeight,
        int viewportX,
        int viewportY,
        int viewportWidth,
        int viewportHeight)
    {
        float viewW = Math.Max(1, viewportWidth);
        float viewH = Math.Max(1, viewportHeight);
        float vpW = Math.Max(1f, viewportWidth);
        float vpH = Math.Max(1f, viewportHeight);
        float vpX = viewportX;
        float vpY = viewportY;
        float scaleX = viewW / vpW;
        float scaleY = viewH / vpH;

        var container = new AbsoluteLayout
        {
            WidthRequest = viewW,
            HeightRequest = viewH,
            HorizontalOptions = LayoutOptions.Start,
            VerticalOptions = LayoutOptions.Start,
            InputTransparent = true,
            Clip = new RectangleGeometry
            {
                Rect = new Rect(0, 0, viewW, viewH),
            },
        };

        if (elements.Count == 0)
        {
            return container;
        }

        int safeCanvasWidth = Math.Max(1, canvasWidth);
        int safeCanvasHeight = Math.Max(1, canvasHeight);
        int deviceLimit = GetDeviceMaxSize();

        foreach (var element in elements.OrderBy(e => e.LayerIndex))
        {
            var segments = element.Draw();
            if (segments is null || segments.Length == 0)
            {
                continue;
            }

            float elementScaleX;
            float elementScaleY;
            float originX;
            float originY;

            if (element.UseUniformScale)
            {
                float uniform = MathF.Min(safeCanvasWidth, safeCanvasHeight);
                elementScaleX = uniform;
                elementScaleY = uniform;
                originX = element.BaseX * safeCanvasWidth + element.RelativeX * uniform;
                originY = element.BaseY * safeCanvasHeight + element.RelativeY * uniform;
            }
            else
            {
                elementScaleX = safeCanvasWidth;
                elementScaleY = safeCanvasHeight;
                originX = element.RelativeX * safeCanvasWidth;
                originY = element.RelativeY * safeCanvasHeight;
            }

            float screenOriginX = (originX - vpX) * scaleX;
            float screenOriginY = (originY - vpY) * scaleY;
            float screenScaleX = elementScaleX * scaleX;
            float screenScaleY = elementScaleY * scaleY;

            float cosA = 0;
            float sinA = 0;
            bool hasRotation = MathF.Abs(element.Rotation) > 0.0001f;
            if (hasRotation)
            {
                cosA = MathF.Cos(element.Rotation);
                sinA = MathF.Sin(element.Rotation);
            }

            foreach (var segment in segments)
            {
                if (segment is null)
                {
                    continue;
                }

                var path = CreatePathFromSegment(
                    segment,
                    screenScaleX,
                    screenScaleY,
                    screenOriginX,
                    screenOriginY,
                    hasRotation,
                    cosA,
                    sinA,
                    deviceLimit);
                if (path is not null)
                {
                    container.Children.Add(path);
                }
            }
        }

        return container;
    }

    private static void ValidateAbsoluteBounds(float absoluteX, float absoluteY, int deviceLimit)
    {
        if (float.IsNaN(absoluteX) || float.IsNaN(absoluteY) ||
            float.IsInfinity(absoluteX) || float.IsInfinity(absoluteY) ||
            MathF.Abs(absoluteX) > deviceLimit || MathF.Abs(absoluteY) > deviceLimit)
        {
            throw new ArgumentOutOfRangeException($"Vector coordinate ({absoluteX}, {absoluteY}) exceeds device limit of {deviceLimit}");
        }
    }

    private static void ValidateSegmentExtent(float width, float height, int deviceLimit)
    {
        if (width > deviceLimit || height > deviceLimit)
        {
            throw new ArgumentOutOfRangeException($"Vector segment extent ({width}, {height}) exceeds device limit of {deviceLimit}");
        }
    }

    private static ShapesPath? CreatePathFromSegment(
        VectorSegment segment,
        float scaleX, float scaleY,
        float originX, float originY,
        bool hasRotation, float cosA, float sinA,
        int deviceLimit)
    {
        Geometry? geometry = null;

        switch (segment)
        {
            case StraightLineVectorSegment s:
                geometry = CreateLineGeometry(s, scaleX, scaleY, originX, originY, hasRotation, cosA, sinA, deviceLimit);
                break;
            case RoundedRectangleVectorSegment s:
                geometry = CreateRoundedRectGeometry(s, scaleX, scaleY, originX, originY, hasRotation, cosA, sinA, deviceLimit);
                break;
            case RectangleVectorSegment s:
                geometry = CreateRectGeometry(s, scaleX, scaleY, originX, originY, hasRotation, cosA, sinA, deviceLimit);
                break;
            case EllipseVectorSegment s:
                geometry = CreateEllipseGeometry(s, scaleX, scaleY, originX, originY, hasRotation, cosA, sinA, deviceLimit);
                break;
            case CubicBezierVectorSegment s:
                geometry = CreateCubicBezierGeometry(s, scaleX, scaleY, originX, originY, hasRotation, cosA, sinA, deviceLimit);
                break;
            case QuadraticBezierVectorSegment s:
                geometry = CreateQuadraticBezierGeometry(s, scaleX, scaleY, originX, originY, hasRotation, cosA, sinA, deviceLimit);
                break;
            case ArcVectorSegment s:
                geometry = CreateArcGeometry(s, scaleX, scaleY, originX, originY, hasRotation, cosA, sinA, deviceLimit);
                break;
            case PolygonVectorSegment s:
                geometry = CreatePolygonGeometry(s.Points, s.Holes, scaleX, scaleY, originX, originY, hasRotation, cosA, sinA, deviceLimit);
                break;
            case PolylineVectorSegment s:
                geometry = CreatePolylineGeometry(s.Points, scaleX, scaleY, originX, originY, hasRotation, cosA, sinA, deviceLimit);
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

    private static MauiPoint NToP(float nx, float ny, float scaleX, float scaleY, float originX, float originY, bool rot, float cosA, float sinA, int deviceLimit)
    {
        if (rot)
        {
            float rx = nx * cosA - ny * sinA;
            float ry = nx * sinA + ny * cosA;
            float absX = rx * scaleX + originX;
            float absY = ry * scaleY + originY;
            ValidateAbsoluteBounds(absX, absY, deviceLimit);
            return new MauiPoint(absX, absY);
        }
        float ax = nx * scaleX + originX;
        float ay = ny * scaleY + originY;
        ValidateAbsoluteBounds(ax, ay, deviceLimit);
        return new MauiPoint(ax, ay);
    }

    private static Geometry CreateLineGeometry(StraightLineVectorSegment s,
        float sx, float sy, float ox, float oy, bool rot, float cosA, float sinA, int deviceLimit)
    {
        var start = NToP(s.X1, s.Y1, sx, sy, ox, oy, rot, cosA, sinA, deviceLimit);
        var end = NToP(s.X2, s.Y2, sx, sy, ox, oy, rot, cosA, sinA, deviceLimit);

        ValidateSegmentExtent((float)Math.Abs(end.X - start.X), (float)Math.Abs(end.Y - start.Y), deviceLimit);

        var figure = new PathFigure { StartPoint = start, IsClosed = false, IsFilled = false };
        figure.Segments.Add(new LineSegment { Point = end });

        var pg = new PathGeometry();
        pg.Figures.Add(figure);
        return pg;
    }

    private static Geometry CreateRectGeometry(RectangleVectorSegment s,
        float sx, float sy, float ox, float oy, bool rot, float cosA, float sinA, int deviceLimit)
    {
        if (!rot)
        {
            var topLeft = NToP(s.X, s.Y, sx, sy, ox, oy, false, 0, 0, deviceLimit);
            ValidateSegmentExtent(s.Width * sx, s.Height * sy, deviceLimit);
            return new RectangleGeometry
            {
                Rect = new Rect(topLeft.X, topLeft.Y, s.Width * sx, s.Height * sy)
            };
        }

        var p0 = NToP(s.X, s.Y, sx, sy, ox, oy, true, cosA, sinA, deviceLimit);
        var p1 = NToP(s.X + s.Width, s.Y, sx, sy, ox, oy, true, cosA, sinA, deviceLimit);
        var p2 = NToP(s.X + s.Width, s.Y + s.Height, sx, sy, ox, oy, true, cosA, sinA, deviceLimit);
        var p3 = NToP(s.X, s.Y + s.Height, sx, sy, ox, oy, true, cosA, sinA, deviceLimit);

        ValidateSegmentExtent((float)Math.Abs(p1.X - p0.X), (float)Math.Abs(p2.Y - p1.Y), deviceLimit);

        return BuildPolygonPathGeometry([p0, p1, p2, p3], closeFigure: true, deviceLimit);
    }

    private static Geometry CreateRoundedRectGeometry(RoundedRectangleVectorSegment s,
        float sx, float sy, float ox, float oy, bool rot, float cosA, float sinA, int deviceLimit)
    {
        float nX = s.X, nY = s.Y, nW = s.Width, nH = s.Height;
        float r = Math.Min(s.CornerRadius, Math.Min(nW, nH) * 0.5f);

        if (!rot)
        {
            var pg = new PathGeometry();
            var figure = new PathFigure { IsClosed = true, IsFilled = true };

            float pxR = r * sx, pyR = r * sy;
            var c0 = NToP(nX + r, nY, sx, sy, ox, oy, false, 0, 0, deviceLimit);
            var c1 = NToP(nX + nW - r, nY, sx, sy, ox, oy, false, 0, 0, deviceLimit);
            var c2 = NToP(nX + nW, nY + r, sx, sy, ox, oy, false, 0, 0, deviceLimit);
            var c3 = NToP(nX + nW, nY + nH - r, sx, sy, ox, oy, false, 0, 0, deviceLimit);
            var c4 = NToP(nX + nW - r, nY + nH, sx, sy, ox, oy, false, 0, 0, deviceLimit);
            var c5 = NToP(nX + r, nY + nH, sx, sy, ox, oy, false, 0, 0, deviceLimit);
            var c6 = NToP(nX, nY + nH - r, sx, sy, ox, oy, false, 0, 0, deviceLimit);
            var c7 = NToP(nX, nY + r, sx, sy, ox, oy, false, 0, 0, deviceLimit);
            ValidateSegmentExtent(nW * sx, nH * sy, deviceLimit);
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
        var p0 = NToP(nX, nY, sx, sy, ox, oy, true, cosA, sinA, deviceLimit);
        var p1 = NToP(nX + nW, nY, sx, sy, ox, oy, true, cosA, sinA, deviceLimit);
        var p2 = NToP(nX + nW, nY + nH, sx, sy, ox, oy, true, cosA, sinA, deviceLimit);
        var p3 = NToP(nX, nY + nH, sx, sy, ox, oy, true, cosA, sinA, deviceLimit);
        return BuildPolygonPathGeometry([p0, p1, p2, p3], closeFigure: true, deviceLimit);
    }

    private static Geometry CreateEllipseGeometry(EllipseVectorSegment s,
        float sx, float sy, float ox, float oy, bool rot, float cosA, float sinA, int deviceLimit)
    {
        var center = NToP(s.X, s.Y, sx, sy, ox, oy, rot, cosA, sinA, deviceLimit);
        ValidateSegmentExtent(s.RadiusX * sx * 2, s.RadiusY * sy * 2, deviceLimit);
        return new EllipseGeometry
        {
            Center = center,
            RadiusX = s.RadiusX * sx,
            RadiusY = s.RadiusY * sy,
        };
    }

    private static Geometry CreateCubicBezierGeometry(CubicBezierVectorSegment s,
        float sx, float sy, float ox, float oy, bool rot, float cosA, float sinA, int deviceLimit)
    {
        var p1 = NToP(s.X1, s.Y1, sx, sy, ox, oy, rot, cosA, sinA, deviceLimit);
        var p2 = NToP(s.X2, s.Y2, sx, sy, ox, oy, rot, cosA, sinA, deviceLimit);
        var p3 = NToP(s.X3, s.Y3, sx, sy, ox, oy, rot, cosA, sinA, deviceLimit);
        var p4 = NToP(s.X4, s.Y4, sx, sy, ox, oy, rot, cosA, sinA, deviceLimit);
        ValidateSegmentExtent(
            (float)(Math.Max(Math.Max(p1.X, p2.X), Math.Max(p3.X, p4.X)) - Math.Min(Math.Min(p1.X, p2.X), Math.Min(p3.X, p4.X))),
            (float)(Math.Max(Math.Max(p1.Y, p2.Y), Math.Max(p3.Y, p4.Y)) - Math.Min(Math.Min(p1.Y, p2.Y), Math.Min(p3.Y, p4.Y))),
            deviceLimit);

        var figure = new PathFigure { StartPoint = p1, IsClosed = false, IsFilled = false };
        figure.Segments.Add(new BezierSegment { Point1 = p2, Point2 = p3, Point3 = p4 });

        var pg = new PathGeometry();
        pg.Figures.Add(figure);
        return pg;
    }

    private static Geometry CreateQuadraticBezierGeometry(QuadraticBezierVectorSegment s,
        float sx, float sy, float ox, float oy, bool rot, float cosA, float sinA, int deviceLimit)
    {
        var p1 = NToP(s.X1, s.Y1, sx, sy, ox, oy, rot, cosA, sinA, deviceLimit);
        var p2 = NToP(s.X2, s.Y2, sx, sy, ox, oy, rot, cosA, sinA, deviceLimit);
        var p3 = NToP(s.X3, s.Y3, sx, sy, ox, oy, rot, cosA, sinA, deviceLimit);
        ValidateSegmentExtent(
            (float)(Math.Max(Math.Max(p1.X, p2.X), p3.X) - Math.Min(Math.Min(p1.X, p2.X), p3.X)),
            (float)(Math.Max(Math.Max(p1.Y, p2.Y), p3.Y) - Math.Min(Math.Min(p1.Y, p2.Y), p3.Y)),
            deviceLimit);
        var figure = new PathFigure { StartPoint = p1, IsClosed = false, IsFilled = false };
        figure.Segments.Add(new QuadraticBezierSegment { Point1 = p2, Point2 = p3 });

        var pg = new PathGeometry();
        pg.Figures.Add(figure);
        return pg;
    }

    private static Geometry CreateArcGeometry(ArcVectorSegment s,
        float sx, float sy, float ox, float oy, bool rot, float cosA, float sinA, int deviceLimit)
    {
        float startAngle = s.StartAngle;
        float endAngle = s.StartAngle + s.SweepAngle;

        // Compute start/end in normalized space
        float startNX = s.X + s.RadiusX * MathF.Cos(startAngle);
        float startNY = s.Y + s.RadiusY * MathF.Sin(startAngle);
        float endNX = s.X + s.RadiusX * MathF.Cos(endAngle);
        float endNY = s.Y + s.RadiusY * MathF.Sin(endAngle);

        var startPt = NToP(startNX, startNY, sx, sy, ox, oy, rot, cosA, sinA, deviceLimit);
        var endPt = NToP(endNX, endNY, sx, sy, ox, oy, rot, cosA, sinA, deviceLimit);

        bool isLargeArc = MathF.Abs(s.SweepAngle) > MathF.PI;
        var sweepDir = s.SweepAngle >= 0 ? SweepDirection.Clockwise : SweepDirection.CounterClockwise;
        ValidateSegmentExtent(s.RadiusX * sx * 2, s.RadiusY * sy * 2, deviceLimit);
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
        float sx, float sy, float ox, float oy, bool rot, float cosA, float sinA, int deviceLimit)
    {
        if (points is null || points.Length == 0)
            return null!;

        var pg = new PathGeometry();
        if (holes is { Length: > 0 })
            pg.FillRule = FillRule.EvenOdd;

        var pts = points.Select(p => NToP(p.X, p.Y, sx, sy, ox, oy, rot, cosA, sinA, deviceLimit)).ToArray();
        var mainFigure = new PathFigure { StartPoint = pts[0], IsClosed = true, IsFilled = true };
        if (pts.Length > 1)
        {
            var segPoints = new PointCollection();
            for (int i = 1; i < pts.Length; i++) segPoints.Add(pts[i]);
            ValidateSegmentExtent(
                (float)(pts.Max(p => p.X) - pts.Min(p => p.X)),
                (float)(pts.Max(p => p.Y) - pts.Min(p => p.Y)),
                deviceLimit);
            mainFigure.Segments.Add(new PolyLineSegment { Points = segPoints });
        }
        pg.Figures.Add(mainFigure);

        if (holes is not null)
        {
            foreach (var hole in holes)
            {
                if (hole is null || hole.Length == 0) continue;
                var holePts = hole.Select(p => NToP(p.X, p.Y, sx, sy, ox, oy, rot, cosA, sinA, deviceLimit)).ToArray();
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
        float sx, float sy, float ox, float oy, bool rot, float cosA, float sinA, int deviceLimit)
    {
        if (points is null || points.Length < 2)
            return null!;

        var pts = points.Select(p => NToP(p.X, p.Y, sx, sy, ox, oy, rot, cosA, sinA, deviceLimit)).ToArray();
        var figure = new PathFigure { StartPoint = pts[0], IsClosed = false, IsFilled = false };
        var segPoints = new PointCollection();
        for (int i = 1; i < pts.Length; i++) segPoints.Add(pts[i]);
        ValidateSegmentExtent(
            (float)(pts.Max(p => p.X) - pts.Min(p => p.X)),
            (float)(pts.Max(p => p.Y) - pts.Min(p => p.Y)),
            deviceLimit);
        figure.Segments.Add(new PolyLineSegment { Points = segPoints });

        var pg = new PathGeometry();
        pg.Figures.Add(figure);
        return pg;
    }

    private static Geometry BuildPolygonPathGeometry(IReadOnlyList<MauiPoint> pts, bool closeFigure, int deviceLimit)
    {
        if (pts.Count == 0) return null!;

        var figure = new PathFigure { StartPoint = pts[0], IsClosed = closeFigure, IsFilled = closeFigure };
        if (pts.Count > 1)
        {
            var segPoints = new PointCollection();
            for (int i = 1; i < pts.Count; i++) segPoints.Add(pts[i]);
            ValidateSegmentExtent(
                (float)(pts.Max(p => p.X) - pts.Min(p => p.X)),
                (float)(pts.Max(p => p.Y) - pts.Min(p => p.Y)),
                deviceLimit);
            figure.Segments.Add(new PolyLineSegment { Points = segPoints });
        }

        var pg = new PathGeometry();
        pg.Figures.Add(figure);
        return pg;
    }

    #endregion

    // ═══════════════════════════════════════════════════════════
    // Rasterization fallback for large ComponentGroup previews
    // ═══════════════════════════════════════════════════════════

    /// <summary>
    /// Rasterises a set of <see cref="VectorCanvasElement"/>s to a bitmap and crops to the
    /// specified viewport rectangle.  Used as a drop-in replacement for
    /// <see cref="BuildViewportVectorPreviewView"/> when a <see cref="Render.VectorContent.Components.ComponentGroup"/>
    /// contains more children than <see cref="GroupRasterizationChildThreshold"/>.
    /// The resulting <see cref="Image"/> view carries a single bitmap rather than N individual
    /// Path objects, avoiding GPU / platform path-count limits.
    /// </summary>
    internal static View BuildRasterizedGroupPreviewView(
        IReadOnlyList<VectorCanvasElement> elements,
        int canvasWidth, int canvasHeight,
        int viewportX, int viewportY,
        int viewportWidth, int viewportHeight)
    {
        int safeW = Math.Max(1, canvasWidth);
        int safeH = Math.Max(1, canvasHeight);

        var vectorPicture = new VectorPicture { Elements = elements.ToList() };

        IPicture? fullRaster = null;
        try
        {
            fullRaster = IVectorContentClip.GlobalDefaultRasterizer.Convert(
                vectorPicture, safeW, safeH, false,
                IVectorContentClip.GlobalDefaultAntiAliasMode);

            if (fullRaster is null || fullRaster.Disposed)
            {
                return CreateEmptyGroupPreviewView(viewportWidth, viewportHeight);
            }

            // Crop to viewport region.  If the viewport is within canvas bounds we
            // can extract a sub-region; otherwise fall back to a scaled preview.
            int vpX = Math.Clamp(viewportX, 0, safeW - 1);
            int vpY = Math.Clamp(viewportY, 0, safeH - 1);
            int vpW = Math.Min(viewportWidth, safeW - vpX);
            int vpH = Math.Min(viewportHeight, safeH - vpY);

            if (vpW <= 0 || vpH <= 0)
            {
                return CreateEmptyGroupPreviewView(viewportWidth, viewportHeight);
            }

            var cropped = CropPictureRegion(fullRaster, vpX, vpY, viewportWidth, viewportHeight);

            return new Image
            {
                Source = cropped.ToImageSource(),
                WidthRequest = Math.Max(1, viewportWidth),
                HeightRequest = Math.Max(1, viewportHeight),
                HorizontalOptions = LayoutOptions.Start,
                VerticalOptions = LayoutOptions.Start,
                Aspect = Aspect.Fill,
                InputTransparent = true,
            };
        }
        catch (Exception ex)
        {
            Log($"Rasterized group preview failed (elements={elements.Count}, " +
                $"viewport={viewportWidth}×{viewportHeight}): {ex.Message}");
            return CreateEmptyGroupPreviewView(viewportWidth, viewportHeight);
        }
        finally
        {
            if (fullRaster is not null && !fullRaster.Disposed)
            {
                fullRaster.Dispose();
            }
        }
    }

    /// <summary>
    /// Returns a transparent placeholder Image for the group preview when
    /// rasterization fails or produces no visible content.
    /// </summary>
    private static View CreateEmptyGroupPreviewView(int width, int height)
    {
        return new Image
        {
            WidthRequest = Math.Max(1, width),
            HeightRequest = Math.Max(1, height),
            HorizontalOptions = LayoutOptions.Start,
            VerticalOptions = LayoutOptions.Start,
            InputTransparent = true,
        };
    }

    /// <summary>
    /// Extracts a rectangular region from <paramref name="source"/> and returns it as a new
    /// <see cref="Picture8bpp"/> of the requested <paramref name="w"/> × <paramref name="h"/> size.
    /// The crop rectangle is clamped to the source bounds; pixels outside the source become
    /// transparent (zero alpha).  If the source uses 16bpp it is first down-converted to 8bpp.
    /// </summary>
    private static IPicture CropPictureRegion(IPicture source, int x, int y, int w, int h)
    {
        var src8 = (Picture8bpp)source.ToBitPerPixel(IPicture.PicturePixelMode.BytePicture);

        int srcW = src8.Width;
        int srcH = src8.Height;

        // Clamp crop rectangle to source bounds
        int cropX = Math.Clamp(x, 0, Math.Max(0, srcW - 1));
        int cropY = Math.Clamp(y, 0, Math.Max(0, srcH - 1));
        int cropW = Math.Min(w, srcW - cropX);
        int cropH = Math.Min(h, srcH - cropY);

        var result = new Picture8bpp(Math.Max(1, w), Math.Max(1, h))
        {
            HasAlphaChannel = src8.HasAlphaChannel,
            ProcessStack = new List<PictureProcessStack>
            {
                new PictureProcessStack
                {
                    OperationDisplayName = "CropPictureRegion",
                    Operator = typeof(DynamicPreview),
                    ProcessingFuncStackTrace = new StackTrace(true),
                    Properties = new Dictionary<string, object>
                    {
                        ["SourceWidth"] = srcW,
                        ["SourceHeight"] = srcH,
                        ["CropX"] = cropX,
                        ["CropY"] = cropY,
                        ["CropWidth"] = cropW,
                        ["CropHeight"] = cropH,
                        ["TargetWidth"] = w,
                        ["TargetHeight"] = h,
                    },
                }
            }
        };

        if (cropW <= 0 || cropH <= 0)
            return result; // fully transparent placeholder

        var srcR = (byte[])src8.GetSpecificChannel(IPicture.ChannelId.Red);
        var srcG = (byte[])src8.GetSpecificChannel(IPicture.ChannelId.Green);
        var srcB = (byte[])src8.GetSpecificChannel(IPicture.ChannelId.Blue);

        // Fast row-by-row copy for the overlapping region
        for (int row = 0; row < cropH; row++)
        {
            int srcBase = (cropY + row) * srcW + cropX;
            int dstBase = row * w;
            Array.Copy(srcR, srcBase, result.r, dstBase, cropW);
            Array.Copy(srcG, srcBase, result.g, dstBase, cropW);
            Array.Copy(srcB, srcBase, result.b, dstBase, cropW);
        }

        if (src8.HasAlphaChannel)
        {
            result.a = new float[result.Pixels];
            var srcA = (float[])src8.GetSpecificChannel(IPicture.ChannelId.Alpha);
            for (int row = 0; row < cropH; row++)
            {
                int srcBase = (cropY + row) * srcW + cropX;
                int dstBase = row * w;
                Array.Copy(srcA, srcBase, result.a, dstBase, cropW);
            }

            // Pixels outside the crop region remain zero alpha (transparent) already.
        }

        return result;
    }

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

    private readonly record struct FallbackFrameCacheKey(Guid ClipId, int TargetWidth, int TargetHeight, uint FrameIndex, long SourceFingerprint);

}