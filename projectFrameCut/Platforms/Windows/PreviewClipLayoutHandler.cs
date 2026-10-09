using Microsoft.Maui.Handlers;
using Microsoft.Maui.Platform;
using projectFrameCut.Controls;
using ILayout = Microsoft.Maui.ILayout;
using PlatformRectangleGeometry = Microsoft.UI.Xaml.Media.RectangleGeometry;
using PlatformSize = global::Windows.Foundation.Size;

namespace projectFrameCut.Platforms.Windows;

public sealed class PreviewClipLayoutHandler : LayoutHandler
{
    public new static readonly IPropertyMapper<ILayout, PreviewClipLayoutHandler> Mapper =
        new PropertyMapper<ILayout, PreviewClipLayoutHandler>(LayoutHandler.Mapper)
        {
            [nameof(PreviewClipLayout.ClipBounds)] = static (handler, view) =>
            {
                var panel = (PreviewClipPanel)handler.PlatformView;
                panel.ClipBounds = ((PreviewClipLayout)view).ClipBounds;
                panel.UpdateClip();
            },
        };

    public PreviewClipLayoutHandler() : base(Mapper)
    {
    }

    protected override LayoutPanel CreatePlatformView() => new PreviewClipPanel
    {
        CrossPlatformLayout = VirtualView,
    };

    protected override void ConnectHandler(LayoutPanel platformView)
    {
        base.ConnectHandler(platformView);
        LogDiagnostic("[PreviewClip] Connected native XAML rectangle clipping.");
    }

    protected override void DisconnectHandler(LayoutPanel platformView)
    {
        platformView.Clip = null;
        base.DisconnectHandler(platformView);
        LogDiagnostic("[PreviewClip] Disconnected native XAML rectangle clipping.");
    }

    private sealed class PreviewClipPanel : LayoutPanel
    {
        public Rect? ClipBounds { get; set; }

        protected override PlatformSize ArrangeOverride(PlatformSize finalSize)
        {
            var size = base.ArrangeOverride(finalSize);
            // MAUI resets UIElement.Clip during arrange; keep both clips on the native XAML path.
            UpdateClip();
            return size;
        }

        public void UpdateClip()
        {
            if (ClipBounds is Rect bounds)
            {
                var rect = new global::Windows.Foundation.Rect(bounds.X, bounds.Y, bounds.Width, bounds.Height);
                if (Clip is PlatformRectangleGeometry clip)
                {
                    if (clip.Rect != rect) clip.Rect = rect;
                }
                else
                    Clip = new PlatformRectangleGeometry { Rect = rect };
            }
            else if (ClipsToBounds)
                Clip = new PlatformRectangleGeometry
                {
                    Rect = new global::Windows.Foundation.Rect(0, 0, ActualWidth, ActualHeight),
                };
            else
                Clip = null;
        }
    }
}
