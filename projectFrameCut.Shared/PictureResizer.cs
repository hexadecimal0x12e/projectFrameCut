using System.Diagnostics;

namespace projectFrameCut.Shared
{
    public interface IPictureResizer
    {
        Picture16bpp Resize(Picture16bpp source, int targetWidth, int targetHeight, bool preserveAspect);
        Picture8bpp Resize(Picture8bpp source, int targetWidth, int targetHeight, bool preserveAspect);
        HDRPicture16bpp Resize(HDRPicture16bpp source, int targetWidth, int targetHeight, bool preserveAspect);
    }

    public class CPUBilinearPictureResizer : IPictureResizer
    {
        public Picture16bpp Resize(Picture16bpp source, int targetWidth, int targetHeight, bool preserveAspect)
        {
            var sw = Stopwatch.StartNew();
            if (targetWidth == source.Width && targetHeight == source.Height) return source;

            if (targetWidth <= 0 || targetHeight <= 0) throw new ArgumentException("targetWidth and targetHeight must be positive");
            if (source.Width <= 0 || source.Height <= 0) throw new InvalidOperationException("Source image has invalid dimensions");

            int destW = targetWidth;
            int destH = targetHeight;

            if (preserveAspect)
            {
                double sx = (double)targetWidth / source.Width;
                double sy = (double)targetHeight / source.Height;
                double s = Math.Min(sx, sy);
                destW = Math.Max(1, (int)(source.Width * s + 0.5));
                destH = Math.Max(1, (int)(source.Height * s + 0.5));
                if (destW == source.Width && destH == source.Height) return source;
            }

            var result = new Picture16bpp(destW, destH);
            int dstPixels = checked(destW * destH);
            result.r = new ushort[dstPixels];
            result.g = new ushort[dstPixels];
            result.b = new ushort[dstPixels];
            result.a = source.hasAlphaChannel ? new float[dstPixels] : null;
            result.hasAlphaChannel = source.hasAlphaChannel;

            double xRatio = (double)source.Width / destW;
            double yRatio = (double)source.Height / destH;

            for (int y = 0; y < destH; y++)
            {
                double srcY = (y + 0.5) * yRatio - 0.5;
                int y0 = (int)Math.Floor(srcY);
                int y1 = y0 + 1;
                double wy = srcY - y0;
                if (y0 < 0)
                {
                    y0 = 0; y1 = 0; wy = 0;
                }
                else if (y0 >= source.Height) { y0 = source.Height - 1; y1 = source.Height - 1; wy = 0; }
                if (y1 >= source.Height) { y1 = source.Height - 1; }

                for (int x = 0; x < destW; x++)
                {
                    double srcX = (x + 0.5) * xRatio - 0.5;
                    int x0 = (int)Math.Floor(srcX);
                    int x1 = x0 + 1;
                    double wx = srcX - x0;
                    if (x0 < 0)
                    {
                        x0 = 0; x1 = 0; wx = 0;
                    }
                    else if (x0 >= source.Width) { x0 = source.Width - 1; x1 = source.Width - 1; wx = 0; }
                    if (x1 >= source.Width) { x1 = source.Width - 1; }

                    int k00 = y0 * source.Width + x0;
                    int k10 = y0 * source.Width + x1;
                    int k01 = y1 * source.Width + x0;
                    int k11 = y1 * source.Width + x1;

                    double r00 = source.r[k00];
                    double r10 = source.r[k10];
                    double r01 = source.r[k01];
                    double r11 = source.r[k11];

                    double g00 = source.g[k00];
                    double g10 = source.g[k10];
                    double g01 = source.g[k01];
                    double g11 = source.g[k11];

                    double b00 = source.b[k00];
                    double b10 = source.b[k10];
                    double b01 = source.b[k01];
                    double b11 = source.b[k11];

                    double wxa = 1.0 - wx;
                    double wya = 1.0 - wy;

                    double rInterp = r00 * wxa * wya + r10 * wx * wya + r01 * wxa * wy + r11 * wx * wy;
                    double gInterp = g00 * wxa * wya + g10 * wx * wya + g01 * wxa * wy + g11 * wx * wy;
                    double bInterp = b00 * wxa * wya + b10 * wx * wya + b01 * wxa * wy + b11 * wx * wy;

                    int dstIdx = y * destW + x;
                    int rr = (int)(rInterp + 0.5);
                    int gg = (int)(gInterp + 0.5);
                    int bb = (int)(bInterp + 0.5);
                    if (rr < 0) rr = 0; if (rr > 65535) rr = 65535;
                    if (gg < 0) gg = 0; if (gg > 65535) gg = 65535;
                    if (bb < 0) bb = 0; if (bb > 65535) bb = 65535;
                    result.r[dstIdx] = (ushort)rr;
                    result.g[dstIdx] = (ushort)gg;
                    result.b[dstIdx] = (ushort)bb;

                    if (source.hasAlphaChannel && source.a != null)
                    {
                        double a00 = source.a[k00];
                        double a10 = source.a[k10];
                        double a01 = source.a[k01];
                        double a11 = source.a[k11];
                        double aInterp = a00 * wxa * wya + a10 * wx * wya + a01 * wxa * wy + a11 * wx * wy;
                        if (double.IsNaN(aInterp) || double.IsInfinity(aInterp)) aInterp = 1.0;
                        if (aInterp < 0) aInterp = 0; if (aInterp > 1) aInterp = 1;
                        result.a![dstIdx] = (float)aInterp;
                    }
                }
            }
            result.ProcessStack.Add(new PictureProcessStack
            {
                OperationDisplayName = "Resize (IPicture)",
                Operator = typeof(Picture16bpp),
                ProcessingFuncStackTrace = new(true),
                Properties = new Dictionary<string, object>
                {
                    { "SourceWidth", source.Width },
                    { "SourceHeight", source.Height },
                    { "TargetWidth", targetWidth },
                    { "TargetHeight", targetHeight },
                    { "PreserveAspect", preserveAspect },
                },
                Elapsed = sw.Elapsed
            });

            return result;
        }

