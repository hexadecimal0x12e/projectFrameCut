using projectFrameCut.Render.RenderAPIBase.ClipAndTrack;
using projectFrameCut.DraftStuff;
using projectFrameCut.Shared;
using System;
using System.Collections.Generic;
using System.IO;
using Microsoft.Maui.Controls;
using Microsoft.Maui.Layouts;
using Path = System.IO.Path;
using Image = Microsoft.Maui.Controls.Image;
using projectFrameCut.Asset;
using System.Linq;
using System.Buffers.Binary;
using System.Threading;
using System.Threading.Tasks;

namespace projectFrameCut.InteractableEditor
{
    /// <summary>
    /// Reuses thumbnail controls in the viewport and releases them when the clip leaves it.
    /// </summary>
    public sealed class OnClipUIPreview(DraftPage page, ClipElementUI clip) : IDisposable
    {
        private const int PoolSpareTiles = 3;
        private const int PoolShrinkThreshold = 8;
        private const int MaxVideoFrameBatch = 4;
        private static readonly SemaphoreSlim MetadataLoadGate = new(4);
        private readonly ClipMode _clipType = clip.ClipType;
        private readonly string? _sourcePath = clip.SourcePath;
        private CancellationTokenSource? _metadataCancellation;
        private string? _photoSourcePath;
        private bool _metadataLoading;
        private bool _metadataMissing;

        public bool Refresh(ClipElementUI current)
        {
            if (_disposed || !ReferenceEquals(clip, current) || _clipType != clip.ClipType
                || _sourcePath != clip.SourcePath || _previewRoot is null || _previewRoot.Parent != clip.Clip.Content)
                return false;
            if (_clipType == ClipMode.PhotoClip && _photoSourcePath != ResolvePhotoPath()) return false;
            if (_clipType == ClipMode.VideoClip && Math.Abs(_videoPreviewHeight - VideoPreviewHeight) > 0.1
                || _clipType == ClipMode.PhotoClip && Math.Abs(_photoThumbHeight - Math.Max(28, clip.Clip.HeightRequest - 14)) > 0.1) return false;
            foreach (var label in _previewRoot.Children.OfType<Label>())
                label.Text = clip.DisplayName ?? clip.Id.ToString();
            _metadataMissing = false;
            _videoRequestedFrames.Clear();
            _lastFirst = -1;
            _lastLast = -1;
            page.ScheduleClipPreviewViewportUpdate();
            return true;
        }

        public View? Update()
        {
            return clip.ClipType switch
            {
                ClipMode.VideoClip => BuildVideoPreview(),
                ClipMode.PhotoClip => BuildPhotoPreview(),
                _ => null
            };
        }

        // ── Video preview state ────────────────────────────────────────────

        private string? _videoThumbDir;
        private string? _videoLocalThumbDir;
        private string? _videoLegacyThumbDir;
        private CancellationTokenSource? _videoFrameCancellation;
        private readonly HashSet<int> _videoRequestedFrames = [];
        private int _videoFrameWidth;
        private double _videoPreviewHeight;
        private int _videoCountOfFrame;
        private (uint SourceStart, uint SourceLength, uint Duration, double PixelWidth, double SourceRate) _lastVideoTiming;
        private List<int>? _videoAvailableFrames;
        private AbsoluteLayout? _videoFrameContainer;
        private readonly List<Image> _videoFramePool = [];
        private readonly List<int> _videoPoolFrames = [];
        private readonly List<int> _videoPoolTiles = [];
        private readonly Dictionary<int, ImageSource> _videoFrameSources = [];

        // ── Photo preview state ────────────────────────────────────────────

        private ImageSource? _photoImageSource;
        private int _photoImageWidth;
        private double _photoThumbHeight;
        private int _photoCountOfTiles;
        private AbsoluteLayout? _photoTileContainer;
        private readonly List<Image> _photoTilePool = [];
        private readonly List<int> _photoPoolTiles = [];

        // ── Shared scroll-aware container ──────────────────────────────────

        private bool _disposed;
        private int _lastFirst = -1;
        private int _lastLast = -1;
        private double _lastContentWidth = -1;
        private Grid? _previewRoot;

        internal void SetZoomSuspended(bool suspended)
        {
            if (_disposed || _previewRoot is null) return;
            if (suspended)
                _videoFrameCancellation?.Cancel();
            else
            {
                _lastContentWidth = -1;
                _lastFirst = -1;
                _lastLast = -1;
            }
            _previewRoot.IsVisible = !suspended;
        }

