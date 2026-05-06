using CommunityToolkit.HighPerformance;
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
using System.Threading;
using System.Threading.Channels;
using static projectFrameCut.Shared.IPicture;
using Image = SixLabors.ImageSharp.Image;

namespace projectFrameCut.Shared
{
    #region base

    /// <summary>
    /// This class is for placing Picture information and as a base of the actual picture (<see cref="IPicture{T}"/>).
    /// </summary>
    public interface IPicture : IDisposable
    {
        /// <summary>
        /// If set, the picture will be saved to this path for diagnostics.
        /// </summary>
        public static string? DiagImagePath { get; set; }
        /// <summary>
        /// Allow convert a IPicture to a lower <see cref="bitPerPixel"/>.
        /// </summary>
        /// <remarks>
        /// When this is false, an <see cref="InvalidOperationException"/> will be thrown when attempting to convert to a lower pixel mode, either in <see cref="ToBitPerPixel(int)"/> or in VideoWriter.
        /// </remarks>
        public static bool AllowPixelModeDowngrade = true;
        /// <summary>
        /// Get how much bits in one pixel.
        /// Please refer to <see cref="PicturePixelMode"/> for more information.
        /// </summary>
        public PicturePixelMode bitPerPixel { get; }
        /// <summary>
        /// The width of this picture
        /// </summary>
        public int Width { get; set; }
        /// <summary>
        /// The height of this picture
        /// </summary>
        public int Height { get; set; }
        /// <summary>
        /// Total pixels of this picture
        /// </summary>
        public int Pixels { get; init; }
        /// <summary>
        /// The frame index this picture comes from. Used for diagnostics only.
        /// </summary>
        public uint? frameIndex { get; set; } //诊断用
        /// <summary>
        /// The file path this picture comes from. Used for diagnostics only.
        /// </summary>
        public string? filePath { get; set; } //诊断用
        /// <summary>
        /// Determine some flag for the picture.
        /// </summary>
        public PictureFlag Flag { get; set; }
        /// <summary>
        /// Records each step of the image processed.
        /// </summary>
        /// <remarks>
        /// Please append your processing step information to this property if you're manipulating the picture manually.
        /// If you're using <see cref="IPictureProcessStep"/>, override <see cref="IPictureProcessStep.GetProcessStack"/> to provide the information.
        /// </remarks>
        public List<PictureProcessStack> ProcessStack { get; set; }
        /// <summary>
        /// Indicates whether this picture has an alpha channel.
        /// </summary>
        public bool hasAlphaChannel { get; set; }
        /// <summary>
        /// Get whether this picture has been disposed.
        /// </summary>
        public bool Disposed { get; }

        /// <summary>
        /// Gets or sets a value indicating whether the object can be disposed.
        /// </summary>
        /// <remarks>
        /// when this is true, <see cref="Dispose(bool)"/> will have no effect, except the force param is True.
        /// </remarks>
        public bool CanBeDisposed { get; set; }

        /// <summary>
        /// Resize the picture. 
        /// </summary>
        /// <param name="preserveAspect">Keep the source picture's aspect ratio while true.</param>
        IPicture Resize(int targetWidth, int targetHeight, bool preserveAspect = true);
        /// <summary>
        /// Convert this picture to the specified bits per pixel.
        /// </summary>
        IPicture ToBitPerPixel(PicturePixelMode bitPerPixel);

        /// <summary>
        /// Convert this picture to the specified bits per pixel.
        /// </summary>
        public sealed IPicture ToBitPerPixel(int bpp) => ToBitPerPixel(new PicturePixelMode(bpp));

        /// <summary>
        /// Get a specific channel's data.
        /// </summary>
        /// <param name="channelId">The channel want to get</param>
        /// <returns>the data. Must in a array</returns>
        object? GetSpecificChannel(ChannelId channelId);

        /// <summary>
        /// Get the diagnostics information of this picture.
        /// </summary>
        /// <returns>The Diagnostics info</returns>
        string GetDiagnosticsInfo();

        /// <summary>
        /// Dispose the Picture when <see cref="CanBeDisposed"/> is true or <paramref name="force"/> is true.
        /// </summary>
        /// <param name="force">Ignore <see cref="CanBeDisposed"/> and dispose it anyway.</param>
        public void Dispose(bool force = false);

        public enum ChannelId
        {
            Red = 0,
            Green = 1,
            Blue = 2,
            Alpha = 3
        }

        [Obsolete("No longer used. To prevent disposing, use CanBeDisposed property; To tag this image, use frameIndex or filePath.")]
        [Flags]
        public enum PictureFlag
        {
            None = 0,
            IsGenerated = 1 << 0,
            NoDisposeAfterWrite = 1 << 1,
        }

        public readonly record struct PicturePixelMode(int Value)
        {
            public static implicit operator int(PicturePixelMode bpp) => bpp.Value;
            public static implicit operator PicturePixelMode(int value) => new(value);
            public override int GetHashCode() => Value;
            public override string ToString() => Value.ToString();

            public bool Equals(PicturePixelMode mode)
            {
                return Value == mode.Value;
            }
            /// <summary>
            /// Represents a picture of 8 bits per pixel, aka <see cref="Picture8bpp"/>.
            /// </summary>
            public static PicturePixelMode BytePicture => new PicturePixelMode(8);
            /// <summary>
            /// Represents a picture of 16 bits per pixel, aka <see cref="Picture16bpp"/>.
            /// </summary>
            public static PicturePixelMode UShortPicture => new PicturePixelMode(16);
        }

    }



    /// <summary>
    /// Represents a picture with pixel data of type T and a float alpha channel.
    /// </summary>
    /// <typeparam name="T">The pixel type.</typeparam>
    public interface IPicture<T> : IPicture, IDisposable
    {
        /// <summary>
        /// Red channel. 0 means dark and 1 means brightest.
        /// </summary>
        [JsonIgnore()]
        public T[] r { get; set; }
        /// <summary>
        /// Green channel. 0 means dark and 1 means brightest.
        /// </summary>
        [JsonIgnore()]
        public T[] g { get; set; }
        /// <summary>
        /// Blue channel. 0 means dark and 1 means brightest.
        /// </summary>
        [JsonIgnore()]
        public T[] b { get; set; }
        /// <summary>
        /// Alpha channel. 0 means completely transparent and 1 means not transparent.
        /// Negative value is not accepted.
        /// If this array is null, means this image does not have alpha channel.
        /// </summary>
        [JsonIgnore()]
        [NotNull()]
        public float[]? a { get; set; }

        /// <summary>
        /// Set the alpha channel.
        /// </summary>
        public IPicture<T> SetAlpha(bool haveAlpha);
        /// <summary>
        /// Resize the target picture.
        /// </summary>
        public new IPicture<T> Resize(int targetWidth, int targetHeight, bool preserveAspect = true);
    }


    /// <summary>
    /// Represents a picture without an alpha channel.
    /// </summary>
    public interface INoAlphaPicture<T> : IPicture, IDisposable
    {
        [JsonIgnore()]
        public T[] r { get; set; }
        [JsonIgnore()]
        public T[] g { get; set; }
        [JsonIgnore()]
        public T[] b { get; set; }

        public new bool hasAlphaChannel { get => false; set { } }

        public new IPicture<T> Resize(int targetWidth, int targetHeight, bool preserveAspect = true);
    }
    /// <summary>
    /// Represents a picture with an uniform, float-based alpha channel.
    /// </summary>
    public interface IUniformAlphaPicture<T> : IPicture, IDisposable
    {
        [JsonIgnore()]
        public T[] r { get; set; }
        [JsonIgnore()]
        public T[] g { get; set; }
        [JsonIgnore()]
        public T[] b { get; set; }
        [JsonIgnore()]
        public float uniformAlpha { get; set; }

        public new IPicture<T> Resize(int targetWidth, int targetHeight, bool preserveAspect = true);

    }

    #endregion

    #region 16bpp

    /// <summary>
    /// The projectFrameCut's 16-bit Picture structure. It's the base of everything you see in the final video.
    /// </summary>
    [DebuggerDisplay("ProcessStack: {ProcessStack}")]
    public class Picture16bpp : IPicture<ushort>
    {
        [JsonIgnore()]
        public ushort[] r { get; set; } = Array.Empty<ushort>();
        [JsonIgnore()]
        public ushort[] g { get; set; } = Array.Empty<ushort>();
        [JsonIgnore()]
        public ushort[] b { get; set; } = Array.Empty<ushort>();
        [JsonIgnore()]
        [NotNull()]
        public float[]? a { get; set; } = null;
        public int Width { get; set; }
        public int Height { get; set; }
        public int Pixels { get; init; }

