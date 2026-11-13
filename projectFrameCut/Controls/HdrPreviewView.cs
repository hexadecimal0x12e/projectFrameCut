using projectFrameCut.LivePreview;

namespace projectFrameCut.Controls;

/// <summary>
/// A platform-backed preview surface. Windows maps this view to an FP16 scRGB
/// composition surface; other platforms never materialize it and keep using Image.
/// </summary>
public sealed class HdrPreviewView : View
{
    internal PreviewLoadDiagnostics? Timing { get; private set; }

    protected override void OnPropertyChanging(string? propertyName = null)
    {
        if (propertyName == nameof(Frame))
            Timing = PreviewLoadDiagnostics.Begin(this, Timing);
        base.OnPropertyChanging(propertyName);
    }

    protected override void OnPropertyChanged(string? propertyName = null)
    {
        if (propertyName == nameof(Frame) && Frame is not null)
            Timing?.Stage($"frame assigned; content={System.Runtime.CompilerServices.RuntimeHelpers.GetHashCode(Frame)}, path={Frame.VfdPath}, format={Frame.TargetPixelFormat}");
        base.OnPropertyChanged(propertyName);
    }

    internal static PreviewFrameSource PrepareFrame(PreviewFrameSource frame, PreviewFrameBuffer? buffer = null)
        => PreviewFrameMaterializer.Prepare(frame, buffer);

    public static readonly BindableProperty FrameProperty = BindableProperty.Create(
        nameof(Frame),
        typeof(PreviewFrameSource),
        typeof(HdrPreviewView),
        default(PreviewFrameSource));

    public PreviewFrameSource? Frame
    {
        get => (PreviewFrameSource?)GetValue(FrameProperty);
        set => SetValue(FrameProperty, value);
    }
}
