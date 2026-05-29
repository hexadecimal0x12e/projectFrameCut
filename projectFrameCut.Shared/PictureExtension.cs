using projectFrameCut.Drawing.Base;
using projectFrameCut.Drawing.Base.Picture;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.Formats;
using SixLabors.ImageSharp.Formats.Png;
using SixLabors.ImageSharp.PixelFormats;
using SixLabors.ImageSharp.Processing;
using System;
using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Numerics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Runtime.Intrinsics;
using System.Runtime.Intrinsics.X86;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading.Channels;
using Image = SixLabors.ImageSharp.Image;

namespace projectFrameCut.Shared
{
    public static class PictureExtensions
    {

        [DebuggerStepThrough()]
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static void SaveAsPng16bpp(this IPicture image, string path, IImageEncoder? imageEncoder = null) //compatibility
            => SaveAsPng(image, path, 16, null, imageEncoder);

        [DebuggerStepThrough()]
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static void SaveAsPng8bpp(this IPicture image, string path, IImageEncoder? imageEncoder = null)
            => SaveAsPng(image, path, 8, null, imageEncoder);


        [DebuggerStepThrough()]
        public static void SaveAsPng(this IPicture image, string path, int resultPPB = 16, bool? saveAlpha = null, IImageEncoder? imageEncoder = null)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(path, nameof(path));
            using var stream = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.None);
            image.SaveAsPng(stream, resultPPB, saveAlpha, imageEncoder);
        }

        [DebuggerStepThrough()]
        public static void SaveAsPng(this IPicture image, Stream stream, int resultPPB = 16, bool? saveAlpha = null, IImageEncoder? imageEncoder = null)
        {
            ArgumentNullException.ThrowIfNull(image);
            ArgumentNullException.ThrowIfNull(stream);

            image.Save(stream, Drawing.Base.PictureExtensions.SharedPngPictureEncoder);
        }


        #region SixLabors

        [DebuggerStepThrough()]
        [MethodImpl(MethodImplOptions.AggressiveInlining | MethodImplOptions.AggressiveOptimization)]
        public static Image SaveToSixLaborsImage(this IPicture image, int resultPPB = 16, bool? saveAlpha = null, bool force = false) 
        {
            if (image is IHDRPicture<ushort> hdrImage)
            {
                return SaveToSixLaborsImage(hdrImage, resultPPB, saveAlpha, DefaultHDRImageDegradeToSDRMode);
            }
            lock (image)
            {
                float[]? aa = image.HasAlphaChannel ? image.GetSpecificChannel(IPicture.ChannelId.Alpha) as float[] : null;
                bool alpha = saveAlpha ?? image.HasAlphaChannel && aa is not null;

                Image result;
                if (image.BitPerPixel == 16)
                {
                    var rr = image.GetSpecificChannel(IPicture.ChannelId.Red) as ushort[];
                    var gg = image.GetSpecificChannel(IPicture.ChannelId.Green) as ushort[];
                    var bb = image.GetSpecificChannel(IPicture.ChannelId.Blue) as ushort[];
                    ArgumentNullException.ThrowIfNull(rr, nameof(IPicture<ushort>.r));
                    ArgumentNullException.ThrowIfNull(gg, nameof(IPicture<ushort>.g));
                    ArgumentNullException.ThrowIfNull(bb, nameof(IPicture<ushort>.b));
                    if (alpha)
                    {
                        var alphaArray = aa ?? Enumerable.Repeat(1f, image.Pixels).ToArray();
                        result = _SaveToInternal16bppWithAlpha(image, rr, gg, bb, alphaArray);
                    }
                    else
                    {
                        result = _SaveToInternal16bppWithNoAlpha(image, rr, gg, bb);
                    }
                }
                else if (image.BitPerPixel == 8)
                {
                    var rr = image.GetSpecificChannel(IPicture.ChannelId.Red) as byte[];
                    var gg = image.GetSpecificChannel(IPicture.ChannelId.Green) as byte[];
                    var bb = image.GetSpecificChannel(IPicture.ChannelId.Blue) as byte[];
                    ArgumentNullException.ThrowIfNull(rr, nameof(IPicture<byte>.r));
                    ArgumentNullException.ThrowIfNull(gg, nameof(IPicture<byte>.g));
                    ArgumentNullException.ThrowIfNull(bb, nameof(IPicture<byte>.b));
                    if (alpha)
                    {
                        var alphaArray = aa ?? Enumerable.Repeat(1f, image.Pixels).ToArray();
                        result = _SaveToInternal8bppWithAlpha(image, rr, gg, bb, alphaArray);
                    }
                    else
                    {
                        result = _SaveToInternal8bppWithNoAlpha(image, rr, gg, bb);
                    }
                }
                else
                {
                    throw new NotSupportedException("Only 8bpp and 16bpp images are supported.");
                }
                return result;
            }
        }


        [DebuggerStepThrough()]
        [MethodImpl(MethodImplOptions.AggressiveOptimization)]
        public static Image SaveToSixLaborsImage(this IHDRPicture<ushort> image, int resultPPB = 16, bool? saveAlpha = null, HDRImageDegradeToSDRMode? degradeToSDRMode = null)
        {
            var mode = degradeToSDRMode ??= DefaultHDRImageDegradeToSDRMode;
            lock (image)
            {
                float[]? aa = image.HasAlphaChannel ? image.GetSpecificChannel(IPicture.ChannelId.Alpha) as float[] : null;
                bool alpha = saveAlpha ?? image.HasAlphaChannel && aa is not null;

                Image result;
                if (alpha)
                {
                    var alphaArray = aa ?? Enumerable.Repeat(1f, image.Pixels).ToArray();
                    result = _SaveToInternalHDR16bppWithAlpha(image, image.r, image.g, image.b, alphaArray, image.Brightness, image.MaximumBrightness, mode);
                }
                else
                {
                    result = _SaveToInternalHDR16bppWithNoAlpha(image, image.r, image.g, image.b, image.Brightness, image.MaximumBrightness, mode);
                }
                return result;
            }
        }

        /// <summary>
        /// A shared instance of a <see cref="PngEncoder"/>.
        /// </summary>
        public static IImageEncoder DefaultEncoder = new PngEncoder()
        {
            BitDepth = PngBitDepth.Bit16
        };

        /// <summary>
        /// Determine the default action while degrading HDR images to SDR.
        /// </summary>
        public static HDRImageDegradeToSDRMode DefaultHDRImageDegradeToSDRMode = HDRImageDegradeToSDRMode.NormalizeBrightnessToRGB;

        private const float HdrSdrReferenceNits = 100f;
        private const float HdrToneMapKnee = 1.5f;
        private const float HdrOutputGamma = 2.2f;
        private const float HdrLumaEpsilon = 1e-6f;

        [DebuggerStepThrough()]
        [MethodImpl(MethodImplOptions.AggressiveOptimization)]
        private static Image _SaveToInternal16bppWithAlpha(IPicture image, ushort[] rr, ushort[] gg, ushort[] bb, ReadOnlySpan<float> aa)
        {
            var result = new Image<Rgba64>(image.Width, image.Height);
            int pixelCount = image.Pixels;
            if (result.DangerousTryGetSinglePixelMemory(out Memory<Rgba64> memory))
            {
                var span = memory.Span;
                for (int i = 0; i < pixelCount; i++)
                {
                    span[i] = new Rgba64(rr[i], gg[i], bb[i], (ushort)(Math.Clamp(aa[i], 0f, 1f) * 65535f));
                }
            }
            else
            {
                int x = 0, y = 0;
                int w = image.Width;
                for (int i = 0; i < pixelCount; i++)
                {
                    result[x, y] = new Rgba64(rr[i], gg[i], bb[i], (ushort)(Math.Clamp(aa[i], 0f, 1f) * 65535f));
                    if (++x == w) { x = 0; y++; }
                }
            }
            return result;
        }
        [DebuggerStepThrough()]
        [MethodImpl(MethodImplOptions.AggressiveOptimization)]
        private static Image _SaveToInternal16bppWithNoAlpha(IPicture image, ushort[] rr, ushort[] gg, ushort[] bb)
        {
            var result = new Image<Rgb48>(image.Width, image.Height);
            int pixelCount = image.Pixels;
            if (result.DangerousTryGetSinglePixelMemory(out Memory<Rgb48> memory))
            {
                var span = memory.Span;
                for (int i = 0; i < pixelCount; i++)
                {
                    span[i] = new Rgb48(rr[i], gg[i], bb[i]);
                }
            }
            else
            {
                int x = 0, y = 0;
                int w = image.Width;
                for (int i = 0; i < pixelCount; i++)
                {
                    result[x, y] = new Rgb48(rr[i], gg[i], bb[i]);
                    if (++x == w) { x = 0; y++; }
                }
            }
            return result;
        }

        [DebuggerStepThrough()]
        [MethodImpl(MethodImplOptions.AggressiveOptimization)]
        private static Image _SaveToInternalHDR16bppWithAlpha(IPicture image, ushort[] rr, ushort[] gg, ushort[] bb, ReadOnlySpan<float> aa, ReadOnlySpan<float> brightness, float maximumBrightness, HDRImageDegradeToSDRMode degradeMode)
        {
            var result = new Image<Rgba64>(image.Width, image.Height);
            int pixelCount = image.Pixels;
            int w = image.Width;

            if (result.DangerousTryGetSinglePixelMemory(out Memory<Rgba64> memory))
            {
                var span = memory.Span;
                switch (degradeMode)
                {
                    case HDRImageDegradeToSDRMode.NormalizeBrightnessToRGB:
                        for (int i = 0; i < pixelCount; i++)
                        {
                            _MapHDRSignalPixelToDisplaySignal(rr[i], gg[i], bb[i], brightness[i], maximumBrightness, out ushort mr, out ushort mg, out ushort mb);
                            span[i] = new Rgba64(mr, mg, mb, (ushort)(Math.Clamp(aa[i], 0f, 1f) * 65535f));
                        }
                        break;
                    case HDRImageDegradeToSDRMode.OverlayMaskFromBrightness:
                        for (int i = 0; i < pixelCount; i++)
                        {
                            float mask = Math.Clamp(brightness[i], 0f, 1f);
                            span[i] = new Rgba64(
                                (ushort)Math.Clamp((int)Math.Round(rr[i] * mask), 0, 65535),
                                (ushort)Math.Clamp((int)Math.Round(gg[i] * mask), 0, 65535),
                                (ushort)Math.Clamp((int)Math.Round(bb[i] * mask), 0, 65535),
                                (ushort)(Math.Clamp(aa[i], 0f, 1f) * 65535f));
                        }
                        break;
                    case HDRImageDegradeToSDRMode.DiscardBrightnessChannel:
                        for (int i = 0; i < pixelCount; i++)
                        {
                            span[i] = new Rgba64(rr[i], gg[i], bb[i], (ushort)(Math.Clamp(aa[i], 0f, 1f) * 65535f));
                        }
                        break;
                    case HDRImageDegradeToSDRMode.DisallowDowngrade:
                        throw new InvalidOperationException($"HDR to SDR degrade is disabled. Current mode: {nameof(HDRImageDegradeToSDRMode.DisallowDowngrade)}.");
                    default:
                        throw new ArgumentOutOfRangeException(nameof(DefaultHDRImageDegradeToSDRMode), DefaultHDRImageDegradeToSDRMode, "Unknown HDR degrade mode.");
                }
            }
            else
            {
                int x = 0, y = 0;
                for (int i = 0; i < pixelCount; i++)
                {
                    ushort mappedR = rr[i];
                    ushort mappedG = gg[i];
                    ushort mappedB = bb[i];
                    switch (degradeMode)
                    {
                        case HDRImageDegradeToSDRMode.NormalizeBrightnessToRGB:
                            _MapHDRSignalPixelToDisplaySignal(rr[i], gg[i], bb[i], brightness[i], maximumBrightness, out mappedR, out mappedG, out mappedB);
                            break;
                        case HDRImageDegradeToSDRMode.OverlayMaskFromBrightness:
                            float brightnessMask = Math.Clamp(brightness[i], 0f, 1f);
                            mappedR = (ushort)Math.Clamp((int)Math.Round(rr[i] * brightnessMask), 0, 65535);
                            mappedG = (ushort)Math.Clamp((int)Math.Round(gg[i] * brightnessMask), 0, 65535);
                            mappedB = (ushort)Math.Clamp((int)Math.Round(bb[i] * brightnessMask), 0, 65535);
                            break;
                        case HDRImageDegradeToSDRMode.DiscardBrightnessChannel:
                            break;
                        case HDRImageDegradeToSDRMode.DisallowDowngrade:
                            throw new InvalidOperationException($"HDR to SDR degrade is disabled. Current mode: {nameof(HDRImageDegradeToSDRMode.DisallowDowngrade)}.");
                        default:
                            throw new ArgumentOutOfRangeException(nameof(DefaultHDRImageDegradeToSDRMode), DefaultHDRImageDegradeToSDRMode, "Unknown HDR degrade mode.");
                    }
                    result[x, y] = new Rgba64(mappedR, mappedG, mappedB, (ushort)(Math.Clamp(aa[i], 0f, 1f) * 65535f));
                    if (++x == w) { x = 0; y++; }
                }
            }
            return result;
        }

        [DebuggerStepThrough()]
        [MethodImpl(MethodImplOptions.AggressiveOptimization)]
        private static Image _SaveToInternalHDR16bppWithNoAlpha(IPicture image, ushort[] rr, ushort[] gg, ushort[] bb, ReadOnlySpan<float> brightness, float maximumBrightness, HDRImageDegradeToSDRMode degradeMode)
        {
            var result = new Image<Rgb48>(image.Width, image.Height);
            int pixelCount = image.Pixels;
            int w = image.Width;

            if (result.DangerousTryGetSinglePixelMemory(out Memory<Rgb48> memory))
            {
                var span = memory.Span;
                switch (degradeMode)
                {
                    case HDRImageDegradeToSDRMode.NormalizeBrightnessToRGB:
                        for (int i = 0; i < pixelCount; i++)
                        {
                            _MapHDRSignalPixelToDisplaySignal(rr[i], gg[i], bb[i], brightness[i], maximumBrightness, out ushort mr, out ushort mg, out ushort mb);
                            span[i] = new Rgb48(mr, mg, mb);
                        }
                        break;
                    case HDRImageDegradeToSDRMode.OverlayMaskFromBrightness:
                        for (int i = 0; i < pixelCount; i++)
                        {
                            float mask = Math.Clamp(brightness[i], 0f, 1f);
                            span[i] = new Rgb48(
                                (ushort)Math.Clamp((int)Math.Round(rr[i] * mask), 0, 65535),
                                (ushort)Math.Clamp((int)Math.Round(gg[i] * mask), 0, 65535),
                                (ushort)Math.Clamp((int)Math.Round(bb[i] * mask), 0, 65535));
                        }
                        break;
                    case HDRImageDegradeToSDRMode.DiscardBrightnessChannel:
                        for (int i = 0; i < pixelCount; i++)
                        {
                            span[i] = new Rgb48(rr[i], gg[i], bb[i]);
                        }
                        break;
                    case HDRImageDegradeToSDRMode.DisallowDowngrade:
                        throw new InvalidOperationException($"HDR to SDR degrade is disabled. Mode: {degradeMode}(current)/{DefaultHDRImageDegradeToSDRMode}(global default).");
                    default:
                        throw new ArgumentOutOfRangeException(nameof(DefaultHDRImageDegradeToSDRMode), DefaultHDRImageDegradeToSDRMode, "Unknown HDR degrade mode.");
                }
            }
            else
            {
                int x = 0, y = 0;
                for (int i = 0; i < pixelCount; i++)
                {
                    ushort mappedR = rr[i];
                    ushort mappedG = gg[i];
                    ushort mappedB = bb[i];
                    switch (degradeMode)
                    {
                        case HDRImageDegradeToSDRMode.NormalizeBrightnessToRGB:
                            _MapHDRSignalPixelToDisplaySignal(rr[i], gg[i], bb[i], brightness[i], maximumBrightness, out mappedR, out mappedG, out mappedB);
                            break;
                        case HDRImageDegradeToSDRMode.OverlayMaskFromBrightness:
                            float brightnessMask = Math.Clamp(brightness[i], 0f, 1f);
                            mappedR = (ushort)Math.Clamp((int)Math.Round(rr[i] * brightnessMask), 0, 65535);
                            mappedG = (ushort)Math.Clamp((int)Math.Round(gg[i] * brightnessMask), 0, 65535);
                            mappedB = (ushort)Math.Clamp((int)Math.Round(bb[i] * brightnessMask), 0, 65535);
                            break;
                        case HDRImageDegradeToSDRMode.DiscardBrightnessChannel:
                            break;
                        case HDRImageDegradeToSDRMode.DisallowDowngrade:
                            throw new InvalidOperationException($"HDR to SDR degrade is disabled. Mode: {degradeMode}(current)/{DefaultHDRImageDegradeToSDRMode}(global default).");
                        default:
                            throw new ArgumentOutOfRangeException(nameof(DefaultHDRImageDegradeToSDRMode), DefaultHDRImageDegradeToSDRMode, "Unknown HDR degrade mode.");
                    }
                    result[x, y] = new Rgb48(mappedR, mappedG, mappedB);
                    if (++x == w) { x = 0; y++; }
                }
            }
            return result;
        }

        [DebuggerStepThrough()]
        [MethodImpl(MethodImplOptions.AggressiveInlining | MethodImplOptions.AggressiveOptimization)]
        private static void _MapHDRSignalPixelToDisplaySignal(ushort sourceR, ushort sourceG, ushort sourceB, float brightness, float maximumBrightness, out ushort mappedR, out ushort mappedG, out ushort mappedB)
        {
            if (!float.IsFinite(brightness))
            {
                mappedR = sourceR;
                mappedG = sourceG;
                mappedB = sourceB;
                return;
            }

            float r = sourceR / 65535f;
            float g = sourceG / 65535f;
            float b = sourceB / 65535f;
            float sourceSignalLuma = Math.Clamp(0.2627f * r + 0.6780f * g + 0.0593f * b, 0f, 1f);
            if (sourceSignalLuma <= HdrLumaEpsilon)
            {
                mappedR = sourceR;
                mappedG = sourceG;
                mappedB = sourceB;
                return;
            }

            float validMaximumBrightness = maximumBrightness > 0f && float.IsFinite(maximumBrightness)
                ? maximumBrightness
                : HdrSdrReferenceNits;

            // A lightweight SDR tone map driven by HDR brightness metadata.
            float relativeToSdrWhite = Math.Max(0f, brightness) * (validMaximumBrightness / HdrSdrReferenceNits);
            float toneMappedLinearLuma = (relativeToSdrWhite * HdrToneMapKnee) / (1f + relativeToSdrWhite * HdrToneMapKnee);
            toneMappedLinearLuma = Math.Clamp(toneMappedLinearLuma, 0f, 1f);
            float targetSignalLuma = MathF.Pow(toneMappedLinearLuma, 1f / HdrOutputGamma);
            float gain = targetSignalLuma / sourceSignalLuma;

            r = Math.Clamp(r * gain, 0f, 1f);
            g = Math.Clamp(g * gain, 0f, 1f);
            b = Math.Clamp(b * gain, 0f, 1f);

            mappedR = (ushort)Math.Clamp((int)Math.Round(r * 65535f), 0, 65535);
            mappedG = (ushort)Math.Clamp((int)Math.Round(g * 65535f), 0, 65535);
            mappedB = (ushort)Math.Clamp((int)Math.Round(b * 65535f), 0, 65535);
        }

        [DebuggerStepThrough()]
        [MethodImpl(MethodImplOptions.AggressiveOptimization)]
        private static Image _SaveToInternal8bppWithAlpha(IPicture image, byte[] rr, byte[] gg, byte[] bb, ReadOnlySpan<float> aa)
        {
            var result = new Image<Rgba32>(image.Width, image.Height);
            int pixelCount = image.Pixels;
            if (result.DangerousTryGetSinglePixelMemory(out Memory<Rgba32> memory))
            {
                var span = memory.Span;
                for (int i = 0; i < pixelCount; i++)
                {
                    span[i] = new Rgba32(rr[i], gg[i], bb[i], (byte)(Math.Clamp(aa[i], 0f, 1f) * 255f));
                }
            }
            else
            {
                int x = 0, y = 0;
                int w = image.Width;
                for (int i = 0; i < pixelCount; i++)
                {
                    result[x, y] = new Rgba32(rr[i], gg[i], bb[i], (byte)(Math.Clamp(aa[i], 0f, 1f) * 255f));
                    if (++x == w) { x = 0; y++; }
                }
            }
            return result;
        }
        [DebuggerStepThrough()]
        [MethodImpl(MethodImplOptions.AggressiveOptimization)]
        private static Image _SaveToInternal8bppWithNoAlpha(IPicture image, byte[] rr, byte[] gg, byte[] bb)
        {
            var result = new Image<Rgb24>(image.Width, image.Height);
            int pixelCount = image.Pixels;
            if (result.DangerousTryGetSinglePixelMemory(out Memory<Rgb24> memory))
            {
                var span = memory.Span;
                for (int i = 0; i < pixelCount; i++)
                {
                    span[i] = new Rgb24(rr[i], gg[i], bb[i]);
                }
            }
            else
            {
                int w = image.Width;
                result.ProcessPixelRows(accessor =>
                {
                    for (int y = 0; y < accessor.Height; y++)
                    {
                        var row = accessor.GetRowSpan(y);
                        int baseIdx = y * w;
                        for (int x = 0; x < w; x++)
                        {
                            int i = baseIdx + x;
                            row[x] = new Rgb24(rr[i], gg[i], bb[i]);
                        }
                    }
                });
            }
            return result;
        }

        [DebuggerStepThrough()]
        [MethodImpl(MethodImplOptions.AggressiveOptimization)]
        public static IPicture ToPJFCPicture(this Image source, int targetPPB)
        {
            ArgumentNullException.ThrowIfNull(source);
            return targetPPB switch
            {
                8 => _ToPicture8bpp(source),
                16 => _ToPicture16bpp(source),
                _ => throw new ArgumentOutOfRangeException(nameof(targetPPB), "Only 8bpp and 16bpp are supported."),
            };
        }

        public static IPicture ModifiyProcessStack(this IPicture picture, List<PictureProcessStack> stack)
        {
            ArgumentNullException.ThrowIfNull(picture);
            lock (picture)
            {
                picture.ProcessStack = stack;
                return picture;
            }
        }

        [DebuggerStepThrough()]
        [MethodImpl(MethodImplOptions.AggressiveOptimization)]
        private static Picture8bpp _ToPicture8bpp(Image source)
        {
            using var image = source.CloneAs<Rgba32>();
            var result = new Picture8bpp(image.Width, image.Height);
            var alpha = new float[result.Pixels];
            bool hasAlpha = false;

            if (image.DangerousTryGetSinglePixelMemory(out Memory<Rgba32> memory))
            {
                var span = memory.Span;
                for (int i = 0; i < span.Length; i++)
                {
                    Rgba32 px = span[i];
                    result.r[i] = px.R;
                    result.g[i] = px.G;
                    result.b[i] = px.B;
                    float a = px.A / 255f;
                    alpha[i] = a;
                    hasAlpha |= px.A < 255;
                }
            }
            else
            {
                int width = image.Width;
                int idx = 0;
                image.ProcessPixelRows(accessor =>
                {
                    for (int y = 0; y < accessor.Height; y++)
                    {
                        var row = accessor.GetRowSpan(y);
                        for (int x = 0; x < width; x++)
                        {
                            Rgba32 px = row[x];
                            result.r[idx] = px.R;
                            result.g[idx] = px.G;
                            result.b[idx] = px.B;
                            float a = px.A / 255f;
                            alpha[idx] = a;
                            hasAlpha |= px.A < 255;
                            idx++;
                        }
                    }
                });
            }

            result.HasAlphaChannel = hasAlpha;
            result.a = hasAlpha ? alpha : null;
            return result;
        }

        [DebuggerStepThrough()]
        [MethodImpl(MethodImplOptions.AggressiveOptimization)]
        private static Picture16bpp _ToPicture16bpp(Image source)
        {
            using var image = source.CloneAs<Rgba64>();
            var result = new Picture16bpp(image.Width, image.Height);
            var alpha = new float[result.Pixels];
            bool hasAlpha = false;

            if (image.DangerousTryGetSinglePixelMemory(out Memory<Rgba64> memory))
            {
                var span = memory.Span;
                for (int i = 0; i < span.Length; i++)
                {
                    Rgba64 px = span[i];
                    result.r[i] = px.R;
                    result.g[i] = px.G;
                    result.b[i] = px.B;
                    float a = px.A / 65535f;
                    alpha[i] = a;
                    hasAlpha |= px.A < 65535;
                }
            }
            else
            {
                int width = image.Width;
                int idx = 0;
                image.ProcessPixelRows(accessor =>
                {
                    for (int y = 0; y < accessor.Height; y++)
                    {
                        var row = accessor.GetRowSpan(y);
                        for (int x = 0; x < width; x++)
                        {
                            Rgba64 px = row[x];
                            result.r[idx] = px.R;
                            result.g[idx] = px.G;
                            result.b[idx] = px.B;
                            float a = px.A / 65535f;
                            alpha[idx] = a;
                            hasAlpha |= px.A < 65535;
                            idx++;
                        }
                    }
                });
            }

            result.HasAlphaChannel = hasAlpha;
            result.a = hasAlpha ? alpha : null;
            return result;
        }

        [DebuggerStepThrough()]
        [MethodImpl(MethodImplOptions.AggressiveOptimization)]
        public static HDRPicture16bpp ToHDRPicture(this IPicture source, float brightness, int maximumBrightness = 5000)
        {
            var s = source.ToBitPerPixel(16) as IPicture<ushort>;
            if (s is null) throw new InvalidCastException($"Could not cast source {source.Tag} to IPicture<ushort>");
            float normalizedBrightness = float.IsFinite(brightness) ? Math.Clamp(brightness, 0f, 1f) : 1f;
            return new HDRPicture16bpp(s, false)
            {
                r = s.r,
                g = s.g,
                b = s.b,
                a = s.a,
                HasAlphaChannel = s.HasAlphaChannel && s.a is not null,
                Brightness = Enumerable.Repeat(normalizedBrightness, s.Pixels).ToArray(),
                MaximumBrightness = maximumBrightness,
                ProcessStack = source.ProcessStack.Append(new PictureProcessStack
                {
                    OperationDisplayName = $"Converted to HDR with brightness {brightness} and max brightness {maximumBrightness}",
                    Operator = typeof(PictureExtensions),
                    ProcessingFuncStackTrace = new StackTrace(true),
                }).ToList()
            };
        }

        [DebuggerStepThrough()]
        [MethodImpl(MethodImplOptions.AggressiveOptimization)]
        public static HDRPicture16bpp ToHDRPictureBySignal(this IPicture source, int maximumBrightness = 203)
        {
            var s = source.ToBitPerPixel(16) as IPicture<ushort>;
            if (s is null) throw new InvalidCastException($"Could not cast source {source.Tag} to IPicture<ushort>");

            int validMaximumBrightness = Math.Clamp(maximumBrightness, 100, 10000);
            var brightness = new float[s.Pixels];

            for (int i = 0; i < s.Pixels; i++)
            {
                float r = s.r[i] / 65535f;
                float g = s.g[i] / 65535f;
                float b = s.b[i] / 65535f;
                float luma = 0.2627f * r + 0.6780f * g + 0.0593f * b;
                brightness[i] = float.IsFinite(luma) ? Math.Clamp(luma, 0f, 1f) : 0f;
            }

            return new HDRPicture16bpp(s, false)
            {
                r = s.r,
                g = s.g,
                b = s.b,
                a = s.a,
                HasAlphaChannel = s.HasAlphaChannel && s.a is not null,
                Brightness = brightness,
                MaximumBrightness = validMaximumBrightness,
                ProcessStack = source.ProcessStack.Append(new PictureProcessStack
                {
                    OperationDisplayName = $"Converted to HDR using signal-derived brightness and max brightness {validMaximumBrightness}",
                    Operator = typeof(PictureExtensions),
                    ProcessingFuncStackTrace = new StackTrace(true),
                }).ToList()
            };
        }

        #endregion

    }

   
}
