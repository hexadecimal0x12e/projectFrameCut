using Microsoft.Maui.Controls;
using projectFrameCut.ApplicationAPIBase.Helpers;

namespace projectFrameCut;

/// <summary>
/// Linux root navigation host.
///
/// The GTK4 Shell handler currently renders only each ShellContent root page and
/// does not render pages added to a ShellSection navigation stack. A TabbedPage
/// containing one NavigationPage per tab keeps the existing PushAsync/PopAsync
/// navigation used throughout the application working on Linux.
/// </summary>
public sealed class LinuxAppTabbedPage : TabbedPage
{
    public LinuxAppTabbedPage()
    {
        Title = Localized.AppBrand;

        AddNavigationTab(new HomePage(), Localized.AppShell_ProjectsTab, "icon_project");
        AddNavigationTab(new AssetsLibraryPage(), Localized.AppShell_AssetsTab, "icon_asset");
        AddNavigationTab(new CreatePage(), Localized.AppShell_CreateTab, "icon_add");
        AddNavigationTab(new TemplateViewPage(), Localized.AppShell_TemplateTab, "icon_template");
        AddNavigationTab(new MainSettingsPage(), Localized._Settings, "icon_setting");

        CurrentPageChanged += (_, _) => ApplyCurrentPageChrome();
        HandlerChanged += (_, _) => ApplyCurrentPageChrome();
    }

    private void AddNavigationTab(Page rootPage, string title, string iconName)
    {
        if (string.IsNullOrWhiteSpace(rootPage.Title))
            rootPage.Title = title;

        var navigationPage = new NavigationPage(rootPage)
        {
            Title = title,
            IconImageSource = ImageHelper.LoadFromAsset(iconName)
        };

        navigationPage.Pushed += (_, args) => ApplyCurrentPageChrome(args.Page);
        navigationPage.Popped += (_, _) => ApplyCurrentPageChrome(navigationPage.CurrentPage);
        navigationPage.PoppedToRoot += (_, _) => ApplyCurrentPageChrome(navigationPage.CurrentPage);

        Children.Add(navigationPage);
    }

    private void ApplyCurrentPageChrome(Page? page = null)
    {
        page ??= (CurrentPage as NavigationPage)?.CurrentPage;
        if (page is null)
            return;

        // Existing pages already describe their desired chrome through Shell's
        // attached properties. Mirror those values onto the GTK4 page handlers.
        NavigationPage.SetHasNavigationBar(page, Shell.GetNavBarIsVisible(page));

        if (Handler?.PlatformView is Gtk.Notebook notebook)
            notebook.SetShowTabs(Shell.GetTabBarIsVisible(page));
    }
}
