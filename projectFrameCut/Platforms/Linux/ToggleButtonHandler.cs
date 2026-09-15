#if LINUX
using Microsoft.Maui.Platforms.Linux.Gtk4.Handlers;

namespace projectFrameCut.Platforms.Linux;

internal sealed class ToggleButtonHandler : ButtonHandler
{
    protected override Gtk.Button CreatePlatformView() => Gtk.ToggleButton.New();
}
#endif
