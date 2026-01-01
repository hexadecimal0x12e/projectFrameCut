using System;
using System.Collections.Generic;
using System.Text;
using projectFrameCut.Shared;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;
using IPicture = projectFrameCut.Shared.IPicture;


#if WINDOWS
using Microsoft.UI.Xaml.Media.Imaging;

#endif

namespace projectFrameCut.DraftStuff
{
    public static class ImageHelper
    {
        private static readonly System.Runtime.CompilerServices.ConditionalWeakTable<Microsoft.Maui.Controls.Image, System.Threading.SemaphoreSlim> _loadingLocks = new();

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
#endif
            return ImageSource.FromFile(assetName);
        }

        public static ImageSource ToImageSource(this IPicture picture)
        {
            if (picture == null) return null;

            IPicture<ushort>? p16 = null;
            bool disposeP16 = false;

            if (picture is IPicture<ushort> casted)
            {
                p16 = casted;
            }
            else
            {
                var converted = picture.ToBitPerPixel(IPicture.PicturePixelMode.UShortPicture);
                if (converted is IPicture<ushort> c)
                {
                    p16 = c;
                    disposeP16 = true;
                }
            }

            if (p16 == null) return null;

            try
            {
                int width = p16.Width;
                int height = p16.Height;
                using var img = new SixLabors.ImageSharp.Image<SixLabors.ImageSharp.PixelFormats.Rgba32>(width, height);

                img.ProcessPixelRows(accessor =>
                {
                    var r = p16.r;
                    var g = p16.g;
                    var b = p16.b;
                    var a = p16.a;
                    bool hasAlpha = p16.hasAlphaChannel && a != null;

                    for (int y = 0; y < height; y++)
                    {
                        var row = accessor.GetRowSpan(y);
                        int offset = y * width;
                        for (int x = 0; x < width; x++)
                        {
                            int i = offset + x;
                            byte R = (byte)(r[i] >> 8);
                            byte G = (byte)(g[i] >> 8);
                            byte B = (byte)(b[i] >> 8);
                            byte A = hasAlpha ? (byte)(a[i] * 255f) : (byte)255;
                            row[x] = new SixLabors.ImageSharp.PixelFormats.Rgba32(R, G, B, A);
                        }
                    }
                });

                var ms = new MemoryStream();
                img.SaveAsPng(ms);
                var bytes = ms.ToArray();
                return ImageSource.FromStream(() => new MemoryStream(bytes));
            }
            finally
            {
                if (disposeP16) p16.Dispose();
            }
        }

    }
}
