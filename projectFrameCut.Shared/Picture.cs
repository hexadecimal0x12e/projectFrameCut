using SixLabors.ImageSharp;
using SixLabors.ImageSharp.Formats;
using SixLabors.ImageSharp.Formats.Png;
using SixLabors.ImageSharp.PixelFormats;
using SixLabors.ImageSharp.Processing;
using System;
using System.Buffers.Binary;
using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Numerics;
using System.Text.Json.Serialization;
using System.Threading.Channels;
using static projectFrameCut.Shared.IPicture;
using Image = SixLabors.ImageSharp.Image;

namespace projectFrameCut.Shared
{


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
        public uint? frameIndex { get; init; } //诊断用
        /// <summary>
        /// The file path this picture comes from. Used for diagnostics only.
        /// </summary>
        public string? filePath { get; init; } //诊断用
        /// <summary>
        /// Determine some flag for the picture.
        /// </summary>
        public PictureFlag Flag { get; set; }
        /// <summary>
        /// Records each step of the image processed.
        /// </summary>
        /// <remarks>
        /// If you're developing a Plugin that implements image processing, please append your processing step information to this property.
        /// </remarks>
        public string? ProcessStack { get; set; }
        /// <summary>
        /// Indicates whether this picture has an alpha channel.
        /// </summary>
        public bool hasAlphaChannel { get; set; }
        /// <summary>
        /// Get whether this picture has been disposed.
        /// </summary>
        /// <remarks>
        /// if <see cref="Disposed"/> is null, this picture'll never be disposed.
        /// </remarks>
        public bool? Disposed { get; set; }

        /// <summary>
        /// Represents the internal SixLabors.ImageSharp.Image representation of this picture, if available.
        /// </summary>
        [JsonIgnore()]
        public Image? SixLaborsImage { get; set; }

        /// <summary>
        /// Resize the picture. 
        /// </summary>
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

        public enum ChannelId
        {
            Red = 0,
            Green = 1,
            Blue = 2,
            Alpha = 3
        }

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
        [JsonIgnore()]
        public T[] r { get; set; }
        [JsonIgnore()]
        public T[] g { get; set; }
        [JsonIgnore()]
        public T[] b { get; set; }
        [JsonIgnore()]
        [NotNull()]
        public float[]? a { get; set; }

        public IPicture<T> SetAlpha(bool haveAlpha);
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

    /// <summary>
    /// This class is for compatibility with some pretty old codes (mostly written before the main application appears in the Git repository). It's basically equals to <see cref="Picture16bpp"/>.
    /// </summary>
    [DebuggerDisplay("ProcessStack: {ProcessStack}")]
    public class Picture : Picture16bpp
    {
        public Picture(IPicture<ushort> picture) : base(picture)
        {
        }

        public Picture(string imagePath) : base(imagePath)
        {
        }

        public Picture(Image source) : base(source)
        {
        }

        public Picture(int width, int height) : base(width, height)
        {
        }
    }

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

        public uint? frameIndex { get; init; } //诊断用
        public string? filePath { get; init; } //诊断用
        public PictureFlag Flag { get; set; }
        public string? ProcessStack { get; set; }
        public bool? Disposed { get; set; } = false;

        public bool hasAlphaChannel { get; set; } = false;

        public PicturePixelMode bitPerPixel => 16;
        [JsonIgnore()]
        public Image? SixLaborsImage { get; set; }

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


