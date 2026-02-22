using Android.App;
using Android.Content;
using Android.Content.PM;
using Android.OS;
using AndroidX.Core.View;
using Android.Runtime;
using projectFrameCut.Setting.SettingManager;
using System;

namespace projectFrameCut.Platforms.Android
{
    [Activity(
        Theme = "@style/AppTheme",
        MainLauncher = true,
        ResizeableActivity = true,
        LaunchMode = LaunchMode.SingleTop,
        ConfigurationChanges = ConfigChanges.ScreenSize | ConfigChanges.Orientation | ConfigChanges.UiMode | ConfigChanges.ScreenLayout | ConfigChanges.SmallestScreenSize | ConfigChanges.Density,
        Name = "com.hexadecimal0x12e.projectFrameCut.MainActivity",
        Label = "projectFrameCut"
        )]
    [IntentFilter([Intent.ActionView],
        Categories = new[] { Intent.CategoryDefault, Intent.CategoryBrowsable },
        DataScheme = "pjfc")]
    [IntentFilter([Intent.ActionView],
        Categories = new[] { Intent.CategoryDefault, Intent.CategoryBrowsable },
        DataScheme = "pjfcAndroid")]
    [IntentFilter([Intent.ActionView],
        Categories = new[] { Intent.CategoryDefault, Intent.CategoryBrowsable },
        DataScheme = "https",
        DataHost = "pjfc.hexadecimal0x12e.com",
        DataPath = "/launch")]
    [IntentFilter([Intent.ActionView],
        Categories = new[] { Intent.CategoryDefault, Intent.CategoryBrowsable },
        DataScheme = "https",
        DataHost = "pjfc.wonderhoy.xyz",// in future this domain will get ICP and use as secondary domain in China Mainland
        DataPath = "/launch")]
    public class MainActivity : MauiAppCompatActivity
    {
        protected override void OnCreate(Bundle? savedInstanceState)
        {
            base.OnCreate(savedInstanceState);

            // Ensure the system windows (status/navigation bars) are not drawn over the app content.
            // This prevents MAUI content from appearing under the status bar on many devices.
            try
            {
                WindowCompat.SetDecorFitsSystemWindows(Window, true);
            }
            catch
            {
                // ignore on older platforms
            }
        }

        protected override void OnDestroy()
        {
            base.OnDestroy();

            // Best-effort flush settings on activity destroy. Block briefly to increase chance of persistence.
            try
            {
                Log("OnDestroy() is called. Calling OnClosing() for plugins...");
                var flushTask = SettingsManager.FlushAndStopAsync();
                // Wait up to 2 seconds for flush to complete to avoid long ANR.
                flushTask.Wait(TimeSpan.FromSeconds(2));
                try
                {
                    foreach (var item in Render.Plugin.PluginManager.LoadedPlugins)
                    {
                        try
                        {
                            item.Value.OnClosing();
                        }
                        catch { }
                    }
                }
                catch { }
            }
            catch
            {
                // ignore errors during shutdown
            }
        }
    }
}
