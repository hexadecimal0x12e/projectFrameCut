#if ANDROID
using Android.Content;
using Android.Views;
using Microsoft.Maui.Handlers;
using Microsoft.Maui.Platform;

namespace projectFrameCut.Platforms.Android;

internal sealed class InteractableEditorHandler : ContentViewHandler
{
    protected override ContentViewGroup CreatePlatformView() => new AndroidViewportContentViewGroup(Context);

    protected override void ConnectHandler(ContentViewGroup platformView)
    {
        base.ConnectHandler(platformView);
        ((AndroidViewportContentViewGroup)platformView).Editor = VirtualView as InteractableEditor.InteractableEditor;
    }

    protected override void DisconnectHandler(ContentViewGroup platformView)
    {
        ((AndroidViewportContentViewGroup)platformView).Editor = null;
        base.DisconnectHandler(platformView);
    }
}

internal sealed class AndroidViewportContentViewGroup : ContentViewGroup
{
    private readonly ScaleGestureDetector _scaleDetector;
    private bool _intercepting;
    private bool _isScaling;
    private double _scale = 1d;

    public AndroidViewportContentViewGroup(Context context) : base(context)
    {
        _scaleDetector = new ScaleGestureDetector(context, new ScaleListener(this));
    }

    public InteractableEditor.InteractableEditor? Editor { get; set; }

    public override bool OnInterceptTouchEvent(MotionEvent? e)
    {
        if (e is null) return base.OnInterceptTouchEvent(null);

        _scaleDetector.OnTouchEvent(e);
        if (e.ActionMasked == MotionEventActions.PointerDown && e.PointerCount >= 2)
        {
            _intercepting = true;
        }

        var intercept = _intercepting;
        if (e.ActionMasked is MotionEventActions.Up or MotionEventActions.Cancel)
        {
            _intercepting = false;
            if (_isScaling)
            {
                _isScaling = false;
                Editor?.EndAndroidViewportGesture();
            }
        }

        return intercept || base.OnInterceptTouchEvent(e);
    }

    public override bool OnTouchEvent(MotionEvent? e) => _intercepting || _isScaling || base.OnTouchEvent(e);

    private sealed class ScaleListener(AndroidViewportContentViewGroup owner) : ScaleGestureDetector.SimpleOnScaleGestureListener
    {
        public override bool OnScaleBegin(ScaleGestureDetector detector)
        {
            if (owner.Editor is null) return false;
            owner._isScaling = true;
            owner._scale = 1d;
            owner.Editor.BeginAndroidViewportGesture(owner.GetPoint(detector.FocusX, detector.FocusY));
            return true;
        }

        public override bool OnScale(ScaleGestureDetector detector)
        {
            owner._scale *= detector.ScaleFactor;
            owner.Editor?.UpdateAndroidViewportGesture(owner._scale, owner.GetPoint(detector.FocusX, detector.FocusY));
            return true;
        }

        public override void OnScaleEnd(ScaleGestureDetector detector)
        {
            owner._isScaling = false;
            owner.Editor?.EndAndroidViewportGesture();
        }
    }

    private Point GetPoint(float x, float y) => new(Context.FromPixels(x), Context.FromPixels(y));
}
#endif
