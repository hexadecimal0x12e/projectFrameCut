using SixLabors.ImageSharp;
using SixLabors.ImageSharp.Formats;
using SixLabors.ImageSharp.Formats.Png;
using SixLabors.ImageSharp.PixelFormats;
using System.Buffers.Binary;
using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.IO;
using System.IO.Compression;
using System.Text.Json.Serialization;
using System.Threading.Channels;

namespace projectFrameCut.Shared
{
    /// <summary>
    /// The projectFrameCut's 16-bit Picture structure. It's the base of everything you see in the final video.
    /// </summary>
    public class Picture : IDisposable
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

        public bool hasAlphaChannel { get; set; } = false;

       

        /// <summary>
        /// Initializes a new instance of the Picture class by copying the properties of an existing Picture.
        /// </summary>
        /// <remarks>The new Picture instance shares the same pixel data reference as the source Picture.
        /// Changes to the pixel data in one instance will affect the other.</remarks>
        /// <param name="picture">The Picture instance to copy the width, height, and pixel data from. Cannot be null.</param>
        public Picture(Picture picture)
        {
            Width = picture.Width;
            Height = picture.Height;
            Pixels = picture.Pixels;
        }

        /// <summary>
        /// Initializes a new instance of the Picture class with the specified width and height.
        /// </summary>
        /// <param name="width">The width of the picture, in pixels. Must be a non-negative integer.</param>
        /// <param name="height">The height of the picture, in pixels. Must be a non-negative integer.</param>
        public Picture(int width, int height)
        {
            Width = width;
            Height = height;
            Pixels = checked(width * height);
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
        public Picture(string imagePath)
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
        }

        public Picture(SixLabors.ImageSharp.Image source)
        {
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
        }

        public Picture SetAlpha(bool haveAlpha)
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

        

        [DebuggerNonUserCode()]
        public void SaveAsPng16bpc(string path, IImageEncoder? imageEncoder = null)
        {
            lock (this)
            {
                //if (string.IsNullOrWhiteSpace(path)) throw new ArgumentException("path is null or empty", nameof(path));
                //if (width <= 0 || height <= 0) throw new ArgumentException("width and height must be positive");
                //if (checked(width * height) != r.Length) throw new ArgumentException("width * height must equal pixels");

                imageEncoder = imageEncoder ?? new PngEncoder()
                {
                    BitDepth = PngBitDepth.Bit16
                };

                int idx = 0, lineStart = 0;

                if (hasAlphaChannel && (a != null && a.Length >= Pixels))
                {
                    using (var img = new Image<Rgba64>(Width, Height))
                    {
                        for (int y = 0; y < Height; y++)
                        {
                            for (int x = 0; x < Width; x++)
                            {
                                idx = y * Width + x;
                                ushort rr = (r != null && r.Length > idx) ? r[idx] : (ushort)0;
                                ushort gg = (g != null && g.Length > idx) ? g[idx] : (ushort)0;
                                ushort bb = (b != null && b.Length > idx) ? b[idx] : (ushort)0;
                                float aval = (hasAlphaChannel && a != null && a.Length > idx) ? a[idx] : 1f;
                                if (float.IsNaN(aval) || float.IsInfinity(aval)) aval = 1f;
                                int ai = (int)Math.Round(aval * 65535f);
                                if (ai < 0) ai = 0;
                                if (ai > 65535) ai = 65535;
                                ushort aa = (ushort)ai;

                                img[x, y] = new Rgba64(rr, gg, bb, aa);
                            }
                        }
                        img.Save(path, imageEncoder);
                        return;
                    }
                }
                else
                {
                    using (var img = new Image<Rgb48>(Width, Height))
                    {
                        for (int y = 0; y < Height; y++)
                        {
                            lineStart = y * Width;
                            for (int x = 0; x < Width; x++)
                            {
                                idx = lineStart + x;
                                img[x, y] = new Rgb48(r[idx], g[idx], b[idx]);

                            }
                        }

                        img.Save(path, imageEncoder);
                        return;
                    }
                }
            }
        }