        public uint? frameIndex { get; set; } //诊断用
        public string? filePath { get; set; } //诊断用
        public PictureFlag Flag { get; set; }
        public List<PictureProcessStack> ProcessStack { get; set; }
        public bool Disposed { get; set; } = false;
        public bool CanBeDisposed { get; set; } = true;

        public bool hasAlphaChannel { get; set; } = false;

        public PicturePixelMode bitPerPixel => 16;
        /// <summary>
        /// Initializes a new instance of the Picture class by copying the properties of an existing Picture.
        /// </summary>
        /// <remarks>The new Picture instance shares the same pixel data reference as the source Picture.
        /// Changes to the pixel data in one instance will affect the other.</remarks>
        /// <param name="picture">The Picture instance to copy the width, height, and pixel data from. Cannot be null.</param>
        public Picture16bpp(IPicture<ushort> picture, bool copyData = false)
        {
            Width = picture.Width;
            Height = picture.Height;
            Pixels = picture.Pixels;
            if (copyData)
            {
                // Ensure pixel buffers reference the source buffers if present, otherwise allocate
                r = (picture.r != null && picture.r.Length == Pixels) ? picture.r : new ushort[Pixels];
                g = (picture.g != null && picture.g.Length == Pixels) ? picture.g : new ushort[Pixels];
                b = (picture.b != null && picture.b.Length == Pixels) ? picture.b : new ushort[Pixels];

                if (picture.a != null && picture.a.Length == Pixels)
                {
                    a = picture.a;
                    hasAlphaChannel = true;
                }
                else
                {
                    a = null;
                    hasAlphaChannel = false;
                }
            }


            ProcessStack = [new PictureProcessStack
            {
                OperationDisplayName = "Create from another",
                Operator = this.GetType(),
                ProcessingFuncStackTrace = new StackTrace(true),
                Properties = new Dictionary<string, object>
                {
                    { "SourceProcessStack", picture?.ProcessStack!  },
                    { "CopyData", copyData }
                },
            }];

            PictureLifecycleTracker.RegisterCreated(this);
        }

        /// <summary>
        /// Initializes a new instance of the Picture class with the specified width and height.
        /// </summary>
        /// <param name="width">The width of the picture, in pixels. Must be a non-negative integer.</param>
        /// <param name="height">The height of the picture, in pixels. Must be a non-negative integer.</param>
        public Picture16bpp(int width, int height)
        {
            Width = width;
            Height = height;
            Pixels = checked(width * height);

            // allocate pixel buffers so the instance is safe to use immediately
            r = new ushort[Pixels];
            g = new ushort[Pixels];
            b = new ushort[Pixels];
            a = null;
            ProcessStack = [new PictureProcessStack
            {
                OperationDisplayName = "Create from scratch",
                Operator = this.GetType(),
                ProcessingFuncStackTrace = new StackTrace(true),
            }];

            PictureLifecycleTracker.RegisterCreated(this);
        }


        /// <summary>
        /// Initializes a new instance of the Picture class by loading image data from the specified file path.
        /// </summary>
        /// <remarks>The image is loaded from the specified file and its pixel data is extracted for use
        /// by the Picture instance. The constructor supports images compatible with the underlying image processing
        /// library. If the file does not exist or is not a valid image, an exception may be thrown by the image loading
        /// process.</remarks>
        /// <param name="imagePath">The file path to the image to load. The path must refer to a valid image file and cannot be null, empty, or
        /// consist only of white-space characters.</param>
        /// <exception cref="ArgumentException">Thrown if imagePath is null, empty, or consists only of white-space characters.</exception>
        [DebuggerNonUserCode()]
        public Picture16bpp(string imagePath)
        {
            if (string.IsNullOrWhiteSpace(imagePath)) throw new ArgumentException("imagePath is null or empty", nameof(imagePath));
            using (Image<Rgba64> img = Image.Load<Rgba64>(imagePath))
            {
                int width = img.Width;
                int height = img.Height;
                int total = checked(width * height);
                Width = width;
                Height = height;

                r = new ushort[total];
                g = new ushort[total];
                b = new ushort[total];
                a = new float[total];

                for (int y = 0; y < height; y++)
                {
                    for (int x = 0; x < width; x++)
                    {
                        int k = y * width + x;
                        Rgba64 px = img[x, y];
                        r[k] = px.R;
                        g[k] = px.G;
                        b[k] = px.B;
                        a[k] = px.A / 65535f;
                    }
                }
            }

            Pixels = checked(Width * Height);
            filePath = imagePath;
            ProcessStack = new List<PictureProcessStack>
            {
                new PictureProcessStack
                {
                    OperationDisplayName = "Create from file",
                    Operator = this.GetType(),
                    ProcessingFuncStackTrace = new StackTrace(true),
                    Properties = new Dictionary<string, object>
                    {
                        { "FilePath", imagePath },
                    },
                }
            };

            PictureLifecycleTracker.RegisterCreated(this);
        }

        /// <summary>
        /// Create a new Picture from a SixLabors.ImageSharp.Image source.
        /// </summary>
        /// <param name="source"></param>
        /// <exception cref="ArgumentNullException"></exception>
        [DebuggerNonUserCode()]
        public Picture16bpp(SixLabors.ImageSharp.Image source)
        {
            Stopwatch sw = Stopwatch.StartNew();
            if (source == null) throw new ArgumentNullException(nameof(source));
            Width = source.Width;
            Height = source.Height;
            Pixels = checked(Width * Height);
            r = new ushort[Pixels];
            g = new ushort[Pixels];
            b = new ushort[Pixels];
            if (source.PixelType.BitsPerPixel == 64) //Rgba32
            {
                hasAlphaChannel = true;
                a = new float[Pixels];
                var img = source.CloneAs<Rgba64>();
                for (int y = 0; y < Height; y++)
                {
                    for (int x = 0; x < Width; x++)
                    {
                        int k = y * Width + x;
                        Rgba64 px = img[x, y];
                        r[k] = px.R;
                        g[k] = px.G;
                        b[k] = px.B;
                        a[k] = px.A / 65535f;
                    }
                }
            }
            else if (source.PixelType.BitsPerPixel == 32) //Rgba32
            {
                hasAlphaChannel = true;
                a = new float[Pixels];
                var img = source.CloneAs<Rgba32>();
                for (int y = 0; y < Height; y++)
                {
                    for (int x = 0; x < Width; x++)
                    {
                        int k = y * Width + x;
                        Rgba32 px = img[x, y];
                        r[k] = (ushort)(px.R * 257);
                        g[k] = (ushort)(px.G * 257);
                        b[k] = (ushort)(px.B * 257);
                        a[k] = px.A / 255f;
                    }
                }
            }
            else //Rgb24
            {
                hasAlphaChannel = false;
                a = null;
                var img = source.CloneAs<Rgb24>();
                for (int y = 0; y < Height; y++)
                {
                    for (int x = 0; x < Width; x++)
                    {
                        int k = y * Width + x;
                        Rgb24 px = img[x, y];
                        r[k] = (ushort)(px.R * 257);
                        g[k] = (ushort)(px.G * 257);
                        b[k] = (ushort)(px.B * 257);
                    }
                }
            }
            ProcessStack = new List<PictureProcessStack>
            {
                new PictureProcessStack
                {
                    OperationDisplayName = "Converted from SixLabors.ImageSharp.Image",
                    Operator = this.GetType(),
                    Elapsed = sw.Elapsed,
                    ProcessingFuncStackTrace = new StackTrace(true),
                }
            };

            PictureLifecycleTracker.RegisterCreated(this);

        }

        public Picture16bpp SetAlpha(bool haveAlpha)
        {
            lock (this)
            {
                if (haveAlpha == hasAlphaChannel)
                {
                    return this;
                }
                hasAlphaChannel = haveAlpha;
                if (!haveAlpha)
                {
                    a = null;
                }
                else
                {
                    a = Enumerable.Repeat(1f, Pixels).ToArray();
                }
                return this;
            }
        }

        public void EnsureAlpha()
        {
            lock (this)
            {
                if (!hasAlphaChannel || a == null || a.Length != Pixels)
                {
                    a = Enumerable.Repeat(1f, Pixels).ToArray();
                    hasAlphaChannel = true;
                }
            }
        }


        public void EnsureNoAlpha()
        {
            if (hasAlphaChannel || a != null || a?.Length == Pixels)
            {
                a = null;
                hasAlphaChannel = false;
            }
        }


