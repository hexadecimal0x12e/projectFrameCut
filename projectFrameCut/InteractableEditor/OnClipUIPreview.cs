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
using projectFrameCut.Drawing.Base.Picture;
using projectFrameCut.Drawing.Base;

namespace projectFrameCut.InteractableEditor
{
    /// <summary>
    /// Generates per-clip timeline previews with viewport-aware rendering.
    /// Only creates frame/tile views for the portion of the clip that is currently
    /// visible within the timeline's horizontal ScrollView, and updates dynamically
    /// as the user scrolls. This avoids the extreme layout cost of instantiating
    /// hundreds of Image+Border elements for long clips all at once.
    ///
    /// Callers must call <see cref="NotifyScrollChanged"/> when the timeline scrolls
    /// so the preview can update which frames are visible. The owning <see cref="DraftPage"/>
    /// does this from its <c>TimelineScrollView_Scrolled</c> handler.
    /// </summary>
    public sealed class OnClipUIPreview(DraftPage page, ClipElementUI clip) : IDisposable
    {
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
        private int _videoFrameWidth;
        private double _videoPreviewHeight;
        private int _videoCountOfFrame;
        private List<int>? _videoAvailableFrames;
        private AbsoluteLayout? _videoFrameContainer;
        private readonly List<Border> _videoFramePool = [];
        private readonly List<int> _videoPoolFrames = [];
        private readonly Dictionary<int, ImageSource> _videoFrameSources = [];

        // ── Photo preview state ────────────────────────────────────────────

        private ImageSource? _photoImageSource;
        private int _photoImageWidth;
        private double _photoThumbHeight;
        private int _photoCountOfTiles;
        private AbsoluteLayout? _photoTileContainer;
        private readonly List<Image> _photoTilePool = [];

        // ── Shared scroll-aware container ──────────────────────────────────

        private bool _disposed;
        private int _lastFirst = -1;
        private int _lastLast = -1;
        private double _lastContentWidth = -1;
        private Grid? _previewRoot;

        /// <summary>
        /// Called by <see cref="DraftPage"/> from <c>TimelineScrollView_Scrolled</c>
        /// so the preview can refresh its visible frame/tile range without needing
        /// direct access to the private ScrollView.
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
            var legacyThumbDir = Path.Combine(page.WorkingPath, "thumbs", "perClip", clipId.ToString());
            var thumbDir = Path.Combine(legacyThumbDir, "timeline");
            if (clip.SourcePath?.StartsWith('$') ?? false)
            {
                var assetId = clip.SourcePath[1..];
                if (AssetDatabase.Assets.TryGetValue(assetId, out var asset))
                {
                    thumbDir = Path.Combine(MauiProgram.DataPath, "My Assets", ".perAssetThumb", assetId);
                }
            }

            var frames = GetNumericFrameFiles(thumbDir);
            if (frames.Count == 0 && !string.Equals(thumbDir, legacyThumbDir, StringComparison.OrdinalIgnoreCase))
            {
                // Compatibility with projects created before timeline/dynamic caches
                // were separated. Dynamic_* files are deliberately filtered out.
                thumbDir = legacyThumbDir;
                frames = GetNumericFrameFiles(thumbDir);
            }
            if (frames.Count == 0)
                return null;
            var availableFrames = frames.Keys.Order().ToList();

            (var origWidth, var origHeight) = new Picture8bpp(frames.Values.First()).GetDimensions();

            var rawClipHeight = clip.Clip.HeightRequest > 0
                ? clip.Clip.HeightRequest
                : (clip.Clip.Height > 0 ? clip.Clip.Height : DraftPage.ClipHeight);
            var previewHeight = rawClipHeight;

            var scaleFactor = previewHeight / (double)origHeight;
            var frameWidth = Math.Max(1, (int)Math.Round(origWidth * scaleFactor));

            _videoThumbDir = thumbDir;
            _videoFrameWidth = frameWidth;
            _videoPreviewHeight = previewHeight;
            _videoAvailableFrames = availableFrames;

            _videoFrameContainer = new AbsoluteLayout
            {
                HeightRequest = previewHeight,
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
            UpdateVideoLayoutMetrics();

            var (initScrollX, initVpW) = page.GetTimelineScrollState();
            UpdateVideoVisibleFrames(initScrollX, initVpW);
            return _previewRoot;
        }

