using projectFrameCut.LivePreview;

namespace projectFrameCut.Controls;

/// <summary>
/// A platform-backed preview surface. Windows maps this view to an FP16 scRGB
/// composition surface; other platforms never materialize it and keep using Image.
/// </summary>
public sealed class HdrPreviewView : View
{
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
