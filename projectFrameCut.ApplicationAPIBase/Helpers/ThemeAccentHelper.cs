using System;

#if ANDROID
using Android.Util;
#elif WINDOWS
using Windows.UI.ViewManagement;
#endif

namespace projectFrameCut.ApplicationAPIBase.Helpers
{
    public static class ThemeAccentHelper
    {
        public static Microsoft.Maui.Graphics.Color? GetSystemAccentColor()
        {
#if ANDROID
            try
            {
                var context = Android.App.Application.Context;
                var typedValue = new TypedValue();
                if (context.Theme.ResolveAttribute(Android.Resource.Attribute.ColorAccent, typedValue, true))
                {
                    var c = new Android.Graphics.Color(typedValue.Data);
                    return Microsoft.Maui.Graphics.Color.FromRgba(c.R, c.G, c.B, c.A / 255.0);
                }
            }
            catch
            {
            }
            return null;
#elif WINDOWS
            try
            {
                var uiSettings = new UISettings();
                var c = uiSettings.GetColorValue(UIColorType.Accent);
                return Microsoft.Maui.Graphics.Color.FromRgba(c.R, c.G, c.B, c.A / 255.0);
            }
            catch
            {
            }
            return null;
#else
            return null;
#endif
        }
    }
}
