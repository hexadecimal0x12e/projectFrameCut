using Microsoft.Maui.Controls.Shapes;

namespace projectFrameCut.Controls;

public sealed class PreviewClipLayout : AbsoluteLayout
{
    public static readonly BindableProperty ClipBoundsProperty = BindableProperty.Create(
        nameof(ClipBounds), typeof(Rect?), typeof(PreviewClipLayout), null,
        propertyChanged: static (bindable, _, _) => ((PreviewClipLayout)bindable).UpdateClip());

    public Rect? ClipBounds
    {
        get => (Rect?)GetValue(ClipBoundsProperty);
        set => SetValue(ClipBoundsProperty, value);
    }

    private void UpdateClip()
    {
#if !WINDOWS
        if (ClipBounds is not Rect bounds)
            Clip = null;
        else if (Clip is RectangleGeometry clip)
            clip.Rect = bounds;
        else
            Clip = new RectangleGeometry { Rect = bounds };
#endif
    }
}
