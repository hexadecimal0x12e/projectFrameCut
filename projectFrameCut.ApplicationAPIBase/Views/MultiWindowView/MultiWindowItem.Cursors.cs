#if WINDOWS
using System.Reflection;
using System.Runtime.CompilerServices;
using Microsoft.Maui.Controls;
using Microsoft.UI.Input;
using Microsoft.UI.Xaml;

namespace projectFrameCut.ApplicationAPIBase.Views.MultiWindowView;

public partial class MultiWindowItem
{
    private static readonly ConditionalWeakTable<View, WindowsResizeCursorRegistration> WindowsCursorRegistrations = new();

    static partial void ConfigureResizeCursor(View handle, ResizeCursorKind cursorKind)
    {
        if (WindowsCursorRegistrations.TryGetValue(handle, out var registration))
        {
            registration.SetCursor(cursorKind);
            return;
        }

        WindowsCursorRegistrations.Add(handle, new WindowsResizeCursorRegistration(handle, cursorKind));
    }

    private sealed class WindowsResizeCursorRegistration
    {
        private static readonly PropertyInfo? ProtectedCursorProperty = typeof(UIElement).GetProperty(
            "ProtectedCursor",
            BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);

        private static readonly InputSystemCursor HorizontalCursor =
            InputSystemCursor.Create(InputSystemCursorShape.SizeWestEast);
        private static readonly InputSystemCursor VerticalCursor =
            InputSystemCursor.Create(InputSystemCursorShape.SizeNorthSouth);
        private static readonly InputSystemCursor NorthwestSoutheastCursor =
            InputSystemCursor.Create(InputSystemCursorShape.SizeNorthwestSoutheast);
        private static readonly InputSystemCursor NortheastSouthwestCursor =
            InputSystemCursor.Create(InputSystemCursorShape.SizeNortheastSouthwest);

        private readonly View _handle;
        private ResizeCursorKind _cursorKind;

        public WindowsResizeCursorRegistration(View handle, ResizeCursorKind cursorKind)
        {
            _handle = handle;
            _cursorKind = cursorKind;
            _handle.HandlerChanged += OnHandlerChanged;
            _handle.Loaded += OnLoaded;
            ApplyCursor();
        }

        public void SetCursor(ResizeCursorKind cursorKind)
        {
            _cursorKind = cursorKind;
            ApplyCursor();
        }

        private void OnHandlerChanged(object? sender, EventArgs e) => ApplyCursor();

        private void OnLoaded(object? sender, EventArgs e) => ApplyCursor();

        private void ApplyCursor()
        {
            if (_handle.Handler?.PlatformView is not UIElement platformView || ProtectedCursorProperty == null)
                return;

            var cursor = _cursorKind switch
            {
                ResizeCursorKind.Horizontal => HorizontalCursor,
                ResizeCursorKind.Vertical => VerticalCursor,
                ResizeCursorKind.NorthwestSoutheast => NorthwestSoutheastCursor,
                ResizeCursorKind.NortheastSouthwest => NortheastSouthwestCursor,
                _ => throw new ArgumentOutOfRangeException()
            };

            ProtectedCursorProperty.SetValue(platformView, cursor);
        }
    }
}
#elif IOS || MACCATALYST
using System.Runtime.CompilerServices;
using CoreGraphics;
using Microsoft.Maui.Controls;
using UIKit;

namespace projectFrameCut.ApplicationAPIBase.Views.MultiWindowView;

public partial class MultiWindowItem
{
    private static readonly ConditionalWeakTable<View, AppleResizeCursorRegistration> AppleCursorRegistrations = new();

    static partial void ConfigureResizeCursor(View handle, ResizeCursorKind cursorKind)
    {
        if (AppleCursorRegistrations.TryGetValue(handle, out var registration))
        {
            registration.SetCursor(cursorKind);
            return;
        }

        AppleCursorRegistrations.Add(handle, new AppleResizeCursorRegistration(handle, cursorKind));
    }

