using projectFrameCut.Render.RenderAPIBase.EffectAndMixture;
using projectFrameCut.Shared;
using System;
using System.Buffers;
using System.Collections.Generic;
using System.Diagnostics;

namespace projectFrameCut.Render.Compose
{
    public static class OverlayMixture
    {
        public const string ComputerId = "OverlayComputer";
        private const float DefaultHdrMaximumBrightness = 1000f;

        public static IPicture Mix(IPicture basePicture, IPicture topPicture, IComputer? computer, IPicture.PicturePixelMode targetPPB)
            => MixInternal(
                basePicture,
                topPicture,
                computer,
                targetPPB,
                resizeTopWhenDimensionMismatch: true,
                topStartX: 0,
                topStartY: 0,
                targetWidth: basePicture.Width,
                targetHeight: basePicture.Height);

        public static IPicture Mix(
            IPicture basePicture,
            IPicture topPicture,
            IComputer? computer,
            IPicture.PicturePixelMode targetPPB,
            int topStartX,
            int topStartY,
            int targetWidth,
            int targetHeight)
        {
            if (targetWidth <= 0 || targetHeight <= 0)
            {
                throw new ArgumentException("targetWidth and targetHeight must be positive.");
            }

            return MixInternal(
                basePicture,
                topPicture,
                computer,
                targetPPB,
                resizeTopWhenDimensionMismatch: false,
                topStartX,
                topStartY,
                targetWidth,
                targetHeight);
        }

