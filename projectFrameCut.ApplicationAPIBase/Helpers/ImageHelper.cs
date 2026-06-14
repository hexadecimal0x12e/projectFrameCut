using System;
using System.Collections.Generic;
using System.Text;
using projectFrameCut.Shared;
using IPicture = projectFrameCut.Drawing.Base.IPicture;
using projectFrameCut.Drawing.Base;



#if WINDOWS
using Microsoft.UI.Xaml.Media.Imaging;

#endif

namespace projectFrameCut.ApplicationAPIBase.Helpers
{
    public static class ImageHelper
    {
        private static readonly System.Runtime.CompilerServices.ConditionalWeakTable<Microsoft.Maui.Controls.Image, System.Threading.SemaphoreSlim> _loadingLocks = new();

        /// <summary>
        /// Trying to load PNG image to a Microsoft.Maui.Controls.Image control.
        /// </summary>
        public static async Task ForceLoadPNGToAImage(this Microsoft.Maui.Controls.Image source, string path)
        {
            var exists = System.IO.File.Exists(path);
            if (!exists)
            {
                throw new FileNotFoundException("Source image not exist.", path);
            }
            var fileUri = new Uri("file:///" + path.Replace('\\', '/'));
            LogDiagnostic("fileUri = " + fileUri);
            var locker = _loadingLocks.GetValue(source, k => new System.Threading.SemaphoreSlim(1, 1));
            await locker.WaitAsync();
            try
            {
#if WINDOWS
                try
                {
                    await MainThread.InvokeOnMainThreadAsync(async () =>
                    {
                        try
                        {
                            LogDiagnostic("PreviewOverlayImage Width=" + source.Width + " Height=" + source.Height);
                            LogDiagnostic("PreviewOverlayImage Measure=" + source.Measure(10000, 10000));
                            var handler = source.Handler;
                            if (handler == null)
                            {
                                LogDiagnostic("PreviewOverlayImage.Handler is null");
                            }

                            var native = handler?.PlatformView as Microsoft.UI.Xaml.Controls.Image;
                            if (native == null)
                            {
                                LogDiagnostic("PlatformView is null or not a WinUI Image. Use ImageSource.FromStream.");
                                source.Source = ImageSource.FromStream(() => System.IO.File.OpenRead(path));
                                return;
                            }

                            var bmp = new BitmapImage();
                            try
                            {
                                bmp.UriSource = fileUri;
                                native.Source = bmp;
                                LogDiagnostic("Successfully to use Uri to load.");
                            }
                            catch (Exception exUri)
                            {
                                LogDiagnostic("Failed to use UriSource: " + exUri);
                                using (var fs2 = System.IO.File.OpenRead(path))
                                {
                                    var randomAccess = fs2.AsRandomAccessStream();
                                    await bmp.SetSourceAsync(randomAccess);
                                    native.Source = bmp;
                                    LogDiagnostic("Successfully to use SetSourceAsync(stream) ");
                                }
                            }

                            native.InvalidateMeasure();
                            native.UpdateLayout();
                            LogDiagnostic("Successfully to update layout.");
                        }
                        catch (Exception exNative)
                        {
                            Log(exNative, $"load image to {source.Id}");
                        }
                        finally
                        {
                            LogDiagnostic("AFTER load PreviewOverlayImage Width=" + source.Width + " Height=" + source.Height);
                            LogDiagnostic("AFTER load PreviewOverlayImage Measure=" + source.Measure(10000, 10000));
                        }
                    });

                }
                catch (Exception ex)
                {
                    Log(ex, $"load image to {source.Id}");
                }
#else
                source.Source = ImageSource.FromStream(() => System.IO.File.OpenRead(path));
                return;
#endif
            }
            finally
            {
                locker.Release();
            }
        }

        /// <summary>
        /// Trying to load a image from program asset. Don't use this in your plugin, it's a internal method.
        /// </summary>
        /// <param name="assetName"></param>
        /// <returns></returns>
        public static ImageSource LoadFromAsset(string assetName)
        {
#if WINDOWS
            int[] zooms = [800, 400, 200, 125, 125, 100];
            foreach (var zoom in zooms)
            {
                var path = Path.Combine(AppContext.BaseDirectory, assetName + $".scale-{zoom}.png");
                if (System.IO.File.Exists(path))
                {
                    return ImageSource.FromFile(path);
                }
            }
            return ImageSource.FromFile(Path.Combine(AppContext.BaseDirectory, assetName + ".scale-100.png"));
#else
            return ImageSource.FromFile(assetName);
#endif
        }

        /// <summary>
        /// Convert a <see cref="IPicture"/> to a <see cref="ImageSource"/>.
        /// </summary>
        /// <param name="picture"></param>
        /// <param name="cache"></param>
        /// <returns></returns>
        public static ImageSource ToImageSource(this IPicture picture)
        {
            if (picture == null) return null;
            var ms = new MemoryStream();
            picture.ToBitPerPixel(8).SaveToPng(ms);
            var bytes = ms.ToArray();
            return ImageSource.FromStream(() => new MemoryStream(bytes));

        }

    }

    public class IPictureImageSource : ImageSource, IStreamImageSource
    {
        public required IPicture Source;

        public Task<Stream> GetStreamAsync(CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (Source.Disposed)
                throw new ObjectDisposedException(nameof(IPictureImageSource));

            using var ms = new MemoryStream();
            Source.ToBitPerPixel(8).SaveToPng(ms);
            ms.Position = 0;
            return Task.FromResult<Stream>(ms);
        }
    }
}
