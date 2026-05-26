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
using static projectFrameCut.Shared.IPicture;
using Image = SixLabors.ImageSharp.Image;

namespace projectFrameCut.Shared
{
    public static class PictureExtensions
    {
        [DebuggerStepThrough()]
        [MethodImpl(MethodImplOptions.AggressiveOptimization)]
        public static IPicture DeepCopy(this IPicture source)
        {
            if (source is null) throw new ArgumentNullException(nameof(source));
            if (source.Disposed) throw new ObjectDisposedException(nameof(source));
            var sw = Stopwatch.StartNew();
            lock (source)
            {
                int width = source.Width;
                int height = source.Height;
                int pixels = source.Pixels;

                if (source.bitPerPixel == 16)
                {
                    // Prefer typed interface if available
                    if (source is IPicture<ushort> s16)
                    {
                        if (s16.r == null || s16.g == null || s16.b == null)
                            throw new InvalidOperationException("Source 16bpp picture has null channel buffers.");

                        var dst = new Picture16bpp(width, height)
                        {
                            frameIndex = s16.frameIndex,
                            filePath = s16.filePath,
                            hasAlphaChannel = s16.hasAlphaChannel
                        };

                        // ensure destination arrays exist
                        dst.r = new ushort[pixels];
                        dst.g = new ushort[pixels];
                        dst.b = new ushort[pixels];
                        Array.Copy(s16.r, dst.r, pixels);
                        Array.Copy(s16.g, dst.g, pixels);
                        Array.Copy(s16.b, dst.b, pixels);

                        if (s16.hasAlphaChannel && s16.a != null)
                        {
                            dst.a = new float[pixels];
                            Array.Copy(s16.a, dst.a, pixels);
                            dst.hasAlphaChannel = true;
                        }
                        else
                        {
                            dst.a = null;
                            dst.hasAlphaChannel = false;
                        }
                        dst.ProcessStack = s16.ProcessStack.Append(new PictureProcessStack
                        {
                            OperationDisplayName = "Deep copied",
                            Operator = typeof(PictureExtensions),
                            ProcessingFuncStackTrace = new StackTrace(true),
                            Elapsed = sw.Elapsed
                        }).ToList();
                        return dst;
                    }
                    else
                    {
                        // Fallback using GetSpecificChannel
                        var rr = source.GetSpecificChannel(IPicture.ChannelId.Red) as ushort[] ?? throw new InvalidOperationException("Red channel missing for 16bpp picture.");
                        var gg = source.GetSpecificChannel(IPicture.ChannelId.Green) as ushort[] ?? throw new InvalidOperationException("Green channel missing for 16bpp picture.");
                        var bb = source.GetSpecificChannel(IPicture.ChannelId.Blue) as ushort[] ?? throw new InvalidOperationException("Blue channel missing for 16bpp picture.");
                        var aa = source.hasAlphaChannel ? source.GetSpecificChannel(IPicture.ChannelId.Alpha) as float[] : null;

                        if (rr.Length != pixels || gg.Length != pixels || bb.Length != pixels || (aa != null && aa.Length != pixels))
                            throw new InvalidOperationException("Source channel buffer lengths do not match picture pixel count.");

                        var dst = new Picture16bpp(width, height)
                        {
                            frameIndex = source.frameIndex,
                            filePath = source.filePath,
                            hasAlphaChannel = source.hasAlphaChannel
                        };

                        dst.r = new ushort[pixels];
                        dst.g = new ushort[pixels];
                        dst.b = new ushort[pixels];
                        Array.Copy(rr, dst.r, pixels);
                        Array.Copy(gg, dst.g, pixels);
                        Array.Copy(bb, dst.b, pixels);

                        if (aa != null)
                        {
                            dst.a = new float[pixels];
                            Array.Copy(aa, dst.a, pixels);
                            dst.hasAlphaChannel = true;
                        }
                        else
                        {
                            dst.a = null;
                            dst.hasAlphaChannel = false;
                        }
                        dst.ProcessStack = source.ProcessStack.Append(new PictureProcessStack
                        {
                            OperationDisplayName = "Deep copied",
                            Operator = typeof(PictureExtensions),
                            ProcessingFuncStackTrace = new StackTrace(true),
                            Elapsed = sw.Elapsed
                        }).ToList();
                        return dst;
                    }
                }
                else if (source.bitPerPixel == 8)
                {
                    if (source is IPicture<byte> s8)
                    {
                        if (s8.r == null || s8.g == null || s8.b == null)
                            throw new InvalidOperationException("Source 8bpp picture has null channel buffers.");

                        var dst = new Picture8bpp(width, height)
                        {
                            frameIndex = s8.frameIndex,
                            filePath = s8.filePath,
                            ProcessStack = s8.ProcessStack.Append(new PictureProcessStack
                            {
                                OperationDisplayName = "Deep copied",
                                Operator = typeof(PictureExtensions),
                                ProcessingFuncStackTrace = new StackTrace(true),
                            }).ToList(),
                            hasAlphaChannel = s8.hasAlphaChannel
                        };

                        dst.r = new byte[pixels];
                        dst.g = new byte[pixels];
                        dst.b = new byte[pixels];
                        Array.Copy(s8.r, dst.r, pixels);
                        Array.Copy(s8.g, dst.g, pixels);
                        Array.Copy(s8.b, dst.b, pixels);

                        if (s8.hasAlphaChannel && s8.a != null)
                        {
                            dst.a = new float[pixels];
                            Array.Copy(s8.a, dst.a, pixels);
                            dst.hasAlphaChannel = true;
                        }
                        else
                        {
                            dst.a = null;
                            dst.hasAlphaChannel = false;
                        }
                        dst.ProcessStack = s8.ProcessStack.Append(new PictureProcessStack
                        {
                            OperationDisplayName = "Deep copied",
                            Operator = typeof(PictureExtensions),
                            ProcessingFuncStackTrace = new StackTrace(true),
                            Elapsed = sw.Elapsed
                        }).ToList();
                        return dst;
                    }
                    else
                    {
                        var rr = source.GetSpecificChannel(IPicture.ChannelId.Red) as byte[] ?? throw new InvalidOperationException("Red channel missing for 8bpp picture.");
                        var gg = source.GetSpecificChannel(IPicture.ChannelId.Green) as byte[] ?? throw new InvalidOperationException("Green channel missing for 8bpp picture.");
                        var bb = source.GetSpecificChannel(IPicture.ChannelId.Blue) as byte[] ?? throw new InvalidOperationException("Blue channel missing for 8bpp picture.");
                        var aa = source.hasAlphaChannel ? source.GetSpecificChannel(IPicture.ChannelId.Alpha) as float[] : null;

                        if (rr.Length != pixels || gg.Length != pixels || bb.Length != pixels || (aa != null && aa.Length != pixels))
                            throw new InvalidOperationException("Source channel buffer lengths do not match picture pixel count.");

                        var dst = new Picture8bpp(width, height)
                        {
                            frameIndex = source.frameIndex,
                            filePath = source.filePath,
                            ProcessStack = source.ProcessStack.Append(new PictureProcessStack
                            {
                                OperationDisplayName = "Deep copied",
                                Operator = typeof(PictureExtensions),
                                ProcessingFuncStackTrace = new StackTrace(true),
                            }).ToList(),
                            hasAlphaChannel = source.hasAlphaChannel
                        };

                        dst.r = new byte[pixels];
                        dst.g = new byte[pixels];
                        dst.b = new byte[pixels];
                        Array.Copy(rr, dst.r, pixels);
                        Array.Copy(gg, dst.g, pixels);
                        Array.Copy(bb, dst.b, pixels);

                        if (aa != null)
                        {
                            dst.a = new float[pixels];
                            Array.Copy(aa, dst.a, pixels);
                            dst.hasAlphaChannel = true;
                        }
                        else
                        {
                            dst.a = null;
                            dst.hasAlphaChannel = false;
                        }
                        dst.ProcessStack = source.ProcessStack.Append(new PictureProcessStack
                        {
                            OperationDisplayName = "Deep copied",
                            Operator = typeof(PictureExtensions),
                            ProcessingFuncStackTrace = new StackTrace(true),
                            Elapsed = sw.Elapsed
                        }).ToList();
                        return dst;
                    }
                }
                else
                {
                    throw new NotSupportedException("Only 8bpp and 16bpp images are supported for deep copy.");
                }
            }
        }


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
            imageEncoder ??= DefaultEncoder;
            image.SaveToSixLaborsImage(resultPPB, saveAlpha).Save(path, imageEncoder);
        }

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
                float[]? aa = image.hasAlphaChannel ? image.GetSpecificChannel(IPicture.ChannelId.Alpha) as float[] : null;
                bool alpha = saveAlpha ?? image.hasAlphaChannel && aa is not null;

