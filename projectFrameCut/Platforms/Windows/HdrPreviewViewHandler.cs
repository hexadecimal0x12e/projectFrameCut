using Microsoft.Graphics.Canvas;
using Microsoft.Graphics.Canvas.UI.Composition;
using Microsoft.Graphics.Canvas.UI.Xaml;
using Microsoft.Maui.Handlers;
using Microsoft.UI.Composition;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Hosting;
using Microsoft.UI.Xaml.Media.Imaging;
using projectFrameCut.Controls;
using projectFrameCut.LivePreview;
using System.Numerics;
using System.Runtime.InteropServices.WindowsRuntime;
using CanvasDirectXPixelFormat = Windows.Graphics.DirectX.DirectXPixelFormat;
using CompositionDirectXAlphaMode = Microsoft.Graphics.DirectX.DirectXAlphaMode;
using CompositionDirectXPixelFormat = Microsoft.Graphics.DirectX.DirectXPixelFormat;
using HorizontalAlignment = Microsoft.UI.Xaml.HorizontalAlignment;
using PlatformGrid = Microsoft.UI.Xaml.Controls.Grid;
using VerticalAlignment = Microsoft.UI.Xaml.VerticalAlignment;
using Visibility = Microsoft.UI.Xaml.Visibility;

namespace projectFrameCut.Platforms.Windows;

public sealed class HdrPreviewViewHandler : ViewHandler<HdrPreviewView, PlatformGrid>
{
    // WinUI 3 SwapChainPanel surfaces are always composed as opaque, but they are also the
    // path that presents FP16 scRGB as Advanced Color. Keep opaque frames on the swap chain
    // and use a CompositionDrawingSurface only when per-pixel transparency is actually needed.
    public static readonly IPropertyMapper<HdrPreviewView, HdrPreviewViewHandler> Mapper =
        new PropertyMapper<HdrPreviewView, HdrPreviewViewHandler>(ViewMapper)
        {
            [nameof(HdrPreviewView.Frame)] = static (handler, view) => handler.UpdateFrame(view.Frame),
        };

    private readonly Microsoft.UI.Xaml.Controls.Image _fallback = new() { Stretch = Microsoft.UI.Xaml.Media.Stretch.Uniform };
    private readonly CanvasSwapChainPanel _panel = new() { Visibility = Visibility.Collapsed };
    private readonly TextBlock _error = new()
    {
        Visibility = Visibility.Collapsed,
        TextWrapping = TextWrapping.Wrap,
        HorizontalAlignment = HorizontalAlignment.Center,
        VerticalAlignment = VerticalAlignment.Center,
        Margin = new Microsoft.UI.Xaml.Thickness(12),
    };
    private CanvasDevice? _device;
    private CanvasSwapChain? _swapChain;
    private CompositionGraphicsDevice? _graphicsDevice;
    private CompositionDrawingSurface? _surface;
    private CompositionSurfaceBrush? _surfaceBrush;
    private SpriteVisual? _surfaceVisual;
    private int _updateVersion;
    private int _frameWidth;
    private int _frameHeight;
    private int _swapChainWidth;
    private int _swapChainHeight;

    public HdrPreviewViewHandler() : base(Mapper)
    {
    }

    protected override PlatformGrid CreatePlatformView()
    {
        var root = new PlatformGrid();
        root.Children.Add(_fallback);
        root.Children.Add(_panel);
        root.Children.Add(_error);
        return root;
    }

    protected override void ConnectHandler(PlatformGrid platformView)
    {
        base.ConnectHandler(platformView);
        _panel.SizeChanged += OnPanelSizeChanged;
        _panel.CompositionScaleChanged += OnCompositionScaleChanged;
        UpdateFrame(VirtualView.Frame);
    }

    protected override void DisconnectHandler(PlatformGrid platformView)
    {
        Interlocked.Increment(ref _updateVersion);
        _panel.SizeChanged -= OnPanelSizeChanged;
        _panel.CompositionScaleChanged -= OnCompositionScaleChanged;
        DisposeSwapChain();
        ElementCompositionPreview.SetElementChildVisual(platformView, null);
        DisposeCompositionResources();
        _device = null;
        base.DisconnectHandler(platformView);
    }

    private void UpdateFrame(PreviewFrameSource? frame)
    {
        var version = Interlocked.Increment(ref _updateVersion);
        _ = PresentAsync(frame, version);
    }

