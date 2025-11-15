using Android.App;
using Android.Content.PM;
using Android.OS;
using Android.Runtime;
using AndroidX.Core.View;

namespace projectFrameCut.Platforms.Android
{
    [Activity(
        Theme = "@style/Maui.SplashTheme",
        MainLauncher = true,
        LaunchMode = LaunchMode.SingleTop,
        ConfigurationChanges = ConfigChanges.ScreenSize | ConfigChanges.Orientation | ConfigChanges.UiMode | ConfigChanges.ScreenLayout | ConfigChanges.SmallestScreenSize | ConfigChanges.Density,
        Name = "com.hexadecimal0x12e.projectFrameCut.MainActivity",
        Label = "projectFrameCut"
        )]
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
    }
}
