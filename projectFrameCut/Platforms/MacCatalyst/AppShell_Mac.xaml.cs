using Microsoft.Maui.Graphics.Platform;
using System.Linq;
using Microsoft.Maui.Controls.PlatformConfiguration.AndroidSpecific;
using ShellItem = Microsoft.Maui.Controls.ShellItem;


#if ANDROID
using Android.App;
using Android.Views;
using AndroidX.Core.View;
#endif

namespace projectFrameCut
{
    public sealed partial class MacAppShell : AppShell
    {
        public MacAppShell()
        {
            AppShell.instance = this;

            // 加载 XAML，所有 Items / 默认 ShellItem 将会被加载
            InitializeComponent();

            Title = Localized.AppBrand;

        }

        public override void ShowNavView()
        {
            //Shell.SetNavBarIsVisible(this, true);
        }

        public override void HideNavView()
        {
            //Shell.SetNavBarIsVisible(this, false);
        }

        public override void CollapseNavView()
        {
        }
    }
}
