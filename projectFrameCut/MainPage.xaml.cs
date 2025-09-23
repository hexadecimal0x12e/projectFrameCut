#if ANDROID
using Android;
using Android.Content.PM;
using AndroidX.Core.App;
using AndroidX.Core.Content;
#endif
using Microsoft.Maui.Controls;
using Microsoft.Maui.Graphics;
using Microsoft.Maui.Layouts;
using projectFrameCut.ViewModels;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Linq;

namespace projectFrameCut
{
    public partial class MainPage : ContentPage
    {
        private readonly TimelineViewModel _vm = new();
        private readonly Dictionary<ClipViewModel, Grid> _clipToView = new();
        private ClipViewModel? _selectedClip;
        private double _dragStartX;
        private double _dragStartSeconds;
        private bool _isResizingLeft;
        private bool _isResizingRight;
        private double _resizeInitialStart;
        private double _resizeInitialDuration;

        private const double TrackHeight = 48;
        private int _dragStartTrackIndex = -1;
        private int _currentDragTrackIndex = -1;

        private VerticalStackLayout? _tracksHeader;
        private VerticalStackLayout? _tracksPanel;
        private AbsoluteLayout? _ruler;
        private BoxView? _playhead;
        private Label? _status;
        private ScrollView? _timelineScroll;

        public MainPage()
        {
            InitializeComponent();
            BindingContext = _vm; // ensure binding for TimelineWidth

            _tracksHeader = this.FindByName<VerticalStackLayout>("TracksHeader");
            _tracksPanel = this.FindByName<VerticalStackLayout>("TracksPanel");
            _ruler = this.FindByName<AbsoluteLayout>("Ruler");
            _playhead = this.FindByName<BoxView>("Playhead");
            _status = this.FindByName<Label>("Status");
            _timelineScroll = this.FindByName<ScrollView>("TimelineScroll");

            _vm.AddTrack("Video 1");
            _vm.AddTrack("Video 2");

            RebuildTracksUI();
            UpdatePlayhead();
            BuildRuler();
        }

        private int GetTrackIndexOfClip(ClipViewModel clip)
        {
            for (int i = 0; i < _vm.Tracks.Count; i++)
            {
                if (_vm.Tracks[i].Clips.Contains(clip))
                    return i;
            }
            return -1;
        }

        private void RepositionClips()
        {
            foreach (var kv in _clipToView)
                UpdateClipLayout(kv.Value, kv.Key);
            UpdatePlayhead();
        }

        private void RebuildTracksUI()
        {
            if (_tracksHeader == null || _tracksPanel == null) return;
            _tracksHeader.Children.Clear();
            _tracksPanel.Children.Clear();
            _clipToView.Clear();

            foreach (var track in _vm.Tracks)
            {
                var header = new Grid { HeightRequest = 48, BackgroundColor = Colors.Gray };
                header.Add(new Label { Text = track.Name, TextColor = Colors.White, Padding = new Thickness(8, 0) });
                _tracksHeader.Children.Add(header);

                // Use AbsoluteLayout for precise positioning of clips
                var lane = new AbsoluteLayout { HeightRequest = TrackHeight, BackgroundColor = Color.FromArgb("#222") };
                // background tap to clear selection
                var tap = new TapGestureRecognizer();
                tap.Tapped += (s, e) => SelectClip(null);
                lane.GestureRecognizers.Add(tap);

                foreach (var clip in track.Clips)
                {
                    var view = CreateClipView(clip);
                    lane.Children.Add(view);
                    _clipToView[clip] = view;
                    UpdateClipLayout(view, clip);
                }

                _tracksPanel.Children.Add(lane);
            }

            BuildRuler();
            SyncRulerScroll();
        }

        private bool WouldCauseOverlapOnTrack(TrackViewModel track, ClipViewModel self, double start, double duration)
        {
            var end = start + duration;
            return GetOverlappingClips(track, self, start, end).Any();
        }

