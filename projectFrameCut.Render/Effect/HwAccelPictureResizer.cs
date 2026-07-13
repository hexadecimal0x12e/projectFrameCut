using projectFrameCut.Drawing.Processing.Resizing;
using projectFrameCut.Render.HwAccelContracts;
using projectFrameCut.Render.Plugin;
using projectFrameCut.Render.RenderAPIBase.EffectAndMixture;
using projectFrameCut.Shared;
using System;
using System.Buffers;
using System.Diagnostics;
using System.Runtime.CompilerServices;

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
        private IResizeComputer? _cachedResizeComputer;
        private bool _computerResolved;

        private static readonly BilinearPictureResizer _cpuFallback = new();
        private readonly CancellationToken _cancellationToken;

        public HwAccelPictureResizer()
            : this(CancellationToken.None)
        {
        }

        public HwAccelPictureResizer(CancellationToken cancellationToken)
        {
            _cancellationToken = cancellationToken;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private IResizeComputer? GetResizeComputer()
        {
            if (!_computerResolved)
            {
                var computer = PluginManager.CreateComputer("ResizeComputer", forceCreate: false);
                _cachedComputer = computer;
                _cachedResizeComputer = computer as IResizeComputer;
                _computerResolved = true;
            }
            return _cachedResizeComputer;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private IComputer? GetComputer() => _computerResolved ? _cachedComputer
            : (GetResizeComputer(), _cachedComputer).Item2;

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
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

        /// <summary>
        /// 验证尺寸并计算目标尺寸，提前返回 source 本身（尺寸不变时）。
        /// </summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private (int destW, int destH) ValidateAndComputeSize<T>(
            IPicture<T> source, int targetWidth, int targetHeight, bool preserveAspect)
        {
            if (targetWidth <= 0 || targetHeight <= 0)
                throw new ArgumentException("targetWidth and targetHeight must be positive");
            if (source.Width <= 0 || source.Height <= 0)
                throw new InvalidOperationException("Source image has invalid dimensions");

            var (destW, destH) = ComputeDestSize(source.Width, source.Height, targetWidth, targetHeight, preserveAspect);
            _cancellationToken.ThrowIfCancellationRequested();
            return (destW, destH);
        }

        private static float[] ConvertToFloat<T>(T[] data) where T : unmanaged
        {
            var result = new float[data.Length];
            if (typeof(T) == typeof(byte))
            {
                var src = (byte[])(object)data;
                for (int i = 0; i < data.Length; i++)
                    result[i] = src[i];
            }
            else if (typeof(T) == typeof(ushort))
            {
                var src = (ushort[])(object)data;
                for (int i = 0; i < data.Length; i++)
                    result[i] = src[i];
            }
            else
            {
                for (int i = 0; i < data.Length; i++)
                    result[i] = (float)Convert.ToDouble(data[i]);
            }
            return result;
        }

        /// <summary>
        /// 填充 ProcessStack 信息并停止计时器。
        /// </summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static void FinalizeResult(IPicture result, Stopwatch sw, string displayName, Type operatorType,
            Dictionary<string, object>? extraProps = null)
        {
            sw.Stop();
            var props = new Dictionary<string, object>
            {
                { "OperationDisplayName", displayName }
            };
            if (extraProps != null)
                foreach (var kv in extraProps)
                    props[kv.Key] = kv.Value;

            result.ProcessStack.Add(new PictureProcessStack
            {
                Elapsed = sw.Elapsed,
                OperationDisplayName = displayName,
                Operator = operatorType,
                ProcessingFuncStackTrace = new StackTrace(true),
                Properties = props
            });
        }

        /// <summary>
        /// 将 source 的 RGB 通道转换为 float[]，并准备 alpha 数组。
        /// </summary>
        private static (float[] r, float[] g, float[] b, float[] a, bool aFromPool, bool hasAlpha)
            PrepareChannels<T>(IPicture<T> source, ArrayPool<float> pool) where T : unmanaged
        {
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
                a = pool.Rent(source.Pixels);
                Array.Fill(a, 1f, 0, source.Pixels);
                aFromPool = true;
            }
            return (r, g, b, a, aFromPool, source.HasAlphaChannel);
        }

        [MethodImpl(MethodImplOptions.AggressiveOptimization)]
        public IPicture<ushort> Resize(IPicture<ushort> source, int targetWidth, int targetHeight, bool preserveAspect)
        {
            var resizeComputer = GetResizeComputer();
            if (resizeComputer == null)
            {
                _cancellationToken.ThrowIfCancellationRequested();
                return (Picture16bpp)_cpuFallback.Resize(source, targetWidth, targetHeight, preserveAspect);
            }

            var (destW, destH) = ValidateAndComputeSize(source, targetWidth, targetHeight, preserveAspect);
            if (destW == source.Width && destH == source.Height)
                return source;

            var sw = Stopwatch.StartNew();
            var (r, g, b, a, aFromPool, hasAlpha) = PrepareChannels(source, ArrayPool<float>.Shared);
            Picture16bpp? result = null;
            try
            {
                _cancellationToken.ThrowIfCancellationRequested();
                var r16 = resizeComputer.ComputeResizeUshort(r, g, b, a,
                    source.Width, source.Height, destW, destH);
                result = new Picture16bpp(destW, destH)
                {
                    r = r16.R,
                    g = r16.G,
                    b = r16.B,
                    a = hasAlpha ? r16.A : null,
                    HasAlphaChannel = hasAlpha
                };
                FinalizeResult(result, sw, "Resize (GPU)", typeof(HwAccelPictureResizer),
                    new() { { "SourceWidth", source.Width }, { "SourceHeight", source.Height },
                            { "TargetWidth", destW }, { "TargetHeight", destH },
                            { "PreserveAspect", preserveAspect } });
            }
            finally
            {
                if (aFromPool) ArrayPool<float>.Shared.Return(a);
            }
            return result!;
        }
        [MethodImpl(MethodImplOptions.AggressiveOptimization)]
        public IPicture<byte> Resize(IPicture<byte> source, int targetWidth, int targetHeight, bool preserveAspect)
        {
            var resizeComputer = GetResizeComputer();
            if (resizeComputer == null)
            {
                _cancellationToken.ThrowIfCancellationRequested();
                return (Picture8bpp)_cpuFallback.Resize(source, targetWidth, targetHeight, preserveAspect);
            }

            var (destW, destH) = ValidateAndComputeSize(source, targetWidth, targetHeight, preserveAspect);
            if (destW == source.Width && destH == source.Height)
                return source;

            var sw = Stopwatch.StartNew();
            var (r, g, b, a, aFromPool, hasAlpha) = PrepareChannels(source, ArrayPool<float>.Shared);
            Picture8bpp? result = null;
            try
            {
                _cancellationToken.ThrowIfCancellationRequested();
                var r8 = resizeComputer.ComputeResizeByte(r, g, b, a,
                    source.Width, source.Height, destW, destH);
                result = new Picture8bpp(destW, destH)
                {
                    r = r8.R,
                    g = r8.G,
                    b = r8.B,
                    a = hasAlpha ? r8.A : null,
                    HasAlphaChannel = hasAlpha
                };
                FinalizeResult(result, sw, "Resize (GPU)", typeof(HwAccelPictureResizer),
                    new() { { "SourceWidth", source.Width }, { "SourceHeight", source.Height },
                            { "TargetWidth", destW }, { "TargetHeight", destH },
                            { "PreserveAspect", preserveAspect } });
            }
            finally
            {
                if (aFromPool) ArrayPool<float>.Shared.Return(a);
            }
            return result!;
        }
        [MethodImpl(MethodImplOptions.AggressiveOptimization)]
        public IHDRPicture<ushort> Resize(IHDRPicture<ushort> source, int targetWidth, int targetHeight, bool preserveAspect)
        {
            var resizeComputer = GetResizeComputer();
            if (resizeComputer == null)
            {
                _cancellationToken.ThrowIfCancellationRequested();
                return (HDRPicture16bpp)_cpuFallback.Resize(source, targetWidth, targetHeight, preserveAspect);
            }

            var (destW, destH) = ValidateAndComputeSize(source, targetWidth, targetHeight, preserveAspect);
            if (destW == source.Width && destH == source.Height)
                return source;

            var sw = Stopwatch.StartNew();
            var (r, g, b, a, aFromPool, hasAlpha) = PrepareChannels(source, ArrayPool<float>.Shared);
            HDRPicture16bpp? result = null;
            try
            {
                _cancellationToken.ThrowIfCancellationRequested();

                var r16 = resizeComputer.ComputeResizeUshort(r, g, b, a,
                    source.Width, source.Height, destW, destH);
                int dstPixels = checked(destW * destH);
                result = new HDRPicture16bpp(destW, destH)
                {
                    r = r16.R,
                    g = r16.G,
                    b = r16.B,
                    a = hasAlpha ? r16.A : null,
                    HasAlphaChannel = hasAlpha,
                    MaximumBrightness = source.MaximumBrightness,
                };

                // Interpolate brightness channel on CPU since GPU computer only handles RGBA
                float[]? sourceBrightness = source.Brightness;
                if (sourceBrightness != null && sourceBrightness.Length == source.Pixels)
                    result.Brightness = InterpolateBrightness(sourceBrightness, source.Width, source.Height, destW, destH, _cancellationToken);
                else
                    result.Brightness = new float[dstPixels];

                FinalizeResult(result, sw, "Resize (GPU)", typeof(HwAccelPictureResizer),
                    new() { { "SourceWidth", source.Width }, { "SourceHeight", source.Height },
                            { "TargetWidth", destW }, { "TargetHeight", destH },
                            { "PreserveAspect", preserveAspect },
                            { "MaximumBrightness", source.MaximumBrightness } });
            }
            finally
            {
                if (aFromPool) ArrayPool<float>.Shared.Return(a);
            }
            return result!;
        }
        [MethodImpl(MethodImplOptions.AggressiveOptimization | MethodImplOptions.AggressiveInlining)]
        private static float[] InterpolateBrightness(
            float[] srcBrightness, int srcW, int srcH, int dstW, int dstH,
            CancellationToken cancellationToken = default)
        {
            var result = new float[dstW * dstH];
            double xRatio = (double)srcW / dstW;
            double yRatio = (double)srcH / dstH;

            for (int y = 0; y < dstH; y++)
            {
                if ((y & 0xF) == 0)
                    cancellationToken.ThrowIfCancellationRequested();

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
