using projectFrameCut.Drawing.Base;
using System;
using System.Collections.Generic;
using System.Text;

namespace projectFrameCut.Render.RenderAPIBase.Sources
{
    /// <summary>
    /// A mini decoder designed for generating frames from a virtual source. 
    /// This is useful for generating frames from a procedural source, such as a shader or a mathematical function.
    /// </summary>
    public interface IVirtualVideoSource : IDisposable
    {
        /// <summary>
        /// Get the type name of this virtual decoder. Should be the same as the class name.
        /// </summary>
        public string TypeName { get; }

        /// <summary>
        /// Initialize the virtual video source. This method should prepare the virtual video source for frame generation.
        /// </summary>
        /// <param name="width">Width of the video</param>
        /// <param name="height">Height of the video</param>
        /// <param name="fps">Frames per second</param>
        /// <param name="targetDuration">The duration of the virtual video source should be in frames</param>
        /// <param name="targetPPB">Target picture pixel mode</param>
        public void Init(int width, int height, int fps, uint targetDuration, IPicture.PicturePixelMode targetPPB);

        /// <summary>
        /// Generate a frame from the virtual video source at the given index.
        /// </summary>
        /// <param name="index">the index of the frame to generate</param>
        /// <param name="hasAlpha">Indicates if the generated frame should have an alpha channel</param>
        /// <returns></returns>
        public IPicture Generate(uint index, bool hasAlpha);

    }
}
