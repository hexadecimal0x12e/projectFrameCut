using System;
using System.Collections.Generic;
using System.Text;

namespace projectFrameCut.Render.RenderAPIBase.Sources 
{
    public interface IAudioSource : IDisposable
    {
        /// <summary>
        /// Initialize the audio source. This method should prepare the audio source for frame extraction.
        /// </summary>
        /// <remarks>
        /// If the file path is null, please just return without doing anything. 
        /// This is because <see cref="IPluginBase.VideoSourceCreator"/> need an instance of this to get <see cref="PreferredExtension"/> to determine which plugin to use.
        /// </remarks>
        public abstract void Initialize();
        /// <summary>
        /// Try to initialize the audio source. Returns true if successful, false otherwise.
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
        public IAudioSource CreateNew(string newSource);

        /// <summary>
        /// The preferred file extensions for this audio source.
        /// </summary>
        public string[] PreferredExtension { get; }

        /// <summary>
        /// Get the total duration of this audio source.
        /// </summary>
        public uint Duration { get;  }
        /// <summary>
        /// Get whether the audio source has been disposed.
        /// </summary>
        public bool Disposed { get; }

        /// <summary>
        /// Get audio samples for a specific time range.
        /// </summary>
        /// <param name="startFrame">The start frame index (based on video framerate).</param>
        /// <param name="frameCount">The number of frames to read.</param>
        /// <param name="videoFramerate">The framerate of the video to synchronize with.</param>
        /// <returns>An <see cref="AudioBuffer"/> containing the samples.</returns>
        public AudioBuffer GetAudioSamples(uint startFrame, uint frameCount, int videoFramerate);
    }
}