        /// <summary>
        /// Resizes the picture using bilinear resampling. When <paramref name="preserveAspect"/> is true,
        /// the image is scaled to fit within the provided target dimensions while preserving aspect ratio.
        /// </summary>
        /// <param name="targetWidth">The target width.</param>
        /// <param name="targetHeight">The target height.</param>
        /// <param name="preserveAspect">Whether to preserve aspect ratio.</param>
        /// <returns>A new Picture instance with the resized image data.</returns>
        [DebuggerNonUserCode()]
        public Picture16bpp Resize(int targetWidth, int targetHeight, bool preserveAspect = true)
        {
            var sw = Stopwatch.StartNew();
            if (targetWidth == Width && targetHeight == Height) return this;
            lock (this)
            {
                if (targetWidth <= 0 || targetHeight <= 0) throw new ArgumentException("targetWidth and targetHeight must be positive");
                if (Width <= 0 || Height <= 0) throw new InvalidOperationException("Source image has invalid dimensions");

                int destW = targetWidth;
                int destH = targetHeight;

                if (preserveAspect)
                {
                    double sx = (double)targetWidth / Width;
                    double sy = (double)targetHeight / Height;
                    double s = Math.Min(sx, sy);
                    destW = Math.Max(1, (int)Math.Round(Width * s));
                    destH = Math.Max(1, (int)Math.Round(Height * s));
                    if (destW == Width && destH == Height) return this;
                }

                var result = new Picture16bpp(destW, destH);
                int dstPixels = checked(destW * destH);
                result.r = new ushort[dstPixels];
                result.g = new ushort[dstPixels];
                result.b = new ushort[dstPixels];
                result.a = hasAlphaChannel ? new float[dstPixels] : null;
                result.hasAlphaChannel = hasAlphaChannel;

                double xRatio = (double)Width / destW;
                double yRatio = (double)Height / destH;
                int srcArraySize = this.r.Length;

                for (int y = 0; y < destH; y++)
                {
                    double srcY = (y + 0.5) * yRatio - 0.5;
                    int y0 = (int)Math.Floor(srcY);
                    int y1 = y0 + 1;
                    double wy = srcY - y0;
                    if (y0 < 0)
                    {
                        y0 = 0; y1 = 0; wy = 0;
                    }
                    else if (y0 >= Height) { y0 = Height - 1; y1 = Height - 1; wy = 0; }
                    if (y1 >= Height) { y1 = Height - 1; }

                    for (int x = 0; x < destW; x++)
                    {
                        double srcX = (x + 0.5) * xRatio - 0.5;
                        int x0 = (int)Math.Floor(srcX);
                        int x1 = x0 + 1;
                        double wx = srcX - x0;
                        if (x0 < 0)
                        {
                            x0 = 0; x1 = 0; wx = 0;
                        }
                        else if (x0 >= Width) { x0 = Width - 1; x1 = Width - 1; wx = 0; }
                        if (x1 >= Width) { x1 = Width - 1; }

                        int k00 = Math.Max(0, Math.Min(srcArraySize - 1, y0 * Width + x0));
                        int k10 = Math.Max(0, Math.Min(srcArraySize - 1, y0 * Width + x1));
                        int k01 = Math.Max(0, Math.Min(srcArraySize - 1, y1 * Width + x0));
                        int k11 = Math.Max(0, Math.Min(srcArraySize - 1, y1 * Width + x1));

                        double r00 = this.r[k00];
                        double r10 = this.r[k10];
                        double r01 = this.r[k01];
                        double r11 = this.r[k11];

                        double g00 = this.g[k00];
                        double g10 = this.g[k10];
                        double g01 = this.g[k01];
                        double g11 = this.g[k11];

                        double b00 = this.b[k00];
                        double b10 = this.b[k10];
                        double b01 = this.b[k01];
                        double b11 = this.b[k11];

                        double rInterp = r00 * (1 - wx) * (1 - wy) + r10 * wx * (1 - wy) + r01 * (1 - wx) * wy + r11 * wx * wy;
                        double gInterp = g00 * (1 - wx) * (1 - wy) + g10 * wx * (1 - wy) + g01 * (1 - wx) * wy + g11 * wx * wy;
                        double bInterp = b00 * (1 - wx) * (1 - wy) + b10 * wx * (1 - wy) + b01 * (1 - wx) * wy + b11 * wx * wy;

                        int dstIdx = y * destW + x;
                        int rr = (int)Math.Round(rInterp);
                        int gg = (int)Math.Round(gInterp);
                        int bb = (int)Math.Round(bInterp);
                        if (rr < 0) rr = 0; if (rr > 65535) rr = 65535;
                        if (gg < 0) gg = 0; if (gg > 65535) gg = 65535;
                        if (bb < 0) bb = 0; if (bb > 65535) bb = 65535;

                        result.r[dstIdx] = (ushort)rr;
                        result.g[dstIdx] = (ushort)gg;
                        result.b[dstIdx] = (ushort)bb;

                        if (hasAlphaChannel && this.a != null)
                        {
                            double a00 = this.a[k00];
                            double a10 = this.a[k10];
                            double a01 = this.a[k01];
                            double a11 = this.a[k11];
                            double aInterp = a00 * (1 - wx) * (1 - wy) + a10 * wx * (1 - wy) + a01 * (1 - wx) * wy + a11 * wx * wy;
                            if (double.IsNaN(aInterp) || double.IsInfinity(aInterp)) aInterp = 1.0;
                            if (aInterp < 0) aInterp = 0; if (aInterp > 1) aInterp = 1;
                            result.a![dstIdx] = (float)aInterp;
                        }
                    }
                }
                result.ProcessStack.Add(new PictureProcessStack
                {
                    OperationDisplayName = "Resize (IPicture)",
                    Operator = this.GetType(),
                    ProcessingFuncStackTrace = new(true),
                    Properties = new Dictionary<string, object>
                    {
                        { "SourceWidth", Width },
                        { "SourceHeight", Height },
                        { "TargetWidth", targetWidth },
                        { "TargetHeight", targetHeight },
                        { "PreserveAspect", preserveAspect },
                    },
                    Elapsed = sw.Elapsed
                });

                return result;
            }
        }

        public static Picture16bpp GenerateSolidColor(int width, int height, ushort r, ushort g, ushort b, float? a)
        {
            var pic = new Picture16bpp(width, height)
            {
                ProcessStack = new List<PictureProcessStack>
                {
                    new PictureProcessStack
                    {
                        OperationDisplayName = "GenerateSolidColor",
                        Operator = typeof(Picture16bpp),
                        ProcessingFuncStackTrace = new StackTrace(true),
                        Properties = new Dictionary<string, object>
                        {
                            { "Width", width },
                            { "Height", height },
                            { "R", r },
                            { "G", g },
                            { "B", b },
                            { "A", a ?? -1f },
                        },
                    }
                }
            };
            pic.r = Enumerable.Repeat(r, pic.Pixels).ToArray();
            pic.g = Enumerable.Repeat(g, pic.Pixels).ToArray();
            pic.b = Enumerable.Repeat(b, pic.Pixels).ToArray();
            if (a != null)
            {
                pic.a = Enumerable.Repeat(a.Value, pic.Pixels).ToArray();
                pic.hasAlphaChannel = true;
            }
            else
            {
                pic.a = null;
                pic.hasAlphaChannel = false;
            }
            return pic;
        }

        private bool disposedValue;

        protected virtual void Dispose(bool disposing, bool force = false)
        {
            if (!force && (disposedValue || !CanBeDisposed)) return;
            lock (this)
            {
                if (!disposedValue)
                {
                    if (disposing)
                    {
                        r = null!;
                        g = null!;
                        b = null!;
                        a = null;
                    }

                    disposedValue = true;
                    PictureLifecycleTracker.MarkDisposed(this);
                }
                Disposed = disposedValue;

            }
        }

        public void Dispose() => Dispose(force: true);


        public void Dispose(bool force = false)
        {
            Dispose(disposing: true, force);
            GC.SuppressFinalize(this);
        }