        private Grid CreateClipView(ClipViewModel clip)
        {
            var g = new Grid
            {
                BackgroundColor = Color.FromArgb("#4466AA"),
                HeightRequest = 44,
                Margin = new Thickness(0, 2),
                HorizontalOptions = LayoutOptions.Start,
            };

            // Content label
            var label = new Label { Text = clip.Name, TextColor = Colors.White, Padding = new Thickness(8, 0) };
            g.Add(label);

            // Center move overlay to avoid gesture competition with handles
            var centerOverlay = new BoxView
            {
                BackgroundColor = Colors.Transparent,
                HorizontalOptions = LayoutOptions.Fill,
                VerticalOptions = LayoutOptions.Fill,
                InputTransparent = false
            };
            g.Add(centerOverlay);

            AttachAndroidTouchBlocker(centerOverlay);

            UpdateClipLayout(g, clip);

            var tapOverlay = new TapGestureRecognizer();
            tapOverlay.Tapped += (s, e) => SelectClip(clip);
            centerOverlay.GestureRecognizers.Add(tapOverlay);

            // Move pan
            var movePan = new PanGestureRecognizer();
            movePan.PanUpdated += (s, e) =>
            {
                if (e.StatusType == GestureStatus.Started)
                {
                    BeginInteraction();
                    SelectClip(clip);
                    _dragStartX = e.TotalX;
                    _dragStartSeconds = clip.StartSeconds;
                    _dragStartTrackIndex = GetTrackIndexOfClip(clip);
                    _currentDragTrackIndex = _dragStartTrackIndex;
                }
                else if (e.StatusType == GestureStatus.Running && !_isResizingLeft && !_isResizingRight)
                {
                    // horizontal 
                    ApplyMove(clip, g, _dragStartSeconds, e.TotalX - _dragStartX);

                    // cross-track 
                    if (_tracksPanel != null && _vm.Tracks.Count > 0 && _dragStartTrackIndex >= 0)
                    {
                        int offsetTracks = (int)Math.Round(e.TotalY / TrackHeight);
                        int newIndex = Math.Clamp(_dragStartTrackIndex + offsetTracks, 0, _vm.Tracks.Count - 1);
                        if (newIndex != _currentDragTrackIndex)
                        {
                            var targetTrack = _vm.Tracks[newIndex];
                            var candidateStart = clip.StartSeconds;
                            var candidateDuration = clip.DurationSeconds;
                            if (!WouldCauseOverlapOnTrack(targetTrack, clip, candidateStart, candidateDuration))
                            {
                                // Move visual
                                if (g.Parent is AbsoluteLayout oldLane && newIndex < _tracksPanel.Children.Count && _tracksPanel.Children[newIndex] is AbsoluteLayout newLane)
                                {
                                    oldLane.Children.Remove(g);
                                    newLane.Children.Add(g);
                                }
                                // Update model
                                var oldTrack = _vm.Tracks[_currentDragTrackIndex];
                                if (oldTrack.Clips.Contains(clip)) oldTrack.Clips.Remove(clip);
                                if (!targetTrack.Clips.Contains(clip)) targetTrack.Clips.Add(clip);
                                _currentDragTrackIndex = newIndex;
                            }
                            else
                            {
                                // Do not move vertically because it would overlap; stay on current track
                            }
                        }
                    }
                }
                else if (e.StatusType == GestureStatus.Completed || e.StatusType == GestureStatus.Canceled)
                {
                    EndInteraction();
                    _isResizingLeft = _isResizingRight = false;
                    UpdateClipLayout(g, clip);
                    SelectClip(clip);
                }
            };
            centerOverlay.GestureRecognizers.Add(movePan);

            // Resize handles
            var leftHandle = new BoxView { WidthRequest = 14, HorizontalOptions = LayoutOptions.Start, VerticalOptions = LayoutOptions.Fill, BackgroundColor = Color.FromArgb("#55FFFFFF") };
            var rightHandle = new BoxView { WidthRequest = 14, HorizontalOptions = LayoutOptions.End, VerticalOptions = LayoutOptions.Fill, BackgroundColor = Color.FromArgb("#55FFFFFF") };
            g.Add(leftHandle);
            g.Add(rightHandle);

            // Touch blockers for handles as well
            AttachAndroidTouchBlocker(leftHandle);
            AttachAndroidTouchBlocker(rightHandle);

            var leftPan = new PanGestureRecognizer();
            leftPan.PanUpdated += (s, e) =>
            {
                if (e.StatusType == GestureStatus.Started)
                {
                    BeginInteraction();
                    SelectClip(clip);
                    _isResizingLeft = true;
                    _dragStartX = e.TotalX;
                    _resizeInitialStart = clip.StartSeconds;
                    _resizeInitialDuration = clip.DurationSeconds;
                }
                else if (e.StatusType == GestureStatus.Running && _isResizingLeft)
                {
                    ApplyResizeLeft(clip, g, _resizeInitialStart, _resizeInitialDuration, e.TotalX - _dragStartX);
                }
                else if (e.StatusType == GestureStatus.Completed || e.StatusType == GestureStatus.Canceled)
                {
                    EndInteraction();
                    _isResizingLeft = false;
                    UpdateClipLayout(g, clip);
                }
            };
            leftHandle.GestureRecognizers.Add(leftPan);

            var rightPan = new PanGestureRecognizer();
            rightPan.PanUpdated += (s, e) =>
            {
                if (e.StatusType == GestureStatus.Started)
                {
                    BeginInteraction();
                    SelectClip(clip);
                    _isResizingRight = true;
                    _dragStartX = e.TotalX;
                    _resizeInitialDuration = clip.DurationSeconds;
                }
                else if (e.StatusType == GestureStatus.Running && _isResizingRight)
                {
                    ApplyResizeRight(clip, g, _resizeInitialDuration, e.TotalX - _dragStartX);
                }
                else if (e.StatusType == GestureStatus.Completed || e.StatusType == GestureStatus.Canceled)
                {
                    EndInteraction();
                    _isResizingRight = false;
                    UpdateClipLayout(g, clip);
                }
            };
            rightHandle.GestureRecognizers.Add(rightPan);

            // Keep tap on the grid too for safety (Windows)
            var tapGrid = new TapGestureRecognizer();
            tapGrid.Tapped += (s, e) => SelectClip(clip);
            g.GestureRecognizers.Add(tapGrid);

            return g;
        }

