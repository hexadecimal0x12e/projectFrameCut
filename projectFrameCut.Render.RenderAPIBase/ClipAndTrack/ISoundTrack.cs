using projectFrameCut.Shared;
using projectFrameCut.Render.RenderAPIBase.Sources;
using System;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.Runtime.CompilerServices;
using System.Text;

namespace projectFrameCut.Render.RenderAPIBase.ClipAndTrack
{
    public interface ISoundTrack
    {
        /// <summary>
        /// Gets the ID of the plugin that provided this value.
        /// </summary>
        public string FromPlugin { get; }
        /// <summary>
        /// Mode of this track. Mostly for compatibility purpose.
        /// </summary>
        public TrackMode TrackType { get; }
        /// <summary>
        /// The type name of this track. You must override it when you're creating a new track type in plugin.
        /// </summary>
        public virtual string TypeName => TrackType != TrackMode.ExtendTrack ? TrackType.ToString() : throw new InvalidOperationException("TrackType is ExtendTrack, and you must override it when you're creating a new track type in plugin.");

        /// <summary>
        /// The unique identifier of this track.
        /// </summary>
        public string Id { get; init; }
        /// <summary>
        /// The name of this track. Mostly used for display purpose.
        /// </summary>
        public string Name { get; init; }

        /// <summary>
        /// Indicate which layer this clip is in. Higher index means upper layer.
        /// </summary>
        public uint LayerIndex { get; init; }

        /// <summary>
        /// Where this clip starts in the whole draft, in frames.
        /// </summary>
        public uint StartFrame { get; init; }
        /// <summary>
        /// The start frame within the source clip, in frames.
        /// </summary>
        public uint RelativeStartFrame { get; init; } // in-point within the source
        /// <summary>
        /// Total duration of the source clip in frames. 0 will be treated as infinite length.
        /// </summary>
        public uint Duration { get; init; }

        /// <summary>
        /// The audio source for this track.
        /// </summary>
        public IAudioSource? AudioSource { get; set; }

        /// <summary>
        /// The actual sound's speed ratio
        /// </summary>
        /// <remarks>
        /// The final time used to do any calculation is by (FrameTime * SpeedRatio)
        /// </remarks>
        public float Ratio { get; init; }
        /// <summary>
        /// Represents the volume of this track.
        /// </summary>
        /// <remarks>
        /// 1 as origin volume, and 0 as mute.
        /// </remarks>
        public float Volume { get; init; }
    }

    public class AudioTrackEquabilityComparer : IEqualityComparer<ISoundTrack>
    {
        public bool Equals(ISoundTrack? x, ISoundTrack? y) => x?.Id == y?.Id;

        public int GetHashCode([DisallowNull] ISoundTrack obj)
        {
            return obj.Id.GetHashCode();
        }

        public static bool IsTrackBelongsToClip(ISoundTrack track, IClip clip) => clip.BindedSoundTrack == track.Id;
    }
}