        private sealed class PreviewGrid : Grid
        {
            protected override ILayoutManager CreateLayoutManager() => new PreviewLayoutManager(this);
        }

        private sealed class PreviewLayoutManager(PreviewGrid grid) : GridLayoutManager(grid)
        {
            public override Size Measure(double widthConstraint, double heightConstraint)
            {
                var size = base.Measure(widthConstraint, heightConstraint);
                // Native layout passes also use this manager; tiles must not widen the handle grid.
                return new Size(double.IsFinite(widthConstraint) ? Math.Max(0, widthConstraint) : 0, size.Height);
            }
        }

        /// <summary>
        /// Updates the visible frame/tile range supplied by the page's viewport index.
        /// </summary>
        /// <returns>False if the preview container has been detached from the visual tree;
        /// the caller should treat this as a signal to clean up.</returns>
        public bool NotifyScrollChanged(double scrollX, double viewportWidth)
        {
            if (_disposed)
                return false;

            // Self-heal: if the container has been removed from the visual tree,
            // signal the caller to clean us up.
            var previewContainer = clip.ClipType is ClipMode.PhotoClip
                ? (VisualElement?)_photoTileContainer
                : _videoFrameContainer;
            if (previewContainer?.Parent is null)
                return false;
            if (page.ClipPreviewsSuspendedForZoom) return true;

            if (_clipType == ClipMode.VideoClip && _videoAvailableFrames is null
                || _clipType == ClipMode.PhotoClip && _photoImageSource is null)
            {
                if (ContentGlobalStartX < scrollX + Math.Max(viewportWidth, 100)
                    && ContentGlobalStartX + ContentWidth > scrollX)
                    StartMetadataLoad();
                return true;
            }

            if (clip.ClipType is ClipMode.VideoClip)
            {
                UpdateVideoLayoutMetrics();
                UpdateVideoVisibleFrames(scrollX, viewportWidth);
            }
            else
            {
                UpdatePhotoLayoutMetrics();
                UpdatePhotoVisibleTiles(scrollX, viewportWidth);
            }

            return true;
        }

        /// <summary>
        /// X of the preview in the horizontal timeline's scroll content.
        /// </summary>
        private double ContentGlobalStartX
        {
            get
            {
                double x = ContentOffsetX;
                for (VisualElement? e = clip.Clip; e is not null && e is not ScrollView; e = e.Parent as VisualElement)
                    x += e.X + e.TranslationX;
                return double.IsFinite(x) ? x : ContentOffsetX;
            }
        }

        private double ContentOffsetX
        {
            get
            {
                if (_previewRoot is { Width: > 0, Parent: not null })
                {
                    double x = 0;
                    for (VisualElement? e = _previewRoot; e is not null && e != clip.Clip; e = e.Parent as VisualElement)
                        x += e.X + e.TranslationX;
                    return x;
                }
                return 30 + clip.Clip.StrokeThickness + clip.Clip.Padding.Left;
            }
        }

        /// <summary>
        /// Width inside the clip border, padding and two 30px handles.
        /// </summary>
        private double ContentWidth
        {
            get
            {
                double cw = clip.Clip.WidthRequest > 0
                    ? clip.Clip.WidthRequest
                    : (clip.origLength > 0 ? clip.origLength : Math.Max(60, clip.Clip.Width));
                double width = Math.Max(0, cw - 60 - clip.Clip.StrokeThickness * 2 - clip.Clip.Padding.HorizontalThickness);
                return _previewRoot is { Width: > 0, Parent: not null } ? Math.Min(width, _previewRoot.Width) : width;
            }
        }

        private double VideoPreviewHeight
        {
            get
            {
                double height = clip.Clip.HeightRequest > 0 ? clip.Clip.HeightRequest
                    : clip.Clip.Height > 0 ? clip.Clip.Height : DraftPage.ClipHeight;
                return Math.Max(1, height - clip.Clip.StrokeThickness * 2 - clip.Clip.Padding.VerticalThickness);
            }
        }

        private static int GetPreviewTileCount(double width, int tileWidth)
        {
            int count = (int)Math.Floor(width / tileWidth);
            return width - count * (double)tileWidth >= tileWidth / 3d ? count + 1 : count;
        }

        // ════════════════════════════════════════════════════════════════════
        //  Video preview
        // ════════════════════════════════════════════════════════════════════

