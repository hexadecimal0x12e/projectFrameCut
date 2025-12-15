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
        /// Read the actual frame from the video source.
        /// </summary>
        /// <param name="targetFrame">the frame index to read</param>
        /// <param name="hasAlpha">keep the alpha channel if true</param>
        /// <returns>the frame</returns>
        abstract IPicture GetFrame(uint targetFrame, bool hasAlpha = false);
        /// <summary>
        /// The <see cref="GetFrame(uint, bool)"/> return's <seealso cref="IPicture.bitPerPixel"/> of the result frames.
        /// Return null if unknown or variable.
        /// </summary>
        public int? ResultBitPerPixel { get; }
        /// <summary>
        /// The preferred file extensions for this video source.
        /// </summary>
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
        /// </summary>
        public int Width { get; }
        /// <summary>
        /// Get the height of the video frames.
        /// </summary>
        public int Height { get; }
        /// <summary>
        /// Get whether the video source has been disposed.
        /// </summary>
        public bool Disposed { get; }

    }
}
