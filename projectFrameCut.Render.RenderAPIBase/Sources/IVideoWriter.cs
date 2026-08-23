using projectFrameCut.Drawing.Base;
using projectFrameCut.Drawing.Base.Picture;

namespace projectFrameCut.Render.RenderAPIBase.Sources
{
    public interface IVideoWriter : IDisposable
    {
        public int Width { get; set; }
        public int Height { get; set; }
        public string OutputPath { get; set; }
        public int FramePerSecond { get; set; }
        public string CodecName { get; set; }
        public string PixelFormat { get; set; }
        /// <summary>
        /// Bitrate for the encoded video stream, in bits per second.
        /// Set before calling <see cref="Initialize"/>; changes after initialization are ignored.
        /// </summary>
        public long BitRate { get; set; }

        /// <summary>
        /// Indicates whether the encoder should prioritize encoding speed over compression efficiency.
        /// </summary>
        public bool PreferToSpeed { get; set; }

        /// <summary>
        /// Metadata key-value pairs to write into the output container.
        /// Set before calling <see cref="Initialize"/>; changes after initialization may be ignored.
        /// </summary>
        Dictionary<string, string>? Metadata { get; set; }

        /// <summary>
        /// Gets the total duration of video written so far, in frames.
        /// </summary>
        public uint DurationWritten { get; }


        public IPicture.PicturePixelMode? TargetPPB { get; }

        public void Initialize();
        public virtual bool TryInitialize()
        {
            try
            {
                Initialize();
                return true;

            }
            catch
            {
                return false;
            }
        }


        public bool SupportCodec(string codecName);
        public void Finish();


        public void Append(IPicture<ushort> picture);
        public void Append(IPicture<byte> picture);
        public void Append(HDRPicture16bpp pic) => Append((IPicture<ushort>)pic);

        public void Append(Picture16bpp pic) => Append((IPicture<ushort>)pic);
        public void Append(Picture8bpp pic) => Append((IPicture<byte>)pic);
        public void Append(IPicture source)
        {
            ArgumentNullException.ThrowIfNull(source);
            if (source.BitPerPixel == 16) Append((IPicture<ushort>)source);
            else if (source.BitPerPixel == 8) Append((IPicture<byte>)source);
            else throw new NotSupportedException($"Unsupported pixel mode.");
        }


    }
}
