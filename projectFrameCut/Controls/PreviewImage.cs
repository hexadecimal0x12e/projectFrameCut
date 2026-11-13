namespace projectFrameCut.Controls;

public sealed class PreviewImage : Image
{
    private PreviewLoadDiagnostics? _timing;
#if WINDOWS
    private Microsoft.UI.Xaml.Controls.Image? _nativeImage;
#else
    private bool _wasLoading;
#endif

    protected override void OnPropertyChanging(string? propertyName = null)
    {
        if (propertyName == nameof(Source))
        {
            _timing = PreviewLoadDiagnostics.Begin(this, _timing);
#if !WINDOWS
            _wasLoading = false;
#endif
        }
        base.OnPropertyChanging(propertyName);
    }

    protected override void OnPropertyChanged(string? propertyName = null)
    {
        if (propertyName == nameof(Source))
        {
            if (Source is null) _timing?.Finish("cleared");
            else _timing?.Stage($"source assigned; content={System.Runtime.CompilerServices.RuntimeHelpers.GetHashCode(Source)}");
        }
        else if (propertyName == nameof(IsLoading))
        {
            if (IsLoading)
            {
#if !WINDOWS
                _wasLoading = true;
#endif
                _timing?.Stage("image loading");
            }
#if WINDOWS
            else CheckNativeImage();
#else
            else if (_wasLoading) _timing?.ContentReady("image loaded");
#endif
        }
        base.OnPropertyChanged(propertyName);
    }

    protected override void OnHandlerChanged()
    {
#if WINDOWS
        if (_nativeImage is not null)
        {
            _nativeImage.ImageOpened -= OnImageOpened;
            _nativeImage.ImageFailed -= OnImageFailed;
        }
        _nativeImage = Handler?.PlatformView as Microsoft.UI.Xaml.Controls.Image;
        if (_nativeImage is not null)
        {
            _nativeImage.ImageOpened += OnImageOpened;
            _nativeImage.ImageFailed += OnImageFailed;
        }
#endif
        base.OnHandlerChanged();
        if (Handler is null) _timing?.Finish("handler detached");
        else _timing?.Stage("handler connected");
#if WINDOWS
        CheckNativeImage();
#endif
    }

#if WINDOWS
    private void CheckNativeImage()
    {
        if (!IsLoading && Source is not null && _nativeImage?.Source is Microsoft.UI.Xaml.Media.Imaging.BitmapSource { PixelWidth: > 0, PixelHeight: > 0 })
            _timing?.ContentReady("native image decoded");
    }

    private void OnImageOpened(object sender, Microsoft.UI.Xaml.RoutedEventArgs e)
        => _timing?.ContentReady("native image opened");

    private void OnImageFailed(object sender, Microsoft.UI.Xaml.ExceptionRoutedEventArgs e)
        => _timing?.Finish($"native image failed: {e.ErrorMessage}");
#endif
}
