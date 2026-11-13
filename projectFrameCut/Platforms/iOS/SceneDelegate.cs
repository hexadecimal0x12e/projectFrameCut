using Foundation;
using UIKit;

namespace projectFrameCut.Platforms.iOS;

[Register("FrameCutSceneDelegate")]
public sealed class SceneDelegate : MauiUISceneDelegate
{
    internal static event EventHandler<UIWindowScene>? GeometryChanged;

    public override void DidUpdateCoordinateSpace(UIWindowScene windowScene, IUICoordinateSpace previousCoordinateSpace,
        UIInterfaceOrientation previousInterfaceOrientation, UITraitCollection previousTraitCollection)
    {
        base.DidUpdateCoordinateSpace(windowScene, previousCoordinateSpace, previousInterfaceOrientation, previousTraitCollection);
        GeometryChanged?.Invoke(this, windowScene);
    }

    public override void OnActivated(UIScene scene)
    {
        base.OnActivated(scene);
        if (scene is UIWindowScene windowScene) GeometryChanged?.Invoke(this, windowScene);
    }
}
