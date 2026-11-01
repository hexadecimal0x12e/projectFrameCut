using Android.App;
using Android.Content;
using Android.Content.PM;
using Android.Content.Res;
using Android.Hardware;
using Android.Hardware.Display;
using Android.OS;
using Android.Util;
using Android.Views;
using AndroidX.Core.Util;
using AndroidX.Window;
using AndroidX.Window.Java.Layout;
using AndroidX.Window.Layout;
using projectFrameCut.ApplicationAPIBase.Project;
using projectFrameCut.Platforms.Android;

namespace projectFrameCut.Services;

internal sealed class DraftFormMonitor : Java.Lang.Object, IConsumer, ISensorEventListener, DisplayManager.IDisplayListener
{
    private readonly Activity _activity;
    private readonly bool _isTablet;
    private readonly Handler _handler = new(Looper.MainLooper!);
    private WindowInfoTrackerCallbackAdapter? _windowTracker;
    private SensorManager? _sensors;
    private DisplayManager? _displays;
    private ViewTreeObserver? _viewTree;
    private bool _isFoldable;
    private bool _hasFoldingFeature;
    private bool? _hingeOpen;
    private int _displayId;
    private bool _disposed;

    public event EventHandler? Changed;

    public DraftFormMonitor(DraftPage page)
    {
        _activity = (Activity)page.Window.Handler.PlatformView!;
        try
        {
            _displays = _activity.GetSystemService(Context.DisplayService) as DisplayManager;
            using var metrics = new DisplayMetrics();
            _displays?.GetDisplay(Display.DefaultDisplay)?.GetRealMetrics(metrics);
            _isTablet = metrics.Density > 0
                ? Math.Min(metrics.WidthPixels, metrics.HeightPixels) / metrics.Density >= 600
                : (global::Android.App.Application.Context.Resources?.Configuration?.SmallestScreenWidthDp ?? 0) >= 600;
            _sensors = _activity.GetSystemService(Context.SensorService) as SensorManager;
            var hinge = _sensors?.GetDefaultSensor(SensorType.HingeAngle);
            _isFoldable = hinge is not null || _activity.PackageManager?.HasSystemFeature(PackageManager.FeatureSensorHingeAngle) == true;
            if (hinge is not null) _sensors!.RegisterListener(this, hinge, SensorDelay.Normal);
            var tracker = WindowInfoTracker.GetOrCreate(_activity);
            if (WindowSdkExtensions.Instance.ExtensionVersion >= 6)
                _isFoldable |= tracker.SupportedPostures.Count > 0;
            _windowTracker = new(tracker);
            _windowTracker.AddWindowLayoutInfoListener(_activity, _activity.MainExecutor!, this);
            _displays?.RegisterDisplayListener(this, _handler);
            _displayId = _activity.Display?.DisplayId ?? Display.DefaultDisplay;
            _viewTree = _activity.Window?.DecorView?.ViewTreeObserver;
            if (_viewTree is not null) _viewTree.GlobalLayout += OnGlobalLayout;
            if (_activity is MainActivity activity) activity.FormEnvironmentChanged += OnEnvironmentChanged;
        }
        catch
        {
            Dispose();
            throw;
        }
    }

    public DraftPageForm GetForm(double width)
    {
        if (DeviceInfo.Idiom == DeviceIdiom.Desktop) return DraftPageForm.Workspace;
        if (_isFoldable)
        {
            // WindowManager may omit the fold on the cover display or in a small window.
            var bounds = _activity.WindowManager?.MaximumWindowMetrics.Bounds;
            bool open = _hingeOpen ?? (_hasFoldingFeature || bounds is not null &&
                Math.Min(bounds.Width(), bounds.Height()) / (_activity.Resources?.DisplayMetrics?.Density ?? 1) >= 600);
            return open ? DraftPageForm.Workspace : DraftPageForm.Mobile;
        }
        if (_isTablet)
            return width > 0 && width < 840 ? DraftPageForm.Mobile : DraftPageForm.Workspace;
        if (_activity.Display is { DisplayId: not Display.DefaultDisplay }) return DraftPageForm.Workspace;
        return _activity.Resources?.Configuration?.Orientation == Orientation.Landscape
            ? DraftPageForm.Workspace : DraftPageForm.Mobile;
    }

    public void Accept(Java.Lang.Object? value)
    {
        if (_disposed || value is not WindowLayoutInfo info) return;
        bool hasFold = info.DisplayFeatures.OfType<IFoldingFeature>().Any();
        if (_hasFoldingFeature == hasFold) return;
        _hasFoldingFeature = hasFold;
        _isFoldable |= _hasFoldingFeature;
        Changed?.Invoke(this, EventArgs.Empty);
    }

    public void OnSensorChanged(SensorEvent? e)
    {
        if (_disposed || e?.Sensor?.Type != SensorType.HingeAngle || e.Values is not { Count: > 0 }) return;
        bool open = e.Values[0] > 1;
        if (_hingeOpen == open) return;
        _hingeOpen = open;
        Changed?.Invoke(this, EventArgs.Empty);
    }

    public void OnAccuracyChanged(Sensor? sensor, SensorStatus accuracy) { }
    public void OnDisplayAdded(int displayId) => OnEnvironmentChanged(this, EventArgs.Empty);
    public void OnDisplayRemoved(int displayId) => OnEnvironmentChanged(this, EventArgs.Empty);
    public void OnDisplayChanged(int displayId) => OnEnvironmentChanged(this, EventArgs.Empty);

    private void OnEnvironmentChanged(object? sender, EventArgs e)
    {
        if (!_disposed) Changed?.Invoke(this, EventArgs.Empty);
    }

    private void OnGlobalLayout(object? sender, EventArgs e)
    {
        int displayId = _activity.Display?.DisplayId ?? Display.DefaultDisplay;
        if (_displayId == displayId) return;
        _displayId = displayId;
        OnEnvironmentChanged(sender, e);
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing && !_disposed)
        {
            _disposed = true;
            if (_activity is MainActivity activity) activity.FormEnvironmentChanged -= OnEnvironmentChanged;
            if (_viewTree?.IsAlive == true) _viewTree.GlobalLayout -= OnGlobalLayout;
            _windowTracker?.RemoveWindowLayoutInfoListener(this);
            _windowTracker?.Dispose();
            _sensors?.UnregisterListener(this);
            _displays?.UnregisterDisplayListener(this);
            _sensors?.Dispose();
            _displays?.Dispose();
            _handler.Dispose();
            Changed = null;
        }
        base.Dispose(disposing);
    }
}
