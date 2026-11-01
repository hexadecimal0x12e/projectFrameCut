using Foundation;
using UIKit;

namespace projectFrameCut.Platforms.iOS
{
    [Register("AppDelegate")]
    public class AppDelegate : MauiUIApplicationDelegate
    {
        protected override MauiApp CreateMauiApp() => MauiProgram.CreateMauiApp();

        public override UISceneConfiguration GetConfiguration(UIApplication application, UISceneSession connectingSceneSession,
            UISceneConnectionOptions options)
        {
            var configuration = base.GetConfiguration(application, connectingSceneSession, options);
            configuration.SceneType = typeof(UIWindowScene);
            configuration.DelegateType = typeof(SceneDelegate);
            return configuration;
        }
    }
}
