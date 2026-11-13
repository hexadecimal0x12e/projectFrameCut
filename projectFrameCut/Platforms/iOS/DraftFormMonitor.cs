using Foundation;
using Microsoft.Maui.Devices;
using projectFrameCut.ApplicationAPIBase.Project;
using projectFrameCut.Platforms.iOS;
using UIKit;

namespace projectFrameCut.Services;

internal sealed class DraftFormMonitor : IDisposable
{
    private readonly DraftPage _page;
    private readonly FormObserverView? _observer;
    private readonly List<NSObject> _notifications = [];
    private UIWindow? _window;
    private HingeStateObserver? _hinge;
    private bool _isFoldable;
    private bool? _hingeOpen;
    private (nint Window, nint Screen, UIInterfaceOrientation Orientation, double Width, double Height, bool Foldable, bool? Open)? _environment;
    private bool _disposed;

    public event EventHandler? Changed;

    public DraftFormMonitor(DraftPage page)
    {
        _page = page;
        try
        {
            if (page.Handler?.PlatformView is UIView view)
            {
                _observer = new FormObserverView(OnEnvironmentChanged)
                {
                    Frame = view.Bounds,
                    AutoresizingMask = UIViewAutoresizing.FlexibleWidth | UIViewAutoresizing.FlexibleHeight,
                    UserInteractionEnabled = false,
                    Hidden = true
                };
                view.AddSubview(_observer);
            }
            SceneDelegate.GeometryChanged += OnSceneGeometryChanged;
            _notifications.Add(UIScreen.Notifications.ObserveDidConnect((_, _) => OnEnvironmentChanged()));
            _notifications.Add(UIScreen.Notifications.ObserveDidDisconnect((_, _) => OnEnvironmentChanged()));
            _notifications.Add(UIScreen.Notifications.ObserveModeDidChange((_, _) => OnEnvironmentChanged()));
            RefreshWindow();
        }
        catch
        {
            Dispose();
            throw;
        }
    }

    private void RefreshWindow()
    {
        var window = (_page.Handler?.PlatformView as UIView)?.Window ?? _page.Window?.Handler?.PlatformView as UIWindow;
        if (_window?.Handle == window?.Handle) return;
        _hinge?.Dispose();
        _hinge = null;
        _window = window;
        if (window is not null) _hinge = new HingeStateObserver(window, OnHingeChanged);
    }

    public DraftPageForm GetForm(double width)
    {
        RefreshWindow();
        if (DeviceInfo.Idiom == DeviceIdiom.Desktop) return DraftPageForm.Workspace;
        if (_isFoldable)
        {
            bool open = _hingeOpen ?? _window?.Screen.TraitCollection.HorizontalSizeClass == UIUserInterfaceSizeClass.Regular;
            return open ? DraftPageForm.Workspace : DraftPageForm.Mobile;
        }
        if (UIDevice.CurrentDevice.UserInterfaceIdiom == UIUserInterfaceIdiom.Pad)
            return width > 0 && width < 840 ? DraftPageForm.Mobile : DraftPageForm.Workspace;
        var screen = _window?.WindowScene?.Screen ?? _window?.Screen;
        if (screen is not null && screen.Handle != UIScreen.MainScreen.Handle) return DraftPageForm.Workspace;
        var orientation = _window?.WindowScene?.InterfaceOrientation ?? UIApplication.SharedApplication.StatusBarOrientation;
        return orientation is UIInterfaceOrientation.LandscapeLeft or UIInterfaceOrientation.LandscapeRight
            ? DraftPageForm.Workspace : DraftPageForm.Mobile;
    }

    private void OnHingeChanged(HingeStatus? status)
    {
        bool? open = status switch
        {
            HingeStatus.Closed => false,
            HingeStatus.PartiallyOpen or HingeStatus.FullyOpen => true,
            _ => null
        };
        bool foldable = _isFoldable || status.HasValue;
        if (_isFoldable == foldable && _hingeOpen == open) return;
        _isFoldable = foldable;
        _hingeOpen = open;
        OnEnvironmentChanged();
    }

    private void OnSceneGeometryChanged(object? sender, UIWindowScene scene)
    {
        if (_window?.WindowScene?.Handle == scene.Handle) OnEnvironmentChanged();
    }

    private void OnEnvironmentChanged()
    {
        if (_disposed) return;
        var window = (_page.Handler?.PlatformView as UIView)?.Window ?? _page.Window?.Handler?.PlatformView as UIWindow;
        var state = ((nint)(window?.Handle ?? default), (nint)(window?.Screen.Handle ?? default),
            window?.WindowScene?.InterfaceOrientation ?? UIInterfaceOrientation.Unknown,
            _page.Width, _page.Height, _isFoldable, _hingeOpen);
        if (_environment == state) return;
        _environment = state;
        Changed?.Invoke(this, EventArgs.Empty);
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        SceneDelegate.GeometryChanged -= OnSceneGeometryChanged;
        _hinge?.Dispose();
        foreach (var token in _notifications)
        {
            NSNotificationCenter.DefaultCenter.RemoveObserver(token);
            token.Dispose();
        }
        _notifications.Clear();
        _observer?.RemoveFromSuperview();
        _observer?.Dispose();
        Changed = null;
    }

    private sealed class FormObserverView(Action changed) : UIView
    {
        public override void LayoutSubviews()
        {
            base.LayoutSubviews();
            changed();
        }

        public override void MovedToWindow()
        {
            base.MovedToWindow();
            changed();
        }

        public override void TraitCollectionDidChange(UITraitCollection? previousTraitCollection)
        {
            base.TraitCollectionDidChange(previousTraitCollection);
            changed();
        }
    }
}