        public Picture8bpp Resize(Picture8bpp source, int targetWidth, int targetHeight, bool preserveAspect)
        {
            var sw = Stopwatch.StartNew();
            if (targetWidth == source.Width && targetHeight == source.Height) return source;

            if (targetWidth <= 0 || targetHeight <= 0) throw new ArgumentException("targetWidth and targetHeight must be positive");
            if (source.Width <= 0 || source.Height <= 0) throw new InvalidOperationException("Source image has invalid dimensions");

            int destW = targetWidth;
            int destH = targetHeight;

            if (preserveAspect)
            {
                double sx = (double)targetWidth / source.Width;
                double sy = (double)targetHeight / source.Height;
                double s = Math.Min(sx, sy);
                destW = Math.Max(1, (int)(source.Width * s + 0.5));
                destH = Math.Max(1, (int)(source.Height * s + 0.5));
                if (destW == source.Width && destH == source.Height) return source;
            }

            var result = new Picture8bpp(destW, destH);
            int dstPixels = checked(destW * destH);
            result.r = new byte[dstPixels];
            result.g = new byte[dstPixels];
            result.b = new byte[dstPixels];
            result.a = source.hasAlphaChannel ? new float[dstPixels] : null;
            result.hasAlphaChannel = source.hasAlphaChannel;

            double xRatio = (double)source.Width / destW;
            double yRatio = (double)source.Height / destH;

            for (int y = 0; y < destH; y++)
            {
                double srcY = (y + 0.5) * yRatio - 0.5;
                int y0 = (int)Math.Floor(srcY);
                int y1 = y0 + 1;
                double wy = srcY - y0;
                if (y0 < 0)
                {
                    y0 = 0; y1 = 0; wy = 0;
                }
                else if (y0 >= source.Height) { y0 = source.Height - 1; y1 = source.Height - 1; wy = 0; }
                if (y1 >= source.Height) { y1 = source.Height - 1; }

                for (int x = 0; x < destW; x++)
                {
                    double srcX = (x + 0.5) * xRatio - 0.5;
                    int x0 = (int)Math.Floor(srcX);
                    int x1 = x0 + 1;
                    double wx = srcX - x0;
                    if (x0 < 0)
                    {
                        x0 = 0; x1 = 0; wx = 0;
                    }
                    else if (x0 >= source.Width) { x0 = source.Width - 1; x1 = source.Width - 1; wx = 0; }
                    if (x1 >= source.Width) { x1 = source.Width - 1; }

                    int k00 = y0 * source.Width + x0;
                    int k10 = y0 * source.Width + x1;
                    int k01 = y1 * source.Width + x0;
                    int k11 = y1 * source.Width + x1;

                    double r00 = source.r[k00];
                    double r10 = source.r[k10];
                    double r01 = source.r[k01];
                    double r11 = source.r[k11];

                    double g00 = source.g[k00];
                    double g10 = source.g[k10];
                    double g01 = source.g[k01];
                    double g11 = source.g[k11];

                    double b00 = source.b[k00];
                    double b10 = source.b[k10];
                    double b01 = source.b[k01];
                    double b11 = source.b[k11];

                    double wxa = 1.0 - wx;
                    double wya = 1.0 - wy;

                    double rInterp = r00 * wxa * wya + r10 * wx * wya + r01 * wxa * wy + r11 * wx * wy;
                    double gInterp = g00 * wxa * wya + g10 * wx * wya + g01 * wxa * wy + g11 * wx * wy;
                    double bInterp = b00 * wxa * wya + b10 * wx * wya + b01 * wxa * wy + b11 * wx * wy;

                    int dstIdx = y * destW + x;
                    int rr = (int)(rInterp + 0.5);
                    int gg = (int)(gInterp + 0.5);
                    int bb = (int)(bInterp + 0.5);
                    if (rr < 0) rr = 0; if (rr > 255) rr = 255;
                    if (gg < 0) gg = 0; if (gg > 255) gg = 255;
                    if (bb < 0) bb = 0; if (bb > 255) bb = 255;
                    result.r[dstIdx] = (byte)rr;
                    result.g[dstIdx] = (byte)gg;
                    result.b[dstIdx] = (byte)bb;

                    if (source.hasAlphaChannel && source.a != null)
                    {
                        double a00 = source.a[k00];
                        double a10 = source.a[k10];
                        double a01 = source.a[k01];
                        double a11 = source.a[k11];
                        double aInterp = a00 * wxa * wya + a10 * wx * wya + a01 * wxa * wy + a11 * wx * wy;
                        if (double.IsNaN(aInterp) || double.IsInfinity(aInterp)) aInterp = 1.0;
                        if (aInterp < 0) aInterp = 0; if (aInterp > 1) aInterp = 1;
                        result.a![dstIdx] = (float)aInterp;
                    }
                }
            }
            result.ProcessStack.Add(new PictureProcessStack
            {
                OperationDisplayName = "Resize (IPicture)",
                Operator = typeof(Picture8bpp),
                ProcessingFuncStackTrace = new(true),
                Properties = new Dictionary<string, object>
                {
                    { "SourceWidth", source.Width },
                    { "SourceHeight", source.Height },
                    { "TargetWidth", targetWidth },
                    { "TargetHeight", targetHeight },
                    { "PreserveAspect", preserveAspect },
                },
                Elapsed = sw.Elapsed
            });
            return result;
        }

