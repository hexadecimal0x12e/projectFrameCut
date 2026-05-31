using projectFrameCut.Render.Plugin;
using projectFrameCut.Render.RenderAPIBase.EffectAndMixture;
using projectFrameCut.Shared;
using System;
using System.Buffers;
using System.Diagnostics;

namespace projectFrameCut.Render.Effect
{
    /// <summary>
    /// GPU-accelerated <see cref="IPictureResizer"/> implementation.
    /// Delegates pixel resizing to an <see cref="IComputer"/> registered as "ResizeComputer".
    /// Falls back to <see cref="CPUBilinearPictureResizer"/> when no GPU computer is available.
    /// Assign to <see cref="IPicture.PictureResizer"/> to enable globally.
    /// </summary>
    public class HwAccelPictureResizer : IPictureResizer
    {
        private IComputer? _cachedComputer;
        private bool _computerResolved;

        private static readonly CPUBilinearPictureResizer _cpuFallback = new();

        private IComputer? GetComputer()
        {
            if (!_computerResolved)
            {
                _cachedComputer = PluginManager.CreateComputer("ResizeComputer", forceCreate: false);
                _computerResolved = true;
            }
            return _cachedComputer;
        }

        private static (int destW, int destH) ComputeDestSize(
            int sourceW, int sourceH, int targetW, int targetH, bool preserveAspect)
        {
            int destW = targetW;
            int destH = targetH;
            if (preserveAspect)
            {
                double sx = (double)targetW / sourceW;
                double sy = (double)targetH / sourceH;
                double s = Math.Min(sx, sy);
                destW = Math.Max(1, (int)(sourceW * s + 0.5));
                destH = Math.Max(1, (int)(sourceH * s + 0.5));
            }
            return (destW, destH);
        }

        private static float[] ConvertToFloat(ushort[] data)
        {
            var result = new float[data.Length];
            for (int i = 0; i < data.Length; i++)
                result[i] = data[i];
            return result;
        }

        private static float[] ConvertToFloat(byte[] data)
        {
            var result = new float[data.Length];
            for (int i = 0; i < data.Length; i++)
                result[i] = data[i];
            return result;
        }

