using projectFrameCut.Drawing.Base.Picture;
using projectFrameCut.Drawing.Vector;
using projectFrameCut.Drawing.Vector.ImportExport;
using projectFrameCut.Shared;
using IPicture = projectFrameCut.Drawing.Base.IPicture;

namespace projectFrameCut.Render.HwAccelEngine.VectorRasterizer
{
    /// <summary>
    /// GPU-accelerated vector-to-raster converter.
    /// On Windows uses Win2D (Direct2D) offscreen rendering with an ILGPU
    /// compute-shader fallback; on Android uses OpenGL ES 3.1 or Vulkan
    /// compute; on other platforms falls back to the CPU scanline renderer.
    /// </summary>
    public class VectorToPictureHwAccel : IVectorPictureRasterizer
    {
#if WINDOWS
        /// <summary>Latched to true after a non-transient Win2D failure so we don't retry it every frame.</summary>
        private static volatile bool s_win2dUnavailable;
#endif

        /// <summary>Convert a vector picture to a raster IPicture using hardware acceleration when available.</summary>
        public IPicture Convert(VectorPicture canvas, int width, int height,
            bool transparentBackground = false, AntiAliasMode aaMode = AntiAliasMode.None, CancellationToken ct = default)
        {
            if (width <= 0 || height <= 0)
                throw new ArgumentOutOfRangeException($"Canvas size must be positive. Got {width}x{height}.");

            int scaleFactor = aaMode switch
            {
                AntiAliasMode.None => 1,
                _ => (int)aaMode
            };

            int renderWidth = width * scaleFactor;
            int renderHeight = height * scaleFactor;

#if WINDOWS
            // Preferred path: Win2D (Direct2D) offscreen rendering. Direct2D has
            // high-quality per-primitive antialiasing, so we render at the target
            // size directly instead of supersampling.
            if (!(s_win2dUnavailable || HwAccelEnginePlugin.disableWin2DRasterizer))
            {
                try
                {
                    return VectorRasterizer.Windows.Win2DVectorRasterizer.Render(
                        canvas, width, height, transparentBackground,
                        antialias: aaMode != AntiAliasMode.None, ct);
                }
                catch (OperationCanceledException)
                {
                    throw;
                }
                catch (Exception ex)
                {
                    s_win2dUnavailable = true;
                    Logger.Log($"Win2D vector rasterization failed, falling back to ILGPU: {ex}", "warning");
                }
            }

            // Fallback path: ILGPU compute rasterization (with SSAA supersampling).
            // Build flat GPU primitives from the vector picture
            var build = PrimitiveBuilder.Build(canvas, renderWidth, renderHeight);
            var primitives = build.Primitives;
            if (primitives.Count == 0)
            {
                Logger.Log("Trying to render a blank rect.", "error");
                return Picture16bpp.GenerateSolidColor(width, height, 128 * 257, 0, 128 * 257, 1f);
            }

            var result = Windows.ILGpuVectorRasterizer.Render(primitives, build.Edges, renderWidth, renderHeight, transparentBackground);

            if (scaleFactor > 1)
                return DownsampleToOutput(result, width, height, scaleFactor, renderWidth);

            return result;
#elif ANDROID
            // Build flat GPU primitives from the vector picture
            var build = PrimitiveBuilder.Build(canvas, renderWidth, renderHeight);
            var primitives = build.Primitives;
            if (primitives.Count == 0)
            {
                Logger.Log("Trying to render a blank rect.", "error");
                return Picture16bpp.GenerateSolidColor(width, height, 128 * 257, 0, 128 * 257, 1f);
            }

            IPicture result;
            if (projectFrameCut.Render.HwAccelEngine.Platforms.Android.ComputerHelper.UseVulkanBackend)
            {
                result = Platforms.Android.VulkanVectorRasterizer.Render(primitives, build.Edges, renderWidth, renderHeight, transparentBackground);
            }
            else
            {
                result = Platforms.Android.OpenGLVectorRasterizer.Render(primitives, build.Edges, renderWidth, renderHeight, transparentBackground);
            }

            if (scaleFactor > 1)
                return DownsampleToOutput(result, width, height, scaleFactor, renderWidth);
            return result;
#else
            // Platform without GPU compute backend — fall back to CPU
            return new CPUVectorPictureRasterizer().Convert(canvas, width, height, transparentBackground, aaMode, ct);
#endif
        }

        private static IPicture DownsampleToOutput(
            IPicture source, int outWidth, int outHeight, int scaleFactor, int renderWidth)
        {
            var src = ((Picture16bpp)source).SetAlpha(true);
            int pixels = outWidth * outHeight;
            int blockSize = scaleFactor * scaleFactor;

            var outR = new ushort[pixels];
            var outG = new ushort[pixels];
            var outB = new ushort[pixels];
            var outA = new float[pixels];

            Parallel.For(0, outHeight, y =>
            {
                int inBaseY = y * scaleFactor;
                for (int x = 0; x < outWidth; x++)
                {
                    int inBaseX = x * scaleFactor;
                    long sumR = 0, sumG = 0, sumB = 0;
                    long sumA = 0;

                    for (int sy = 0; sy < scaleFactor; sy++)
                    {
                        int row = (inBaseY + sy) * renderWidth + inBaseX;
                        for (int sx = 0; sx < scaleFactor; sx++)
                        {
                            int idx = row + sx;
                            sumR += src.r[idx];
                            sumG += src.g[idx];
                            sumB += src.b[idx];
                            sumA += (long)(src.a[idx] * ushort.MaxValue);
                        }
                    }

                    int oi = y * outWidth + x;
                    outR[oi] = (ushort)(sumR / blockSize);
                    outG[oi] = (ushort)(sumG / blockSize);
                    outB[oi] = (ushort)(sumB / blockSize);
                    outA[oi] = (float)sumA / (blockSize * ushort.MaxValue);
                }
            });

            bool needsAlpha = false;
            for (int i = 0; i < pixels; i++)
            {
                if (outA[i] < 1f)
                { needsAlpha = true; break; }
            }

            return new Picture16bpp(outWidth, outHeight)
            {
                r = outR,
                g = outG,
                b = outB,
                a = needsAlpha ? outA : null,
                HasAlphaChannel = needsAlpha,
            };
        }
    }
}
