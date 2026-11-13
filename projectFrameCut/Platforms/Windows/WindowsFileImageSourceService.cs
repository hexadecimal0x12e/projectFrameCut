using NativeImageSource = Microsoft.UI.Xaml.Media.ImageSource;

namespace projectFrameCut.Platforms.Windows;

public sealed class WindowsFileImageSourceService : FileImageSourceService
{
    private readonly StreamImageSourceService _streamService = new();

    public override async Task<IImageSourceServiceResult<NativeImageSource>?> GetImageSourceAsync(
        IImageSource imageSource, float scale = 1, CancellationToken cancellationToken = default)
    {
        var file = (IFileImageSource)imageSource;
        if (file.IsEmpty || !Path.IsPathRooted(file.File))
            return await base.GetImageSourceAsync(imageSource, scale, cancellationToken);

        string path = file.File;
        try
        {
            // StorageFile can pump COM messages and reenter WinUI during layout.
            return await _streamService.GetImageSourceAsync(
                Microsoft.Maui.Controls.ImageSource.FromStream(() => File.OpenRead(path)),
                scale, cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            Log(ex, $"load local image '{path}'", this);
            throw;
        }
    }
}
