using projectFrameCut.Render.RenderAPIBase.EffectAndMixture;
using projectFrameCut.Shared;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.Reflection;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace projectFrameCut.Render.RenderAPIBase
{
    public interface IClip : IDisposable
    {
        /// <summary>
        /// Gets the ID of the plugin that provided this value.
        /// </summary>
        public string FromPlugin { get; }
        /// <summary>
        /// Mode of this clip. Mostly for compatibility purpose.
        /// </summary>
        public ClipMode ClipType { get; }
        /// <summary>
        /// The type name of this clip. You must override it when you're creating a new clip type in plugin.
        /// </summary>
        public virtual string TypeName => ClipType != ClipMode.ExtendClip ? ClipType.ToString() : throw new InvalidOperationException("ClipType is ExtendClip, and you must override it when you're creating a new clip type in plugin.");

        /// <summary>
        /// The unique identifier of this clip.
        /// </summary>
        public string Id { get; init; }
        /// <summary>
        /// The name of this clip. Mostly used for display purpose.
        /// </summary>
        public string Name { get; init; }

        /// <summary>
        /// Represent which sound track's id is binded to this clip.
        /// </summary>
        public string BindedSoundTrack { get; init;  }
        
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
        /// The source's frame time (1 / frame rate) of this clip, in seconds.
        /// </summary>
        public float FrameTime { get; init; }
        /// <summary>
        /// The actual frame time's ratio
        /// </summary>
        /// <remarks>
        /// The final frame time used to do any calculation is by (FrameTime * SpeedRatio)
        /// </remarks>
        public float SecondPerFrameRatio { get; init; }
        /// <summary>
        /// Get the mixture mode applied to this clip.
        /// </summary>
        public MixtureMode MixtureMode { get; init; }
        /// <summary>
        /// The args for the mixture mode.
        /// </summary>
        public Dictionary<string, object>? MixtureArgs { get; init; }
        /// <summary>
        /// The effects applied to this clip.
        /// </summary>
        public EffectAndMixtureJSONStructure[]? Effects { get; init; }

        [JsonIgnore]
        public IEffect[]? EffectsInstances { get; init; }

        /// <summary>
        /// Get the path of the source file for this clip. May be null for some kind of clips.
        /// </summary>
        public string? FilePath { get; init; }

        /// <summary>
        /// Get the frame index relative to the source clip for the specified target frame in the draft.
        /// </summary>
        /// <param name="targetFrame">the frame index in the whole clip you'd like to get</param>
        /// <returns>the index of frame relative to the source, or null if the frame you want is not available (probably because of little overlap caused by rounding) </returns>
        /// <exception cref="IndexOutOfRangeException">Frame is not exist in this clip.</exception>
        [DebuggerNonUserCode()]
        public uint? GetRelativeFrameIndex(uint targetFrame)
        {
            uint duration = Duration;
            uint startFrame = StartFrame;
            uint relativeStartFrame = RelativeStartFrame;
            if (SecondPerFrameRatio != 1)
            {
                duration = (uint)Math.Round(Duration * SecondPerFrameRatio);
                startFrame = (uint)Math.Round(StartFrame * SecondPerFrameRatio);
                relativeStartFrame = (uint)Math.Round(RelativeStartFrame * SecondPerFrameRatio);
            }

            long offsetFromClipStart = (long)targetFrame - startFrame;

            if (offsetFromClipStart == duration)
            {
                return null;
            }

            if (offsetFromClipStart < 0 || offsetFromClipStart >= duration)
            {
                throw new IndexOutOfRangeException($"Frame #{targetFrame} is not in clip [{startFrame}, {startFrame + duration}).");
            }

            ulong sourceIndexLong = (ulong)relativeStartFrame + (ulong)offsetFromClipStart;
            if (sourceIndexLong > uint.MaxValue)
            {
                throw new IndexOutOfRangeException($"Frame mapping overflow for frame #{targetFrame}.");
            }

            return (uint)Math.Round(sourceIndexLong / SecondPerFrameRatio);
        }

        /// <summary>
        /// Get the frame at the specified index relative to the start of the draft, and resize it to the specified width and height. Strongly recommended to use this.
        /// </summary>
        /// <param name="targetFrame">the frame in the whole clip you'd like to get</param>
        /// <returns>the target clip, or null if the frame you want is 1 frame longer than the range (probably because of little overlap caused by rounding)</returns>
        /// <exception cref="IndexOutOfRangeException">Frame is not exist in this clip.</exception>
        public IPicture? GetFrame(uint targetFrame)
        {
            var relativeIndex = GetRelativeFrameIndex(targetFrame);
            if (relativeIndex is null)
            {
                return null;
            }
            return GetFrameRelativeToStartPointOfSource(relativeIndex.Value);
        }

        /// <summary>
        /// Get the frame at the specified index relative to the start of the draft, and resize it to the specified width and height. Strongly recommended to use this.
        /// </summary>
        /// <param name="targetFrame">the frame in the whole clip you'd like to get</param>
        /// <param name="targetWidth">target width, clip will be resized automatically.</param>
        /// <param name="targetHeight">target height, clip will be resized automatically.</param>
        /// <param name="forceResize">force to resize result frame</param>
        /// <returns>the target clip,or be the last frame if the frame you want is 1 frame longer than the range (probably because of little overlap caused by rounding)</returns>
        /// <remarks>you may override this method if you source can generate the frame directly with a specific size.</remarks>
        /// <exception cref="IndexOutOfRangeException">Frame is not exist in this clip.</exception>
        public virtual IPicture GetFrame(uint targetFrame, int targetWidth, int targetHeight, bool forceResize = false)
            => GetFrameRelativeToStartPointOfSource(GetRelativeFrameIndex(targetFrame) ?? Duration, targetWidth, targetHeight, forceResize);

        /// <summary>
        /// Get the frame at the specified index relative to the start of the clip.
        /// </summary>
        /// <remarks>
        /// DO NOT DO ANY RANGE CHECK OR FRAME INDEX MAPPING IN THIS FUNCTION!!! <see cref="IClip"/> will help you do this, and do this in your code will cause unexpected result.
        /// </remarks>
        /// <param name="frameIndex">frame index related to the source clip</param>
        /// <returns>the result frame</returns>
        public IPicture GetFrameRelativeToStartPointOfSource(uint frameIndex);

        /// <summary>
        /// Get the frame at the specified index relative to the start of the clip with the specific size.
        /// </summary>
        /// <remarks>
        /// the default implementation will call <see cref="GetFrameRelativeToStartPointOfSource(uint)"/> and resize the result.
        /// </remarks>
        /// <param name="frameIndex">frame index related to the source clip</param>
        /// <returns>the result frame</returns>
        public virtual IPicture GetFrameRelativeToStartPointOfSource(uint frameIndex, int targetWidth, int targetHeight, bool forceResize)
            => GetFrameRelativeToStartPointOfSource(frameIndex).Resize(targetWidth, targetHeight, forceResize);

        /// <summary>
        /// Get the length of this clip in frames. use null for infinite length.
        /// </summary>
        /// <returns>any positive integer represented the total length of the clip, or null for infinite length</returns>
        [Obsolete("Use Duration property instead.", false)]
        public uint? GetClipLength();

        /// <summary>
        /// Re-initialize the clip. Call this function when the source file is changed and you want to reload it.
        /// </summary>
        public void ReInit();


    }

    public class ClipEquabilityComparer : IEqualityComparer<IClip>
    {
        public bool Equals(IClip? x, IClip? y) => x?.Id == y?.Id;

        public int GetHashCode([DisallowNull] IClip obj)
        {
            return obj.Id.GetHashCode();
        }
    }
}
