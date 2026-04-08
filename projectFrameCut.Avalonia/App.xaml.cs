using projectFrameCut.ApplicationAPIBase.Helpers;

namespace projectFrameCut
{
    public partial class App : Application
    {
        public App()
        {
            InitializeComponent();
        }

        protected override Window CreateWindow(IActivationState? activationState)
        {
            var shell = new Shell();
            var mauiWindow = new Microsoft.Maui.Controls.Window(shell);

            shell.Items.Add(new ShellContent { Content = new HomePage(), Title = Localized.AppShell_ProjectsTab, Icon = ImageHelper.LoadFromAsset("icon_project"), Route = "home" });
            shell.Items.Add(new ShellContent { Content = new TemplateViewPage(), Title = Localized.AppShell_TemplateTab, Icon = ImageHelper.LoadFromAsset("icon_template"), Route = "template" });
            shell.Items.Add(new ShellContent { Content = new AssetsLibraryPage(), Title = Localized.AppShell_AssetsTab, Icon = ImageHelper.LoadFromAsset("icon_asset"), Route = "assets" });
            shell.Items.Add(new ShellContent { Content = new MainSettingsPage(), Title = Localized._Settings, Icon = ImageHelper.LoadFromAsset("icon_setting"), Route = "options" });
            return mauiWindow;
        }
    }
}