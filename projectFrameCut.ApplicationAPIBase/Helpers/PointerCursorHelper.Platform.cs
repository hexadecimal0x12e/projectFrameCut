#if WINDOWS
using System.Reflection;
using System.Runtime.CompilerServices;
using Microsoft.Maui.Controls;
using Microsoft.UI.Input;
using Microsoft.UI.Xaml;

namespace projectFrameCut.ApplicationAPIBase.Helpers;

public static partial class PointerCursorHelper
{
    private static readonly ConditionalWeakTable<View, WindowsCursorRegistration> WindowsRegistrations = new();

    static partial void SetCursorCore(View view, PointerCursorKind? cursorKind)
    {
        if (WindowsRegistrations.TryGetValue(view, out var registration))
        {
            registration.SetCursor(cursorKind);
            return;
        }

        WindowsRegistrations.Add(view, new WindowsCursorRegistration(view, cursorKind));
    }

    private sealed class WindowsCursorRegistration
    {
        private static readonly PropertyInfo? ProtectedCursorProperty = typeof(UIElement).GetProperty(
            "ProtectedCursor",
            BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);

        private readonly View _view;
        private PointerCursorKind? _cursorKind;
        private static readonly InputSystemCursor HandCursor = InputSystemCursor.Create(InputSystemCursorShape.Hand);
        private static readonly InputSystemCursor MoveCursor = InputSystemCursor.Create(InputSystemCursorShape.SizeAll);
        private static readonly InputSystemCursor HorizontalResizeCursor = InputSystemCursor.Create(InputSystemCursorShape.SizeWestEast);
        private static readonly InputSystemCursor VerticalResizeCursor = InputSystemCursor.Create(InputSystemCursorShape.SizeNorthSouth);
        private static readonly InputSystemCursor NorthwestSoutheastResizeCursor = InputSystemCursor.Create(InputSystemCursorShape.SizeNorthwestSoutheast);
        private static readonly InputSystemCursor NortheastSouthwestResizeCursor = InputSystemCursor.Create(InputSystemCursorShape.SizeNortheastSouthwest);

        public WindowsCursorRegistration(View view, PointerCursorKind? cursorKind)
        {
            _view = view;
            _cursorKind = cursorKind;
            _view.HandlerChanged += OnHandlerChanged;
            _view.Loaded += OnLoaded;
            ApplyCursor();
        }

        public void SetCursor(PointerCursorKind? cursorKind)
        {
            _cursorKind = cursorKind;
            ApplyCursor();
        }

        private void OnHandlerChanged(object? sender, EventArgs e) => ApplyCursor();

        private void OnLoaded(object? sender, EventArgs e) => ApplyCursor();

        private void ApplyCursor()
        {
            if (_view.Handler?.PlatformView is not UIElement platformView || ProtectedCursorProperty is null)
                return;

            ProtectedCursorProperty.SetValue(platformView, _cursorKind switch
            {
                PointerCursorKind.Hand => HandCursor,
                PointerCursorKind.Move => MoveCursor,
                PointerCursorKind.HorizontalResize => HorizontalResizeCursor,
                PointerCursorKind.VerticalResize => VerticalResizeCursor,
                PointerCursorKind.NorthwestSoutheastResize => NorthwestSoutheastResizeCursor,
                PointerCursorKind.NortheastSouthwestResize => NortheastSouthwestResizeCursor,
                _ => null
            });
        }
    }
}
#elif IOS || MACCATALYST
using System.Runtime.CompilerServices;
using CoreGraphics;
using Microsoft.Maui.Controls;
using UIKit;

namespace projectFrameCut.ApplicationAPIBase.Helpers;

public static partial class PointerCursorHelper
{
    private static readonly ConditionalWeakTable<View, AppleCursorRegistration> AppleRegistrations = new();

    static partial void SetCursorCore(View view, PointerCursorKind? cursorKind)
    {
        if (AppleRegistrations.TryGetValue(view, out var registration))
        {
            registration.SetCursor(cursorKind);
            return;
        }

        AppleRegistrations.Add(view, new AppleCursorRegistration(view, cursorKind));
    }

