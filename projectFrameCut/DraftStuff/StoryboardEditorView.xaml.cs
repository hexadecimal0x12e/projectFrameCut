using projectFrameCut.Render.ClipsAndTracks;
using projectFrameCut.ViewModels;
using Color = Microsoft.Maui.Graphics.Color;
using PointF = Microsoft.Maui.Graphics.PointF;
using RectF = Microsoft.Maui.Graphics.RectF;

namespace projectFrameCut.DraftStuff;

public partial class StoryboardEditorView : ContentView
{
    private StoryboardEditorViewModel? _viewModel;
    private TimelineDrawable _timelineDrawable = null!;

    // ── Constructors ──────────────────────────────────────

    /// <summary>Parameterless constructor required by XAML parser.</summary>
    public StoryboardEditorView()
    {
        InitializeComponent();
    }

    /// <summary>
    /// Create the editor for the given <paramref name="clip"/>.
    /// Works for both SVG-backed and composition-only clips.
    /// </summary>
    public StoryboardEditorView(VectorCanvasClip clip, int projectWidth, int projectHeight) : this()
    {
        _viewModel = new StoryboardEditorViewModel(clip, Dispatcher,
            pickSvgFile: OpenSvgFilePickerAsync)
        {
            PreviewWidth = projectWidth,
            PreviewHeight = projectHeight,
        };
        BindingContext = _viewModel;

        // Set up timeline rendering
        _timelineDrawable = new TimelineDrawable { ViewModel = _viewModel };
        TimelineCanvas.Drawable = _timelineDrawable;

        // Wire up timeline invalidation
        _viewModel.RegisterTimelineInvalidate(() =>
        {
            MainThread.BeginInvokeOnMainThread(() => TimelineCanvas.Invalidate());
        });

        // Subscribe to Apply/Cancel events
        _viewModel.ChangesApplied += OnChangesApplied;
        _viewModel.ChangesCancelled += OnChangesCancelled;

        // Invalidate timeline when selected component/track/progress changes
        _viewModel.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName is nameof(StoryboardEditorViewModel.CurrentProgress)
                or nameof(StoryboardEditorViewModel.SelectedTrack)
                or nameof(StoryboardEditorViewModel.SelectedComponent))
            {
                TimelineCanvas.Invalidate();
            }
        };
    }

    // ── Public events for host page ───────────────────────

    /// <summary>Raised when the user applies storyboard changes.</summary>
    public event Action<Dictionary<string, object>?>? ChangesApplied;

    /// <summary>Raised when the user cancels editing.</summary>
    public event EventHandler? ChangesCancelled;

    private void OnChangesApplied(Dictionary<string, object>? e)
    {
        ChangesApplied?.Invoke(e);
    }

    private void OnChangesCancelled(object? sender, EventArgs e)
    {
        ChangesCancelled?.Invoke(this, EventArgs.Empty);
    }

    // ── SVG file picker ───────────────────────────────────

    private async Task<string?> OpenSvgFilePickerAsync()
    {
        try
        {
            var customFileType = new FilePickerFileType(
                new Dictionary<DevicePlatform, IEnumerable<string>>
                {
                    { DevicePlatform.WinUI, new[] { ".svg" } },
                    { DevicePlatform.Android, new[] { "image/svg+xml" } },
                    { DevicePlatform.iOS, new[] { "public.svg-image" } },
                    { DevicePlatform.macOS, new[] { "public.svg-image" } },
                });

            var result = await FilePicker.PickAsync(new PickOptions
            {
                PickerTitle = "Select SVG file to import",
                FileTypes = customFileType,
            });

            return result?.FullPath;
        }
        catch (Exception ex)
        {
            Log(ex, "SVG file picker failed", this);
            return null;
        }
    }

    // ── Timeline tap gesture ──────────────────────────────

    /// <summary>
    /// When the user taps the timeline, first check if a keyframe dot was hit.
    /// If so, select it for editing. Otherwise, add a new keyframe at that time
    /// for the currently selected track.
    /// </summary>
    private void OnTimelineTapped(object? sender, TappedEventArgs e)
    {
        if (_viewModel is null) return;

        var pos = e.GetPosition(TimelineCanvas);
        if (pos is null) return;

        float tapX = (float)pos.Value.X;
        float tapY = (float)pos.Value.Y;

        // ── Try hit-test existing keyframe dots first ──
        var activeTracks = GetActiveTracksForHitTest();
        float timelineWidth = (float)Math.Max(1, TimelineCanvas.Width
            - TimelineDrawable.LeftMargin - TimelineDrawable.RightMargin);
        float contentTop = TimelineDrawable.RulerHeight + 4;
        const float hitRadius = 14f; // Generous hit area for touch

        for (int t = 0; t < activeTracks.Count; t++)
        {
            float trackCenterY = contentTop + t * TimelineDrawable.TrackRowHeight
                + TimelineDrawable.TrackRowHeight / 2f;

            if (Math.Abs(tapY - trackCenterY) > hitRadius)
                continue; // Tap not in this track row

            for (int k = 0; k < activeTracks[t].KeyFrames.Count; k++)
            {
                var kf = activeTracks[t].KeyFrames[k];
                float kfX = TimelineDrawable.LeftMargin
                    + Math.Clamp(kf.Time, 0f, 1f) * timelineWidth;

                float distX = Math.Abs(tapX - kfX);
                float distY = Math.Abs(tapY - trackCenterY);

                if (distX <= hitRadius && distY <= hitRadius)
                {
                    // Hit! Select this keyframe and its track
                    _viewModel.SelectedTrack = activeTracks[t];
                    _viewModel.SelectedKeyFrame = kf;
                    return;
                }
            }
        }

        // ── No keyframe hit — add a new one (if a track is selected) ──
        if (_viewModel.SelectedTrack is null) return;

        float time = (float)Math.Clamp(
            (tapX - TimelineDrawable.LeftMargin) / timelineWidth,
            0f, 1f);

        float value = _viewModel.SelectedTrack.Source.GetValue(time);
        _viewModel.SelectedTrack.AddKeyFrame(time, value);

        // Clear keyframe selection after adding (new VMs are created)
        _viewModel.SelectedKeyFrame = null;
    }

    /// <summary>
    /// Returns the currently visible tracks for hit-test purposes,
    /// matching the logic in <see cref="TimelineDrawable.GetActiveTracks"/>.
    /// </summary>
    private System.Collections.ObjectModel.ObservableCollection<AnimationTrackItemViewModel> GetActiveTracksForHitTest()
    {
        if (_viewModel?.SelectedComponent?.Tracks is { Count: > 0 } compTracks)
            return compTracks;
        return _viewModel?.Tracks ?? new();
    }
}

