#if WINDOWS
using Microsoft.Maui;
using Microsoft.Maui.Handlers;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Media;
using MauiToggleButton = projectFrameCut.Controls.ToggleButton;
using WinUIToggleButton = Microsoft.UI.Xaml.Controls.Primitives.ToggleButton;

namespace projectFrameCut.Platforms.Windows;

internal sealed class ToggleButtonHandler : ViewHandler<MauiToggleButton, WinUIToggleButton>
{
    public static readonly IPropertyMapper<MauiToggleButton, ToggleButtonHandler> Mapper =
        new PropertyMapper<MauiToggleButton, ToggleButtonHandler>(ViewHandler.ViewMapper)
        {
            [nameof(MauiToggleButton.Text)] = MapText,
            [nameof(MauiToggleButton.TextColor)] = MapTextColor,
            [nameof(ITextStyle.Font)] = MapFont,
            [nameof(MauiToggleButton.Padding)] = MapPadding,
            [nameof(MauiToggleButton.Background)] = MapBackground,
            [nameof(MauiToggleButton.CornerRadius)] = MapCornerRadius,
            [nameof(MauiToggleButton.IsToggled)] = MapIsToggled,
        };

    public ToggleButtonHandler() : base(Mapper)
    {
    }

    protected override WinUIToggleButton CreatePlatformView() => new();

    protected override void ConnectHandler(WinUIToggleButton platformView)
    {
        platformView.Click += OnClick;
        base.ConnectHandler(platformView);
    }

    protected override void DisconnectHandler(WinUIToggleButton platformView)
    {
        platformView.Click -= OnClick;
        base.DisconnectHandler(platformView);
    }

    private void OnClick(object sender, RoutedEventArgs e)
        => ((IButton)VirtualView).Clicked();

    private static void MapText(ToggleButtonHandler handler, MauiToggleButton view)
        => handler.PlatformView.Content = view.Text;

    private static void MapTextColor(ToggleButtonHandler handler, MauiToggleButton view)
    {
        var brush = ToBrush(view.TextColor);
        handler.PlatformView.Foreground = brush;
        handler.PlatformView.Resources["ToggleButtonForegroundChecked"] = ToBrush(view.OnTextColor);
        handler.PlatformView.Resources["ToggleButtonForegroundCheckedPointerOver"] = ToBrush(view.OnTextColor);
        handler.PlatformView.Resources["ToggleButtonForegroundCheckedPressed"] = ToBrush(view.OnTextColor);
    }

    private static void MapFont(ToggleButtonHandler handler, MauiToggleButton view)
    {
        handler.PlatformView.FontSize = view.FontSize > 0 ? view.FontSize : 14;
        if (!string.IsNullOrWhiteSpace(view.FontFamily))
        {
            handler.PlatformView.FontFamily = new FontFamily(view.FontFamily);
        }
    }

    private static void MapPadding(ToggleButtonHandler handler, MauiToggleButton view)
    {
        var padding = view.Padding;
        handler.PlatformView.Padding = new Microsoft.UI.Xaml.Thickness(padding.Left, padding.Top, padding.Right, padding.Bottom);
    }

    private static void MapBackground(ToggleButtonHandler handler, MauiToggleButton view)
    {
        handler.PlatformView.Background = ToBrush(view.BackgroundColor);
        handler.PlatformView.Resources["ToggleButtonBackground"] = ToBrush(view.OffBackgroundColor);
        handler.PlatformView.Resources["ToggleButtonBackgroundPointerOver"] = ToBrush(view.OffBackgroundColor);
        handler.PlatformView.Resources["ToggleButtonBackgroundPressed"] = ToBrush(view.OffBackgroundColor);
        handler.PlatformView.Resources["ToggleButtonBackgroundChecked"] = ToBrush(view.OnBackgroundColor);
        handler.PlatformView.Resources["ToggleButtonBackgroundCheckedPointerOver"] = ToBrush(view.OnBackgroundColor);
        handler.PlatformView.Resources["ToggleButtonBackgroundCheckedPressed"] = ToBrush(view.OnBackgroundColor);
    }

    private static void MapCornerRadius(ToggleButtonHandler handler, MauiToggleButton view)
    {
        // MAUI uses -1 to mean "use the platform default". WinUI rejects
        // negative corner radii, so preserve the native default in that case.
        if (view.CornerRadius >= 0)
        {
            handler.PlatformView.Resources["ControlCornerRadius"] = new Microsoft.UI.Xaml.CornerRadius(view.CornerRadius);
        }
        else
        {
            handler.PlatformView.Resources.Remove("ControlCornerRadius");
        }
    }

    private static void MapIsToggled(ToggleButtonHandler handler, MauiToggleButton view)
        => handler.PlatformView.IsChecked = view.IsToggled;

    private static Microsoft.UI.Xaml.Media.SolidColorBrush ToBrush(Microsoft.Maui.Graphics.Color color)
        => new(global::Windows.UI.Color.FromArgb(
            (byte)Math.Round(color.Alpha * byte.MaxValue),
            (byte)Math.Round(color.Red * byte.MaxValue),
            (byte)Math.Round(color.Green * byte.MaxValue),
            (byte)Math.Round(color.Blue * byte.MaxValue)));
}
#endif