        private View? BuildVideoPreview()
        {
            var clipId = clip.Id;
            var legacyThumbDir = Path.Combine(ProjectCachePaths.GetRoot(page.WorkingPath), "thumbs", "perClip", clipId.ToString());
            var thumbDir = Path.Combine(legacyThumbDir, "timeline");
            if (clip.SourcePath?.StartsWith('$') ?? false)
            {
                var assetId = clip.SourcePath[1..];
                if (!page.Assets.ContainsKey(assetId) && AssetDatabase.Assets.TryGetValue(assetId, out var asset))
                {
                    legacyThumbDir = Path.Combine(MauiProgram.DataPath, "My Assets", ".perAssetThumb", assetId);
                }
            }

            var previewHeight = VideoPreviewHeight;

            _videoThumbDir = thumbDir;
            _videoLocalThumbDir = thumbDir;
            _videoLegacyThumbDir = legacyThumbDir;
            _videoPreviewHeight = previewHeight;

            _videoFrameContainer = new AbsoluteLayout
            {
                HeightRequest = previewHeight,
                InputTransparent = true,
                IsClippedToBounds = true,
                VerticalOptions = LayoutOptions.Fill,
                HorizontalOptions = LayoutOptions.Fill,
            };
            _previewRoot = new PreviewGrid
            {
                IsVisible = !page.ClipPreviewsSuspendedForZoom,
                HeightRequest = previewHeight,
                HorizontalOptions = LayoutOptions.Fill,
                VerticalOptions = LayoutOptions.Fill,
                IsClippedToBounds = true,
                Padding = 0,
                Children =
                {
                    _videoFrameContainer,
                    new Label
                    {
                        Text = clip.DisplayName ?? clip.Id.ToString(),
                        HorizontalOptions = LayoutOptions.Center,
                        VerticalOptions = LayoutOptions.Center,
                        BackgroundColor = Color.FromRgba("#80808080"),
                        LineBreakMode = LineBreakMode.TailTruncation,
                        MaxLines = 1
                    }
                }
            };
            _previewRoot.Loaded += PreviewRoot_Loaded;
            _previewRoot.SizeChanged += PreviewRoot_Loaded;

            return _previewRoot;
        }

        private sealed record ThumbnailMetadata(string Path, int Width, List<int>? Frames);

        private void StartMetadataLoad()
        {
            if (_metadataLoading || _metadataMissing) return;
            _metadataLoading = true;
            var token = (_metadataCancellation ??= new()).Token;
            var video = _clipType == ClipMode.VideoClip;
            var path = video ? _videoThumbDir : _photoSourcePath;
            var legacyPath = _videoLegacyThumbDir;
            var height = video ? _videoPreviewHeight : _photoThumbHeight;

            _ = Task.Run(async () =>
            {
                ThumbnailMetadata? metadata = null;
                try
                {
                    await MetadataLoadGate.WaitAsync(token).ConfigureAwait(false);
                    try
                    {
                        if (path is not null)
                        {
                            List<int>? available = null;
                            string? imagePath = path;
                            if (video)
                            {
                                var frames = GetNumericFrameFiles(path, token);
                                if (frames.Count == 0 && legacyPath is not null
                                    && !string.Equals(path, legacyPath, StringComparison.OrdinalIgnoreCase))
                                {
                                    path = legacyPath;
                                    frames = GetNumericFrameFiles(path, token);
                                }
                                imagePath = frames.Count > 0 ? frames.Values.First() : null;
                                available = frames.Keys.Order().ToList();
                            }
                            else if (!File.Exists(imagePath)) imagePath = null;

                            if (imagePath is not null)
                            {
                                token.ThrowIfCancellationRequested();
                                var (width, imageHeight) = ReadImageDimensions(imagePath);
                                metadata = new(path, Math.Max(1, (int)Math.Round(width * height / imageHeight)), available);
                            }
                        }
                    }
                    finally { MetadataLoadGate.Release(); }
                }
                catch (OperationCanceledException) when (token.IsCancellationRequested) { return; }
                catch (Exception ex)
                {
                    if (!token.IsCancellationRequested) Log(ex, $"Load timeline preview for clip {clip.Id}", this);
                }

                if (token.IsCancellationRequested) return;
                page.Dispatcher.Dispatch(() =>
                {
                    if (_disposed || token.IsCancellationRequested) return;
                    _metadataLoading = false;
                    _metadataCancellation?.Dispose();
                    _metadataCancellation = null;
                    _metadataMissing = metadata is null;
                    if (metadata is null) return;
                    if (video)
                    {
                        _videoThumbDir = metadata.Path;
                        _videoAvailableFrames = metadata.Frames;
                        _videoFrameWidth = metadata.Width;
                        UpdateVideoLayoutMetrics();
                    }
                    else
                    {
                        _photoImageSource = CreateThumbnailSource(metadata.Path);
                        _photoImageWidth = metadata.Width;
                        UpdatePhotoLayoutMetrics();
                    }
                    page.ScheduleClipPreviewViewportUpdate();
                });
            });
        }