    private sealed class AppleResizeCursorRegistration
    {
        private readonly View _handle;
        private ResizeCursorKind _cursorKind;
        private UIView? _platformView;
        private UIPointerInteraction? _interaction;
        private AppleResizePointerDelegate? _pointerDelegate;

        public AppleResizeCursorRegistration(View handle, ResizeCursorKind cursorKind)
        {
            _handle = handle;
            _cursorKind = cursorKind;
            _handle.HandlerChanged += OnHandlerChanged;
            _handle.Loaded += OnLoaded;
            _handle.Unloaded += OnUnloaded;
            ApplyCursor();
        }

        public void SetCursor(ResizeCursorKind cursorKind)
        {
            if (_cursorKind == cursorKind) return;

            _cursorKind = cursorKind;
            DetachCursor();
            ApplyCursor();
        }

        private void OnHandlerChanged(object? sender, EventArgs e)
        {
            DetachCursor();
            ApplyCursor();
        }

        private void OnLoaded(object? sender, EventArgs e) => ApplyCursor();

        private void OnUnloaded(object? sender, EventArgs e) => DetachCursor();

        private void ApplyCursor()
        {
            if (_interaction != null || _handle.Handler?.PlatformView is not UIView platformView)
                return;

            _platformView = platformView;
            _pointerDelegate = new AppleResizePointerDelegate(_cursorKind);
            _interaction = new UIPointerInteraction(_pointerDelegate);
            _platformView.AddInteraction(_interaction);
        }

        private void DetachCursor()
        {
            if (_platformView != null && _interaction != null)
                _platformView.RemoveInteraction(_interaction);

            _interaction?.Dispose();
            _pointerDelegate?.Dispose();
            _interaction = null;
            _pointerDelegate = null;
            _platformView = null;
        }
    }

    private sealed class AppleResizePointerDelegate : UIPointerInteractionDelegate
    {
        private readonly UIPointerShape _shape;
        private readonly UIPointerStyle _style;

        public AppleResizePointerDelegate(ResizeCursorKind cursorKind)
        {
            using var path = CreateResizePointerPath(cursorKind);
            _shape = UIPointerShape.Create(path);
            _style = UIPointerStyle.Create(_shape, UIAxis.Neither);
        }

        public override UIPointerStyle? GetStyleForRegion(UIPointerInteraction interaction, UIPointerRegion region) => _style;

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                _style.Dispose();
                _shape.Dispose();
            }

            base.Dispose(disposing);
        }

        private static UIBezierPath CreateResizePointerPath(ResizeCursorKind cursorKind)
        {
            ReadOnlySpan<CGPoint> horizontalArrow =
            [
                new(-11, 0),
                new(-6, -5),
                new(-6, -2),
                new(6, -2),
                new(6, -5),
                new(11, 0),
                new(6, 5),
                new(6, 2),
                new(-6, 2),
                new(-6, 5)
            ];

            var angle = cursorKind switch
            {
                ResizeCursorKind.Horizontal => 0d,
                ResizeCursorKind.Vertical => Math.PI / 2d,
                ResizeCursorKind.NorthwestSoutheast => Math.PI / 4d,
                ResizeCursorKind.NortheastSouthwest => -Math.PI / 4d,
                _ => throw new ArgumentOutOfRangeException(nameof(cursorKind))
            };
            var sin = Math.Sin(angle);
            var cos = Math.Cos(angle);

            var path = new UIBezierPath();
            for (var index = 0; index < horizontalArrow.Length; index++)
            {
                var point = horizontalArrow[index];
                var rotated = new CGPoint(
                    (point.X * cos) - (point.Y * sin),
                    (point.X * sin) + (point.Y * cos));

                if (index == 0)
                    path.MoveTo(rotated);
                else
                    path.AddLineTo(rotated);
            }

            path.ClosePath();
            return path;
        }
    }
}
#endif