        public IPicture ToBitPerPixel(PicturePixelMode bitPerPixel)
        {
            if (bitPerPixel == PicturePixelMode.UShortPicture)
            {
                return this;
            }
            else if (bitPerPixel == PicturePixelMode.BytePicture)
            {
                if (!AllowPixelModeDowngrade) throw new InvalidOperationException($"AllowPixelModeDowngrade is false, so you can't convert a Picture16bpp to Picture8bpp.") { Data = { { "ProcessStack", ProcessStack } } };
                var sw = Stopwatch.StartNew();
                var pic = new Picture8bpp(Width, Height)
                {
                    frameIndex = this.frameIndex,
                    filePath = this.filePath,
                    hasAlphaChannel = this.hasAlphaChannel,
                    ProcessStack = this.ProcessStack,
                };

                if (hasAlphaChannel && a != null)
                {
                    pic.a = new float[Pixels];
                    Array.Copy(a, pic.a, Pixels);
                }
                else
                {
                    pic.a = null;
                }

                for (int i = 0; i < Pixels; i++)
                {
                    pic.r[i] = (byte)(r[i] / 257);
                    pic.g[i] = (byte)(g[i] / 257);
                    pic.b[i] = (byte)(b[i] / 257);
                }
                pic.ProcessStack.Add(new PictureProcessStack
                {
                    OperationDisplayName = "Converted from 16bpp to 8bpp",
                    Operator = this.GetType(),
                    ProcessingFuncStackTrace = new StackTrace(true),
                    Elapsed = sw.Elapsed
                });
                return pic;
            }
            else
            {
                throw new ArgumentOutOfRangeException(nameof(bitPerPixel), "仅支持 8 或 16 bpp。");
            }
        }

        IPicture<ushort> IPicture<ushort>.SetAlpha(bool haveAlpha)
        {
            return SetAlpha(haveAlpha);
        }

        IPicture<ushort> IPicture<ushort>.Resize(int targetWidth, int targetHeight, bool preserveAspect)
        {
            return Resize(targetWidth, targetHeight, preserveAspect);
        }

        IPicture IPicture.Resize(int targetWidth, int targetHeight, bool preserveAspect)
        {
            return Resize(targetWidth, targetHeight, preserveAspect);
        }

        public object? GetSpecificChannel(IPicture.ChannelId channelId)
        {
            return channelId switch
            {
                IPicture.ChannelId.Red => r,
                IPicture.ChannelId.Green => g,
                IPicture.ChannelId.Blue => b,
                IPicture.ChannelId.Alpha => a!,
                _ => throw new ArgumentOutOfRangeException(nameof(channelId), "Invalid channel ID."),
            };
        }

        public string GetDiagnosticsInfo() => $"16BitPerPixel image, Size: {Width}*{Height}, avg R:{r.Average(Convert.ToDecimal)} G:{g.Average(Convert.ToDecimal)} B:{b.Average(Convert.ToDecimal)} A:(has:{hasAlphaChannel}){a?.Average(Convert.ToDecimal) ?? -1}";
    }

    #endregion

    #region 8bpp

    /// <summary>
    /// The projectFrameCut's 8-bit Picture structure.
    /// </summary>
    [DebuggerDisplay("ProcessStack: {ProcessStack}")]
    public class Picture8bpp : IPicture<byte>
    {
        [JsonIgnore()]
        public byte[] r { get; set; } = Array.Empty<byte>();
        [JsonIgnore()]
        public byte[] g { get; set; } = Array.Empty<byte>();
        [JsonIgnore()]
        public byte[] b { get; set; } = Array.Empty<byte>();
        [JsonIgnore()]
        [NotNull()]
        public float[]? a { get; set; } = null;
        public int Width { get; set; }
        public int Height { get; set; }
        public int Pixels { get; init; }

        public uint? frameIndex { get; set; } //诊断用
        public string? filePath { get; set; } //诊断用
        public PictureFlag Flag { get; set; }
        public List<PictureProcessStack> ProcessStack { get; set; }
        public bool Disposed { get; set; } = false;
        public bool CanBeDisposed { get; set; } = true;

        public bool hasAlphaChannel { get; set; } = false;

        public PicturePixelMode bitPerPixel => 8;



        /// <summary>
        /// Initializes a new instance of the Picture class by copying the properties of an existing Picture.
        /// </summary>
        /// <remarks>The new Picture instance shares the same pixel data reference as the source Picture.
        /// Changes to the pixel data in one instance will affect the other.</remarks>
        /// <param name="picture">The Picture instance to copy the width, height, and pixel data from. Cannot be null.</param>
        public Picture8bpp(IPicture<byte> picture, bool copyData = false)
        {
            Width = picture.Width;
            Height = picture.Height;
            Pixels = picture.Pixels;
            if (copyData)
            {
                // Ensure pixel buffers reference the source buffers if present, otherwise allocate
                r = (picture.r != null && picture.r.Length == Pixels) ? picture.r : new byte[Pixels];
                g = (picture.g != null && picture.g.Length == Pixels) ? picture.g : new byte[Pixels];
                b = (picture.b != null && picture.b.Length == Pixels) ? picture.b : new byte[Pixels];

                if (picture.a != null && picture.a.Length == Pixels)
                {
                    a = picture.a;
                    hasAlphaChannel = true;
                }
                else
                {
                    a = null;
                    hasAlphaChannel = false;
                }
            }


            ProcessStack = new List<PictureProcessStack>
            {
                new PictureProcessStack
                {
                    OperationDisplayName = "Create from another",
                    Operator = this.GetType(),
                    ProcessingFuncStackTrace = new StackTrace(true),
                    Properties = new Dictionary<string, object>
                    {
                        { "SourceProcessStack", picture?.ProcessStack! },
                        { "CopyData", copyData }
                    },
                }
            };

            PictureLifecycleTracker.RegisterCreated(this);

        }

        /// <summary>
        /// Initializes a new instance of the Picture8bpp class with the specified width and height.
        /// </summary>
        /// <param name="width">The width of the picture, in pixels. Must be a non-negative integer.</param>
        /// <param name="height">The height of the picture, in pixels. Must be a non-negative integer.</param>
        public Picture8bpp(int width, int height)
        {
            Width = width;
            Height = height;
            Pixels = checked(width * height);

            // allocate pixel buffers so the instance is safe to use immediately
            r = new byte[Pixels];
            g = new byte[Pixels];
            b = new byte[Pixels];
            a = null;
            ProcessStack = new List<PictureProcessStack>
            {
                new PictureProcessStack
                {
                    OperationDisplayName = "Created from scratch",
                    Operator = this.GetType(),
                    ProcessingFuncStackTrace = new StackTrace(true),
                    Properties = new Dictionary<string, object>
                    {
                        { "Width", Width },
                        { "Height", Height }
                    },
                }
            };

            PictureLifecycleTracker.RegisterCreated(this);

        }


        /// <summary>
        /// Initializes a new instance of the Picture8bpp class by loading image data from the specified file path.
        /// </summary>
        /// <param name="imagePath">The file path to the image to load.</param>
        [DebuggerNonUserCode()]
        public Picture8bpp(string imagePath)
        {
            if (string.IsNullOrWhiteSpace(imagePath)) throw new ArgumentException("imagePath is null or empty", nameof(imagePath));
            using (Image<Rgba32> img = Image.Load<Rgba32>(imagePath))
            {
                int width = img.Width;
                int height = img.Height;
                int total = checked(width * height);
                Width = width;
                Height = height;

                r = new byte[total];
                g = new byte[total];
                b = new byte[total];
                a = new float[total];

                for (int y = 0; y < height; y++)
                {
                    for (int x = 0; x < width; x++)
                    {
                        int k = y * width + x;
                        Rgba32 px = img[x, y];
                        r[k] = px.R;
                        g[k] = px.G;
                        b[k] = px.B;
                        a[k] = px.A / 255f;
                    }
                }
            }

            Pixels = checked(Width * Height);
            filePath = imagePath;
            ProcessStack = new List<PictureProcessStack>
            {
                new PictureProcessStack
                {
                    OperationDisplayName = $"Created from file '{imagePath}'",
                    Operator = this.GetType(),
                    ProcessingFuncStackTrace = new StackTrace(true),
                    Properties = new Dictionary<string, object>
                    {
                        { "FilePath", imagePath },
                        { "Width", Width },
                        { "Height", Height }
                    },
                }
            };

            PictureLifecycleTracker.RegisterCreated(this);

        }

