using projectFrameCut.Drawing.Processing.Resizing;
using projectFrameCut.Render.HwAccelContracts;
using projectFrameCut.Render.Plugin;
using projectFrameCut.Render.RenderAPIBase.EffectAndMixture;
using projectFrameCut.Shared;
using System;
using System.Buffers;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Runtime.CompilerServices;

namespace projectFrameCut.Render.Compose
{
    public class ClassicOverlayMixture : IMixture
    {
        public const string ComputerId = "OverlayComputer";
        public const string ApproximateComputerId = "ApproximateOverlayComputer";
        private const float DefaultHdrMaximumBrightness = 1000f;

        public static bool EnableApproximatePath { get; set; } = true;

        public static ClassicOverlayMixture Default { get; } = new();
        public string TypeName => "ClassicOverlayMixture";
        public string FromPlugin => InternalPluginBase.InternalPluginBaseID;
        public string? NeedComputer => EnableApproximatePath ? ApproximateComputerId : ComputerId;
        public bool IsReorderable => true;
        public string Name { get; set; }
        public string Id { get; set; }
        public Dictionary<string, object> Parameters { get; set; }
        public string? BindedEffectGroupID { get; set; }

        public IPicture Mix(IPicture basePicture, IPicture topPicture, IComputer? computer, IPicture.PicturePixelMode targetPPB)
            => MixInternal(basePicture, topPicture, computer, targetPPB, true, 0, 0, basePicture.Width, basePicture.Height);

        public IPicture Mix(
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

            return MixInternal(basePicture, topPicture, computer, targetPPB, false, topStartX, topStartY, targetWidth, targetHeight);
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

            IPicture? resizedTop = null;
            try
            {
                var sw = Stopwatch.StartNew();
                OverlayedPictureProcessStack procStack = new OverlayedPictureProcessStack
                {
                    BaseSteps = basePicture.ProcessStack,
                    TopSteps = topPicture.ProcessStack,
                    OperationDisplayName = "Overlay effect",
                    Operator = typeof(ClassicOverlayMixture),
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
                    throw new InvalidDataException("Pictures are invalid.");
                }

                bool baseHasHdrBrightness = TryGetHdrBrightness(basePicture, out float[]? baseBrightness, out float baseMaximumBrightness);
                bool topHasHdrBrightness = TryGetHdrBrightness(topPicture, out float[]? topBrightness, out float topMaximumBrightness);
                bool shouldComposeHdrBrightness = baseHasHdrBrightness || topHasHdrBrightness;
                int targetPixels = checked(targetWidth * targetHeight);

                bool outputHasAlpha =
                    basePicture.HasAlphaChannel
                    || topPicture.HasAlphaChannel
                    || basePicture.Width != targetWidth
                    || basePicture.Height != targetHeight;

                float outputMaximumBrightness = DefaultHdrMaximumBrightness;
                if (shouldComposeHdrBrightness)
                {
                    outputMaximumBrightness = baseHasHdrBrightness && topHasHdrBrightness
                        ? Math.Max(baseMaximumBrightness, topMaximumBrightness)
                        : (baseHasHdrBrightness ? baseMaximumBrightness : topMaximumBrightness);
                }

                IPicture result = (int)targetPPB == 8
                    ? Mix8bpp(
                        basePicture, topPicture, computer, topStartX, topStartY, targetWidth, targetHeight, targetPixels,
                        outputHasAlpha, procStack, baseBrightness, topBrightness)
                    : Mix16bpp(
                        basePicture, topPicture, computer, topStartX, topStartY, targetWidth, targetHeight, targetPixels,
                        outputHasAlpha, shouldComposeHdrBrightness, outputMaximumBrightness, procStack, baseBrightness, topBrightness);

                sw.Stop();
                procStack.Elapsed = sw.Elapsed;

                return result;
            }
            finally
            {
                try { resizedTop?.Dispose(); } catch { }
            }
        }

        private static IPicture Mix8bpp(
            IPicture basePicture,
            IPicture topPicture,
            IComputer computer,
            int topStartX,
            int topStartY,
            int targetWidth,
            int targetHeight,
            int targetPixels,
            bool outputHasAlpha,
            OverlayedPictureProcessStack procStack,
            float[]? baseBrightness,
            float[]? topBrightness)
        {
            var outR = new byte[targetPixels];
            var outG = new byte[targetPixels];
            var outB = new byte[targetPixels];
            var outA = new float[targetPixels];

            FillBaseLayer8(basePicture, targetWidth, outR, outG, outB, outA);

            int overlapLeft = Math.Max(0, topStartX);
            int overlapTop = Math.Max(0, topStartY);
            int overlapRight = Math.Min(targetWidth, topStartX + topPicture.Width);
            int overlapBottom = Math.Min(targetHeight, topStartY + topPicture.Height);

            if (EnableApproximatePath)
            {
                BlendApproximate8(basePicture, topPicture, computer, topStartX, topStartY, targetWidth, overlapLeft, overlapTop, overlapRight, overlapBottom, outR, outG, outB, outA);
            }
            else
            {
                BlendExact8(basePicture, topPicture, computer, topStartX, topStartY, targetWidth, overlapLeft, overlapTop, overlapRight, overlapBottom, outR, outG, outB, outA);
            }

            return new Picture8bpp(targetWidth, targetHeight)
            {
                r = outR,
                g = outG,
                b = outB,
                a = outputHasAlpha ? outA : null,
                HasAlphaChannel = outputHasAlpha,
                ProcessStack = new List<PictureProcessStack> { procStack },
            };
        }

