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
using System.Threading.Tasks;
using static System.Net.Mime.MediaTypeNames;

namespace projectFrameCut;

public partial class DraftPage : ContentPage
{
    public event EventHandler<ClipUpdateEventArgs>? OnClipChanged;

    public const int ClipHeight = 62;
    private const double MinClipWidth = 30.0;

    ConcurrentDictionary<string, ClipElementUI> Clips = new();
    ConcurrentDictionary<int, AbsoluteLayout> Tracks = new();
    ConcurrentDictionary<string, double> HandleStartWidth = new();

    ClipElementUI? _selected = null;

    //avoid when you click on blank space of popup, popup hides
    TapGestureRecognizer nopGesture = new();

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

    _RoundRectangleRadiusType[] RoundRectangleRadius;

    public bool ShowShadow { get; private set; } = true;
    public bool LogUIMessageToLogger { get; private set; } = true;

    public DraftPage()
    {
        InitializeComponent();
#if ANDROID
        OverlayLayer.IsVisible = false; 
#endif


        //hybridWebView.DefaultFile = "index.html";
        //hybridWebView.HybridRoot = "WebClipView";
        //hybridWebView.RawMessageReceived += HybridWebView_RawMessageReceived;
        TrackCalculator.HeightPerTrack = ClipHeight;

        SetStateBusy();

        nopGesture.Tapped += (s, e) =>
        {

        };

        Loaded += (s, e) =>
        {
            AddTrackButton_Clicked(new(), e);
            AddClip_Clicked(new(), e);
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
            Log($"Clips changed, Total clips: {Clips.Count}");
            foreach (var item in Clips)
            {
                Log($"Clip@{item.Key}");
            }
        };

        if (this.Window is not null)
        {
            this.Window.SizeChanged += Window_SizeChanged;
        }
    }


    


    #region add stuff
    private void AddTrackButton_Clicked(object sender, EventArgs e)
    {
        if (WebViewRadio.IsChecked)
        {
            webViewTrackCount++;
            hybridWebView.EvaluateJavaScriptAsync("createTrack()");
            return;
        }

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

        int currentTrack = int.Parse(Tracks.Count.ToString());

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

        Tracks.AddOrUpdate(trackCount, track, (int _, AbsoluteLayout _) => track);

        Border content = new Border
        {
            Content = Tracks[trackCount],
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

        var cid = Guid.NewGuid().ToString();

        var element = new ClipElementUI
        {
            Id = cid,
            layoutX = 0,
            layoutY = 0,
            Clip = new Border
            {
                Stroke = Colors.Gray,
                StrokeThickness = 2,
                Background = new SolidColorBrush(Colors.CornflowerBlue),
                WidthRequest = 150,
                HeightRequest = 62,
                TranslationX = ResolveOverlapStartPixels(Tracks.Last().Key, cid, 0, 150),
                StrokeShape = new RoundRectangle
                {
                    CornerRadius = 20,
                    BackgroundColor = Colors.White,
                    StrokeThickness = 8
                },

                //Shadow = new Shadow
                //{
                //    Brush = Colors.Black,
                //    Opacity = 0.3f,
                //    Offset = new Point(4, 4),
                //    Radius = 6
                //},
            },
            LeftHandle = new Border
            {
                Stroke = Colors.Gray,
                StrokeThickness = 2,
                Background = new SolidColorBrush(Colors.White),
                WidthRequest = 10,
                HeightRequest = 30,

                HorizontalOptions = LayoutOptions.Center,
                VerticalOptions = LayoutOptions.Center,
                StrokeShape = new RoundRectangle
                {
                    CornerRadius = 20,
                    BackgroundColor = Colors.White,
                },

                //Shadow = new Shadow
                //{
                //    Brush = Colors.Black,
                //    Opacity = 0.7f,
                //    Offset = new Point(4, 4),
                //    Radius = 6
                //},
            },
            RightHandle = new Border
            {
                Stroke = Colors.Gray,
                StrokeThickness = 2,
                Background = new SolidColorBrush(Colors.White),
                WidthRequest = 10,
                HeightRequest = 30,

                HorizontalOptions = LayoutOptions.Center,
                VerticalOptions = LayoutOptions.Center,
                StrokeShape = new RoundRectangle
                {
                    CornerRadius = 20,
                    BackgroundColor = Colors.White,
                },
                //Shadow = new Shadow
                //{
                //    Brush = Colors.Black,
                //    Opacity = 0.7f,
                //    Offset = new Point(4, 4),
                //    Radius = 6
                //},
            },
            maxFrameCount = 10000
        };
        var cont = new HorizontalStackLayout
        {
            Children =
            {
                new Label
                {
                    Text = $"Clip {Clips.Count + 1}"
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
                new ColumnDefinition { Width = new GridLength(12, GridUnitType.Absolute) },
                new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) },
                new ColumnDefinition { Width = new GridLength(14, GridUnitType.Absolute) }
            }
        };

        element.Clip.BindingContext = element;
        element.LeftHandle.BindingContext = element;
        element.RightHandle.BindingContext = element;

        Clips.AddOrUpdate(cid, element, (_, _) => element);

        var clipPanGesture = new PanGestureRecognizer();
        clipPanGesture.PanUpdated += ClipPaned;

        var RightHandleGesture = new PanGestureRecognizer();
        RightHandleGesture.PanUpdated += RightHandlePaned;

        var LeftHandleGesture = new PanGestureRecognizer();
        LeftHandleGesture.PanUpdated += LeftHandlePanded;

        var SelectTapGesture = new TapGestureRecognizer();
        SelectTapGesture.Buttons = ButtonsMask.Primary | ButtonsMask.Secondary;
        SelectTapGesture.Tapped += SelectTapGesture_Tapped;

        var DoubleTapGesture = new TapGestureRecognizer();
        DoubleTapGesture.NumberOfTapsRequired = 2;
        DoubleTapGesture.Tapped += DoubleTapGesture_Tapped;


        Clips[cid].Clip.GestureRecognizers.Add(clipPanGesture);
        Clips[cid].Clip.GestureRecognizers.Add(SelectTapGesture);
        Clips[cid].Clip.GestureRecognizers.Add(DoubleTapGesture);
        Clips[cid].LeftHandle.GestureRecognizers.Add(LeftHandleGesture);
        Clips[cid].RightHandle.GestureRecognizers.Add(RightHandleGesture);


        Tracks.Last().Value.Add(Clips[cid].Clip);
        // record original track and update adjacency
        Clips[cid].origTrack = Tracks.Keys.Max();

        //UpdateAdjacencyForClip(cid, Clips[cid].origTrack ?? Tracks.Keys.Max());
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
            Log($"ShowClipPopup error: {ex}");
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
        clip.MovingStatus = ClipMovingStatus.Move;
        clip.layoutX = border.TranslationX;
        clip.layoutY = border.TranslationY;
        clip.defaultY = border.TranslationY;
    }