        private void UpdateClipLayout(Grid g, ClipViewModel clip)
        {
            var left = clip.StartSeconds * _vm.PixelsPerSecond;
            var width = clip.DurationSeconds * _vm.PixelsPerSecond;

            if (g.Parent is AbsoluteLayout)
            {
                AbsoluteLayout.SetLayoutBounds(g, new Rect(left, 2, width, 44));
                AbsoluteLayout.SetLayoutFlags(g, AbsoluteLayoutFlags.None);
            }
            else
            {
                g.TranslationX = left;
                g.WidthRequest = width;
            }
        }

        private void SelectClip(ClipViewModel? clip)
        {
            if (_selectedClip == clip) return;
            _selectedClip = clip;

            foreach (var kv in _clipToView)
                kv.Value.BackgroundColor = kv.Key == clip ? Color.FromArgb("#66AA44") : Color.FromArgb("#4466AA");

            if (_status != null)
                _status.Text = clip == null ? "No selection" : $"Selected: {clip.Name} ({clip.StartSeconds:0.00}s - {clip.EndSeconds:0.00}s)";
        }

        private void BuildRuler()
        {
            if (_ruler == null) return;
            _ruler.Children.Clear();
            var totalSeconds = 60;
            var pps = _vm.PixelsPerSecond;

            for (int s = 0; s <= totalSeconds; s++)
            {
                var x = s * pps;
                var tick = new BoxView { Color = Colors.LightGray, WidthRequest = 1, HeightRequest = 12 };
                AbsoluteLayout.SetLayoutBounds(tick, new Rect(x, 0, 1, 12));
                AbsoluteLayout.SetLayoutFlags(tick, AbsoluteLayoutFlags.None);
                _ruler.Children.Add(tick);

                var label = new Label { TextColor = Colors.White, FontSize = 10, Text = s.ToString() };
                AbsoluteLayout.SetLayoutBounds(label, new Rect(x + 2, 0, 30, 12));
                AbsoluteLayout.SetLayoutFlags(label, AbsoluteLayoutFlags.None);
                _ruler.Children.Add(label);

                if (s < totalSeconds)
                {
                    for (int i = 1; i < 5; i++)
                    {
                        var subx = x + i * (pps / 5);
                        var subtick = new BoxView { Color = Colors.Gray, WidthRequest = 1, HeightRequest = 8 };
                        AbsoluteLayout.SetLayoutBounds(subtick, new Rect(subx, 0, 1, 8));
                        AbsoluteLayout.SetLayoutFlags(subtick, AbsoluteLayoutFlags.None);
                        _ruler.Children.Add(subtick);
                    }
                }
            }
        }

        private void UpdatePlayhead()
        {
            var x = _vm.PlayheadSeconds * _vm.PixelsPerSecond;
            if (_playhead != null)
                _playhead.TranslationX = x;
        }

