using System;
using System.Collections.Generic;
using System.Text;
#if iDevices
using UIKit;
using CoreAnimation;
#endif

namespace projectFrameCut.Services
{
    internal class UISafeZoneServices
    {
        public  static double GetSafeZone()
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


    }


}
