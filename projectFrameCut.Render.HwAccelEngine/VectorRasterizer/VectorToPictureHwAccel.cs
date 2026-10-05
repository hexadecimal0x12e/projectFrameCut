using projectFrameCut.Drawing.Base.Picture;
using projectFrameCut.Drawing.Vector;
using projectFrameCut.Drawing.Vector.ImportExport;
using projectFrameCut.Shared;
using projectFrameCut.Render.RenderAPIBase.VectorContent;


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
            var build = PrimitiveBuilder.Build(VectorPictureRasterization.ScaleStrokes(canvas, scaleFactor), renderWidth, renderHeight);
            var primitives = build.Primitives;
            if (primitives.Count == 0)
            {
                Logger.Log("Trying to render a blank rect.", "error");
                return Picture16bpp.GenerateSolidColor(width, height, 128 * 257, 0, 128 * 257, 1f);
            }

            var result = Windows.ILGpuVectorRasterizer.Render(primitives, build.Edges, renderWidth, renderHeight, transparentBackground);

            if (scaleFactor > 1)
            {
                using (result)
                    return VectorPictureRasterization.Downsample((Picture16bpp)result, width, height, scaleFactor, ct);
            }

            return result;
#elif ANDROID
            // Build flat GPU primitives from the vector picture
            var build = PrimitiveBuilder.Build(VectorPictureRasterization.ScaleStrokes(canvas, scaleFactor), renderWidth, renderHeight);
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
            {
                using (result)
                    return VectorPictureRasterization.Downsample((Picture16bpp)result, width, height, scaleFactor, ct);
            }
            return result;
#else
            // Platform without GPU compute backend — fall back to CPU
            return new CpuVectorPictureRasterizer().Convert(canvas, width, height, transparentBackground, aaMode, ct);
#endif
        }

    }
}