        public Picture16bpp Resize(Picture16bpp source, int targetWidth, int targetHeight, bool preserveAspect)
        {
            var computer = GetComputer();
            if (computer == null)
                return _cpuFallback.Resize(source, targetWidth, targetHeight, preserveAspect);

            var sw = Stopwatch.StartNew();
            if (targetWidth == source.Width && targetHeight == source.Height)
                return source;

            if (targetWidth <= 0 || targetHeight <= 0)
                throw new ArgumentException("targetWidth and targetHeight must be positive");
            if (source.Width <= 0 || source.Height <= 0)
                throw new InvalidOperationException("Source image has invalid dimensions");

            var (destW, destH) = ComputeDestSize(source.Width, source.Height, targetWidth, targetHeight, preserveAspect);
            if (destW == source.Width && destH == source.Height)
                return source;

            float[] r = ConvertToFloat(source.r);
            float[] g = ConvertToFloat(source.g);
            float[] b = ConvertToFloat(source.b);

            float[] a;
            bool aFromPool = false;
            if (source.a != null)
            {
                a = source.a;
            }
            else
            {
                a = ArrayPool<float>.Shared.Rent(source.Pixels);
                Array.Fill(a, 1f, 0, source.Pixels);
                aFromPool = true;
            }

            IPicture? result = null;
            try
            {
                // Pass 16 as pixel-type hint so the computer can return ushort[] for RGB directly
                var resultArr = computer.Compute(new object[]
                {
                    r, g, b, a,
                    (float)source.Width, (float)source.Height,
                    (float)destW, (float)destH,
                    16  // pixel type hint
                });

                if (resultArr.Length != 4)
                    throw new InvalidOperationException("Accelerator didn't return the expected 4 arrays.");

                result = new Picture16bpp(destW, destH)
                {
                    a = source.HasAlphaChannel && resultArr[3] is float[] aOut ? aOut : null,
                    HasAlphaChannel = source.HasAlphaChannel
                };

                // Computer may return ushort[] (typed path) or float[] (fallback)
                if (resultArr[0] is ushort[] rOutUS && resultArr[1] is ushort[] gOutUS && resultArr[2] is ushort[] bOutUS)
                {
                    ((Picture16bpp)result).r = rOutUS;
                    ((Picture16bpp)result).g = gOutUS;
                    ((Picture16bpp)result).b = bOutUS;
                }
                else if (resultArr[0] is float[] rOutF && resultArr[1] is float[] gOutF && resultArr[2] is float[] bOutF)
                {
                    var pixels = result.Pixels;
                    var rRes = new ushort[pixels];
                    var gRes = new ushort[pixels];
                    var bRes = new ushort[pixels];
                    for (int i = 0; i < pixels; i++)
                    {
                        rRes[i] = (ushort)Math.Clamp(rOutF[i], 0, 65535);
                        gRes[i] = (ushort)Math.Clamp(gOutF[i], 0, 65535);
                        bRes[i] = (ushort)Math.Clamp(bOutF[i], 0, 65535);
                    }
                    ((Picture16bpp)result).r = rRes;
                    ((Picture16bpp)result).g = gRes;
                    ((Picture16bpp)result).b = bRes;
                }
                else
                {
                    throw new InvalidOperationException("Accelerator returned unexpected array types.");
                }

                sw.Stop();
                result.ProcessStack.Add(new PictureProcessStack
                {
                    OperationDisplayName = "Resize (GPU)",
                    Operator = typeof(HwAccelPictureResizer),
                    ProcessingFuncStackTrace = new StackTrace(true),
                    Properties = new Dictionary<string, object>
                    {
                        { "SourceWidth", source.Width },
                        { "SourceHeight", source.Height },
                        { "TargetWidth", destW },
                        { "TargetHeight", destH },
                        { "PreserveAspect", preserveAspect },
                    },
                    Elapsed = sw.Elapsed,
                });
            }
            finally
            {
                if (aFromPool)
                    ArrayPool<float>.Shared.Return(a);
            }

            return (Picture16bpp)result!;
        }

        public Picture8bpp Resize(Picture8bpp source, int targetWidth, int targetHeight, bool preserveAspect)
        {
            var computer = GetComputer();
            if (computer == null)
                return _cpuFallback.Resize(source, targetWidth, targetHeight, preserveAspect);

            var sw = Stopwatch.StartNew();
            if (targetWidth == source.Width && targetHeight == source.Height)
                return source;

            if (targetWidth <= 0 || targetHeight <= 0)
                throw new ArgumentException("targetWidth and targetHeight must be positive");
            if (source.Width <= 0 || source.Height <= 0)
                throw new InvalidOperationException("Source image has invalid dimensions");

            var (destW, destH) = ComputeDestSize(source.Width, source.Height, targetWidth, targetHeight, preserveAspect);
            if (destW == source.Width && destH == source.Height)
                return source;

            float[] r = ConvertToFloat(source.r);
            float[] g = ConvertToFloat(source.g);
            float[] b = ConvertToFloat(source.b);

            float[] a;
            bool aFromPool = false;
            if (source.a != null)
            {
                a = source.a;
            }
            else
            {
                a = ArrayPool<float>.Shared.Rent(source.Pixels);
                Array.Fill(a, 1f, 0, source.Pixels);
                aFromPool = true;
            }

            IPicture? result = null;
            try
            {
                // Pass 8 as pixel-type hint so the computer can return byte[] for RGB directly
                var resultArr = computer.Compute(new object[]
                {
                    r, g, b, a,
                    (float)source.Width, (float)source.Height,
                    (float)destW, (float)destH,
                    8  // pixel type hint
                });

                if (resultArr.Length != 4)
                    throw new InvalidOperationException("Accelerator didn't return the expected 4 arrays.");

                result = new Picture8bpp(destW, destH)
                {
                    a = source.HasAlphaChannel && resultArr[3] is float[] aOut ? aOut : null,
                    HasAlphaChannel = source.HasAlphaChannel
                };

                // Computer may return byte[] (typed path) or float[] (fallback)
                if (resultArr[0] is byte[] rOutB && resultArr[1] is byte[] gOutB && resultArr[2] is byte[] bOutB)
                {
                    ((Picture8bpp)result).r = rOutB;
                    ((Picture8bpp)result).g = gOutB;
                    ((Picture8bpp)result).b = bOutB;
                }
                else if (resultArr[0] is float[] rOutF && resultArr[1] is float[] gOutF && resultArr[2] is float[] bOutF)
                {
                    var pixels = result.Pixels;
                    var rRes = new byte[pixels];
                    var gRes = new byte[pixels];
                    var bRes = new byte[pixels];
                    for (int i = 0; i < pixels; i++)
                    {
                        rRes[i] = (byte)Math.Clamp(rOutF[i], 0, 255);
                        gRes[i] = (byte)Math.Clamp(gOutF[i], 0, 255);
                        bRes[i] = (byte)Math.Clamp(bOutF[i], 0, 255);
                    }
                    ((Picture8bpp)result).r = rRes;
                    ((Picture8bpp)result).g = gRes;
                    ((Picture8bpp)result).b = bRes;
                }
                else
                {
                    throw new InvalidOperationException("Accelerator returned unexpected array types.");
                }

                sw.Stop();
                result.ProcessStack.Add(new PictureProcessStack
                {
                    OperationDisplayName = "Resize (GPU)",
                    Operator = typeof(HwAccelPictureResizer),
                    ProcessingFuncStackTrace = new StackTrace(true),
                    Properties = new Dictionary<string, object>
                    {
                        { "SourceWidth", source.Width },
                        { "SourceHeight", source.Height },
                        { "TargetWidth", destW },
                        { "TargetHeight", destH },
                        { "PreserveAspect", preserveAspect },
                    },
                    Elapsed = sw.Elapsed,
                });
            }
            finally
            {
                if (aFromPool)
                    ArrayPool<float>.Shared.Return(a);
            }

            return (Picture8bpp)result!;
        }

