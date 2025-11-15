using Microsoft.Maui.Graphics.Platform;
using System.Linq;
#if ANDROID
using Android.App;
using Android.Views;
using AndroidX.Core.View;

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

#if ANDROID
            // 延后在 UI 线程设置当前项，确保 Items 已经创建（避免 "Active Shell Item not set" 异常）
            Microsoft.Maui.Controls.Application.Current?.Dispatcher.Dispatch(() =>
            {
                try
                {
                    // 优先使用 XAML 中的命名项（例如 x:Name="ProjectsTab")
                    if (this.FindByName<ShellItem>("ProjectsTab") is ShellItem projectsTab)
                    {
                        this.CurrentItem = projectsTab;
                        return;
                    }

                    // 按 route 查找（例如 Route="home"）
                    var homeItem = this.Items?.FirstOrDefault(i => string.Equals(i.Route, "home", System.StringComparison.OrdinalIgnoreCase));
                    if (homeItem is not null)
                    {
                        this.CurrentItem = homeItem;
                        return;
                    }

                    // 兜底：选择第一个项（如果存在）
                    if (this.Items != null && this.Items.Count > 0)
                    {
                        this.CurrentItem = this.Items[0];
                    }
                }
                catch (System.Exception ex)
                {
                    try { Log(ex, "Activate initial ShellItem (Android)", this); } catch { System.Diagnostics.Debug.WriteLine(ex); }
                }
            });
#endif


        }

        public void ShowNavView()
        {
#if ANDROID
            try
            {
                var activity = Microsoft.Maui.ApplicationModel.Platform.CurrentActivity as Activity;
                if (activity == null) return;

                var window = activity.Window;
                var decorView = window.DecorView;

                var controller = new WindowInsetsControllerCompat(window, decorView);
                controller.Show(WindowInsetsCompat.Type.SystemBars());
            }
            catch (System.Exception ex)
            {
                try { Log(ex, "ShowNavView (Android)", this); } catch { System.Diagnostics.Debug.WriteLine(ex); }
            }
#endif
        }

        public void HideNavView()
        {
#if ANDROID
            try
            {
                var activity = Microsoft.Maui.ApplicationModel.Platform.CurrentActivity as Activity;
                if (activity == null) return;

                var window = activity.Window;
                var decorView = window.DecorView;

                var controller = new WindowInsetsControllerCompat(window, decorView);
                // hide status + navigation bars
                controller.Hide(WindowInsetsCompat.Type.SystemBars());
                // allow transient reveal by swipe
                controller.SystemBarsBehavior = WindowInsetsControllerCompat.BehaviorShowTransientBarsBySwipe;
            }
            catch (System.Exception ex)
            {
                try { Log(ex, "HideNavView (Android)", this); } catch { System.Diagnostics.Debug.WriteLine(ex); }
            }
#endif
        }
    }
}
#endif