    private sealed class AppleCursorRegistration
    {
        private readonly View _view;
        private PointerCursorKind? _cursorKind;
        private UIView? _platformView;
        private UIPointerInteraction? _interaction;
        private ApplePointerDelegate? _pointerDelegate;

        public AppleCursorRegistration(View view, PointerCursorKind? cursorKind)
        {
            _view = view;
            _cursorKind = cursorKind;
            _view.HandlerChanged += OnHandlerChanged;
            _view.Loaded += OnLoaded;
            _view.Unloaded += OnUnloaded;
            ApplyCursor();
        }

        public void SetCursor(PointerCursorKind? cursorKind)
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
            if (_interaction is not null || _cursorKind is null || _view.Handler?.PlatformView is not UIView platformView)
                return;

            _platformView = platformView;
            _pointerDelegate = new ApplePointerDelegate(_cursorKind.Value);
            _interaction = new UIPointerInteraction(_pointerDelegate);
            _platformView.AddInteraction(_interaction);
        }

        private void DetachCursor()
        {
            if (_platformView is not null && _interaction is not null)
                _platformView.RemoveInteraction(_interaction);

            _interaction?.Dispose();
            _pointerDelegate?.Dispose();
            _interaction = null;
            _pointerDelegate = null;
            _platformView = null;
        }
    }

    private sealed class ApplePointerDelegate : UIPointerInteractionDelegate
    {
        private readonly UIPointerShape _shape;
        private readonly UIPointerStyle _style;

        public ApplePointerDelegate(PointerCursorKind cursorKind)
        {
            using var path = CreatePointerPath(cursorKind);
            _shape = UIPointerShape.Create(path);
            _style = UIPointerStyle.Create(_shape, UIAxis.Neither);
        }

        public override UIPointerStyle? GetStyleForRegion(UIPointerInteraction interaction, UIPointerRegion region)
            => _style;

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                _style.Dispose();
                _shape.Dispose();
            }

            base.Dispose(disposing);
        }

        private static UIBezierPath CreatePointerPath(PointerCursorKind cursorKind)
        {
            ReadOnlySpan<CGPoint> horizontalArrow =
            [
                new(-11, 0), new(-6, -5), new(-6, -2), new(6, -2), new(6, -5),
                new(11, 0), new(6, 5), new(6, 2), new(-6, 2), new(-6, 5)
            ];
            ReadOnlySpan<CGPoint> pointer =
            [
                new(-7, -11), new(-3, 10), new(0, 4), new(4, 9), new(7, 7), new(2, 1), new(8, 0)
            ];
            ReadOnlySpan<CGPoint> move =
            [
                new(0, -11), new(-4, -6), new(-2, -6), new(-2, -2), new(-6, -2), new(-6, -4),
                new(-11, 0), new(-6, 4), new(-6, 2), new(-2, 2), new(-2, 6), new(-4, 6),
                new(0, 11), new(4, 6), new(2, 6), new(2, 2), new(6, 2), new(6, 4),
                new(11, 0), new(6, -4), new(6, -2), new(2, -2), new(2, -6), new(4, -6)
            ];
            var points = cursorKind switch
            {
                PointerCursorKind.Hand => pointer,
                PointerCursorKind.Move => move,
                _ => horizontalArrow
            };
            var angle = cursorKind switch
            {
                PointerCursorKind.HorizontalResize => 0d,
                PointerCursorKind.VerticalResize => Math.PI / 2d,
                PointerCursorKind.NorthwestSoutheastResize => Math.PI / 4d,
                PointerCursorKind.NortheastSouthwestResize => -Math.PI / 4d,
                _ => 0d
            };
            var sin = Math.Sin(angle);
            var cos = Math.Cos(angle);
            var path = new UIBezierPath();
            for (var i = 0; i < points.Length; i++)
            {
                var point = points[i];
                var rotated = new CGPoint(
                    (point.X * cos) - (point.Y * sin),
                    (point.X * sin) + (point.Y * cos));
                if (i == 0) path.MoveTo(rotated);
                else path.AddLineTo(rotated);
            }

            path.ClosePath();
            return path;
        }
    }
}
#endif
