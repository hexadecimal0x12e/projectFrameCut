using System;
using System.Collections.Generic;
using System.Text;
#if iDevices
using UIKit;
using CoreAnimation;
#endif

namespace projectFrameCut.Services
{
    internal class UIServices
    {
        public static double GetWindowCornerRadius()
        {
            if (!SettingsManager.IsBoolSettingTrue("ui_ForceUseUserDefinedSafeZone"))
            {
#if WINDOWS
                if (OperatingSystem.IsWindowsVersionAtLeast(10, 0, 22000, 0)) //Windows 11
                {
                    return 8; //a fixed value, see in WinUI3 Gallery
                }
                else
                {
                    return 0;
                }
#elif MACCATALYST
                try
                {
                    double radius = 0;
                    UIKit.UIWindow window = null;
                    var app = UIKit.UIApplication.SharedApplication;
                    if (app != null)
                    {
                        window = app.KeyWindow;
                        if (window == null && app.Windows != null)
                        {
                            foreach (var w in app.Windows)
                            {
                                if (w.IsKeyWindow)
                                {
                                    window = w;
                                    break;
                                }
                            }
                            if (window == null && app.Windows.Length > 0)
                                window = app.Windows[0];
                        }
                    }

                    if (window?.Layer != null)
                        radius = window.Layer.CornerRadius;

                    if (radius > 0)
                        return radius;
                }
                catch
                {
                }

                return double.TryParse(SettingsManager.GetSetting("ui_SafeZoneCornerRadius", "10"), out var result1) ? result1 : 10;
#else 
                return double.TryParse(SettingsManager.GetSetting("ui_SafeZoneCornerRadius", "10"), out var result1) ? result1 : 10;
#endif
            }

            return double.TryParse(SettingsManager.GetSetting("ui_SafeZoneCornerRadius", "10"), out var result) ? result : 10;
        }

        public static async void RegisterSelectOrContextMenu(Border border, Action? OnSelected = null, Action? OnClicked = null, Action? OnContextMenuClick = null, int ContextMenuMinTime = 500)
        {
#if MACCATALYST || WINDOWS
            if (OnSelected is not null)
            {
                var selectTap = new TapGestureRecognizer { NumberOfTapsRequired = 1, Buttons = ButtonsMask.Primary };
                selectTap.Tapped += async (_, __) =>
                {
                    OnSelected();
                };
                border.GestureRecognizers.Add(selectTap);
            }

            if (OnClicked is not null)
            {
                var addTap = new TapGestureRecognizer { NumberOfTapsRequired = 2, Buttons = ButtonsMask.Primary };
                addTap.Tapped += (_, __) =>
                {
                    OnClicked();
                };
                border.GestureRecognizers.Add(addTap);
            }

            if (OnContextMenuClick is not null)
            {
                var rightTap = new TapGestureRecognizer { NumberOfTapsRequired = 1, Buttons = ButtonsMask.Secondary };
                rightTap.Tapped += async (_, __) =>
                {
                    OnContextMenuClick();
                };
                border.GestureRecognizers.Add(rightTap);
            }
#elif ANDROID || IOS               
            var pointerGesture = new PointerGestureRecognizer();
            DateTime pointerDownTime = DateTime.MinValue;
            pointerGesture.PointerPressed += (_, __) => pointerDownTime = DateTime.Now;
            pointerGesture.PointerReleased += async (_, __) =>
            {
                var duration = (DateTime.Now - pointerDownTime).TotalMilliseconds;
                if (duration >= ContextMenuMinTime)
                {
                    OnContextMenuClick?.Invoke(); 
                }
                else
                {
                    OnClicked?.Invoke();    
                }
            };
            border.GestureRecognizers.Add(pointerGesture);
#endif
        }


    }

#if ANDROID
    public class DisableScrollListener : Java.Lang.Object, Android.Views.View.IOnTouchListener
    {
        public bool OnTouch(Android.Views.View? v, Android.Views.MotionEvent e)
        {
            return true;
        }
    }
#endif


}
