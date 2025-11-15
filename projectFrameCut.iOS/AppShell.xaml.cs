using System;
#if IOS
using UIKit;
using System.Runtime.InteropServices;
#endif

namespace projectFrameCut
{
    public partial class AppShell : Shell
    {
        public static AppShell instance;

        public AppShell()
        {
            instance = this;
            InitializeComponent();
            Title = Localized.AppBrand;

#if iDevices
            if (OperatingSystem.IsIOSVersionAtLeast(13))
            {
                // 设置图标（符号名称可替换）
                SetSfSymbol(ProjectsTab, "folder");
                SetSfSymbol(AssetsTab, "photo.on.rectangle");
                SetSfSymbol(DebugTab, "wrench.and.screwdriver");
            }
#elif ANDROID
            ProjectsTab.Icon = ImageSource.FromFile("icon_project.svg");
            AssetsTab.Icon = ImageSource.FromFile("icon_asset.svg");
            DebugTab.Icon = ImageSource.FromFile("icon_debug.svg");
#endif
        }

#if iDevices
        private void SetSfSymbol(ShellItem tab, string symbolName, UIImageSymbolWeight weight = UIImageSymbolWeight.Regular, UIColor? tint = null, double pointSize = 0)
        {
            try
            {
                UIImageSymbolConfiguration config;

                if (pointSize > 0)
                {
                    // 使用 NFloat 作为点大小，不使用 ApplyConfiguration，改为直接使用组合重载
                    config = UIImageSymbolConfiguration.Create(new NFloat(pointSize), weight);
                }
                else
                {
                    config = UIImageSymbolConfiguration.Create(weight);
                }

                var uiImage = UIImage.GetSystemImage(symbolName, config);
                if (uiImage == null) return;

                // 颜色交给 TabBar 的 TintColor/UnselectedItemTintColor 管理，避免使用不可用的 ImageWithTintColor
                // 如需强制原色，可启用下行（iOS 支持）：
                // uiImage = uiImage.ImageWithRenderingMode(UIImageRenderingMode.AlwaysOriginal);

                tab.Icon = ImageSource.FromStream(() => uiImage.AsPNG().AsStream());
            }
            catch (Exception ex)
            {
                Log(ex, $"Set SF Symbol {symbolName} failed.", this);
            }
        }
#endif
    }
}