    private async Task PresentAsync(PreviewFrameSource? frame, int version)
    {
        if (frame is null)
        {
            ShowError(null);
            _panel.Visibility = Visibility.Collapsed;
            SetSurfaceVisibility(false);
            _fallback.Visibility = Visibility.Collapsed;
            return;
        }

        if (!string.IsNullOrWhiteSpace(frame.FallbackImagePath))
            await LoadFallbackAsync(frame.FallbackImagePath, version);

        if (version != Volatile.Read(ref _updateVersion)) return;
        if (string.IsNullOrWhiteSpace(frame.ScRgbPath))
        {
            _panel.Visibility = Visibility.Collapsed;
            SetSurfaceVisibility(false);
            if (frame.RequireSwapChain)
                ShowError("FP16 scRGB preview data is unavailable.");
            return;
        }

        try
        {
            var expectedStride = checked(frame.Width * 8);
            if (frame.Width <= 0 || frame.Height <= 0 || frame.Stride != expectedStride)
                throw new InvalidDataException($"Invalid FP16 preview layout: {frame.Width}x{frame.Height}, stride {frame.Stride}.");

            var bytes = await File.ReadAllBytesAsync(frame.ScRgbPath);
            if (version != Volatile.Read(ref _updateVersion)) return;
            if (bytes.Length != checked(frame.Stride * frame.Height))
                throw new InvalidDataException($"Invalid FP16 preview payload length {bytes.Length}.");

            _device ??= CanvasDevice.GetSharedDevice();
            if (!_device.IsPixelFormatSupported(CanvasDirectXPixelFormat.R16G16B16A16Float))
                throw new NotSupportedException("The active Direct3D device does not support R16G16B16A16_FLOAT.");

            if (HasTransparentPixels(bytes))
                PresentTransparentFrame(bytes, frame.Width, frame.Height);
            else
                PresentOpaqueHdrFrame(bytes, frame.Width, frame.Height);

            if (version != Volatile.Read(ref _updateVersion)) return;
            ShowError(null);
            _fallback.Visibility = Visibility.Collapsed;
        }
        catch (Exception ex)
        {
            if (version != Volatile.Read(ref _updateVersion)) return;
            Log(ex, "Show HDR Preview content", this);
            DisposeSwapChain();
            ElementCompositionPreview.SetElementChildVisual(PlatformView, null);
            DisposeCompositionResources();
            _device = null;
            SetSurfaceVisibility(false);
            if (frame.RequireSwapChain)
            {
                _fallback.Visibility = Visibility.Collapsed;
                ShowError($"HDR preview failed: {ex.Message}");
            }
            else
            {
                ShowError(null);
                _fallback.Visibility = string.IsNullOrWhiteSpace(frame.FallbackImagePath)
                    ? Visibility.Collapsed
                    : Visibility.Visible;
            }
        }
    }