        private static IPicture MixInternal(
            IPicture basePicture,
            IPicture topPicture,
            IComputer? computer,
            IPicture.PicturePixelMode targetPPB,
            bool resizeTopWhenDimensionMismatch,
            int topStartX,
            int topStartY,
            int targetWidth,
            int targetHeight)
        {
            if (computer is null)
            {
                throw new ArgumentNullException(nameof(computer), "OverlayMixture requires a computer.");
            }

            if (targetWidth <= 0 || targetHeight <= 0)
            {
                throw new ArgumentException("targetWidth and targetHeight must be positive.");
            }

            static bool HasValidChannels(IPicture pic)
            {
                if (pic is Picture8bpp p8)
                {
                    if (p8.r is null || p8.g is null || p8.b is null) return false;
                    if (p8.r.Length != p8.Pixels || p8.g.Length != p8.Pixels || p8.b.Length != p8.Pixels) return false;
                    if (p8.hasAlphaChannel && (p8.a is null || p8.a.Length != p8.Pixels)) return false;
                    return true;
                }

                if (pic is Picture16bpp p16)
                {
                    if (p16.r is null || p16.g is null || p16.b is null) return false;
                    if (p16.r.Length != p16.Pixels || p16.g.Length != p16.Pixels || p16.b.Length != p16.Pixels) return false;
                    if (p16.hasAlphaChannel && (p16.a is null || p16.a.Length != p16.Pixels)) return false;
                    return true;
                }

                return true;
            }

            static void ExtractChannels(IPicture pic, out float[] r, out float[] g, out float[] b, out float[]? a)
            {
                if (pic is IPicture<ushort> p16)
                {
                    r = new float[p16.Pixels];
                    g = new float[p16.Pixels];
                    b = new float[p16.Pixels];
                    for (int i = 0; i < p16.Pixels; i++)
                    {
                        r[i] = p16.r[i];
                        g[i] = p16.g[i];
                        b[i] = p16.b[i];
                    }

                    a = p16.hasAlphaChannel ? p16.a : null;
                    return;
                }

                if (pic is IPicture<byte> p8)
                {
                    r = new float[p8.Pixels];
                    g = new float[p8.Pixels];
                    b = new float[p8.Pixels];
                    for (int i = 0; i < p8.Pixels; i++)
                    {
                        r[i] = p8.r[i] * 257f;
                        g[i] = p8.g[i] * 257f;
                        b[i] = p8.b[i] * 257f;
                    }

                    a = p8.hasAlphaChannel ? p8.a : null;
                    return;
                }

                throw new NotSupportedException();
            }

            static float Clamp01(float value)
            {
                if (!float.IsFinite(value)) return 0f;
                if (value < 0f) return 0f;
                if (value > 1f) return 1f;
                return value;
            }

            static float EstimateBrightness(float rr, float gg, float bb)
            {
                float v = (0.2627f * rr + 0.6780f * gg + 0.0593f * bb) / 65535f;
                return Clamp01(v);
            }

            static float ReadAsFloat(object? src, int index)
            {
                if (src is float[] f) return f[index];
                if (src is ushort[] u16) return u16[index];
                if (src is byte[] u8) return u8[index] * 257f;
                throw new InvalidOperationException("Invalid overlay output channel type");
            }

            static float ReadAsAlpha01(object? src, int index)
            {
                if (src is float[] f) return Clamp01(f[index]);
                if (src is ushort[] u16) return Clamp01(u16[index] / 65535f);
                if (src is byte[] u8) return Clamp01(u8[index] / 255f);
                throw new InvalidOperationException("Invalid overlay output alpha/brightness type");
            }

            static float NormalizeHdrMaximumBrightness(float value)
            {
                if (!float.IsFinite(value) || value <= 0f)
                {
                    return DefaultHdrMaximumBrightness;
                }

                return value;
            }

            static bool TryGetHdrBrightness(IPicture pic, out float[]? brightness, out float maximumBrightness)
            {
                if (pic is IHDRPicture<ushort> hdr && hdr.Brightness != null && hdr.Brightness.Length == pic.Pixels)
                {
                    brightness = hdr.Brightness;
                    maximumBrightness = NormalizeHdrMaximumBrightness(hdr.MaximumBrightness);
                    return true;
                }

                brightness = null;
                maximumBrightness = DefaultHdrMaximumBrightness;
                return false;
            }

            static byte[] ConvertToByteChannel(object? src)
            {
                if (src is byte[] b) return b;
                if (src is ushort[] u16)
                {
                    var dstU16 = new byte[u16.Length];
                    for (int i = 0; i < u16.Length; i++)
                    {
                        float v = u16[i] / 257.0f;
                        if (v < 0) v = 0;
                        if (v > 255) v = 255;
                        dstU16[i] = (byte)v;
                    }
                    return dstU16;
                }

                if (src is float[] f)
                {
                    var dst = new byte[f.Length];
                    for (int i = 0; i < f.Length; i++)
                    {
                        float v = f[i] / 257.0f;
                        if (v < 0) v = 0;
                        if (v > 255) v = 255;
                        dst[i] = (byte)v;
                    }
                    return dst;
                }

                throw new InvalidOperationException("Invalid overlay output channel type for byte target");
            }

            static ushort[] ConvertToUShortChannel(object? src)
            {
                if (src is ushort[] u16) return u16;
                if (src is byte[] b)
                {
                    var dstU16 = new ushort[b.Length];
                    for (int i = 0; i < b.Length; i++)
                    {
                        dstU16[i] = (ushort)(b[i] * 257.0f);
                    }
                    return dstU16;
                }

                if (src is float[] f)
                {
                    var dst = new ushort[f.Length];
                    for (int i = 0; i < f.Length; i++)
                    {
                        float v = f[i];
                        if (v < 0) v = 0;
                        if (v > 65535) v = 65535;
                        dst[i] = (ushort)v;
                    }
                    return dst;
                }

                throw new InvalidOperationException("Invalid overlay output channel type for ushort target");
            }

            IPicture? resizedTop = null;
            try
            {
                var sw = Stopwatch.StartNew();
                OverlayedPictureProcessStack procStack = new OverlayedPictureProcessStack
                {
                    BaseSteps = basePicture.ProcessStack,
                    TopSteps = topPicture.ProcessStack,
                    OperationDisplayName = "Overlay effect",
                    Operator = typeof(OverlayMixture),
                    ProcessingFuncStackTrace = new(true),
                };

                if (resizeTopWhenDimensionMismatch && (topPicture.Width != targetWidth || topPicture.Height != targetHeight))
                {
                    resizedTop = topPicture.Resize(targetWidth, targetHeight, false);
                    topPicture = resizedTop;
                    topStartX = 0;
                    topStartY = 0;
                }

                if (!HasValidChannels(basePicture) || !HasValidChannels(topPicture))
                {
                    var baseBpp = (int)basePicture.bitPerPixel;
                    var topBpp = (int)topPicture.bitPerPixel;
                    try
                    {
                        basePicture = basePicture.SaveToSixLaborsImage(baseBpp, saveAlpha: basePicture.hasAlphaChannel)
                            .ToPJFCPicture(baseBpp);
                    }
                    catch
                    {
                    }

                    try
                    {
                        topPicture = topPicture.SaveToSixLaborsImage(topBpp, saveAlpha: topPicture.hasAlphaChannel)
                            .ToPJFCPicture(topBpp);
                    }
                    catch
                    {
                    }
                }

                ExtractChannels(basePicture, out float[] baseR, out float[] baseG, out float[] baseB, out float[]? baseA);
                ExtractChannels(topPicture, out float[] topR, out float[] topG, out float[] topB, out float[]? topA);

                bool baseHasHdrBrightness = TryGetHdrBrightness(basePicture, out float[]? baseBrightness, out float baseMaximumBrightness);
                bool topHasHdrBrightness = TryGetHdrBrightness(topPicture, out float[]? topBrightness, out float topMaximumBrightness);
                bool shouldComposeHdrBrightness = baseHasHdrBrightness || topHasHdrBrightness;

                int targetPixels = checked(targetWidth * targetHeight);
                var outR = new float[targetPixels];
                var outG = new float[targetPixels];
                var outB = new float[targetPixels];
                var outA = new float[targetPixels];
                float[]? outBrightness = shouldComposeHdrBrightness ? new float[targetPixels] : null;

                for (int y = 0; y < targetHeight; y++)
                {
                    int rowTarget = y * targetWidth;
                    bool inBaseY = y >= 0 && y < basePicture.Height;
                    for (int x = 0; x < targetWidth; x++)
                    {
                        int dstIdx = rowTarget + x;
                        if (!inBaseY || x >= basePicture.Width)
                        {
                            outR[dstIdx] = 0f;
                            outG[dstIdx] = 0f;
                            outB[dstIdx] = 0f;
                            outA[dstIdx] = 0f;
                            if (outBrightness != null) outBrightness[dstIdx] = 0f;
                            continue;
                        }

                        int baseIdx = y * basePicture.Width + x;
                        outR[dstIdx] = baseR[baseIdx];
                        outG[dstIdx] = baseG[baseIdx];
                        outB[dstIdx] = baseB[baseIdx];
                        outA[dstIdx] = baseA is null ? 1f : Clamp01(baseA[baseIdx]);

                        if (outBrightness != null)
                        {
                            outBrightness[dstIdx] = baseBrightness != null
                                ? Clamp01(baseBrightness[baseIdx])
                                : EstimateBrightness(baseR[baseIdx], baseG[baseIdx], baseB[baseIdx]);
                        }
                    }
                }

#if DEBUG
                var id = Guid.NewGuid();
                if (!string.IsNullOrWhiteSpace(IPicture.DiagImagePath))
                {
                    LogDiagnostic(
                        $"""
                        Overlay operation {id},
                        base:
                        {basePicture.GetDiagnosticsInfo()}

                        top:
                        {topPicture.GetDiagnosticsInfo()}
                        """
                        );
                    basePicture.SaveAsPng16bpp(Path.Combine(IPicture.DiagImagePath, $"_OverlayDiag-{id}-base.png"));
                    topPicture.SaveAsPng16bpp(Path.Combine(IPicture.DiagImagePath, $"_OverlayDiag-{id}-top.png"));
                }
#endif

                int overlapLeft = Math.Max(0, topStartX);
                int overlapTop = Math.Max(0, topStartY);
                int overlapRight = Math.Min(targetWidth, topStartX + topPicture.Width);
                int overlapBottom = Math.Min(targetHeight, topStartY + topPicture.Height);
                int overlapWidth = Math.Max(0, overlapRight - overlapLeft);
                int overlapHeight = Math.Max(0, overlapBottom - overlapTop);
                int overlapPixels = overlapWidth * overlapHeight;

                var pool = ArrayPool<float>.Shared;
                var intPool = ArrayPool<int>.Shared;

                int[]? mixedIndices = null;
                float[]? mixTopR = null;
                float[]? mixTopG = null;
                float[]? mixTopB = null;
                float[]? mixBaseR = null;
                float[]? mixBaseG = null;
                float[]? mixBaseB = null;
                float[]? mixTopA = null;
                float[]? mixBaseA = null;
                float[]? mixTopBrightness = null;
                float[]? mixBaseBrightness = null;

                int mixedCount = 0;
                try
                {
                    if (overlapPixels > 0)
                    {
                        mixedIndices = intPool.Rent(overlapPixels);
                        mixTopR = pool.Rent(overlapPixels);
                        mixTopG = pool.Rent(overlapPixels);
                        mixTopB = pool.Rent(overlapPixels);
                        mixBaseR = pool.Rent(overlapPixels);
                        mixBaseG = pool.Rent(overlapPixels);
                        mixBaseB = pool.Rent(overlapPixels);
                        mixTopA = pool.Rent(overlapPixels);
                        mixBaseA = pool.Rent(overlapPixels);
                        if (shouldComposeHdrBrightness)
                        {
                            mixTopBrightness = pool.Rent(overlapPixels);
                            mixBaseBrightness = pool.Rent(overlapPixels);
                        }
                    }

                    for (int y = overlapTop; y < overlapBottom; y++)
                    {
                        int topY = y - topStartY;
                        int topRow = topY * topPicture.Width;
                        int dstRow = y * targetWidth;
                        for (int x = overlapLeft; x < overlapRight; x++)
                        {
                            int topX = x - topStartX;
                            int topIdx = topRow + topX;
                            int dstIdx = dstRow + x;

                            float alpha = topA is null ? 1f : Clamp01(topA[topIdx]);
                            if (alpha <= 0.05f)
                            {
                                continue;
                            }

                            if (alpha >= 0.999f)
                            {
                                outR[dstIdx] = topR[topIdx];
                                outG[dstIdx] = topG[topIdx];
                                outB[dstIdx] = topB[topIdx];
                                outA[dstIdx] = 1f;
                                if (outBrightness != null)
                                {
                                    outBrightness[dstIdx] = topBrightness != null
                                        ? Clamp01(topBrightness[topIdx])
                                        : EstimateBrightness(topR[topIdx], topG[topIdx], topB[topIdx]);
                                }
                                continue;
                            }

                            mixedIndices![mixedCount] = dstIdx;
                            mixTopR![mixedCount] = topR[topIdx];
                            mixTopG![mixedCount] = topG[topIdx];
                            mixTopB![mixedCount] = topB[topIdx];
                            mixBaseR![mixedCount] = outR[dstIdx];
                            mixBaseG![mixedCount] = outG[dstIdx];
                            mixBaseB![mixedCount] = outB[dstIdx];
                            mixTopA![mixedCount] = alpha;
                            mixBaseA![mixedCount] = outA[dstIdx];

                            if (shouldComposeHdrBrightness)
                            {
                                mixTopBrightness![mixedCount] = topBrightness != null
                                    ? Clamp01(topBrightness[topIdx])
                                    : EstimateBrightness(topR[topIdx], topG[topIdx], topB[topIdx]);
                                mixBaseBrightness![mixedCount] = outBrightness![dstIdx];
                            }

                            mixedCount++;
                        }
                    }

                    if (mixedCount > 0)
                    {
                        object[] outRResult = computer.Compute([mixTopR!, mixBaseR!, mixTopA!, mixBaseA!, 16, mixedCount]);
                        object[] outGResult = computer.Compute([mixTopG!, mixBaseG!, mixTopA!, mixBaseA!, 16, mixedCount]);
                        object[] outBResult = computer.Compute([mixTopB!, mixBaseB!, mixTopA!, mixBaseA!, 16, mixedCount]);

                        for (int i = 0; i < mixedCount; i++)
                        {
                            int idx = mixedIndices![i];
                            outR[idx] = ReadAsFloat(outRResult[0], i);
                            outG[idx] = ReadAsFloat(outGResult[0], i);
                            outB[idx] = ReadAsFloat(outBResult[0], i);
                            outA[idx] = ReadAsAlpha01(outRResult[1], i);
                        }

                        if (shouldComposeHdrBrightness)
                        {
                            object[] brightnessResult = computer.Compute([mixTopBrightness!, mixBaseBrightness!, mixTopA!, mixBaseA!, 0, mixedCount]);
                            for (int i = 0; i < mixedCount; i++)
                            {
                                outBrightness![mixedIndices![i]] = ReadAsAlpha01(brightnessResult[0], i);
                            }
                        }
                    }
                }
                finally
                {
                    if (mixedIndices != null) intPool.Return(mixedIndices, clearArray: false);
                    if (mixTopR != null) pool.Return(mixTopR, clearArray: false);
                    if (mixTopG != null) pool.Return(mixTopG, clearArray: false);
                    if (mixTopB != null) pool.Return(mixTopB, clearArray: false);
                    if (mixBaseR != null) pool.Return(mixBaseR, clearArray: false);
                    if (mixBaseG != null) pool.Return(mixBaseG, clearArray: false);
                    if (mixBaseB != null) pool.Return(mixBaseB, clearArray: false);
                    if (mixTopA != null) pool.Return(mixTopA, clearArray: false);
                    if (mixBaseA != null) pool.Return(mixBaseA, clearArray: false);
                    if (mixTopBrightness != null) pool.Return(mixTopBrightness, clearArray: false);
                    if (mixBaseBrightness != null) pool.Return(mixBaseBrightness, clearArray: false);
                }

                float outputMaximumBrightness = DefaultHdrMaximumBrightness;
                if (shouldComposeHdrBrightness)
                {
                    outputMaximumBrightness = baseHasHdrBrightness && topHasHdrBrightness
                        ? Math.Max(baseMaximumBrightness, topMaximumBrightness)
                        : (baseHasHdrBrightness ? baseMaximumBrightness : topMaximumBrightness);
                }

                bool outputHasAlpha =
                    basePicture.hasAlphaChannel
                    || topPicture.hasAlphaChannel
                    || basePicture.Width != targetWidth
                    || basePicture.Height != targetHeight;

                IPicture result;
                if ((int)targetPPB == 8)
                {
                    result = new Picture8bpp(targetWidth, targetHeight)
                    {
                        r = ConvertToByteChannel(outR),
                        g = ConvertToByteChannel(outG),
                        b = ConvertToByteChannel(outB),
                        a = outputHasAlpha ? outA : null,
                        hasAlphaChannel = outputHasAlpha,
                        ProcessStack = new List<PictureProcessStack> { procStack },
                    };
                }
                else
                {
                    if (shouldComposeHdrBrightness)
                    {
                        result = new HDRPicture16bpp(targetWidth, targetHeight)
                        {
                            r = ConvertToUShortChannel(outR),
                            g = ConvertToUShortChannel(outG),
                            b = ConvertToUShortChannel(outB),
                            a = outputHasAlpha ? outA : null,
                            hasAlphaChannel = outputHasAlpha,
                            ProcessStack = new List<PictureProcessStack> { procStack },
                            Brightness = outBrightness ?? new float[targetPixels],
                            MaximumBrightness = outputMaximumBrightness,
                        };
                    }
                    else
                    {
                        result = new Picture16bpp(targetWidth, targetHeight)
                        {
                            r = ConvertToUShortChannel(outR),
                            g = ConvertToUShortChannel(outG),
                            b = ConvertToUShortChannel(outB),
                            a = outputHasAlpha ? outA : null,
                            hasAlphaChannel = outputHasAlpha,
                            ProcessStack = new List<PictureProcessStack> { procStack },
                        };
                    }
                }

                sw.Stop();
                procStack.Elapsed = sw.Elapsed;

#if DEBUG
                if (!string.IsNullOrWhiteSpace(IPicture.DiagImagePath))
                {
                    LogDiagnostic(
                        $"""
                        Overlay operation {id},
                        result:
                        {result.GetDiagnosticsInfo()}
                        """
                        );
                    result.SaveAsPng16bpp(Path.Combine(IPicture.DiagImagePath, $"_OverlayDiag-{id}-result.png"));
                }
#endif

                return result;
            }
            finally
            {
                try { resizedTop?.Dispose(); } catch { }
            }
        }
    }
}
