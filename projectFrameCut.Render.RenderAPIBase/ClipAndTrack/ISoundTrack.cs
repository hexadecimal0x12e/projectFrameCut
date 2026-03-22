using projectFrameCut.Render.RenderAPIBase.EffectAndMixture;
using projectFrameCut.Render.RenderAPIBase.Sources;
using projectFrameCut.Shared;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json.Serialization;

namespace projectFrameCut.Render.RenderAPIBase.ClipAndTrack
{
    public interface ISoundTrack : IDisposable
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
        /// Indicate which layer this track is in. Higher index means upper layer.
        /// </summary>
        public uint LayerIndex { get; init; }

        /// <summary>
        /// Where this track starts in the whole draft, in frames.
        /// </summary>
        public uint StartFrame { get; init; }
        /// <summary>
        /// The start frame within the source track, in frames.
        /// </summary>
        public uint RelativeStartFrame { get; init; } // in-point within the source
        /// <summary>
        /// Total duration of the source track in frames. 0 will be treated as infinite length.
        /// </summary>
        public uint Duration { get; init; }

        /// <summary>
        /// Get the path of the source file for this track. May be null for some kind of tracks.
        /// </summary>
        public string? FilePath { get; set; }

        /// <summary>
        /// Indicates whether this track need a source file path to work. If this property is false, the system will not check the file path and directly call the GetFrame function. Otherwise, the system will check the file path before calling GetFrame, and if the file path is null or invalid, it will throw an exception instead of calling GetFrame.
        /// </summary>
        public bool NeedFilePath { get; }

        /// <summary>
        /// The actual sound's speed ratio
        /// </summary>
        /// <remarks>
        /// The final time used to do any calculation is by (FrameTime * SpeedRatio)
        /// </remarks>
        public float Ratio { get; set; }
        /// <summary>
        /// Represents the volume of this track.
        /// </summary>
        /// <remarks>
        /// 1 as origin volume, and 0 as mute.
        /// </remarks>
        public float Volume { get; set; }

        /// <summary>
        /// The source's sample rate. Used for index mapping and sample reading.
        /// </summary>
        public int SamplePerSecond { get; }

        /// <summary>
        /// The effects applied to this track's Data.
        /// Used in serialization and deserialization.
        /// </summary>
        public EffectAndMixtureJSONStructure[]? Effects { get; init; }

        [JsonIgnore]
        /// <summary>
        /// The actual effects applied to this track.
        /// </summary>
        public IEffect[]? EffectsInstances { get; set; }

        /// <summary>
        /// The ExtraData/Metadata from the <see cref="projectFrameCut.Render.RenderAPIBase.Project.SoundtrackDTO.MetaData"/>
        /// </summary>
        public Dictionary<string, object> ExtraData { get; set; }

        /// <summary>
        /// Read the samples from the source related to the start point of the source.
        /// This is the ONLY method you need to implement.
        /// </summary>
        /// <remarks>
        /// <b>DON'T DO any index mapping.</b>
        /// If you don't do these, it may cause an unexcepted result.
        /// </remarks>
        /// <param name="startIndex">the start index within the source track</param>
        /// <param name="length">the number of samples to read</param>
        /// <returns>The collection of audio samples.</returns>
        public IAudioSamples GetAudioSamplesRelatedToStartPointOfSource(uint startIndex, int length);

        /// <summary>
        /// Get the frame index relative to the source track for the specified target frame in the draft.
        /// </summary>
        /// <param name="sampleIndex">the frame index in the whole track you'd like to get</param>
        /// <returns>the index of frame relative to the source, or null if the frame you want is not available (probably because of little overlap caused by rounding) </returns>
        /// <exception cref="IndexOutOfRangeException">Frame is not exist in this track.</exception>
        [DebuggerNonUserCode()]
        public uint? GetRelativeSampleIndex(uint sampleIndex)
        {
            uint duration = Duration;
            uint startFrame = StartFrame;
            uint relativeStartFrame = RelativeStartFrame;
            if (Ratio != 1)
            {
                duration = (uint)Math.Round(Duration * Ratio);
                startFrame = (uint)Math.Round(StartFrame * Ratio);
                relativeStartFrame = (uint)Math.Round(RelativeStartFrame * Ratio);
            }

            long offsetFromtrackStart = (long)sampleIndex - startFrame;

            if (offsetFromtrackStart == duration)
            {
                return null;
            }

            if (offsetFromtrackStart < 0 || offsetFromtrackStart >= duration)
            {
                throw new IndexOutOfRangeException($"SampleIndex #{sampleIndex} is not in track [{startFrame}, {startFrame + duration}).");
            }

            ulong sourceIndexLong = (ulong)relativeStartFrame + (ulong)offsetFromtrackStart;
            if (sourceIndexLong > uint.MaxValue)
            {
                throw new IndexOutOfRangeException($"Frame mapping overflow for frame #{sampleIndex}.");
            }

            return (uint)Math.Round(sourceIndexLong / Ratio);
        }

        /// <summary>
        /// Get the samples starting from the point related to whole draft, with the specified duration and sample rate.
        /// </summary>
        /// <remarks>
        /// This is the method that will be called by the renderer, and it will call <see cref="GetAudioSamplesRelatedToStartPointOfSource(uint, int, int)"/> internally after doing the index mapping.
        /// </remarks>
        /// <param name="index"></param>
        /// <param name="duration"></param>
        /// <param name="samplePerSecond"></param>
        /// <returns></returns>
        public IAudioSamples GetSample(uint index, int duration)
            => GetAudioSamplesRelatedToStartPointOfSource(GetRelativeSampleIndex(index) ?? Duration, duration);

                /// <summary>
        /// Re-initialize the track. Call this function when the source file is changed and you want to reload it.
        /// </summary>
        public void ReInit();

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
