using UIKit;
using Microsoft.Maui.Controls.Platform;
using Microsoft.Maui.Controls;
using Microsoft.Maui.Platform;

namespace projectFrameCut.Platforms.iOS
{
    public static class BorderExtensions
    {
        /// <summary>
        /// 给 Border 添加液态玻璃模糊效果（iOS 专用）
        /// </summary>
        /// <param name="border">要应用效果的 Border</param>
        /// <param name="style">模糊样式，如 SystemMaterial、Light、Dark 等</param>
        /// <param name="cornerRadius">圆角半径</param>
        /// <param name="opacity">透明度 (0 ~ 1)</param>
        public static void AddGlassEffect(this View view,
                                          string style = "SystemMaterial",
                                          double cornerRadius = 20,
                                          double opacity = 1.0)
        {
#if IOS
            // 获取原生 UIView
            var nativeView = view.ToPlatform(Microsoft.Maui.Controls.Application.Current.MainPage.Handler.MauiContext);

            if (nativeView is null)
                return;

            // 查找是否已有 blurView，避免重复添加
            foreach (var subview in nativeView.Subviews)
            {
                if (subview is UIVisualEffectView)
                    return;
            }

            // 创建模糊效果
            var blurEffect = UIBlurEffect.FromStyle(GetBlurStyle(style));
            var blurView = new UIVisualEffectView(blurEffect)
            {
                Alpha = (float)opacity,
                Frame = nativeView.Bounds,
                AutoresizingMask = UIViewAutoresizing.FlexibleWidth | UIViewAutoresizing.FlexibleHeight
            };

            // 设置圆角
            nativeView.Layer.CornerRadius = (float)cornerRadius;
            nativeView.ClipsToBounds = true;

            // 插入到背景层（最底层）
            nativeView.InsertSubview(blurView, 0);
#endif
        }

#if IOS
        static UIBlurEffectStyle GetBlurStyle(string style)
        {
            return style switch
            {
                "Light" => UIBlurEffectStyle.Light,
                "Dark" => UIBlurEffectStyle.Dark,
                "ExtraLight" => UIBlurEffectStyle.ExtraLight,
                "SystemThinMaterial" => UIBlurEffectStyle.SystemThinMaterial,
                "SystemMaterial" => UIBlurEffectStyle.SystemMaterial,
                "SystemChromeMaterial" => UIBlurEffectStyle.SystemChromeMaterial,
                "SystemUltraThinMaterial" => UIBlurEffectStyle.SystemUltraThinMaterial,
                _ => UIBlurEffectStyle.SystemMaterial
            };
        }
#endif
    }
}