        public HDRPicture16bpp Resize(HDRPicture16bpp source, int targetWidth, int targetHeight, bool preserveAspect)
        {
            var computer = GetComputer();
            if (computer == null)
                return _cpuFallback.Resize(source, targetWidth, targetHeight, preserveAspect);

            var sw = Stopwatch.StartNew();
            if (targetWidth == source.Width && targetHeight == source.Height)
                return source;

            if (targetWidth <= 0 || targetHeight <= 0)
                throw new ArgumentException("targetWidth and targetHeight must be positive");
            if (source.Width <= 0 || source.Height <= 0)
                throw new InvalidOperationException("Source image has invalid dimensions");

            var (destW, destH) = ComputeDestSize(source.Width, source.Height, targetWidth, targetHeight, preserveAspect);
            if (destW == source.Width && destH == source.Height)
                return source;

            float[] r = ConvertToFloat(source.r);
            float[] g = ConvertToFloat(source.g);
            float[] b = ConvertToFloat(source.b);

            float[] a;
            bool aFromPool = false;
            if (source.a != null)
            {
                a = source.a;
            }
            else
            {
                a = ArrayPool<float>.Shared.Rent(source.Pixels);
                Array.Fill(a, 1f, 0, source.Pixels);
                aFromPool = true;
            }

            HDRPicture16bpp? result = null;
            try
            {
                var resultArr = computer.Compute(new object[]
                {
                    r, g, b, a,
                    (float)source.Width, (float)source.Height,
                    (float)destW, (float)destH,
                    16  // pixel type hint
                });

                if (resultArr.Length != 4)
                    throw new InvalidOperationException("Accelerator didn't return the expected 4 arrays.");

                int dstPixels = checked(destW * destH);
                result = new HDRPicture16bpp(destW, destH)
                {
                    a = source.HasAlphaChannel && resultArr[3] is float[] aOut ? aOut : null,
                    HasAlphaChannel = source.HasAlphaChannel,
                    MaximumBrightness = source.MaximumBrightness,
                };

                if (resultArr[0] is ushort[] rOutUS && resultArr[1] is ushort[] gOutUS && resultArr[2] is ushort[] bOutUS)
                {
                    result.r = rOutUS;
                    result.g = gOutUS;
                    result.b = bOutUS;
                }
                else if (resultArr[0] is float[] rOutF && resultArr[1] is float[] gOutF && resultArr[2] is float[] bOutF)
                {
                    var pixels = result.Pixels;
                    result.r = new ushort[pixels];
                    result.g = new ushort[pixels];
                    result.b = new ushort[pixels];
                    for (int i = 0; i < pixels; i++)
                    {
                        result.r[i] = (ushort)Math.Clamp(rOutF[i], 0, 65535);
                        result.g[i] = (ushort)Math.Clamp(gOutF[i], 0, 65535);
                        result.b[i] = (ushort)Math.Clamp(bOutF[i], 0, 65535);
                    }
                }
                else
                {
                    throw new InvalidOperationException("Accelerator returned unexpected array types.");
                }

                // Interpolate brightness channel on CPU (bilinear) since the GPU computer
                // only handles the RGBA channels.
                float[]? sourceBrightness = source.Brightness;
                if (sourceBrightness != null && sourceBrightness.Length == source.Pixels)
                {
                    result.Brightness = InterpolateBrightness(
                        sourceBrightness, source.Width, source.Height, destW, destH);
                }
                else
                {
                    result.Brightness = new float[dstPixels];
                }

                sw.Stop();
                result.ProcessStack.Add(new PictureProcessStack
                {
                    OperationDisplayName = "Resize (GPU)",
                    Operator = typeof(HwAccelPictureResizer),
                    ProcessingFuncStackTrace = new StackTrace(true),
                    Properties = new Dictionary<string, object>
                    {
                        { "SourceWidth", source.Width },
                        { "SourceHeight", source.Height },
                        { "TargetWidth", destW },
                        { "TargetHeight", destH },
                        { "PreserveAspect", preserveAspect },
                        { "MaximumBrightness", source.MaximumBrightness },
                    },
                    Elapsed = sw.Elapsed,
                });
            }
            finally
            {
                if (aFromPool)
                    ArrayPool<float>.Shared.Return(a);
            }

            return result;
        }

