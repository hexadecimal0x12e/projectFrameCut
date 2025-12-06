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
        public static async Task ForceLoadPNGToAImage(this Image source, string path)
        {
#if WINDOWS
            try
            {
                var exists = System.IO.File.Exists(path);
                LogDiagnostic("File.Exists: " + exists + " path=" + path);
                if (!exists)
                {
                    throw new FileNotFoundException("Source image not exist.", path);
                }

                var fi = new System.IO.FileInfo(path);
                LogDiagnostic("File length: " + fi.Length + " bytes");

                byte[] header = new byte[8];
                using (var fs = System.IO.File.OpenRead(path))
                {
                    var read = await fs.ReadAsync(header, 0, header.Length);
                    LogDiagnostic("Read header bytes: " + read);
                }
                LogDiagnostic("Header hex: " + BitConverter.ToString(header));
                bool looksLikePng = header.Length >= 4 && header[0] == 0x89 && header[1] == 0x50 && header[2] == 0x4E && header[3] == 0x47;
                LogDiagnostic("looksLikePng: " + looksLikePng);

                await MainThread.InvokeOnMainThreadAsync(() =>
                {
                    LogDiagnostic("PreviewOverlayImage Width=" + source.Width + " Height=" + source.Height);
                    LogDiagnostic("PreviewOverlayImage Measure=" + source.Measure(10000, 10000));
                });

                var fileUri = new Uri("file:///" + path.Replace('\\', '/'));
                LogDiagnostic("fileUri = " + fileUri);

                await MainThread.InvokeOnMainThreadAsync(async () =>
                {
                    try
                    {
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

                        LogDiagnostic("native image control obtained");

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
                });

                await MainThread.InvokeOnMainThreadAsync(() =>
                {
                    LogDiagnostic("AFTER load PreviewOverlayImage Width=" + source.Width + " Height=" + source.Height);
                    LogDiagnostic("AFTER load PreviewOverlayImage Measure=" + source.Measure(10000, 10000));
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
    }
}
