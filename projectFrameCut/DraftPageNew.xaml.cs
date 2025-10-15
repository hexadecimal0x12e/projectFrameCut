using Microsoft.Maui.Controls.Shapes;
using projectFrameCut.DraftStuff;
using projectFrameCut.ViewModels;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text.Json;

namespace projectFrameCut;

public partial class DraftPage : ContentPage
{
    public event EventHandler<ClipUpdateEventArgs>? OnClipChanged;

    public const int ClipHeight = 62;

    ConcurrentDictionary<string, ClipElementUI> Clips = new();
    ConcurrentDictionary<int, AbsoluteLayout> Tracks = new();
    //ConcurrentDictionary<string, ClipMovingStatus> MovingStatus = new();
    ConcurrentDictionary<string, Stopwatch> HoldingTimer = new();

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
            hybridWebView.EvaluateJavaScriptAsync($"createTrack()");
            return;
        }

        var head = new Border
        {
            Content = new VerticalStackLayout
            {
                Children = {
                    new Label { Text = "New Track 1" },
                    new Button { Text = "Remove", BackgroundColor = Colors.Red, TextColor = Colors.White, HorizontalOptions = LayoutOptions.End }
                }
            },
            HeightRequest = 60,
            Margin = new(0, 0, 0, 2)


        };

        var track = new AbsoluteLayout
        {

        };

        Tracks.AddOrUpdate(trackCount, track, (_, _) => track);

        var content = new Border
        {
            Content = Tracks[trackCount],
            HeightRequest = 60,
            Margin = new(0, 0, 0, 2)


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
                WidthRequest = 120,
                HeightRequest = 60,
                StrokeShape = new RoundRectangle
                {
                    CornerRadius = 20
                },
                Shadow = new Shadow
                {
                    Brush = Colors.Black,
                    Opacity = 0.3f,
                    Offset = new Point(4, 4),
                    Radius = 6
                }
            }
        };

        Clips.AddOrUpdate(cid, element, (_, _) => element);

        var panGesture = new PanGestureRecognizer();

        panGesture.PanUpdated += PanGesture_PanUpdated;

        Clips[cid].Clip.GestureRecognizers.Add(panGesture);


        Tracks.Last().Value.Add(Clips[cid].Clip);

    }

    private void PanGesture_PanUpdated(object? sender, PanUpdatedEventArgs e)
    {
        double widthOffset = TrackHeadLayout?.Width ?? 0;
        double heightOffset = (trackCount - 1) * ClipHeight;
        var cid = Clips.FirstOrDefault(c => c.Value.Clip == sender).Key ?? throw new KeyNotFoundException("Clip is not in tracks."); ;
        var clip = Clips[cid];
        int newTrack = 0;
        if (sender is Border border)
        {

            var origTrack = TrackCalculator.CalculateWhichTrackShouldIn(border.TranslationY);

            switch (e.StatusType)
            {
                case GestureStatus.Started:
                    clip.layoutX = border.TranslationX;
                    clip.layoutY = border.TranslationY;
                    clip.defaultY = border.TranslationY;
                    HoldingTimer.AddOrUpdate(cid, Stopwatch.StartNew(), (_, _) => Stopwatch.StartNew());
                    break;

                case GestureStatus.Running:
                    StatusLabel.Text = $"moving {cid} \r\nto {clip.layoutX + e.TotalX},{clip.layoutY + e.TotalY}\r\n";
                    var xToBe = clip.layoutX + e.TotalX;
                    var yToBe = clip.layoutY + e.TotalY;
                    if (xToBe < 0) break;
                    border.TranslationX = xToBe;
#if ANDROID
                    border.TranslationY = yToBe;
#endif

                    if (!Clips.ContainsKey("ghost_" + cid)
                        && HoldingTimer.TryGetValue(cid, out var value)
                        && value.ElapsedMilliseconds > 500
                        && Math.Abs(Math.Abs(clip.defaultY) - Math.Abs(yToBe)) > 60)
                    {
                        InitMoveBetweenTracks(clip, cid, border);
                    }
                    else if (Clips.ContainsKey("ghost_" + cid))
                    {
                        var ghostClip = Clips["ghost_" + cid];
                        
                        var clipAbsolutePosition = GetAbsolutePosition(border, OverlayLayer);

                        ghostClip.Clip.TranslationX = clipAbsolutePosition.X;
                        ghostClip.Clip.TranslationY = clipAbsolutePosition.Y;

                        border.TranslationY = yToBe;
                        newTrack = TrackCalculator.CalculateWhichTrackShouldIn(ghostClip.Clip.TranslationY);
                        if (origTrack != newTrack)
                        {
                            //todo: show a shadow
                        }
                        StatusLabel.Text += $"ghost clip: {Clips["ghost_" + cid].Clip.TranslationX},{Clips["ghost_" + cid].Clip.TranslationY} \r\nnew clip should in track:{TrackCalculator.CalculateWhichTrackShouldIn(ghostClip.Clip.TranslationY)} ";
                    }




                    break;

                case GestureStatus.Completed:
                    if (Clips.TryRemove("ghost_" + cid, out var _clip))
                    {


                        border.Opacity = 1;
                        Log($"{cid} moved to {border.TranslationX},{border.TranslationY}");
                        OnClipChanged?.Invoke(cid, new ClipUpdateEventArgs { SourceId = cid, Reason = ClipUpdateReason.ClipItselfMove });
                        newTrack = TrackCalculator.CalculateWhichTrackShouldIn(_clip.Clip.TranslationY);
                        if(newTrack >= trackCount || newTrack < 0) //remove
                        {
                            Tracks.Where((t) => t.Value.Contains(border)).ToList().ForEach((t) => t.Value.Remove(border));
                            OverlayLayer.Children.Remove(_clip.Clip);
                            Clips.Remove(cid, out var _);
                            StatusLabel.Text += $"\r\n\r\n{cid} removed. x:{border.TranslationX} y:{border.TranslationY}";

                            return;
                        }

                        if (origTrack != newTrack)
                        {
                            ClipsTrackChanged(border, clip, newTrack);
                            Tracks.Where((t) => t.Value.Contains(border)).ToList().ForEach((t) => t.Value.Remove(border));
                        }
                        OverlayLayer.Children.Remove(_clip.Clip);

                    }
                    StatusLabel.Text += $"\r\n\r\nChanges on {cid} saved. now in Track {newTrack}, x:{border.TranslationX} y:{border.TranslationY}";

                    break;
            }




        }

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
        var newBorder = new Border
        {
            Stroke = border.Stroke,
            StrokeThickness = border.StrokeThickness,
            Background = new SolidColorBrush(Colors.CornflowerBlue),
            WidthRequest = border.WidthRequest,
            HeightRequest = border.HeightRequest,
            StrokeShape = border.StrokeShape,
            Shadow = border.Shadow,
            TranslationX = border.TranslationX,
            TranslationY = 0
        };
        var panGesture = new PanGestureRecognizer();

        panGesture.PanUpdated += PanGesture_PanUpdated;

        newBorder.GestureRecognizers.Add(panGesture);

        

        Tracks[newTrack].Add(newBorder);
        Clips[clip.Id].defaultY = newBorder.TranslationY;
        Clips[clip.Id].Clip = newBorder;
        
        

    }

    private async void AddWebViewClip(int trackIndex, string clipId, string text, int start, int duration)
    {
        var script = $"window.addClip({trackIndex}, '{clipId}', '{text}', {start}, {duration});";
        await hybridWebView.EvaluateJavaScriptAsync(script);
    }




    private void InitMoveBetweenTracks(ClipElementUI clipElementUI, string cid, Border border)
    {
        border.Stroke = Colors.Green;

        var ghostBorder = new Border
        {
            Stroke = border.Stroke,
            StrokeThickness = border.StrokeThickness,
            Background = new SolidColorBrush(Colors.DeepSkyBlue),
            WidthRequest = border.WidthRequest,
            HeightRequest = border.HeightRequest,
            StrokeShape = border.StrokeShape,
            Shadow = new Shadow
            {
                Brush = Colors.Black,
                Offset = new Point(20, 20),
                Radius = 40,
                Opacity = 0.65f
            }
        };

        border.Opacity = 0.1;


        var ghostElement = new ClipElementUI
        {
            layoutX = clipElementUI.layoutX,
            layoutY = clipElementUI.layoutY,
            defaultY = clipElementUI.defaultY,
            Clip = ghostBorder
        };

        Clips[cid].ghostLayoutX = clipElementUI.layoutX;
        Clips[cid].ghostLayoutY = clipElementUI.layoutY;

        Clips.AddOrUpdate("ghost_" + cid, ghostElement, (_, _) => ghostElement);

        OverlayLayer.Add(ghostBorder);
    }

    private void MoveLeftButton_Clicked(object sender, EventArgs e)
    {
        foreach (var item in Tracks)
        {
            foreach (var border in item.Value.Children)
            {
                if(border is Border b)
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

