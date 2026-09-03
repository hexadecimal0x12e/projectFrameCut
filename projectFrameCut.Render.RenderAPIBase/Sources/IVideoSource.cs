using projectFrameCut.Shared;
using projectFrameCut.Render.RenderAPIBase.Plugins;
using System;
using System.Collections.Generic;
using System.Text;
using projectFrameCut.Drawing.Base;
using projectFrameCut.Drawing.Effect;
using projectFrameCut.Drawing.Processing.Resizing;

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
        /// <returns>the frame</returns>
        abstract IPicture GetFrame(uint targetFrame);

        /// <summary>
        /// Reads a frame, crops a rectangle in source coordinates, and scales it to the requested output size.
        /// Decoder implementations may override this method to perform the conversion before the frame leaves
        /// the decoder. The default implementation preserves compatibility with third-party decoders.
        /// </summary>
        public virtual IPicture GetFrame(uint targetFrame, int sourceX, int sourceY, int sourceWidth, int sourceHeight,
            int targetWidth, int targetHeight)
        {
            ValidateFrameRegion(sourceX, sourceY, sourceWidth, sourceHeight, targetWidth, targetHeight);

            IPicture result = GetFrame(targetFrame);
            try
            {
                if (sourceX != 0 || sourceY != 0 || sourceWidth != result.Width || sourceHeight != result.Height)
                {
                    var cropped = CropEffect.Process(result, sourceX, sourceY, sourceWidth, sourceHeight);
                    result.Dispose();
                    result = cropped;
                }

                if (result.Width != targetWidth || result.Height != targetHeight)
                {
                    var resized = result.Resize(targetWidth, targetHeight, preserveAspect: false);
                    result.Dispose();
                    result = resized;
                }

                return result;
            }
            catch
            {
                result.Dispose();
                throw;
            }
        }

        /// <summary>Reads and scales the complete source frame.</summary>
        public virtual IPicture GetFrame(uint targetFrame, int targetWidth, int targetHeight)
            => GetFrame(targetFrame, 0, 0, Width, Height, targetWidth, targetHeight);

        private void ValidateFrameRegion(int sourceX, int sourceY, int sourceWidth, int sourceHeight,
            int targetWidth, int targetHeight)
        {
            if (sourceX < 0 || sourceY < 0)
                throw new ArgumentOutOfRangeException(nameof(sourceX), "The crop origin must be non-negative.");
            if (sourceWidth <= 0 || sourceHeight <= 0)
                throw new ArgumentOutOfRangeException(nameof(sourceWidth), "The crop size must be positive.");
            if (targetWidth <= 0 || targetHeight <= 0)
                throw new ArgumentOutOfRangeException(nameof(targetWidth), "The output size must be positive.");
            if (sourceX > Width - sourceWidth || sourceY > Height - sourceHeight)
                throw new ArgumentOutOfRangeException(nameof(sourceWidth), "The crop rectangle must be inside the decoded frame.");
        }
        /// <summary>
        /// The <see cref="GetFrame(uint)"/> return's <seealso cref="IPicture.BitPerPixel"/> of the result frames.
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
        /// Enable or disable disk-based frame caching. When enabled, decoded frames are written to disk for faster re-access across sessions.
        /// </summary>
        public static bool EnableDiskCache { get; set; }

    }

    public interface IVideoSource<T> : IVideoSource 
    {
        public new IPicture<T> GetFrame(uint targetFrame);
        IPicture IVideoSource.GetFrame(uint targetFrame) => GetFrame(targetFrame);

        public new virtual IPicture<T> GetFrame(uint targetFrame, int sourceX, int sourceY, int sourceWidth, int sourceHeight,
            int targetWidth, int targetHeight)
        {
            if (sourceX < 0 || sourceY < 0 || sourceWidth <= 0 || sourceHeight <= 0 ||
                sourceX > Width - sourceWidth || sourceY > Height - sourceHeight)
                throw new ArgumentOutOfRangeException(nameof(sourceWidth), "The crop rectangle must be inside the decoded frame.");
            if (targetWidth <= 0 || targetHeight <= 0)
                throw new ArgumentOutOfRangeException(nameof(targetWidth), "The output size must be positive.");

            IPicture<T> result = GetFrame(targetFrame);
            try
            {
                if (sourceX != 0 || sourceY != 0 || sourceWidth != result.Width || sourceHeight != result.Height)
                {
                    var cropped = (IPicture<T>)CropEffect.Process(
                        (IPicture)result, sourceX, sourceY, sourceWidth, sourceHeight);
                    result.Dispose();
                    result = cropped;
                }

                if (result.Width != targetWidth || result.Height != targetHeight)
                {
                    var resized = (IPicture<T>)((IPicture)result).Resize(
                        targetWidth, targetHeight, preserveAspect: false);
                    result.Dispose();
                    result = resized;
                }

                return result;
            }
            catch
            {
                result.Dispose();
                throw;
            }
        }

        IPicture IVideoSource.GetFrame(uint targetFrame, int sourceX, int sourceY, int sourceWidth, int sourceHeight,
            int targetWidth, int targetHeight)
            => GetFrame(targetFrame, sourceX, sourceY, sourceWidth, sourceHeight,
                targetWidth, targetHeight);

        public new virtual IPicture<T> GetFrame(uint targetFrame, int targetWidth, int targetHeight)
            => GetFrame(targetFrame, 0, 0, Width, Height, targetWidth, targetHeight);

        IPicture IVideoSource.GetFrame(uint targetFrame, int targetWidth, int targetHeight)
            => GetFrame(targetFrame, targetWidth, targetHeight);
    }
}