        public HDRPicture16bpp Resize(HDRPicture16bpp source, int targetWidth, int targetHeight, bool preserveAspect)
        {
            var sw = Stopwatch.StartNew();
            if (targetWidth == source.Width && targetHeight == source.Height) return source;

            if (targetWidth <= 0 || targetHeight <= 0) throw new ArgumentException("targetWidth and targetHeight must be positive");
            if (source.Width <= 0 || source.Height <= 0) throw new InvalidOperationException("Source image has invalid dimensions");

            int destW = targetWidth;
            int destH = targetHeight;

            if (preserveAspect)
            {
                double sx = (double)targetWidth / source.Width;
                double sy = (double)targetHeight / source.Height;
                double s = Math.Min(sx, sy);
                destW = Math.Max(1, (int)(source.Width * s + 0.5));
                destH = Math.Max(1, (int)(source.Height * s + 0.5));
                if (destW == source.Width && destH == source.Height) return source;
            }

            int dstPixels = checked(destW * destH);
            var result = new HDRPicture16bpp(destW, destH, allocateArrays: false)
            {
                r = new ushort[dstPixels],
                g = new ushort[dstPixels],
                b = new ushort[dstPixels],
                a = source.hasAlphaChannel ? new float[dstPixels] : null,
                hasAlphaChannel = source.hasAlphaChannel,
                Brightness = new float[dstPixels],
                MaximumBrightness = (source.MaximumBrightness > 0f && float.IsFinite(source.MaximumBrightness))
                    ? source.MaximumBrightness
                    : 1000f,
            };

            float[]? sourceBrightness = (source.Brightness != null && source.Brightness.Length == source.Pixels) ? source.Brightness : null;
            bool hasBrightness = sourceBrightness != null;

            double xRatio = (double)source.Width / destW;
            double yRatio = (double)source.Height / destH;

            for (int y = 0; y < destH; y++)
            {
                double srcY = (y + 0.5) * yRatio - 0.5;
                int y0 = (int)Math.Floor(srcY);
                int y1 = y0 + 1;
                double wy = srcY - y0;
                if (y0 < 0)
                {
                    y0 = 0; y1 = 0; wy = 0;
                }
                else if (y0 >= source.Height) { y0 = source.Height - 1; y1 = source.Height - 1; wy = 0; }
                if (y1 >= source.Height) { y1 = source.Height - 1; }

                for (int x = 0; x < destW; x++)
                {
                    double srcX = (x + 0.5) * xRatio - 0.5;
                    int x0 = (int)Math.Floor(srcX);
                    int x1 = x0 + 1;
                    double wx = srcX - x0;
                    if (x0 < 0)
                    {
                        x0 = 0; x1 = 0; wx = 0;
                    }
                    else if (x0 >= source.Width) { x0 = source.Width - 1; x1 = source.Width - 1; wx = 0; }
                    if (x1 >= source.Width) { x1 = source.Width - 1; }

                    int k00 = y0 * source.Width + x0;
                    int k10 = y0 * source.Width + x1;
                    int k01 = y1 * source.Width + x0;
                    int k11 = y1 * source.Width + x1;

                    double r00 = source.r[k00];
                    double r10 = source.r[k10];
                    double r01 = source.r[k01];
                    double r11 = source.r[k11];

                    double g00 = source.g[k00];
                    double g10 = source.g[k10];
                    double g01 = source.g[k01];
                    double g11 = source.g[k11];

                    double b00 = source.b[k00];
                    double b10 = source.b[k10];
                    double b01 = source.b[k01];
                    double b11 = source.b[k11];

                    double wxa = 1.0 - wx;
                    double wya = 1.0 - wy;

                    double rInterp = r00 * wxa * wya + r10 * wx * wya + r01 * wxa * wy + r11 * wx * wy;
                    double gInterp = g00 * wxa * wya + g10 * wx * wya + g01 * wxa * wy + g11 * wx * wy;
                    double bInterp = b00 * wxa * wya + b10 * wx * wya + b01 * wxa * wy + b11 * wx * wy;

                    int dstIdx = y * destW + x;
                    int rr = (int)(rInterp + 0.5);
                    int gg = (int)(gInterp + 0.5);
                    int bb = (int)(bInterp + 0.5);
                    if (rr < 0) rr = 0; if (rr > 65535) rr = 65535;
                    if (gg < 0) gg = 0; if (gg > 65535) gg = 65535;
                    if (bb < 0) bb = 0; if (bb > 65535) bb = 65535;
                    result.r[dstIdx] = (ushort)rr;
                    result.g[dstIdx] = (ushort)gg;
                    result.b[dstIdx] = (ushort)bb;

                    if (source.hasAlphaChannel && source.a != null)
                    {
                        double a00 = source.a[k00];
                        double a10 = source.a[k10];
                        double a01 = source.a[k01];
                        double a11 = source.a[k11];
                        double aInterp = a00 * wxa * wya + a10 * wx * wya + a01 * wxa * wy + a11 * wx * wy;
                        if (double.IsNaN(aInterp) || double.IsInfinity(aInterp)) aInterp = 1.0;
                        if (aInterp < 0) aInterp = 0; if (aInterp > 1) aInterp = 1;
                        result.a![dstIdx] = (float)aInterp;
                    }

                    double br00, br10, br01, br11;
                    if (sourceBrightness != null)
                    {
                        br00 = sourceBrightness[k00];
                        br10 = sourceBrightness[k10];
                        br01 = sourceBrightness[k01];
                        br11 = sourceBrightness[k11];
                    }
                    else
                    {
                        br00 = Math.Clamp((0.2627 * r00 + 0.6780 * g00 + 0.0593 * b00) / 65535.0, 0.0, 1.0);
                        br10 = Math.Clamp((0.2627 * r10 + 0.6780 * g10 + 0.0593 * b10) / 65535.0, 0.0, 1.0);
                        br01 = Math.Clamp((0.2627 * r01 + 0.6780 * g01 + 0.0593 * b01) / 65535.0, 0.0, 1.0);
                        br11 = Math.Clamp((0.2627 * r11 + 0.6780 * g11 + 0.0593 * b11) / 65535.0, 0.0, 1.0);
                    }

                    double brightnessInterp = br00 * wxa * wya + br10 * wx * wya + br01 * wxa * wy + br11 * wx * wy;
                    if (double.IsNaN(brightnessInterp) || double.IsInfinity(brightnessInterp)) brightnessInterp = 0.0;
                    result.Brightness[dstIdx] = (float)Math.Clamp(brightnessInterp, 0.0, 1.0);
                }
            }

            result.ProcessStack.Add(new PictureProcessStack
            {
                OperationDisplayName = "Resize (HDR IPicture)",
                Operator = typeof(HDRPicture16bpp),
                ProcessingFuncStackTrace = new(true),
                Properties = new Dictionary<string, object>
                {
                    { "SourceWidth", source.Width },
                    { "SourceHeight", source.Height },
                    { "TargetWidth", targetWidth },
                    { "TargetHeight", targetHeight },
                    { "PreserveAspect", preserveAspect },
                    { "MaximumBrightness", result.MaximumBrightness },
                    { "HasBrightnessChannel", hasBrightness },
                },
                Elapsed = sw.Elapsed
            });

            return result;
        }
    }
}
