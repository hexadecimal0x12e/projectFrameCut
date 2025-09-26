using Microsoft.Maui.Controls;
using Microsoft.Maui.Graphics;
using Microsoft.Maui.Layouts;
using projectFrameCut.ViewModels;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Linq;
using projectFrameCut.Shared; 
using System.Text.Json;
using System.Threading.Tasks;
using System.IO; 

namespace projectFrameCut
{
    public partial class DraftPage : ContentPage
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

        // Track drag helpers
        private const double TrackHeight = 48; // must match lane HeightRequest
        private int _dragStartTrackIndex = -1;
        private int _currentDragTrackIndex = -1;

        // References to named elements
        private VerticalStackLayout? _tracksHeader;
        private VerticalStackLayout? _tracksPanel;
        private AbsoluteLayout? _ruler;
        private BoxView? _playhead;
        private Label? _status;
        private ScrollView? _timelineScroll;

        // Simple asset model
        public class AssetItem
        {
            public string Name { get; set; } = string.Empty;
            public string? Path { get; set; }
            public string Icon { get; set; }
            public DateTime AddedAt { get; set; } = DateTime.Now;
        }

        // Collection for asset library (bind via x:Reference)
        public ObservableCollection<AssetItem> Assets { get; } = new();

        public DraftPage()
        {
            InitializeComponent();
            BindingContext = _vm; // ensure binding for TimelineWidth

            // Resolve named elements from XAML
            _tracksHeader = this.FindByName<VerticalStackLayout>("TracksHeader");
            _tracksPanel = this.FindByName<VerticalStackLayout>("TracksPanel");
            _ruler = this.FindByName<AbsoluteLayout>("Ruler");
            _playhead = this.FindByName<BoxView>("Playhead");
            _status = this.FindByName<Label>("Status");
            _timelineScroll = this.FindByName<ScrollView>("TimelineScroll");

            // Initialize with two tracks
            _vm.AddTrack("Video #1");
            _vm.AddTrack("Video #2");

            RebuildTracksUI();
            UpdatePlayhead();
            BuildRuler();

            Title = "Blank project";
        }

        public DraftPage(string draftSource)
        {
            InitializeComponent();
            BindingContext = _vm; // ensure binding for TimelineWidth

            // Resolve named elements from XAML
            _tracksHeader = this.FindByName<VerticalStackLayout>("TracksHeader");
            _tracksPanel = this.FindByName<VerticalStackLayout>("TracksPanel");
            _ruler = this.FindByName<AbsoluteLayout>("Ruler");
            _playhead = this.FindByName<BoxView>("Playhead");
            _status = this.FindByName<Label>("Status");
            _timelineScroll = this.FindByName<ScrollView>("TimelineScroll");

            ImportDraft(draftSource);

        }


        protected override void OnNavigatedTo(NavigatedToEventArgs args)
        {
            base.OnNavigatedTo(args);

            
        }

        // Helper: determine clip type by file extension
        private static ClipMode DetermineClipMode(string? path)
        {
            if (string.IsNullOrWhiteSpace(path)) return ClipMode.Special;
            var ext = Path.GetExtension(path).ToLowerInvariant();
            // Common video extensions
            string[] video = [".mp4", ".mov", ".mkv", ".avi", ".webm", ".m4v"]; 
            string[] image = [".png", ".jpg", ".jpeg", ".webp", ".bmp", ".tiff"]; 
            if (video.Contains(ext)) return ClipMode.VideoClip;
            if (image.Contains(ext)) return ClipMode.PhotoClip;
            return ClipMode.Special; // fallback
        }

