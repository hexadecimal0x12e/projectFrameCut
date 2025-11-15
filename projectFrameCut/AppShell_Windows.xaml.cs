namespace projectFrameCut
{
    public partial class AppShell : Shell
    {
        public static AppShell instance;
        public AppShell()
        {
            instance = this;
            Title = Localized.AppBrand;
#if WINDOWS
            if (this.Items == null || this.Items.Count == 0)
            {
                var shellContent = new Microsoft.Maui.Controls.ShellContent
                {
                    Route = "home",
                    ContentTemplate = new Microsoft.Maui.Controls.DataTemplate(typeof(HomePage)),
                    Title = Localized.AppShell_ProjectsTab
                };

                this.Items.Add(shellContent);
            }
#endif
        }

        public void ShowNavView()
        {
            App.ShowNavBar();
        }

        public void HideNavView()
        {
            App.HideNavBar();
        }

    }
}
