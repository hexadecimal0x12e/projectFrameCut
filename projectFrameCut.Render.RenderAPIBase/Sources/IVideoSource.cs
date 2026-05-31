using projectFrameCut.Shared;
using projectFrameCut.Render.RenderAPIBase.Plugins;
using System;
using System.Collections.Generic;
using System.Text;

namespace projectFrameCut.Render.RenderAPIBase.Sources
{
    /// <summary>
    /// The interface for video source (decoder) implementations.
    /// </summary>
    public interface IVideoSource : IDisposable
    {
        /// <summary>
        /// Get the type name of this decoder. Should be the same as the class name.
        /// </summary>
        public string TypeName { get; }

        /// <summary>
        /// Initialize the video source. This method should prepare the video source for frame extraction.
        /// </summary>
        /// <remarks>
        /// If the file path is null, please just return without doing anything. 
        /// This is because <see cref="IPluginBase.VideoSourceCreator"/> need an instance of this to get <see cref="PreferredExtension"/> to determine which plugin to use.
        /// </remarks>
        public abstract void Initialize();
        /// <summary>
        /// Try to initialize the video source. Returns true if successful, false otherwise.
        /// </summary>
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
        /// <summary>
        /// Create a new instance of the video source with a different source.
        /// </summary>

        public IVideoSource CreateNew(string newSource);

        /// <summary>
        /// Read the actual frame from the video source.
        /// </summary>
        /// <param name="targetFrame">the frame index to read</param>
        /// <param name="hasAlpha">keep the alpha channel if true</param>
        /// <returns>the frame</returns>
        abstract IPicture GetFrame(uint targetFrame, bool hasAlpha = false);
        /// <summary>
        /// The <see cref="GetFrame(uint, bool)"/> return's <seealso cref="IPicture.BitPerPixel"/> of the result frames.
        /// Return null if unknown or variable.
        /// </summary>
        public int? ResultBitPerPixel { get; }
        /// <summary>
        /// The preferred file extensions for this video source.
        /// </summary>
        /// <remarks>
        /// For each extension, you'll need to add a '.' to the beginning of extension, like '.mp4'
        /// </remarks>
        public string[] PreferredExtension { get; }
        /// <summary>
        /// Current index of the video source.
        /// </summary>
        public uint Index { get; set; }
        /// <summary>
        /// How many frames are there in total. Return -1 if unknown, or <see cref="long.MinValue"/> if infinite.
        /// </summary>
        public long TotalFrames { get; }
        /// <summary>
        /// The frame rate of the video source. Return 0 if unknown.
        /// </summary>
        public double Fps { get; }
        /// <summary>
        /// Get the width of the video frames.
        /// Return 0 if unknown.
        /// </summary>
        public int Width { get; }
        /// <summary>
        /// Get the height of the video frames.
        /// Return 0 if unknown.
        /// </summary>
        public int Height { get; }
        /// <summary>
        /// Get whether the video source has been disposed.
        /// </summary>
        public bool Disposed { get; }

        /// <summary>
        /// Enable or disable read-lock to avoid potential crashes.
        /// </summary>
        public bool EnableLock { get; set; }

        /// <summary>
        /// Controls whether throw a exception while a solvable error occurs (like little bit of exceed range of frame(s)).
        /// </summary>
        /// <remarks>
        /// Note this is not available for all type of IVideoSource.
        /// </remarks>
        public bool StrictMode { get; set; }

        /// <summary>
        /// Enable or disable in-memory frame caching. When enabled, decoded frames are kept in RAM for faster re-access within the session.
        /// </summary>
        public static bool EnableMemoryCache { get; set; }

        /// <summary>
        /// Enable or disable disk-based frame caching. When enabled, decoded frames are written to disk for faster re-access across sessions.
        /// </summary>
        public static bool EnableDiskCache { get; set; }

    }

    public interface IVideoSource<T> : IVideoSource 
    {
        public new IPicture<T> GetFrame(uint targetFrame, bool hasAlpha = false);
        IPicture IVideoSource.GetFrame(uint targetFrame, bool hasAlpha) => GetFrame(targetFrame, hasAlpha);
    }

    public interface IVideoWriter : IDisposable
    {
        public int Width { get; set; }
        public int Height { get; set; }
        public string OutputPath { get; set; }
        public int FramePerSecond { get; set; }
        public string CodecName { get; set; }
        public string PixelFormat { get; set; }
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
        /// <summary>
        /// Metadata key-value pairs to write into the output container.
        /// Set before calling <see cref="Initialize"/>; changes after initialization may be ignored.
        /// </summary>
        Dictionary<string, string>? Metadata { get; set; }

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