        // Export timeline to DraftStructureJSON
        private DraftStructureJSON BuildDraft(string projectName = "Default Project")
        {
            const uint frameRate = 60; // global framerate assumption
            double frameTime = 1.0 / frameRate;
            var clipDtos = new List<ClipDraftDTO>();

            for (int trackIndex = 0; trackIndex < _vm.Tracks.Count; trackIndex++)
            {
                var track = _vm.Tracks[trackIndex];
                foreach (var clip in track.Clips.OrderBy(c => c.StartSeconds))
                {
                    uint startFrame = (uint)Math.Round(clip.StartSeconds * frameRate);
                    uint durationFrames = (uint)Math.Max(1, Math.Round(clip.DurationSeconds * frameRate));
                    var clipType = DetermineClipMode(clip.SourcePath);
                    clipDtos.Add(new ClipDraftDTO
                    {
                        Id = clip.Id,
                        Name = clip.Name,
                        ClipType = clipType,
                        LayerIndex = (uint)trackIndex,
                        StartFrame = startFrame,
                        Duration = durationFrames,
                        FrameTime = (float)frameTime,
                        MixtureMode = RenderMode.Overlay,
                        FilePath = clip.SourcePath
                    });
                }
            }

            return new DraftStructureJSON
            {
                Name = projectName,
                Clips = clipDtos.Cast<object>().ToArray()
            };
        }

        // Example method to get JSON string (could be bound to a button)
        private string ExportDraftToJson()
        {
            var draft = BuildDraft("Timeline Export");
            var opts = new JsonSerializerOptions { WriteIndented = true };
            return JsonSerializer.Serialize(draft, opts);
        }

        // Example handler (needs a Button in XAML with Clicked="OnExportDraft")
        private async void OnExportDraft(object sender, EventArgs e)
        {
            try
            {
                var json = ExportDraftToJson();
                Debug.WriteLine(json);
                await DisplayAlert("Draft Export", json.Length > 1000 ? json.Substring(0, 1000) + "..." : json, "OK");
            }
            catch (Exception ex)
            {
                Debug.WriteLine(ex);
                await DisplayAlert("Error", ex.Message, "OK");
            }
        }

        // Import DraftStructureJSON and rebuild all tracks/clips
        private async void ImportDraft(string src)
        {
            try
            {
                var opts = new JsonSerializerOptions
                {
                    PropertyNameCaseInsensitive = true
                };
                var draft = JsonSerializer.Deserialize<DraftStructureJSON>(src, opts);
                if (draft == null)
                {
                    await DisplayAlert("错误", "解析 DraftStructureJSON 失败", "OK");
                    return;
                }
                Title = draft.Name;

                ImportDraftToTimeline(draft);
            }
            catch (Exception ex)
            {
                Debug.WriteLine(ex);
                await DisplayAlert("Error", ex.Message, "OK");
            }
        }