        [DebuggerNonUserCode()]
        public Picture8bpp(SixLabors.ImageSharp.Image source)
        {
            Stopwatch sw = Stopwatch.StartNew();
            if (source == null) throw new ArgumentNullException(nameof(source));
            Width = source.Width;
            Height = source.Height;
            Pixels = checked(Width * Height);
            r = new byte[Pixels];
            g = new byte[Pixels];
            b = new byte[Pixels];
            if (source.PixelType.BitsPerPixel == 64) //Rgba64
            {
                hasAlphaChannel = true;
                a = new float[Pixels];
                var img = source.CloneAs<Rgba64>();
                for (int y = 0; y < Height; y++)
                {
                    for (int x = 0; x < Width; x++)
                    {
                        int k = y * Width + x;
                        Rgba64 px = img[x, y];
                        r[k] = (byte)(px.R / 257);
                        g[k] = (byte)(px.G / 257);
                        b[k] = (byte)(px.B / 257);
                        a[k] = px.A / 65535f;
                    }
                }
            }
            else if (source.PixelType.BitsPerPixel == 32) //Rgba32
            {
                hasAlphaChannel = true;
                a = new float[Pixels];
                var img = source.CloneAs<Rgba32>();
                for (int y = 0; y < Height; y++)
                {
                    for (int x = 0; x < Width; x++)
                    {
                        int k = y * Width + x;
                        Rgba32 px = img[x, y];
                        r[k] = px.R;
                        g[k] = px.G;
                        b[k] = px.B;
                        a[k] = px.A / 255f;
                    }
                }
            }
            else //Rgb24
            {
                hasAlphaChannel = false;
                a = null;
                var img = source.CloneAs<Rgb24>();
                for (int y = 0; y < Height; y++)
                {
                    for (int x = 0; x < Width; x++)
                    {
                        int k = y * Width + x;
                        Rgb24 px = img[x, y];
                        r[k] = px.R;
                        g[k] = px.G;
                        b[k] = px.B;
                    }
                }
            }
            ProcessStack = new List<PictureProcessStack>
            {
                new PictureProcessStack
                {
                    OperationDisplayName = "Converted from SixLabors.ImageSharp.Image",
                    Operator = this.GetType(),
                    ProcessingFuncStackTrace = new StackTrace(true),
                    Elapsed = sw.Elapsed,
                    Properties = new Dictionary<string, object>
                    {
                        { "Width", Width },
                        { "Height", Height }
                    },
                }
            };

            PictureLifecycleTracker.RegisterCreated(this);

        }

        public Picture8bpp SetAlpha(bool haveAlpha)
        {
            lock (this)
            {
                if (haveAlpha == hasAlphaChannel)
                {
                    return this;
                }
                hasAlphaChannel = haveAlpha;
                if (!haveAlpha)
                {
                    a = null;
                }
                else
                {
                    a = Enumerable.Repeat(1f, Pixels).ToArray();
                }
                return this;
            }
        }

        public void EnsureAlpha()
        {
            lock (this)
            {
                if (!hasAlphaChannel || a == null || a.Length != Pixels)
                {
                    a = Enumerable.Repeat(1f, Pixels).ToArray();
                    hasAlphaChannel = true;
                }
            }
        }


        public void EnsureNoAlpha()
        {
            if (hasAlphaChannel || a != null || a?.Length == Pixels)
            {
                a = null;
                hasAlphaChannel = false;
            }
        }


        /// <summary>
        /// Resizes the picture using bilinear resampling. When <paramref name="preserveAspect"/> is true,
        /// the image is scaled to fit within the provided target dimensions while preserving aspect ratio.
        /// </summary>
        /// <param name="targetWidth">The target width.</param>
        /// <param name="targetHeight">The target height.</param>
        /// <param name="preserveAspect">Whether to preserve aspect ratio.</param>
        /// <returns>A new Picture8bpp instance with the resized image data.</returns>
        [DebuggerNonUserCode()]
        public Picture8bpp Resize(int targetWidth, int targetHeight, bool preserveAspect = true)
        {
            var sw = Stopwatch.StartNew();
            if (targetWidth == Width && targetHeight == Height) return this;
            lock (this)
            {
                if (targetWidth <= 0 || targetHeight <= 0) throw new ArgumentException("targetWidth and targetHeight must be positive");
                if (Width <= 0 || Height <= 0) throw new InvalidOperationException("Source image has invalid dimensions");

                int destW = targetWidth;
                int destH = targetHeight;

                if (preserveAspect)
                {
                    double sx = (double)targetWidth / Width;
                    double sy = (double)targetHeight / Height;
                    double s = Math.Min(sx, sy);
                    destW = Math.Max(1, (int)Math.Round(Width * s));
                    destH = Math.Max(1, (int)Math.Round(Height * s));
                    if (destW == Width && destH == Height) return this;
                }


                var result = new Picture8bpp(destW, destH);
                int dstPixels = checked(destW * destH);
                result.r = new byte[dstPixels];
                result.g = new byte[dstPixels];
                result.b = new byte[dstPixels];
                result.a = hasAlphaChannel ? new float[dstPixels] : null;
                result.hasAlphaChannel = hasAlphaChannel;

                double xRatio = (double)Width / destW;
                double yRatio = (double)Height / destH;
                int srcArraySize = this.r.Length;

                for (int y = 0; y < destH; y++)
                {
                    double srcY = (y + 0.5) * yRatio - 0.5;
                    int y0 = (int)Math.Floor(srcY);
                    int y1 = y0 + 1;
                    double wy = srcY - y0;
                    if (y0 < 0)
                    {
                        y0 = 0; y1 = 0; wy = 0;
                    }
                    else if (y0 >= Height) { y0 = Height - 1; y1 = Height - 1; wy = 0; }
                    if (y1 >= Height) { y1 = Height - 1; }

                    for (int x = 0; x < destW; x++)
                    {
                        double srcX = (x + 0.5) * xRatio - 0.5;
                        int x0 = (int)Math.Floor(srcX);
                        int x1 = x0 + 1;
                        double wx = srcX - x0;
                        if (x0 < 0)
                        {
                            x0 = 0; x1 = 0; wx = 0;
                        }
                        else if (x0 >= Width) { x0 = Width - 1; x1 = Width - 1; wx = 0; }
                        if (x1 >= Width) { x1 = Width - 1; }

                        int k00 = Math.Max(0, Math.Min(srcArraySize - 1, y0 * Width + x0));
                        int k10 = Math.Max(0, Math.Min(srcArraySize - 1, y0 * Width + x1));
                        int k01 = Math.Max(0, Math.Min(srcArraySize - 1, y1 * Width + x0));
                        int k11 = Math.Max(0, Math.Min(srcArraySize - 1, y1 * Width + x1));

                        double r00 = this.r[k00];
                        double r10 = this.r[k10];
                        double r01 = this.r[k01];
                        double r11 = this.r[k11];

                        double g00 = this.g[k00];
                        double g10 = this.g[k10];
                        double g01 = this.g[k01];
                        double g11 = this.g[k11];

                        double b00 = this.b[k00];
                        double b10 = this.b[k10];
                        double b01 = this.b[k01];
                        double b11 = this.b[k11];

                        double rInterp = r00 * (1 - wx) * (1 - wy) + r10 * wx * (1 - wy) + r01 * (1 - wx) * wy + r11 * wx * wy;
                        double gInterp = g00 * (1 - wx) * (1 - wy) + g10 * wx * (1 - wy) + g01 * (1 - wx) * wy + g11 * wx * wy;
                        double bInterp = b00 * (1 - wx) * (1 - wy) + b10 * wx * (1 - wy) + b01 * (1 - wx) * wy + b11 * wx * wy;

                        int dstIdx = y * destW + x;
                        int rr = (int)Math.Round(rInterp);
                        int gg = (int)Math.Round(gInterp);
                        int bb = (int)Math.Round(bInterp);
                        if (rr < 0) rr = 0; if (rr > 255) rr = 255;
                        if (gg < 0) gg = 0; if (gg > 255) gg = 255;
                        if (bb < 0) bb = 0; if (bb > 255) bb = 255;

                        result.r[dstIdx] = (byte)rr;
                        result.g[dstIdx] = (byte)gg;
                        result.b[dstIdx] = (byte)bb;

                        if (hasAlphaChannel && this.a != null)
                        {
                            double a00 = this.a[k00];
                            double a10 = this.a[k10];
                            double a01 = this.a[k01];
                            double a11 = this.a[k11];
                            double aInterp = a00 * (1 - wx) * (1 - wy) + a10 * wx * (1 - wy) + a01 * (1 - wx) * wy + a11 * wx * wy;
                            if (double.IsNaN(aInterp) || double.IsInfinity(aInterp)) aInterp = 1.0;
                            if (aInterp < 0) aInterp = 0; if (aInterp > 1) aInterp = 1;
                            result.a![dstIdx] = (float)aInterp;
                        }
                    }
                }
                result.ProcessStack.Add(new PictureProcessStack
                {
                    OperationDisplayName = "Resize (IPicture)",
                    Operator = this.GetType(),
                    ProcessingFuncStackTrace = new(true),
                    Properties = new Dictionary<string, object>
                    {
                        { "SourceWidth", Width },
                        { "SourceHeight", Height },
                        { "TargetWidth", targetWidth },
                        { "TargetHeight", targetHeight },
                        { "PreserveAspect", preserveAspect },
                    },
                    Elapsed = sw.Elapsed
                });
                return result;
            }
        }

