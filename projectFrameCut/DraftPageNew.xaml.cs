using Microsoft.Maui.Controls.Shapes;
using projectFrameCut.DraftStuff;
using projectFrameCut.Shared;
using projectFrameCut.ViewModels;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Threading.Tasks;
using Microsoft.Maui.Devices;
using projectFrameCut.PropertyPanel;

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

    int trackCount = 0;
    int webViewTrackCount = 1;
    double tracksViewOffset = 0;

    bool isPopupShowing = false;
    Border Popup = new();

    private Size WindowSize = new(500, 500);

    public DraftPage()
    {
        InitializeComponent();
        //hybridWebView.DefaultFile = "index.html";
        //hybridWebView.HybridRoot = "WebClipView";
        //hybridWebView.RawMessageReceived += HybridWebView_RawMessageReceived;
        TrackCalculator.HeightPerTrack = ClipHeight;

        nopGesture.Tapped += (s, e) =>
        {

        };

        Loaded += (s, e) =>
        {
            WindowSize = GetScreenSizeInDp();
            AddTrackButton_Clicked(new(), e);
            AddClip_Clicked(new(), e);
            this.Window.SizeChanged += Window_SizeChanged;
            var bgTap = new TapGestureRecognizer();
            bgTap.Tapped += async (s, e) => await HidePopup();
            OverlayLayer.GestureRecognizers.Clear();
            OverlayLayer.GestureRecognizers.Add(bgTap);
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
                HeightRequest = 60,
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
            }
        };
        var cont = new HorizontalStackLayout
        {
            Children =
            {
                new Label
                {
                    Text = "Clip"
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
                new ColumnDefinition { Width = new GridLength(12, GridUnitType.Absolute) }
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
            await ShowClipPopup(border, clip);
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

        if(e.StatusType == GestureStatus.Running)
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

        border.TranslationX = xToBe;

        bool ghostExists = Clips.ContainsKey("ghost_" + cid);

        if (!ghostExists && Math.Abs(yToBe - clip.defaultY) > 50.0)
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

        int newTrack = TrackCalculator.CalculateWhichTrackShouldIn(ghostClip.Clip.TranslationY);

        ClipElementUI shadow = Clips["shadow_" + cid];
        shadow.Clip.TranslationX = xToBe;

        if (origTrack == newTrack)
        {
            return;
        }

        UpdateShadowTrack(shadow, newTrack);
    }

    private void UpdateShadowTrack(ClipElementUI shadow, int newTrack)
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
    }

    private void HandlePanCompleted(Border border, ClipElementUI clip, string cid)
    {
        if (clip.MovingStatus != ClipMovingStatus.Free && clip.MovingStatus != ClipMovingStatus.Move) return;

        if (Clips.TryRemove("shadow_" + cid, out var shadowClip))
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
            int newTrack = TrackCalculator.CalculateWhichTrackShouldIn(ghostClip.Clip.TranslationY);
            OverlayLayer.Children.Remove(ghostClip.Clip);

            if (clip.origTrack is int oldTrack && Tracks.TryGetValue(oldTrack, out var oldTrackLayout))
            {
                oldTrackLayout.Children.Remove(border);
            }

            if (newTrack < 0 || newTrack > trackCount)
            {
                Clips.TryRemove(cid, out _);
                StatusLabel.Text = $"clip {cid} removed.";
                return;
            }
            else
            {
                if (newTrack == trackCount)
                {
                    AddTrackButton_Clicked(this, EventArgs.Empty);
                }

                if (clip.origTrack != newTrack)
                {
                    ClipsTrackChanged(border, clip, newTrack);
                }
                else
                {
                    border.TranslationY = 0.0;
                    if (Tracks.TryGetValue(newTrack, out var currentTrack))
                    {
                        currentTrack.Children.Add(border);
                    }
                }
                Clips[cid].origTrack = newTrack;
            }

        }


        StatusLabel.Text = $"clip {cid} saved. track:{clip.origTrack} x:{border.TranslationX}";
        Log($"{cid} moved to {border.TranslationX},{border.TranslationY}");
        OnClipChanged?.Invoke(cid, new ClipUpdateEventArgs
        {
            SourceId = cid,
            Reason = ClipUpdateReason.ClipItselfMove
        });


        clip.MovingStatus = ClipMovingStatus.Free;
    }

    private void ClipsTrackChanged(Border border, ClipElementUI clip, int newTrack)
    {
        border.TranslationY = 0;
        Tracks[newTrack].Add(border);
        Clips[clip.Id].defaultY = border.TranslationY;
        Clips[clip.Id].Clip = border;
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
                clip.handleLayoutX = border.TranslationX;
                clip.layoutX = clip.Clip.TranslationX;
                clip.Clip.BatchBegin();
                HandleStartWidth.AddOrUpdate(clip.Id, clip.Clip.WidthRequest, (_, __) => clip.Clip.WidthRequest);
                break;

            case GestureStatus.Running:
                double startWidth = HandleStartWidth.TryGetValue(clip.Id, out var sw) ? sw : clip.Clip.WidthRequest;
                double newWidth = Math.Max(MinClipWidth, startWidth - e.TotalX);

                clip.Clip.TranslationX = clip.layoutX + e.TotalX;
                clip.Clip.WidthRequest = newWidth;
                break;

            case GestureStatus.Completed:
                HandleStartWidth.TryRemove(clip.Id, out _);
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
                clip.Clip.WidthRequest = newWidth;
                break;

            case GestureStatus.Completed:
                HandleStartWidth.TryRemove(clip.Id, out _);
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
        await ShowAFullscreenPopupInBottom(800, BuildPropertyPanel(_selected ?? new()));
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
            Content = new ScrollView { Content = content }
        };
        OverlayLayer.InputTransparent = false;
        Popup.GestureRecognizers.Add(nopGesture);

        OverlayLayer.Add(Popup);

        var targetY = height;
        try
        {
            await Popup.TranslateTo(Popup.TranslationX, size.Height - targetY, 300, Easing.SinOut);
        }
        catch {  }
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
        .AddCustomChild(new Rectangle
        {
            WidthRequest = 100,
            HeightRequest = 500,
            Fill = Colors.Green
        })
        .ListenToChanges(async (s, e) =>
        {
            await DisplayAlert("Property Changed", $"Property '{e.Id}' changed from '{e.OriginValue}' to '{e.Value}'", "OK");
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
                    b.TranslationX *= 1.5;
                }
            }
        }

        tracksViewOffset *= 1.5;
    }

    private void ZoomInButton_Clicked(object sender, EventArgs e)
    {
        foreach (var item in Tracks)
        {
            foreach (var border in item.Value.Children)
            {
                if (border is Border b)
                {
                    b.TranslationX /= 1.5;
                }
            }
        }

        tracksViewOffset /= 1.5;
    }  

    private Size GetScreenSizeInDp()
    {
        var info = DeviceDisplay.MainDisplayInfo;
        double widthDp = info.Width / info.Density;
        double heightDp = info.Height / info.Density;
        return new Size(widthDp, heightDp);
    }

    protected override void OnAppearing()
    {
        base.OnAppearing();

        var size = GetScreenSizeInDp();
        Debug.WriteLine($"Window size on appearing: {size.Width:F0} x {size.Height:F0} (DIP)");

        
    }

    protected override void OnDisappearing()
    {
        base.OnDisappearing();

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
        Debug.WriteLine($"Window size changed: {w:F0} x {h:F0} (DIP)");

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