        private static IPicture Mix16bpp(
            IPicture basePicture,
            IPicture topPicture,
            IComputer computer,
            int topStartX,
            int topStartY,
            int targetWidth,
            int targetHeight,
            int targetPixels,
            bool outputHasAlpha,
            bool shouldComposeHdrBrightness,
            float outputMaximumBrightness,
            OverlayedPictureProcessStack procStack,
            float[]? baseBrightness,
            float[]? topBrightness)
        {
            var outR = new ushort[targetPixels];
            var outG = new ushort[targetPixels];
            var outB = new ushort[targetPixels];
            var outA = new float[targetPixels];
            float[]? outBrightness = shouldComposeHdrBrightness ? new float[targetPixels] : null;

            FillBaseLayer16(basePicture, targetWidth, outR, outG, outB, outA, outBrightness, baseBrightness);

            int overlapLeft = Math.Max(0, topStartX);
            int overlapTop = Math.Max(0, topStartY);
            int overlapRight = Math.Min(targetWidth, topStartX + topPicture.Width);
            int overlapBottom = Math.Min(targetHeight, topStartY + topPicture.Height);

            if (EnableApproximatePath)
            {
                BlendApproximate16(basePicture, topPicture, topBrightness, computer, topStartX, topStartY, targetWidth, overlapLeft, overlapTop, overlapRight, overlapBottom, outR, outG, outB, outA, outBrightness);
            }
            else
            {
                BlendExact16(basePicture, topPicture, computer, topBrightness, topStartX, topStartY, targetWidth, overlapLeft, overlapTop, overlapRight, overlapBottom, outR, outG, outB, outA, outBrightness);
            }

            return new HDRPicture16bpp(targetWidth, targetHeight)
            {
                r = outR,
                g = outG,
                b = outB,
                a = outputHasAlpha ? outA : null,
                HasAlphaChannel = outputHasAlpha,
                ProcessStack = new List<PictureProcessStack> { procStack },
                Brightness = shouldComposeHdrBrightness ? outBrightness ?? new float[targetPixels] : new float[targetPixels],
                MaximumBrightness = outputMaximumBrightness,
            };
        }

        private static void FillBaseLayer8(IPicture basePicture, int targetWidth, byte[] outR, byte[] outG, byte[] outB, float[] outA)
        {
            if (basePicture is IPicture<ushort> p16)
            {
                for (int y = 0; y < basePicture.Height; y++)
                {
                    int srcRow = y * basePicture.Width;
                    int dstRow = y * targetWidth;

                    // SIMD downconvert 16bpp → 8bpp RGB (divide by 257) inline since it involves arithmetic
                    int w = basePicture.Width;
                    for (int x = 0; x < w; x++)
                    {
                        int srcIdx = srcRow + x;
                        int dstIdx = dstRow + x;
                        outR[dstIdx] = (byte)(p16.r[srcIdx] / 257);
                        outG[dstIdx] = (byte)(p16.g[srcIdx] / 257);
                        outB[dstIdx] = (byte)(p16.b[srcIdx] / 257);
                    }

                    if (p16.HasAlphaChannel && p16.a is not null)
                        SIMDAlphaProcessor.ClampAlphaOffset(p16.a, outA, dstRow, w);
                    else
                        SIMDAlphaProcessor.FillDefaultAlpha(outA, dstRow, w);
                }
                return;
            }

            if (basePicture is IPicture<byte> p8)
            {
                for (int y = 0; y < basePicture.Height; y++)
                {
                    int srcRow = y * basePicture.Width;
                    int dstRow = y * targetWidth;
                    int w = basePicture.Width;

                    SIMDAlphaProcessor.CopyBytesRgb(
                        p8.r, p8.g, p8.b,
                        outR, outG, outB,
                        srcRow, dstRow, w);

                    if (p8.HasAlphaChannel && p8.a is not null)
                        SIMDAlphaProcessor.ClampAlphaOffset(p8.a, outA, dstRow, w);
                    else
                        SIMDAlphaProcessor.FillDefaultAlpha(outA, dstRow, w);
                }
            }
        }

        private static void FillBaseLayer16(
            IPicture basePicture,
            int targetWidth,
            ushort[] outR,
            ushort[] outG,
            ushort[] outB,
            float[] outA,
            float[]? outBrightness,
            float[]? baseBrightness)
        {
            if (basePicture is IPicture<ushort> p16)
            {
                for (int y = 0; y < basePicture.Height; y++)
                {
                    int srcRow = y * basePicture.Width;
                    int dstRow = y * targetWidth;
                    int w = basePicture.Width;

                    SIMDAlphaProcessor.CopyUshortsRgb(
                        p16.r, p16.g, p16.b,
                        outR, outG, outB,
                        srcRow, dstRow, w);

                    if (p16.HasAlphaChannel && p16.a is not null)
                        SIMDAlphaProcessor.ClampAlphaOffset(p16.a, outA, dstRow, w);
                    else
                        SIMDAlphaProcessor.FillDefaultAlpha(outA, dstRow, w);

                    if (outBrightness != null)
                    {
                        if (baseBrightness != null)
                            SIMDAlphaProcessor.ClampAlpha(baseBrightness, outBrightness, w);
                        else
                            SIMDAlphaProcessor.EstimateBrightnessFromUshort(p16.r, p16.g, p16.b, outBrightness, w);
                    }
                }
                return;
            }

            if (basePicture is IPicture<byte> p8)
            {
                for (int y = 0; y < basePicture.Height; y++)
                {
                    int srcRow = y * basePicture.Width;
                    int dstRow = y * targetWidth;
                    int w = basePicture.Width;

                    SIMDAlphaProcessor.UpconvertBytesToUshortsRgb(
                        p8.r, p8.g, p8.b,
                        outR, outG, outB,
                        srcRow, dstRow, w);

                    if (p8.HasAlphaChannel && p8.a is not null)
                        SIMDAlphaProcessor.ClampAlphaOffset(p8.a, outA, dstRow, w);
                    else
                        SIMDAlphaProcessor.FillDefaultAlpha(outA, dstRow, w);

                    if (outBrightness != null)
                    {
                        if (baseBrightness != null)
                            SIMDAlphaProcessor.ClampAlpha(baseBrightness, outBrightness, w);
                        else
                            SIMDAlphaProcessor.EstimateBrightnessFromUshortOffset(
                                outR, outG, outB, dstRow, outBrightness, dstRow, w);
                    }
                }
            }
        }