        public static Picture8bpp GenerateSolidColor(int width, int height, byte r, byte g, byte b, float? a)
        {
            var pic = new Picture8bpp(width, height)
            {
                ProcessStack = new List<PictureProcessStack>
                {
                    new PictureProcessStack
                    {
                        OperationDisplayName = "Created solid color",
                        Operator = typeof(Picture8bpp),
                        ProcessingFuncStackTrace = new StackTrace(true),
                        Properties = new Dictionary<string, object>
                        {
                            { "Width", width },
                            { "Height", height },
                            { "R", r },
                            { "G", g },
                            { "B", b },
                            { "A", a ?? 1.0f }
                        },
                    }
                }
            };
            pic.r = Enumerable.Repeat(r, pic.Pixels).ToArray();
            pic.g = Enumerable.Repeat(g, pic.Pixels).ToArray();
            pic.b = Enumerable.Repeat(b, pic.Pixels).ToArray();
            if (a != null)
            {
                pic.a = Enumerable.Repeat(a.Value, pic.Pixels).ToArray();
                pic.hasAlphaChannel = true;
            }
            else
            {
                pic.a = null;
                pic.hasAlphaChannel = false;
            }
            return pic;
        }

        private bool disposedValue;

        protected virtual void Dispose(bool disposing, bool force = false)
        {
            if (!force && (disposedValue || CanBeDisposed)) return;
            lock (this)
            {
                if (!disposedValue)
                {
                    if (disposing)
                    {
                        r = null!;
                        g = null!;
                        b = null!;
                        a = null;
                    }

                    disposedValue = true;
                    PictureLifecycleTracker.MarkDisposed(this);
                }
                Disposed = disposedValue;
            }
        }

        public void Dispose() => Dispose(force: true);

        public void Dispose(bool force = false)
        {
            Dispose(disposing: true,force);
            GC.SuppressFinalize(this);
        }

        public IPicture ToBitPerPixel(PicturePixelMode bitPerPixel)
        {
            if (bitPerPixel == PicturePixelMode.BytePicture)
            {
                return this;
            }
            else if (bitPerPixel == PicturePixelMode.UShortPicture)
            {
                var sw = Stopwatch.StartNew();
                var pic = new Picture16bpp(Width, Height)
                {
                    frameIndex = this.frameIndex,
                    filePath = this.filePath,
                    hasAlphaChannel = this.hasAlphaChannel,
                    ProcessStack = this.ProcessStack
                };


                if (hasAlphaChannel && a != null)
                {
                    pic.a = new float[Pixels];
                    Array.Copy(a, pic.a, Pixels);
                }
                else
                {
                    pic.a = null;
                }

                for (int i = 0; i < Pixels; i++)
                {
                    pic.r[i] = (ushort)(r[i] * 257);
                    pic.g[i] = (ushort)(g[i] * 257);
                    pic.b[i] = (ushort)(b[i] * 257);
                }
                pic.ProcessStack.Add(new PictureProcessStack
                {
                    OperationDisplayName = "Converted from 8bpp to 16bpp",
                    Operator = this.GetType(),
                    ProcessingFuncStackTrace = new StackTrace(true),
                    Elapsed = sw.Elapsed
                });
                return pic;
            }
            else
            {
                throw new ArgumentOutOfRangeException(nameof(bitPerPixel), "仅支持 8 或 16 bpp。");
            }
        }

        IPicture<byte> IPicture<byte>.SetAlpha(bool haveAlpha)
        {
            return SetAlpha(haveAlpha);
        }

        IPicture<byte> IPicture<byte>.Resize(int targetWidth, int targetHeight, bool preserveAspect)
        {
            return Resize(targetWidth, targetHeight, preserveAspect);
        }

        IPicture IPicture.Resize(int targetWidth, int targetHeight, bool preserveAspect)
        {
            return Resize(targetWidth, targetHeight, preserveAspect);
        }

        public object? GetSpecificChannel(IPicture.ChannelId channelId)
        {
            return channelId switch
            {
                IPicture.ChannelId.Red => r,
                IPicture.ChannelId.Green => g,
                IPicture.ChannelId.Blue => b,
                IPicture.ChannelId.Alpha => a!,
                _ => throw new ArgumentOutOfRangeException(nameof(channelId), "Invalid channel ID."),
            };
        }

        public string GetDiagnosticsInfo() => $"8BitPerPixel image, Size: {Width}*{Height}, avg R:{r.Average(Convert.ToDecimal)} G:{g.Average(Convert.ToDecimal)} B:{b.Average(Convert.ToDecimal)} A:(has:{hasAlphaChannel}){a?.Average(Convert.ToDecimal) ?? -1}";


    }

    public class BitMaskPicture : INoAlphaPicture<bool>
    {
        [JsonIgnore()]
        public bool[] r { get; set; } = Array.Empty<bool>();
        [JsonIgnore()]
        public bool[] g { get; set; } = Array.Empty<bool>();
        [JsonIgnore()]
        public bool[] b { get; set; } = Array.Empty<bool>();

        public PicturePixelMode bitPerPixel => 1;

        public int Width { get; set; }
        public int Height { get; set; }
        public int Pixels { get; init; }
        public uint? frameIndex { get; set; }
        public string? filePath { get; set; }
        public PictureFlag Flag { get; set; }
        public List<PictureProcessStack> ProcessStack { get; set; }
        public bool hasAlphaChannel { get; set; }
        public bool Disposed { get; set; } = false;
        public bool CanBeDisposed { get; set; } = true;

        public BitMaskPicture()
        {
            ProcessStack = new List<PictureProcessStack>();
            PictureLifecycleTracker.RegisterCreated(this);
        }

        public string GetDiagnosticsInfo() => $"BitMaskPicture image, Size: {Width}*{Height}, avg R:{r.Average(v => v ? 1 : 0)} G:{g.Average(v => v ? 1 : 0)} B:{b.Average(v => v ? 1 : 0)}";


        public object? GetSpecificChannel(IPicture.ChannelId channelId)
        {
            return channelId switch
            {
                IPicture.ChannelId.Red => r,
                IPicture.ChannelId.Green => g,
                IPicture.ChannelId.Blue => b,
                _ => throw new ArgumentOutOfRangeException(nameof(channelId), "Invalid channel ID."),
            };
        }

        public IPicture<bool> Resize(int targetWidth, int targetHeight, bool preserveAspect = true)
        {
            throw new NotImplementedException();
        }

        public IPicture<bool> SetAlpha(bool haveAlpha)
        {
            throw new NotSupportedException($"Setting alpha channel is not supported for BitMaskPicture.");
        }

        public IPicture ToBitPerPixel(PicturePixelMode bitPerPixel)
        {
            if (bitPerPixel.Value == 1) return this;
            else return ToNormalPicture().ToBitPerPixel(bitPerPixel);
        }

        public Picture8bpp ToNormalPicture()
        {
            return new Picture8bpp(Width, Height)
            {
                r = r.Select(v => v ? (byte)255 : (byte)0).ToArray(),
                g = g.Select(v => v ? (byte)255 : (byte)0).ToArray(),
                b = b.Select(v => v ? (byte)255 : (byte)0).ToArray(),
                a = null,
                hasAlphaChannel = false,
                Width = Width,
                Height = Height,
                Pixels = Pixels,
                ProcessStack = ProcessStack.Append(new PictureProcessStack
                {
                    OperationDisplayName = "Converted from BitGrayscalePicture",
                    Operator = this.GetType(),
                    ProcessingFuncStackTrace = new StackTrace(true),
                }).ToList()

            };
        }

        IPicture IPicture.Resize(int targetWidth, int targetHeight, bool preserveAspect)
        {
            return Resize(targetWidth, targetHeight, preserveAspect);
        }

        private bool disposedValue;

        protected virtual void Dispose(bool disposing, bool force = false)
        {
            if (!force && (disposedValue || !CanBeDisposed)) return;
            lock (this)
            {
                if (!disposedValue)
                {
                    if (disposing)
                    {
                        r = null!;
                        g = null!;
                        b = null!;
                    }

                    disposedValue = true;
                    PictureLifecycleTracker.MarkDisposed(this);
                }
                Disposed = disposedValue;

            }
        }

