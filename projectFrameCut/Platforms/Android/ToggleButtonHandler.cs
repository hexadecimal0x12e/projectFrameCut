#if ANDROID
using Google.Android.Material.Button;
using Microsoft.Maui;
using Microsoft.Maui.Handlers;
using MauiToggleButton = projectFrameCut.Controls.ToggleButton;

namespace projectFrameCut.Platforms.Android;

internal sealed class ToggleButtonHandler : ButtonHandler
{
    private static readonly IPropertyMapper<IButton, IButtonHandler> ToggleMapper =
        new PropertyMapper<IButton, IButtonHandler>(ButtonHandler.Mapper)
        {
            [nameof(MauiToggleButton.IsToggled)] = MapIsToggled,
        };

    public ToggleButtonHandler() : base(ToggleMapper)
    {
    }

    protected override void ConnectHandler(MaterialButton platformView)
    {
        platformView.Checkable = true;
        base.ConnectHandler(platformView);
    }

    private static void MapIsToggled(IButtonHandler handler, IButton view)
    {
        if (handler.PlatformView is MaterialButton button && view is MauiToggleButton toggleButton)
        {
            button.Checked = toggleButton.IsToggled;
        }
    }
}
#endif
