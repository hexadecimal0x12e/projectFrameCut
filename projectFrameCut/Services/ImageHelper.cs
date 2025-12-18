using System;
using System.Collections.Generic;
using System.Text;

#if WINDOWS
using Microsoft.UI.Xaml.Media.Imaging;

#endif

namespace projectFrameCut.DraftStuff
{
    public static class ImageHelper
    {
        private static readonly System.Runtime.CompilerServices.ConditionalWeakTable<Image, System.Threading.SemaphoreSlim> _loadingLocks = new();

        public static async Task ForceLoadPNGToAImage(this Image source, string path)
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

        public static ImageSource LoadFromAsset(string assetName)
        {
#if WINDOWS
            return ImageSource.FromFile(Path.Combine(AppContext.BaseDirectory, assetName + ".scale-100.png"));
#endif
            return ImageSource.FromFile(assetName);
        }
    }
}