            ProcessStack = $"Created from another, {Width}*{Height}, data {(copyData ? "copied" : "uncopied")},\r\n'{picture.ProcessStack}'\r\n";
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
            ProcessStack = $"Created from scratch, {Width}*{Height}\r\n";

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
            ProcessStack = $"Created from file '{imagePath}', {Width}*{Height}\r\n";

        }

        /// <summary>
        /// Create a new Picture from a SixLabors.ImageSharp.Image source.
        /// </summary>
        /// <param name="source"></param>
        /// <exception cref="ArgumentNullException"></exception>
        [DebuggerNonUserCode()]
        public Picture16bpp(SixLabors.ImageSharp.Image source)
        {
            if (source == null) throw new ArgumentNullException(nameof(source));
            Width = source.Width;
            Height = source.Height;
            Pixels = checked(Width * Height);
            SixLaborsImage = source;
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
            ProcessStack = $"Created from SixLabors.ImageSharp.Image, {Width}*{Height}\r\n";

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
        //[DebuggerNonUserCode()]
        public Picture16bpp Resize(int targetWidth, int targetHeight, bool preserveAspect = true)
        {
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

                var result = new Picture(destW, destH);
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
                result.ProcessStack = $"{ProcessStack}\r\nResize to {targetWidth}*{targetHeight}\r\n";

                return result;
            }
        }

        public static Picture GenerateSolidColor(int width, int height, ushort r, ushort g, ushort b, float? a)
        {
            var pic = new Picture(width, height)
            {
                ProcessStack = $"SolidColor, {width}*{height}, rgba:{r},{g},{b},{(a is not null ? $"{a}" : "ff")}\r\n"
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

        protected virtual void Dispose(bool disposing)
        {
            if (disposedValue || Disposed is null) return;
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
                }
                Disposed = disposedValue;
            }
        }

        public void Dispose()
        {
            Dispose(disposing: true);
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
                var pic = new Picture8bpp(Width, Height)
                {
                    frameIndex = this.frameIndex,
                    filePath = this.filePath,
                    hasAlphaChannel = this.hasAlphaChannel
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

        public string GetDiagnosticsInfo() => $"16BitPerPixel image, Size: {Width}*{Height}, avg R:{r.Average(Convert.ToDecimal)} G:{g.Average(Convert.ToDecimal)} B:{b.Average(Convert.ToDecimal)} A:{a?.Average(Convert.ToDecimal) ?? -1}, \r\nProcessStack:\r\n{ProcessStack}";
    }

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

        public uint? frameIndex { get; init; } //诊断用
        public string? filePath { get; init; } //诊断用
        public PictureFlag Flag { get; set; }
        public string? ProcessStack { get; set; }
        public bool? Disposed { get; set; } = false;

        public bool hasAlphaChannel { get; set; } = false;

        public PicturePixelMode bitPerPixel => 8;
        [JsonIgnore()]
        public Image? SixLaborsImage { get; set; }


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


            ProcessStack = $"Created from another, {Width}*{Height}, data {(copyData ? "copied" : "uncopied")},\r\n'{picture.ProcessStack}'\r\n";
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
            ProcessStack = $"Created from scratch, {Width}*{Height}\r\n";

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
            ProcessStack = $"Created from file '{imagePath}', {Width}*{Height}\r\n";

        }

        public Picture8bpp(SixLabors.ImageSharp.Image source)
        {
            if (source == null) throw new ArgumentNullException(nameof(source));
            Width = source.Width;
            Height = source.Height;
            Pixels = checked(Width * Height);
            SixLaborsImage = source;
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
            ProcessStack = $"Created from SixLabors.ImageSharp.Image, {Width}*{Height}\r\n";

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
                result.ProcessStack = $"{ProcessStack}\r\nResize to {targetWidth}*{targetHeight}\r\n";
                return result;
            }
        }

        public static Picture8bpp GenerateSolidColor(int width, int height, byte r, byte g, byte b, float? a)
        {
            var pic = new Picture8bpp(width, height)
            {
                ProcessStack = $"SolidColor, {width}*{height}, rgba:{r},{g},{b},{(a is not null ? $"{a}" : "ff")}\r\n"
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

        protected virtual void Dispose(bool disposing)
        {
            if (disposedValue || Disposed is null) return;
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
                }
                Disposed = disposedValue;

            }
        }

        public void Dispose()
        {
            Dispose(disposing: true);
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
                var pic = new Picture(Width, Height)
                {
                    frameIndex = this.frameIndex,
                    filePath = this.filePath,
                    hasAlphaChannel = this.hasAlphaChannel
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

        public string GetDiagnosticsInfo() => $"8BitPerPixel image, Size: {Width}*{Height}, avg R:{r.Average(Convert.ToDecimal)} G:{g.Average(Convert.ToDecimal)} B:{b.Average(Convert.ToDecimal)} A:{a?.Average(Convert.ToDecimal) ?? -1}, \r\nProcessStack:\r\n{ProcessStack}";


    }



    public static class PictureExtensions
    {
        public static IPicture DeepCopy(this IPicture source)
        {
            if (source is null) throw new ArgumentNullException(nameof(source));
            if (source.Disposed is not null && source.Disposed.Value) throw new ObjectDisposedException(nameof(source));
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
                            ProcessStack = s16.ProcessStack,
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
                            ProcessStack = source.ProcessStack,
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
                            ProcessStack = s8.ProcessStack,
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
                            ProcessStack = source.ProcessStack,
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

                        return dst;
                    }
                }
                else
                {
                    throw new NotSupportedException("Only 8bpp and 16bpp images are supported for deep copy.");
                }
            }
        }

        public static bool UseSixLaborsImageSharpToResize = true;

        public static Picture8bpp Resize(this Picture8bpp source, int targetWidth, int targetHeight, bool preserveAspect = true)
        {
            if (!UseSixLaborsImageSharpToResize) return source.Resize(targetWidth, targetHeight, preserveAspect);
            var img = source.SaveToSixLaborsImage();
            img.Mutate(i => i.Resize(new ResizeOptions
            {
                Size = new Size(targetWidth, targetHeight),
                Mode = preserveAspect ? ResizeMode.Max : ResizeMode.Stretch
            }));
            return new Picture8bpp(img);

        }
        public static Picture16bpp Resize(this Picture16bpp source, int targetWidth, int targetHeight, bool preserveAspect = true)
        {
            if (!UseSixLaborsImageSharpToResize) return source.Resize(targetWidth, targetHeight, preserveAspect);
            var img = source.SaveToSixLaborsImage();
            img.Mutate(i => i.Resize(new ResizeOptions
            {
                Size = new Size(targetWidth, targetHeight),
                Mode = preserveAspect ? ResizeMode.Max : ResizeMode.Stretch
            }));
            return new Picture16bpp(img);

        }

        public static void LogPicInfo(this IPicture<ushort> src)
        {
            Logger.LogDiagnostic($"16BitPerPixel image, Size: {src.Width}*{src.Height}, avg R:{src.r.Average(Convert.ToDecimal)} G:{src.g.Average(Convert.ToDecimal)} B:{src.b.Average(Convert.ToDecimal)} A:{src.a?.Average(Convert.ToDecimal) ?? -1}, \r\nProcessStack:\r\n{src.ProcessStack}");

        }
        public static void LogPicInfo(this IPicture<byte> src)
        {
            Logger.LogDiagnostic($"8BitPerPixel image,Size: {src.Width}*{src.Height}, avg R:{src.r.Average(Convert.ToDecimal)} G:{src.g.Average(Convert.ToDecimal)} B:{src.b.Average(Convert.ToDecimal)} A:{src.a?.Average(Convert.ToDecimal) ?? -1}, \r\nProcessStack:\r\n{src.ProcessStack}");
        }

        [DebuggerStepThrough()]
        public static void SaveAsPng16bpp(this IPicture image, string path, IImageEncoder? imageEncoder = null) //compatibility
            => SaveAsPng(image, path, 16, null, imageEncoder);

        //[DebuggerStepThrough()]
        public static void SaveAsPng8bpp(this IPicture image, string path, IImageEncoder? imageEncoder = null)
            => SaveAsPng(image, path, 8, null, imageEncoder);


        //[DebuggerStepThrough()]
        public static void SaveAsPng(this IPicture image, string path, int resultPPB = 16, bool? saveAlpha = null, IImageEncoder? imageEncoder = null)
        {
            if (Debugger.IsAttached || MyLoggerExtensions.LoggingDiagnosticInfo)
            {
                if (image is Picture16bpp p1) p1.LogPicInfo();
                else if (image is Picture8bpp p2) p2.LogPicInfo();
                else Logger.LogDiagnostic("Unknown picture type, cannot get info.");
            }
            ArgumentException.ThrowIfNullOrWhiteSpace(path, nameof(path));
            imageEncoder = imageEncoder ?? DefaultEncoder;
            image.SaveToSixLaborsImage(resultPPB, saveAlpha).Save(path, imageEncoder);
        }

        //[DebuggerStepThrough()]
        public static Image SaveToSixLaborsImage(this IPicture image, int resultPPB = 16, bool? saveAlpha = null, bool force = false)
        {
            if (image.SixLaborsImage is not null && !force) return image.SixLaborsImage;
            lock (image)
            {
                IEnumerable<float> aa = image.hasAlphaChannel ? image.GetSpecificChannel(IPicture.ChannelId.Alpha) as float[] : null;
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
                        if (aa is null) aa = Enumerable.Repeat(1f, image.Pixels);
                        result = _SaveToInternal16bppWithAlpha(image, rr, gg, bb, aa);
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
                        if (aa is null) aa = Enumerable.Repeat(1f, image.Pixels);
                        result = _SaveToInternal8bppWithAlpha(image, rr, gg, bb, aa);
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
                image.SixLaborsImage = result;
                return result;
            }
        }

        private static IImageEncoder DefaultEncoder = new PngEncoder()
        {
            BitDepth = PngBitDepth.Bit16
        };

        private static T readNextFromEnumerator<T>(IEnumerator<T> en)
        {
            if (en.MoveNext())
            {
                return en.Current;
            }
            else
            {
                if (Debugger.IsAttached) Debugger.Break();
                throw new InvalidOperationException("The source enumerable is empty.");
            }
        }
        [DebuggerStepThrough()]
        private static Image _SaveToInternal16bppWithAlpha(IPicture image, IEnumerable<ushort> rr, IEnumerable<ushort> gg, IEnumerable<ushort> bb, IEnumerable<float> aa)
        {
            var result = new Image<Rgba64>(image.Width, image.Height);
            var r = rr.GetEnumerator();
            var g = gg.GetEnumerator();
            var b = bb.GetEnumerator();
            var a = aa.GetEnumerator();
            int x = 0, y = 0;
            for (int i = 0; i < image.Pixels; i++)
            {
                result[x, y] = new Rgba64
                {
                    R = readNextFromEnumerator(r),
                    G = readNextFromEnumerator(g),
                    B = readNextFromEnumerator(b),
                    A = (ushort)(Math.Clamp(readNextFromEnumerator(a), 0f, 1f) * 65535f)
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
        private static Image _SaveToInternal16bppWithNoAlpha(IPicture image, IEnumerable<ushort> rr, IEnumerable<ushort> gg, IEnumerable<ushort> bb)
        {
            var result = new Image<Rgb48>(image.Width, image.Height);
            var r = rr.GetEnumerator();
            var g = gg.GetEnumerator();
            var b = bb.GetEnumerator();
            int x = 0, y = 0;
            for (int i = 0; i < image.Pixels; i++)
            {
                result[x, y] = new Rgb48
                {
                    R = readNextFromEnumerator(r),
                    G = readNextFromEnumerator(g),
                    B = readNextFromEnumerator(b),
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
        private static Image _SaveToInternal8bppWithAlpha(IPicture image, IEnumerable<byte> rr, IEnumerable<byte> gg, IEnumerable<byte> bb, IEnumerable<float> aa)
        {
            var result = new Image<Rgba32>(image.Width, image.Height);
            var r = rr.GetEnumerator();
            var g = gg.GetEnumerator();
            var b = bb.GetEnumerator();
            var a = aa.GetEnumerator();
            int x = 0, y = 0;
            for (int i = 0; i < image.Pixels; i++)
            {
                result[x, y] = new Rgba32
                {
                    R = readNextFromEnumerator(r),
                    G = readNextFromEnumerator(g),
                    B = readNextFromEnumerator(b),
                    A = (byte)(Math.Clamp(readNextFromEnumerator(a), 0f, 1f) * 255f)
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
        private static Image _SaveToInternal8bppWithNoAlpha(IPicture image, IEnumerable<byte> rr, IEnumerable<byte> gg, IEnumerable<byte> bb)
        {
            var result = new Image<Rgb24>(image.Width, image.Height);
            var r = rr.GetEnumerator();
            var g = gg.GetEnumerator();
            var b = bb.GetEnumerator();
            int x = 0, y = 0;
            for (int i = 0; i < image.Pixels; i++)
            {
                result[x, y] = new Rgb24
                {
                    R = readNextFromEnumerator(r),
                    G = readNextFromEnumerator(g),
                    B = readNextFromEnumerator(b),
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
}
