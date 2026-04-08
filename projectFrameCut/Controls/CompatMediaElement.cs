using Microsoft.Maui.Controls;

#if !Avalonia
using CommunityToolkit.Maui.Views;
#endif

namespace projectFrameCut.Controls;

#if Avalonia
/// <summary>
/// Avalonia-safe media placeholder to avoid MediaElement runtime issues.
/// </summary>
public class CompatMediaElement : ContentView
{
    private object? _source;

    public event EventHandler? MediaEnded;

    public object? Source
    {
        get => _source;
        set => _source = value;
    }

    public Aspect Aspect { get; set; } = Aspect.AspectFit;

    public bool ShouldAutoPlay { get; set; }

    public bool ShouldLoopPlayback { get; set; }

    public bool ShouldMute { get; set; }

    public bool ShouldShowPlaybackControls { get; set; }

    public bool ShouldKeepScreenOn { get; set; }

    public void Play()
    {
    }

    public void Pause()
    {
    }

    public void Stop()
    {
    }
}
#else
/// <summary>
/// Real media element on non-Avalonia targets.
/// </summary>
public class CompatMediaElement : MediaElement
{
}
#endif
