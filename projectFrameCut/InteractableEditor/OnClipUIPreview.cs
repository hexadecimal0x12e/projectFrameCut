using projectFrameCut.Render.RenderAPIBase.ClipAndTrack;
using projectFrameCut.DraftStuff;
using projectFrameCut.Shared;
using System;
using System.Collections.Generic;
using System.IO;
using Microsoft.Maui.Controls;
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
            double height = clip.Clip.HeightRequest > 0 ? clip.Clip.HeightRequest
                : clip.Clip.Height > 0 ? clip.Clip.Height : DraftPage.ClipHeight;
            if (_clipType == ClipMode.VideoClip && Math.Abs(_videoPreviewHeight - height) > 0.1
                || _clipType == ClipMode.PhotoClip && Math.Abs(_photoThumbHeight - Math.Max(28, clip.Clip.HeightRequest - 14)) > 0.1) return false;
            foreach (var label in _previewRoot.Children.OfType<Label>())
                label.Text = clip.DisplayName ?? clip.Id.ToString();
            _metadataMissing = false;
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
        private string? _videoLegacyThumbDir;
        private int _videoFrameWidth;
        private double _videoPreviewHeight;
        private int _videoCountOfFrame;
        private List<int>? _videoAvailableFrames;
        private AbsoluteLayout? _videoFrameContainer;
        private readonly List<Border> _videoFramePool = [];
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
        /// Global X of the content area (column 1) within the clip.
        /// The clip Border has a 3-column Grid: [handle 30px] [content *] [handle 30px].
        /// </summary>
        private double ContentGlobalStartX
        {
            get
            {
                double tx = clip.Clip.TranslationX;
                return double.IsNaN(tx) || double.IsInfinity(tx) ? 30 : tx + 30;
            }
        }

        /// <summary>
        /// Width of the content area inside the clip (clip width minus the two 30px handles).
        /// </summary>
        private double ContentWidth
        {
            get
            {
                double cw = clip.Clip.WidthRequest > 0
                    ? clip.Clip.WidthRequest
                    : (clip.origLength > 0 ? clip.origLength : Math.Max(60, clip.Clip.Width));
                return Math.Max(1, cw - 60);
            }
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
                if (AssetDatabase.Assets.TryGetValue(assetId, out var asset))
                {
                    thumbDir = Path.Combine(MauiProgram.DataPath, "My Assets", ".perAssetThumb", assetId);
                }
            }

            var rawClipHeight = clip.Clip.HeightRequest > 0
                ? clip.Clip.HeightRequest
                : (clip.Clip.Height > 0 ? clip.Clip.Height : DraftPage.ClipHeight);
            var previewHeight = rawClipHeight;

            _videoThumbDir = thumbDir;
            _videoLegacyThumbDir = legacyThumbDir;
            _videoPreviewHeight = previewHeight;

            _videoFrameContainer = new AbsoluteLayout
            {
                HeightRequest = previewHeight,
                WidthRequest = ContentWidth,
                InputTransparent = true,
                IsClippedToBounds = true,
                VerticalOptions = LayoutOptions.Fill,
                HorizontalOptions = LayoutOptions.Fill,
            };
            _previewRoot = new Grid
            {
                HeightRequest = previewHeight,
                WidthRequest = ContentWidth,
                HorizontalOptions = LayoutOptions.Fill,
                VerticalOptions = LayoutOptions.Fill,
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
                        MaxLines = 1
                    }
                }
            };
            _previewRoot.Loaded += PreviewRoot_Loaded;

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
                        _photoImageSource = ImageSource.FromFile(metadata.Path);
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

        private void UpdateVideoVisibleFrames(double scrollX, double viewportWidth)
        {
            if (_videoFrameContainer is null || _videoThumbDir is null || _videoAvailableFrames is null)
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

            bool refreshFrames = _lastFirst < 0;
            _lastFirst = firstVisible;
            _lastLast = lastVisible;

            int visibleCount = lastVisible - firstVisible + 1;
            EnsureVideoFramePoolSize(visibleCount);

            // Keep each tile on the same view until it leaves the pooled range.
            for (int tileIndex = firstVisible; tileIndex <= lastVisible; tileIndex++)
            {
                int poolIndex = tileIndex % _videoFramePool.Count;
                bool moved = _videoPoolTiles[poolIndex] != tileIndex;
                if (moved || refreshFrames)
                {
                    int frame = GetVideoFrameForTile(tileIndex);
                    if (_videoPoolFrames[poolIndex] != frame)
                    {
                        _videoPoolFrames[poolIndex] = frame;
                        if (!_videoFrameSources.TryGetValue(frame, out var source))
                        {
                            source = ImageSource.FromFile(Path.Combine(_videoThumbDir, $"{frame}.png"));
                            _videoFrameSources[frame] = source;
                        }
                        ((Image)_videoFramePool[poolIndex].Content!).Source = source;
                    }
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
        }

        private void UpdateVideoLayoutMetrics()
        {
            if (_videoFrameContainer is null || _videoFrameWidth <= 0)
                return;

            double contentWidth = ContentWidth;
            if (Math.Abs(contentWidth - _lastContentWidth) < 0.1)
                return;

            _lastContentWidth = contentWidth;
            _videoCountOfFrame = Math.Max(1, (int)Math.Ceiling(contentWidth / _videoFrameWidth));
            _videoFrameContainer.WidthRequest = contentWidth;
            if (_previewRoot is not null)
                _previewRoot.WidthRequest = contentWidth;
            _lastFirst = -1;
            _lastLast = -1;
        }

        private int GetVideoFrameForTile(int tileIndex)
        {
            if (_videoAvailableFrames is not { Count: > 0 })
                return 0;

            double progress = Math.Clamp(tileIndex * _videoFrameWidth / ContentWidth, 0, 1);
            uint duration = page.GetClipFrameRange(clip).Duration;
            uint offset = (uint)Math.Round(progress * Math.Max(0, (long)duration - 1));
            uint sourceOffset = ClipTiming.SourceOffset(offset, clip.lengthInFrame, clip.SpeedProvider, duration);
            double sourceFrame = (ulong)clip.relativeStartFrame + sourceOffset;
            if (page.ProjectInfo.TargetFrameRate > 0 && float.IsFinite(clip.sourceSecondPerFrame) && clip.sourceSecondPerFrame > 0)
                sourceFrame /= page.ProjectInfo.TargetFrameRate * (double)clip.sourceSecondPerFrame;
            int target = (int)Math.Min(int.MaxValue, Math.Floor(sourceFrame + 1e-9));
            int index = _videoAvailableFrames.BinarySearch(target);
            if (index >= 0)
                return _videoAvailableFrames[index];

            index = ~index;
            if (index == 0)
                return _videoAvailableFrames[0];
            if (index >= _videoAvailableFrames.Count)
                return _videoAvailableFrames[^1];

            int before = _videoAvailableFrames[index - 1];
            int after = _videoAvailableFrames[index];
            return target - before <= after - target ? before : after;
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
                    ((Image)_videoFramePool[^1].Content!).Source = null;
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
                    var border = new Border
                    {
                        StrokeThickness = 1,
                        Padding = 0,
                        Margin = new(0),
                        InputTransparent = true,
                        IsVisible = false,
                        Content = new Image
                        {
                            InputTransparent = true,
                            Aspect = Aspect.AspectFit,
                        },
                    };
                    _videoFramePool.Add(border);
                    _videoPoolFrames.Add(-1);
                    _videoPoolTiles.Add(-1);
                    _videoFrameContainer.Children.Add(border);
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
                WidthRequest = ContentWidth,
                InputTransparent = true,
                VerticalOptions = LayoutOptions.Fill,
                HorizontalOptions = LayoutOptions.Fill,
            };
            _previewRoot = new Grid
            {
                HeightRequest = thumbHeight,
                WidthRequest = ContentWidth,
                InputTransparent = true,
                VerticalOptions = LayoutOptions.Center,
                HorizontalOptions = LayoutOptions.Fill,
                IsClippedToBounds = true,
            };

            _previewRoot.Children.Add(_photoTileContainer);
            _previewRoot.Children.Add(new Label
            {
                Text = clip.DisplayName ?? clip.Id.ToString(),
                HorizontalOptions = LayoutOptions.Center,
                VerticalOptions = LayoutOptions.Center,
                BackgroundColor = Color.FromRgba("#80808080"),
                MaxLines = 1,
            });
            _previewRoot.Loaded += PreviewRoot_Loaded;

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

            int visibleCount = lastVisible - firstVisible + 1;
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
            if (_photoTileContainer is null || _photoImageWidth <= 0)
                return;

            double contentWidth = ContentWidth;
            if (Math.Abs(contentWidth - _lastContentWidth) < 0.1)
                return;

            _lastContentWidth = contentWidth;
            _photoCountOfTiles = Math.Max(1, (int)Math.Ceiling(contentWidth / _photoImageWidth));
            _photoTileContainer.WidthRequest = contentWidth;
            if (_previewRoot is not null)
                _previewRoot.WidthRequest = contentWidth;
            _lastFirst = -1;
            _lastLast = -1;
        }

        private void PreviewRoot_Loaded(object? sender, EventArgs e)
        {
            if (_disposed)
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
            if (_metadataLoading)
            {
                _metadataLoading = false;
                _metadataCancellation?.Cancel();
                _metadataCancellation?.Dispose();
                _metadataCancellation = null;
            }
            _metadataMissing = false;
            if (_videoFramePool.Count == 0 && _photoTilePool.Count == 0) return;
            foreach (var border in _videoFramePool)
                ((Image)border.Content!).Source = null;
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
                _previewRoot.Loaded -= PreviewRoot_Loaded;
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