// ═══════════════════════════════════════════════════════════
// TimelineDrawable — custom IDrawable for the timeline view
// ═══════════════════════════════════════════════════════════

/// <summary>
/// Draws the keyframe timeline: time ruler, track lanes,
/// keyframe dots, connection curves, and the playhead.
/// Supports both SVG-element tracks and per-component tracks.
/// </summary>
public class TimelineDrawable : IDrawable
{
    public StoryboardEditorViewModel? ViewModel { get; set; }

    // Layout constants
    public const float LeftMargin = 56f;
    public const float RightMargin = 12f;
    public const float RulerHeight = 22f;
    public const float TrackRowHeight = 30f;
    private const float KeyFrameRadius = 6f;
    private const float PlayheadWidth = 2f;

    // Colors
    private static readonly Color RulerBg = Color.FromArgb("#1a1a1a");
    private static readonly Color RulerText = Color.FromArgb("#9CA3AF");
    private static readonly Color RulerTick = Color.FromArgb("#555555");
    private static readonly Color TrackBgEven = Color.FromArgb("#222222");
    private static readonly Color TrackBgOdd = Color.FromArgb("#282828");
    private static readonly Color TrackLabelColor = Color.FromArgb("#9CA3AF");
    private static readonly Color KeyFrameFill = Color.FromArgb("#FFD272");
    private static readonly Color KeyFrameFillSelected = Color.FromArgb("#4A9EFF");
    private static readonly Color KeyFrameStroke = Color.FromArgb("#B0893E");
    private static readonly Color KeyFrameStrokeSelected = Color.FromArgb("#2A6ECC");
    private static readonly Color ConnLineColor = Color.FromArgb("#888888");
    private static readonly Color ConnLineSelectedColor = Color.FromArgb("#4A9EFF");
    private static readonly Color PlayheadColor = Color.FromArgb("#FFD272");
    private static readonly Color GridLineColor = Color.FromArgb("#3a3a3a");

