using Microsoft.Maui.Graphics.Platform;
using System.Linq;
using Microsoft.Maui.Controls.PlatformConfiguration.AndroidSpecific;
using ShellItem = Microsoft.Maui.Controls.ShellItem;


namespace projectFrameCut
{
    public partial class AppShell : Shell
    {
        public static AppShell instance;


        public AppShell()
        {
            instance = this;

            // 必须加载 XAML，否则 Items / 命名的 ShellItem 不会被生成
            InitializeComponent();

            Title = Localized.AppBrand;

        }


        public void ShowNavView()
        {
            //Shell.SetNavBarIsVisible(this, true);
        }

        public void HideNavView()
        {
            //Shell.SetNavBarIsVisible(this, false);


        }
    }
}
