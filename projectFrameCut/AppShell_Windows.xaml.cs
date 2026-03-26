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
                //case Type t when t == typeof(DebuggingMainPage):
                //    App.MainNavView?.SelectedItem = App.debugItem;
                //    break;
                case Type t when t.Name.Contains("Setting",StringComparison.InvariantCultureIgnoreCase):
                    App.MainNavView?.SelectedItem = App.settingItem;
                    break;
                default:
                    //keep unchange
                    // App.MainNavView?.SelectedItem = App.homeItem;
                    break;
            }
        }



        public async void ShowNavView()
        {
            await App.ShowNavBar();
        }

        public void HideNavView()
        {
            App.HideNavBar();
        }

    }
}