    public void Draw(ICanvas canvas, RectF dirtyRect)
    {
        if (ViewModel is null) return;

        canvas.Antialias = true;
        float width = dirtyRect.Width;
        float timelineWidth = Math.Max(1, width - LeftMargin - RightMargin);

        // 1. Draw time ruler background
        canvas.FillColor = RulerBg;
        canvas.FillRectangle(0, 0, width, RulerHeight);

        // Ruler ticks at 0%, 25%, 50%, 75%, 100%
        float[] tickPositions = { 0f, 0.25f, 0.5f, 0.75f, 1f };
        foreach (float t in tickPositions)
        {
            float x = LeftMargin + t * timelineWidth;
            canvas.StrokeColor = t == 0f || t == 1f ? RulerTick : GridLineColor;
            canvas.StrokeSize = 1;
            canvas.DrawLine(x, 0, x, RulerHeight);

            canvas.FontColor = RulerText;
            canvas.FontSize = 10;
            string label = $"{t * 100:F0}%";
            canvas.DrawString(label, x - 15, 2, 30, RulerHeight - 4,
                HorizontalAlignment.Center, VerticalAlignment.Center);
        }

        // 2. Draw grid lines extending below ruler
        foreach (float t in new[] { 0.25f, 0.5f, 0.75f })
        {
            float x = LeftMargin + t * timelineWidth;
            canvas.StrokeColor = GridLineColor.WithAlpha(0.3f);
            canvas.StrokeSize = 0.5f;
            canvas.DrawLine(x, RulerHeight, x, dirtyRect.Height);
        }

        // 3. Get active tracks (prefer component tracks if selected)
        var activeTracks = GetActiveTracks();
        float contentTop = RulerHeight + 4;

        if (activeTracks.Count == 0)
        {
            string msg = ViewModel.SelectedComponent is not null
                ? "No tracks — add one from the left panel."
                : "Select a component or SVG element, then add a track.";
            canvas.FontColor = Color.FromArgb("#555555");
            canvas.FontSize = 13;
            canvas.DrawString(msg,
                LeftMargin, contentTop + 20, timelineWidth, 30,
                HorizontalAlignment.Center, VerticalAlignment.Center);
            DrawPlayhead(canvas, 0, dirtyRect.Height, timelineWidth);
            return;
        }

        for (int i = 0; i < activeTracks.Count; i++)
        {
            float y = contentTop + i * TrackRowHeight;

            // Row background
            canvas.FillColor = i % 2 == 0 ? TrackBgEven : TrackBgOdd;
            canvas.FillRectangle(0, y, width, TrackRowHeight - 2);

            // Track label
            canvas.FontColor = TrackLabelColor;
            canvas.FontSize = 9;
            canvas.DrawString(activeTracks[i].DisplayName,
                3, y + 2, LeftMargin - 6, TrackRowHeight - 6,
                HorizontalAlignment.Left, VerticalAlignment.Center);

            // Keyframe connections and dots
            DrawTrackLane(canvas, activeTracks[i], y, timelineWidth,
                activeTracks[i] == ViewModel.SelectedTrack);
        }

        // 4. Draw playhead
        DrawPlayhead(canvas, contentTop, dirtyRect.Height - contentTop, timelineWidth);
    }

    /// <summary>
    /// Returns the currently active tracks: component tracks if a component
    /// is selected, otherwise the global SVG tracks.
    /// </summary>
    private System.Collections.ObjectModel.ObservableCollection<AnimationTrackItemViewModel> GetActiveTracks()
    {
        if (ViewModel?.SelectedComponent?.Tracks is { Count: > 0 } compTracks)
            return compTracks;
        return ViewModel?.Tracks ?? new();
    }