        private static void BlendApproximate8(
            IPicture basePicture,
            IPicture topPicture,
            IComputer computer,
            int topStartX,
            int topStartY,
            int targetWidth,
            int overlapLeft,
            int overlapTop,
            int overlapRight,
            int overlapBottom,
            byte[] outR,
            byte[] outG,
            byte[] outB,
            float[] outA)
        {
            int overlapPixels = Math.Max(0, overlapRight - overlapLeft) * Math.Max(0, overlapBottom - overlapTop);
            var intPool = ArrayPool<int>.Shared;
            var floatPool = ArrayPool<float>.Shared;

            int[]? mixedIndices = null;
            float[]? mixTopR = null;
            float[]? mixTopG = null;
            float[]? mixTopB = null;
            float[]? mixBaseR = null;
            float[]? mixBaseG = null;
            float[]? mixBaseB = null;
            float[]? mixTopA = null;
            float[]? mixBaseA = null;

            int mixedCount = 0;
            try
            {
                if (overlapPixels > 0)
                {
                    mixedIndices = intPool.Rent(overlapPixels);
                    mixTopR = floatPool.Rent(overlapPixels);
                    mixTopG = floatPool.Rent(overlapPixels);
                    mixTopB = floatPool.Rent(overlapPixels);
                    mixBaseR = floatPool.Rent(overlapPixels);
                    mixBaseG = floatPool.Rent(overlapPixels);
                    mixBaseB = floatPool.Rent(overlapPixels);
                    mixTopA = floatPool.Rent(overlapPixels);
                    mixBaseA = floatPool.Rent(overlapPixels);
                }

                if (topPicture is IPicture<ushort> top16)
                {
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

                            float alpha = top16.a is null ? 1f : Clamp01(top16.a[topIdx]);
                            if (alpha <= 0.05f) continue;

                            if (alpha >= 0.999f)
                            {
                                outR[dstIdx] = (byte)(top16.r[topIdx] / 257f);
                                outG[dstIdx] = (byte)(top16.g[topIdx] / 257f);
                                outB[dstIdx] = (byte)(top16.b[topIdx] / 257f);
                                outA[dstIdx] = 1f;
                                continue;
                            }

                            mixedIndices[mixedCount] = dstIdx;
                            mixTopR[mixedCount] = top16.r[topIdx];
                            mixTopG[mixedCount] = top16.g[topIdx];
                            mixTopB[mixedCount] = top16.b[topIdx];
                            mixBaseR[mixedCount] = outR[dstIdx] * 257f;
                            mixBaseG[mixedCount] = outG[dstIdx] * 257f;
                            mixBaseB[mixedCount] = outB[dstIdx] * 257f;
                            mixTopA[mixedCount] = alpha;
                            mixBaseA[mixedCount] = outA[dstIdx];
                            mixedCount++;
                        }
                    }
                }
                else if (topPicture is IPicture<byte> top8)
                {
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

                            float alpha = top8.a is null ? 1f : Clamp01(top8.a[topIdx]);
                            if (alpha <= 0.05f) continue;

                            if (alpha >= 0.999f)
                            {
                                outR[dstIdx] = top8.r[topIdx];
                                outG[dstIdx] = top8.g[topIdx];
                                outB[dstIdx] = top8.b[topIdx];
                                outA[dstIdx] = 1f;
                                continue;
                            }

                            mixedIndices[mixedCount] = dstIdx;
                            mixTopR[mixedCount] = top8.r[topIdx] * 257f;
                            mixTopG[mixedCount] = top8.g[topIdx] * 257f;
                            mixTopB[mixedCount] = top8.b[topIdx] * 257f;
                            mixBaseR[mixedCount] = outR[dstIdx] * 257f;
                            mixBaseG[mixedCount] = outG[dstIdx] * 257f;
                            mixBaseB[mixedCount] = outB[dstIdx] * 257f;
                            mixTopA[mixedCount] = alpha;
                            mixBaseA[mixedCount] = outA[dstIdx];
                            mixedCount++;
                        }
                    }
                }

                if (mixedCount > 0)
                {
                    byte[] rOutArr, gOutArr, bOutArr;
                    float[] aOutArr;

                    if (computer is IOverlayComputer ovc8)
                    {
                        var rResult = ovc8.Overlay8(mixTopR!, mixBaseR!, mixTopA!, mixBaseA!, mixedCount);
                        var gResult = ovc8.Overlay8(mixTopG!, mixBaseG!, mixTopA!, mixBaseA!, mixedCount);
                        var bResult = ovc8.Overlay8(mixTopB!, mixBaseB!, mixTopA!, mixBaseA!, mixedCount);
                        rOutArr = rResult.Color; gOutArr = gResult.Color; bOutArr = bResult.Color;
                        aOutArr = rResult.Alpha;
                    }
                    else if (computer is IApproximateOverlayComputer aoc8)
                    {
                        var rResult = aoc8.ApproximateOverlay8(mixTopR!, mixBaseR!, mixTopA!, mixBaseA!, mixedCount);
                        var gResult = aoc8.ApproximateOverlay8(mixTopG!, mixBaseG!, mixTopA!, mixBaseA!, mixedCount);
                        var bResult = aoc8.ApproximateOverlay8(mixTopB!, mixBaseB!, mixTopA!, mixBaseA!, mixedCount);
                        rOutArr = rResult.Color; gOutArr = gResult.Color; bOutArr = bResult.Color;
                        aOutArr = rResult.Alpha;
                    }
                    else
                    {
                        object[] outRResult = computer.Compute([mixTopR!, mixBaseR!, mixTopA!, mixBaseA!, 8, mixedCount]);
                        object[] outGResult = computer.Compute([mixTopG!, mixBaseG!, mixTopA!, mixBaseA!, 8, mixedCount]);
                        object[] outBResult = computer.Compute([mixTopB!, mixBaseB!, mixTopA!, mixBaseA!, 8, mixedCount]);
                        rOutArr = (byte[])outRResult[0];
                        gOutArr = (byte[])outGResult[0];
                        bOutArr = (byte[])outBResult[0];
                        aOutArr = (float[])outRResult[1];
                    }

                    for (int i = 0; i < mixedCount; i++)
                    {
                        int idx = mixedIndices![i];
                        outR[idx] = rOutArr[i];
                        outG[idx] = gOutArr[i];
                        outB[idx] = bOutArr[i];
                        outA[idx] = aOutArr[i];
                    }
                }
            }
            finally
            {
                if (mixedIndices != null) intPool.Return(mixedIndices, clearArray: false);
                if (mixTopR != null) floatPool.Return(mixTopR, clearArray: false);
                if (mixTopG != null) floatPool.Return(mixTopG, clearArray: false);
                if (mixTopB != null) floatPool.Return(mixTopB, clearArray: false);
                if (mixBaseR != null) floatPool.Return(mixBaseR, clearArray: false);
                if (mixBaseG != null) floatPool.Return(mixBaseG, clearArray: false);
                if (mixBaseB != null) floatPool.Return(mixBaseB, clearArray: false);
                if (mixTopA != null) floatPool.Return(mixTopA, clearArray: false);
                if (mixBaseA != null) floatPool.Return(mixBaseA, clearArray: false);
            }
        }

        private static void BlendApproximate16(
            IPicture basePicture,
            IPicture topPicture,
            float[]? topBrightness,
            IComputer computer,
            int topStartX,
            int topStartY,
            int targetWidth,
            int overlapLeft,
            int overlapTop,
            int overlapRight,
            int overlapBottom,
            ushort[] outR,
            ushort[] outG,
            ushort[] outB,
            float[] outA,
            float[]? outBrightness)
        {
            int overlapPixels = Math.Max(0, overlapRight - overlapLeft) * Math.Max(0, overlapBottom - overlapTop);
            var intPool = ArrayPool<int>.Shared;
            var floatPool = ArrayPool<float>.Shared;

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
                    mixTopR = floatPool.Rent(overlapPixels);
                    mixTopG = floatPool.Rent(overlapPixels);
                    mixTopB = floatPool.Rent(overlapPixels);
                    mixBaseR = floatPool.Rent(overlapPixels);
                    mixBaseG = floatPool.Rent(overlapPixels);
                    mixBaseB = floatPool.Rent(overlapPixels);
                    mixTopA = floatPool.Rent(overlapPixels);
                    mixBaseA = floatPool.Rent(overlapPixels);
                    if (outBrightness != null)
                    {
                        mixTopBrightness = floatPool.Rent(overlapPixels);
                        mixBaseBrightness = floatPool.Rent(overlapPixels);
                    }
                }

                if (topPicture is IPicture<ushort> top16)
                {
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

                            float alpha = top16.a is null ? 1f : Clamp01(top16.a[topIdx]);
                            if (alpha <= 0.05f) continue;

                            if (alpha >= 0.999f)
                            {
                                outR[dstIdx] = top16.r[topIdx];
                                outG[dstIdx] = top16.g[topIdx];
                                outB[dstIdx] = top16.b[topIdx];
                                outA[dstIdx] = 1f;
                                if (outBrightness != null)
                                {
                                    outBrightness[dstIdx] = topBrightness != null
                                        ? Clamp01(topBrightness[topIdx])
                                        : EstimateBrightness(top16.r[topIdx], top16.g[topIdx], top16.b[topIdx]);
                                }
                                continue;
                            }

                            mixedIndices[mixedCount] = dstIdx;
                            mixTopR[mixedCount] = top16.r[topIdx];
                            mixTopG[mixedCount] = top16.g[topIdx];
                            mixTopB[mixedCount] = top16.b[topIdx];
                            mixBaseR[mixedCount] = outR[dstIdx];
                            mixBaseG[mixedCount] = outG[dstIdx];
                            mixBaseB[mixedCount] = outB[dstIdx];
                            mixTopA[mixedCount] = alpha;
                            mixBaseA[mixedCount] = outA[dstIdx];
                            if (outBrightness != null)
                            {
                                mixTopBrightness![mixedCount] = topBrightness != null
                                    ? Clamp01(topBrightness[topIdx])
                                    : EstimateBrightness(top16.r[topIdx], top16.g[topIdx], top16.b[topIdx]);
                                mixBaseBrightness![mixedCount] = outBrightness[dstIdx];
                            }
                            mixedCount++;
                        }
                    }
                }
                else if (topPicture is IPicture<byte> top8)
                {
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

                            float alpha = top8.a is null ? 1f : Clamp01(top8.a[topIdx]);
                            if (alpha <= 0.05f) continue;

                            if (alpha >= 0.999f)
                            {
                                outR[dstIdx] = (ushort)(top8.r[topIdx] * 257);
                                outG[dstIdx] = (ushort)(top8.g[topIdx] * 257);
                                outB[dstIdx] = (ushort)(top8.b[topIdx] * 257);
                                outA[dstIdx] = 1f;
                                if (outBrightness != null)
                                {
                                    outBrightness[dstIdx] = topBrightness != null
                                        ? Clamp01(topBrightness[topIdx])
                                        : EstimateBrightness(top8.r[topIdx] * 257f, top8.g[topIdx] * 257f, top8.b[topIdx] * 257f);
                                }
                                continue;
                            }

                            mixedIndices[mixedCount] = dstIdx;
                            mixTopR[mixedCount] = top8.r[topIdx] * 257f;
                            mixTopG[mixedCount] = top8.g[topIdx] * 257f;
                            mixTopB[mixedCount] = top8.b[topIdx] * 257f;
                            mixBaseR[mixedCount] = outR[dstIdx];
                            mixBaseG[mixedCount] = outG[dstIdx];
                            mixBaseB[mixedCount] = outB[dstIdx];
                            mixTopA[mixedCount] = alpha;
                            mixBaseA[mixedCount] = outA[dstIdx];
                            if (outBrightness != null)
                            {
                                mixTopBrightness![mixedCount] = topBrightness != null
                                    ? Clamp01(topBrightness[topIdx])
                                    : EstimateBrightness(mixTopR[mixedCount], mixTopG[mixedCount], mixTopB[mixedCount]);
                                mixBaseBrightness![mixedCount] = outBrightness[dstIdx];
                            }
                            mixedCount++;
                        }
                    }
                }

                if (mixedCount > 0)
                {
                    ushort[] rOutArr, gOutArr, bOutArr;
                    float[] aOutArr;

                    if (computer is IOverlayComputer ovc16)
                    {
                        var rResult = ovc16.Overlay16(mixTopR!, mixBaseR!, mixTopA!, mixBaseA!, mixedCount);
                        var gResult = ovc16.Overlay16(mixTopG!, mixBaseG!, mixTopA!, mixBaseA!, mixedCount);
                        var bResult = ovc16.Overlay16(mixTopB!, mixBaseB!, mixTopA!, mixBaseA!, mixedCount);
                        rOutArr = rResult.Color; gOutArr = gResult.Color; bOutArr = bResult.Color;
                        aOutArr = rResult.Alpha;
                    }
                    else if (computer is IApproximateOverlayComputer aoc16)
                    {
                        var rResult = aoc16.ApproximateOverlay16(mixTopR!, mixBaseR!, mixTopA!, mixBaseA!, mixedCount);
                        var gResult = aoc16.ApproximateOverlay16(mixTopG!, mixBaseG!, mixTopA!, mixBaseA!, mixedCount);
                        var bResult = aoc16.ApproximateOverlay16(mixTopB!, mixBaseB!, mixTopA!, mixBaseA!, mixedCount);
                        rOutArr = rResult.Color; gOutArr = gResult.Color; bOutArr = bResult.Color;
                        aOutArr = rResult.Alpha;
                    }
                    else
                    {
                        object[] outRResult = computer.Compute([mixTopR!, mixBaseR!, mixTopA!, mixBaseA!, 16, mixedCount]);
                        object[] outGResult = computer.Compute([mixTopG!, mixBaseG!, mixTopA!, mixBaseA!, 16, mixedCount]);
                        object[] outBResult = computer.Compute([mixTopB!, mixBaseB!, mixTopA!, mixBaseA!, 16, mixedCount]);
                        rOutArr = (ushort[])outRResult[0];
                        gOutArr = (ushort[])outGResult[0];
                        bOutArr = (ushort[])outBResult[0];
                        aOutArr = (float[])outRResult[1];
                    }

                    for (int i = 0; i < mixedCount; i++)
                    {
                        int idx = mixedIndices![i];
                        outR[idx] = rOutArr[i];
                        outG[idx] = gOutArr[i];
                        outB[idx] = bOutArr[i];
                        outA[idx] = aOutArr[i];
                    }

                    if (outBrightness != null)
                    {
                        float[] brightnessAlpha;
                        if (computer is IOverlayComputer ovcHdr)
                        {
                            var hdrResult = ovcHdr.OverlayHdr(mixTopBrightness!, mixBaseBrightness!, mixTopA!, mixBaseA!, mixedCount);
                            brightnessAlpha = hdrResult.Alpha;
                        }
                        else
                        {
                            object[] brightnessResult = computer.Compute([mixTopBrightness!, mixBaseBrightness!, mixTopA!, mixBaseA!, 0, mixedCount]);
                            brightnessAlpha = (float[])brightnessResult[0];
                        }
                        for (int i = 0; i < mixedCount; i++)
                        {
                            outBrightness[mixedIndices![i]] = brightnessAlpha[i];
                        }
                    }
                }
            }
            finally
            {
                if (mixedIndices != null) intPool.Return(mixedIndices, clearArray: false);
                if (mixTopR != null) floatPool.Return(mixTopR, clearArray: false);
                if (mixTopG != null) floatPool.Return(mixTopG, clearArray: false);
                if (mixTopB != null) floatPool.Return(mixTopB, clearArray: false);
                if (mixBaseR != null) floatPool.Return(mixBaseR, clearArray: false);
                if (mixBaseG != null) floatPool.Return(mixBaseG, clearArray: false);
                if (mixBaseB != null) floatPool.Return(mixBaseB, clearArray: false);
                if (mixTopA != null) floatPool.Return(mixTopA, clearArray: false);
                if (mixBaseA != null) floatPool.Return(mixBaseA, clearArray: false);
                if (mixTopBrightness != null) floatPool.Return(mixTopBrightness, clearArray: false);
                if (mixBaseBrightness != null) floatPool.Return(mixBaseBrightness, clearArray: false);
            }
        }

        private static void BlendExact8(
            IPicture basePicture,
            IPicture topPicture,
            IComputer computer,
            int topStartX,
            int topStartY,
            int targetWidth,
            int overlapLeft,
            int overlapTop,
            int overlapRight,
            int overlapBottom,
            byte[] outR,
            byte[] outG,
            byte[] outB,
            float[] outA)
        {
            int overlapPixels = Math.Max(0, overlapRight - overlapLeft) * Math.Max(0, overlapBottom - overlapTop);
            var intPool = ArrayPool<int>.Shared;
            var floatPool = ArrayPool<float>.Shared;

            int[]? mixedIndices = null;
            float[]? mixTopR = null;
            float[]? mixTopG = null;
            float[]? mixTopB = null;
            float[]? mixBaseR = null;
            float[]? mixBaseG = null;
            float[]? mixBaseB = null;
            float[]? mixTopA = null;
            float[]? mixBaseA = null;

            int mixedCount = 0;
            try
            {
                if (overlapPixels > 0)
                {
                    mixedIndices = intPool.Rent(overlapPixels);
                    mixTopR = floatPool.Rent(overlapPixels);
                    mixTopG = floatPool.Rent(overlapPixels);
                    mixTopB = floatPool.Rent(overlapPixels);
                    mixBaseR = floatPool.Rent(overlapPixels);
                    mixBaseG = floatPool.Rent(overlapPixels);
                    mixBaseB = floatPool.Rent(overlapPixels);
                    mixTopA = floatPool.Rent(overlapPixels);
                    mixBaseA = floatPool.Rent(overlapPixels);
                }

                if (topPicture is IPicture<ushort> top16)
                {
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
                            float alpha = top16.a is null ? 1f : Clamp01(top16.a[topIdx]);
                            if (alpha <= 0.05f) continue;

                            if (alpha >= 0.999f)
                            {
                                outR[dstIdx] = (byte)(top16.r[topIdx] / 257f);
                                outG[dstIdx] = (byte)(top16.g[topIdx] / 257f);
                                outB[dstIdx] = (byte)(top16.b[topIdx] / 257f);
                                outA[dstIdx] = 1f;
                                continue;
                            }

                            mixedIndices[mixedCount] = dstIdx;
                            mixTopR[mixedCount] = top16.r[topIdx];
                            mixTopG[mixedCount] = top16.g[topIdx];
                            mixTopB[mixedCount] = top16.b[topIdx];
                            mixBaseR[mixedCount] = outR[dstIdx] * 257f;
                            mixBaseG[mixedCount] = outG[dstIdx] * 257f;
                            mixBaseB[mixedCount] = outB[dstIdx] * 257f;
                            mixTopA[mixedCount] = alpha;
                            mixBaseA[mixedCount] = outA[dstIdx];
                            mixedCount++;
                        }
                    }
                }
                else if (topPicture is IPicture<byte> top8)
                {
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
                            float alpha = top8.a is null ? 1f : Clamp01(top8.a[topIdx]);
                            if (alpha <= 0.05f) continue;

                            if (alpha >= 0.999f)
                            {
                                outR[dstIdx] = top8.r[topIdx];
                                outG[dstIdx] = top8.g[topIdx];
                                outB[dstIdx] = top8.b[topIdx];
                                outA[dstIdx] = 1f;
                                continue;
                            }

                            mixedIndices[mixedCount] = dstIdx;
                            mixTopR[mixedCount] = top8.r[topIdx] * 257f;
                            mixTopG[mixedCount] = top8.g[topIdx] * 257f;
                            mixTopB[mixedCount] = top8.b[topIdx] * 257f;
                            mixBaseR[mixedCount] = outR[dstIdx] * 257f;
                            mixBaseG[mixedCount] = outG[dstIdx] * 257f;
                            mixBaseB[mixedCount] = outB[dstIdx] * 257f;
                            mixTopA[mixedCount] = alpha;
                            mixBaseA[mixedCount] = outA[dstIdx];
                            mixedCount++;
                        }
                    }
                }

                if (mixedCount > 0)
                {
                    byte[] rOutArr, gOutArr, bOutArr;
                    float[] aOutArr;

                    if (computer is IOverlayComputer ovc8)
                    {
                        var rResult = ovc8.Overlay8(mixTopR!, mixBaseR!, mixTopA!, mixBaseA!, mixedCount);
                        var gResult = ovc8.Overlay8(mixTopG!, mixBaseG!, mixTopA!, mixBaseA!, mixedCount);
                        var bResult = ovc8.Overlay8(mixTopB!, mixBaseB!, mixTopA!, mixBaseA!, mixedCount);
                        rOutArr = rResult.Color; gOutArr = gResult.Color; bOutArr = bResult.Color;
                        aOutArr = rResult.Alpha;
                    }
                    else if (computer is IApproximateOverlayComputer aoc8)
                    {
                        var rResult = aoc8.ApproximateOverlay8(mixTopR!, mixBaseR!, mixTopA!, mixBaseA!, mixedCount);
                        var gResult = aoc8.ApproximateOverlay8(mixTopG!, mixBaseG!, mixTopA!, mixBaseA!, mixedCount);
                        var bResult = aoc8.ApproximateOverlay8(mixTopB!, mixBaseB!, mixTopA!, mixBaseA!, mixedCount);
                        rOutArr = rResult.Color; gOutArr = gResult.Color; bOutArr = bResult.Color;
                        aOutArr = rResult.Alpha;
                    }
                    else
                    {
                        object[] outRResult = computer.Compute([mixTopR!, mixBaseR!, mixTopA!, mixBaseA!, 8, mixedCount]);
                        object[] outGResult = computer.Compute([mixTopG!, mixBaseG!, mixTopA!, mixBaseA!, 8, mixedCount]);
                        object[] outBResult = computer.Compute([mixTopB!, mixBaseB!, mixTopA!, mixBaseA!, 8, mixedCount]);
                        rOutArr = (byte[])outRResult[0];
                        gOutArr = (byte[])outGResult[0];
                        bOutArr = (byte[])outBResult[0];
                        aOutArr = (float[])outRResult[1];
                    }

                    for (int i = 0; i < mixedCount; i++)
                    {
                        int idx = mixedIndices![i];
                        outR[idx] = rOutArr[i];
                        outG[idx] = gOutArr[i];
                        outB[idx] = bOutArr[i];
                        outA[idx] = aOutArr[i];
                    }
                }
            }
            finally
            {
                if (mixedIndices != null) intPool.Return(mixedIndices, clearArray: false);
                if (mixTopR != null) floatPool.Return(mixTopR, clearArray: false);
                if (mixTopG != null) floatPool.Return(mixTopG, clearArray: false);
                if (mixTopB != null) floatPool.Return(mixTopB, clearArray: false);
                if (mixBaseR != null) floatPool.Return(mixBaseR, clearArray: false);
                if (mixBaseG != null) floatPool.Return(mixBaseG, clearArray: false);
                if (mixBaseB != null) floatPool.Return(mixBaseB, clearArray: false);
                if (mixTopA != null) floatPool.Return(mixTopA, clearArray: false);
                if (mixBaseA != null) floatPool.Return(mixBaseA, clearArray: false);
            }
        }

        private static void BlendExact16(
            IPicture basePicture,
            IPicture topPicture,
            IComputer computer,
            float[]? topBrightness,
            int topStartX,
            int topStartY,
            int targetWidth,
            int overlapLeft,
            int overlapTop,
            int overlapRight,
            int overlapBottom,
            ushort[] outR,
            ushort[] outG,
            ushort[] outB,
            float[] outA,
            float[]? outBrightness)
        {
            int overlapPixels = Math.Max(0, overlapRight - overlapLeft) * Math.Max(0, overlapBottom - overlapTop);
            var intPool = ArrayPool<int>.Shared;
            var floatPool = ArrayPool<float>.Shared;

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
                    mixTopR = floatPool.Rent(overlapPixels);
                    mixTopG = floatPool.Rent(overlapPixels);
                    mixTopB = floatPool.Rent(overlapPixels);
                    mixBaseR = floatPool.Rent(overlapPixels);
                    mixBaseG = floatPool.Rent(overlapPixels);
                    mixBaseB = floatPool.Rent(overlapPixels);
                    mixTopA = floatPool.Rent(overlapPixels);
                    mixBaseA = floatPool.Rent(overlapPixels);
                    if (outBrightness != null)
                    {
                        mixTopBrightness = floatPool.Rent(overlapPixels);
                        mixBaseBrightness = floatPool.Rent(overlapPixels);
                    }
                }

                if (topPicture is IPicture<ushort> top16)
                {
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
                            float alpha = top16.a is null ? 1f : Clamp01(top16.a[topIdx]);
                            if (alpha <= 0.05f) continue;

                            if (alpha >= 0.999f)
                            {
                                outR[dstIdx] = top16.r[topIdx];
                                outG[dstIdx] = top16.g[topIdx];
                                outB[dstIdx] = top16.b[topIdx];
                                outA[dstIdx] = 1f;
                                if (outBrightness != null)
                                {
                                    outBrightness[dstIdx] = topBrightness != null
                                        ? Clamp01(topBrightness[topIdx])
                                        : EstimateBrightness(top16.r[topIdx], top16.g[topIdx], top16.b[topIdx]);
                                }
                                continue;
                            }

                            mixedIndices[mixedCount] = dstIdx;
                            mixTopR[mixedCount] = top16.r[topIdx];
                            mixTopG[mixedCount] = top16.g[topIdx];
                            mixTopB[mixedCount] = top16.b[topIdx];
                            mixBaseR[mixedCount] = outR[dstIdx];
                            mixBaseG[mixedCount] = outG[dstIdx];
                            mixBaseB[mixedCount] = outB[dstIdx];
                            mixTopA[mixedCount] = alpha;
                            mixBaseA[mixedCount] = outA[dstIdx];
                            if (outBrightness != null)
                            {
                                mixTopBrightness![mixedCount] = topBrightness != null
                                    ? Clamp01(topBrightness[topIdx])
                                    : EstimateBrightness(top16.r[topIdx], top16.g[topIdx], top16.b[topIdx]);
                                mixBaseBrightness![mixedCount] = outBrightness[dstIdx];
                            }
                            mixedCount++;
                        }
                    }
                }
                else if (topPicture is IPicture<byte> top8)
                {
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
                            float alpha = top8.a is null ? 1f : Clamp01(top8.a[topIdx]);
                            if (alpha <= 0.05f) continue;

                            if (alpha >= 0.999f)
                            {
                                outR[dstIdx] = (ushort)(top8.r[topIdx] * 257);
                                outG[dstIdx] = (ushort)(top8.g[topIdx] * 257);
                                outB[dstIdx] = (ushort)(top8.b[topIdx] * 257);
                                outA[dstIdx] = 1f;
                                if (outBrightness != null)
                                {
                                    outBrightness[dstIdx] = topBrightness != null
                                        ? Clamp01(topBrightness[topIdx])
                                        : EstimateBrightness(top8.r[topIdx] * 257f, top8.g[topIdx] * 257f, top8.b[topIdx] * 257f);
                                }
                                continue;
                            }

                            mixedIndices[mixedCount] = dstIdx;
                            mixTopR[mixedCount] = top8.r[topIdx] * 257f;
                            mixTopG[mixedCount] = top8.g[topIdx] * 257f;
                            mixTopB[mixedCount] = top8.b[topIdx] * 257f;
                            mixBaseR[mixedCount] = outR[dstIdx];
                            mixBaseG[mixedCount] = outG[dstIdx];
                            mixBaseB[mixedCount] = outB[dstIdx];
                            mixTopA[mixedCount] = alpha;
                            mixBaseA[mixedCount] = outA[dstIdx];
                            if (outBrightness != null)
                            {
                                mixTopBrightness![mixedCount] = topBrightness != null
                                    ? Clamp01(topBrightness[topIdx])
                                    : EstimateBrightness(mixTopR[mixedCount], mixTopG[mixedCount], mixTopB[mixedCount]);
                                mixBaseBrightness![mixedCount] = outBrightness[dstIdx];
                            }
                            mixedCount++;
                        }
                    }
                }

                if (mixedCount > 0)
                {
                    ushort[] rOutArr, gOutArr, bOutArr;
                    float[] aOutArr;

                    if (computer is IOverlayComputer ovc16)
                    {
                        var rResult = ovc16.Overlay16(mixTopR!, mixBaseR!, mixTopA!, mixBaseA!, mixedCount);
                        var gResult = ovc16.Overlay16(mixTopG!, mixBaseG!, mixTopA!, mixBaseA!, mixedCount);
                        var bResult = ovc16.Overlay16(mixTopB!, mixBaseB!, mixTopA!, mixBaseA!, mixedCount);
                        rOutArr = rResult.Color; gOutArr = gResult.Color; bOutArr = bResult.Color;
                        aOutArr = rResult.Alpha;
                    }
                    else if (computer is IApproximateOverlayComputer aoc16)
                    {
                        var rResult = aoc16.ApproximateOverlay16(mixTopR!, mixBaseR!, mixTopA!, mixBaseA!, mixedCount);
                        var gResult = aoc16.ApproximateOverlay16(mixTopG!, mixBaseG!, mixTopA!, mixBaseA!, mixedCount);
                        var bResult = aoc16.ApproximateOverlay16(mixTopB!, mixBaseB!, mixTopA!, mixBaseA!, mixedCount);
                        rOutArr = rResult.Color; gOutArr = gResult.Color; bOutArr = bResult.Color;
                        aOutArr = rResult.Alpha;
                    }
                    else
                    {
                        object[] outRResult = computer.Compute([mixTopR!, mixBaseR!, mixTopA!, mixBaseA!, 16, mixedCount]);
                        object[] outGResult = computer.Compute([mixTopG!, mixBaseG!, mixTopA!, mixBaseA!, 16, mixedCount]);
                        object[] outBResult = computer.Compute([mixTopB!, mixBaseB!, mixTopA!, mixBaseA!, 16, mixedCount]);
                        rOutArr = (ushort[])outRResult[0];
                        gOutArr = (ushort[])outGResult[0];
                        bOutArr = (ushort[])outBResult[0];
                        aOutArr = (float[])outRResult[1];
                    }

                    for (int i = 0; i < mixedCount; i++)
                    {
                        int idx = mixedIndices![i];
                        outR[idx] = rOutArr[i];
                        outG[idx] = gOutArr[i];
                        outB[idx] = bOutArr[i];
                        outA[idx] = aOutArr[i];
                    }

                    if (outBrightness != null)
                    {
                        float[] brightnessAlpha;
                        if (computer is IOverlayComputer ovcHdr)
                        {
                            var hdrResult = ovcHdr.OverlayHdr(mixTopBrightness!, mixBaseBrightness!, mixTopA!, mixBaseA!, mixedCount);
                            brightnessAlpha = hdrResult.Alpha;
                        }
                        else
                        {
                            object[] brightnessResult = computer.Compute([mixTopBrightness!, mixBaseBrightness!, mixTopA!, mixBaseA!, 0, mixedCount]);
                            brightnessAlpha = (float[])brightnessResult[0];
                        }
                        for (int i = 0; i < mixedCount; i++)
                        {
                            outBrightness[mixedIndices![i]] = brightnessAlpha[i];
                        }
                    }
                }
            }
            finally
            {
                if (mixedIndices != null) intPool.Return(mixedIndices, clearArray: false);
                if (mixTopR != null) floatPool.Return(mixTopR, clearArray: false);
                if (mixTopG != null) floatPool.Return(mixTopG, clearArray: false);
                if (mixTopB != null) floatPool.Return(mixTopB, clearArray: false);
                if (mixBaseR != null) floatPool.Return(mixBaseR, clearArray: false);
                if (mixBaseG != null) floatPool.Return(mixBaseG, clearArray: false);
                if (mixBaseB != null) floatPool.Return(mixBaseB, clearArray: false);
                if (mixTopA != null) floatPool.Return(mixTopA, clearArray: false);
                if (mixBaseA != null) floatPool.Return(mixBaseA, clearArray: false);
                if (mixTopBrightness != null) floatPool.Return(mixTopBrightness, clearArray: false);
                if (mixBaseBrightness != null) floatPool.Return(mixBaseBrightness, clearArray: false);
            }
        }

        private static bool HasValidChannels(IPicture pic)
        {
            ObjectDisposedException.ThrowIf(pic.Disposed, pic);
            if (pic is Picture8bpp p8)
            {
                if (p8.r is null || p8.g is null || p8.b is null)
                {
                    Log("ClassicOverlayMixture: Invalid Picture8bpp channels (null)");
                    return false;
                }
                if (p8.r.Length != p8.Pixels || p8.g.Length != p8.Pixels || p8.b.Length != p8.Pixels)
                {
                    Log("ClassicOverlayMixture: Invalid Picture8bpp channels (length mismatch)");
                    return false;
                }
                if (p8.HasAlphaChannel && (p8.a is null || p8.a.Length != p8.Pixels))
                {
                    Log("ClassicOverlayMixture: Invalid Picture8bpp alpha channel (null or length mismatch)");
                    return false;
                }
                return true;
            }

            if (pic is Picture16bpp p16)
            {
                if (p16.r is null || p16.g is null || p16.b is null)
                {
                    Log("ClassicOverlayMixture: Invalid Picture16bpp channels (null)");
                    return false;
                }
                if (p16.r.Length != p16.Pixels || p16.g.Length != p16.Pixels || p16.b.Length != p16.Pixels)
                {
                    Log("ClassicOverlayMixture: Invalid Picture16bpp channels (length mismatch)");
                    return false;
                }
                if (p16.HasAlphaChannel && (p16.a is null || p16.a.Length != p16.Pixels))
                {
                    Log("ClassicOverlayMixture: Invalid Picture16bpp alpha channel (null or length mismatch)");
                    return false;
                }
                return true;
            }

            return true;
        }

        private static bool TryGetHdrBrightness(IPicture pic, out float[]? brightness, out float maximumBrightness)
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

        private static float NormalizeHdrMaximumBrightness(float value)
            => !float.IsFinite(value) || value <= 0f ? DefaultHdrMaximumBrightness : value;

        private static float Clamp01(float value)
            => Math.Clamp(value, 0f, 1f);

        private static float EstimateBrightness(float rr, float gg, float bb)
            => Clamp01((0.2627f * rr + 0.6780f * gg + 0.0593f * bb) / 65535f);

        private static float ReadAlpha01(object? src, int index)
        {
            if (src is float[] f) return Clamp01(f[index]);
            if (src is ushort[] u16) return Clamp01(u16[index] / 65535f);
            if (src is byte[] u8) return Clamp01(u8[index] / 255f);
            throw new InvalidOperationException("Invalid output alpha/brightness type");
        }

        public IEffect WithParameters(Dictionary<string, object> parameters) => new ClassicOverlayMixture { Parameters = parameters };
    }

    public class ClassicOverlayMixtureFactory : IEffectFactory
    {
        public string FromPlugin => InternalPluginBase.InternalPluginBaseID;
        public string TypeName => "ClassicOverlayMixture";
        public EffectTarget Target => EffectTarget.Mixture;
        public List<string> ParametersNeeded { get; } = ["AccuracyMode"];
        public Dictionary<string, string> ParametersType { get; } = new()
        {
            { "AccuracyMode", "string" }
        };
        public EffectImplementType[] SupportsImplementTypes => [EffectImplementType.NotSpecified];

        public IEffect Build(EffectImplementType implementType, Dictionary<string, object>? parameters = null)
        {
            return new ClassicOverlayMixture { };
        }
    }
}