        private static float[] InterpolateBrightness(
            float[] srcBrightness, int srcW, int srcH, int dstW, int dstH)
        {
            var result = new float[dstW * dstH];
            double xRatio = (double)srcW / dstW;
            double yRatio = (double)srcH / dstH;

            for (int y = 0; y < dstH; y++)
            {
                double srcY = (y + 0.5) * yRatio - 0.5;
                int y0 = (int)Math.Floor(srcY);
                int y1 = y0 + 1;
                double wy = srcY - y0;
                if (y0 < 0) { y0 = 0; y1 = 0; wy = 0; }
                else if (y0 >= srcH) { y0 = srcH - 1; y1 = srcH - 1; wy = 0; }
                if (y1 >= srcH) { y1 = srcH - 1; }

                for (int x = 0; x < dstW; x++)
                {
                    double srcX = (x + 0.5) * xRatio - 0.5;
                    int x0 = (int)Math.Floor(srcX);
                    int x1 = x0 + 1;
                    double wx = srcX - x0;
                    if (x0 < 0) { x0 = 0; x1 = 0; wx = 0; }
                    else if (x0 >= srcW) { x0 = srcW - 1; x1 = srcW - 1; wx = 0; }
                    if (x1 >= srcW) { x1 = srcW - 1; }

                    int k00 = y0 * srcW + x0;
                    int k10 = y0 * srcW + x1;
                    int k01 = y1 * srcW + x0;
                    int k11 = y1 * srcW + x1;

                    double v00 = srcBrightness[k00];
                    double v10 = srcBrightness[k10];
                    double v01 = srcBrightness[k01];
                    double v11 = srcBrightness[k11];

                    double wxa = 1.0 - wx;
                    double wya = 1.0 - wy;
                    double interp = v00 * wxa * wya + v10 * wx * wya
                                  + v01 * wxa * wy + v11 * wx * wy;

                    if (double.IsNaN(interp) || double.IsInfinity(interp)) interp = 0.0;
                    result[y * dstW + x] = (float)Math.Clamp(interp, 0.0, 1.0);
                }
            }
            return result;
        }
    }
}