        [DebuggerNonUserCode()]
        public void SaveAsPng8bpc(string path, IImageEncoder? imageEncoder = null)
        {
            lock (this)
            {
                //if (string.IsNullOrWhiteSpace(path)) throw new ArgumentException("path is null or empty", nameof(path));
                //if (width <= 0 || height <= 0) throw new ArgumentException("width and height must be positive");
                //if (checked(width * height) != r.Length) throw new ArgumentException("width * height must equal pixels");

                imageEncoder = imageEncoder ?? new PngEncoder()
                {
                    BitDepth = PngBitDepth.Bit8
                };

                int idx = 0, lineStart = 0;

                if (hasAlphaChannel && (a != null && a.Length >= Pixels))
                {
                    using (var img = new Image<Rgba32>(Width, Height))
                    {
                        for (int y = 0; y < Height; y++)
                        {
                            for (int x = 0; x < Width; x++)
                            {
                                idx = y * Width + x;
                                byte rr = (byte)((r != null && r.Length > idx) ? r[idx] / 257 : 0);
                                byte gg = (byte)((g != null && g.Length > idx) ? g[idx] / 257 : 0);
                                byte bb = (byte)((b != null && b.Length > idx) ? b[idx] / 257 : 0);
                                float aval = (hasAlphaChannel && a != null && a.Length > idx) ? a[idx] : 1f;
                                if (float.IsNaN(aval) || float.IsInfinity(aval)) aval = 1f;
                                int ai = (int)Math.Round(aval * 255f);
                                if (ai < 0) ai = 0;
                                if (ai > 255) ai = 255;
                                byte aa = (byte)ai;

                                img[x, y] = new Rgba32(rr, gg, bb, aa);
                            }
                        }
                        img.Save(path, imageEncoder);
                        return;
                    }
                }
                else
                {
                    using (var img = new Image<Rgb24>(Width, Height))
                    {
                        for (int y = 0; y < Height; y++)
                        {
                            lineStart = y * Width;
                            for (int x = 0; x < Width; x++)
                            {
                                idx = lineStart + x;
                                byte rr = (byte)(r[idx] / 257);
                                byte gg = (byte)(g[idx] / 257);
                                byte bb = (byte)(b[idx] / 257);
                                img[x, y] = new Rgb24(rr, gg, bb);

                            }
                        }

                        img.Save(path, imageEncoder);
                        return;
                    }
                }
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
        public Picture Resize(int targetWidth, int targetHeight, bool preserveAspect = true)
        {
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
                }

                if (destW == Width && destH == Height) return this;

                var result = new Picture(destW, destH);
                int dstPixels = checked(destW * destH);
                result.r = new ushort[dstPixels];
                result.g = new ushort[dstPixels];
                result.b = new ushort[dstPixels];
                result.a = hasAlphaChannel ? new float[dstPixels] : null;
                result.hasAlphaChannel = hasAlphaChannel;

                double xRatio = (double)Width / destW;
                double yRatio = (double)Height / destH;

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
                        if (x1 >= Width) { x1 = Width - 1; }

                        int k00 = y0 * Width + x0;
                        int k10 = y0 * Width + x1;
                        int k01 = y1 * Width + x0;
                        int k11 = y1 * Width + x1;

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

                return result;
            }
        }

     

        public static Picture GenerateSolidColor(int width,int height,ushort r, ushort g, ushort b, float? a)
        {
            var pic = new Picture(width, height);
            pic.r = Enumerable.Repeat(r, pic.Pixels).ToArray();
            pic.g = Enumerable.Repeat(g, pic.Pixels).ToArray();
            pic.b = Enumerable.Repeat(b, pic.Pixels).ToArray();
            if(a != null)
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
            }
        }

        public void Dispose()
        {
            Dispose(disposing: true);
            GC.SuppressFinalize(this);
        }
    }
}
