
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.Formats;
using SixLabors.ImageSharp.Formats.Png;
using SixLabors.ImageSharp.PixelFormats;
using System.Buffers.Binary;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Threading.Channels;

namespace projectFrameCut.Render
{
    public class Picture
    {
        public ushort[] r { get; set; } = Array.Empty<ushort>();
        public ushort[] g { get; set; } = Array.Empty<ushort>();
        public ushort[] b { get; set; } = Array.Empty<ushort>();
        public float[]? a { get; set; } = null;
        public int Width { get; set; }
        public int Height { get; set; }
        public int Pixels { get; init; }

        public uint? frameIndex { get; init; } //诊断用

        public bool hasAlphaChannel { get; set; } = false;

        //public Picture()
        //{
        //    Width = 0;
        //    Height = 0;
        //}

        public Picture(Picture picture)
        {
            Width = picture.Width;
            Height = picture.Height;
            Pixels = picture.Pixels;
        }

        public Picture(int width, int height)
        {
            Width = width;
            Height = height;
            Pixels = checked(width * height);
        }

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
        }

        public Picture SetAlpha(bool haveAlpha)
        {
            if(haveAlpha == hasAlphaChannel)
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

        public void EnsureAlpha()
        {
            if (!hasAlphaChannel || a == null || a.Length != Pixels)
            {
                a = Enumerable.Repeat(1f, Pixels).ToArray();
                hasAlphaChannel = true;
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

        [DebuggerNonUserCode()]
        public void SaveAsPng8bpc(string path, IImageEncoder? imageEncoder = null)
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
                using (var img = new Image<Rgba32>(Width, Height))
                {
                    for (int y = 0; y < Height; y++)
                    {
                        for (int x = 0; x < Width; x++)
                        {
                            idx = y * Width + x;
                            ushort rr = (ushort)((r != null && r.Length > idx) ? r[idx] / 257 : 0);
                            ushort gg = (ushort)((g != null && g.Length > idx) ? g[idx] / 257 : 0);
                            ushort bb = (ushort)((b != null && b.Length > idx) ? b[idx] / 257 : 0);
                            float aval = (hasAlphaChannel && a != null && a.Length > idx) ? a[idx] : 1f;
                            if (float.IsNaN(aval) || float.IsInfinity(aval)) aval = 1f;
                            int ai = (int)Math.Round(aval * 255f);
                            if (ai < 0) ai = 0;
                            if (ai > 255) ai = 255;
                            ushort aa = (ushort)ai;

                            img[x, y] = new Rgba32(rr, gg, bb, aa);
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
                            img[x, y] = new Rgb48((ushort)(r[idx] / 257), (ushort)(g[idx] / 257), (ushort)(b[idx] / 257));

                        }
                    }

                    img.Save(path, imageEncoder);
                    return;
                }
            }
        }
    }
}
