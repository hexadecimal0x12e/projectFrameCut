using projectFrameCut.Shared;
using System;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.Text;

namespace projectFrameCut.Render.RenderAPIBase.Clip
{
    public interface ITrack
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
        /// The actual sound's ratio
        /// </summary>
        /// <remarks>
        /// The final time used to do any calculation is by (FrameTime * SpeedRatio)
        /// </remarks>
        public float Ratio { get; init; }
        /// <summary>
        /// Represents the volume of this track.
        /// </summary>
        /// <remarks>
        /// 65535 as maximum volume (257x louder, that's very enough), 255 as the origin source's volume, and 0 as mute.
        /// </remarks>
        public ushort Volume { get; init; }
    }

    public class TrackEquabilityComparer : IEqualityComparer<ITrack>
    {
        public bool Equals(ITrack? x, ITrack? y) => x?.Id == y?.Id;

        public int GetHashCode([DisallowNull] ITrack obj)
        {
            return obj.Id.GetHashCode();
        }
    }
}
