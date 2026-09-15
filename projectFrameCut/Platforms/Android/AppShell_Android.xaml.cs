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
    public sealed partial class AndroidAppShell : AppShell
    {
        public AndroidAppShell()
        {
            AppShell.instance = this;

            // 加载 XAML，所有 Items / 默认 ShellItem 将会被加载
            InitializeComponent();

            Title = Localized.AppBrand;

#if ANDROID
            // 从 UI 线程设置当前项，确保 Items 已经加载完成，防止 "Active Shell Item not set" 异常。
            Microsoft.Maui.Controls.Application.Current?.Dispatcher.Dispatch(() =>
            {
                try
                {
                    // 优先使用 XAML 中的命名项（比如 x:Name="ProjectsTab")
                    if (this.FindByName<ShellItem>("ProjectsTab") is ShellItem projectsTab)
                    {
                        this.CurrentItem = projectsTab;
                        return;
                    }

                    // 按 route 查找，Route="home"
                    var homeItem = this.Items?.FirstOrDefault(i => string.Equals(i.Route, "home", System.StringComparison.OrdinalIgnoreCase));
                    if (homeItem is not null)
                    {
                        this.CurrentItem = homeItem;
                        return;
                    }

                    // 兜底，选择第一个项（如果存在）
                    if (this.Items != null && this.Items.Count > 0)
                    {
                        this.CurrentItem = this.Items[0];
                    }
                }
                catch (System.Exception ex)
                {
                    //try { Log(ex, "Activate initial ShellItem (Android)", this); } catch { System.Diagnostics.Debug.WriteLine(ex); }
                }
            });
#endif

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