                Image result;
                if (image.bitPerPixel == 16)
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
                else if (image.bitPerPixel == 8)
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
                float[]? aa = image.hasAlphaChannel ? image.GetSpecificChannel(IPicture.ChannelId.Alpha) as float[] : null;
                bool alpha = saveAlpha ?? image.hasAlphaChannel && aa is not null;

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
            return targetPPB switch
            {
                8 => new Picture8bpp(source),
                16 => new Picture16bpp(source),
                _ => throw new ArgumentOutOfRangeException(nameof(targetPPB), "Only 8bpp and 16bpp are supported."),
            };
        }

        [DebuggerStepThrough()]
        [MethodImpl(MethodImplOptions.AggressiveOptimization)]
        public static HDRPicture16bpp ToHDRPicture(this IPicture source, float brightness, int maximumBrightness = 5000)
        {
            var s = source.ToBitPerPixel(16) as IPicture<ushort>;
            if (s is null) throw new InvalidCastException($"Could not cast source {source.filePath}/{source.frameIndex} to IPicture<ushort>");
            float normalizedBrightness = float.IsFinite(brightness) ? Math.Clamp(brightness, 0f, 1f) : 1f;
            return new HDRPicture16bpp(s, false)
            {
                r = s.r,
                g = s.g,
                b = s.b,
                a = s.a,
                hasAlphaChannel = s.hasAlphaChannel && s.a is not null,
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
            if (s is null) throw new InvalidCastException($"Could not cast source {source.filePath}/{source.frameIndex} to IPicture<ushort>");

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
                hasAlphaChannel = s.hasAlphaChannel && s.a is not null,
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


        public static (int Width, int Height) GetDimensions(this IPicture picture) => (picture.Width, picture.Height);
        public static (int Width, int Height) GetDimensions(string picPath) => new Picture8bpp(picPath).GetDimensions();

        public static bool TryFromXYToArrayIndex(this IPicture reference, int x, int y, out int index)
            => TryFromXYToArrayIndex(x, y, reference.Width, reference.Height, out index);

        public static bool TryFromXYToArrayIndex(int x, int y, int width, int height, out int index)
        {
            if (x < 0 || x >= width || y < 0 || y >= height)
            {
                index = -1;
                return false;
            }
            index = y * width + x;
            return true;
        }

        public static Pixel<T> GetPixel<T>(this IPicture<T> source, int x, int y)
        {
            if (!TryFromXYToArrayIndex(x, y, source.Width, source.Height, out int idx))
            {
                if (x < 0 || x >= source.Width)
                    throw new ArgumentOutOfRangeException(nameof(x), "x is out of bounds.");
                if (y < 0 || y >= source.Height)
                    throw new ArgumentOutOfRangeException(nameof(y), "y is out of bounds.");
                throw new ArgumentOutOfRangeException("x or y", "x or y is out of bounds.");
            }
            return new Pixel<T>
            {
                r = source.r[idx],
                g = source.g[idx],
                b = source.b[idx],
                a = (source.a != null) ? source.a[idx] : 1f
            };
        }

        public struct Pixel<T>
        {
            public T r;
            public T g;
            public T b;
            public float a;
        }
    }

    internal static class PictureBufferUtilities
    {
        private static readonly Vector128<byte> Rgba32RMask = Vector128.Create((byte)0, 4, 8, 12, 0x80, 0x80, 0x80, 0x80, 0x80, 0x80, 0x80, 0x80, 0x80, 0x80, 0x80, 0x80);
        private static readonly Vector128<byte> Rgba32GMask = Vector128.Create((byte)1, 5, 9, 13, 0x80, 0x80, 0x80, 0x80, 0x80, 0x80, 0x80, 0x80, 0x80, 0x80, 0x80, 0x80);
        private static readonly Vector128<byte> Rgba32BMask = Vector128.Create((byte)2, 6, 10, 14, 0x80, 0x80, 0x80, 0x80, 0x80, 0x80, 0x80, 0x80, 0x80, 0x80, 0x80, 0x80);
        private static readonly Vector128<byte> Rgba32AMask = Vector128.Create((byte)3, 7, 11, 15, 0x80, 0x80, 0x80, 0x80, 0x80, 0x80, 0x80, 0x80, 0x80, 0x80, 0x80, 0x80);
        private static readonly Vector128<byte> Rgba64RMask = Vector128.Create((byte)0, 1, 8, 9, 0x80, 0x80, 0x80, 0x80, 0x80, 0x80, 0x80, 0x80, 0x80, 0x80, 0x80, 0x80);
        private static readonly Vector128<byte> Rgba64GMask = Vector128.Create((byte)2, 3, 10, 11, 0x80, 0x80, 0x80, 0x80, 0x80, 0x80, 0x80, 0x80, 0x80, 0x80, 0x80, 0x80);
        private static readonly Vector128<byte> Rgba64BMask = Vector128.Create((byte)4, 5, 12, 13, 0x80, 0x80, 0x80, 0x80, 0x80, 0x80, 0x80, 0x80, 0x80, 0x80, 0x80, 0x80);

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static T[] AllocateFilledArray<T>(int length, T value)
        {
            T[] array = GC.AllocateUninitializedArray<T>(length);
            array.AsSpan().Fill(value);
            return array;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static void ConvertUShortToByte(ReadOnlySpan<ushort> source, Span<byte> destination)
        {
            if (source.Length != destination.Length)
            {
                throw new ArgumentException("Source and destination lengths must match.");
            }

            for (int i = 0; i < source.Length; i++)
            {
                destination[i] = (byte)(source[i] / 257);
            }
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static void ConvertByteToUShort(ReadOnlySpan<byte> source, Span<ushort> destination)
        {
            if (source.Length != destination.Length)
            {
                throw new ArgumentException("Source and destination lengths must match.");
            }

            for (int i = 0; i < source.Length; i++)
            {
                destination[i] = (ushort)(source[i] * 257);
            }
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static void ConvertBoolMaskToBytes(ReadOnlySpan<bool> source, Span<byte> destination)
        {
            if (source.Length != destination.Length)
            {
                throw new ArgumentException("Source and destination lengths must match.");
            }

            for (int i = 0; i < source.Length; i++)
            {
                destination[i] = source[i] ? byte.MaxValue : byte.MinValue;
            }
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static void ClampBrightness(ReadOnlySpan<float> source, Span<float> destination, double offset)
        {
            if (source.Length != destination.Length)
            {
                throw new ArgumentException("Source and destination lengths must match.");
            }

            for (int i = 0; i < source.Length; i++)
            {
                destination[i] = (float)Math.Clamp(source[i] + offset, 0.0, 1.0);
            }
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static void CopyRgba32RowToByteChannels(ReadOnlySpan<Rgba32> row, Span<byte> r, Span<byte> g, Span<byte> b, Span<float> alpha, bool hasAlpha)
        {
            if (row.Length != r.Length || row.Length != g.Length || row.Length != b.Length || (hasAlpha && row.Length != alpha.Length))
            {
                throw new ArgumentException("Source and destination lengths must match.");
            }

            int i = 0;
            if (Ssse3.IsSupported)
            {
                ReadOnlySpan<byte> rowBytes = MemoryMarshal.AsBytes(row);
                ref byte srcBase = ref MemoryMarshal.GetReference(rowBytes);
                ref byte rBase = ref MemoryMarshal.GetReference(r);
                ref byte gBase = ref MemoryMarshal.GetReference(g);
                ref byte bBase = ref MemoryMarshal.GetReference(b);
                int simdLimit = row.Length & ~3;
                for (; i < simdLimit; i += 4)
                {
                    int byteOffset = i * 4;
                    Vector128<byte> src = Unsafe.ReadUnaligned<Vector128<byte>>(ref Unsafe.Add(ref srcBase, byteOffset));
                    uint packedR = Ssse3.Shuffle(src, Rgba32RMask).AsUInt32().GetElement(0);
                    uint packedG = Ssse3.Shuffle(src, Rgba32GMask).AsUInt32().GetElement(0);
                    uint packedB = Ssse3.Shuffle(src, Rgba32BMask).AsUInt32().GetElement(0);
                    Unsafe.WriteUnaligned(ref Unsafe.Add(ref rBase, i), packedR);
                    Unsafe.WriteUnaligned(ref Unsafe.Add(ref gBase, i), packedG);
                    Unsafe.WriteUnaligned(ref Unsafe.Add(ref bBase, i), packedB);

                    if (hasAlpha)
                    {
                        Vector128<byte> alphaVec = Ssse3.Shuffle(src, Rgba32AMask);
                        alpha[i] = alphaVec.GetElement(0) / 255f;
                        alpha[i + 1] = alphaVec.GetElement(1) / 255f;
                        alpha[i + 2] = alphaVec.GetElement(2) / 255f;
                        alpha[i + 3] = alphaVec.GetElement(3) / 255f;
                    }
                }
            }

            for (; i < row.Length; i++)
            {
                Rgba32 px = row[i];
                r[i] = px.R;
                g[i] = px.G;
                b[i] = px.B;
                if (hasAlpha)
                {
                    alpha[i] = px.A / 255f;
                }
            }
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static void CopyRgba32RowToUShortChannels(ReadOnlySpan<Rgba32> row, Span<ushort> r, Span<ushort> g, Span<ushort> b, float[]? a = null)
        {
            if (row.Length != r.Length || row.Length != g.Length || row.Length != b.Length || (a is not null && row.Length != a.Length))
            {
                throw new ArgumentException("Source and destination lengths must match.");
            }

            Span<float> alpha = a is null ? default : a.AsSpan();
            bool hasAlpha = a is not null;
            int i = 0;
            if (Ssse3.IsSupported)
            {
                ReadOnlySpan<byte> rowBytes = MemoryMarshal.AsBytes(row);
                ref byte srcBase = ref MemoryMarshal.GetReference(rowBytes);
                ref byte rBase = ref Unsafe.As<ushort, byte>(ref MemoryMarshal.GetReference(r));
                ref byte gBase = ref Unsafe.As<ushort, byte>(ref MemoryMarshal.GetReference(g));
                ref byte bBase = ref Unsafe.As<ushort, byte>(ref MemoryMarshal.GetReference(b));
                int simdLimit = row.Length & ~3;
                var scale = Vector128.Create((ushort)257);
                for (; i < simdLimit; i += 4)
                {
                    int byteOffset = i * 4;
                    Vector128<byte> src = Unsafe.ReadUnaligned<Vector128<byte>>(ref Unsafe.Add(ref srcBase, byteOffset));
                    Vector128<ushort> rWords = Sse2.UnpackLow(Ssse3.Shuffle(src, Rgba32RMask), Vector128<byte>.Zero).AsUInt16();
                    Vector128<ushort> gWords = Sse2.UnpackLow(Ssse3.Shuffle(src, Rgba32GMask), Vector128<byte>.Zero).AsUInt16();
                    Vector128<ushort> bWords = Sse2.UnpackLow(Ssse3.Shuffle(src, Rgba32BMask), Vector128<byte>.Zero).AsUInt16();
                    Vector128<ushort> scaledR = Sse2.MultiplyLow(rWords, scale);
                    Vector128<ushort> scaledG = Sse2.MultiplyLow(gWords, scale);
                    Vector128<ushort> scaledB = Sse2.MultiplyLow(bWords, scale);

                    Unsafe.WriteUnaligned(ref Unsafe.Add(ref rBase, i * 2), scaledR.AsUInt64().GetElement(0));
                    Unsafe.WriteUnaligned(ref Unsafe.Add(ref gBase, i * 2), scaledG.AsUInt64().GetElement(0));
                    Unsafe.WriteUnaligned(ref Unsafe.Add(ref bBase, i * 2), scaledB.AsUInt64().GetElement(0));

                    if (hasAlpha)
                    {
                        alpha[i] = row[i].A / 255f;
                        alpha[i + 1] = row[i + 1].A / 255f;
                        alpha[i + 2] = row[i + 2].A / 255f;
                        alpha[i + 3] = row[i + 3].A / 255f;
                    }
                }
            }

            for (; i < row.Length; i++)
            {
                Rgba32 px = row[i];
                r[i] = (ushort)(px.R * 257);
                g[i] = (ushort)(px.G * 257);
                b[i] = (ushort)(px.B * 257);
                if (hasAlpha)
                {
                    alpha[i] = px.A / 255f;
                }
            }
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static void CopyRgba64RowToUShortChannels(ReadOnlySpan<Rgba64> row, Span<ushort> r, Span<ushort> g, Span<ushort> b, Span<float> alpha, bool hasAlpha)
        {
            if (row.Length != r.Length || row.Length != g.Length || row.Length != b.Length || (hasAlpha && row.Length != alpha.Length))
            {
                throw new ArgumentException("Source and destination lengths must match.");
            }

            int i = 0;
            if (Ssse3.IsSupported)
            {
                ReadOnlySpan<byte> rowBytes = MemoryMarshal.AsBytes(row);
                ref byte srcBase = ref MemoryMarshal.GetReference(rowBytes);
                ref byte rBase = ref Unsafe.As<ushort, byte>(ref MemoryMarshal.GetReference(r));
                ref byte gBase = ref Unsafe.As<ushort, byte>(ref MemoryMarshal.GetReference(g));
                ref byte bBase = ref Unsafe.As<ushort, byte>(ref MemoryMarshal.GetReference(b));
                int simdLimit = row.Length & ~1;
                for (; i < simdLimit; i += 2)
                {
                    int byteOffset = i * 8;
                    Vector128<byte> src = Unsafe.ReadUnaligned<Vector128<byte>>(ref Unsafe.Add(ref srcBase, byteOffset));
                    uint packedR = Ssse3.Shuffle(src, Rgba64RMask).AsUInt32().GetElement(0);
                    uint packedG = Ssse3.Shuffle(src, Rgba64GMask).AsUInt32().GetElement(0);
                    uint packedB = Ssse3.Shuffle(src, Rgba64BMask).AsUInt32().GetElement(0);
                    Unsafe.WriteUnaligned(ref Unsafe.Add(ref rBase, i * 2), packedR);
                    Unsafe.WriteUnaligned(ref Unsafe.Add(ref gBase, i * 2), packedG);
                    Unsafe.WriteUnaligned(ref Unsafe.Add(ref bBase, i * 2), packedB);

                    if (hasAlpha)
                    {
                        alpha[i] = row[i].A / 65535f;
                        alpha[i + 1] = row[i + 1].A / 65535f;
                    }
                }
            }

            for (; i < row.Length; i++)
            {
                Rgba64 px = row[i];
                r[i] = px.R;
                g[i] = px.G;
                b[i] = px.B;
                if (hasAlpha)
                {
                    alpha[i] = px.A / 65535f;
                }
            }
        }
    }

    /// <summary>
    /// Read-only lifecycle snapshot of one picture instance.
    /// </summary>
    public readonly record struct PictureLifecycleSnapshot(
        long Id,
        string TypeName,
        int Width,
        int Height,
        DateTime CreatedAtUtc,
        DateTime? DisposedAtUtc,
        DateTime? CollectedAtUtc,
        bool IsDisposed,
        bool IsCollected,
        TimeSpan? LifetimeToDispose,
        TimeSpan? LifetimeToCollect,
        StackTrace CreateStack,
        StackTrace? DisposeStack,
        List<PictureProcessStack>? FinalProcessStack);

    /// <summary>
    /// Centralized lifecycle tracker for <see cref="IPicture"/> objects.
    /// </summary>
    public static class PictureLifecycleTracker
    {
        private sealed record PictureIdentity(long Id);

        private sealed record PictureLifecycleState(long Id, string TypeName, int Width, int Height, DateTime CreatedAtUtc, StackTrace CreateStack)
        {
            private long _disposedAtTicks;
            private long _collectedAtTicks;
            private StackTrace? DisposedStack;
            private List<PictureProcessStack>? FinalStack;

            public PictureLifecycleState(long id, IPicture picture) : this(id, picture.GetType().FullName ?? picture.GetType().Name, picture.Width, picture.Height, DateTime.UtcNow, new StackTrace(true))
            {
            }

            public void MarkDisposed(List<PictureProcessStack>? stack)
            {
                Interlocked.CompareExchange(ref _disposedAtTicks, DateTime.UtcNow.Ticks, 0);
                Interlocked.Exchange(ref DisposedStack, new StackTrace(true));
                Interlocked.Exchange(ref FinalStack, stack);
            }

            public void MarkCollected(List<PictureProcessStack>? stack)
            {
                Interlocked.CompareExchange(ref _collectedAtTicks, DateTime.UtcNow.Ticks, 0);
                if (stack != null)
                {
                    Interlocked.Exchange(ref FinalStack, stack);
                }
            }

            public PictureLifecycleSnapshot ToSnapshot()
            {
                long disposedTicks = Volatile.Read(ref _disposedAtTicks);
                long collectedTicks = Volatile.Read(ref _collectedAtTicks);
                DateTime? disposedAt = disposedTicks > 0 ? new DateTime(disposedTicks, DateTimeKind.Utc) : null;
                DateTime? collectedAt = collectedTicks > 0 ? new DateTime(collectedTicks, DateTimeKind.Utc) : null;

                return new PictureLifecycleSnapshot(
                    Id,
                    TypeName,
                    Width,
                    Height,
                    CreatedAtUtc,
                    disposedAt,
                    collectedAt,
                    disposedAt.HasValue,
                    collectedAt.HasValue,
                    disposedAt?.Subtract(CreatedAtUtc),
                    collectedAt?.Subtract(CreatedAtUtc),
                    CreateStack,
                    DisposedStack,
                    FinalStack);
            }
        }

        private sealed class FinalizationSentinel
        {
            private readonly long _id;
            private readonly WeakReference<IPicture> _picture;

            public FinalizationSentinel(long id, IPicture picture)
            {
                _id = id;
                _picture = new WeakReference<IPicture>(picture);
            }

            ~FinalizationSentinel()
            {
                if (_picture.TryGetTarget(out IPicture? picture))
                {
                    PictureLifecycleTracker.MarkCollected(_id, picture.ProcessStack);
                    return;
                }

                PictureLifecycleTracker.MarkCollected(_id, null);
            }
        }

        private static long _nextId;
        private static readonly ConcurrentDictionary<long, PictureLifecycleState> States = new();
        private static readonly ConditionalWeakTable<IPicture, PictureIdentity> Identities = new();
        private static readonly ConditionalWeakTable<IPicture, FinalizationSentinel> Sentinels = new();

        /// <summary>
        /// Enables tracking globally. Keep false in production unless diagnostics are needed.
        /// </summary>
        public static bool Enabled
        {
            get => Volatile.Read(ref field);
            set => Volatile.Write(ref field, value);
        }

        /// <summary>
        /// Track GC collection time using an extra finalizer sentinel per picture.
        /// Keep disabled when only creation/dispose duration is needed.
        /// </summary>
        public static bool TrackCollection { get; set; } = false;

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static void RegisterCreated(IPicture picture)
        {
            if (!Enabled) return;

            PictureIdentity identity = Identities.GetValue(picture, _ => new PictureIdentity(Interlocked.Increment(ref _nextId)));
            States.TryAdd(identity.Id, new PictureLifecycleState(identity.Id, picture));

            if (TrackCollection)
            {
                Sentinels.GetValue(picture, _ => new FinalizationSentinel(identity.Id, picture));
            }
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static void MarkDisposed(IPicture picture)
        {
            if (!Enabled) return;
            if (!Identities.TryGetValue(picture, out PictureIdentity? identity)) return;
            if (States.TryGetValue(identity.Id, out PictureLifecycleState? state))
            {
                state.MarkDisposed(picture.ProcessStack);
            }
        }

        public static IReadOnlyList<PictureLifecycleSnapshot> GetSnapshots(bool includeDisposed = true)
        {
            var snapshots = States.Values
                .Select(state => state.ToSnapshot())
                .Where(snapshot => includeDisposed || !snapshot.IsDisposed)
                .OrderBy(snapshot => snapshot.Id)
                .ToArray();
            return snapshots;
        }

        public static void Clear()
        {
            States.Clear();
        }

        private static void MarkCollected(long id, List<PictureProcessStack>? stack)
        {
            if (States.TryGetValue(id, out PictureLifecycleState? state))
            {
                state.MarkCollected(stack);
            }
        }

        public static async Task ExportPictureLifecycleTrackerSnapshots(string outputPath)
        {
            try
            {
                if (!PictureLifecycleTracker.Enabled)
                {
                    Logger.Log("PictureLifecycleTracker is disabled. Skipped lifecycle snapshot export.");
                    return;
                }

                var snapshots = PictureLifecycleTracker.GetSnapshots(includeDisposed: true);
                await using var stream = new FileStream(outputPath, FileMode.Create, FileAccess.Write, FileShare.ReadWrite);
                await using var writer = new StreamWriter(stream, new UTF8Encoding(encoderShouldEmitUTF8Identifier: true));

                await writer.WriteLineAsync(string.Join(',',
                [
                    "Id",
                    "TypeName",
                    "Width",
                    "Height",
                    "CreatedAtUtc",
                    "DisposedAtUtc",
                    "CollectedAtUtc",
                    "IsDisposed",
                    "IsCollected",
                    "LifetimeToDisposeMs",
                    "LifetimeToCollectMs",
                    "CreateStackTrace",
                    "DisposeStackTrace",
                    "FinalProcessStack"
                ]));

                foreach (var snapshot in snapshots)
                {
                    await writer.WriteLineAsync(string.Join(',',
                    [
                        EscapeCsv(snapshot.Id.ToString(CultureInfo.InvariantCulture)),
                        EscapeCsv(snapshot.TypeName),
                        EscapeCsv(snapshot.Width.ToString(CultureInfo.InvariantCulture)),
                        EscapeCsv(snapshot.Height.ToString(CultureInfo.InvariantCulture)),
                        EscapeCsv(snapshot.CreatedAtUtc.ToString("O", CultureInfo.InvariantCulture)),
                        EscapeCsv(snapshot.DisposedAtUtc?.ToString("O", CultureInfo.InvariantCulture)?? "N/A"),
                        EscapeCsv(snapshot.CollectedAtUtc?.ToString("O", CultureInfo.InvariantCulture)?? "N/A"),
                        EscapeCsv(snapshot.IsDisposed ? "true" : "false"),
                        EscapeCsv(snapshot.IsCollected ? "true" : "false"),
                        EscapeCsv(snapshot.LifetimeToDispose?.TotalMilliseconds.ToString(CultureInfo.InvariantCulture)),
                        EscapeCsv(snapshot.LifetimeToCollect?.TotalMilliseconds.ToString(CultureInfo.InvariantCulture)),
                        EscapeCsv(snapshot.CreateStack.ToString()),
                        EscapeCsv(snapshot.DisposeStack?.ToString() ?? "N/A"),
                        EscapeCsv(snapshot.FinalProcessStack is List<PictureProcessStack> p ? PictureProcessStack.FormatProcessStackForLog(p, 12): "N/A"),

                    ]));
                }

                await writer.FlushAsync();
                await stream.FlushAsync();
                await writer.DisposeAsync();
                await stream.DisposeAsync();
                Logger.Log($"Exported PictureLifecycleTracker snapshots: {snapshots.Count} records, {outputPath}");
            }
            catch (Exception ex)
            {
                Logger.Log(ex, "export PictureLifecycleTracker snapshots");
            }
        }

        private static string EscapeCsv(string? value)
        {
            if (string.IsNullOrEmpty(value))
            {
                return string.Empty;
            }

            if (value.Contains(',') || value.Contains('"') || value.Contains('\n') || value.Contains('\r'))
            {
                return $"\"{value.Replace("\"", "\"\"")}\"";
            }

            return value;
        }
    }
}