    private void HandlePanRunning(PanUpdatedEventArgs e, Border border, ClipElementUI clip, string cid, int origTrack)
    {
        if (clip.MovingStatus != ClipMovingStatus.Free && clip.MovingStatus != ClipMovingStatus.Move) return;

        double xToBe = clip.layoutX + e.TotalX;
        double yToBe = clip.layoutY + e.TotalY;
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
            if(newTrack > trackCount + 1 || newTrack < 0) SetStatusText(Localized.DraftPage_ReleaseToRemove);
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
                                item.Value.Children.Remove(border); //avoid add a same view to 2 different container
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
        UpdateAdjacencyForTrack(clip.origTrack ?? TrackCalculator.CalculateWhichTrackShouldIn(border.TranslationY));
        SetStateOK();
        SetStatusText(Localized.DraftPage_EverythingFine);


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
                SetStateBusy();
                clip.handleLayoutX = border.TranslationX;
                clip.layoutX = clip.Clip.TranslationX;
                clip.Clip.BatchBegin();
                SetStatusText(Localized.DraftPage_WaitForUser);
                HandleStartWidth.AddOrUpdate(clip.Id, clip.Clip.WidthRequest, (_, __) => clip.Clip.WidthRequest);
                break;

            case GestureStatus.Running:
                double startWidth = HandleStartWidth.TryGetValue(clip.Id, out var sw) ? sw : clip.Clip.WidthRequest;
                double newWidth = Math.Max(MinClipWidth, startWidth - e.TotalX);
                if (clip.maxFrameCount is null || clip.maxFrameCount * SecondsPerFrame >= newWidth * FramePerPixel * tracksZoomOffest)
                {
                    clip.Clip.TranslationX = clip.layoutX + e.TotalX;
                    clip.Clip.WidthRequest = newWidth;
                    SetStatusText(Localized.DraftPage_WaitForUser);
                }
                else
                {
                    SetStatusText(Localized.DraftPage_ReachLimit($"{clip.maxFrameCount * SecondsPerFrame}s"));
                }
                    
                break;

            case GestureStatus.Completed:
                HandleStartWidth.TryRemove(clip.Id, out _);
                clip.Clip.BatchCommit();
                OnClipChanged?.Invoke(clip.Id, new ClipUpdateEventArgs
                {
                    SourceId = clip.Id,
                    Reason = ClipUpdateReason.ClipResized
                });
                // snap start X and resolve overlap on current track
                double finalX = SnapPixels(clip.Clip.TranslationX);
                int trackIdx = clip.origTrack ?? TrackCalculator.CalculateWhichTrackShouldIn(clip.Clip.TranslationY);
                double finalWidth = clip.Clip.WidthRequest;
                double resolvedX = ResolveOverlapStartPixels(trackIdx, clip.Id, finalX, finalWidth);
                clip.Clip.TranslationX = resolvedX;
                clip.MovingStatus = ClipMovingStatus.Free;
                StatusLabel.Text = $"clip {clip.Id} resized. x:{clip.Clip.TranslationX} width:{clip.Clip.WidthRequest}";
                SetStateOK();
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
                SetStateBusy();
                SetStatusText(Localized.DraftPage_WaitForUser);

                clip.handleLayoutX = border.TranslationX;
                clip.layoutX = clip.Clip.TranslationX;
                clip.Clip.BatchBegin();
                HandleStartWidth.AddOrUpdate(clip.Id, clip.Clip.WidthRequest, (_, __) => clip.Clip.WidthRequest);
                break;

            case GestureStatus.Running:
                double startWidth = HandleStartWidth.TryGetValue(clip.Id, out var sw) ? sw : clip.Clip.WidthRequest;
                double newWidth = Math.Max(MinClipWidth, startWidth + e.TotalX);
                if(clip.maxFrameCount is null || clip.maxFrameCount * SecondsPerFrame >= newWidth * FramePerPixel * tracksZoomOffest)
                {
                    clip.Clip.WidthRequest = newWidth;
                    SetStatusText(Localized.DraftPage_WaitForUser);
                }
                else
                {
                    SetStatusText(Localized.DraftPage_ReachLimit($"{clip.maxFrameCount * SecondsPerFrame}s"));
                }
                break;

            case GestureStatus.Completed:
                HandleStartWidth.TryRemove(clip.Id, out _);
                clip.Clip.BatchCommit();
                OnClipChanged?.Invoke(clip.Id, new ClipUpdateEventArgs
                {
                    SourceId = clip.Id,
                    Reason = ClipUpdateReason.ClipResized
                });
                // snap end and resolve overlap
                double finalXr = SnapPixels(clip.Clip.TranslationX);
                int trackIdr = clip.origTrack ?? TrackCalculator.CalculateWhichTrackShouldIn(clip.Clip.TranslationY);
                double finalW = clip.Clip.WidthRequest;
                double resolvedXr = ResolveOverlapStartPixels(trackIdr, clip.Id, finalXr, finalW);
                clip.Clip.TranslationX = resolvedXr;
                clip.MovingStatus = ClipMovingStatus.Free;
                StatusLabel.Text = $"clip {clip.Id} resized. x:{clip.Clip.TranslationX} width:{clip.Clip.WidthRequest}";
                UpdateAdjacencyForTrack(clip.origTrack ?? TrackCalculator.CalculateWhichTrackShouldIn(clip.Clip.TranslationY));
                SetStateOK();

                break;
        }
    }
    #endregion

    #region adjust(?) clip
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

        RoundRectangleRadius = new _RoundRectangleRadiusType[byorder.Count];

        for (int i = 0; i < byorder.Count; i++)
        {
            RoundRectangleRadius[i] = new _RoundRectangleRadiusType { tl = defaultRadius, tr = defaultRadius, br = defaultRadius, bl = defaultRadius };
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
                    RoundRectangleRadius[i].tl = 0;
                    RoundRectangleRadius[i].br = 0;
                    RoundRectangleRadius[i - 1].tr = 0;
                    RoundRectangleRadius[i - 1].bl = 0;
                }
            }

            if (i < byorder.Count - 1)
            {
                if (Math.Abs(right.Start - self.End) <= tol)  //right
                {
                    RoundRectangleRadius[i].tr = 0;
                    RoundRectangleRadius[i].bl = 0;
                    RoundRectangleRadius[i + 1].tl = 0;
                    RoundRectangleRadius[i + 1].br = 0;
                }
            }



        }

        foreach (var item in byorder)
        {
            var i = byorder.IndexOf(item);
            item.Clip.Clip.StrokeShape = new RoundRectangle
            {
                CornerRadius =
                new Microsoft.Maui.CornerRadius(RoundRectangleRadius[i].tl, RoundRectangleRadius[i].tr, RoundRectangleRadius[i].br, RoundRectangleRadius[i].bl)
            };

        }
    }
    #endregion

    #region popup

    private async Task ShowClipPopup(Border clipBorder, ClipElementUI clip)
    {
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
        await ShowAFullscreenPopupInBottom(WindowSize.Height * 0.75, BuildPropertyPanel(_selected ?? new()));
    }

    private async Task ShowAFullscreenPopupInBottom(double height, View content)
    {
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
            Shadow = new Shadow
            {
                Brush = Colors.Black,
                Opacity = 1f,
                Radius = 3
            },
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
    private View BuildPropertyPanel(ClipElementUI clip)
    {
        return new PropertyPanelBuilder()
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
        
        .AddChildrensInALine("a line", (c) => c.AddText("11").AddEntry("testInput1", "Input 1", "").AddText("22").AddCheckbox("testCheckbox2",false))
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
        .Build();


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
        // 直接读取 Window.Width / Window.Height（单位为 DIP）
        double w = this.Window?.Width ?? 0;
        double h = this.Window?.Height ?? 0;
        WindowSize = new(w, h);
        Log($"Window size changed: {w:F0} x {h:F0} (DIP)");

        // 如需额外逻辑（例如调整 OverlayLayer），在这里处理
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
}