        public void ImportDraftToTimeline(DraftStructureJSON draft)
        {
            // Reset tracks
            _vm.Tracks.Clear();

            // collect DTOs
            var dtos = new List<ClipDraftDTO>();
            foreach (var obj in draft.Clips ?? Array.Empty<object>())
            {
                switch (obj)
                {
                    case JsonElement je:
                        try
                        {
                            var dto = je.Deserialize<ClipDraftDTO>(new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
                            if (dto != null) dtos.Add(dto);
                        }
                        catch { /* ignore bad clip */ }
                        break;
                    case ClipDraftDTO dto:
                        dtos.Add(dto);
                        break;
                }
            }

            // Ensure enough tracks by LayerIndex
            int trackCount = dtos.Count == 0 ? 1 : (int)(dtos.Max(c => (int)c.LayerIndex) + 1);
            for (int i = 0; i < trackCount; i++)
                _vm.AddTrack($"Video #{i + 1}");

            // frame time: prefer clip's FrameTime or draft.targetFrameRate
            double frameTime = 0;
            if (dtos.Any() && dtos.All(c => c.FrameTime > 0))
                frameTime = dtos.First().FrameTime;
            else if (draft.targetFrameRate > 0)
                frameTime = 1.0 / draft.targetFrameRate;
            else
                frameTime = 1.0 / 60.0;

            double maxEndSeconds = 0;
            foreach (var c in dtos.OrderBy(c => c.LayerIndex).ThenBy(c => c.StartFrame))
            {
                var startSeconds = c.StartFrame * frameTime;
                var durationSeconds = Math.Max(0.05, c.Duration * frameTime);

                var clip = new ClipViewModel
                {
                    Id = string.IsNullOrWhiteSpace(c.Id) ? Guid.NewGuid().ToString() : c.Id,
                    Name = string.IsNullOrWhiteSpace(c.Name) ? Path.GetFileName(c.FilePath ?? "Clip") : c.Name,
                    StartSeconds = startSeconds,
                    DurationSeconds = durationSeconds,
                    SourcePath = c.FilePath
                };

                int idx = (int)Math.Clamp(c.LayerIndex, 0, Math.Max(0, _vm.Tracks.Count - 1));
                var track = _vm.Tracks[idx];
                // resolve overlaps within the track to be safe
                clip.StartSeconds = ResolveOverlapStart(track, clip, clip.StartSeconds, clip.DurationSeconds);
                track.Clips.Add(clip);
                maxEndSeconds = Math.Max(maxEndSeconds, clip.StartSeconds + clip.DurationSeconds);

                Assets.Add(new AssetItem
                {
                    Name = Path.GetFileName(c.FilePath) ?? $"unknown draft@{c.Id}",
                    Path = c.FilePath,
                    Icon = c.ClipType switch
                    {
                        ClipMode.VideoClip => "📽️",
                        ClipMode.PhotoClip => "🖼️",
                        ClipMode.SolidColorClip => "🟦",
                        _ => "❔"
                    }
                });
            }

            // Expand timeline length to fit imported content with some padding
            _vm.TotalSeconds = Math.Max(60, Math.Ceiling(maxEndSeconds + 2));

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

                // Add clip views
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

        // Example handler for adding an asset directly to the timeline
        public void OnAddAssetToTimeline(object sender, EventArgs e)
        {
            if (sender is Button btn && btn.CommandParameter is AssetItem asset)
            {
                if (_vm.Tracks.Count == 0)
                    _vm.AddTrack("Video 1");
                var track = _vm.Tracks[0]; // always first track for now
                var clip = new ClipViewModel
                {
                    Name = asset.Name,
                    StartSeconds = _vm.PlayheadSeconds,
                    DurationSeconds = 3,
                    SourcePath = asset.Path // capture path so it can be exported
                };
                track.Clips.Add(clip);
                clip.StartSeconds = ResolveOverlapStart(track, clip, clip.StartSeconds, clip.DurationSeconds);
                RebuildTracksUI();
            }
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

            // Initial layout
            UpdateClipLayout(g, clip);

            // Move pan
            var movePan = new PanGestureRecognizer();
            movePan.PanUpdated += (s, e) =>
            {
                if (e.StatusType == GestureStatus.Started)
                {
                    SelectClip(clip);
                    _dragStartX = e.TotalX;
                    _dragStartSeconds = clip.StartSeconds;
                    _dragStartTrackIndex = GetTrackIndexOfClip(clip);
                    _currentDragTrackIndex = _dragStartTrackIndex;
                }
                else if (e.StatusType == GestureStatus.Running && !_isResizingLeft && !_isResizingRight)
                {
                    // horizontal move with snapping/overlap-resolve
                    ApplyMove(clip, g, _dragStartSeconds, e.TotalX - _dragStartX);

                    // cross-track move with pre-check to prevent overlap
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

            var leftPan = new PanGestureRecognizer();
            leftPan.PanUpdated += (s, e) =>
            {
                if (e.StatusType == GestureStatus.Started)
                {
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
                    _isResizingRight = false;
                    UpdateClipLayout(g, clip);
                }
            };
            rightHandle.GestureRecognizers.Add(rightPan);

            // Tap to select
            var tap = new TapGestureRecognizer();
            tap.Tapped += (s, e) => SelectClip(clip);
            g.GestureRecognizers.Add(tap);

            return g;
        }

        private void UpdateClipLayout(Grid g, ClipViewModel clip)
        {
            var left = clip.StartSeconds * _vm.PixelsPerSecond;
            var width = clip.DurationSeconds * _vm.PixelsPerSecond;

            g.TranslationX = left;
            g.WidthRequest = width;
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
            var totalSeconds = (int)Math.Ceiling(_vm.TotalSeconds);
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

        // Modified: async to allow file picking for SourcePath
        private async void OnAddClip(object sender, EventArgs e)
        {
            if (_vm.Tracks.Count == 0) _vm.AddTrack();
            var track = _vm.Tracks[Math.Max(0, _vm.Tracks.Count - 1)];
            string? pickedPath = null;
#if ANDROID || IOS || MACCATALYST || WINDOWS
            try
            {
                var result = await FilePicker.PickAsync(new PickOptions { PickerTitle = "选择素材文件 (可取消)" });
                if (result != null)
                    pickedPath = result.FullPath;
            }
            catch (Exception ex)
            {
                Debug.WriteLine(ex);
            }
#endif
            var clip = new ClipViewModel
            {
                Name = pickedPath != null ? Path.GetFileName(pickedPath) : $"Clip {track.Clips.Count + 1}",
                StartSeconds = _vm.PlayheadSeconds,
                DurationSeconds = 3,
                SourcePath = pickedPath
            };
            track.Clips.Add(clip);
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
                SourcePath = _selectedClip.SourcePath // inherit path
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
                // If still overlapping due to unsatisfiable space, resolved may land outside; verify
                var end = resolved + clip.DurationSeconds;
                if (!GetOverlappingClips(track, clip, resolved, end).Any())
                {
                    clip.StartSeconds = resolved;
                    UpdateClipLayout(view, clip);
                    return;
                }
            }
            // fallback: set snapped without overlap resolution if no track
            clip.StartSeconds = snapped;
            UpdateClipLayout(view, clip);
        }

        private void ApplyResizeLeft(ClipViewModel clip, Grid view, double baseStart, double baseDuration, double deltaPixels)
        {
            var initialEnd = baseStart + baseDuration; // right edge fixed during gesture
            var desiredStart = Math.Max(0, baseStart + deltaPixels / _vm.PixelsPerSecond);
            var snapStart = SnapSeconds(desiredStart, clip);

            var track = GetTrackForClip(clip);
            double newStart = snapStart;
            if (track != null)
            {
                // Resolve overlap using fixed end (duration changes)
                var durationFromStart = Math.Max(0.05, initialEnd - snapStart);
                var resolvedStart = ResolveOverlapStart(track, clip, snapStart, durationFromStart);
                // Clamp so we never pass the fixed end - min duration
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
                // also ensure no overlap remains
                var start = clip.StartSeconds;
                if (GetOverlappingClips(track, clip, start, limitedEnd).Any())
                {
                    // push end to the left to the nearest non-overlap (to next clip's start)
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

                // If leftCandidate still overlaps, discard it
                bool leftOverlaps = GetOverlappingClips(track, self, leftCandidate, leftCandidate + duration).Any();

                // If we are already at leftCandidate (e.g., 0) or leftCandidate is invalid, move right
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

       
        private async void OnAddAsset(object sender, EventArgs e)
        {
            try
            {
#if ANDROID || IOS || MACCATALYST || WINDOWS
                var result = await FilePicker.PickAsync(new PickOptions
                {
                    PickerTitle = "选择素材文件"
                });
                if (result != null)
                {
                    Assets.Add(new AssetItem
                    {
                        Name = result.FileName,
                        Path = result.FullPath
                    });
                }
#else
                Assets.Add(new AssetItem { Name = $"Dummy_{Assets.Count + 1}.png", Path = null });
#endif
            }
            catch (Exception ex)
            {
                Debug.WriteLine(ex);
                await DisplayAlert("Error", ex.Message, "OK");
            }
        }
    }
}