        private void OnAddTrack(object sender, EventArgs e)
        {
            _vm.AddTrack();
            RebuildTracksUI();
        }

        private void OnAddClip(object sender, EventArgs e)
        {
            if (_vm.Tracks.Count == 0) _vm.AddTrack();
            var track = _vm.Tracks[Math.Max(0, _vm.Tracks.Count - 1)];
            var clip = new ClipViewModel
            {
                Name = $"Clip {track.Clips.Count + 1}",
                StartSeconds = _vm.PlayheadSeconds,
                DurationSeconds = 3,
            };
            track.Clips.Add(clip);
            // Resolve immediate overlap on this track
            clip.StartSeconds = ResolveOverlapStart(track, clip, clip.StartSeconds, clip.DurationSeconds);

            RebuildTracksUI();
        }

        private void OnSplitClip(object sender, EventArgs e)
        {
            if (_selectedClip == null) return;
            var t = FindTrack(_selectedClip);
            if (t == null) return;
            var cutPos = _vm.PlayheadSeconds;
            if (cutPos <= _selectedClip.StartSeconds || cutPos >= _selectedClip.EndSeconds) return;

            var leftDur = cutPos - _selectedClip.StartSeconds;
            var rightDur = _selectedClip.EndSeconds - cutPos;

            var right = new ClipViewModel
            {
                Name = _selectedClip.Name + " (2)",
                StartSeconds = cutPos,
                DurationSeconds = rightDur,
            };
            _selectedClip.DurationSeconds = leftDur;
            t.Clips.Add(right);
            RebuildTracksUI();
        }

        private void OnDeleteClip(object sender, EventArgs e)
        {
            if (_selectedClip == null) return;
            var t = FindTrack(_selectedClip);
            if (t == null) return;
            t.Clips.Remove(_selectedClip);
            _selectedClip = null;
            RebuildTracksUI();
        }

        private TrackViewModel? FindTrack(ClipViewModel clip)
        {
            foreach (var t in _vm.Tracks)
                if (t.Clips.Contains(clip)) return t;
            return null;
        }

        private void OnZoomIn(object sender, EventArgs e)
        {
            _vm.PixelsPerSecond *= 1.25;
            BuildRuler();
            RepositionClips();
            SyncRulerScroll();
        }

        private void OnZoomOut(object sender, EventArgs e)
        {
            _vm.PixelsPerSecond /= 1.25;
            BuildRuler();
            RepositionClips();
            SyncRulerScroll();
        }

        private void OnTimelineScrolled(object? sender, ScrolledEventArgs e)
        {
            SyncRulerScroll();
        }

        private void SyncRulerScroll()
        {
            if (_ruler == null || _timelineScroll == null) return;
            var x = _timelineScroll.ScrollX;
            foreach (var child in _ruler.Children)
            {
                if (child is VisualElement ve)
                {
                    ve.TranslationX = -x;
                }
            }
        }

        // SNAP HELPERS
        private double SnapSeconds(double seconds, ClipViewModel self)
        {
            if (!_vm.SnapEnabled) return Math.Max(0, seconds);
            double best = seconds;
            double bestDist = _vm.SnapThresholdSeconds + 1; // larger than threshold

            // 1) grid
            if (_vm.SnapGridSeconds > 0)
            {
                var grid = Math.Round(seconds / _vm.SnapGridSeconds) * _vm.SnapGridSeconds;
                var d = Math.Abs(grid - seconds);
                if (d < bestDist && d <= _vm.SnapThresholdSeconds)
                {
                    best = grid; bestDist = d;
                }
            }

            // 2) playhead
            if (_vm.SnapToPlayhead)
            {
                var d = Math.Abs(_vm.PlayheadSeconds - seconds);
                if (d < bestDist && d <= _vm.SnapThresholdSeconds)
                {
                    best = _vm.PlayheadSeconds; bestDist = d;
                }
            }

            // 3) other clips edges (all tracks)
            if (_vm.SnapToClips)
            {
                foreach (var t in _vm.Tracks)
                {
                    foreach (var c in t.Clips)
                    {
                        if (ReferenceEquals(c, self)) continue;
                        // start
                        var d1 = Math.Abs(c.StartSeconds - seconds);
                        if (d1 < bestDist && d1 <= _vm.SnapThresholdSeconds) { best = c.StartSeconds; bestDist = d1; }
                        // end
                        var d2 = Math.Abs(c.EndSeconds - seconds);
                        if (d2 < bestDist && d2 <= _vm.SnapThresholdSeconds) { best = c.EndSeconds; bestDist = d2; }
                    }
                }
            }

            return Math.Max(0, best);
        }

