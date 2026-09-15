using Microsoft.Maui.Graphics;
using Microsoft.Maui.Layouts;
using Microsoft.Maui.Platforms.Linux.Gtk4.Handlers;

namespace projectFrameCut.Platforms.Linux;

internal sealed class LinuxBorderHandler : BorderHandler
{
    public override Size GetDesiredSize(
        double widthConstraint,
        double heightConstraint)
    {
        var measured = base.GetDesiredSize(
            widthConstraint,
            heightConstraint);

        var view = VirtualView;

        return new Size(
            LayoutManager.ResolveConstraints(
                widthConstraint,
                view.Width,
                measured.Width,
                view.MinimumWidth,
                view.MaximumWidth),
            LayoutManager.ResolveConstraints(
                heightConstraint,
                view.Height,
                measured.Height,
                view.MinimumHeight,
                view.MaximumHeight));
    }
}