        private static Dictionary<int, string> GetNumericFrameFiles(string directory)
        {
            if (!Directory.Exists(directory)) return [];

            var result = new Dictionary<int, string>();
            foreach (var path in Directory.EnumerateFiles(directory, "*.png", SearchOption.TopDirectoryOnly))
            {
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
            {
                SetVideoFramePoolVisibleCount(0);
                _lastFirst = -1;
                _lastLast = -1;
                return;
            }

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

            _lastFirst = firstVisible;
            _lastLast = lastVisible;

            int visibleCount = lastVisible - firstVisible + 1;
            EnsureVideoFramePoolSize(visibleCount);
            SetVideoFramePoolVisibleCount(visibleCount);

            for (int poolIndex = 0; poolIndex < visibleCount; poolIndex++)
            {
                int tileIndex = firstVisible + poolIndex;
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

                AbsoluteLayout.SetLayoutBounds(_videoFramePool[poolIndex],
                    new Rect(tileIndex * step, 0, _videoFrameWidth, _videoPreviewHeight));
            }
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
            double ratio = clip.SecondPerFrameRatio;
            if (!double.IsFinite(ratio) || ratio <= 0)
                ratio = 1;

            double clipWidth = ContentWidth + 60;
            uint duration = Math.Max(1u, page.PixelToFrame(clipWidth));
            double sourceDuration = duration / ratio;
            if (clip.maxFrameCount > clip.relativeStartFrame)
                sourceDuration = Math.Min(sourceDuration, clip.maxFrameCount - clip.relativeStartFrame);

            int target = (int)Math.Round(clip.relativeStartFrame + progress * Math.Max(0, sourceDuration - 1));
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

            while (_videoFramePool.Count < requiredCount)
            {
                var border = new Border
                {
                    StrokeThickness = 1,
                    Padding = 0,
                    Margin = new(0),
                    InputTransparent = true,
                    Content = new Image
                    {
                        InputTransparent = true,
                        Aspect = Aspect.AspectFit,
                    },
                };
                _videoFramePool.Add(border);
                _videoPoolFrames.Add(-1);
                _videoFrameContainer.Children.Add(border);
            }
        }

        private void SetVideoFramePoolVisibleCount(int visibleCount)
        {
            for (int i = 0; i < _videoFramePool.Count; i++)
                _videoFramePool[i].IsVisible = i < visibleCount;
        }

        // ════════════════════════════════════════════════════════════════════
        //  Photo preview
        // ════════════════════════════════════════════════════════════════════

        private View? BuildPhotoPreview()
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

            if (!File.Exists(sourcePath))
                return null;

            var thumbHeight = Math.Max(28, clip.Clip.HeightRequest - 14);
            var (origWidth, origHeight) = new Picture8bpp(sourcePath).GetDimensions();
            var scaleFactor = thumbHeight / (double)origHeight;
            var imageWidth = Math.Max(1, (int)Math.Round(origWidth * scaleFactor));

            _photoImageSource = ImageSource.FromFile(sourcePath);
            _photoImageWidth = imageWidth;
            _photoThumbHeight = thumbHeight;

            // Keep a small, reusable pool of Image controls. Rebuilding the visible
            // children on every tile boundary makes long photo clips repeatedly load
            // the same bitmap and forces layout/GC work while the user is scrolling.
            _photoTileContainer = new AbsoluteLayout
            {
                HeightRequest = thumbHeight,
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
            UpdatePhotoLayoutMetrics();

            var (initScrollX, initVpW) = page.GetTimelineScrollState();
            UpdatePhotoVisibleTiles(initScrollX, initVpW);
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
            {
                SetPhotoTilePoolVisibleCount(0);
                _lastFirst = -1;
                _lastLast = -1;
                return;
            }

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
            SetPhotoTilePoolVisibleCount(visibleCount);

            for (int poolIndex = 0; poolIndex < visibleCount; poolIndex++)
            {
                int tileIndex = firstVisible + poolIndex;
                AbsoluteLayout.SetLayoutBounds(
                    _photoTilePool[poolIndex],
                    new Rect(tileIndex * step, 0, _photoImageWidth, _photoThumbHeight));
            }
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
            var (scrollX, viewportWidth) = page.GetTimelineScrollState();
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
        }

        private void EnsurePhotoTilePoolSize(int requiredCount)
        {
            if (_photoTileContainer is null || _photoImageSource is null)
                return;

            while (_photoTilePool.Count < requiredCount)
            {
                var image = new Image
                {
                    Source = _photoImageSource,
                    Aspect = Aspect.AspectFit,
                    HeightRequest = _photoThumbHeight,
                    WidthRequest = _photoImageWidth,
                    InputTransparent = true,
                };
                _photoTilePool.Add(image);
                _photoTileContainer.Children.Add(image);
            }
        }

        private void SetPhotoTilePoolVisibleCount(int visibleCount)
        {
            for (int i = 0; i < _photoTilePool.Count; i++)
                _photoTilePool[i].IsVisible = i < visibleCount;
        }

        // ════════════════════════════════════════════════════════════════════
        //  Cleanup
        // ════════════════════════════════════════════════════════════════════

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            if (_previewRoot is not null)
                _previewRoot.Loaded -= PreviewRoot_Loaded;
            _videoAvailableFrames = null;
            _videoFramePool.Clear();
            _videoPoolFrames.Clear();
            _videoFrameSources.Clear();
            _photoImageSource = null;
            _photoTilePool.Clear();
            _photoTileContainer = null;
            _videoFrameContainer = null;
            _previewRoot = null;
        }
    }
}
