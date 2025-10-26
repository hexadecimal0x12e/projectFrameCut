using Microsoft.Maui.Controls.Shapes;
using Microsoft.Maui.Devices;
using projectFrameCut.DraftStuff;
using projectFrameCut.PropertyPanel;
using projectFrameCut.Shared;
using projectFrameCut.ViewModels;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading.Tasks;
using static ILGPU.Stride1D;
using static System.Net.Mime.MediaTypeNames;

namespace projectFrameCut;

public partial class DraftPage : ContentPage
{
    public event EventHandler<ClipUpdateEventArgs>? OnClipChanged;

    public const int ClipHeight = 62;
    private const double MinClipWidth = 30.0;

    public ConcurrentDictionary<string, ClipElementUI> Clips = new();
    public ConcurrentDictionary<int, AbsoluteLayout> Tracks = new();
    ConcurrentDictionary<string, double> HandleStartWidth = new();

    ClipElementUI? _selected = null;

    TapGestureRecognizer nopGesture = new(), rulerTapGesture = new();

    public double SecondsPerFrame { get; set; } = 1 / 30d;
    public double FramePerPixel { get; set; } = 1d;

    int trackCount = 0;
    int webViewTrackCount = 1;
    double tracksViewOffset = 0;
    double tracksZoomOffest = 1d;


    bool isPopupShowing = false;
    Border Popup = new();

    private Size WindowSize = new(500, 500);

    private const double SnapGridPixels = 10.0;
    private const double SnapThresholdPixels = 8.0;
    private bool SnapEnabled = true;

    _RoundRectangleRadiusType[] RoundRectangleRadius = Array.Empty<_RoundRectangleRadiusType>();

    PanDeNoise Xdenoiser = new(), Ydenoiser = new();

    public bool ShowShadow { get; private set; } = true;
    public bool LogUIMessageToLogger { get; private set; } = false;
    public bool Denoise { get; private set; } = true;

    public DraftPage()
    {
        InitializeComponent();
#if ANDROID
        OverlayLayer.IsVisible = false;
        OverlayLayer.InputTransparent = false;
#endif


        //hybridWebView.DefaultFile = "index.html";
        //hybridWebView.HybridRoot = "WebClipView";
        //hybridWebView.RawMessageReceived += HybridWebView_RawMessageReceived;
        TrackCalculator.HeightPerTrack = ClipHeight;

        PostInit();
        AddTrackButton_Clicked(new object(), EventArgs.Empty);
        AddClip_Clicked(new object(), EventArgs.Empty);

    }


    private void PostInit()
    {
        rulerTapGesture.Tapped += PlayheadTapped;

        nopGesture.Tapped += (s, e) =>
        {
#if ANDROID
            OverlayLayer.IsVisible = false;
#endif
        };

        Loaded += (s, e) =>
        {
            PlayheadLine.TranslationY = UpperContent.Height - RulerLayout.Height;
            RulerLayout.GestureRecognizers.Add(rulerTapGesture);
            PlayheadLine.HeightRequest = Tracks.Count * ClipHeight + RulerLayout.Height;
            this.Window.SizeChanged += Window_SizeChanged;
            var bgTap = new TapGestureRecognizer();
            bgTap.Tapped += async (s, e) => await HidePopup();
            OverlayLayer.GestureRecognizers.Clear();
            OverlayLayer.GestureRecognizers.Add(bgTap);
            SetStateOK();
            SetStatusText(Localized.DraftPage_EverythingFine);

        };

        OnClipChanged += (s, e) =>
        {
            foreach (var item in Clips)
            {
                if (!item.Value.isInfiniteLength && item.Value.lengthInFrame > item.Value.maxFrameCount)
                {
                    SetStateFail($"Clip {item.Key} has a invaild length {item.Value.lengthInFrame} frames, larger than it's source {item.Value.maxFrameCount}.");
                }
            }
            PlayheadLine.HeightRequest = Tracks.Count * ClipHeight + RulerLayout.Height;
            Log($"Clips changed, Total clips: {Clips.Count}");
            foreach (var item in Clips)
            {
                Log($"Clip@{item.Key}");
            }
            Log(JsonSerializer.Serialize(DraftImportAndExportHelper.ExportFromDraftPage(this)));
        };

        if (this.Window is not null)
        {
            this.Window.SizeChanged += Window_SizeChanged;
        }
    }

    public DraftPage(ConcurrentDictionary<string, ClipElementUI> clips, int initialTrackCount)
    {
        InitializeComponent();
#if ANDROID
        OverlayLayer.IsVisible = false;
        OverlayLayer.InputTransparent = false;
#endif


        //hybridWebView.DefaultFile = "index.html";
        //hybridWebView.HybridRoot = "WebClipView";
        //hybridWebView.RawMessageReceived += HybridWebView_RawMessageReceived;
        TrackCalculator.HeightPerTrack = ClipHeight;

        SetStateBusy();
        Clips = clips;
        Tracks = new ConcurrentDictionary<int, AbsoluteLayout>();

        // Create tracks in numeric order so visual order matches track indices.
        for (int i = 0; i < initialTrackCount; i++)
        {
            if (!Tracks.ContainsKey(i)) AddATrack(i);
        }

        // Add clips to their tracks after tracks are created.
        foreach (var kv in Clips.OrderBy(kv => kv.Value.origTrack ?? 0).ThenBy(kv => kv.Value.origX))
        {
            var item = kv.Value;
            int t = item.origTrack ?? 0;
            // ensure track exists (defensive)
            if (!Tracks.ContainsKey(t)) AddATrack(t);
            AddAClip(item, t);
            RegisterClip(item, true);
        }

        // ensure internal trackCount matches requested count
        trackCount = initialTrackCount;

        PostInit();
    }



    #region add stuff


    private ClipElementUI CreateAndAddClip(
        double startX,
        double width,
        int trackIndex,
        string? id = null,
        string? labelText = null,
        Brush? background = null,
        Border? prototype = null,
        bool resolveOverlap = true,
        uint relativeStart = 0,
        uint maxFrames = 0)
    {
        if (!Tracks.ContainsKey(trackIndex))
            throw new ArgumentOutOfRangeException(nameof(trackIndex));

        var element = CreateClip(startX, width, trackIndex, id, labelText, background, prototype, relativeStart, maxFrames);
        RegisterClip(element, resolveOverlap);
        AddAClip(element, trackIndex);

        return element;
    }



