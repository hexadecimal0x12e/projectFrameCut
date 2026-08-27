#if IOS
using Microsoft.Maui;
using Microsoft.Maui.Handlers;
using MauiToggleButton = projectFrameCut.Controls.ToggleButton;
using UIKit;

namespace projectFrameCut.Platforms.iOS;

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

    private static void MapIsToggled(IButtonHandler handler, IButton view)
    {
        if (handler.PlatformView is UIButton button && view is MauiToggleButton toggleButton)
        {
            button.Selected = toggleButton.IsToggled;
        }
    }
}
#endif
