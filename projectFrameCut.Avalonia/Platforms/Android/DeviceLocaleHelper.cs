using System.Globalization;
#if ANDROID
using Java.Util;
using Android.OS;
#endif

namespace projectFrameCut.Platforms.Android
{
    public static class DeviceLocaleHelper
    {

        public static string GetDeviceLanguageTag()
        {
#if ANDROID
            try
            {
                if (Build.VERSION.SdkInt >= BuildVersionCodes.N)
                {
                    var list = global::Android.OS.LocaleList.Default;
                    if (list != null && !list.IsEmpty)
                    {
                        var top = list.Get(0);
                        try { return top.ToLanguageTag(); }
                        catch { return FormatLocale(top); }
                    }
                }

                var locale = Java.Util.Locale.Default;
                try { return locale.ToLanguageTag(); }
                catch { return FormatLocale(locale); }
            }
            catch
            {
                return CultureInfo.CurrentCulture.Name;
            }
#else
            return CultureInfo.CurrentCulture.Name;
#endif
        }

        private static string FormatLocale(Java.Util.Locale l)
        {
            if (l == null) return CultureInfo.CurrentCulture.Name;
            var lang = l.Language ?? "";
            var country = l.Country ?? "";
            return string.IsNullOrEmpty(country) ? lang : $"{lang}-{country}";
        }


        public static CultureInfo GetDeviceCultureInfo()
        {
            var tag = GetDeviceLanguageTag(); 
            try
            {
                var locateName = tag.Split('-').First() switch //yes, on android there is something like 'en-CN', wtf???
                {
                    "zh" => tag.Contains("CN") ? "zh-CN" : (tag.Contains("TW") || tag.Contains("HK")) ? "zh-TW" : tag,
                    "en" => "en-US",
                    "ja" => "ja-JP",
                    "ko" => "ko-KR",
                    "fr" => "fr-FR",
                    _ => tag,
                };
                return new CultureInfo(locateName);
            }
            catch
            {
                try { return new CultureInfo(tag.Replace('_', '-')); }
                catch { return CultureInfo.InvariantCulture; }
            }
        }
    }
}