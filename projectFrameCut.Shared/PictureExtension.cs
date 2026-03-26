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
                IEnumerable<float>? aa = image.hasAlphaChannel ? image.GetSpecificChannel(IPicture.ChannelId.Alpha) as float[] : null;
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
                        var alphaArray = (aa as float[]) ?? Enumerable.Repeat(1f, image.Pixels).ToArray();
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
                        var alphaArray = (aa as float[]) ?? Enumerable.Repeat(1f, image.Pixels).ToArray();
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
        IPicture.PicturePixelMode BitPerPixel,
        int Width,
        int Height,
        int Pixels,
        DateTime CreatedAtUtc,
        DateTime? DisposedAtUtc,
        DateTime? CollectedAtUtc,
        bool IsDisposed,
        bool IsCollected,
        TimeSpan? LifetimeToDispose,
        TimeSpan? LifetimeToCollect);

    /// <summary>
    /// Centralized lifecycle tracker for <see cref="IPicture"/> objects.
    /// </summary>
    public static class PictureLifecycleTracker
    {
        private sealed class PictureIdentity
        {
            public PictureIdentity(long id)
            {
                Id = id;
            }

            public long Id { get; }
        }

        private sealed class PictureLifecycleState
        {
            private long _disposedAtTicks;
            private long _collectedAtTicks;

            public PictureLifecycleState(long id, IPicture picture)
            {
                Id = id;
                TypeName = picture.GetType().FullName ?? picture.GetType().Name;
                BitPerPixel = picture.bitPerPixel;
                Width = picture.Width;
                Height = picture.Height;
                Pixels = picture.Pixels;
                CreatedAtUtc = DateTime.UtcNow;
            }

            public long Id { get; }
            public string TypeName { get; }
            public IPicture.PicturePixelMode BitPerPixel { get; }
            public int Width { get; }
            public int Height { get; }
            public int Pixels { get; }
            public DateTime CreatedAtUtc { get; }

            public void MarkDisposed()
            {
                Interlocked.CompareExchange(ref _disposedAtTicks, DateTime.UtcNow.Ticks, 0);
            }

            public void MarkCollected()
            {
                Interlocked.CompareExchange(ref _collectedAtTicks, DateTime.UtcNow.Ticks, 0);
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
                    BitPerPixel,
                    Width,
                    Height,
                    Pixels,
                    CreatedAtUtc,
                    disposedAt,
                    collectedAt,
                    disposedAt.HasValue,
                    collectedAt.HasValue,
                    disposedAt?.Subtract(CreatedAtUtc),
                    collectedAt?.Subtract(CreatedAtUtc));
            }
        }

        private sealed class FinalizationSentinel
        {
            private readonly long _id;

            public FinalizationSentinel(long id)
            {
                _id = id;
            }

            ~FinalizationSentinel()
            {
                PictureLifecycleTracker.MarkCollected(_id);
            }
        }

        private static long _nextId;
        private static bool _enabled;
        private static readonly ConcurrentDictionary<long, PictureLifecycleState> States = new();
        private static readonly ConditionalWeakTable<IPicture, PictureIdentity> Identities = new();
        private static readonly ConditionalWeakTable<IPicture, FinalizationSentinel> Sentinels = new();

        /// <summary>
        /// Enables tracking globally. Keep false in production unless diagnostics are needed.
        /// </summary>
        public static bool Enabled
        {
            get => Volatile.Read(ref _enabled);
            set => Volatile.Write(ref _enabled, value);
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
                Sentinels.GetValue(picture, _ => new FinalizationSentinel(identity.Id));
            }
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static void MarkDisposed(IPicture picture)
        {
            if (!Enabled) return;
            if (!Identities.TryGetValue(picture, out PictureIdentity? identity)) return;
            if (States.TryGetValue(identity.Id, out PictureLifecycleState? state))
            {
                state.MarkDisposed();
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

        private static void MarkCollected(long id)
        {
            if (States.TryGetValue(id, out PictureLifecycleState? state))
            {
                state.MarkCollected();
            }
        }
    }
}