    private async Task LoadFallbackAsync(string path, int version)
    {
        try
        {
            using var file = File.Open(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            using var stream = file.AsRandomAccessStream();
            var bitmap = new BitmapImage();
            await bitmap.SetSourceAsync(stream);
            if (version != Volatile.Read(ref _updateVersion)) return;
            _fallback.Source = bitmap;
            _fallback.Visibility = Visibility.Visible;
        }
        catch
        {
            if (version == Volatile.Read(ref _updateVersion))
                _fallback.Visibility = Visibility.Collapsed;
        }
    }

    private void ShowError(string? message)
    {
        _error.Text = message ?? string.Empty;
        _error.Visibility = string.IsNullOrWhiteSpace(message) ? Visibility.Collapsed : Visibility.Visible;
    }

    private static bool HasTransparentPixels(byte[] bytes)
    {
        // The backend writes one little-endian Half per RGBA channel. Half(1) is 0x3c00.
        // The payload length was validated above, so every alpha sample has two bytes.
        for (var alphaOffset = 6; alphaOffset < bytes.Length; alphaOffset += 8)
        {
            if (bytes[alphaOffset] != 0x00 || bytes[alphaOffset + 1] != 0x3c)
                return true;
        }

        return false;
    }

    private void PresentOpaqueHdrFrame(byte[] bytes, int width, int height)
    {
        SetSurfaceVisibility(false);

        if (_swapChain is null || _swapChainWidth != width || _swapChainHeight != height)
        {
            DisposeSwapChain();
            _swapChain = new CanvasSwapChain(
                _device!,
                width,
                height,
                96f,
                CanvasDirectXPixelFormat.R16G16B16A16Float,
                2,
                CanvasAlphaMode.Premultiplied);
            _panel.SwapChain = _swapChain;
            _swapChainWidth = width;
            _swapChainHeight = height;
            UpdateSwapChainTransform();
        }

        using var bitmap = CanvasBitmap.CreateFromBytes(
            _device!,
            bytes,
            width,
            height,
            CanvasDirectXPixelFormat.R16G16B16A16Float,
            96f,
            CanvasAlphaMode.Premultiplied);
        using (var drawing = _swapChain!.CreateDrawingSession(Microsoft.UI.Colors.Transparent))
        {
            drawing.DrawImage(bitmap);
        }
        _swapChain!.Present(0);
        _panel.Visibility = Visibility.Visible;
    }

    private void PresentTransparentFrame(byte[] bytes, int width, int height)
    {
        _panel.Visibility = Visibility.Collapsed;
        EnsureDrawingSurface(width, height);

        using var bitmap = CanvasBitmap.CreateFromBytes(
            _device!,
            bytes,
            width,
            height,
            CanvasDirectXPixelFormat.R16G16B16A16Float,
            96f,
            CanvasAlphaMode.Premultiplied);
        using (var drawing = CanvasComposition.CreateDrawingSession(_surface!))
        {
            drawing.Clear(Microsoft.UI.Colors.Transparent);
            drawing.DrawImage(bitmap);
        }
        SetSurfaceVisibility(true);
    }

    private void OnPanelSizeChanged(object sender, Microsoft.UI.Xaml.SizeChangedEventArgs e)
        => UpdateSwapChainTransform();

    private void OnCompositionScaleChanged(SwapChainPanel sender, object args)
        => UpdateSwapChainTransform();

    private void UpdateSwapChainTransform()
    {
        if (_swapChain is null || _swapChainWidth <= 0 || _swapChainHeight <= 0 || _panel.ActualWidth <= 0 || _panel.ActualHeight <= 0)
            return;

        var scale = Math.Min(_panel.ActualWidth / _swapChainWidth, _panel.ActualHeight / _swapChainHeight);
        var offsetX = (_panel.ActualWidth - _swapChainWidth * scale) * 0.5;
        var offsetY = (_panel.ActualHeight - _swapChainHeight * scale) * 0.5;
        _swapChain.TransformMatrix = new Matrix3x2((float)scale, 0f, 0f, (float)scale, (float)offsetX, (float)offsetY);
    }

    private void DisposeSwapChain()
    {
        _panel.SwapChain = null;
        _swapChain?.Dispose();
        _swapChain = null;
        _swapChainWidth = 0;
        _swapChainHeight = 0;
        _panel.Visibility = Visibility.Collapsed;
    }

    private void InitializeCompositionSurface(PlatformGrid platformView)
    {
        var compositor = ElementCompositionPreview.GetElementVisual(platformView).Compositor;
        _device ??= CanvasDevice.GetSharedDevice();
        _graphicsDevice = CanvasComposition.CreateCompositionGraphicsDevice(compositor, _device);
        _surfaceBrush = compositor.CreateSurfaceBrush();
        _surfaceBrush.Stretch = CompositionStretch.Uniform;
        _surfaceVisual = compositor.CreateSpriteVisual();
        _surfaceVisual.RelativeSizeAdjustment = Vector2.One;
        _surfaceVisual.Brush = _surfaceBrush;
        _surfaceVisual.Opacity = 0f;
        ElementCompositionPreview.SetElementChildVisual(platformView, _surfaceVisual);
    }

    private void EnsureDrawingSurface(int width, int height)
    {
        if (_graphicsDevice is null || _surfaceBrush is null || _surfaceVisual is null)
            InitializeCompositionSurface(PlatformView);

        if (_surface is not null && _frameWidth == width && _frameHeight == height)
            return;

        _surfaceBrush!.Surface = null;
        _surface?.Dispose();
        _surface = _graphicsDevice!.CreateDrawingSurface(
            new global::Windows.Foundation.Size(width, height),
            CompositionDirectXPixelFormat.R16G16B16A16Float,
            CompositionDirectXAlphaMode.Premultiplied);
        _surfaceBrush.Surface = _surface;
        _frameWidth = width;
        _frameHeight = height;
    }

    private void SetSurfaceVisibility(bool visible)
    {
        if (_surfaceVisual is not null)
            _surfaceVisual.Opacity = visible ? 1f : 0f;
    }

    private void DisposeCompositionResources()
    {
        if (_surfaceBrush is not null)
            _surfaceBrush.Surface = null;
        _surface?.Dispose();
        _surface = null;
        _frameWidth = 0;
        _frameHeight = 0;

        _graphicsDevice?.Dispose();
        _graphicsDevice = null;

        _surfaceVisual?.Dispose();
        _surfaceVisual = null;
        _surfaceBrush?.Dispose();
        _surfaceBrush = null;
    }
}
