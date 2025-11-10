using Android.App;
using Android.Content.PM;
using Android.OS;

namespace projectFrameCut.Platforms.Android
{
    [Activity(
        Theme = "@style/MainTheme",
        MainLauncher = true,
        LaunchMode = LaunchMode.SingleTop,
        ConfigurationChanges = ConfigChanges.ScreenSize | ConfigChanges.Orientation | ConfigChanges.UiMode | ConfigChanges.ScreenLayout | ConfigChanges.SmallestScreenSize | ConfigChanges.Density,
        Name = "com.hexadecimal0x12e.projectframecut.MainActivity",
        Label = "projectFrameCut"
        )]
    public class MainActivity : MauiAppCompatActivity
    {
    }
}
