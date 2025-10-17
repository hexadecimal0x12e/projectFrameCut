using Microsoft.Maui.Controls.Shapes;
using projectFrameCut.DraftStuff;
using projectFrameCut.Shared;
using projectFrameCut.ViewModels;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Threading.Tasks;

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

    int trackCount = 0;
    int webViewTrackCount = 1;
    double tracksViewOffset = 0;

    public DraftPage()
    {
        InitializeComponent();
        hybridWebView.DefaultFile = "index.html";
        hybridWebView.HybridRoot = "WebClipView";
        hybridWebView.RawMessageReceived += HybridWebView_RawMessageReceived;
        TrackCalculator.HeightPerTrack = ClipHeight;

        Loaded += (s, e) =>
        {
            AddTrackButton_Clicked(s, e);
            AddClip_Clicked(s, e);
        };
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
        LeftHandleGesture.PanUpdated += LeftHandleGesture_PanUpdated;

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

    private async void DoubleTapGesture_Tapped(object? sender, TappedEventArgs e)
    {
        await DisplayAlert(Title, "You double clicked!", "ok");
    }

    private void SelectTapGesture_Tapped(object? sender, TappedEventArgs e)
    {
        if (sender is not Border border) return;
        if (border.BindingContext is not ClipElementUI clip) return;
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

    private void LeftHandleGesture_PanUpdated(object? sender, PanUpdatedEventArgs e)
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

    private void ClipPaned(object? sender, PanUpdatedEventArgs e)
    {
        if (sender is not Border border) return;
        if (border.BindingContext is not ClipElementUI clip) return;
        if (clip.MovingStatus == ClipMovingStatus.Resize) return;
        var cid = clip.Id;

        int origTrack = TrackCalculator.CalculateWhichTrackShouldIn(border.TranslationY);
        clip.origTrack ??= origTrack;

        switch (e.StatusType)
        {
            case GestureStatus.Started:
                HandlePanStarted(border, clip);
                break;

            case GestureStatus.Running:
                HandlePanRunning(e, border, clip, cid, origTrack);
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

    private void ClipsTrackChanged(Border border, ClipElementUI clip, int newTrack)
    {
        border.TranslationY = 0;
        Tracks[newTrack].Add(border);
        Clips[clip.Id].defaultY = border.TranslationY;
        Clips[clip.Id].Clip = border;
    }

    private async void AddWebViewClip(int trackIndex, string clipId, string text, int start, int duration)
    {
        var script = $"window.addClip({trackIndex}, '{clipId}', '{text}', {start}, {duration});";
        await hybridWebView.EvaluateJavaScriptAsync(script);
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
}