        public void Dispose() => Dispose(force: true);

        public void Dispose(bool force = false)
        {
            Dispose(disposing: true,force);
            GC.SuppressFinalize(this);
        }
    }
    #endregion

    #region hdr
    /// <summary>
    /// The structure of a HDR Picture.
    /// </summary>
    public interface IHDRPicture<T> : IPicture<T>
    {
        /// <summary>
        /// Get the brightness for each pixel.
        /// </summary>
        /// <remarks>
        /// for each item, 1 means as same bright as <see cref="MaximumBrightness"/>; 
        /// 0 means no brightness (same as dark)
        /// Negative value is not accepted.
        /// </remarks>
        [JsonIgnore()]
        public float[] Brightness { get; set; }

        /// <summary>
        /// Get or set the maximum brightness (unit in nit) of this picture.
        /// </summary>
        public float MaximumBrightness { get; set; }
    }

    public class HDRPicture16bpp : Picture16bpp, IHDRPicture<ushort>
    {
        private const float DefaultHdrMaximumBrightness = 1000f;

        public HDRPicture16bpp(string imagePath) : base(imagePath)
        {
        }

        public HDRPicture16bpp(Image source) : base(source)
        {
        }

        public HDRPicture16bpp(IPicture<ushort> picture, bool copyData = false) : base(picture, copyData)
        {
        }

        public HDRPicture16bpp(int width, int height) : base(width, height)
        {
        }

        public float[] Brightness { get; set; } = Array.Empty<float>();
        public float MaximumBrightness { get; set; } = DefaultHdrMaximumBrightness;

        public new HDRPicture16bpp Resize(int targetWidth, int targetHeight, bool preserveAspect = true)
        {
            var sw = Stopwatch.StartNew();
            if (targetWidth == Width && targetHeight == Height) return this;

            lock (this)
            {
                if (targetWidth <= 0 || targetHeight <= 0) throw new ArgumentException("targetWidth and targetHeight must be positive");
                if (Width <= 0 || Height <= 0) throw new InvalidOperationException("Source image has invalid dimensions");

                int destW = targetWidth;
                int destH = targetHeight;

                if (preserveAspect)
                {
                    double sx = (double)targetWidth / Width;
                    double sy = (double)targetHeight / Height;
                    double s = Math.Min(sx, sy);
                    destW = Math.Max(1, (int)Math.Round(Width * s));
                    destH = Math.Max(1, (int)Math.Round(Height * s));
                    if (destW == Width && destH == Height) return this;
                }

                int dstPixels = checked(destW * destH);
                var result = new HDRPicture16bpp(destW, destH)
                {
                    r = new ushort[dstPixels],
                    g = new ushort[dstPixels],
                    b = new ushort[dstPixels],
                    a = hasAlphaChannel ? new float[dstPixels] : null,
                    hasAlphaChannel = hasAlphaChannel,
                    Brightness = new float[dstPixels],
                    MaximumBrightness = (MaximumBrightness > 0f && float.IsFinite(MaximumBrightness))
                        ? MaximumBrightness
                        : DefaultHdrMaximumBrightness,
                };

                float[]? sourceBrightness = (Brightness != null && Brightness.Length == Pixels) ? Brightness : null;
                bool hasBrightness = sourceBrightness != null;

                double xRatio = (double)Width / destW;
                double yRatio = (double)Height / destH;
                int srcArraySize = this.r.Length;

                for (int y = 0; y < destH; y++)
                {
                    double srcY = (y + 0.5) * yRatio - 0.5;
                    int y0 = (int)Math.Floor(srcY);
                    int y1 = y0 + 1;
                    double wy = srcY - y0;
                    if (y0 < 0)
                    {
                        y0 = 0; y1 = 0; wy = 0;
                    }
                    else if (y0 >= Height) { y0 = Height - 1; y1 = Height - 1; wy = 0; }
                    if (y1 >= Height) { y1 = Height - 1; }

                    for (int x = 0; x < destW; x++)
                    {
                        double srcX = (x + 0.5) * xRatio - 0.5;
                        int x0 = (int)Math.Floor(srcX);
                        int x1 = x0 + 1;
                        double wx = srcX - x0;
                        if (x0 < 0)
                        {
                            x0 = 0; x1 = 0; wx = 0;
                        }
                        else if (x0 >= Width) { x0 = Width - 1; x1 = Width - 1; wx = 0; }
                        if (x1 >= Width) { x1 = Width - 1; }

                        int k00 = Math.Max(0, Math.Min(srcArraySize - 1, y0 * Width + x0));
                        int k10 = Math.Max(0, Math.Min(srcArraySize - 1, y0 * Width + x1));
                        int k01 = Math.Max(0, Math.Min(srcArraySize - 1, y1 * Width + x0));
                        int k11 = Math.Max(0, Math.Min(srcArraySize - 1, y1 * Width + x1));

                        double r00 = this.r[k00];
                        double r10 = this.r[k10];
                        double r01 = this.r[k01];
                        double r11 = this.r[k11];

                        double g00 = this.g[k00];
                        double g10 = this.g[k10];
                        double g01 = this.g[k01];
                        double g11 = this.g[k11];

                        double b00 = this.b[k00];
                        double b10 = this.b[k10];
                        double b01 = this.b[k01];
                        double b11 = this.b[k11];

                        double rInterp = r00 * (1 - wx) * (1 - wy) + r10 * wx * (1 - wy) + r01 * (1 - wx) * wy + r11 * wx * wy;
                        double gInterp = g00 * (1 - wx) * (1 - wy) + g10 * wx * (1 - wy) + g01 * (1 - wx) * wy + g11 * wx * wy;
                        double bInterp = b00 * (1 - wx) * (1 - wy) + b10 * wx * (1 - wy) + b01 * (1 - wx) * wy + b11 * wx * wy;

                        int dstIdx = y * destW + x;
                        int rr = (int)Math.Round(rInterp);
                        int gg = (int)Math.Round(gInterp);
                        int bb = (int)Math.Round(bInterp);
                        if (rr < 0) rr = 0; if (rr > 65535) rr = 65535;
                        if (gg < 0) gg = 0; if (gg > 65535) gg = 65535;
                        if (bb < 0) bb = 0; if (bb > 65535) bb = 65535;

                        result.r[dstIdx] = (ushort)rr;
                        result.g[dstIdx] = (ushort)gg;
                        result.b[dstIdx] = (ushort)bb;

                        if (hasAlphaChannel && this.a != null)
                        {
                            double a00 = this.a[k00];
                            double a10 = this.a[k10];
                            double a01 = this.a[k01];
                            double a11 = this.a[k11];
                            double aInterp = a00 * (1 - wx) * (1 - wy) + a10 * wx * (1 - wy) + a01 * (1 - wx) * wy + a11 * wx * wy;
                            if (double.IsNaN(aInterp) || double.IsInfinity(aInterp)) aInterp = 1.0;
                            if (aInterp < 0) aInterp = 0; if (aInterp > 1) aInterp = 1;
                            result.a![dstIdx] = (float)aInterp;
                        }

                        double br00, br10, br01, br11;
                        if (sourceBrightness != null)
                        {
                            br00 = sourceBrightness[k00];
                            br10 = sourceBrightness[k10];
                            br01 = sourceBrightness[k01];
                            br11 = sourceBrightness[k11];
                        }
                        else
                        {
                            br00 = Math.Clamp((0.2627 * this.r[k00] + 0.6780 * this.g[k00] + 0.0593 * this.b[k00]) / 65535.0, 0.0, 1.0);
                            br10 = Math.Clamp((0.2627 * this.r[k10] + 0.6780 * this.g[k10] + 0.0593 * this.b[k10]) / 65535.0, 0.0, 1.0);
                            br01 = Math.Clamp((0.2627 * this.r[k01] + 0.6780 * this.g[k01] + 0.0593 * this.b[k01]) / 65535.0, 0.0, 1.0);
                            br11 = Math.Clamp((0.2627 * this.r[k11] + 0.6780 * this.g[k11] + 0.0593 * this.b[k11]) / 65535.0, 0.0, 1.0);
                        }

                        double brightnessInterp = br00 * (1 - wx) * (1 - wy) + br10 * wx * (1 - wy) + br01 * (1 - wx) * wy + br11 * wx * wy;
                        if (double.IsNaN(brightnessInterp) || double.IsInfinity(brightnessInterp)) brightnessInterp = 0.0;
                        result.Brightness[dstIdx] = (float)Math.Clamp(brightnessInterp, 0.0, 1.0);
                    }
                }

                result.ProcessStack.Add(new PictureProcessStack
                {
                    OperationDisplayName = "Resize (HDR IPicture)",
                    Operator = this.GetType(),
                    ProcessingFuncStackTrace = new(true),
                    Properties = new Dictionary<string, object>
                    {
                        { "SourceWidth", Width },
                        { "SourceHeight", Height },
                        { "TargetWidth", targetWidth },
                        { "TargetHeight", targetHeight },
                        { "PreserveAspect", preserveAspect },
                        { "MaximumBrightness", result.MaximumBrightness },
                        { "HasBrightnessChannel", hasBrightness },
                    },
                    Elapsed = sw.Elapsed
                });

                return result;
            }
        }

