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

        private const int PreviewWidthFactor = 10;

        // ── Video preview state ────────────────────────────────────────────

        private string? _videoThumbDir;
        private int _videoFrameWidth;
        private double _videoPreviewHeight;
        private int _videoCountOfFrame;
        private double _videoActualSpacing;
        private List<int>? _videoFrameToShow;
        private double _videoClipWidth;

        // ── Photo preview state ────────────────────────────────────────────

        private ImageSource? _photoImageSource;
        private int _photoImageWidth;
        private double _photoThumbHeight;
        private int _photoCountOfTiles;
        private AbsoluteLayout? _photoTileContainer;
        private readonly List<Image> _photoTilePool = [];

        // ── Shared scroll-aware container ──────────────────────────────────

        private HorizontalStackLayout? _frameContainer;
        private bool _disposed;
        private int _lastFirst = -1;
        private int _lastLast = -1;

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
                : _frameContainer;
            if (previewContainer?.Parent is null)
                return false;

            if (clip.ClipType is ClipMode.VideoClip)
                UpdateVideoVisibleFrames(scrollX, viewportWidth);
            else
                UpdatePhotoVisibleTiles(scrollX, viewportWidth);

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

            var pngs = GetNumericFrameFiles(thumbDir);
            if (pngs.Count == 0 && !string.Equals(thumbDir, legacyThumbDir, StringComparison.OrdinalIgnoreCase))
            {
                // Compatibility with projects created before timeline/dynamic caches
                // were separated. Dynamic_* files are deliberately filtered out.
                thumbDir = legacyThumbDir;
                pngs = GetNumericFrameFiles(thumbDir);
            }
            if (pngs.Count == 0)
                return null;
            var availableFrames = pngs.Keys.Order().ToList();

            (var origWidth, var origHeight) = new Picture8bpp(pngs.Values.First()).GetDimensions();

            var rawClipHeight = clip.Clip.HeightRequest > 0
                ? clip.Clip.HeightRequest
                : (clip.Clip.Height > 0 ? clip.Clip.Height : DraftPage.ClipHeight);
            var previewHeight = rawClipHeight;

            var scaleFactor = previewHeight / (double)origHeight;
            var frameWidth = Math.Max(1, (int)Math.Round(origWidth * scaleFactor));

            var clipWidth = clip.Clip.WidthRequest > 0
                ? clip.Clip.WidthRequest
                : (clip.origLength > 0 ? clip.origLength : clip.Clip.Width);
            var availableWidth = Math.Max(1, clipWidth - 60);
            var countOfFrame = (int)(availableWidth / frameWidth) - 1;
            if (countOfFrame <= 0) return null;
            if (Math.Abs((countOfFrame + 1f) * frameWidth - availableWidth) < frameWidth * 0.75f) countOfFrame++;
            var totalFramesWidth = countOfFrame * frameWidth;
            var spacing = countOfFrame > 1 ? (availableWidth - totalFramesWidth) / (countOfFrame - 1) : 0;

            List<int> frameToShow = new(countOfFrame);
            for (int i = 0; i < countOfFrame; i++)
            {
                var idx = countOfFrame > 1
                    ? (int)Math.Floor(i * (availableFrames.Count - 1) / (double)(countOfFrame - 1))
                    : 0;
                frameToShow.Add(availableFrames[idx]);
            }

            // Cache parameters for scroll-aware updates
            _videoThumbDir = thumbDir;
            _videoFrameWidth = frameWidth;
            _videoPreviewHeight = previewHeight;
            _videoCountOfFrame = countOfFrame;
            _videoActualSpacing = spacing / 2;
            _videoFrameToShow = frameToShow;
            _videoClipWidth = clipWidth;

            _frameContainer = new HorizontalStackLayout
            {
                HeightRequest = previewHeight,
                InputTransparent = true,
                IsClippedToBounds = true,
                VerticalOptions = LayoutOptions.Fill,
                HorizontalOptions = LayoutOptions.Fill,
                Spacing = _videoActualSpacing,
                Padding = 0
            };

            // Query initial scroll state via the page's scroll state helper
            var (initScrollX, initVpW) = page.GetTimelineScrollState();
            UpdateVideoVisibleFrames(initScrollX, initVpW);

            return new Grid
            {
                HeightRequest = previewHeight,
                VerticalOptions = LayoutOptions.Fill,
                Padding = 0,
                Children =
                {
                    _frameContainer,
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
            if (_frameContainer is null || _videoThumbDir is null || _videoFrameToShow is null)
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
                if (_frameContainer.Children.Count > 0)
                    _frameContainer.Children.Clear();
                _lastFirst = -1;
                _lastLast = -1;
                return;
            }

            // Visible range within the content area
            double visibleLeftInContent = Math.Max(0, viewportLeft - contentStart);
            double visibleRightInContent = Math.Min(contentWidth, viewportRight - contentStart);

            double step = _videoFrameWidth + _videoActualSpacing;
            if (step <= 0) return;

            int firstVisible = Math.Max(0, (int)(visibleLeftInContent / step) - 2); // buffer 2 frames
            int lastVisible = Math.Min(_videoCountOfFrame - 1,
                (int)Math.Ceiling(visibleRightInContent / step) + 2);

            if (_lastFirst == firstVisible && _lastLast == lastVisible)
                return; // no change

            _lastFirst = firstVisible;
            _lastLast = lastVisible;

            // Rebuild the visible frame set
            _frameContainer.Children.Clear();

            if (firstVisible > 0)
            {
                // Leading spacer to maintain correct frame alignment
                _frameContainer.Children.Add(new BoxView
                {
                    WidthRequest = firstVisible * step,
                    HeightRequest = 1,
                    Color = Colors.Transparent,
                    InputTransparent = true,
                });
            }

            for (int i = firstVisible; i <= lastVisible; i++)
            {
                if (i >= _videoFrameToShow.Count) break;
                var item = _videoFrameToShow[i];
                _frameContainer.Children.Add(new Border
                {
                    StrokeThickness = 1,
                    Padding = 0,
                    Content = new Image
                    {
                        Source = ImageSource.FromFile(Path.Combine(_videoThumbDir, $"{item}.png")),
                        InputTransparent = true,
                        VerticalOptions = LayoutOptions.Fill,
                        WidthRequest = _videoFrameWidth,
                        Aspect = Aspect.AspectFit,
                    },
                    Margin = new(0),
                });
            }
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
            var clipWidth = clip.Clip.WidthRequest > 0
                ? clip.Clip.WidthRequest
                : (clip.origLength > 0 ? clip.origLength : clip.Clip.Width);

            var (origWidth, origHeight) = new Picture8bpp(sourcePath).GetDimensions();
            var scaleFactor = thumbHeight / (double)origHeight;
            var imageWidth = Math.Max(1, (int)Math.Round(origWidth * scaleFactor));

            var availableWidth = Math.Max(1, clipWidth - 60);
            var countOfTiles = Math.Max(1, (int)(availableWidth / imageWidth) + 1);

            // Cache parameters for scroll-aware updates
            _photoImageSource = ImageSource.FromFile(sourcePath);
            _photoImageWidth = imageWidth;
            _photoThumbHeight = thumbHeight;
            _photoCountOfTiles = countOfTiles;

            // Keep a small, reusable pool of Image controls. Rebuilding the visible
            // children on every tile boundary makes long photo clips repeatedly load
            // the same bitmap and forces layout/GC work while the user is scrolling.
            _photoTileContainer = new AbsoluteLayout
            {
                HeightRequest = thumbHeight,
                WidthRequest = availableWidth,
                InputTransparent = true,
                VerticalOptions = LayoutOptions.Fill,
                HorizontalOptions = LayoutOptions.Start,
            };

            var (initScrollX, initVpW) = page.GetTimelineScrollState();
            UpdatePhotoVisibleTiles(initScrollX, initVpW);

            var container = new Grid
            {
                HeightRequest = thumbHeight,
                WidthRequest = availableWidth,
                InputTransparent = true,
                VerticalOptions = LayoutOptions.Center,
                HorizontalOptions = LayoutOptions.Fill,
                IsClippedToBounds = true,
            };

            container.Children.Add(_photoTileContainer);
            container.Children.Add(new Label
            {
                Text = clip.DisplayName ?? clip.Id.ToString(),
                HorizontalOptions = LayoutOptions.Center,
                VerticalOptions = LayoutOptions.Center,
                BackgroundColor = Color.FromRgba("#80808080"),
                MaxLines = 1,
            });

            return container;
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
            _videoFrameToShow = null;
            _photoImageSource = null;
            _photoTilePool.Clear();
            _photoTileContainer = null;
            _frameContainer = null;
        }
    }
}