        private static (int Width, int Height) ReadImageDimensions(string path)
        {
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            Span<byte> header = stackalloc byte[24];
            int count = stream.ReadAtLeast(header, header.Length, throwOnEndOfStream: false);
            int width;
            int height;
            if (count >= 24 && header[0] == 137 && header[1..8].SequenceEqual("PNG\r\n\u001a\n"u8)
                && header[12..16].SequenceEqual("IHDR"u8))
            {
                width = BinaryPrimitives.ReadInt32BigEndian(header[16..20]);
                height = BinaryPrimitives.ReadInt32BigEndian(header[20..24]);
            }
            else if (count >= 18 && header[..4].SequenceEqual("VFCD"u8))
            {
                if (header[4] > 2 && (header[4] != 255 || header[5] != 2))
                    throw new InvalidDataException($"Unsupported timeline VFD version: {path}");
                int offset = header[4] == 255 && header[5] == 2 ? 10 : 5;
                width = BinaryPrimitives.ReadInt32LittleEndian(header.Slice(offset, 4));
                height = BinaryPrimitives.ReadInt32LittleEndian(header.Slice(offset + 4, 4));
            }
            else throw new InvalidDataException($"Unsupported timeline picture header: {path}");
            if (width <= 0 || height <= 0) throw new InvalidDataException($"Invalid timeline picture dimensions: {path}");
            return (width, height);
        }

        private static Dictionary<int, string> GetNumericFrameFiles(string directory, CancellationToken token)
        {
            if (!Directory.Exists(directory)) return [];

            var result = new Dictionary<int, string>();
            foreach (var path in Directory.EnumerateFiles(directory, "*.png", SearchOption.TopDirectoryOnly))
            {
                token.ThrowIfCancellationRequested();
                if (int.TryParse(Path.GetFileNameWithoutExtension(path), out var frame))
                    result[frame] = path;
            }
            return result;
        }

        // Avoid WinRT StorageFile calls on the UI thread when a pooled image changes source.
        private static ImageSource CreateThumbnailSource(string path)
            => ImageSource.FromStream(token => Task.Run<Stream>(() => File.OpenRead(path), token));

