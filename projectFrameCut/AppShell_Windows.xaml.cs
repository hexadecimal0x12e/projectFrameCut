namespace projectFrameCut
{
    public partial class AppShell : Shell
    {
        public static AppShell instance;
        public AppShell(bool placeDefaultItem = true)
        {
            instance = this;
            Title = Localized.AppBrand;
#if WINDOWS
            // Must be set explicitly because the XAML file is never loaded via
            // InitializeComponent(). Without this, MAUI Shell creates flyout
            // items (ShellFlyoutItemView) whose ShellView property resolves to
            // null when the RootNavigationView is measured, causing a
            // NullReferenceException on startup.
            FlyoutBehavior = FlyoutBehavior.Disabled;

            if (placeDefaultItem && (this.Items == null || this.Items.Count == 0))
            {
                var shellContent = new Microsoft.Maui.Controls.ShellContent
                {
                    Route = "home",
                    ContentTemplate = new Microsoft.Maui.Controls.DataTemplate(typeof(HomePage)),
                    Title = Localized.AppShell_ProjectsTab
                };

                this.Items.Add(shellContent);
            }
            this.Navigated += AppShell_Navigated;

#endif
        }

        private void AppShell_Navigated(object? sender, Microsoft.Maui.Controls.ShellNavigatedEventArgs e)
        {
            var currentPage = Microsoft.Maui.Controls.Shell.Current?.CurrentPage;

            if(!Shell.GetNavBarIsVisible(currentPage))
            {
                App.HideNavBar();
            }
            // not do ShowNavBar() because the nav bar may be hidden by the page itself, so we don't want to override that.
            switch (currentPage?.GetType())
            {
                case Type t when t == typeof(HomePage):
                    App.MainNavView?.SelectedItem = App.homeItem;
                    break;
                case Type t when t == typeof(AssetsLibraryPage):
                    App.MainNavView?.SelectedItem = App.assetItem;
                    break;
                case Type t when t == typeof(TemplatedPage):
                    App.MainNavView?.SelectedItem = App.templateItem;
                    break;
                case Type t when t == typeof(CreatePage):
                    App.MainNavView?.SelectedItem = App.createItem;
                    break;
                case Type t when t.Name.Contains("Template",StringComparison.InvariantCultureIgnoreCase):
                    App.MainNavView?.SelectedItem = App.templateItem;
                    break;
                case Type t when t.Name.Contains("Setting",StringComparison.InvariantCultureIgnoreCase):
                    App.MainNavView?.SelectedItem = App.settingItem;
                    break;
                default:
                    //keep unchange
                    // App.MainNavView?.SelectedItem = App.homeItem;
                    break;
            }
        }



        public void ShowNavView() => App.ShowNavBar();
        public void HideNavView() => App.HideNavBar();
        public void CollapseNavView() => App.CollapseNavView();

    }
}