    public static ClipElementUI CreateClip(
        double startX,
        double width,
        int trackIndex,
        string? id = null,
        string? labelText = null,
        Brush? background = null,
        Border? prototype = null,
        uint relativeStart = 0,
        uint maxFrames = 0)
    {

        string cid = id ?? Guid.NewGuid().ToString();

        // Build UI
        var clipBorder = new Border
        {
            Stroke = prototype?.Stroke ?? Colors.Gray,
            StrokeThickness = prototype?.StrokeThickness ?? 2,
            Background = background ?? prototype?.Background ?? new SolidColorBrush(Colors.CornflowerBlue),
            WidthRequest = width,
            HeightRequest = prototype?.HeightRequest > 0 ? prototype!.HeightRequest : ClipHeight,
            StrokeShape = prototype?.StrokeShape ?? new RoundRectangle
            {
                CornerRadius = 20,
                BackgroundColor = Colors.White,
                StrokeThickness = 8
            }
        };

        var leftHandle = new Border
        {
            Stroke = Colors.Gray,
            StrokeThickness = 2,
            Background = new SolidColorBrush(Colors.White),
            WidthRequest = 25,
            HeightRequest = 55,
            HorizontalOptions = LayoutOptions.Center,
            VerticalOptions = LayoutOptions.Center,
            StrokeShape = new RoundRectangle
            {
                CornerRadius = 20,
                BackgroundColor = Colors.White,
            }
        };

        var rightHandle = new Border
        {
            Stroke = Colors.Gray,
            StrokeThickness = 2,
            Background = new SolidColorBrush(Colors.White),
            WidthRequest = 25,
            HeightRequest = 55,
            HorizontalOptions = LayoutOptions.Center,
            VerticalOptions = LayoutOptions.Center,
            StrokeShape = new RoundRectangle
            {
                CornerRadius = 20,
                BackgroundColor = Colors.White,
            }
        };

        var element = new ClipElementUI
        {
            Id = cid,
            layoutX = 0,
            layoutY = 0,
            Clip = clipBorder,
            LeftHandle = leftHandle,
            RightHandle = rightHandle,
            maxFrameCount = maxFrames,
            relativeStartFrame = relativeStart,
            isInfiniteLength = width <= 0,
            origLength = width,
            origTrack = trackIndex,
            origX = startX
        };

        var cont = new HorizontalStackLayout
        {
            Children =
            {
                new Label
                {
                    Text = string.IsNullOrWhiteSpace(labelText) ? $"Clip {cid[^4..]}" : labelText
                }
            },
            HorizontalOptions = LayoutOptions.Center,
            VerticalOptions = LayoutOptions.Center,
        };

        Grid.SetColumn(element.LeftHandle, 0);
        Grid.SetColumn(element.RightHandle, 2);
        Grid.SetColumn(cont, 1);

        element.Clip.Content = new Grid
        {
            Children =
            {
                element.LeftHandle,
                cont,
                element.RightHandle
            },
            ColumnDefinitions =
            {
                new ColumnDefinition { Width = new GridLength(30, GridUnitType.Absolute) },
                new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) },
                new ColumnDefinition { Width = new GridLength(30, GridUnitType.Absolute) }
            }
        };

        element.Clip.BindingContext = element;
        element.LeftHandle.BindingContext = element;
        element.RightHandle.BindingContext = element;


        return element;
    }

    private void RegisterClip(ClipElementUI element, bool resolveOverlap)
    {
        var cid = element.Id;


        // gestures
        var clipPanGesture = new PanGestureRecognizer();
        // ensure the handler receives the Border as sender by capturing the view in a lambda
        clipPanGesture.PanUpdated += (s, e) => ClipPaned(element.Clip, e);

        var rightHandleGesture = new PanGestureRecognizer();
        // pass the specific handle Border as sender so handler can cast sender to Border
        rightHandleGesture.PanUpdated += (s, e) => RightHandlePaned(element.RightHandle, e);

        var leftHandleGesture = new PanGestureRecognizer();
        leftHandleGesture.PanUpdated += (s, e) => LeftHandlePanded(element.LeftHandle, e);

        var selectTapGesture = new TapGestureRecognizer();
        selectTapGesture.Buttons = ButtonsMask.Primary | ButtonsMask.Secondary;
        selectTapGesture.Tapped += SelectTapGesture_Tapped;

        var doubleTapGesture = new TapGestureRecognizer();
        doubleTapGesture.NumberOfTapsRequired = 2;
        doubleTapGesture.Tapped += DoubleTapGesture_Tapped;

        element.Clip.GestureRecognizers.Add(clipPanGesture);
        element.Clip.GestureRecognizers.Add(selectTapGesture);
        element.Clip.GestureRecognizers.Add(doubleTapGesture);
        element.LeftHandle.GestureRecognizers.Add(leftHandleGesture);
        element.RightHandle.GestureRecognizers.Add(rightHandleGesture);

        // compute X
        if (resolveOverlap)
        {
            double snapped = SnapPixels(element.origX);
            element.Clip.TranslationX = ResolveOverlapStartPixels(element.origTrack ?? 0, cid, snapped, element.origLength);
        }

        Clips.AddOrUpdate(element.Id, element, (_, _) => element);
    }

    private void Split_Clicked(object sender, EventArgs e)
    {
        // Only for native view mode
        if (WebViewRadio.IsChecked) return;
        var clip = _selected;
        UnSelectTapGesture_Tapped(sender, null);
        if (clip is null) { SetStatusText("No clip selected"); return; }
        // Compute split X in the same coordinate space as clip TranslationX
        // PlayheadLine.TranslationX is in OverlayLayer coordinates. Convert to TrackContentLayout space.
        try
        {
            // Absolute X of playhead relative to page root
            var playheadAbs = GetAbsolutePosition(PlayheadLine, null);
            var contentAbs = GetAbsolutePosition(TrackContentLayout, null);
            double playheadXInContent = playheadAbs.X - contentAbs.X;

            var border = clip.Clip;
            double clipStartX = border.TranslationX;
            double clipWidth = border.Width > 0 ? border.Width : border.WidthRequest;
            double clipEndX = clipStartX + clipWidth;

            // Validate split inside clip
            if (playheadXInContent <= clipStartX + 1 || playheadXInContent >= clipEndX - 1)
            {
                SetStatusText("Playhead not inside selected clip");
                return;
            }

            // Left keeps start; adjust its width to split point
            double leftWidth = Math.Max(MinClipWidth, playheadXInContent - clipStartX);
            double rightWidth = Math.Max(MinClipWidth, clipEndX - playheadXInContent);

            // Update left clip UI
            border.WidthRequest = leftWidth;

            // Place right into same track via helper
            int trackIdx = clip.origTrack ?? Tracks.Keys.Max();
            // compute frames offset from original clip's in-point for the split
            uint framesOffset = (uint)Math.Round(leftWidth * FramePerPixel * tracksZoomOffest);

            _ = CreateAndAddClip(
                startX: playheadXInContent,
                width: rightWidth,
                trackIndex: trackIdx,
                id: null,
                labelText: $"Clip {clip.Id[^4..]} (2)",
                background: border.Background,
                prototype: border,
                resolveOverlap: true,
                // pass source total frames (or Infinity) so resize checks use frames
                maxFrames: clip.maxFrameCount,
                // relative start for right clip = original in-point + frames consumed by left clip
                relativeStart: (uint)(clip.relativeStartFrame + framesOffset));

            UpdateAdjacencyForTrack();
            SetStatusText("Split done");
        }
        catch (Exception ex)
        {
            Log(ex, "Split_Clicked", this);
            SetStatusText("Split failed");
        }
        finally
        {
            UpdateAdjacencyForTrack();
        }
    }

    private void AddAClip(ClipElementUI c, int trackIndex)
    {
        if (!Tracks.ContainsKey(trackIndex))
            throw new ArgumentOutOfRangeException(nameof(trackIndex));

        Tracks[trackIndex].Children.Add(c.Clip);
        UpdateAdjacencyForTrack();
    }


    private void AddTrackButton_Clicked(object sender, EventArgs e)
    {
        if (WebViewRadio.IsChecked)
        {
            webViewTrackCount++;
            hybridWebView.EvaluateJavaScriptAsync("createTrack()");
            return;
        }


        AddATrack(Tracks.Count);

        OnClipChanged?.Invoke(this, new ClipUpdateEventArgs { Reason = ClipUpdateReason.TrackAdd, SourceId = trackCount.ToString() });
    }

    public void AddATrack(int trackId)
    {
        Button removeBtn = new Button
        {
            Text = "Remove",
            BackgroundColor = Colors.Red,
            TextColor = Colors.White,
            HorizontalOptions = LayoutOptions.End
        };

        Border head = new Border
        {
            Content = new VerticalStackLayout
            {
                Children =
                {
                    new Label
                    {
                        Text = "New Track 1"
                    },
                    removeBtn
                }
            },
            HeightRequest = 60.0,
            Margin = new Thickness(0.0, 0.0, 0.0, 2.0)
        };

        AbsoluteLayout track = new AbsoluteLayout();

        var UnselectTapGesture = new TapGestureRecognizer();
        UnselectTapGesture.Tapped += UnSelectTapGesture_Tapped;

        track.GestureRecognizers.Add(UnselectTapGesture);
        head.GestureRecognizers.Add(UnselectTapGesture);

        int currentTrack = trackId;

        removeBtn.Clicked += (s, e) =>
        {
            while (Tracks[currentTrack].Children.FirstOrDefault((IView)null) != null)
            {
                Tracks[currentTrack].Children.Remove(Tracks[currentTrack].Children.First());
            }
            Dictionary<string, ClipElementUI> dictionary = Clips.Where(c => Tracks[currentTrack].Children.Contains(c.Value.Clip)).ToDictionary();
            while (dictionary.Count != 0)
            {
                string key = dictionary.First().Key;
                Clips.Remove(key, out var _);
                dictionary.Remove(key);
                if (dictionary.Count <= 0)
                {
                    break;
                }
            }
            TrackHeadLayout.Children.Remove(head);
            TrackContentLayout.Children.RemoveAt(currentTrack);
        };

        Tracks.AddOrUpdate(trackId, track, (int _, AbsoluteLayout _) => track);

        Border content = new Border
        {
            Content = track,
            HeightRequest = 60.0,
            Margin = new Thickness(0.0, 0.0, 0.0, 2.0)
        };

        TrackHeadLayout.Children.Insert(TrackHeadLayout.Count - 1, head);
        TrackContentLayout.Children.Add(content);
        trackCount++;
    }

    private void AddClip_Clicked(object sender, EventArgs e)
    {
        if (WebViewRadio.IsChecked)
        {
            var clipId = $"clip_{Guid.NewGuid().ToString("N").Substring(0, 8)}";
            var text = $"Clip {Clips.Count + 1}";
            var start = (Clips.Count % 5) * 130; // Example positioning
            var duration = 120;
            var trackIndex = webViewTrackCount; // Add to the last track

            AddWebViewClip(trackIndex, clipId, text, start, duration);
            return;
        }

        // Native view mode - create via helper
        int nativeTrackIndex = Tracks.Last().Key;
        _ = CreateAndAddClip(
            startX: 0,
            width: FrameToPixel(15 * 30),
            trackIndex: nativeTrackIndex,
            relativeStart: 0,
            maxFrames: 15 * 30,
            id: null,
            labelText: $"Clip {Clips.Count + 1}",
            background: new SolidColorBrush(Colors.CornflowerBlue),
            prototype: null,
            resolveOverlap: true);
    }

    #endregion

    #region select clip
    private async void DoubleTapGesture_Tapped(object? sender, TappedEventArgs e)
    {
        if (sender is not Border border) return;
        if (border.BindingContext is not ClipElementUI clip) return;
        Log($"Clip {clip.Id} double clicked, state:{clip.MovingStatus}");
        try
        {
            if (WindowSize.Width > WindowSize.Height)
            {
                await ShowClipPopup(border, clip);
            }
            else
            {
                await ShowAFullscreenPopupInBottom(WindowSize.Height / 1.2, BuildPropertyPanel(clip));
            }
        }
        catch (Exception ex)
        {
            Log(ex, "ShowClipPopup", clip);
            throw;
        }
    }

    private void SelectTapGesture_Tapped(object? sender, TappedEventArgs e)
    {
        if (sender is not Border border) return;
        if (border.BindingContext is not ClipElementUI clip) return;
        if (_selected is not null)
        {
            _selected.Clip.Background = new SolidColorBrush(Colors.CornflowerBlue);
        }
        Log($"Clip {clip.Id} clicked, state:{clip.MovingStatus}");
        if (clip.MovingStatus != ClipMovingStatus.Free) return;
        _selected = clip;
        clip.Clip.Background = Colors.YellowGreen;


    }

    private void UnSelectTapGesture_Tapped(object? sender, TappedEventArgs e)
    {
        if (_selected is null) return;
        _selected.Clip.Background = new SolidColorBrush(Colors.CornflowerBlue);
        _selected = null;
    }
    #endregion

    #region move clip
    private void ClipPaned(object? sender, PanUpdatedEventArgs e)
    {
        if (sender is not Border border) return;
        if (border.BindingContext is not ClipElementUI clip) return;
        if (clip.MovingStatus == ClipMovingStatus.Resize) return;
        var cid = clip.Id;

        int origTrack = TrackCalculator.CalculateWhichTrackShouldIn(border.TranslationY);
        clip.origTrack ??= origTrack;

        if (e.StatusType == GestureStatus.Running)
            HandlePanRunning(e, border, clip, cid, origTrack);
        else
            switch (e.StatusType)
            {
                case GestureStatus.Started:
                    HandlePanStarted(border, clip);
                    break;

                case GestureStatus.Completed:
                    HandlePanCompleted(border, clip, cid);
                    break;
            }
    }

    private void HandlePanStarted(Border border, ClipElementUI clip)
    {
        SetStateBusy();
        SetStatusText(Localized.DraftPage_WaitForUser);
        if (Denoise)
        {
            Xdenoiser.Reset();
            Ydenoiser.Reset();
        }
        clip.MovingStatus = ClipMovingStatus.Move;
        clip.layoutX = border.TranslationX;
        clip.layoutY = border.TranslationY;
        clip.defaultY = border.TranslationY;
    }

    private void HandlePanRunning(PanUpdatedEventArgs e, Border border, ClipElementUI clip, string cid, int origTrack)
    {
        if (clip.MovingStatus != ClipMovingStatus.Free && clip.MovingStatus != ClipMovingStatus.Move) return;

        double xToBe = -1, yToBe = -1;

        if (Denoise)
        {
            xToBe = clip.layoutX + Xdenoiser.Process(e.TotalX);
            yToBe = clip.layoutY + Ydenoiser.Process(e.TotalY);
        }
        else
        {
            xToBe = clip.layoutX + e.TotalX;
            yToBe = clip.layoutY + e.TotalY;
        }

        double actualYToBe = yToBe + UpperContent.Height;

        bool ghostExists = Clips.ContainsKey("ghost_" + cid);

        // If no ghost (still within same track), apply snapping and overlap resolution live
        if (!ghostExists)
        {
            double clipWidth = (border.Width > 0) ? border.Width : border.WidthRequest;
            int trackIndex = clip.origTrack ?? origTrack;
            double snapped = SnapPixels(xToBe);
            double resolved = ResolveOverlapStartPixels(trackIndex, cid, snapped, clipWidth);
            border.TranslationX = resolved;
        }
        else
        {
            border.TranslationX = xToBe;
        }
        if (!ghostExists && Math.Abs(actualYToBe - clip.defaultY) > 50.0)
        {
            InitMoveBetweenTracks(clip, cid, border);
        }
        else if (ghostExists)
        {
            border.TranslationY = yToBe;
            UpdateGhostAndShadow(border, cid, xToBe, origTrack);
        }
    }

    private void UpdateGhostAndShadow(Border border, string cid, double xToBe, int origTrack)
    {
        ClipElementUI ghostClip = Clips["ghost_" + cid];
        Point clipAbsolutePosition = GetAbsolutePosition(border, OverlayLayer);
        ghostClip.Clip.TranslationX = clipAbsolutePosition.X;
        ghostClip.Clip.TranslationY = clipAbsolutePosition.Y;
        double actualYToBe = clipAbsolutePosition.Y - UpperContent.Height;

        int newTrack = TrackCalculator.CalculateWhichTrackShouldIn(actualYToBe);

        ClipElementUI shadow = Clips["shadow_" + cid];
        // Apply snapping and overlap resolution for shadow placement
        double proposed = xToBe;
        double snapped = SnapPixels(proposed);
        double clipWidth = border.Width > 0 ? border.Width : border.WidthRequest;
        if (newTrack >= 0 && newTrack < trackCount)
        {
            // resolve overlaps on the target track
            var resolved = ResolveOverlapStartPixels(newTrack, cid, snapped, clipWidth);
            shadow.Clip.TranslationX = resolved;
        }
        else
        {
            shadow.Clip.TranslationX = snapped;
        }

        if (origTrack == newTrack)
        {
            return;
        }
        if (ShowShadow) UpdateShadowTrack(shadow, newTrack);
    }

    private void UpdateShadowTrack(ClipElementUI shadow, int newTrack)
    {
        try
        {
            if (shadow.origTrack.HasValue && Tracks.TryGetValue(shadow.origTrack.Value, out var oldTrackLayout))
            {
                oldTrackLayout.Children.Remove(shadow.Clip);
            }
            else
            {
                Tracks.Values
                    .FirstOrDefault(t => t.Children.Contains(shadow.Clip))
                    ?.Children.Remove(shadow.Clip);
            }

            if (newTrack >= 0 && newTrack < trackCount)
            {
                Tracks[newTrack].Children.Add(shadow.Clip);
                shadow.origTrack = newTrack;

            }
            else
            {
                shadow.origTrack = null;
            }
            if (newTrack > trackCount + 1 || newTrack < 0) SetStatusText(Localized.DraftPage_ReleaseToRemove);
            else SetStatusText(Localized.DraftPage_WaitForUser);
        }
        catch (Exception ex) //just ignore it, avoid crash
        {
            Log(ex, $"set shadow for {shadow.Id}", this);
        }
    }

    private async void HandlePanCompleted(Border border, ClipElementUI clip, string cid)
    {
        if (clip.MovingStatus != ClipMovingStatus.Free && clip.MovingStatus != ClipMovingStatus.Move) return;

        if (ShowShadow && Clips.TryRemove("shadow_" + cid, out var shadowClip))
        {
            if (shadowClip.origTrack is int sTrack && Tracks.TryGetValue(sTrack, out var sLayout))
            {
                sLayout.Children.Remove(shadowClip.Clip);
            }
            else
            {
                Tracks.Values.FirstOrDefault(t => t.Children.Contains(shadowClip.Clip))?.Children.Remove(shadowClip.Clip);
            }
        }

        if (Clips.TryRemove("ghost_" + cid, out var ghostClip))
        {
            int newTrack = TrackCalculator.CalculateWhichTrackShouldIn(ghostClip.Clip.TranslationY - UpperContent.Height);
            OverlayLayer.Children.Remove(ghostClip.Clip);

            if (clip.origTrack is int oldTrack && Tracks.TryGetValue(oldTrack, out var oldTrackLayout))
            {
                oldTrackLayout.Children.Remove(border);
            }

            if (newTrack < 0 || newTrack > trackCount)
            {
                Clips.TryRemove(cid, out _);
                SetStatusText(Localized.DraftPage_Removed);
                SetStateOK();
                Log($"clip {cid} removed.");
                return;
            }
            else
            {
                if (newTrack == trackCount)
                {
                    AddTrackButton_Clicked(this, EventArgs.Empty);
                }

                // snap X and resolve overlaps on target track before inserting
                double clipWidth = (border.Width > 0) ? border.Width : border.WidthRequest;
                double snappedX = SnapPixels(border.TranslationX);
                double resolvedX = ResolveOverlapStartPixels(newTrack, cid, snappedX, clipWidth);
                border.TranslationX = resolvedX;

                if (clip.origTrack != newTrack)
                {
                    await ClipsTrackChanged(border, clip, newTrack);
                }
                else
                {
                    border.TranslationY = 0.0;
                    if (Tracks.TryGetValue(newTrack, out var currentTrack))
                    {
                        try
                        {
                            foreach (var item in Tracks)
                            {
                                item.Value.Children.Remove(border); //avoid add a same view to 2 different container, cause crash
                            }
                            currentTrack.Children.Add(border);
                        }
                        catch (Exception ex)
                        {
                            Log(ex, $"re-add clip {cid} to track {newTrack}", this);
                        }
                    }
                }
                Clips[cid].origTrack = newTrack;
            }

        }


        Log($"{cid} moved to {border.TranslationX},{border.TranslationY} in track:{clip.origTrack} ");
        OnClipChanged?.Invoke(cid, new ClipUpdateEventArgs
        {
            SourceId = cid,
            Reason = ClipUpdateReason.ClipItselfMove
        });


        clip.MovingStatus = ClipMovingStatus.Free;
        CleanupGhostAndShadow();
        UpdateAdjacencyForTrack();
        SetStateOK();
        SetStatusText(Localized.DraftPage_EverythingFine);
#if ANDROID
        OverlayLayer.IsVisible = false;
#endif


    }

    private async Task ClipsTrackChanged(Border border, ClipElementUI clip, int newTrack)
    {
        await Dispatcher.DispatchAsync(async () =>
        {
            try
            {
                border.TranslationY = 0;
                Tracks[newTrack].Add(border);
                Clips[clip.Id].defaultY = border.TranslationY;
                Clips[clip.Id].Clip = border;
                // update adjacency after adding to new track
                UpdateAdjacencyForTrack(newTrack);
            }
            catch (Exception ex)
            {
                Log(ex, $"Set clip {clip.Id}", this);
                await DisplayAlert(Localized._Info, "Failed to set clip. Please reload draft.", Localized._OK);
            }
        });

    }

    private void InitMoveBetweenTracks(ClipElementUI clipElementUI, string cid, Border border)
    {
#if ANDROID
        OverlayLayer.IsVisible = true;
#endif
        border.Stroke = Colors.Green;
        Border ghostBorder = new Border
        {
            Stroke = border.Stroke,
            StrokeThickness = border.StrokeThickness,
            Background = new SolidColorBrush(Colors.DeepSkyBlue),
            WidthRequest = border.WidthRequest,
            HeightRequest = border.HeightRequest,
            StrokeShape = border.StrokeShape,
            //Shadow = new Shadow
            //{
            //    Brush = Colors.Black,
            //    Offset = new Point(20.0, 20.0),
            //    Radius = 7.5f,
            //    Opacity = 0.65f
            //}            
        };

        ClipElementUI ghostElement = new ClipElementUI
        {
            layoutX = clipElementUI.layoutX,
            layoutY = clipElementUI.layoutY,
            defaultY = clipElementUI.defaultY,
            Clip = ghostBorder
        };

        Clips[cid].ghostLayoutX = clipElementUI.layoutX;
        Clips[cid].ghostLayoutY = clipElementUI.layoutY;
        Clips.AddOrUpdate("ghost_" + cid, ghostElement, (string _, ClipElementUI _) => ghostElement);
        OverlayLayer.Add(ghostBorder);

        Border shadowBorder = new Border
        {
            Stroke = border.Stroke,
            StrokeThickness = border.StrokeThickness,
            Background = new SolidColorBrush(Colors.DeepSkyBlue),
            WidthRequest = border.WidthRequest,
            HeightRequest = border.HeightRequest,
            StrokeShape = border.StrokeShape,
            //Shadow = border.Shadow,
            Opacity = 0.45
        };

        ClipElementUI shadowElement = new ClipElementUI
        {
            Clip = shadowBorder,
            origTrack = 0
        };
        Clips.AddOrUpdate("shadow_" + cid, shadowElement, (string _, ClipElementUI _) => shadowElement);
    }

    #endregion

    #region resize clip
    private void LeftHandlePanded(object? sender, PanUpdatedEventArgs e)
    {
        if (sender is not Border border) return;
        if (border.BindingContext is not ClipElementUI clip) return;

        clip.MovingStatus = ClipMovingStatus.Resize;

        switch (e.StatusType)
        {
            case GestureStatus.Started:
                clip.handleLayoutX = border.TranslationX;
                clip.layoutX = clip.Clip.TranslationX;
                clip.Clip.BatchBegin();
                HandleStartWidth.AddOrUpdate(clip.Id, clip.Clip.WidthRequest, (_, __) => clip.Clip.WidthRequest);
                break;

            case GestureStatus.Running:
                double startWidth = HandleStartWidth.TryGetValue(clip.Id, out var sw) ? sw : clip.Clip.WidthRequest;
                double newWidth = Math.Max(MinClipWidth, startWidth - e.TotalX);
                if (clip.isInfiniteLength) goto go_resize;
                //Log($"Clip's new width {newWidth}, max width {FrameToPixel(clip.maxFrameCount)}");
                double lengthAvailable = FrameToPixel(clip.relativeStartFrame);
                bool reachStartOfSrc = !clip.isInfiniteLength && startWidth + lengthAvailable - newWidth > -0.5d;
                bool isLongerThanSrc = newWidth <= FrameToPixel(clip.maxFrameCount);
                if (!reachStartOfSrc || !isLongerThanSrc)
                {
                    clip.Clip.TranslationX = clip.layoutX + lengthAvailable;
                    SetStatusText(Localized.DraftPage_ReachLimit($"{clip.maxFrameCount * SecondsPerFrame}s"));
                    break;
                }

            go_resize:
                clip.Clip.TranslationX = clip.layoutX + e.TotalX;
                clip.Clip.WidthRequest = newWidth;
                SetStatusText(Localized.DraftPage_WaitForUser);
                break;

            case GestureStatus.Completed:
                HandleStartWidth.TryRemove(clip.Id, out _);
                clip.lengthInFrame = PixelToFrame(clip.Clip.Width);
                double deltaPx = clip.Clip.TranslationX - clip.layoutX;
                long deltaFrames = (long)Math.Round(deltaPx * FramePerPixel * tracksZoomOffest);
                long newRel = (long)clip.relativeStartFrame + deltaFrames;
                if (newRel < 0) newRel = 0;
                uint maxRelAllowed = (clip.maxFrameCount >= clip.lengthInFrame) ? (clip.maxFrameCount - clip.lengthInFrame) : 0u;
                if ((ulong)newRel > maxRelAllowed) newRel = maxRelAllowed;
                clip.relativeStartFrame = (uint)newRel;
                clip.Clip.BatchCommit();
                OnClipChanged?.Invoke(clip.Id, new ClipUpdateEventArgs
                {
                    SourceId = clip.Id,
                    Reason = ClipUpdateReason.ClipResized
                });
                clip.MovingStatus = ClipMovingStatus.Free;
                StatusLabel.Text = $"clip {clip.Id} resized. x:{border.TranslationX} width:{border.WidthRequest}";
                break;
        }
    }

    private void RightHandlePaned(object? sender, PanUpdatedEventArgs e)
    {
        if (sender is not Border border) return;
        if (border.BindingContext is not ClipElementUI clip) return;

        clip.MovingStatus = ClipMovingStatus.Resize;

        switch (e.StatusType)
        {
            case GestureStatus.Started:
                clip.handleLayoutX = border.TranslationX;
                clip.layoutX = clip.Clip.TranslationX;
                clip.Clip.BatchBegin();
                HandleStartWidth.AddOrUpdate(clip.Id, clip.Clip.WidthRequest, (_, __) => clip.Clip.WidthRequest);
                break;

            case GestureStatus.Running:
                double startWidth = HandleStartWidth.TryGetValue(clip.Id, out var sw) ? sw : clip.Clip.WidthRequest;
                double newWidth = Math.Max(MinClipWidth, startWidth + e.TotalX);
                bool isLongerThanSrc = newWidth + FrameToPixel(clip.relativeStartFrame) >= FrameToPixel(clip.maxFrameCount);
                //Log($"longer than src:{isLongerThanSrc} Clip need {PixelToFrame(newWidth)} frames/{newWidth}px, total {clip.maxFrameCount} frames/{FrameToPixel(clip.maxFrameCount)}px");

                if (clip.isInfiniteLength || !isLongerThanSrc)
                {
                    clip.Clip.WidthRequest = newWidth;
                    SetStatusText(Localized.DraftPage_WaitForUser);
                }
                else
                {
                    clip.Clip.WidthRequest = FrameToPixel(clip.maxFrameCount) - FrameToPixel(clip.relativeStartFrame);
                    SetStatusText(Localized.DraftPage_ReachLimit($"{clip.maxFrameCount * SecondsPerFrame}s"));
                }
                break;

            case GestureStatus.Completed:
                HandleStartWidth.TryRemove(clip.Id, out _);
                clip.Clip.BatchCommit();
                clip.lengthInFrame = PixelToFrame(clip.Clip.Width);
                OnClipChanged?.Invoke(clip.Id, new ClipUpdateEventArgs
                {
                    SourceId = clip.Id,
                    Reason = ClipUpdateReason.ClipResized
                });
                clip.MovingStatus = ClipMovingStatus.Free;
                StatusLabel.Text = $"clip {clip.Id} resized. x:{border.TranslationX} width:{border.WidthRequest}";

                break;
        }
    }
    #endregion

    #region adjust(?) track and clip
    class _RoundRectangleRadiusType
    {
        public double tl { get; set; }
        public double tr { get; set; }
        public double bl { get; set; }
        public double br { get; set; }
    }

    class _TrackClassForUpdateAdjacencyForTrack //why not use anonymous class? because of AOT on IOS/Mac!!!
    {
        public double Start { get; set; }
        public double End { get; set; }
        public ClipElementUI Clip { get; set; }
    }

    private void UpdateAdjacencyForTrack() => Tracks.Keys.ToList().ForEach((x) => UpdateAdjacencyForTrack(x));


    private void UpdateAdjacencyForTrack(int trackIndex)
    {
        if (!Tracks.TryGetValue(trackIndex, out var track)) return;
        var byorder = track.Children.OfType<Border>()
            .Select(b => b.BindingContext)
            .OfType<ClipElementUI>()
            .Select(c =>
            {
                double start = Math.Round(c.Clip.X + c.Clip.TranslationX);
                double width = (!double.IsNaN(c.Clip.Width) && c.Clip.Width > 0) ? Math.Round(c.Clip.Width) : Math.Round(c.Clip.WidthRequest);
                double end = start + width;
                return new _TrackClassForUpdateAdjacencyForTrack { Start = start, End = end, Clip = c };
            })
            .OrderBy(t => t.Start)
            .ToList();

        const double defaultRadius = 20.0;

        // Use a local radius array to avoid races between concurrent UpdateAdjacencyForTrack calls
        var localRadius = new _RoundRectangleRadiusType[byorder.Count];

        for (int i = 0; i < byorder.Count; i++)
        {
            localRadius[i] = new _RoundRectangleRadiusType { tl = defaultRadius, tr = defaultRadius, br = defaultRadius, bl = defaultRadius };
        }

        foreach (var item in byorder)
        {
            try { item.Clip.Clip.StrokeShape = new RoundRectangle { CornerRadius = new Microsoft.Maui.CornerRadius(defaultRadius) }; } catch { }
        }

        double tol = 3;

        for (int i = 0; i < byorder.Count; i++)
        {
            var self = byorder[i];
            var left = (i > 0) ? byorder[i - 1] : null;
            var right = (i < byorder.Count - 1) ? byorder[i + 1] : null;

            if (i > 0)
            {
                if (Math.Abs(left.End - self.Start) <= tol) //left
                {
                    localRadius[i].tl = 0;
                    localRadius[i].br = 0;
                    localRadius[i - 1].tr = 0;
                    localRadius[i - 1].bl = 0;
                }
            }

            if (i < byorder.Count - 1)
            {
                if (Math.Abs(right.Start - self.End) <= tol)  //right
                {
                    localRadius[i].tr = 0;
                    localRadius[i].bl = 0;
                    localRadius[i + 1].tl = 0;
                    localRadius[i + 1].br = 0;
                }
            }



        }

        foreach (var item in byorder)
        {
            var i = byorder.IndexOf(item);
            // capture radius locally to avoid races when dispatcher runs later
            var r = localRadius[i];
            Dispatcher.Dispatch(() =>
            {
                try
                {

                    item.Clip.Clip.StrokeShape = new RoundRectangle
                    {
                        CornerRadius =
                    new Microsoft.Maui.CornerRadius(r.tl, r.tr, r.br, r.bl)
                    };
                }
                catch (Exception e)
                {
                    Log(e, "update round rectangle", this);
                    SetStateFail("Failed to update clip border.");
                }
            });

        }
    }

    public void CleanupGhostAndShadow()
    {
        // Only remove temporary ghost/shadow entries created during drag operations.
        // Previously this removed any entry whose key wasn't a GUID, which deleted
        // imported clips that used IDs like "clip1". That caused all clips to disappear
        // after any drag operation. Only remove keys prefixed with the temporary markers.
        var keysToRemove = Clips.Keys.Where(k => k != null && (k.StartsWith("ghost_") || k.StartsWith("shadow_"))).ToList();
        foreach (var key in keysToRemove)
        {
            if (Clips.TryRemove(key, out var removed))
            {
                try
                {
                    // remove visual from overlay if present
                    if (removed?.Clip != null)
                    {
                        try { OverlayLayer?.Children.Remove(removed.Clip); } catch { }
                        // also remove from any track containers just in case
                        foreach (var t in Tracks.Values.ToList())
                            try { t.Children.Remove(removed.Clip); } catch { }
                    }
                }
                catch { }
            }
        }
    }
    #endregion

    #region popup

    private async Task ShowClipPopup(Border clipBorder, ClipElementUI clip)
    {
#if ANDROID
        OverlayLayer.IsVisible = true;
#endif
        var existing = OverlayLayer.Children.FirstOrDefault(c => (c as VisualElement)?.StyleId == "ClipPopupFrame" || (c as VisualElement)?.StyleId == "ClipPopupTriangle");
        if (existing != null)
        {
            var toRemove = OverlayLayer.Children.Where(c => (c as VisualElement)?.StyleId == "ClipPopupFrame" || (c as VisualElement)?.StyleId == "ClipPopupTriangle").ToList();
            foreach (var r in toRemove)
                OverlayLayer.Children.Remove(r);
        }

        OverlayLayer.InputTransparent = false;

        double desiredPopupWidth = 500;
        double desiredPopupHeight = 400;
        double arrowSize = 20;
        double spacing = 8;
        double minPopupWidth = 200;
        double minPopupHeight = 120;
        double margin = 8;

        Point clipAbs = GetAbsolutePosition(clipBorder, null);
        Point overlayAbs = GetAbsolutePosition(OverlayLayer, null);

        int retries = 0;
        while ((OverlayLayer.Width <= 0 || OverlayLayer.Height <= 0 || double.IsNaN(clipAbs.Y) || clipAbs.Y <= 0) && retries < 6)
        {
            await Task.Delay(30);
            clipAbs = GetAbsolutePosition(clipBorder, null);
            overlayAbs = GetAbsolutePosition(OverlayLayer, null);
            retries++;
        }

        double cumulativeScrollY = 0;
        VisualElement? parent = clipBorder.Parent as VisualElement;
        while (parent != null && parent != OverlayLayer)
        {
            if (parent is ScrollView sv)
            {
                cumulativeScrollY += sv.ScrollY;
            }
            parent = parent.Parent as VisualElement;
        }
        double clipWidth = (clipBorder.Width > 0) ? clipBorder.Width : clipBorder.WidthRequest;
        double clipHeight = (clipBorder.Height > 0) ? clipBorder.Height : clipBorder.HeightRequest;

        Point abs = new Point(clipAbs.X - overlayAbs.X, clipAbs.Y - overlayAbs.Y - cumulativeScrollY);

        // for fallback 
        double overlayW = OverlayLayer.Width > 0 ? OverlayLayer.Width : this.Width;
        double overlayH = OverlayLayer.Height > 0 ? OverlayLayer.Height : this.Height;
        if (double.IsNaN(overlayW) || overlayW <= 0) overlayW = 1000;
        if (double.IsNaN(overlayH) || overlayH <= 0) overlayH = 1000;

        double availableBelow = overlayH - (abs.Y + clipHeight) - spacing - arrowSize - margin;
        double availableAbove = abs.Y - spacing - arrowSize - margin;
        if (availableBelow < 0) availableBelow = 0;
        if (availableAbove < 0) availableAbove = 0;

        double popupWidth = Math.Min(desiredPopupWidth, Math.Max(minPopupWidth, overlayW - margin * 2));
        double popupHeight;
        bool popupBelow;

        if (availableBelow >= desiredPopupHeight)
        {
            popupBelow = true;
            popupHeight = desiredPopupHeight;
        }
        else if (availableAbove >= desiredPopupHeight)
        {
            popupBelow = false;
            popupHeight = desiredPopupHeight;
        }
        else
        {
            if (availableBelow >= availableAbove)
            {
                popupBelow = true;
                popupHeight = Math.Max(minPopupHeight, Math.Min(desiredPopupHeight, availableBelow));
            }
            else
            {
                popupBelow = false;
                popupHeight = Math.Max(minPopupHeight, Math.Min(desiredPopupHeight, availableAbove));
            }

            popupHeight = Math.Min(popupHeight, Math.Max(minPopupHeight, overlayH - margin * 2));
        }

        double clipCenterX = abs.X + (clipWidth / 2.0);
        double popupX = clipCenterX - (popupWidth / 2.0);
        if (popupX < margin) popupX = margin;
        if (popupX + popupWidth + margin > overlayW) popupX = Math.Max(margin, overlayW - popupWidth - margin);

        double popupY;
        if (popupBelow)
        {
            popupY = abs.Y + clipHeight + spacing + arrowSize;
            if (popupY + popupHeight + margin > overlayH)
            {
                popupY = Math.Max(margin, overlayH - popupHeight - margin);
            }
        }
        else
        {
            popupY = abs.Y - popupHeight - arrowSize - spacing;
            if (popupY < margin)
            {
                popupY = margin;
            }
        }

        double triangleLeft = clipCenterX - (arrowSize / 2.0);
        double triangleMin = popupX + 6;
        double triangleMax = popupX + popupWidth - arrowSize - 6;
        triangleLeft = Math.Clamp(triangleLeft, triangleMin, triangleMax);
        double triangleTop;

        Polygon triangle;
        if (popupBelow)
        {
            triangle = new Polygon
            {
                StyleId = "ClipPopupTriangle",
                Fill = Colors.Grey,
                Points = new PointCollection
                {
                    new Point(0, arrowSize),
                    new Point(arrowSize / 2.0, 0),
                    new Point(arrowSize, arrowSize)
                }
            };
            triangleTop = popupY - arrowSize;
        }
        else
        {
            triangle = new Polygon
            {
                StyleId = "ClipPopupTriangle",
                Fill = Colors.Grey,
                Points = new PointCollection
                {
                    new Point(0, 0),
                    new Point(arrowSize / 2.0, arrowSize),
                    new Point(arrowSize, 0)
                },
                Opacity = 0.75
            };
            triangleTop = popupY + popupHeight;
        }

        AbsoluteLayout.SetLayoutBounds(triangle, new Rect(triangleLeft, triangleTop, arrowSize, arrowSize));

        var frame = new Border
        {
            StyleId = "ClipPopupFrame",
            Background = new SolidColorBrush(Colors.Grey),
            Stroke = Colors.Black,
            StrokeThickness = 1,
            StrokeShape = new RoundRectangle { CornerRadius = 4 },
            Padding = new Thickness(2),
            Opacity = 0.95,
            Content = new ScrollView { Content = BuildPropertyPanel(clip) }
        };

        frame.GestureRecognizers.Add(nopGesture);

        AbsoluteLayout.SetLayoutBounds(frame, new Rect(popupX, popupY, popupWidth, popupHeight));

        frame.Opacity = 0;
        frame.Scale = 0.95;
        frame.TranslationY = 5;

        triangle.Opacity = 0;
        triangle.Scale = 0.95;
        triangle.TranslationY = 5;

        OverlayLayer.Children.Add(frame);
        OverlayLayer.Children.Add(triangle);

        const uint entranceMs = 220u;
        try
        {
            await Task.WhenAll(
                frame.FadeTo(0.9, entranceMs, Easing.CubicOut),
                frame.ScaleTo(1, entranceMs, Easing.CubicOut),
                frame.TranslateTo(0, 0, entranceMs, Easing.CubicOut),
                triangle.FadeTo(0.9, entranceMs, Easing.CubicOut),
                triangle.ScaleTo(1, entranceMs, Easing.CubicOut),
                triangle.TranslateTo(0, 0, entranceMs, Easing.CubicOut)
            );
        }
        catch { }
    }

    private async Task HidePopup()
    {
        OverlayLayer.InputTransparent = true;
        await Task.WhenAll(HideClipPopup(), HideFullscreenPopup());
#if ANDROID
        OverlayLayer.IsVisible = false;
#endif
    }

    private async Task HideClipPopup()
    {
        var toRemove = OverlayLayer.Children.Where(c => (c as VisualElement)?.StyleId == "ClipPopupFrame" || (c as VisualElement)?.StyleId == "ClipPopupTriangle").ToList();

        // Animate out (run all animations in parallel)
        const uint exitMs = 220u;
        var visuals = toRemove.OfType<VisualElement>().ToList();
        var tasks = new List<Task>();
        foreach (var v in visuals)
        {
            try
            {
                var t = Task.WhenAll(
                    v.FadeTo(0, exitMs, Easing.CubicIn),
                    v.ScaleTo(0.95, exitMs, Easing.CubicIn),
                    v.TranslateTo(0, 10, exitMs, Easing.CubicIn)
                );
                tasks.Add(t);
            }
            catch { }
        }
        try { await Task.WhenAll(tasks); } catch { }

        foreach (var r in toRemove)
            OverlayLayer.Children.Remove(r);
        OverlayLayer.InputTransparent = true;

    }


    private async void FullscreenFlyoutTestButton_Clicked(object sender, EventArgs e)
    {
        await ShowAFullscreenPopupInRight(WindowSize.Height * 0.75, BuildPropertyPanel(_selected ?? new()));
    }

    private async Task ShowAFullscreenPopupInBottom(double height, View content)
    {
#if ANDROID
        OverlayLayer.IsVisible = true;
#endif
        var size = WindowSize;

        Popup = new Border
        {
            WidthRequest = size.Width - 40,
            HeightRequest = height,
            TranslationX = 15,
            TranslationY = size.Height + 10,
            Background = new SolidColorBrush(Colors.Grey),

            StrokeShape = new RoundRectangle
            {
                CornerRadius = 8,
                StrokeThickness = 8
            },
#if !ANDROID
            Shadow = new Shadow
            {
                Brush = Colors.Black,
                Opacity = 1f,
                Radius = 3
            },
#endif
            Padding = 12,
            Content = new ScrollView { Content = content },
            Opacity = 0.95
        };
        OverlayLayer.InputTransparent = false;
        Popup.GestureRecognizers.Add(nopGesture);

        OverlayLayer.Add(Popup);

        var targetY = height;
        try
        {
            await Popup.TranslateTo(Popup.TranslationX, size.Height - targetY, 300, Easing.SinOut);
        }
        catch { }
    }

    private async Task ShowAFullscreenPopupInRight(double width, View content)
    {
#if ANDROID
        OverlayLayer.IsVisible = true;
#endif
        var size = WindowSize;

        Popup = new Border
        {
            WidthRequest = size.Width - width,
            HeightRequest = size.Height * 0.85,
            TranslationX = size.Width + 20,
            TranslationY = 20,
            Background = new SolidColorBrush(Colors.Grey),

            StrokeShape = new RoundRectangle
            {
                CornerRadius = 8,
                StrokeThickness = 8
            },
#if !ANDROID
            Shadow = new Shadow
            {
                Brush = Colors.Black,
                Opacity = 1f,
                Radius = 3
            },
#endif
            Padding = 12,
            Content = new ScrollView { Content = content },
            Opacity = 0.95
        };
        OverlayLayer.InputTransparent = false;
        Popup.GestureRecognizers.Add(nopGesture);

        OverlayLayer.Add(Popup);

        var targetX = width;
        try
        {
            await Popup.TranslateTo(width, Popup.TranslationY, 300, Easing.SinOut);
        }
        catch { }
    }

    private async Task HideFullscreenPopup()
    {
        var size = WindowSize;

        try
        {
            await Popup.TranslateTo(Popup.TranslationX, size.Height + 10, 300, Easing.SinIn);
        }
        catch { }

        OverlayLayer.Remove(Popup);
        OverlayLayer.InputTransparent = true;

    }

    #endregion

    #region misc

    private async void AssetPanelButton_Clicked(object sender, EventArgs e)
    {
        var newClip = CreateClip(
            startX: 0,
            width: 150,
            trackIndex: Tracks.Last().Key,
            id: null,
            labelText: $"Clip {Clips.Count + 1}",
            background: new SolidColorBrush(Colors.CornflowerBlue),
            prototype: null);
        RegisterClip(newClip, true);

        var layout = new VerticalStackLayout
        {
            Children =
            {
                newClip.Clip
            }
        };

        await ShowAFullscreenPopupInRight(500, layout);
    }

    private void OnExportedClick(object sender, EventArgs e)
    {

    }

    private async void SettingsClick(object sender, EventArgs e)
    {
        await Navigation.PushAsync(new ContentPage
        {
            Content = new ScrollView
            {
                Content = BuildPropertyPanel(_selected ?? new())
            }
        });
    }

    [DebuggerNonUserCode()]
    public uint PixelToFrame(double px) => (uint)(px * FramePerPixel * tracksZoomOffest);
    [DebuggerNonUserCode()]
    public double FrameToPixel(uint f) => f / (FramePerPixel * tracksZoomOffest);

    private View BuildPropertyPanel(ClipElementUI clip)
    {
        return new Editor
        {
            Text = JsonSerializer.Serialize(clip, new JsonSerializerOptions
            {
                WriteIndented = true,
                NumberHandling = System.Text.Json.Serialization.JsonNumberHandling.AllowNamedFloatingPointLiterals,
            }),
            IsReadOnly = true,
            HeightRequest = 300,
        };

        /*return new PropertyPanelBuilder()
        .AddText(new TitleAndDescriptionLineLabel("ppb Test", "a example of PropertyPanelBuilder", 32))
        .AddText("This is a test", fontSize: 16, fontAttributes: FontAttributes.Bold)
        .AddEntry("testEntry", "Test Entry:", "text", "Enter something...", EntrySeter: (entry) =>
        {
            entry.WidthRequest = 200;
        })
        .AddSlider("testSlider", "Test Slider:", 0, 100, 50)
        .AddSeparator(null)
        .AddCheckbox("testCheckbox", "Test Checkbox:", false)
        .AddSwitch("testSwitch", "Test Switch:", true)
        .AddSeparator(null)
        .AddButton("testButton", "Test button 1", "Click me!")
        .AddCustomChild("pick a date", (p, c) =>
        {
            var picker = new DatePicker
            {
                WidthRequest = 200,
                Date = DateTime.Now,
                BindingContext = p
            };
            picker.DateSelected += (s, e) => c(e.NewDate.ToString("G"));
            return picker;
        }, "testDatePicker", DateTime.Now.ToString("G"))

        .AddChildrensInALine("a line", (c) => c.AddText("11").AddEntry("testInput1", "Input 1", "").AddText("22").AddCheckbox("testCheckbox2", false))
        .AddSeparator()
        .AddCustomChild(new Rectangle
        {
            WidthRequest = 100,
            HeightRequest = 500,
            Fill = Colors.Green
        })
        .ListenToChanges(async (s, e) =>
        {
            SetStatusText($"Property '{e.Id}' changed from '{e.OriginValue}' to '{e.Value}'");
        })
        .Build();*/


    }

    private async void AddWebViewClip(int trackIndex, string clipId, string text, int start, int duration)
    {
        var script = $"window.addClip({trackIndex}, '{clipId}', '{text}', {start}, {duration});";
        await hybridWebView.EvaluateJavaScriptAsync(script);
    }

    private void MoveLeftButton_Clicked(object sender, EventArgs e)
    {
        foreach (var item in Tracks)
        {
            foreach (var border in item.Value.Children)
            {
                if (border is Border b)
                {
                    b.TranslationX -= 50;
                }
            }
        }

        tracksViewOffset -= 50;
    }

    private void MoveRightButton_Clicked(object sender, EventArgs e)
    {
        foreach (var item in Tracks)
        {
            foreach (var border in item.Value.Children)
            {
                if (border is Border b)
                {
                    b.TranslationX += 50;
                }
            }
        }

        tracksViewOffset += 50;
    }

    private void ZoomOutButton_Clicked(object sender, EventArgs e)
    {
        foreach (var item in Tracks)
        {
            foreach (var border in item.Value.Children)
            {
                if (border is Border b)
                {
                    b.WidthRequest *= 1.5;

                    b.TranslationX += b.WidthRequest * 1.5;

                }
            }
        }

        tracksViewOffset *= 1.5;
        tracksZoomOffest *= 1.5;

    }

    private void ZoomInButton_Clicked(object sender, EventArgs e)
    {
        foreach (var item in Tracks)
        {
            foreach (var border in item.Value.Children)
            {
                if (border is Border b)
                {
                    b.WidthRequest /= 1.5;

                    b.TranslationX -= b.WidthRequest * 1.5;

                }
            }
        }

        tracksViewOffset /= 1.5;
        tracksZoomOffest /= 1.5;
        Log(new NullReferenceException(), "111", this);
    }

    private Size GetScreenSizeInDp()
    {
        var info = DeviceDisplay.MainDisplayInfo;
        double widthDp = info.Width / info.Density;
        double heightDp = info.Height / info.Density;
        return new Size(widthDp, heightDp);
    }

    protected override async void OnAppearing()
    {
        base.OnAppearing();
        MyLoggerExtensions.OnExceptionLog += MyLoggerExtensions_OnExceptionLog;

        var size = GetScreenSizeInDp();
        Log($"Window size on appearing: {size.Width:F0} x {size.Height:F0} (DIP)");
        // 短等待，确保布局完成
        await Task.Delay(50);

        var w = this.Window?.Width ?? 0;
        var h = this.Window?.Height ?? 0;
        WindowSize = new Size(w, h);
    }

    private void MyLoggerExtensions_OnExceptionLog(Exception obj)
    {
        Dispatcher.Dispatch(() =>
        {
            StatusLabel.TextColor = Colors.Red;
            StatusLabel.Text = Localized._ExceptionTemplate(obj);
        });
    }

    protected override void OnDisappearing()
    {
        base.OnDisappearing();
        MyLoggerExtensions.OnExceptionLog -= MyLoggerExtensions_OnExceptionLog;


        HidePopup();

        if (this.Window is not null)
        {
            this.Window.SizeChanged -= Window_SizeChanged;
        }
    }

    private void Window_SizeChanged(object? sender, EventArgs e)
    {
        double w = this.Window?.Width ?? 0;
        double h = this.Window?.Height ?? 0;
        WindowSize = new(w, h);
        Log($"Window size changed: {w:F0} x {h:F0} (DIP)");

    }

    private Point GetAbsolutePosition(VisualElement element, VisualElement ancestor)
    {
        double x = element.X + element.TranslationX;
        double y = element.Y + element.TranslationY;

        VisualElement? parent = element.Parent as VisualElement;
        while (parent != null && parent != ancestor)
        {
            x += parent.X + parent.TranslationX;
            y += parent.Y + parent.TranslationY;
            parent = parent.Parent as VisualElement;
        }

        return new Point(x, y);
    }

    private double SnapPixels(double x)
    {
        if (!SnapEnabled) return Math.Max(0, x);
        double best = x;
        double bestDist = SnapThresholdPixels + 1;

        // 1) grid
        var grid = Math.Round(x / SnapGridPixels) * SnapGridPixels;
        var d = Math.Abs(grid - x);
        if (d < bestDist && d <= SnapThresholdPixels)
        {
            best = grid; bestDist = d;
        }

        // 2) edges of other clips
        foreach (var kv in Clips)
        {
            var key = kv.Key;
            if (string.IsNullOrEmpty(key)) continue;
            if (key.StartsWith("ghost_") || key.StartsWith("shadow_")) continue;
            var b = kv.Value.Clip;
            if (b == null) continue;
            double bx = b.TranslationX;
            double bw = (!double.IsNaN(b.Width) && b.Width > 0) ? b.Width : b.WidthRequest;
            // prefer integer pixel edges
            bx = Math.Round(bx);
            bw = Math.Round(bw);
            var startEdge = bx;
            var endEdge = bx + bw;
            var ds = Math.Abs(startEdge - x);
            if (ds < bestDist && ds <= SnapThresholdPixels) { best = startEdge; bestDist = ds; }
            var de = Math.Abs(endEdge - x);
            if (de < bestDist && de <= SnapThresholdPixels) { best = endEdge; bestDist = de; }
        }

        // clamp and round to integer pixel to avoid sub-pixel gaps
        return Math.Max(0, Math.Round(best));
    }

    private readonly double LeftOverlapDelta = 4.2d;
    private readonly double RightOverlapDelta = 3.2d;

    private double ResolveOverlapStartPixels(int trackIndex, string selfId, double startX, double width)
    {
        // Try to find a non-overlapping X on the given track by shifting left/right
        if (!Tracks.TryGetValue(trackIndex, out var trackLayout))
            return startX;

        // use rounded start to avoid fractional pixel gaps
        double s = startX;
        const double eps = 1e-3;
        for (int i = 0; i < 16; i++)
        {
            double end = s + width;
            var overlappers = trackLayout.Children
                .OfType<Border>()
                .Where(b =>
                {
                    // find clip id for this border
                    var pair = Clips.FirstOrDefault(kv => kv.Value.Clip == b);
                    if (pair.Key == null) return false;
                    if (pair.Key == selfId) return false;
                    // skip ghost/shadow
                    if (pair.Key.StartsWith("ghost_") || pair.Key.StartsWith("shadow_")) return false;
                    double bx = Math.Round(b.TranslationX);
                    double bw = (!double.IsNaN(b.Width) && b.Width > 0) ? Math.Round(b.Width) : Math.Round(b.WidthRequest);
                    // overlap check using integer pixels
                    return Math.Max(s, bx) < Math.Min(end, bx + bw);
                })
                .ToList();

            if (overlappers.Count == 0) break;

            // compute integer-edge candidates
            double rightCandidate = overlappers.Max(b => Math.Round(b.TranslationX) + (((!double.IsNaN(b.Width) && b.Width > 0) ? Math.Round(b.Width) : Math.Round(b.WidthRequest))));
            double leftCandidate = overlappers.Min(b => Math.Round(b.TranslationX)) - Math.Round(width);
            if (leftCandidate < 0) leftCandidate = 0;

            // prefer tight adjacency to the right (no gap) to eliminate visible gaps
            // however if shifting left yields strictly smaller movement and does not overlap, pick it
            double moveRight = Math.Abs(rightCandidate - s);
            double moveLeft = Math.Abs(s - leftCandidate);

            // check if leftCandidate would overlap (using integer math)
            bool leftOverlaps = trackLayout.Children
                .OfType<Border>()
                .Any(b =>
                {
                    var pair = Clips.FirstOrDefault(kv => kv.Value.Clip == b);
                    if (pair.Key == null) return false;
                    if (pair.Key == selfId) return false;
                    if (pair.Key.StartsWith("ghost_") || pair.Key.StartsWith("shadow_")) return false;
                    double bx = Math.Round(b.TranslationX);
                    double bw = (!double.IsNaN(b.Width) && b.Width > 0) ? Math.Round(b.Width) : Math.Round(b.WidthRequest);
                    return Math.Max(leftCandidate, bx) < Math.Min(leftCandidate + Math.Round(width), bx + bw);
                });

            if (!leftOverlaps && moveLeft < moveRight)
            {
                s = leftCandidate + LeftOverlapDelta;
            }
            else
            {
                // default to tight adjacency on right to avoid gaps
                s = rightCandidate - RightOverlapDelta;
            }
        }

        // final rounding
        return Math.Max(0, s);
    }

    private void HybridWebView_RawMessageReceived(object? sender, HybridWebViewRawMessageReceivedEventArgs e)
    {
        // todo
        Debug.WriteLine($"Message from JS: {e.Message}");
    }

    private void ViewSelector_CheckedChanged(object sender, CheckedChangedEventArgs e)
    {
        if (!e.Value) return;

        if (sender == NativeViewRadio)
        {
            NativeView.IsVisible = true;
            hybridWebView.IsVisible = false;
        }
        else if (sender == WebViewRadio)
        {
            NativeView.IsVisible = false;
            hybridWebView.IsVisible = true;
        }
    }
    #endregion

    #region status
    public void SetStateBusy()
    {
        if (StateIndicator is null) return;
        Dispatcher.Dispatch(() =>
        {
            StateIndicator.Children.Clear();
            StateIndicator.Children.Add(new ActivityIndicator
            {
                Color = Colors.Orange,
                IsRunning = true,
                WidthRequest = 16,
                HeightRequest = 16,
                Margin = new(6, 3, 0, 0)
            });
        });
    }

    public void SetStateOK()
    {
        if (StateIndicator is null) return;
        Dispatcher.Dispatch(() =>
        {
            StateIndicator.Children.Clear();
            StateIndicator.Children.Add(new Microsoft.Maui.Controls.Shapes.Path
            {
                Stroke = Colors.Green,
                StrokeThickness = 3,
                Data = (Geometry)new PathGeometryConverter().ConvertFromInvariantString("M 4,12 L 9,17 L 20,6"),
                WidthRequest = 20,
                HeightRequest = 20,
                Margin = new Thickness(2, -1, 0, 0)


            });
        });

    }

    public void SetStateFail()
    {
        if (StateIndicator is null) return;
        Dispatcher.Dispatch(() =>
        {
            StateIndicator.Children.Clear();
            StateIndicator.Children.Add(new Microsoft.Maui.Controls.Shapes.Path
            {
                Stroke = Colors.Red,
                StrokeThickness = 3,
                Data = (Geometry)new PathGeometryConverter().ConvertFromInvariantString("M 4,4 L 20,20 M 20,4 L 4,20"),
                WidthRequest = 20,
                HeightRequest = 20,
                Margin = new Thickness(0, -3, 0, 0)


            });
        });

    }

    private void SetStateFail(string text)
    {
        SetStateFail();
        Dispatcher.Dispatch(() =>
        {
            StatusLabel.TextColor = Colors.Red;
            StatusLabel.Text = text;

        });
        Log(text, "UI err");
    }


    public void SetStatusText(string text)
    {
        Dispatcher.Dispatch(() =>
        {
            StatusLabel.TextColor = Colors.White;
            StatusLabel.Text = text;

        });
        if (LogUIMessageToLogger) Log(text, "UI msg");
    }



    #endregion

    #region track interface
    private void PlayheadTapped(object? sender, TappedEventArgs e)
    {
        try
        {
            // Get tap position relative to the ruler
            var p = e.GetPosition(RulerLayout);
            if (p is null) return;

            // Convert to OverlayLayer coordinate space
            Point rulerAbs = GetAbsolutePosition(RulerLayout, null);
            Point overlayAbs = GetAbsolutePosition(OverlayLayer, null);
            double xInOverlay = rulerAbs.X - overlayAbs.X + p.Value.X;

            // Optional: snap to grid/clip edges using existing logic
            double snappedX = SnapPixels(xInOverlay);

            // Clamp to overlay bounds
            double overlayWidth = OverlayLayer.Width > 0 ? OverlayLayer.Width : this.Width;
            double playheadWidth = (PlayheadLine.Width > 0) ? PlayheadLine.Width : PlayheadLine.WidthRequest;
            if (double.IsNaN(overlayWidth) || overlayWidth <= 0) overlayWidth = 0;
            double clampedX = Math.Clamp(snappedX, 0, Math.Max(0, overlayWidth - playheadWidth));

            PlayheadLine.TranslationX = clampedX;
            SetStatusText($"Playhead moved to {clampedX:F0}px");
        }
        catch (Exception ex)
        {
            Log($"PlayheadTapped error: {ex}");
        }
    }

    #endregion


}
