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
using projectFrameCut.Render.Contracts;
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
    private CanvasBitmap? _opaqueBitmap;
    private ScRgbFrame? _opaqueFrame;
    private CanvasBitmap? _transparentBitmap;
    private ScRgbFrame? _transparentFrame;
    private CompositionGraphicsDevice? _graphicsDevice;
    private CompositionDrawingSurface? _surface;
    private CompositionSurfaceBrush? _surfaceBrush;
    private SpriteVisual? _surfaceVisual;
    private int _updateVersion;
    private bool _connected;
    private PreviewFrameSource? _requestedFrame;
    private bool _hasFrameRequest;
    private int _frameWidth;
    private int _frameHeight;
    private int _swapChainWidth;
    private int _swapChainHeight;
    private bool _swapChainContentDirty;
    private Matrix3x2? _swapChainTransform;
    private Vector2 _compositionScale;
    private PreviewLoadDiagnostics? _presentTiming;
    private bool _frameSubmitted;
    private bool _presentationConfigured;

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
        _connected = true;
        VirtualView.Timing?.Stage("native handler connected");
        _panel.Loaded += OnPanelLoaded;
        _panel.SizeChanged += OnPanelSizeChanged;
        _panel.CompositionScaleChanged += OnCompositionScaleChanged;
        UpdateFrame(VirtualView.Frame);
    }

    protected override void DisconnectHandler(PlatformGrid platformView)
    {
        _connected = false;
        _requestedFrame = null;
        _hasFrameRequest = false;
        VirtualView.Timing?.Finish("handler detached");
        Interlocked.Increment(ref _updateVersion);
        _panel.Loaded -= OnPanelLoaded;
        _panel.SizeChanged -= OnPanelSizeChanged;
        _panel.CompositionScaleChanged -= OnCompositionScaleChanged;
        DisposeSwapChain();
        ElementCompositionPreview.SetElementChildVisual(platformView, null);
        DisposeCompositionResources();
        _device = null;
        base.DisconnectHandler(platformView);
    }

    private bool IsCurrent(int version) => _connected && version == Volatile.Read(ref _updateVersion);

    private async void UpdateFrame(PreviewFrameSource? frame)
    {
        if (!_connected || (_hasFrameRequest && ReferenceEquals(_requestedFrame, frame))) return;
        _requestedFrame = frame;
        _hasFrameRequest = true;
        var version = Interlocked.Increment(ref _updateVersion);
        if (!IsCurrent(version)) return;
        var timing = VirtualView.Timing;
        timing?.Stage("native frame mapped");
        try
        {
            if (frame is not null && frame.ScRgb is not { IsCurrent: true } && frame.Png is null)
            {
                timing?.Stage("materialization queued");
                frame = await Task.Run(() =>
                {
                    timing?.Stage("materialization started");
                    var prepared = HdrPreviewView.PrepareFrame(frame);
                    timing?.Stage("materialization finished");
                    return prepared;
                });
            }
            if (!IsCurrent(version)) return;
            timing?.Stage("frame materialized; UI resumed");
            _presentTiming = timing;
            Present(frame, version);
        }
        catch (Exception ex)
        {
            if (IsCurrent(version)) _hasFrameRequest = false;
            timing?.Finish("materialization failed");
            Log(ex, $"Present preview frame '{frame?.VfdPath}'", this);
        }
    }

    private void Present(PreviewFrameSource? frame, int version)
    {
        _frameSubmitted = false;
        _presentationConfigured = false;
        if (frame is null)
        {
            _presentTiming?.Finish("cleared");
            ShowError(null);
            _panel.Visibility = Visibility.Collapsed;
            SetSurfaceVisibility(false);
            _fallback.Visibility = Visibility.Collapsed;
            return;
        }

        if (!IsCurrent(version)) return;
        if (frame.TargetPixelFormat != PreviewPixelFormat.Rgba16FloatScRgb)
        {
            _panel.Visibility = Visibility.Collapsed;
            SetSurfaceVisibility(false);
            LoadFallback(frame, version);
            return;
        }

        try
        {
            var materialized = frame.ScRgb ?? throw new InvalidOperationException("Preview scRGB data was not prepared.");
            lock (materialized.PixelsGate)
            {
                if (!IsCurrent(version)) return;
                if (!materialized.IsCurrent)
                {
                    _hasFrameRequest = false;
                    UpdateFrame(frame with { ScRgb = null, Png = null });
                    return;
                }

                _device ??= CanvasDevice.GetSharedDevice();
                if (!_device.IsPixelFormatSupported(CanvasDirectXPixelFormat.R16G16B16A16Float))
                    throw new NotSupportedException("The active Direct3D device does not support R16G16B16A16_FLOAT.");
                _presentTiming?.Stage("graphics device ready");

                if (materialized.HasTransparency)
                    PresentTransparentFrame(materialized);
                else
                    PresentOpaqueHdrFrame(materialized);

                if (!IsCurrent(version)) return;
                ShowError(null);
                _fallback.Visibility = Visibility.Collapsed;
                _presentationConfigured = true;
                _presentTiming?.Stage("presentation configured");
                if (_frameSubmitted) _presentTiming?.ContentReady("HDR content ready");
            }
        }
        catch (Exception ex)
        {
            if (!IsCurrent(version)) return;
            _presentTiming?.Stage("HDR presentation failed");
            Log(ex, $"Show HDR preview content '{frame.VfdPath}'", this);
            DisposeSwapChain();
            ElementCompositionPreview.SetElementChildVisual(PlatformView, null);
            DisposeCompositionResources();
            _device = null;
            SetSurfaceVisibility(false);
            if (frame.RequireSwapChain)
            {
                _hasFrameRequest = false;
                _presentTiming?.Finish("failed");
                _fallback.Visibility = Visibility.Collapsed;
                ShowError($"HDR preview failed: {ex.Message}");
            }
            else
            {
                ShowError(null);
                LoadFallback(frame, version);
            }
        }
    }

    private async void LoadFallback(PreviewFrameSource frame, int version)
    {
        var timing = _presentTiming;
        try
        {
            timing?.Stage("PNG preparation started");
            var bytes = frame.Png ?? await Task.Run(() => PreviewFrameMaterializer.ToPngBytes(frame));
            if (!IsCurrent(version)) return;
            timing?.Stage("PNG bytes ready");
            using var memory = new MemoryStream(bytes, writable: false);
            using var stream = memory.AsRandomAccessStream();
            var bitmap = new BitmapImage();
            var assigned = false;
            if (timing is not null)
            {
                bitmap.ImageOpened += (_, _) =>
                {
                    if (!IsCurrent(version)) return;
                    timing.Stage("native fallback image opened");
                    if (assigned) timing.ContentReady("fallback content ready");
                };
                bitmap.ImageFailed += (_, e) =>
                {
                    if (IsCurrent(version)) timing.Finish($"fallback image failed: {e.ErrorMessage}");
                };
            }
            bitmap.SetSource(stream);
            timing?.Stage("PNG decoded");
            if (!IsCurrent(version)) return;
            _fallback.Source = bitmap;
            _fallback.Visibility = Visibility.Visible;
            timing?.Stage("fallback source assigned");
            assigned = true;
            if (bitmap.PixelWidth > 0 && bitmap.PixelHeight > 0)
                timing?.ContentReady("fallback content ready");
        }
        catch (Exception ex)
        {
            if (IsCurrent(version))
            {
                _hasFrameRequest = false;
                timing?.Finish("fallback failed");
                Log(ex, $"Load preview fallback '{frame.VfdPath}'", this);
                _fallback.Visibility = Visibility.Collapsed;
                ShowError($"Preview failed: {ex.Message}");
            }
        }
    }

    private void ShowError(string? message)
    {
        _error.Text = message ?? string.Empty;
        _error.Visibility = string.IsNullOrWhiteSpace(message) ? Visibility.Collapsed : Visibility.Visible;
    }

    private static bool HasSameContent(ScRgbFrame? previous, ScRgbFrame frame)
        => previous is not null && previous.Width == frame.Width && previous.Height == frame.Height
            && previous.Stride == frame.Stride && previous.ContentHash == frame.ContentHash;

    private void UploadBitmap(ref CanvasBitmap? bitmap, ScRgbFrame? previous, ScRgbFrame frame)
    {
        if (bitmap is not null && previous is not null && previous.Width == frame.Width && previous.Height == frame.Height)
        {
            bitmap.SetPixelBytes(frame.Bytes);
            return;
        }

        bitmap?.Dispose();
        bitmap = null;
        bitmap = CanvasBitmap.CreateFromBytes(
            _device!, frame.Bytes, frame.Width, frame.Height,
            CanvasDirectXPixelFormat.R16G16B16A16Float, 96f, CanvasAlphaMode.Premultiplied);
    }

    private void PresentOpaqueHdrFrame(ScRgbFrame frame)
    {
        SetSurfaceVisibility(false);

        if (_swapChain is null)
        {
            _swapChain = new CanvasSwapChain(
                _device!,
                frame.Width,
                frame.Height,
                96f,
                CanvasDirectXPixelFormat.R16G16B16A16Float,
                2,
                CanvasAlphaMode.Premultiplied);
            _panel.SwapChain = _swapChain;
            _swapChainWidth = frame.Width;
            _swapChainHeight = frame.Height;
            _swapChainContentDirty = true;
            _swapChainTransform = null;
        }
        else if (_swapChainWidth != frame.Width || _swapChainHeight != frame.Height)
        {
            _swapChain.ResizeBuffers(frame.Width, frame.Height);
            _swapChainWidth = frame.Width;
            _swapChainHeight = frame.Height;
            _swapChainContentDirty = true;
            _swapChainTransform = null;
        }
        _presentTiming?.Stage("swap chain ready");

        if (_opaqueBitmap is null || !HasSameContent(_opaqueFrame, frame))
        {
            _swapChainContentDirty = true;
            UploadBitmap(ref _opaqueBitmap, _opaqueFrame, frame);
            _presentTiming?.Stage("opaque bitmap uploaded");
        }
        else
        {
            _presentTiming?.Stage("swap chain content reused");
        }
        _opaqueFrame = frame;
        _panel.Visibility = Visibility.Visible;
        DrawOpaqueFrame();
    }

    private void PresentTransparentFrame(ScRgbFrame frame)
    {
        _panel.Visibility = Visibility.Collapsed;
        EnsureDrawingSurface(frame.Width, frame.Height);
        _presentTiming?.Stage("composition surface ready");

        if (HasSameContent(_transparentFrame, frame))
        {
            _transparentFrame = frame;
            SetSurfaceVisibility(true);
            ContentSubmitted("composition content reused");
            return;
        }

        UploadBitmap(ref _transparentBitmap, _transparentFrame, frame);
        _presentTiming?.Stage("transparent bitmap uploaded");
        using (var drawing = CanvasComposition.CreateDrawingSession(_surface!))
        {
            drawing.Clear(Microsoft.UI.Colors.Transparent);
            drawing.DrawImage(_transparentBitmap!);
        }
        _transparentFrame = frame;
        SetSurfaceVisibility(true);
        ContentSubmitted("composition drawing submitted");
    }

    private void OnPanelLoaded(object sender, RoutedEventArgs e)
        => RefreshOpaqueFrame();

    private void OnPanelSizeChanged(object sender, Microsoft.UI.Xaml.SizeChangedEventArgs e)
        => RefreshOpaqueFrame();

    private void OnCompositionScaleChanged(SwapChainPanel sender, object args)
        => RefreshOpaqueFrame();

    private void RefreshOpaqueFrame()
    {
        if (!_connected || _panel.Visibility != Visibility.Visible)
            return;

        try
        {
            DrawOpaqueFrame();
        }
        catch (Exception ex)
        {
            _swapChainContentDirty = true;
            _hasFrameRequest = false;
            Log(ex, "Resize HDR preview content", this);
        }
    }

    private void DrawOpaqueFrame()
    {
        if (_swapChain is null || _opaqueBitmap is null || _swapChainWidth <= 0 || _swapChainHeight <= 0 || _panel.ActualWidth <= 0 || _panel.ActualHeight <= 0)
        {
            _presentTiming?.Stage("waiting for swap chain layout");
            return;
        }

        var scale = Math.Min(_panel.ActualWidth / _swapChainWidth, _panel.ActualHeight / _swapChainHeight);
        var offsetX = (_panel.ActualWidth - _swapChainWidth * scale) * 0.5;
        var offsetY = (_panel.ActualHeight - _swapChainHeight * scale) * 0.5;
        var transform = new Matrix3x2((float)scale, 0f, 0f, (float)scale, (float)offsetX, (float)offsetY);
        var compositionScale = new Vector2(_panel.CompositionScaleX, _panel.CompositionScaleY);
        if (!_swapChainContentDirty && _swapChainTransform == transform && _compositionScale == compositionScale)
        {
            _frameSubmitted = true;
            return;
        }

        _swapChain.TransformMatrix = transform;
        // A real layout change needs Present; the next back buffer may still hold an older frame.
        using (var drawing = _swapChain.CreateDrawingSession(Microsoft.UI.Colors.Transparent))
        {
            drawing.DrawImage(_opaqueBitmap);
        }
        _presentTiming?.Stage("swap chain drawing submitted");
        _swapChain.Present(0);
        _swapChainContentDirty = false;
        _swapChainTransform = transform;
        _compositionScale = compositionScale;
        ContentSubmitted("swap chain presented");
    }

    private void ContentSubmitted(string stage)
    {
        _frameSubmitted = true;
        _presentTiming?.Stage(stage);
        if (_presentationConfigured) _presentTiming?.ContentReady("HDR content ready");
    }

    private void DisposeSwapChain()
    {
        _opaqueBitmap?.Dispose();
        _opaqueBitmap = null;
        _opaqueFrame = null;
        _panel.SwapChain = null;
        _swapChain?.Dispose();
        _swapChain = null;
        _swapChainWidth = 0;
        _swapChainHeight = 0;
        _swapChainContentDirty = false;
        _swapChainTransform = null;
        _compositionScale = default;
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
        _transparentBitmap?.Dispose();
        _transparentBitmap = null;
        _transparentFrame = null;
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