        private void UpdateVideoVisibleFrames(double scrollX, double viewportWidth)
        {
            if (_videoFrameContainer is null || _videoThumbDir is null || _videoAvailableFrames is not { Count: > 0 })
                return;

            double contentStart = ContentGlobalStartX;
            double contentWidth = ContentWidth;

            // Viewport bounds
            double viewportLeft = scrollX;
            double viewportRight = scrollX + Math.Max(viewportWidth, 100);

            // Check if the content area is completely outside the viewport
            double contentEnd = contentStart + contentWidth;
            if (contentEnd <= viewportLeft || contentStart >= viewportRight)
                return;

            // Visible range within the content area
            double visibleLeftInContent = Math.Max(0, viewportLeft - contentStart);
            double visibleRightInContent = Math.Min(contentWidth, viewportRight - contentStart);

            double step = _videoFrameWidth;
            if (step <= 0) return;

            int firstVisible = Math.Max(0, (int)(visibleLeftInContent / step) - 2);
            int lastVisible = Math.Min(_videoCountOfFrame - 1,
                (int)Math.Ceiling(visibleRightInContent / step) + 2);

            if (_lastFirst == firstVisible && _lastLast == lastVisible)
                return; // no change

            if (_lastFirst >= 0)
            {
                _videoRequestedFrames.Clear();
                if (firstVisible > _lastLast || lastVisible < _lastFirst)
                    _videoFrameCancellation?.Cancel();
            }
            _lastFirst = firstVisible;
            _lastLast = lastVisible;

            int visibleCount = Math.Max(0, lastVisible - firstVisible + 1);
            EnsureVideoFramePoolSize(visibleCount);
            var missing = new HashSet<int>();

            // Keep each tile on the same view until it leaves the pooled range.
            for (int tileIndex = firstVisible; tileIndex <= lastVisible; tileIndex++)
            {
                int poolIndex = tileIndex % _videoFramePool.Count;
                bool moved = _videoPoolTiles[poolIndex] != tileIndex;
                int target = GetVideoSourceFrameForTile(tileIndex);
                int index = _videoAvailableFrames.BinarySearch(target);
                if (index < 0)
                {
                    if (missing.Count < MaxVideoFrameBatch && !_videoRequestedFrames.Contains(target))
                        missing.Add(target);
                    index = ~index;
                    if (index == _videoAvailableFrames.Count || index > 0
                        && target - _videoAvailableFrames[index - 1] <= _videoAvailableFrames[index] - target)
                        index--;
                }
                int frame = _videoAvailableFrames[index];
                if (_videoPoolFrames[poolIndex] != frame)
                {
                    _videoPoolFrames[poolIndex] = frame;
                    if (!_videoFrameSources.TryGetValue(frame, out var source))
                    {
                        source = CreateThumbnailSource(Path.Combine(_videoThumbDir, $"{frame}.png"));
                        _videoFrameSources[frame] = source;
                    }
                    _videoFramePool[poolIndex].Source = source;
                }

                if (moved)
                {
                    _videoPoolTiles[poolIndex] = tileIndex;
                    AbsoluteLayout.SetLayoutBounds(_videoFramePool[poolIndex],
                        new Rect(tileIndex * step, 0, _videoFrameWidth, _videoPreviewHeight));
                }
                if (!_videoFramePool[poolIndex].IsVisible) _videoFramePool[poolIndex].IsVisible = true;
            }
            for (int i = 0; i < _videoFramePool.Count; i++)
                if ((_videoPoolTiles[i] < firstVisible || _videoPoolTiles[i] > lastVisible) && _videoFramePool[i].IsVisible)
                    _videoFramePool[i].IsVisible = false;
            if (missing.Count > 0) StartVideoFrameLoad(missing.Order().ToList());
        }

        private void UpdateVideoLayoutMetrics()
        {
            if (page.ClipPreviewsSuspendedForZoom || _videoFrameContainer is null || _videoFrameWidth <= 0)
                return;

            double contentWidth = ContentWidth;
            int count = GetPreviewTileCount(contentWidth, _videoFrameWidth);
            var timing = (clip.relativeStartFrame, clip.lengthInFrame, page.GetClipFrameRange(clip).Duration,
                page.FrameToPixel(1), page.ProjectInfo.TargetFrameRate * (double)clip.sourceSecondPerFrame);
            if (count == _videoCountOfFrame && Math.Abs(contentWidth - _lastContentWidth) < 0.1 && timing.Equals(_lastVideoTiming))
                return;

            _lastContentWidth = contentWidth;
            _lastVideoTiming = timing;
            _videoFrameCancellation?.Cancel();
            _videoCountOfFrame = count;
            for (int i = 0; i < _videoFramePool.Count; i++)
                if (_videoPoolTiles[i] >= count) _videoFramePool[i].IsVisible = false;
            _lastFirst = -1;
            _lastLast = -1;
        }

        private int GetVideoSourceFrameForTile(int tileIndex)
        {
            uint duration = page.GetClipFrameRange(clip).Duration;
            uint offset = Math.Min(page.PixelToFrame(ContentOffsetX + tileIndex * (double)_videoFrameWidth), duration > 0 ? duration - 1 : 0);
            uint sourceOffset = ClipTiming.SourceOffset(offset, clip.lengthInFrame, clip.SpeedProvider, duration);
            double sourceFrame = (ulong)clip.relativeStartFrame + sourceOffset;
            if (page.ProjectInfo.TargetFrameRate > 0 && float.IsFinite(clip.sourceSecondPerFrame) && clip.sourceSecondPerFrame > 0)
                sourceFrame /= page.ProjectInfo.TargetFrameRate * (double)clip.sourceSecondPerFrame;
            return (int)Math.Clamp(Math.Floor(sourceFrame + 1e-9), 0, page.GetClipSourceFrameCount(clip) - 1);
        }