        private (double snappedStart, double snappedEnd) SnapRange(double start, double end, ClipViewModel self)
        {
            var s = SnapSeconds(start, self);
            var e = SnapSeconds(end, self);
            return (s, e);
        }

        // inject snapping into gestures
        private void ApplyMove(ClipViewModel clip, Grid view, double baseStartSeconds, double deltaPixels)
        {
            var candidate = Math.Max(0, baseStartSeconds + deltaPixels / _vm.PixelsPerSecond);
            var snapped = SnapSeconds(candidate, clip);

            var track = GetTrackForClip(clip);
            if (track != null)
            {
                var resolved = ResolveOverlapStart(track, clip, snapped, clip.DurationSeconds);
                var end = resolved + clip.DurationSeconds;
                if (!GetOverlappingClips(track, clip, resolved, end).Any())
                {
                    clip.StartSeconds = resolved;
                    UpdateClipLayout(view, clip);
                    return;
                }
            }
            clip.StartSeconds = snapped;
            UpdateClipLayout(view, clip);
        }

        private void ApplyResizeLeft(ClipViewModel clip, Grid view, double baseStart, double baseDuration, double deltaPixels)
        {
            var initialEnd = baseStart + baseDuration; 
            var desiredStart = Math.Max(0, baseStart + deltaPixels / _vm.PixelsPerSecond);
            var snapStart = SnapSeconds(desiredStart, clip);

            var track = GetTrackForClip(clip);
            double newStart = snapStart;
            if (track != null)
            {
                var durationFromStart = Math.Max(0.05, initialEnd - snapStart);
                var resolvedStart = ResolveOverlapStart(track, clip, snapStart, durationFromStart);
                newStart = Math.Clamp(resolvedStart, 0, initialEnd - 0.05);
            }
            var newDuration = Math.Max(0.05, initialEnd - newStart);
            clip.StartSeconds = newStart;
            clip.DurationSeconds = newDuration;
            UpdateClipLayout(view, clip);
        }

        private void ApplyResizeRight(ClipViewModel clip, Grid view, double baseDuration, double deltaPixels)
        {
            var desiredEnd = clip.StartSeconds + Math.Max(0.05, baseDuration + deltaPixels / _vm.PixelsPerSecond);
            var (_, snapEnd) = SnapRange(clip.StartSeconds, desiredEnd, clip);

            var track = GetTrackForClip(clip);
            double limitedEnd = snapEnd;
            if (track != null)
            {
                limitedEnd = LimitEndToNextClip(track, clip, snapEnd);
                var start = clip.StartSeconds;
                if (GetOverlappingClips(track, clip, start, limitedEnd).Any())
                {
                    limitedEnd = LimitEndToNextClip(track, clip, start + 0.05);
                }
            }

            var newDuration = Math.Max(0.05, limitedEnd - clip.StartSeconds);
            clip.DurationSeconds = newDuration;
            UpdateClipLayout(view, clip);
        }

        // Overlap helpers (added)
        private TrackViewModel? GetTrackForClip(ClipViewModel clip)
            => _vm.Tracks.FirstOrDefault(t => t.Clips.Contains(clip));

        private static bool RangesOverlap(double aStart, double aEnd, double bStart, double bEnd)
            => Math.Max(aStart, bStart) < Math.Min(aEnd, bEnd);

        private IEnumerable<ClipViewModel> GetOverlappingClips(TrackViewModel track, ClipViewModel self, double start, double end)
            => track.Clips.Where(c => !ReferenceEquals(c, self) && RangesOverlap(start, end, c.StartSeconds, c.EndSeconds));

        private double ResolveOverlapStart(TrackViewModel track, ClipViewModel self, double snappedStart, double duration)
        {
            double start = Math.Max(0, snappedStart);
            const double eps = 1e-6;
            for (int i = 0; i < 16; i++)
            {
                var end = start + duration;
                var overlappers = GetOverlappingClips(track, self, start, end).ToList();
                if (overlappers.Count == 0) break;

                double rightCandidate = overlappers.Max(c => c.EndSeconds);
                double leftCandidate = overlappers.Min(c => c.StartSeconds) - duration;
                if (leftCandidate < 0) leftCandidate = 0;

                bool leftOverlaps = GetOverlappingClips(track, self, leftCandidate, leftCandidate + duration).Any();

                if (leftOverlaps || Math.Abs(start - leftCandidate) < eps)
                {
                    start = rightCandidate;
                    continue;
                }

                // choose the smaller movement otherwise
                double moveRight = Math.Abs(rightCandidate - start);
                double moveLeft = Math.Abs(start - leftCandidate);
                start = moveRight <= moveLeft ? rightCandidate : leftCandidate;
            }
            return Math.Max(0, start);
        }