    private void DrawTrackLane(ICanvas canvas, AnimationTrackItemViewModel track,
        float y, float timelineWidth, bool isSelected)
    {
        var keyframes = track.KeyFrames;
        if (keyframes.Count == 0) return;

        float centerY = y + TrackRowHeight / 2f;
        var lineColor = isSelected ? ConnLineSelectedColor : ConnLineColor;
        var dotFill = isSelected ? KeyFrameFillSelected : KeyFrameFill;
        var dotStroke = isSelected ? KeyFrameStrokeSelected : KeyFrameStroke;

        // Collect dot positions
        var positions = new PointF[keyframes.Count];
        for (int i = 0; i < keyframes.Count; i++)
        {
            float x = LeftMargin + Math.Clamp(keyframes[i].Time, 0f, 1f) * timelineWidth;
            positions[i] = new PointF(x, centerY);
        }

        // Draw connecting line
        if (positions.Length >= 2)
        {
            canvas.StrokeColor = lineColor;
            canvas.StrokeSize = 1.5f;
            canvas.StrokeDashPattern = null;
            canvas.StrokeLineCap = LineCap.Round;

            var path = new PathF();
            path.MoveTo(positions[0].X, positions[0].Y);
            for (int i = 1; i < positions.Length; i++)
            {
                float midX = (positions[i - 1].X + positions[i].X) / 2f;
                path.CurveTo(
                    midX, positions[i - 1].Y,
                    midX, positions[i].Y,
                    positions[i].X, positions[i].Y);
            }
            canvas.DrawPath(path);
        }

        // Draw keyframe dots
        var selectedKf = ViewModel?.SelectedKeyFrame;
        for (int i = 0; i < positions.Length; i++)
        {
            bool isThisSelected = selectedKf is not null
                && selectedKf == keyframes[i];

            if (isThisSelected)
            {
                // White glow ring for the selected keyframe
                canvas.FillColor = Color.FromArgb("#FFFFFF").WithAlpha(0.3f);
                canvas.FillCircle(positions[i].X, positions[i].Y, KeyFrameRadius + 5f);
            }

            canvas.FillColor = dotStroke;
            canvas.FillCircle(positions[i].X, positions[i].Y, KeyFrameRadius + 1.5f);

            canvas.FillColor = dotFill;
            canvas.FillCircle(positions[i].X, positions[i].Y, KeyFrameRadius);

            if (i == keyframes.Count - 1)
            {
                canvas.StrokeColor = dotStroke;
                canvas.StrokeSize = 1.5f;
                canvas.DrawCircle(positions[i].X, positions[i].Y, KeyFrameRadius + 2f);
            }
            else if (isThisSelected)
            {
                // Extra ring for selected keyframe
                canvas.StrokeColor = Color.FromArgb("#FFFFFF").WithAlpha(0.7f);
                canvas.StrokeSize = 1.5f;
                canvas.DrawCircle(positions[i].X, positions[i].Y, KeyFrameRadius + 3f);
            }
        }
    }

    private void DrawPlayhead(ICanvas canvas, float top, float height, float timelineWidth)
    {
        if (ViewModel is null) return;

        float x = LeftMargin + Math.Clamp(ViewModel.CurrentProgress, 0f, 1f) * timelineWidth;

        canvas.StrokeColor = PlayheadColor;
        canvas.StrokeSize = PlayheadWidth;
        canvas.StrokeLineCap = LineCap.Butt;
        canvas.DrawLine(x, top + RulerHeight, x, top + height);

        float triSize = 5f;
        var triPath = new PathF();
        triPath.MoveTo(x - triSize, RulerHeight);
        triPath.LineTo(x + triSize, RulerHeight);
        triPath.LineTo(x, RulerHeight - triSize);
        triPath.Close();

        canvas.FillColor = PlayheadColor;
        canvas.FillPath(triPath);
    }
}