        private void StartVideoFrameLoad(List<int> frames)
        {
            if (_videoFrameCancellation is not null || _videoLocalThumbDir is null) return;
            var sourcePath = clip.SourcePath;
            if (string.IsNullOrWhiteSpace(sourcePath)) return;
            if (sourcePath.StartsWith('$'))
            {
                if (!page.Assets.TryGetValue(sourcePath[1..], out var asset)
                    && !AssetDatabase.Assets.TryGetValue(sourcePath[1..], out asset)) return;
                sourcePath = asset.Path;
            }
            if (string.IsNullOrWhiteSpace(sourcePath)) return;

            var cts = _videoFrameCancellation = new CancellationTokenSource();
            var token = cts.Token;
            var directory = _videoLocalThumbDir;
            int totalFrames = page.GetClipSourceFrameCount(clip);
            foreach (int frame in frames) _videoRequestedFrames.Add(frame);
            _ = Task.Run(async () =>
            {
                List<int>? available = null;
                try
                {
                    await Task.Delay(100, token).ConfigureAwait(false);
                    await page.previewer.GenerateTimelineThumbnailsAsync(clip.Id, sourcePath, totalFrames,
                        1, MaxVideoFrameBatch, DraftPage.ThumbTargetWidth, token, frames).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (token.IsCancellationRequested) { }
                catch (Exception ex)
                {
                    Log(ex, $"Load visible timeline thumbnails for clip {clip.Id}", this);
                }
                finally
                {
                    try
                    {
                        available = GetNumericFrameFiles(directory, CancellationToken.None).Keys.Order().ToList();
                    }
                    catch (Exception ex) { Log(ex, $"Refresh timeline thumbnail cache for clip {clip.Id}", this); }
                    page.Dispatcher.Dispatch(() =>
                    {
                        if (!ReferenceEquals(_videoFrameCancellation, cts))
                        {
                            cts.Dispose();
                            return;
                        }
                        _videoFrameCancellation = null;
                        cts.Dispose();
                        if (_disposed) return;
                        if (token.IsCancellationRequested)
                            foreach (int frame in frames) _videoRequestedFrames.Remove(frame);
                        if (available is { Count: > 0 })
                        {
                            if (_videoThumbDir != directory)
                            {
                                _videoThumbDir = directory;
                                _videoFrameSources.Clear();
                                for (int i = 0; i < _videoPoolFrames.Count; i++)
                                {
                                    _videoPoolFrames[i] = -1;
                                    _videoFramePool[i].Source = null;
                                }
                            }
                            _videoAvailableFrames = available;
                        }
                        _lastFirst = -1;
                        _lastLast = -1;
                        page.ScheduleClipPreviewViewportUpdate();
                    });
                }
            });
        }

        private void EnsureVideoFramePoolSize(int requiredCount)
        {
            if (_videoFrameContainer is null)
                return;

            int target = Math.Min(_videoCountOfFrame, requiredCount + PoolSpareTiles);
            bool resized = false;
            if (_videoFramePool.Count > target + PoolShrinkThreshold)
            {
                while (_videoFramePool.Count > target)
                {
                    _videoFramePool[^1].Source = null;
                    _videoFrameContainer.Children.Remove(_videoFramePool[^1]);
                    _videoFramePool.RemoveAt(_videoFramePool.Count - 1);
                    _videoPoolFrames.RemoveAt(_videoPoolFrames.Count - 1);
                    _videoPoolTiles.RemoveAt(_videoPoolTiles.Count - 1);
                }
                resized = true;
            }
            if (_videoFramePool.Count < requiredCount)
            {
                while (_videoFramePool.Count < target)
                {
                    var image = new Image
                    {
                        WidthRequest = _videoFrameWidth,
                        HeightRequest = _videoPreviewHeight,
                        Aspect = Aspect.AspectFit,
                        InputTransparent = true,
                        IsVisible = false,
                    };
                    _videoFramePool.Add(image);
                    _videoPoolFrames.Add(-1);
                    _videoPoolTiles.Add(-1);
                    _videoFrameContainer.Children.Add(image);
                }
                resized = true;
            }
            if (resized)
                for (int i = 0; i < _videoPoolTiles.Count; i++) _videoPoolTiles[i] = -1;
        }

        // ════════════════════════════════════════════════════════════════════
        //  Photo preview
        // ════════════════════════════════════════════════════════════════════

        private string? ResolvePhotoPath()
        {
            var sourcePath = clip.SourcePath;
            if (string.IsNullOrWhiteSpace(sourcePath))
                return null;

            if (sourcePath.StartsWith('$'))
            {
                var assetId = sourcePath[1..];
                if (page.Assets.TryGetValue(assetId, out var asset))
                    sourcePath = asset.Path;
                else
                    return null;
            }

            return sourcePath;
        }

        private View? BuildPhotoPreview()
        {
            var sourcePath = ResolvePhotoPath();
            if (string.IsNullOrWhiteSpace(sourcePath)) return null;
            _photoSourcePath = sourcePath;

            var thumbHeight = Math.Max(28, clip.Clip.HeightRequest - 14);
            _photoThumbHeight = thumbHeight;

            // Keep a small, reusable pool of Image controls. Rebuilding the visible
            // children on every tile boundary makes long photo clips repeatedly load
            // the same bitmap and forces layout/GC work while the user is scrolling.
            _photoTileContainer = new AbsoluteLayout
            {
                HeightRequest = thumbHeight,
                InputTransparent = true,
                IsClippedToBounds = true,
                VerticalOptions = LayoutOptions.Fill,
                HorizontalOptions = LayoutOptions.Fill,
            };
            _previewRoot = new PreviewGrid
            {
                IsVisible = !page.ClipPreviewsSuspendedForZoom,
                HeightRequest = thumbHeight,
                InputTransparent = true,
                VerticalOptions = LayoutOptions.Center,
                HorizontalOptions = LayoutOptions.Fill,
                IsClippedToBounds = true,
                Padding = 0,
            };

            _previewRoot.Children.Add(_photoTileContainer);
            _previewRoot.Children.Add(new Label
            {
                Text = clip.DisplayName ?? clip.Id.ToString(),
                HorizontalOptions = LayoutOptions.Center,
                VerticalOptions = LayoutOptions.Center,
                BackgroundColor = Color.FromRgba("#80808080"),
                LineBreakMode = LineBreakMode.TailTruncation,
                MaxLines = 1,
            });
            _previewRoot.Loaded += PreviewRoot_Loaded;
            _previewRoot.SizeChanged += PreviewRoot_Loaded;

            return _previewRoot;
        }

        private void UpdatePhotoVisibleTiles(double scrollX, double viewportWidth)
        {
            if (_photoTileContainer is null || _photoImageSource is null)
                return;

            double contentStart = ContentGlobalStartX;
            double contentWidth = ContentWidth;

            double viewportLeft = scrollX;
            double viewportRight = scrollX + Math.Max(viewportWidth, 100);

            double contentEnd = contentStart + contentWidth;
            if (contentEnd <= viewportLeft || contentStart >= viewportRight)
                return;

            double visibleLeftInContent = Math.Max(0, viewportLeft - contentStart);
            double visibleRightInContent = Math.Min(contentWidth, viewportRight - contentStart);

            double step = _photoImageWidth;
            if (step <= 0) return;

            int firstVisible = Math.Max(0, (int)(visibleLeftInContent / step) - 1);
            int lastVisible = Math.Min(_photoCountOfTiles - 1,
                (int)Math.Ceiling(visibleRightInContent / step) + 1);

            if (_lastFirst == firstVisible && _lastLast == lastVisible)
                return;

            _lastFirst = firstVisible;
            _lastLast = lastVisible;

            int visibleCount = Math.Max(0, lastVisible - firstVisible + 1);
            EnsurePhotoTilePoolSize(visibleCount);

            for (int tileIndex = firstVisible; tileIndex <= lastVisible; tileIndex++)
            {
                int poolIndex = tileIndex % _photoTilePool.Count;
                if (_photoPoolTiles[poolIndex] != tileIndex)
                {
                    _photoPoolTiles[poolIndex] = tileIndex;
                    AbsoluteLayout.SetLayoutBounds(_photoTilePool[poolIndex],
                        new Rect(tileIndex * step, 0, _photoImageWidth, _photoThumbHeight));
                }
                if (_photoTilePool[poolIndex].Source is null) _photoTilePool[poolIndex].Source = _photoImageSource;
                if (!_photoTilePool[poolIndex].IsVisible) _photoTilePool[poolIndex].IsVisible = true;
            }
            for (int i = 0; i < _photoTilePool.Count; i++)
                if ((_photoPoolTiles[i] < firstVisible || _photoPoolTiles[i] > lastVisible) && _photoTilePool[i].IsVisible)
                    _photoTilePool[i].IsVisible = false;
        }

        private void UpdatePhotoLayoutMetrics()
        {
            if (page.ClipPreviewsSuspendedForZoom || _photoTileContainer is null || _photoImageWidth <= 0)
                return;

            double contentWidth = ContentWidth;
            int count = GetPreviewTileCount(contentWidth, _photoImageWidth);
            if (count == _photoCountOfTiles && Math.Abs(contentWidth - _lastContentWidth) < 0.1)
                return;

            _lastContentWidth = contentWidth;
            _photoCountOfTiles = count;
            for (int i = 0; i < _photoTilePool.Count; i++)
                if (_photoPoolTiles[i] >= count) _photoTilePool[i].IsVisible = false;
            _lastFirst = -1;
            _lastLast = -1;
        }

        private void PreviewRoot_Loaded(object? sender, EventArgs e)
        {
            if (_disposed || page.ClipPreviewsSuspendedForZoom)
                return;

            _lastFirst = -1;
            _lastLast = -1;
            page.ScheduleClipPreviewViewportUpdate();
        }

        private void EnsurePhotoTilePoolSize(int requiredCount)
        {
            if (_photoTileContainer is null || _photoImageSource is null)
                return;

            int target = Math.Min(_photoCountOfTiles, requiredCount + PoolSpareTiles);
            bool resized = false;
            if (_photoTilePool.Count > target + PoolShrinkThreshold)
            {
                while (_photoTilePool.Count > target)
                {
                    _photoTilePool[^1].Source = null;
                    _photoTileContainer.Children.Remove(_photoTilePool[^1]);
                    _photoTilePool.RemoveAt(_photoTilePool.Count - 1);
                    _photoPoolTiles.RemoveAt(_photoPoolTiles.Count - 1);
                }
                resized = true;
            }
            if (_photoTilePool.Count < requiredCount)
            {
                while (_photoTilePool.Count < target)
                {
                    var image = new Image
                    {
                        Aspect = Aspect.AspectFit,
                        HeightRequest = _photoThumbHeight,
                        WidthRequest = _photoImageWidth,
                        InputTransparent = true,
                        IsVisible = false,
                    };
                    _photoTilePool.Add(image);
                    _photoPoolTiles.Add(-1);
                    _photoTileContainer.Children.Add(image);
                }
                resized = true;
            }
            if (resized)
                for (int i = 0; i < _photoPoolTiles.Count; i++) _photoPoolTiles[i] = -1;
        }

        // ════════════════════════════════════════════════════════════════════
        //  Cleanup
        // ════════════════════════════════════════════════════════════════════

        public void Suspend()
        {
            _videoFrameCancellation?.Cancel();
            _videoFrameCancellation = null;
            _videoRequestedFrames.Clear();
            if (_metadataLoading)
            {
                _metadataLoading = false;
                _metadataCancellation?.Cancel();
                _metadataCancellation?.Dispose();
                _metadataCancellation = null;
            }
            _metadataMissing = false;
            if (_videoFramePool.Count == 0 && _photoTilePool.Count == 0) return;
            foreach (var image in _videoFramePool)
                image.Source = null;
            foreach (var image in _photoTilePool)
                image.Source = null;
            _videoFrameContainer?.Children.Clear();
            _photoTileContainer?.Children.Clear();
            _videoFramePool.Clear();
            _videoPoolFrames.Clear();
            _videoPoolTiles.Clear();
            _videoFrameSources.Clear();
            _photoTilePool.Clear();
            _photoPoolTiles.Clear();
            _lastFirst = -1;
            _lastLast = -1;
        }

        public void Dispose()
        {
            if (_disposed) return;
            Suspend();
            _disposed = true;
            _metadataCancellation?.Cancel();
            _metadataCancellation?.Dispose();
            _metadataCancellation = null;
            if (_previewRoot is not null)
            {
                _previewRoot.Loaded -= PreviewRoot_Loaded;
                _previewRoot.SizeChanged -= PreviewRoot_Loaded;
            }
            _videoAvailableFrames = null;
            _videoFramePool.Clear();
            _videoPoolFrames.Clear();
            _videoPoolTiles.Clear();
            _videoFrameSources.Clear();
            _photoImageSource = null;
            _photoTilePool.Clear();
            _photoPoolTiles.Clear();
            _photoTileContainer = null;
            _videoFrameContainer = null;
            _previewRoot = null;
        }
    }
}