        private double LimitEndToNextClip(TrackViewModel track, ClipViewModel self, double snappedEnd)
        {
            double nextStart = double.PositiveInfinity;
            foreach (var c in track.Clips)
            {
                if (ReferenceEquals(c, self)) continue;
                if (c.StartSeconds >= self.StartSeconds)
                    nextStart = Math.Min(nextStart, c.StartSeconds);
            }
            if (double.IsPositiveInfinity(nextStart)) return snappedEnd;
            return Math.Min(snappedEnd, nextStart);
        }

        private void EnforceNoOverlapAfterTrackChange(ClipViewModel clip, Grid view)
        {
            var track = GetTrackForClip(clip);
            if (track == null) return;
            var resolved = ResolveOverlapStart(track, clip, clip.StartSeconds, clip.DurationSeconds);
            clip.StartSeconds = resolved;
            UpdateClipLayout(view, clip);
        }

        private void OnRulerTapped(object sender, TappedEventArgs e)
        {
            if (_ruler == null) return;
            var p = e.GetPosition(_ruler);
            if (p is Point pt)
            {
                _vm.PlayheadSeconds = pt.X / _vm.PixelsPerSecond;
                UpdatePlayhead();
            }
        }

        private void BeginInteraction()
        {
#if ANDROID
            try
            {
                var view = _timelineScroll?.Handler?.PlatformView as Android.Views.View;
                view?.Parent?.RequestDisallowInterceptTouchEvent(true);
            }
            catch { }
#endif
        }

        private void EndInteraction()
        {
#if ANDROID
            try
            {
                var view = _timelineScroll?.Handler?.PlatformView as Android.Views.View;
                view?.Parent?.RequestDisallowInterceptTouchEvent(false);
            }
            catch { }
#endif
        }

        private void AttachAndroidTouchBlocker(VisualElement element)
        {
#if ANDROID
            element.HandlerChanged += (s, e) =>
            {
                try
                {
                    var native = element?.Handler?.PlatformView as global::Android.Views.View;
                    if (native == null) return;
                    native.Touch += (sender, args) =>
                    {
                        var action = args?.Event?.Action ?? global::Android.Views.MotionEventActions.Cancel;
                        if (action == global::Android.Views.MotionEventActions.Down)
                        {
                            native.Parent?.RequestDisallowInterceptTouchEvent(true);
                        }
                        else if (action == global::Android.Views.MotionEventActions.Up || action == global::Android.Views.MotionEventActions.Cancel)
                        {
                            native.Parent?.RequestDisallowInterceptTouchEvent(false);
                        }
                        args.Handled = false; // let gestures proceed
                    };
                }
                catch { }
            };
#endif
        }

        public async Task<bool> EnsureStoragePermissionAsync()
        {
#if ANDROID
            var activity = Platform.CurrentActivity;
            if (ContextCompat.CheckSelfPermission(activity, Manifest.Permission.ReadExternalStorage) != Permission.Granted ||
                ContextCompat.CheckSelfPermission(activity, Manifest.Permission.WriteExternalStorage) != Permission.Granted)
            {
                ActivityCompat.RequestPermissions(
                    activity,
                    new string[]
                    {
                Manifest.Permission.ReadExternalStorage,
                Manifest.Permission.WriteExternalStorage
                    },
                    1001);

                await Task.Delay(1000);
            }

            // 再次检查
            return ContextCompat.CheckSelfPermission(activity, Manifest.Permission.ReadExternalStorage) == Permission.Granted &&
                   ContextCompat.CheckSelfPermission(activity, Manifest.Permission.WriteExternalStorage) == Permission.Granted;
#else
    return true;
#endif
        }

        private async void TestPageButton_Clicked(object sender, EventArgs e)
        {
            await EnsureStoragePermissionAsync();
            await Navigation.PushAsync(new TestPage());
        }

    }
}