        public HDRPicture16bpp SetBrightnessOffset(double offset)
        {
            Brightness = Brightness.Select(br => (float)Math.Clamp(br + offset, 0.0, 1.0)).ToArray();
            return this;
        }


        public static HDRPicture16bpp GenerateSolidColor(int width, int height, ushort r, ushort g, ushort b, float? a, float brightness = 1f, float maximumBrightness = DefaultHdrMaximumBrightness)
        {
            int pixels = checked(width * height);
            float validBrightness = float.IsFinite(brightness) ? Math.Clamp(brightness, 0f, 1f) : 0f;
            float validMaximumBrightness = (maximumBrightness > 0f && float.IsFinite(maximumBrightness))
                ? maximumBrightness
                : DefaultHdrMaximumBrightness;

            var pic = new HDRPicture16bpp(width, height)
            {
                ProcessStack = new List<PictureProcessStack>
                {
                    new PictureProcessStack
                    {
                        OperationDisplayName = "GenerateSolidColor (HDR)",
                        Operator = typeof(HDRPicture16bpp),
                        ProcessingFuncStackTrace = new StackTrace(true),
                        Properties = new Dictionary<string, object>
                        {
                            { "Width", width },
                            { "Height", height },
                            { "R", r },
                            { "G", g },
                            { "B", b },
                            { "A", a ?? -1f },
                            { "Brightness", validBrightness },
                            { "MaximumBrightness", validMaximumBrightness },
                        },
                    }
                },
                MaximumBrightness = validMaximumBrightness,
                Brightness = Enumerable.Repeat(validBrightness, pixels).ToArray(),
            };

            pic.r = Enumerable.Repeat(r, pixels).ToArray();
            pic.g = Enumerable.Repeat(g, pixels).ToArray();
            pic.b = Enumerable.Repeat(b, pixels).ToArray();

            if (a != null)
            {
                pic.a = Enumerable.Repeat(Math.Clamp(a.Value, 0f, 1f), pixels).ToArray();
                pic.hasAlphaChannel = true;
            }
            else
            {
                pic.a = null;
                pic.hasAlphaChannel = false;
            }

            return pic;
        }

        public string GetDiagnosticsInfo() => $"HDR image, Size: {Width}*{Height}, avg R:{r.Average(Convert.ToDecimal)} G:{g.Average(Convert.ToDecimal)} B:{b.Average(Convert.ToDecimal)} A:(has:{hasAlphaChannel}){a?.Average(Convert.ToDecimal) ?? -1} L:{Brightness.Average()}(0..1), {Brightness.Average() * MaximumBrightness}nit";

        private const float SdrReferenceNits = 100f;
        private const float ToneMapKnee = 1.5f;
        private const float OutputGamma = 2.2f;
        private const float LumaEpsilon = 1e-6f;

        /// <summary>
        /// Convert this HDR picture to a standard SDR <see cref="Picture16bpp"/>.
        /// </summary>
        /// <param name="mode">
        /// The degradation strategy:
        /// <see cref="HDRImageDegradeToSDRMode.NormalizeBrightnessToRGB"/> applies a knee-based tone map
        /// driven by the brightness channel and maximum brightness;
        /// <see cref="HDRImageDegradeToSDRMode.OverlayMaskFromBrightness"/> multiplies RGB by the brightness value;
        /// <see cref="HDRImageDegradeToSDRMode.DiscardBrightnessChannel"/> copies RGB unchanged.
        /// </param>
        public Picture16bpp DegradeToSDR(HDRImageDegradeToSDRMode? DegradeMode = null)
        {
            var mode = DegradeMode ?? PictureExtensions.DefaultHDRImageDegradeToSDRMode;
            var sw = Stopwatch.StartNew();
            if (mode == HDRImageDegradeToSDRMode.DisallowDowngrade)
                throw new InvalidOperationException($"HDR to SDR degrade is disabled. Mode: {mode}.");

            var result = new Picture16bpp(Width, Height)
            {
                frameIndex = frameIndex,
                filePath = filePath,
            };

            if (hasAlphaChannel && a != null)
            {
                result.a = new float[Pixels];
                Array.Copy(a, result.a, Pixels);
                result.hasAlphaChannel = true;
            }

            result.ProcessStack = new List<PictureProcessStack>(ProcessStack);

            bool hasBrightness = Brightness != null && Brightness.Length == Pixels;

            float validMaximumBrightness = MaximumBrightness > 0f && float.IsFinite(MaximumBrightness)
                ? MaximumBrightness
                : SdrReferenceNits;

            for (int i = 0; i < Pixels; i++)
            {
                if (!hasBrightness || mode == HDRImageDegradeToSDRMode.DiscardBrightnessChannel)
                {
                    result.r[i] = r[i];
                    result.g[i] = g[i];
                    result.b[i] = b[i];
                    continue;
                }

                float pixelBrightness = Brightness[i];

                switch (mode)
                {
                    case HDRImageDegradeToSDRMode.NormalizeBrightnessToRGB:
                        MapHDRToSDR(r[i], g[i], b[i], pixelBrightness, validMaximumBrightness,
                            out var mappedR, out var mappedG, out var mappedB);
                        result.r[i] = mappedR;
                        result.g[i] = mappedG;
                        result.b[i] = mappedB;
                        break;

                    case HDRImageDegradeToSDRMode.OverlayMaskFromBrightness:
                        float mask = Math.Clamp(pixelBrightness, 0f, 1f);
                        result.r[i] = (ushort)Math.Clamp((int)Math.Round(r[i] * mask), 0, 65535);
                        result.g[i] = (ushort)Math.Clamp((int)Math.Round(g[i] * mask), 0, 65535);
                        result.b[i] = (ushort)Math.Clamp((int)Math.Round(b[i] * mask), 0, 65535);
                        break;

                    default:
                        throw new ArgumentOutOfRangeException(nameof(mode), mode, "Unknown HDR degrade mode.");
                }
            }

            result.ProcessStack.Add(new PictureProcessStack
            {
                OperationDisplayName = "DegradeToSDR",
                Operator = typeof(HDRPicture16bpp),
                ProcessingFuncStackTrace = new StackTrace(true),
                Properties = new Dictionary<string, object>
                {
                    { "Mode", mode.ToString() },
                    { "MaximumBrightness", MaximumBrightness },
                },
                Elapsed = sw.Elapsed,
            });

            return result;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static void MapHDRToSDR(ushort sourceR, ushort sourceG, ushort sourceB, float brightness, float maximumBrightness, out ushort mappedR, out ushort mappedG, out ushort mappedB)
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
            if (sourceSignalLuma <= LumaEpsilon)
            {
                mappedR = sourceR;
                mappedG = sourceG;
                mappedB = sourceB;
                return;
            }

            float relativeToSdrWhite = Math.Max(0f, brightness) * (maximumBrightness / SdrReferenceNits);
            float toneMappedLinearLuma = (relativeToSdrWhite * ToneMapKnee) / (1f + relativeToSdrWhite * ToneMapKnee);
            toneMappedLinearLuma = Math.Clamp(toneMappedLinearLuma, 0f, 1f);
            float targetSignalLuma = MathF.Pow(toneMappedLinearLuma, 1f / OutputGamma);
            float gain = targetSignalLuma / sourceSignalLuma;

            r = Math.Clamp(r * gain, 0f, 1f);
            g = Math.Clamp(g * gain, 0f, 1f);
            b = Math.Clamp(b * gain, 0f, 1f);

            mappedR = (ushort)Math.Clamp((int)Math.Round(r * 65535f), 0, 65535);
            mappedG = (ushort)Math.Clamp((int)Math.Round(g * 65535f), 0, 65535);
            mappedB = (ushort)Math.Clamp((int)Math.Round(b * 65535f), 0, 65535);
        }

    }
    #endregion
}
