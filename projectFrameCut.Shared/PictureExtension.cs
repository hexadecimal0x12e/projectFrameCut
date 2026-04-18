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
        public static void SaveAsPng16bpp(this IPicture image, string path, IImageEncoder? imageEncoder = null) //compatibility
            => SaveAsPng(image, path, 16, null, imageEncoder);

        [DebuggerStepThrough()]
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
        public static Image SaveToSixLaborsImage(this IPicture image, int resultPPB = 16, bool? saveAlpha = null, bool force = false)
        {
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

                    if (!force && image is IHDRPicture<ushort> hdrImage
                        && hdrImage.Brightness != null
                        && hdrImage.Brightness.Length == image.Pixels)
                    {
                        if (alpha)
                        {
                            var alphaArray = aa ?? Enumerable.Repeat(1f, image.Pixels).ToArray();
                            result = _SaveToInternalHDR16bppWithAlpha(image, rr, gg, bb, alphaArray, hdrImage.Brightness, hdrImage.MaximumBrightness);
                        }
                        else
                        {
                            result = _SaveToInternalHDR16bppWithNoAlpha(image, rr, gg, bb, hdrImage.Brightness, hdrImage.MaximumBrightness);
                        }
                    }
                    else
                    {
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

        private static IImageEncoder DefaultEncoder = new PngEncoder()
        {
            BitDepth = PngBitDepth.Bit16
        };

        private const float HdrSdrReferenceNits = 100f;
        private const float HdrToneMapKnee = 1.5f;
        private const float HdrOutputGamma = 2.2f;
        private const float HdrLumaEpsilon = 1e-6f;

        [DebuggerStepThrough()]
        private static Image _SaveToInternal16bppWithAlpha(IPicture image, ushort[] rr, ushort[] gg, ushort[] bb, ReadOnlySpan<float> aa)
        {
            var result = new Image<Rgba64>(image.Width, image.Height);
            int x = 0, y = 0;
            for (int i = 0; i < image.Pixels; i++)
            {
                result[x, y] = new Rgba64
                {
                    R = rr[i],
                    G = gg[i],
                    B = bb[i],
                    A = (ushort)(Math.Clamp(aa[i], 0f, 1f) * 65535f)
                };
                if (x == image.Width - 1)
                {
                    x = 0;
                    y++;
                }
                else
                {
                    x++;
                }
            }
            return result;
        }
        [DebuggerStepThrough()]
        private static Image _SaveToInternal16bppWithNoAlpha(IPicture image, ushort[] rr, ushort[] gg, ushort[] bb)
        {
            var result = new Image<Rgb48>(image.Width, image.Height);
            int x = 0, y = 0;
            for (int i = 0; i < image.Pixels; i++)
            {
                result[x, y] = new Rgb48
                {
                    R = rr[i],
                    G = gg[i],
                    B = bb[i],
                };
                if (x == image.Width - 1)
                {
                    x = 0;
                    y++;
                }
                else
                {
                    x++;
                }
            }
            return result;
        }

        [DebuggerStepThrough()]
        private static Image _SaveToInternalHDR16bppWithAlpha(IPicture image, ushort[] rr, ushort[] gg, ushort[] bb, ReadOnlySpan<float> aa, ReadOnlySpan<float> brightness, float maximumBrightness)
        {
            var result = new Image<Rgba64>(image.Width, image.Height);
            int x = 0, y = 0;
            for (int i = 0; i < image.Pixels; i++)
            {
                _MapHDRSignalPixelToDisplaySignal(rr[i], gg[i], bb[i], brightness[i], maximumBrightness, out ushort mappedR, out ushort mappedG, out ushort mappedB);
                result[x, y] = new Rgba64
                {
                    R = mappedR,
                    G = mappedG,
                    B = mappedB,
                    A = (ushort)(Math.Clamp(aa[i], 0f, 1f) * 65535f)
                };

                if (x == image.Width - 1)
                {
                    x = 0;
                    y++;
                }
                else
                {
                    x++;
                }
            }
            return result;
        }

        [DebuggerStepThrough()]
        private static Image _SaveToInternalHDR16bppWithNoAlpha(IPicture image, ushort[] rr, ushort[] gg, ushort[] bb, ReadOnlySpan<float> brightness, float maximumBrightness)
        {
            var result = new Image<Rgb48>(image.Width, image.Height);
            int x = 0, y = 0;
            for (int i = 0; i < image.Pixels; i++)
            {
                _MapHDRSignalPixelToDisplaySignal(rr[i], gg[i], bb[i], brightness[i], maximumBrightness, out ushort mappedR, out ushort mappedG, out ushort mappedB);
                result[x, y] = new Rgb48
                {
                    R = mappedR,
                    G = mappedG,
                    B = mappedB,
                };

                if (x == image.Width - 1)
                {
                    x = 0;
                    y++;
                }
                else
                {
                    x++;
                }
            }
            return result;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
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
        private static Image _SaveToInternal8bppWithAlpha(IPicture image, byte[] rr, byte[] gg, byte[] bb, ReadOnlySpan<float> aa)
        {
            var result = new Image<Rgba32>(image.Width, image.Height);
            int x = 0, y = 0;
            for (int i = 0; i < image.Pixels; i++)
            {
                result[x, y] = new Rgba32
                {
                    R = rr[i],
                    G = gg[i],
                    B = bb[i],
                    A = (byte)(Math.Clamp(aa[i], 0f, 1f) * 255f)
                };
                if (x == image.Width - 1)
                {
                    x = 0;
                    y++;
                }
                else
                {
                    x++;
                }
            }
            return result;
        }
        [DebuggerStepThrough()]
        private static Image _SaveToInternal8bppWithNoAlpha(IPicture image, byte[] rr, byte[] gg, byte[] bb)
        {
            var result = new Image<Rgb24>(image.Width, image.Height);
            int x = 0, y = 0;
            for (int i = 0; i < image.Pixels; i++)
            {
                result[x, y] = new Rgb24
                {
                    R = rr[i],
                    G = gg[i],
                    B = bb[i],
                };
                if (x == image.Width - 1)
                {
                    x = 0;
                    y++;
                }
                else
                {
                    x++;
                }
            }
            return result;
        }

        [DebuggerStepThrough()]
        public static IPicture ToPJFCPicture(this Image source, int targetPPB)
        {
            return targetPPB switch
            {
                8 => new Picture8bpp(source),
                16 => new Picture16bpp(source),
                _ => throw new ArgumentOutOfRangeException(nameof(targetPPB), "Only 8bpp and 16bpp are supported."),
            };
        }

        public static HDRPicture16bpp ToHDRPicture(this IPicture source, float brightness, int maximumBrightness = 5000)
        {
            var s = source.ToBitPerPixel(16) as IPicture<ushort>;
            if(s is null) throw new InvalidCastException($"Could not cast source {source.filePath}/{source.frameIndex} to IPicture<ushort>");
            return new HDRPicture16bpp(s, false)
            {
                r = s.r,
                g = s.g,
                b = s.b,
                a = s.a,
                Brightness = Enumerable.Repeat(1f, s.Pixels).ToArray(),
                MaximumBrightness = maximumBrightness,
                ProcessStack = source.ProcessStack.Append(new PictureProcessStack
                {
                    OperationDisplayName = $"Converted to HDR with brightness {brightness} and max brightness {maximumBrightness}",
                    Operator = typeof(PictureExtensions),
                    ProcessingFuncStackTrace = new StackTrace(true),
                }).ToList()
            };
        }


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
